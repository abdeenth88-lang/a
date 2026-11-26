// AB Bridges - "Export Base Point": pick a point near the bridge, type its Z, keep it in the drawing and write it
// into the bridge Excel (sheet "Super Structure", own block at the top right). In Revit, "Import Base Point" puts the
// survey point and the project base point there, and "Create Deck" uses it as the (0,0,0) of the deck family.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace ABBridges
{
    internal struct BasePt
    {
        public double E, N, Z;
        public BasePt(double e, double n, double z) { E = e; N = n; Z = z; }
    }

    // the base point kept in the drawing (own record, so it never clashes with the NGL record)
    internal static class BasePointStore
    {
        private const string Key = "AB_BRIDGES_BASEPOINT";

        public static BasePt? Load(Database db)
        {
            try
            {
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                    BasePt? res = null;
                    if (nod.Contains(Key))
                    {
                        var xr = (Xrecord)tr.GetObject(nod.GetAt(Key), OpenMode.ForRead);
                        TypedValue[] v = xr.Data?.AsArray();
                        if (v != null && v.Length >= 3)
                            res = new BasePt(Convert.ToDouble(v[0].Value), Convert.ToDouble(v[1].Value), Convert.ToDouble(v[2].Value));
                    }
                    tr.Commit();
                    return res;
                }
            }
            catch { return null; }
        }

        // call with the document locked
        public static void Save(Database db, BasePt p)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                var rb = new ResultBuffer(new TypedValue((int)DxfCode.Real, p.E), new TypedValue((int)DxfCode.Real, p.N), new TypedValue((int)DxfCode.Real, p.Z));
                if (nod.Contains(Key))
                {
                    var xr = (Xrecord)tr.GetObject(nod.GetAt(Key), OpenMode.ForWrite);
                    xr.Data = rb;
                }
                else
                {
                    nod.UpgradeOpen();
                    var xr = new Xrecord { Data = rb };
                    nod.SetAt(Key, xr);
                    tr.AddNewlyCreatedDBObject(xr, true);
                }
                tr.Commit();
            }
        }
    }

    internal static class BasePointCmd
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        public static void Run(Document doc)
        {
            Editor ed = doc.Editor;
            Database db = doc.Database;
            BasePt? old = BasePointStore.Load(db);
            if (old.HasValue)
                ed.WriteMessage($"\nCurrent base point: E {old.Value.E:0.000}  N {old.Value.N:0.000}  Z {old.Value.Z:0.000}");

            PromptPointResult pr = ed.GetPoint("\nPick the base point near the bridge: ");
            if (pr.Status != PromptStatus.OK) return;
            Point3d wcs = pr.Value.TransformBy(ed.CurrentUserCoordinateSystem);

            var zo = new PromptDoubleOptions("\nBase point Z (elevation, m): ")
            {
                AllowNone = false,
                DefaultValue = old.HasValue ? old.Value.Z : 0.0,
                UseDefaultValue = true
            };
            PromptDoubleResult zr = ed.GetDouble(zo);
            if (zr.Status != PromptStatus.OK) return;

            var p = new BasePt(Math.Round(wcs.X, 4), Math.Round(wcs.Y, 4), zr.Value);
            using (doc.LockDocument()) BasePointStore.Save(db, p);
            ed.WriteMessage($"\nBase point kept in this drawing: E {p.E:0.000}  N {p.N:0.000}  Z {p.Z:0.000}" +
                            "\n(Extract Data writes it into the Super Structure sheet of every new Excel.)");

            var ko = new PromptKeywordOptions("\nWrite it into an existing bridge Excel now? ") { AllowNone = true };
            ko.Keywords.Add("Yes");
            ko.Keywords.Add("No");
            ko.Keywords.Default = "Yes";
            PromptResult kr = ed.GetKeywords(ko);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            if (kr.Status == PromptStatus.OK && kr.StringResult == "No") return;

            using (var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Bridge Excel to write the base point into",
                Filter = "Excel workbook (*.xlsx)|*.xlsx"
            })
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                try
                {
                    BasePointXlsx.Write(dlg.FileName, p);
                    ed.WriteMessage($"\nBase point written into {Path.GetFileName(dlg.FileName)} (sheet Super Structure).\n");
                }
                catch (IOException)
                {
                    ed.WriteMessage("\nThe Excel file is open in another program - close it and run Export Base Point again.\n");
                }
                catch (Exception ex)
                {
                    ed.WriteMessage("\nThe base point could not be written into the Excel: " + ex.Message + "\n");
                }
            }
        }
    }

    // writes the base point block into an existing workbook (sheet "Super Structure"), next to the main table:
    //   Q2 "BASE POINT  (Revit 0,0,0)"   Q3 "E" R3 value   Q4 "N" R4 value   Q5 "Z" R5 value
    internal static class BasePointXlsx
    {
        public const int Col = 17;              // Q
        public const int Row = 2;
        public const string Title = "BASE POINT  (Revit 0,0,0)";

        private static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static void Write(string path, BasePt p)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Update))
            {
                string sheetPath = SheetPath(zip, "Super Structure") ?? throw new InvalidOperationException("sheet \"Super Structure\" not found");
                ZipArchiveEntry e = zip.GetEntry(sheetPath);
                XDocument doc;
                using (Stream s = e.Open()) doc = XDocument.Load(s);

                XElement data = doc.Root.Element(X + "sheetData");
                SetText(data, Row, Col, Title);
                SetText(data, Row + 1, Col, "E");
                SetNumber(data, Row + 1, Col + 1, p.E);
                SetText(data, Row + 2, Col, "N");
                SetNumber(data, Row + 2, Col + 1, p.N);
                SetText(data, Row + 3, Col, "Z");
                SetNumber(data, Row + 3, Col + 1, p.Z);

                e.Delete();
                ZipArchiveEntry ne = zip.CreateEntry(sheetPath, CompressionLevel.Optimal);
                using (Stream s = ne.Open()) doc.Save(s, SaveOptions.DisableFormatting);
            }
        }

        private static string SheetPath(ZipArchive zip, string name)
        {
            XDocument wb, rels;
            using (Stream s = zip.GetEntry("xl/workbook.xml").Open()) wb = XDocument.Load(s);
            using (Stream s = zip.GetEntry("xl/_rels/workbook.xml.rels").Open()) rels = XDocument.Load(s);
            XElement sh = wb.Root.Element(X + "sheets")?.Elements(X + "sheet")
                .FirstOrDefault(x => string.Equals((string)x.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
            if (sh == null) return null;
            string rid = (string)sh.Attribute(Rel + "id");
            string target = rels.Root.Elements(PRel + "Relationship").FirstOrDefault(r => (string)r.Attribute("Id") == rid)?.Attribute("Target")?.Value;
            if (target == null) return null;
            target = target.TrimStart('/');
            return target.StartsWith("xl/") ? target : "xl/" + target;
        }

        private static XElement Cell(XElement data, int r, int c)
        {
            string rs = r.ToString(CultureInfo.InvariantCulture);
            XElement row = data.Elements(X + "row").FirstOrDefault(x => (string)x.Attribute("r") == rs);
            if (row == null)
            {
                row = new XElement(X + "row", new XAttribute("r", rs));
                XElement after = data.Elements(X + "row").LastOrDefault(x => (int?)x.Attribute("r") < r);
                if (after == null) data.AddFirst(row); else after.AddAfterSelf(row);
            }
            row.Attribute("spans")?.Remove();
            string refc = XSheet.Ref(r, c);
            XElement cell = row.Elements(X + "c").FirstOrDefault(x => (string)x.Attribute("r") == refc);
            if (cell != null) { cell.RemoveNodes(); cell.Attribute("t")?.Remove(); return cell; }
            cell = new XElement(X + "c", new XAttribute("r", refc));
            XElement prev = row.Elements(X + "c").LastOrDefault(x => ColOf((string)x.Attribute("r")) < c);
            if (prev == null) row.AddFirst(cell); else prev.AddAfterSelf(cell);
            return cell;
        }

        private static int ColOf(string refc)
        {
            string letters = Regex.Match(refc ?? "", "^[A-Z]+").Value;
            int n = 0;
            foreach (char ch in letters) n = n * 26 + (ch - 'A' + 1);
            return n;
        }

        private static void SetText(XElement data, int r, int c, string text)
        {
            XElement cell = Cell(data, r, c);
            cell.SetAttributeValue("t", "inlineStr");
            cell.Add(new XElement(X + "is", new XElement(X + "t", text)));
        }

        private static void SetNumber(XElement data, int r, int c, double v)
        {
            XElement cell = Cell(data, r, c);
            cell.Add(new XElement(X + "v", v.ToString("R", CultureInfo.InvariantCulture)));
        }
    }
}

namespace ABBridges
{
    // the Substructure sheet data: across every axis, the design surface at 4 points and the NGL every <= 1 m
    internal static class AxisSections
    {
        public static List<AxisSection> Build(List<Section> sections, SurfaceSet design, SurfaceSet ngl, List<string> warn)
        {
            var res = new List<AxisSection>();
            foreach (Section s in sections.Where(x => x.Kind == SectionKind.Axis))
            {
                if (!s.HasEdges) { warn.Add($"Substructure: axis {s.Remark} has no edges - left out of the Substructure sheet."); continue; }
                Vector3d dir = new Vector3d(s.Right.X - s.Left.X, s.Right.Y - s.Left.Y, 0);
                if (dir.Length < 1e-6) continue;
                dir = dir.GetNormal();
                Point3d c = s.ClPoint;
                double Off(double x, double y) => (x - c.X) * dir.X + (y - c.Y) * dir.Y;

                var a = new AxisSection
                {
                    Name = s.Remark,
                    Station = s.Station,
                    SkewDeg = s.SkewDeg,
                    X = c.X,
                    Y = c.Y,
                    DirX = dir.X,
                    DirY = dir.Y
                };

                // design: left edge, alignment, center (midpoint of the edges), right edge
                double mx = (s.Left.X + s.Right.X) / 2, my = (s.Left.Y + s.Right.Y) / 2;
                double mz = design?.Z(mx, my, double.IsNaN(s.CenterZ) ? (double?)null : s.CenterZ) ?? double.NaN;
                if (double.IsNaN(mz) && !double.IsNaN(s.LeftZ) && !double.IsNaN(s.RightZ)) mz = (s.LeftZ + s.RightZ) / 2;
                a.Design.Add(("Left edge", Off(s.Left.X, s.Left.Y), s.Left.X, s.Left.Y, s.LeftZ));
                a.Design.Add(("Alignment", 0.0, c.X, c.Y, s.CenterZ));
                a.Design.Add(("Center", Off(mx, my), mx, my, mz));
                a.Design.Add(("Right edge", Off(s.Right.X, s.Right.Y), s.Right.X, s.Right.Y, s.RightZ));

                // NGL from the left edge to the right edge, equal parts <= 1 m
                if (ngl != null && !ngl.IsEmpty)
                {
                    double w = s.Left.DistanceTo(s.Right);
                    int n = Math.Max(1, (int)Math.Ceiling(w / 1.0 - 1e-9));
                    int missing = 0;
                    double? near = null;
                    for (int k = 0; k <= n; k++)
                    {
                        double t = (double)k / n;
                        double x = s.Left.X + (s.Right.X - s.Left.X) * t, y = s.Left.Y + (s.Right.Y - s.Left.Y) * t;
                        double? z = ngl.Z(x, y, near);
                        if (z.HasValue) near = z; else missing++;
                        string nm = k == 0 ? "Left edge" : k == n ? "Right edge" : "#" + k;
                        a.Ngl.Add((nm, Off(x, y), x, y, z ?? double.NaN));
                    }
                    if (missing > 0) warn.Add($"Substructure: axis {s.Remark} - {missing} NGL point(s) outside the NGL surface (left blank).");
                }
                res.Add(a);
            }
            if (ngl == null || ngl.IsEmpty) warn.Add("Substructure: no NGL surface picked - the NGL part of the Substructure sheet is empty.");
            return res;
        }
    }
}
