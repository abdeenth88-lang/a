// AB Bridges - Tendon Profile: the Excel sheet "Tendon Profile" (stages x paths, X / Y-Tendon columns), 3D tendons in the webs,
// one drafting view per path and stage (vertical x3, like the PT drawings) and one sheet per stage (path 1 over path 2).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using UI = Autodesk.Revit.UI;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;
using RColor = Autodesk.Revit.DB.Color;
using WinColor = System.Drawing.Color;
using View = Autodesk.Revit.DB.View;

namespace ABBridgesRevit
{
    internal sealed class TendonProfile
    {
        public int Stage, Path;
        public List<(string x, double y)> Pts = new List<(string, double)>();
        public Dictionary<string, string> Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Title => $"Stage {Stage} · Path {Path}";
        public int Filled => Pts.Count(p => !double.IsNaN(p.y));
    }

    internal sealed class TendonSet
    {
        public int Stages = 2, Paths = 2;
        public List<TendonProfile> Profiles = new List<TendonProfile>();     // per stage and path (cut at the splits)
        public List<TendonProfile> Whole = new List<TendonProfile>();        // per path, as typed
        public Dictionary<int, string> Splits = new Dictionary<int, string>();
        public TendonProfile Get(int s, int p) => Profiles.FirstOrDefault(x => x.Stage == s && x.Path == p);
    }

    // ================================================================ Excel sheet
    internal static class TendonSheet
    {
        public const string Name = "Tendon Profile";
        public static readonly string[] DataRows =
        {
            "Total jacking force PJ (kN)", "No. of tendons per web", "No. of strands per tendon", "Area of strand (mm2)",
            "No. of webs", "Jacking end (Left / Right)", "Web offsets from CL (m)", "Box depth (m)"
        };
        private static readonly string[] DataDefaults = { "", "", "", "", "2", "Left", "-2.50; 2.50", "2.00" };
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";

        public const string SplitRow = "Split at (X) - stages";

        // layout: B2 paths; row 5 = "Path p" blocks, row 6 = X | Y-Tendon (m), the whole path from Anchor1 to Anchor2,
        // then "Split at (X)" (e.g. 0.85L2 or 0.85L2; 0.72L4) and the data rows. The stages are cut at the splits.
        public static TendonSet Read(string path, TendonGeo g)
        {
            Dictionary<(int r, int c), string> cells = BridgeExcel.ReadCells(path, Name);
            if (cells == null) return null;
            string T(int r, int c) => cells.TryGetValue((r, c), out string v) ? v.Trim() : "";
            double N(int r, int c) => double.TryParse(T(r, c), NumberStyles.Float, CI, out double v) ? v : double.NaN;
            var set = new TendonSet { Stages = 1 };
            if (int.TryParse(T(2, 2), out int pa) && pa > 0) set.Paths = pa;
            int maxC = cells.Keys.Select(k => k.c).DefaultIfEmpty(0).Max(), maxR = cells.Keys.Select(k => k.r).DefaultIfEmpty(0).Max();
            for (int c = 1; c <= maxC; c++)
            {
                Match m = Regex.Match(T(5, c), @"^Path\s*(\d+)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var whole = new TendonProfile { Stage = 0, Path = int.Parse(m.Groups[1].Value) };
                int r = 7;
                for (; r <= maxR; r++)
                {
                    string x = T(r, c);
                    if (x.Length == 0 || x == SplitRow || DataRows.Contains(x, StringComparer.OrdinalIgnoreCase)) break;
                    whole.Pts.Add((x, N(r, c + 1)));
                }
                string splits = "";
                for (; r <= maxR; r++)
                {
                    string k = T(r, c);
                    if (k == SplitRow) splits = T(r, c + 1);
                    else if (DataRows.Contains(k, StringComparer.OrdinalIgnoreCase)) whole.Data[k] = T(r, c + 1);
                }
                set.Whole.Add(whole);
                set.Splits[whole.Path] = splits;
                foreach (TendonProfile sp in Cut(whole, splits, g)) set.Profiles.Add(sp);
            }
            set.Stages = set.Profiles.Select(x => x.Stage).DefaultIfEmpty(1).Max();
            return set;
        }

        // one path cut at its splits into stages; the split point belongs to both stages (its Y must be typed by the user)
        private static List<TendonProfile> Cut(TendonProfile whole, string splits, TendonGeo g)
        {
            var cuts = splits.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())
                             .Select(x => (x, sta: g.StationOf(x))).Where(x => !double.IsNaN(x.sta)).OrderBy(x => x.sta).ToList();
            var pts = whole.Pts.Select(q => (q.x, q.y, sta: TendonGeo.IsAnchor(q.x) ? (q.x.EndsWith("2") ? double.MaxValue : double.MinValue) : g.StationOf(q.x)))
                               .Where(q => !double.IsNaN(q.sta)).OrderBy(q => q.sta).ToList();
            double YAt(double sta)
            {
                var inner = pts.Where(q => q.sta > double.MinValue && q.sta < double.MaxValue && !double.IsNaN(q.y)).ToList();
                for (int i = 0; i + 1 < inner.Count; i++)
                    if (sta >= inner[i].sta - 1e-9 && sta <= inner[i + 1].sta + 1e-9)
                        return inner[i].y + (inner[i + 1].y - inner[i].y) * (sta - inner[i].sta) / Math.Max(1e-9, inner[i + 1].sta - inner[i].sta);
                return double.NaN;
            }
            var res = new List<TendonProfile>();
            double lo = double.MinValue;
            for (int k = 0; k <= cuts.Count; k++)
            {
                double hi = k < cuts.Count ? cuts[k].sta : double.MaxValue;
                var sp = new TendonProfile { Stage = k + 1, Path = whole.Path, Data = whole.Data };
                if (k > 0) sp.Pts.Add((cuts[k - 1].x, pts.Where(q => Math.Abs(q.sta - lo) < 1e-6).Select(q => q.y).DefaultIfEmpty(YAt(lo)).First()));
                foreach (var q in pts) if (q.sta > lo + 1e-6 && q.sta < hi - 1e-6 || (k == 0 && q.sta == double.MinValue) || (k == cuts.Count && q.sta == double.MaxValue)) sp.Pts.Add((q.x, q.y));
                if (k < cuts.Count) sp.Pts.Add((cuts[k].x, pts.Where(q => Math.Abs(q.sta - hi) < 1e-6).Select(q => q.y).DefaultIfEmpty(YAt(hi)).First()));
                res.Add(sp);
                lo = hi;
            }
            return res;
        }

        // default X of a whole path: Anchor1, every 0.1 of every span, Anchor2
        private static List<string> DefaultX(int spans)
        {
            var res = new List<string> { "Anchor1" };
            for (int sp = 1; sp <= spans; sp++)
                for (int k = sp == 1 ? 0 : 1; k <= 10; k++) res.Add((k / 10.0).ToString("0.0", CI) + "L" + sp.ToString(CI));
            res.Add("Anchor2");
            return res;
        }

        // writes the sheet (new, or again with more / fewer paths) keeping every value already typed
        public static void Write(string path, int paths, int spans, TendonSet old, TendonGeo g = null)
        {
            var rows = new SortedDictionary<int, SortedDictionary<int, (string v, bool num, int style)>>();
            void Put(int r, int c, string v, int style, bool num = false)
            {
                if (!rows.TryGetValue(r, out var row)) rows[r] = row = new SortedDictionary<int, (string, bool, int)>();
                row[c] = (v, num, StylePlaceholder + style);
            }
            Put(1, 1, "AB Bridges - Tendon Profile: the whole path from Anchor1 (start) to Anchor2 (end); Y = top of box to tendon CG (m); stages cut at 'Split at'", 1);
            Put(2, 1, "Number of paths", 2); Put(2, 2, paths.ToString(CI), 3, true);
            int col = 1;
            for (int p = 1; p <= paths; p++)
            {
                TendonProfile was = old?.Whole.FirstOrDefault(x => x.Path == p);
                Put(5, col, $"Path {p}", 1);
                Put(6, col, "X", 2); Put(6, col + 1, "Y-Tendon (m)", 2);
                var pts = (was != null && was.Pts.Count > 0 ? was.Pts : DefaultX(spans).Select(x => (x, double.NaN))).ToList();
                // every split gets its own row in X (in its place along the path), Y empty to be typed
                if (g != null && old != null && old.Splits.TryGetValue(p, out string sps))
                    foreach (string sx in sps.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
                    {
                        double st = g.StationOf(sx);
                        if (double.IsNaN(st) || pts.Any(q => Math.Abs(g.StationOf(q.x) - st) < 1e-6)) continue;
                        int at = pts.FindIndex(q => !TendonGeo.IsAnchor(q.x) && g.StationOf(q.x) > st);
                        if (at < 0) at = Math.Max(0, pts.Count - 1);
                        pts.Insert(at, (sx, double.NaN));
                    }
                int r = 7;
                foreach (var q in pts)
                {
                    Put(r, col, q.x, 4);
                    if (double.IsNaN(q.y)) Put(r, col + 1, "", 3); else Put(r, col + 1, q.y.ToString("R", CI), 3, true);
                    r++;
                }
                r++;
                string split = old != null && old.Splits.TryGetValue(p, out string os) ? os : (spans >= 2 ? (p == 1 ? "0.85L2" : "0.15L2") : "");
                Put(r, col, SplitRow, 2); Put(r, col + 1, split, 3); r++;
                for (int k = 0; k < DataRows.Length; k++, r++)
                {
                    Put(r, col, DataRows[k], 4);
                    string v = was != null && was.Data.TryGetValue(DataRows[k], out string dv) ? dv : DataDefaults[k];
                    Put(r, col + 1, v, 3, double.TryParse(v, NumberStyles.Float, CI, out _));
                }
                col += 3;
            }

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"").Append(M.NamespaceName).Append("\"><cols>");
            for (int c = 1; c < col; c++) sb.Append($"<col min=\"{c}\" max=\"{c}\" width=\"{(c % 3 == 0 ? 3 : 22)}\" customWidth=\"1\"/>");
            sb.Append("</cols><sheetData>");
            foreach (var row in rows)
            {
                sb.Append($"<row r=\"{row.Key}\">");
                foreach (var cell in row.Value)
                {
                    string refc = ColName(cell.Key) + row.Key.ToString(CI);
                    int sid = cell.Value.style;
                    if (cell.Value.num) sb.Append($"<c r=\"{refc}\" s=\"{sid}\"><v>{cell.Value.v}</v></c>");
                    else if (cell.Value.v.Length == 0) sb.Append($"<c r=\"{refc}\" s=\"{sid}\"/>");
                    else sb.Append($"<c r=\"{refc}\" s=\"{sid}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(cell.Value.v)}</t></is></c>");
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");

            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                int first = AddStyles(zip);
                string xml = Regex.Replace(sb.ToString(), " s=\"(\\d+)\"", mm => " s=\"" + (first - 1 + int.Parse(mm.Groups[1].Value) - StylePlaceholder) + "\"");
                string part = SheetPart(zip) ?? NewSheet(zip);
                zip.GetEntry(part)?.Delete();
                using (var w = new StreamWriter(zip.CreateEntry(part, CompressionLevel.Optimal).Open(), new UTF8Encoding(false))) w.Write(xml);
            }
        }

        private const int StylePlaceholder = 1000;

        private static string ColName(int c) { string s = ""; while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; } return s; }

        private static XDocument Load(ZipArchive zip, string name)
        {
            ZipArchiveEntry e = zip.GetEntry(name);
            if (e == null) return null;
            using (Stream s = e.Open()) return XDocument.Load(s);
        }

        private static void Save(ZipArchive zip, string name, XDocument doc)
        {
            zip.GetEntry(name)?.Delete();
            using (var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false))) doc.Save(w, SaveOptions.DisableFormatting);
        }

        // 4 styles: 1 title (navy, white), 2 header (amber), 3 input (light orange), 4 label (grey); returns the index of style 1
        private static int AddStyles(ZipArchive zip)
        {
            XDocument st = Load(zip, "xl/styles.xml");
            XElement root = st.Root;
            XElement fonts = root.Element(M + "fonts"), fills = root.Element(M + "fills"), borders = root.Element(M + "borders"), xfs = root.Element(M + "cellXfs");
            int f0 = fonts.Elements(M + "font").Count();
            fonts.Add(new XElement(M + "font", new XElement(M + "b"), new XElement(M + "sz", new XAttribute("val", 11)), new XElement(M + "color", new XAttribute("rgb", "FFFFFFFF")), new XElement(M + "name", new XAttribute("val", "Calibri"))));
            fonts.Add(new XElement(M + "font", new XElement(M + "b"), new XElement(M + "sz", new XAttribute("val", 11)), new XElement(M + "name", new XAttribute("val", "Calibri"))));
            fonts.SetAttributeValue("count", fonts.Elements(M + "font").Count());
            int fl0 = fills.Elements(M + "fill").Count();
            foreach (string rgb in new[] { "FF1F3A5F", "FFFAC775", "FFFAEEDA", "FFF1EFE8" })
                fills.Add(new XElement(M + "fill", new XElement(M + "patternFill", new XAttribute("patternType", "solid"),
                    new XElement(M + "fgColor", new XAttribute("rgb", rgb)), new XElement(M + "bgColor", new XAttribute("indexed", 64)))));
            fills.SetAttributeValue("count", fills.Elements(M + "fill").Count());
            int b0 = borders.Elements(M + "border").Count();
            XElement Side(string n) => new XElement(M + n, new XAttribute("style", "thin"), new XElement(M + "color", new XAttribute("rgb", "FF9E9E9E")));
            borders.Add(new XElement(M + "border", Side("left"), Side("right"), Side("top"), Side("bottom"), new XElement(M + "diagonal")));
            borders.SetAttributeValue("count", borders.Elements(M + "border").Count());
            int x0 = xfs.Elements(M + "xf").Count();
            (int font, int fill)[] defs = { (f0, fl0), (f0 + 1, fl0 + 1), (0, fl0 + 2), (0, fl0 + 3) };
            foreach (var d in defs)
                xfs.Add(new XElement(M + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", d.font), new XAttribute("fillId", d.fill), new XAttribute("borderId", b0),
                    new XAttribute("xfId", 0), new XAttribute("applyFont", 1), new XAttribute("applyFill", 1), new XAttribute("applyBorder", 1), new XAttribute("applyAlignment", 1),
                    new XElement(M + "alignment", new XAttribute("horizontal", "center"), new XAttribute("vertical", "center"))));
            xfs.SetAttributeValue("count", xfs.Elements(M + "xf").Count());
            Save(zip, "xl/styles.xml", st);
            return x0;
        }

        private static string SheetPart(ZipArchive zip)
        {
            XDocument wb = Load(zip, "xl/workbook.xml"), rels = Load(zip, "xl/_rels/workbook.xml.rels");
            XElement sh = wb.Root.Element(M + "sheets").Elements(M + "sheet").FirstOrDefault(x => (string)x.Attribute("name") == Name);
            if (sh == null) return null;
            string rid = (string)sh.Attribute(R + "id");
            string target = (string)rels.Root.Elements(PR + "Relationship").First(x => (string)x.Attribute("Id") == rid).Attribute("Target");
            return target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
        }

        private static string NewSheet(ZipArchive zip)
        {
            XDocument wb = Load(zip, "xl/workbook.xml"), rels = Load(zip, "xl/_rels/workbook.xml.rels"), ct = Load(zip, "[Content_Types].xml");
            int n = 1;
            while (zip.GetEntry($"xl/worksheets/sheet{n}.xml") != null) n++;
            string part = $"xl/worksheets/sheet{n}.xml";
            int rn = 1;
            var ids = new HashSet<string>(rels.Root.Elements(PR + "Relationship").Select(x => (string)x.Attribute("Id")));
            while (ids.Contains("rId" + rn)) rn++;
            rels.Root.Add(new XElement(PR + "Relationship", new XAttribute("Id", "rId" + rn),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), new XAttribute("Target", $"worksheets/sheet{n}.xml")));
            XElement sheets = wb.Root.Element(M + "sheets");
            int sid = sheets.Elements(M + "sheet").Select(x => (int)x.Attribute("sheetId")).DefaultIfEmpty(0).Max() + 1;
            sheets.Add(new XElement(M + "sheet", new XAttribute("name", Name), new XAttribute("sheetId", sid), new XAttribute(R + "id", "rId" + rn)));
            ct.Root.Add(new XElement(CT + "Override", new XAttribute("PartName", "/" + part),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
            Save(zip, "xl/workbook.xml", wb);
            Save(zip, "xl/_rels/workbook.xml.rels", rels);
            Save(zip, "[Content_Types].xml", ct);
            return part;
        }
    }

    // ================================================================ geometry from the bridge Excel
    internal sealed class TendonGeo
    {
        public List<double> AxisSta = new List<double>();
        public List<string> AxisName = new List<string>();
        public double DeckStart, DeckEnd;
        public List<StationRow> Rows;
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        public TendonGeo(BridgeExcel bx)
        {
            foreach (AxisData a in bx.Axes.OrderBy(a => a.Station)) { AxisSta.Add(a.Station); AxisName.Add(a.Name); }
            Rows = bx.Stations.Values.Where(s => !double.IsNaN(s.Sta)).OrderBy(s => s.Sta).ToList();
            if (AxisSta.Count == 0 && Rows.Count > 0)
                foreach (StationRow r in Rows.Where(r => r.Type == "Axis")) { AxisSta.Add(r.Sta); AxisName.Add(r.Remark); }
            DeckStart = Rows.Count > 0 ? Rows[0].Sta : AxisSta.FirstOrDefault();
            DeckEnd = Rows.Count > 0 ? Rows[Rows.Count - 1].Sta : AxisSta.LastOrDefault();
        }

        public int Spans => Math.Max(0, AxisSta.Count - 1);

        // "0.85L2" -> station; Anchor1 / Anchor2 -> NaN (the caller puts them at the stage ends)
        public double StationOf(string x)
        {
            Match m = Regex.Match(x ?? "", @"^\s*([0-9.]+)\s*L\s*(\d+)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) return double.NaN;
            double f = double.Parse(m.Groups[1].Value, CI);
            int span = int.Parse(m.Groups[2].Value);
            if (span < 1 || span > Spans) return double.NaN;
            return AxisSta[span - 1] + f * (AxisSta[span] - AxisSta[span - 1]);
        }

        public static bool IsAnchor(string x) => (x ?? "").Trim().StartsWith("Anc", StringComparison.OrdinalIgnoreCase);

        // the stations of a profile (anchors at the stage ends)
        public List<(string x, double sta, double y)> Stations(TendonSet set, TendonProfile p)
        {
            var res = new List<(string, double, double)>();
            var inner = p.Pts.Where(q => !IsAnchor(q.x)).Select(q => (q.x, sta: StationOf(q.x), q.y)).Where(q => !double.IsNaN(q.sta)).ToList();
            if (inner.Count == 0) return res;
            double s0 = p.Stage == 1 ? DeckStart : inner[0].sta, s1 = p.Stage == set.Stages ? DeckEnd : inner[inner.Count - 1].sta;
            foreach (var q in p.Pts)
            {
                if (IsAnchor(q.x)) res.Add((q.x, q.x.EndsWith("1") ? Math.Min(s0, inner[0].sta) : Math.Max(s1, inner[inner.Count - 1].sta), q.y));
                else { double st = StationOf(q.x); if (!double.IsNaN(st)) res.Add((q.x, st, q.y)); }
            }
            return res.OrderBy(q => q.Item2).ToList();
        }

        // the split of a path after a stage: the last point before Anchor2
        public double SplitAfter(TendonProfile p)
        {
            var inner = p.Pts.Where(q => !IsAnchor(q.x)).Select(q => StationOf(q.x)).Where(v => !double.IsNaN(v)).ToList();
            return inner.Count == 0 ? double.NaN : inner.Max();
        }

        // centre-line point (shared m), left->right unit and top level at a station (from the section edge points)
        public bool At(double sta, out double x, out double y, out double z, out double ux, out double uy)
        {
            x = y = z = ux = uy = double.NaN;
            var rs = Rows.Where(r => r.HasPoints).ToList();
            if (rs.Count < 2) return false;
            int i = 0;
            while (i < rs.Count - 2 && rs[i + 1].Sta < sta) i++;
            StationRow a = rs[i], b = rs[i + 1];
            double t = Math.Abs(b.Sta - a.Sta) < 1e-9 ? 0 : (sta - a.Sta) / (b.Sta - a.Sta);
            double L(double va, double vb) => va + (vb - va) * t;
            double lx = L(a.LX, b.LX), ly = L(a.LY, b.LY), lz = L(a.LZ, b.LZ), rx = L(a.RX, b.RX), ry = L(a.RY, b.RY), rz = L(a.RZ, b.RZ);
            x = (lx + rx) / 2; y = (ly + ry) / 2; z = (lz + rz) / 2;
            double len = Math.Sqrt((rx - lx) * (rx - lx) + (ry - ly) * (ry - ly));
            if (len < 1e-9) return false;
            ux = (rx - lx) / len; uy = (ry - ly) / len;
            return true;
        }
    }

    // ================================================================ the dialog
    internal sealed class TendonForm : Form
    {
        public string TitleBlock;
        public double VScale = 3;
        public bool Make3D = true;
        public int Stages, Paths;

        public TendonForm(TendonSet set, TendonGeo g, List<string> titleBlocks, bool update)
        {
            Text = update ? "AB Bridges - Update Tendon" : "AB Bridges - Create Tendon";
            Font = new System.Drawing.Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 470);
            BackColor = WinColor.White;
            Controls.Add(new Label { Text = "  " + Text.Replace("AB Bridges - ", "AB Bridges  ·  "), Dock = DockStyle.Top, Height = 34, BackColor = UiTheme.Navy, ForeColor = WinColor.White,
                                     Font = new System.Drawing.Font("Segoe UI Semibold", 11f), TextAlign = ContentAlignment.MiddleLeft });
            var list = new ListView { Location = new Point(14, 48), Size = new Size(492, 200), View = System.Windows.Forms.View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            list.Columns.Add("Profile", 130); list.Columns.Add("Points", 70); list.Columns.Add("Split after this stage", 270);
            foreach (TendonProfile p in set.Profiles.OrderBy(x => x.Path).ThenBy(x => x.Stage))
            {
                double sp = g.SplitAfter(p);
                string last = p.Pts.Where(q => !TendonGeo.IsAnchor(q.x)).Select(q => q.x).LastOrDefault() ?? "";
                int empty = p.Pts.Count - p.Filled;
                var it = new ListViewItem(new[] { p.Title, empty > 0 ? $"{empty} empty" : $"✓ {p.Pts.Count}",
                    p.Stage == set.Stages ? "(last stage - to the deck end)" : $"{last}  =  {sp:0.000}" });
                if (empty > 0) it.ForeColor = WinColor.Firebrick;
                list.Items.Add(it);
            }
            Controls.Add(list);
            Controls.Add(new Label { Text = "Stages = the splits typed in the Excel under every path (Split at). Change them there, then Update Tendon.",
                                     Location = new Point(14, 252), Size = new Size(492, 32), ForeColor = WinColor.DimGray });

            int y = 292;
            Controls.Add(new Label { Text = "Stages", Location = new Point(14, y + 3), AutoSize = true });
            var nS = new NumericUpDown { Location = new Point(110, y), Width = 60, Minimum = 1, Maximum = 9, Value = Math.Max(1, set.Stages), Enabled = false };
            Controls.Add(nS);
            Controls.Add(new Label { Text = "Paths", Location = new Point(200, y + 3), AutoSize = true });
            var nP = new NumericUpDown { Location = new Point(250, y), Width = 60, Minimum = 1, Maximum = 4, Value = Math.Max(1, set.Whole.Count) };
            Controls.Add(nP);
            y += 34;
            Controls.Add(new Label { Text = "Vertical scale", Location = new Point(14, y + 3), AutoSize = true });
            var vs = new NumericUpDown { Location = new Point(110, y), Width = 60, Minimum = 1, Maximum = 10, DecimalPlaces = 1, Value = 3 };
            Controls.Add(vs);
            var c3 = new CheckBox { Text = "3D tendons in the webs", Location = new Point(200, y + 2), AutoSize = true, Checked = true };
            Controls.Add(c3);
            y += 34;
            Controls.Add(new Label { Text = "Title block", Location = new Point(14, y + 3), AutoSize = true });
            var tb = new ComboBox { Location = new Point(110, y), Width = 396, DropDownStyle = ComboBoxStyle.DropDownList, Enabled = !update };
            tb.Items.AddRange(titleBlocks.Cast<object>().ToArray());
            if (tb.Items.Count > 0) tb.SelectedIndex = 0;
            Controls.Add(tb);

            var ok = new Button { Text = update ? "Update" : "Create", Location = new Point(316, 420), Size = new Size(92, 32), BackColor = UiTheme.Navy, ForeColor = WinColor.White, FlatStyle = FlatStyle.Flat };
            var cancel = new Button { Text = "Cancel", Location = new Point(414, 420), Size = new Size(92, 32), DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                TitleBlock = tb.SelectedItem as string; VScale = (double)vs.Value; Make3D = c3.Checked;
                Stages = (int)nS.Value; Paths = (int)nP.Value;
                DialogResult = DialogResult.OK; Close();
            };
            Controls.Add(ok); Controls.Add(cancel);
            CancelButton = cancel;
            Shown += (s, e) => UiTheme.Snap(this, "Revit " + Text.Replace("AB Bridges - ", ""));
        }
    }

    // ================================================================ commands
    [Transaction(TransactionMode.Manual)]
    public class CreateTendonCommand : UI.IExternalCommand
    {
        public UI.Result Execute(UI.ExternalCommandData data, ref string message, ElementSet elements) => TendonRun.Run(data, false);
    }

    [Transaction(TransactionMode.Manual)]
    public class UpdateTendonCommand : UI.IExternalCommand
    {
        public UI.Result Execute(UI.ExternalCommandData data, ref string message, ElementSet elements) => TendonRun.Run(data, true);
    }

    internal static class TendonRun
    {
        private const string ExcelKey = "SUB|EXCEL", IdsKey = "TEN|IDS|", SheetKey = "TEN|SHEET|", ViewKey = "TEN|VIEW|";
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private static double Ft(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);

        public static UI.Result Run(UI.ExternalCommandData data, bool update)
        {
            UI.UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            try
            {
                Dictionary<string, string> map = AbStore.Load(doc);
                string excel = map.TryGetValue(ExcelKey, out string ex0) && File.Exists(ex0) ? ex0 : null;
                if (excel == null)
                    using (var dlg = new OpenFileDialog { Title = "Choose the bridge Excel", Filter = "Excel (*.xlsx)|*.xlsx" })
                    {
                        if (dlg.ShowDialog() != DialogResult.OK) return UI.Result.Cancelled;
                        excel = dlg.FileName;
                    }
                BridgeExcel bx = BridgeExcel.Load(excel, false);
                var g = new TendonGeo(bx);
                if (g.Spans < 1) { UI.TaskDialog.Show("AB Bridges", "The Excel has no axes (sheet Substructure / Super Structure)."); return UI.Result.Cancelled; }

                TendonSet set = TendonSheet.Read(excel, g);
                if (set == null)
                {
                    TendonSheet.Write(excel, 2, g.Spans, null);
                    UI.TaskDialog.Show("AB Bridges", $"The sheet \"{TendonSheet.Name}\" was added to the Excel (2 paths).\n\nFor every path type the whole tendon (X / Y from Anchor1 to Anchor2), the split(s) and the data, save the Excel, then run Create Tendon again.\n\n{excel}");
                    return UI.Result.Succeeded;
                }

                List<string> tbs = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>().Select(s => s.FamilyName + " : " + s.Name).OrderBy(n => n).ToList();
                TendonForm form;
                using (form = new TendonForm(set, g, tbs, update))
                    if (form.ShowDialog() != DialogResult.OK) return UI.Result.Cancelled;

                // more / fewer stages or paths: the sheet is written again (values kept) - the user fills the new blocks first
                bool missing = set.Whole.Any(w => set.Splits.TryGetValue(w.Path, out string sp0) &&
                    sp0.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Any(x => !w.Pts.Any(q => Math.Abs(g.StationOf(q.x) - g.StationOf(x.Trim())) < 1e-6)));
                if (missing)
                {
                    TendonSheet.Write(excel, set.Whole.Count, g.Spans, set, g);
                    UI.TaskDialog.Show("AB Bridges", "The split points were added as new rows in X (in their place along the path). Type their Y, save the Excel, then run it again.");
                    return UI.Result.Succeeded;
                }
                var noY = set.Whole.Where(w => set.Splits.TryGetValue(w.Path, out string sp1) &&
                    sp1.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                       .Any(x => w.Pts.Any(q => Math.Abs(g.StationOf(q.x) - g.StationOf(x.Trim())) < 1e-6 && double.IsNaN(q.y))))
                    .Select(w => w.Path).ToList();
                if (noY.Count > 0)
                {
                    UI.TaskDialog.Show("AB Bridges", "Type the Y of the split point in Path " + string.Join(", ", noY) + ", save the Excel, then run it again.");
                    return UI.Result.Succeeded;
                }
                if (form.Paths != set.Whole.Count)
                {
                    TendonSheet.Write(excel, form.Paths, g.Spans, set);
                    UI.TaskDialog.Show("AB Bridges", $"The Excel now has {form.Paths} path(s) (your values kept). Fill the new ones, save, then run it again.");
                    return UI.Result.Succeeded;
                }

                var log = new StringBuilder();
                // 3D tendons: one family per stage, path and web (made outside the project transaction)
                var made3D = new List<(TendonProfile p, double off, Family fam)>();
                if (form.Make3D)
                    foreach (TendonProfile p in set.Profiles.Where(x => x.Filled == x.Pts.Count && x.Pts.Count > 1))
                        foreach (double off in Offsets(p))
                        {
                            try
                            {
                                Family f = Tendon3D.Build(data.Application.Application, doc, g, set, p, off, out XYZ _);
                                if (f != null) made3D.Add((p, off, f));
                            }
                            catch (Exception e) { log.AppendLine($"{p.Title} web {off:0.00}: 3D not made ({e.Message})"); }
                        }

                using (var t = new Transaction(doc, update ? "AB Bridges - Update Tendon" : "AB Bridges - Create Tendon"))
                {
                    t.Start();
                    // the 3D instances (a family loaded again updates its instance by itself)
                    foreach (var m in made3D)
                    {
                        string key = IdsKey + "3D|" + m.fam.Name;
                        if (map.TryGetValue(key, out string idv) && long.TryParse(idv, out long lid) && doc.GetElement(new ElementId(lid)) != null) continue;
                        FamilySymbol sym = doc.GetElement(m.fam.GetFamilySymbolIds().First()) as FamilySymbol;
                        if (sym == null) continue;
                        if (!sym.IsActive) sym.Activate();
                        XYZ at = Tendon3D.Origin(doc, g, set, m.p, m.off);
                        List<Level> levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
                        var placed = SubstructureCommand.PlaceOne(doc, sym, at, 0, XYZ.BasisX, levels);
                        map[key] = placed.inst.Id.Value.ToString(CI);
                    }
                    if (made3D.Count > 0) log.AppendLine($"{made3D.Count} 3D tendon(s) made.");

                    // like the Parsons sheets: one sheet per path x stage; a long stage is cut into parts stacked
                    // on the sheet (FOR CONTINUATION, SEE BELOW / ABOVE); data table and notes at the bottom left
                    FamilySymbol tb = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>().FirstOrDefault(x => x.FamilyName + " : " + x.Name == form.TitleBlock);
                    ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x => x.ViewFamily == ViewFamily.Drafting);
                    if (vft == null) log.AppendLine("No drafting view type in the project.");
                    int nSheets = 0;
                    foreach (TendonProfile p in set.Profiles)
                    {
                        if (vft == null) break;
                        string pk = p.Stage + "|" + p.Path;
                        var parts = TendonDraw.Parts(g, set, p, 150);
                        // views of parts that are no longer needed go
                        for (int k = parts.Count + 1; k <= 6; k++)
                            if (map.TryGetValue(ViewKey + pk + "|" + k, out string ov) && long.TryParse(ov, out long lo))
                            { try { doc.Delete(new ElementId(lo)); } catch { } map.Remove(ViewKey + pk + "|" + k); map.Remove(IdsKey + "2D|" + pk + "|" + k); }
                        var pv = new List<View>();
                        for (int k = 1; k <= parts.Count; k++)
                        {
                            string vk = ViewKey + pk + "|" + k;
                            View v = map.TryGetValue(vk, out string vid) && long.TryParse(vid, out long l2) ? doc.GetElement(new ElementId(l2)) as View : null;
                            if (v == null)
                            {
                                v = ViewDrafting.Create(doc, vft.Id);
                                try { v.Name = $"AB Tendon - Path {p.Path} - Stage {p.Stage}" + (parts.Count > 1 ? $" - Part {k}" : ""); } catch { }
                                v.Scale = 150;
                                map[vk] = v.Id.Value.ToString(CI);
                            }
                            string dk = IdsKey + "2D|" + pk + "|" + k;
                            if (map.TryGetValue(dk, out string old))
                                foreach (string sid in old.Split(',')) if (long.TryParse(sid, out long l3)) { try { doc.Delete(new ElementId(l3)); } catch { } }
                            List<ElementId> ids = TendonDraw.Draw(doc, v, g, set, p, form.VScale, parts[k - 1].a, parts[k - 1].b, k, parts.Count);
                            map[dk] = string.Join(",", ids.Select(i => i.Value.ToString(CI)));
                            pv.Add(v);
                        }

                        // the sheet: made once, kept on Update; missing parts are placed, the data table / notes redrawn
                        string sk = SheetKey + pk;
                        ViewSheet sheet = map.TryGetValue(sk, out string shv) && long.TryParse(shv, out long l4) ? doc.GetElement(new ElementId(l4)) as ViewSheet : null;
                        if (sheet == null)
                        {
                            if (update) continue;
                            sheet = ViewSheet.Create(doc, tb?.Id ?? ElementId.InvalidElementId);
                            try { sheet.Name = $"TENDON PROFILE - PATH {p.Path} - STAGE {p.Stage}"; sheet.SheetNumber = $"AB-TP-{p.Stage:00}-{p.Path}"; } catch { }
                            map[sk] = sheet.Id.Value.ToString(CI);
                            nSheets++;
                        }
                        BoundingBoxUV o = sheet.Outline;
                        double W = o.Max.U - o.Min.U, H = o.Max.V - o.Min.V;
                        if (W < Ft(0.2)) { W = Ft(0.841); H = Ft(0.594); o = new BoundingBoxUV(0, 0, W, H); }
                        double right = o.Min.U + W * 0.80;                       // the title block column at the right
                        double zoneTop = o.Max.V - H * 0.06, zoneBot = o.Min.V + H * 0.12;
                        var placed = new HashSet<ElementId>(sheet.GetAllPlacedViews());
                        for (int k = 0; k < pv.Count; k++)
                        {
                            if (placed.Contains(pv[k].Id) || !Viewport.CanAddViewToSheet(doc, sheet.Id, pv[k].Id)) continue;
                            double cv = zoneTop - (zoneTop - zoneBot) * (k + 0.5) / pv.Count;
                            double cu = k == 0 ? (o.Min.U + right) / 2 : (o.Min.U + right) / 2 + W * 0.08;   // the lower part sits a bit to the right, the table at its left
                            Viewport.Create(doc, sheet.Id, pv[k].Id, new XYZ(cu, cv, 0));
                        }
                        string sdk = IdsKey + "SHEET|" + pk;
                        if (map.TryGetValue(sdk, out string olds))
                            foreach (string sid in olds.Split(',')) if (long.TryParse(sid, out long l5)) { try { doc.Delete(new ElementId(l5)); } catch { } }
                        var sids = TendonDraw.Data(doc, sheet, new XYZ(o.Min.U + W * 0.27, o.Min.V + H * 0.35, 0), 1.0 / 1000, p);
                        sids.AddRange(TendonDraw.Notes(doc, sheet, new XYZ(o.Min.U + W * 0.04, o.Min.V + H * 0.17, 0), p));
                        map[sdk] = string.Join(",", sids.Select(i => i.Value.ToString(CI)));
                    }
                    if (nSheets > 0) log.AppendLine($"{nSheets} sheet(s) with the profile views.");
                    AbStore.Save(doc, map);
                    t.Commit();
                }
                UI.TaskDialog.Show("AB Bridges - Tendon Profile", log.Length > 0 ? log.ToString() : "Done.");
                return UI.Result.Succeeded;
            }
            catch (Exception ex)
            {
                UI.TaskDialog.Show("AB Bridges - error", ex.Message + "\n\n" + ex);
                return UI.Result.Cancelled;
            }
        }

        public static List<double> Offsets(TendonProfile p)
        {
            var res = new List<double>();
            if (p.Data.TryGetValue("Web offsets from CL (m)", out string v))
                foreach (string part in v.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    if (double.TryParse(part.Trim(), NumberStyles.Float, CI, out double d)) res.Add(d);
            return res.Count > 0 ? res : new List<double> { 0 };
        }
    }

    // ================================================================ 3D tendon (a family with the swept tube)
    internal static class Tendon3D
    {
        private static double Ft(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);

        // points of the tendon centre (internal), along the stations of the profile
        public static List<XYZ> Points(Document doc, TendonGeo g, TendonSet set, TendonProfile p, double off)
        {
            var geo = new Geo(doc);
            var res = new List<XYZ>();
            foreach (var q in g.Stations(set, p))
            {
                if (!g.At(q.sta, out double x, out double y, out double z, out double ux, out double uy)) continue;
                XYZ pt = geo.ToInternal(x + ux * off, y + uy * off, z - q.y);
                if (res.Count == 0 || res[res.Count - 1].DistanceTo(pt) > 1e-3) res.Add(pt);
            }
            return res;
        }

        public static XYZ Origin(Document doc, TendonGeo g, TendonSet set, TendonProfile p, double off) => Points(doc, g, set, p, off).FirstOrDefault() ?? XYZ.Zero;

        public static Family Build(Autodesk.Revit.ApplicationServices.Application app, Document doc, TendonGeo g, TendonSet set, TendonProfile p, double off, out XYZ origin)
        {
            List<XYZ> pts = Points(doc, g, set, p, off);
            origin = pts.FirstOrDefault();
            if (pts.Count < 2) return null;
            string template = DeckBuilder.FindTemplate(app) ?? throw new InvalidOperationException("no family template found");
            Document fdoc = app.NewFamilyDocument(template);
            string name = $"AB Tendon P{p.Path} S{p.Stage} W{off.ToString("0.00", CultureInfo.InvariantCulture).Replace('-', 'm')}";
            string file = Path.Combine(Path.GetTempPath(), name + ".rfa");
            try
            {
                using (var t = new Transaction(fdoc, "tendon"))
                {
                    t.Start();
                    var local = pts.Select(q => q - origin).ToList();
                    Curve path = local.Count == 2 ? (Curve)Line.CreateBound(local[0], local[1]) : HermiteSpline.Create(local, false);
                    var loop = new CurveLoop(); loop.Append(path);
                    XYZ tan = path.ComputeDerivatives(path.GetEndParameter(0), false).BasisX.Normalize();
                    Plane pl = Plane.CreateByNormalAndOrigin(tan, path.GetEndPoint(0));
                    double r = Ft(0.05);
                    var prof = new CurveLoop();
                    prof.Append(Arc.Create(pl, r, 0, Math.PI));
                    prof.Append(Arc.Create(pl, r, Math.PI, 2 * Math.PI));
                    Solid solid = GeometryCreationUtilities.CreateSweptGeometry(loop, 0, path.GetEndParameter(0), new List<CurveLoop> { prof });
                    FreeFormElement.Create(fdoc, solid);
                    t.Commit();
                }
                fdoc.SaveAs(file, new SaveAsOptions { OverwriteExistingFile = true });
                return fdoc.LoadFamily(doc, new DeckBuilder.LoadOpts());
            }
            finally { fdoc.Close(false); }
        }
    }

    // ================================================================ the 2D drawing (detail, like the PT drawings)
    internal static class TendonDraw
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private static double Ft(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);

        // a stage longer than the sheet is cut into parts at its points (each part ~ equal, max ~90 m at 1:150 on A1)
        public static List<(int a, int b)> Parts(TendonGeo g, TendonSet set, TendonProfile p, int scale)
        {
            var st = g.Stations(set, p);
            var res = new List<(int, int)>();
            if (st.Count < 2) { res.Add((0, Math.Max(0, st.Count - 1))); return res; }
            double L = st.Last().sta - st.First().sta, max = 0.60 * scale;
            int n = Math.Max(1, Math.Min(4, (int)Math.Ceiling(L / max - 1e-6)));
            int a = 0;
            for (int k = 1; k < n; k++)
            {
                double target = st.First().sta + L * k / n;
                int b = Enumerable.Range(a + 1, st.Count - a - 2).OrderBy(i => Math.Abs(st[i].sta - target)).FirstOrDefault();
                if (b <= a) break;
                res.Add((a, b)); a = b;
            }
            res.Add((a, st.Count - 1));
            return res;
        }

        public static List<ElementId> Draw(Document doc, View v, TendonGeo g, TendonSet set, TendonProfile p, double vs, int ia, int ib, int part, int parts)
        {
            var ids = new List<ElementId>();
            var all = g.Stations(set, p);
            if (all.Count < 2) return ids;
            double total = all.Last().sta - all.First().sta;
            ib = Math.Min(ib, all.Count - 1); ia = Math.Max(0, Math.Min(ia, ib - 1));
            var st = all.GetRange(ia, ib - ia + 1);
            bool first = part == 1, last = part == parts;
            double s0 = st.First().sta, s1 = st.Last().sta;
            double depth = Num(p, "Box depth (m)", 2.0);
            double paper = v.Scale / 1000.0;                 // 1 mm on the sheet in metres of the view
            XYZ P(double sta, double yDown) => new XYZ(Ft(sta - s0), Ft(-yDown * vs), 0);
            ElementId tt = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            GraphicsStyle thin = SubstructureCommand.LineStyle(doc, "AB Tendon Thin", new RColor(0, 0, 0));
            GraphicsStyle dash = SubstructureCommand.LineStyle(doc, "AB Tendon Hidden", new RColor(60, 60, 60));
            GraphicsStyle cable = SubstructureCommand.LineStyle(doc, "AB Tendon", new RColor(200, 30, 30));
            try
            {
                LinePatternElement lp = LinePatternElement.GetLinePatternElementByName(doc, "Dash") ?? new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>().FirstOrDefault(x => x.Name.Contains("Dash"));
                if (lp != null && dash != null) dash.GraphicsStyleCategory.SetLinePatternId(lp.Id, GraphicsStyleType.Projection);
                if (cable != null) cable.GraphicsStyleCategory.SetLineWeight(6, GraphicsStyleType.Projection);
            }
            catch { }

            void Ln(XYZ a, XYZ b, GraphicsStyle gs) { if (a.DistanceTo(b) < 1e-4) return; try { DetailCurve dc = doc.Create.NewDetailCurve(v, Line.CreateBound(a, b)); if (gs != null) dc.LineStyle = gs; ids.Add(dc.Id); } catch { } }
            void Tx(XYZ at, string t, double rot = 0, HorizontalTextAlignment ha = HorizontalTextAlignment.Center)
            {
                try { ids.Add(TextNote.Create(doc, v.Id, at, t, new TextNoteOptions(tt) { Rotation = rot, HorizontalAlignment = ha }).Id); } catch { }
            }

            // box: grey fill, outline, the section lines, the cells (dashed) between the diaphragms
            var box = new CurveLoop();
            box.Append(Line.CreateBound(P(s0, 0), P(s1, 0))); box.Append(Line.CreateBound(P(s1, 0), P(s1, depth)));
            box.Append(Line.CreateBound(P(s1, depth), P(s0, depth))); box.Append(Line.CreateBound(P(s0, depth), P(s0, 0)));
            try
            {
                FilledRegionType frt = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                    .OrderByDescending(x => x.Name.IndexOf("Solid", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("Gray", StringComparison.OrdinalIgnoreCase) >= 0).FirstOrDefault();
                if (frt != null) ids.Add(FilledRegion.Create(doc, frt.Id, v.Id, new List<CurveLoop> { box }).Id);
            }
            catch { }
            foreach (Curve c in box) Ln(c.GetEndPoint(0), c.GetEndPoint(1), thin);
            var rows = g.Rows.Where(r => r.Sta >= s0 - 1e-6 && r.Sta <= s1 + 1e-6).ToList();
            foreach (StationRow r in rows) Ln(P(r.Sta, 0), P(r.Sta, depth), thin);
            var solid = new List<(double a, double b)>();
            for (int i = 0; i < rows.Count; i++)
            {
                string ty = rows[i].Type ?? "";
                if ((ty.StartsWith("Diaphragm face") || ty.StartsWith("Internal")) && i + 1 < rows.Count)
                {
                    string nx = rows[i + 1].Type ?? "";
                    if (nx == "Axis" && i + 2 < rows.Count) { solid.Add((rows[i].Sta, rows[i + 2].Sta)); i += 2; }
                    else if (nx.StartsWith("Internal") || nx.StartsWith("Diaphragm face")) { solid.Add((rows[i].Sta, rows[i + 1].Sta)); i++; }
                }
                else if (ty == "Corbel") solid.Add((Math.Min(rows[i].Sta, s0), rows[i].Sta));
            }
            double cur = s0;
            foreach (var sd in solid.OrderBy(x => x.a).Concat(new[] { (a: s1, b: s1) }))
            {
                if (sd.a - cur > 0.5)
                {
                    double t1 = 0.25, b1 = depth - 0.22;
                    XYZ a = P(cur + 0.15, t1), b = P(sd.a - 0.15, t1), c = P(sd.a - 0.15, b1), d = P(cur + 0.15, b1);
                    Ln(a, b, dash); Ln(b, c, dash); Ln(c, d, dash); Ln(d, a, dash);
                }
                cur = Math.Max(cur, sd.b);
            }

            // axes: bubble, dashed line; span dims measured along the C/L; the stage dim
            double yb = -Ft(30 * paper), ydim = -Ft(14 * paper);
            for (int i = 0; i < g.AxisSta.Count; i++)
            {
                double a = g.AxisSta[i];
                if (a < s0 - 1e-6 || a > s1 + 1e-6) continue;
                XYZ c = new XYZ(Ft(a - s0), -yb, 0);
                double rr = Ft(3.2 * paper);
                try { ids.Add(doc.Create.NewDetailCurve(v, Arc.Create(c, rr, 0, Math.PI, XYZ.BasisX, XYZ.BasisY)).Id); ids.Add(doc.Create.NewDetailCurve(v, Arc.Create(c, rr, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY)).Id); } catch { }
                Tx(c + new XYZ(0, Ft(1.2 * paper), 0), g.AxisName[i]);
                Ln(new XYZ(c.X, c.Y - rr, 0), P(a, depth) + new XYZ(0, -Ft(2 * paper), 0), dash);
            }
            for (int i = 0; i + 1 < g.AxisSta.Count; i++)
            {
                double a = Math.Max(g.AxisSta[i], s0), b = Math.Min(g.AxisSta[i + 1], s1);
                if (b - a < 0.5) continue;
                XYZ pa = new XYZ(Ft(a - s0), -ydim, 0), pb = new XYZ(Ft(b - s0), -ydim, 0);
                Ln(pa, pb, thin); Ln(pa + new XYZ(-Ft(paper), -Ft(paper), 0), pa + new XYZ(Ft(paper), Ft(paper), 0), thin); Ln(pb + new XYZ(-Ft(paper), -Ft(paper), 0), pb + new XYZ(Ft(paper), Ft(paper), 0), thin);
                Tx((pa + pb) / 2 + new XYZ(0, Ft(3 * paper), 0), ((b - a) * 1000).ToString("0", CI));
                Tx((pa + pb) / 2 + new XYZ(0, -Ft(1 * paper), 0), "SPAN " + (i + 1));
            }
            if (first) Tx(new XYZ(-Ft(4 * paper), -ydim + Ft(3 * paper), 0), "MEASURED ALONG\nC/L OF BRIDGE", 0, HorizontalTextAlignment.Right);
            double ys = -Ft(24 * paper);
            Ln(new XYZ(0, -ys, 0), new XYZ(Ft(s1 - s0), -ys, 0), thin);
            string jack = p.Data.TryGetValue("Jacking end (Left / Right)", out string je) && je.Trim().StartsWith("R", StringComparison.OrdinalIgnoreCase) ? "RIGHT" : "LEFT";
            Tx(new XYZ(Ft((s1 - s0) / 2), -ys + Ft(3 * paper), 0), $"CONSTRUCTION STAGE {p.Stage}\n{total * 1000:0} ({jack} END JACKING)");

            // the tendon: one smooth curve through the points, * at the jacking end
            var cp = st.Select(q => P(q.sta, double.IsNaN(q.y) ? depth / 2 : q.y)).ToList();
            try
            {
                Curve tc = cp.Count == 2 ? (Curve)Line.CreateBound(cp[0], cp[1]) : HermiteSpline.Create(cp, false);
                DetailCurve dc = doc.Create.NewDetailCurve(v, tc); if (cable != null) dc.LineStyle = cable; ids.Add(dc.Id);
            }
            catch { }
            XYZ je0 = jack == "LEFT" ? cp.First() : cp.Last();
            if ((jack == "LEFT" && first) || (jack == "RIGHT" && last)) Tx(je0 + new XYZ(jack == "LEFT" ? Ft(3 * paper) : -Ft(3 * paper), Ft(4 * paper), 0), "*");
            if (p.Stage < set.Stages && last)
            {
                XYZ cj = P(s1, depth) + new XYZ(-Ft(paper), 0, 0);
                Ln(cj, cj + new XYZ(-Ft(10 * paper), -Ft(10 * paper), 0), thin);
                Tx(cj + new XYZ(-Ft(10 * paper), -Ft(13 * paper), 0), "CONSTRUCTION JOINT", 0, HorizontalTextAlignment.Right);
            }
            // depth dim at the end
            XYZ d0 = P(s1, 0) + new XYZ(Ft(6 * paper), 0, 0), d1 = P(s1, depth) + new XYZ(Ft(6 * paper), 0, 0);
            Ln(d0, d1, thin);
            Tx((d0 + d1) / 2 + new XYZ(Ft(2.5 * paper), 0, 0), (depth * 1000).ToString("0", CI), Math.PI / 2);

            // the table: DISTANCE ALONG C/L / DISTANCE FROM TOP OF BOX TO TENDONS CG (mm)
            double cw = Ft(9 * paper), rh = Ft(14 * paper), x0 = 0, y0 = P(s0, depth).Y - Ft(18 * paper);
            double hw = Ft(48 * paper);
            int n = st.Count;
            var head = new[] { "DISTANCE ALONG C/L", "DISTANCE FROM TOP OF BOX\nTO TENDONS CG IN (mm)" };
            for (int k = 0; k <= 2; k++) Ln(new XYZ(x0 - hw, y0 - k * rh, 0), new XYZ(x0 + n * cw, y0 - k * rh, 0), thin);
            Ln(new XYZ(x0 - hw, y0, 0), new XYZ(x0 - hw, y0 - 2 * rh, 0), thin);
            for (int k = 0; k <= n; k++) Ln(new XYZ(x0 + k * cw, y0, 0), new XYZ(x0 + k * cw, y0 - 2 * rh, 0), thin);
            Tx(new XYZ(x0 - hw / 2, y0 - rh / 2, 0), head[0]);
            Tx(new XYZ(x0 - hw / 2, y0 - 1.5 * rh, 0), head[1]);
            for (int k = 0; k < n; k++)
            {
                double xc = x0 + (k + 0.5) * cw;
                string lab = TendonGeo.IsAnchor(st[k].x) ? (st[k].x.EndsWith("2") ? "Anc.2" : "Anc.1") : st[k].x;
                Tx(new XYZ(xc, y0 - rh / 2, 0), lab, Math.PI / 2);
                Tx(new XYZ(xc, y0 - 1.5 * rh, 0), double.IsNaN(st[k].y) ? "" : (st[k].y * 1000).ToString("0", CI), Math.PI / 2);
            }

            // a cut part: the break line and FOR CONTINUATION, SEE BELOW / ABOVE
            double yTop = Ft(36 * paper), yBot = y0 - 2 * rh;
            if (!last)
            {
                double xb = Ft(s1 - s0) + Ft(12 * paper);
                Ln(new XYZ(xb, yTop, 0), new XYZ(xb, yBot, 0), dash);
                Tx(new XYZ(xb + Ft(5 * paper), (yTop + yBot) / 2, 0), "FOR CONTINUATION, SEE BELOW", Math.PI / 2);
            }
            if (!first)
            {
                double xb = x0 - hw - Ft(4 * paper);
                Ln(new XYZ(xb, yTop, 0), new XYZ(xb, yBot, 0), dash);
                Tx(new XYZ(xb - Ft(5 * paper), (yTop + yBot) / 2, 0), "FOR CONTINUATION, SEE ABOVE", Math.PI / 2);
            }

            // title under the view (underlined) and the scale
            double tyl = y0 - 2 * rh - Ft(12 * paper), xm = Ft((s1 - s0) / 2);
            Tx(new XYZ(xm, tyl, 0), $"WEB POST TENSIONING TENDON PROFILE - PATH {p.Path} - STAGE {p.Stage}" + (parts > 1 ? $" (PART {part} OF {parts})" : ""));
            Ln(new XYZ(xm - Ft(60 * paper), tyl - Ft(4.5 * paper), 0), new XYZ(xm + Ft(60 * paper), tyl - Ft(4.5 * paper), 0), thin);
            Tx(new XYZ(xm - Ft(60 * paper), tyl - Ft(7.5 * paper), 0), "1 : " + v.Scale, 0, HorizontalTextAlignment.Left);
            return ids;
        }

        // the data table, drawn on the sheet (paper = metres per sheet mm)
        public static List<ElementId> Data(Document doc, View v, XYZ at, double paper, TendonProfile p)
        {
            var ids = new List<ElementId>();
            ElementId tt = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            GraphicsStyle thin = SubstructureCommand.LineStyle(doc, "AB Tendon Thin", new RColor(0, 0, 0));
            void Ln(XYZ a, XYZ b) { try { DetailCurve dc = doc.Create.NewDetailCurve(v, Line.CreateBound(a, b)); if (thin != null) dc.LineStyle = thin; ids.Add(dc.Id); } catch { } }
            void Tx(XYZ q, string t) { try { ids.Add(TextNote.Create(doc, v.Id, q, t, new TextNoteOptions(tt) { HorizontalAlignment = HorizontalTextAlignment.Center }).Id); } catch { } }
            double tw = Ft(70 * paper), tv = Ft(18 * paper), trh = Ft(8 * paper);
            string[] keys = { "Total jacking force PJ (kN)", "No. of tendons per web", "No. of strands per tendon", "Area of strand (mm2)", "No. of webs" };
            int n = keys.Length + 1;
            for (int k = 0; k <= n; k++) Ln(new XYZ(at.X, at.Y - k * trh, 0), new XYZ(at.X + tw + tv, at.Y - k * trh, 0));
            foreach (double xx in new[] { at.X, at.X + tw, at.X + tw + tv }) Ln(new XYZ(xx, at.Y, 0), new XYZ(xx, at.Y - n * trh, 0));
            for (int k = 0; k < keys.Length; k++)
            {
                Tx(new XYZ(at.X + tw / 2, at.Y - (k + 0.35) * trh, 0), keys[k].ToUpperInvariant());
                Tx(new XYZ(at.X + tw + tv / 2, at.Y - (k + 0.35) * trh, 0), p.Data.TryGetValue(keys[k], out string kv) ? kv : "");
            }
            Tx(new XYZ(at.X + tw / 2, at.Y - (keys.Length + 0.35) * trh, 0), "* DENOTES JACKING END");
            Tx(new XYZ(at.X + tw + tv / 2, at.Y - (keys.Length + 0.35) * trh, 0), "@CJ");
            return ids;
        }

        // the notes at the bottom left of the sheet
        public static List<ElementId> Notes(Document doc, View v, XYZ at, TendonProfile p)
        {
            var ids = new List<ElementId>();
            ElementId tt = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            string t = "NOTES:\n1. ALL DIMENSIONS ARE IN MILLIMETERS UNLESS OTHERWISE NOTED.\n2. PJ REPRESENTS THE JACKING FORCE REQUIRED.\n" +
                       "3. Y = DISTANCE FROM TOP OF BOX TO TENDONS CG.\n4. VERTICAL SCALE OF THE PROFILE IS EXAGGERATED.";
            try { ids.Add(TextNote.Create(doc, v.Id, at, Ft(0.20), t, new TextNoteOptions(tt) { HorizontalAlignment = HorizontalTextAlignment.Left }).Id); } catch { }
            return ids;
        }

        private static double Num(TendonProfile p, string key, double def) =>
            p.Data.TryGetValue(key, out string s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : def;
    }
}
