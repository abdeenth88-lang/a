// AB Bridges - the Excel workbook: Super Structure, Profile (+ under-bridge crossings),
// Parameters (Revit families) and Info.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ABBridges
{
    internal sealed class ReportData
    {
        public string BridgeName;
        public List<Section> Sections;
        public List<AxisInfo> Axes;
        public List<SpanInfo> Spans;
        public List<SpanLength> SpanLengths;
        public List<Crossing> Crossings;
        public List<(string, string)> Info;           // run info (Info sheet)
        public string DesignNames, NglNames;
        public List<string> Warnings;
        public BasePt? BasePoint;                      // Export Base Point (kept in the drawing)
        public List<AxisSection> AxisSections;         // design / NGL across every axis (sheet Substructure)
    }

    // one axis for the Substructure sheet
    internal sealed class AxisSection
    {
        public string Name;
        public double Station, SkewDeg;
        public double X, Y;                  // axis point on the alignment
        public double DirX, DirY;            // unit, left edge -> right edge
        public List<(string name, double off, double x, double y, double z)> Design = new List<(string, double, double, double, double)>();
        public List<(string name, double off, double x, double y, double z)> Ngl = new List<(string, double, double, double, double)>();
    }

    internal static class Report
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private const string Navy = "1F3A5F";
        private const string Ink = "1F2937";
        private const string Purple = "5B21B6";

        private static XStyle St(string fill = null, bool bold = false, string color = Ink, string align = null,
                                 string fmt = null, bool thickLeft = false, double size = 11)
            => new XStyle { Fill = fill, Bold = bold, Color = color, HAlign = align, NumFmt = fmt, ThickLeft = thickLeft, Size = size };

        public static void Write(string path, ReportData d)
        {
            var sheets = new List<XSheet>
            {
                SuperStructure(d),
                Profile(d),
                Substructure(d),
                Parameters(d),
                InfoSheet(d)
            };
            // sheets made in Revit (Tendon Profile) are kept when the Excel is generated again
            XSheet keep = XlsxKeep.Read(path, "Tendon Profile");
            if (keep != null) sheets.Add(keep);
            // free sheet for the tables that go to Revit (Import Excel); kept as it is when generated again
            XSheet toRevit = XlsxKeep.Read(path, "To Revit");
            if (toRevit == null)
            {
                toRevit = new XSheet("To Revit") { Landscape = false };
                toRevit.Set(1, 1, "Put here the tables for Revit. In Revit: Import Excel, select them (Ctrl for more than one), Done.", St(color: "808080"));
                toRevit.Rows[1][1].Style.Border = false;
                toRevit.Widths[1] = 14;
            }
            sheets.Add(toRevit);
            XlsxBook.Write(path, sheets);
        }

        // ---------------------------------------------------------------- common blocks

        private static List<(string, string)> TopInfo(ReportData d)
        {
            string axes = string.Join("   |   ", d.Axes.Select(a => a.Name + "  " + Fmt.Chainage(a.Station)));
            var l = new List<(string, string)> { ("Bridge", d.BridgeName), ("Design surface", d.DesignNames) };
            if (!string.IsNullOrEmpty(d.NglNames)) l.Add(("NGL surface", d.NglNames));
            l.Add(("Axes", axes));
            l.Add(("Units", "meters"));
            l.Add(("Created", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CI)));
            return l;
        }

        // title row + info block; returns the first free row (group row)
        private static int Head(XSheet sh, string title, int ncols, List<(string, string)> info)
        {
            sh.Merge(1, 1, 1, ncols, title, new XStyle { Fill = Navy, Bold = true, Color = "FFFFFF", Size = 16, VCenter = true, Indent = 1 });
            sh.Heights[1] = 30;
            int r = 2;
            foreach (var (k, v) in info)
            {
                sh.Merge(r, 1, r, 2, k, St(null, true, Navy));
                sh.Merge(r, 3, r, Math.Min(ncols, 10), v, St());
                r++;
            }
            return r + 1;
        }

        private sealed class Grp
        {
            public string Title;
            public string[] Cols;
            public string Fill;
            public Grp(string t, string f, params string[] c) { Title = t; Fill = f; Cols = c; }
        }

        // group row + header row; returns the start column of every group
        private static List<int> Groups(XSheet sh, int g, int startCol, params Grp[] groups)
        {
            var starts = new List<int>();
            int c = startCol;
            foreach (Grp gr in groups)
            {
                starts.Add(c);
                sh.Merge(g, c, g, c + gr.Cols.Length - 1, gr.Title, new XStyle { Fill = gr.Fill, Bold = true, Color = Navy, HAlign = "center" });
                for (int j = 0; j < gr.Cols.Length; j++)
                    sh.Set(g + 1, c + j, gr.Cols[j], new XStyle { Fill = Navy, Bold = true, Color = "FFFFFF", HAlign = "center", VCenter = true });
                c += gr.Cols.Length;
            }
            sh.Heights[g + 1] = 22;
            return starts;
        }

        private static (string fill, bool bold) RowFill(Section s, int i)
        {
            switch (s.Kind)
            {
                case SectionKind.Axis: return ("FFF2A8", true);
                case SectionKind.DiaphragmFace: return ("FDE9D9", false);
                case SectionKind.Corbel: return ("D9F2E3", false);
                case SectionKind.ConstructionJoint: return ("E9DFFB", true);
                case SectionKind.NearDiaphragm: return ("EEF2F7", false);
                default: return (i % 2 == 0 ? "FFFFFF" : "F7F9FC", false);
            }
        }

        // writes one value with the row style; numbers get 3 decimals
        private static void Put(XSheet sh, int r, int c, object v, string fill, bool bold, bool thickLeft, bool center, string color = Ink, string fmt = "0.000")
        {
            bool num = v is double || v is XFormula;
            var st = new XStyle
            {
                Fill = fill,
                Bold = bold,
                Color = color,
                ThickLeft = thickLeft,
                NumFmt = (v is double || v is XFormula) ? fmt : null,
                HAlign = num ? "right" : (center ? "center" : null)
            };
            sh.Set(r, c, v, st);
        }

        private static object D(double v) => double.IsNaN(v) ? null : (object)v;

        // ---------------------------------------------------------------- Super Structure

        private static XSheet SuperStructure(ReportData d)
        {
            var sh = new XSheet("Super Structure");
            int g = Head(sh, "AB Bridges  —  Super Structure", 15, TopInfo(d));
            List<int> starts = Groups(sh, g, 1,
                new Grp("SECTION", "DCE6F1", "No", "Remark", "Type", "Chainage", "Station", "Skew (°)"),
                new Grp("LEFT EDGE", "E2EFDA", "X", "Y", "Z", "Z source"),
                new Grp("RIGHT EDGE", "FCE4D6", "X", "Y", "Z", "Z source"),
                new Grp("", "DCE6F1", "Width"));

            int r = g + 2;
            for (int i = 0; i < d.Sections.Count; i++, r++)
            {
                Section s = d.Sections[i];
                var (fill, bold) = RowFill(s, i);
                string col = s.Kind == SectionKind.ConstructionJoint ? Purple : Ink;
                object[] vals =
                {
                    (double)(i + 1), s.Remark, s.TypeName, Fmt.Chainage(s.Station), s.Station, s.SkewDeg,
                    s.HasEdges ? D(s.Left.X) : null, s.HasEdges ? D(s.Left.Y) : null, D(s.LeftZ), s.LeftZSource,
                    s.HasEdges ? D(s.Right.X) : null, s.HasEdges ? D(s.Right.Y) : null, D(s.RightZ), s.RightZSource,
                    D(s.Width)
                };
                for (int j = 0; j < vals.Length; j++)
                {
                    int c = j + 1;
                    object v = vals[j];
                    string fmt = c == 1 ? "0" : "0.000";
                    Put(sh, r, c, v, fill, bold, starts.Contains(c), c == 3 || c == 4 || c == 10 || c == 14, col, fmt);
                }
            }
            Widths(sh, 5, 17, 18, 11, 10, 9, 13, 13, 9, 26, 13, 13, 9, 26, 8);

            // base point (Export Base Point): own block at the top right, read by Revit (Import Base Point / Create Deck)
            if (d.BasePoint.HasValue)
            {
                int bc = BasePointXlsx.Col, br = BasePointXlsx.Row;
                sh.Merge(br, bc, br, bc + 1, BasePointXlsx.Title, new XStyle { Fill = "0B6E4F", Bold = true, Color = "FFFFFF", HAlign = "center" });
                string[] k = { "E", "N", "Z" };
                double[] v = { d.BasePoint.Value.E, d.BasePoint.Value.N, d.BasePoint.Value.Z };
                for (int i = 0; i < 3; i++)
                {
                    sh.Set(br + 1 + i, bc, k[i], St("E2F0D9", true, Navy, "center"));
                    sh.Set(br + 1 + i, bc + 1, v[i], new XStyle { Fill = "FFFFFF", Bold = true, Color = Ink, NumFmt = "0.000", HAlign = "right" });
                }
                sh.Widths[bc] = 8;
                sh.Widths[bc + 1] = 14;
            }
            return sh;
        }

        // ---------------------------------------------------------------- Profile

        private static XSheet Profile(ReportData d)
        {
            var sh = new XSheet("Profile");
            int g = Head(sh, "AB Bridges  —  Profile (at alignment)", 11, TopInfo(d));
            List<int> starts = Groups(sh, g, 1,
                new Grp("SECTION", "DCE6F1", "No", "Remark", "Type", "Chainage", "Station"),
                new Grp("ALIGNMENT", "EDE7F6", "X", "Y"),
                new Grp("ROAD", "E2EFDA", "Z", "Z source"),
                new Grp("NGL", "FCE4D6", "Z"),
                new Grp("ROAD − NGL", "DCE6F1", "Height"));

            int r = g + 2;
            for (int i = 0; i < d.Sections.Count; i++, r++)
            {
                Section s = d.Sections[i];
                var (fill, bold) = RowFill(s, i);
                string col = s.Kind == SectionKind.ConstructionJoint ? Purple : Ink;
                double h = double.IsNaN(s.CenterZ) || double.IsNaN(s.NglZ) ? double.NaN : s.CenterZ - s.NglZ;
                object[] vals =
                {
                    (double)(i + 1), s.Remark, s.TypeName, Fmt.Chainage(s.Station), s.Station,
                    s.ClPoint.X, s.ClPoint.Y, D(s.CenterZ), s.CenterZSource, D(s.NglZ), D(h)
                };
                for (int j = 0; j < vals.Length; j++)
                {
                    int c = j + 1;
                    Put(sh, r, c, vals[j], fill, bold, starts.Contains(c), c == 3 || c == 4 || c == 9, col, c == 1 ? "0" : "0.000");
                }
            }

            CrossingTable(sh, g, 13, d.Crossings);
            Widths(sh, 5, 17, 18, 11, 10, 13, 13, 9, 13, 9, 10, 3, 10, 11, 13, 13, 9, 9, 9, 10);
            return sh;
        }

        private static readonly string[] RoadColors = { "E2EFDA", "FFE8CC", "E8E2F4", "DDEBF7", "FDE2E2", "EDEDED" };

        private static void CrossingTable(XSheet sh, int g, int c0, List<Crossing> crossings)
        {
            if (crossings == null || crossings.Count == 0) return;
            string[] cols = { "Point", "Station", "X", "Y", "Road Z", "Bridge Z", "Depth", "Clearance" };
            sh.Merge(g, c0, g, c0 + cols.Length - 1, "UNDER-BRIDGE CROSSINGS", new XStyle { Fill = "DCE6F1", Bold = true, Color = Navy, HAlign = "center" });
            for (int j = 0; j < cols.Length; j++)
                sh.Set(g + 1, c0 + j, cols[j], new XStyle { Fill = Navy, Bold = true, Color = "FFFFFF", HAlign = "center", VCenter = true });

            string cDepth = XSheet.Col(c0 + 6), cClr = XSheet.Col(c0 + 7), cBz = XSheet.Col(c0 + 5), cRz = XSheet.Col(c0 + 4);
            int r = g + 2;
            var heads = new List<int>();
            int ci = 0;
            foreach (Crossing cr in crossings)
            {
                string color = RoadColors[ci++ % RoadColors.Length];
                int hr = r;
                sh.Merge(r, c0, r, c0 + 4, $"{cr.Road}   ({Fmt.Chainage(cr.From)} → {Fmt.Chainage(cr.To)})", St(color, true, Navy));
                sh.Set(r, c0 + 5, "Depth ›", St(color, true, Navy, "right"));
                sh.Set(r, c0 + 6, cr.Depth, new XStyle { Fill = "FFFFFF", Bold = true, Color = "0050BE", NumFmt = "0.000", HAlign = "right", BorderColor = "0050BE" });
                r++;
                int first = r;
                for (int k = 0; k < cr.Points.Count; k++, r++)
                {
                    CrossPoint p = cr.Points[k];
                    string fl = k % 2 == 0 ? "FFFFFF" : "F7F9FC";
                    Put(sh, r, c0, p.Name, fl, false, true, true);
                    Put(sh, r, c0 + 1, Fmt.Chainage(p.Station), fl, false, false, true);
                    Put(sh, r, c0 + 2, p.P.X, fl, false, false, false);
                    Put(sh, r, c0 + 3, p.P.Y, fl, false, false, false);
                    Put(sh, r, c0 + 4, D(p.RoadZ), fl, false, false, false);
                    Put(sh, r, c0 + 5, D(p.BridgeZ), fl, false, false, false);
                    Put(sh, r, c0 + 6, new XFormula($"${cDepth}${hr}"), fl, false, false, false, "0050BE");
                    Put(sh, r, c0 + 7, new XFormula($"IF(OR({cBz}{r}=\"\",{cRz}{r}=\"\"),\"\",{cBz}{r}-{cRz}{r}-{cDepth}{r})"), fl, true, false, false);
                }
                sh.Set(hr, c0 + 7, new XFormula($"MIN({cClr}{first}:{cClr}{r - 1})"),
                       new XStyle { Fill = color, Bold = true, Color = "BE1E1E", NumFmt = "0.000", HAlign = "right" });
                heads.Add(hr);
                r++;
            }
            sh.Merge(r, c0, r, c0 + 6, "Bridge min clearance", St(null, true, Navy));
            sh.Set(r, c0 + 7, new XFormula("MIN(" + string.Join(",", heads.Select(h => cClr + h.ToString(CI))) + ")"),
                   new XStyle { Bold = true, Size = 12, Color = "BE1E1E", NumFmt = "0.000", HAlign = "right" });
        }

        // ---------------------------------------------------------------- Parameters

        private static XSheet Parameters(ReportData d)
        {
            var sh = new XSheet("Parameters");
            int g = Head(sh, "AB Bridges  —  Family Parameters (filled from Revit)", 15,
                new List<(string, string)> { ("Bridge", d.BridgeName), ("Note", "Family names and parameters are written by AB Bridges for Revit. More families can be added from Revit (new groups on the right).") });
            Groups(sh, g, 1,
                new Grp("STATIONS", "DCE6F1", "No", "Remark", "Type", "Chainage", "Station"),
                new Grp("FAMILY 1  (name from Revit)", "E2EFDA", "Point", "parameters from Revit →"),
                new Grp("FAMILY 2  (name from Revit)", "D9F2E3", "Instance", "Pt", "parameters from Revit →"),
                new Grp("FAMILY 3  (name from Revit)", "FCE4D6", "Instance", "Pt", "parameters from Revit →"));

            // corbel instances: corbel line (Pt 1) + diaphragm face (Pt 2) at each end
            var corbel = new Dictionary<Section, (string, int)>();
            List<Section> secs = d.Sections;
            Section cs = secs.FirstOrDefault(s => s.Remark == "Corbel Start");
            Section ce = secs.FirstOrDefault(s => s.Remark == "Corbel End");
            if (d.Axes.Count >= 2)
            {
                AxisInfo a1 = d.Axes[0], a2 = d.Axes[d.Axes.Count - 1];
                Section f1 = secs.FirstOrDefault(s => s.Remark == a1.Name + " Before") ?? secs.FirstOrDefault(s => s.Remark == a1.Name && s.Kind == SectionKind.Axis);
                Section f2 = secs.FirstOrDefault(s => s.Remark == a2.Name + " After") ?? secs.FirstOrDefault(s => s.Remark == a2.Name && s.Kind == SectionKind.Axis);
                if (cs != null && f1 != null) { corbel[cs] = ("CB-Start", 1); corbel[f1] = ("CB-Start", 2); }
                if (ce != null && f2 != null) { corbel[f2] = ("CB-End", 2); corbel[ce] = ("CB-End", 1); }
            }
            // outrigger instances: before face, axis, after face of every axis
            var outr = new Dictionary<Section, (string, int)>();
            foreach (AxisInfo a in d.Axes)
            {
                Section ax = secs.FirstOrDefault(s => s.Remark == a.Name && s.Kind == SectionKind.Axis);
                if (ax == null) continue;
                Section b = secs.FirstOrDefault(s => s.Remark == a.Name + " Before") ?? ax;
                Section f = secs.FirstOrDefault(s => s.Remark == a.Name + " After") ?? ax;
                outr[b] = ("OR-" + a.Name, 1);
                if (!outr.ContainsKey(ax)) outr[ax] = ("OR-" + a.Name, 2);
                if (!outr.ContainsKey(f)) outr[f] = ("OR-" + a.Name, 3);
            }

            int r = g + 2;
            for (int i = 0; i < secs.Count; i++, r++)
            {
                Section s = secs[i];
                var (fill, bold) = RowFill(s, i);
                string col = s.Kind == SectionKind.ConstructionJoint ? Purple : Ink;
                object[] vals = { (double)(i + 1), s.Remark, s.TypeName, Fmt.Chainage(s.Station), s.Station };
                for (int j = 0; j < vals.Length; j++)
                    Put(sh, r, j + 1, vals[j], fill, bold, j == 0, j == 2 || j == 3, col, j == 0 ? "0" : "0.000");

                Put(sh, r, 6, (double)(i + 1), "FFFFFF", false, true, true, Ink, "0");
                Put(sh, r, 7, null, "F2F7FF", false, false, false);

                Instance(sh, r, 8, corbel.TryGetValue(s, out var cb) ? cb : ((string, int)?)null);
                Instance(sh, r, 11, outr.TryGetValue(s, out var orr) ? orr : ((string, int)?)null);
            }
            Widths(sh, 5, 17, 18, 11, 10, 7, 24, 10, 5, 24, 10, 5, 24);
            return sh;
        }

        // instance name + point number (which instances are built is chosen in Revit's Create Deck dialog)
        private static void Instance(XSheet sh, int r, int c, (string name, int pt)? inst)
        {
            if (inst == null)
            {
                for (int j = 0; j < 3; j++) Put(sh, r, c + j, null, "EEEEEE", false, j == 0, false);
                return;
            }
            Put(sh, r, c, inst.Value.name, "FFFFFF", false, true, true);
            Put(sh, r, c + 1, (double)inst.Value.pt, "FFFFFF", false, false, true, Ink, "0");
            Put(sh, r, c + 2, null, "F2F7FF", false, false, false);
        }

        // ---------------------------------------------------------------- Substructure

        // one block per axis: axis data, the design surface at 4 points and the NGL every <= 1 m across the axis.
        // Revit (Substructure) reads the blocks by the labels in column A: AXIS, Station, Skew (°), Point X, Point Y,
        // Dir X, Dir Y, DESIGN SURFACE, NGL.
        private static XSheet Substructure(ReportData d)
        {
            var sh = new XSheet("Substructure");
            int r = Head(sh, "AB Bridges  —  Substructure (across every axis)", 5, new List<(string, string)>
            {
                ("Bridge", d.BridgeName),
                ("Design surface", d.DesignNames),
                ("NGL surface", string.IsNullOrEmpty(d.NglNames) ? "—  (pick an NGL surface in Extract Data)" : d.NglNames),
                ("Offset", "+ right of the alignment, along the axis line (skew included); Units meters")
            });
            if (d.AxisSections == null || d.AxisSections.Count == 0)
            {
                sh.Merge(r, 1, r, 5, "No axis with both edges found.", St("FFFFFF"));
                Widths(sh, 16, 12, 14, 14, 10);
                return sh;
            }
            foreach (AxisSection a in d.AxisSections)
            {
                sh.Merge(r, 1, r, 5, $"{a.Name}   ·   St {Fmt.Chainage(a.Station)}   ·   skew {a.SkewDeg.ToString("0.000", CI)}°",
                         new XStyle { Fill = Navy, Bold = true, Color = "FFFFFF", Size = 12, Indent = 1 });
                r++;
                (string, object)[] kv =
                {
                    ("AXIS", a.Name), ("Station", a.Station), ("Skew (°)", a.SkewDeg),
                    ("Point X", a.X), ("Point Y", a.Y), ("Dir X", a.DirX), ("Dir Y", a.DirY)
                };
                foreach (var (k, v) in kv)
                {
                    Put(sh, r, 1, k, "EEF2F7", true, true, false, Navy);
                    Put(sh, r, 2, v, "FFFFFF", false, false, true, Ink, k.StartsWith("Dir") ? "0.000000" : "0.000");
                    r++;
                }
                r = PointBlock(sh, r, "DESIGN SURFACE", "DDEBF7", "0C447C", a.Design);
                r = PointBlock(sh, r, "NGL", "E2EFDA", "27500A", a.Ngl);
                r++;
            }
            Widths(sh, 16, 12, 14, 14, 10);
            return sh;
        }

        private static int PointBlock(XSheet sh, int r, string title, string fill, string color, List<(string name, double off, double x, double y, double z)> pts)
        {
            sh.Merge(r, 1, r, 5, title, new XStyle { Fill = fill, Bold = true, Color = color });
            r++;
            string[] h = { "Point", "Offset", "X", "Y", "Z" };
            for (int j = 0; j < h.Length; j++)
                sh.Set(r, j + 1, h[j], new XStyle { Fill = Navy, Bold = true, Color = "FFFFFF", HAlign = "center" });
            r++;
            for (int k = 0; k < pts.Count; k++, r++)
            {
                var p = pts[k];
                string fl = k % 2 == 0 ? "FFFFFF" : "F7F9FC";
                Put(sh, r, 1, p.name, fl, false, true, true);
                Put(sh, r, 2, p.off, fl, false, false, false);
                Put(sh, r, 3, p.x, fl, false, false, false);
                Put(sh, r, 4, p.y, fl, false, false, false);
                Put(sh, r, 5, D(p.z), fl, false, false, false);
            }
            return r;
        }

        // ---------------------------------------------------------------- Info

        private static XSheet InfoSheet(ReportData d)
        {
            var sh = new XSheet("Info");
            int g = Head(sh, "AB Bridges  —  Run Info", 8, d.Info);

            Groups(sh, g, 1, new Grp("BEARING AXES", "DCE6F1", "Axis", "Chainage", "Skew (°)", "Diaphragm", "Before D1", "Before D2", "After D1", "After D2"));
            int r = g + 2;
            int n = d.Axes.Count;
            foreach (AxisInfo a in d.Axes)
            {
                bool first = a.Index == 0, last = a.Index == n - 1;
                Put(sh, r, 1, a.Name, "FFF2A8", true, true, true);
                Put(sh, r, 2, Fmt.Chainage(a.Station), "FFF2A8", false, false, true);
                Put(sh, r, 3, a.SkewDeg, "FFFFFF", false, false, false);
                Put(sh, r, 4, a.Diaphragm, "FFFFFF", false, false, false);
                Put(sh, r, 5, first ? (object)"—" : a.BeforeD1, "FFFFFF", false, false, true);
                Put(sh, r, 6, first ? (object)"—" : a.BeforeD2, "FFFFFF", false, false, true);
                Put(sh, r, 7, last ? (object)"—" : a.AfterD1, "FFFFFF", false, false, true);
                Put(sh, r, 8, last ? (object)"—" : a.AfterD2, "FFFFFF", false, false, true);
                r++;
            }

            // construction joints
            var used = d.Spans.Where(s => !double.IsNaN(s.CJ1)).ToList();
            if (used.Count > 0)
            {
                r++;
                Groups(sh, r, 1, new Grp("CONSTRUCTION JOINTS", "E9DFFB", "Span", "Length", "Factor", "Dist 1", "Dist 2", "CJ1", "CJ2", "CJ3"));
                r += 2;
                foreach (SpanInfo s in used)
                {
                    CjSpan cj = BridgeSettings.CjOf(s.Key);
                    SpanLength sl = d.SpanLengths?.FirstOrDefault(x => x.Key == s.Key);
                    string lenNote = cj.UserLength.HasValue ? " (typed)" : (sl != null && sl.FromCenterline ? " (centerline)" : " (auto)");
                    Put(sh, r, 1, s.A.Name + " → " + s.B.Name + lenNote, "FFFFFF", false, true, true);
                    Put(sh, r, 2, cj.Length, "FFFFFF", false, false, false);
                    Put(sh, r, 3, cj.Factor, "FFFFFF", false, false, false);
                    Put(sh, r, 4, cj.Dist1, "FFFFFF", false, false, false);
                    Put(sh, r, 5, cj.Dist2, "FFFFFF", false, false, false);
                    Put(sh, r, 6, Fmt.Chainage(s.CJ1), "FFFFFF", true, false, true, Purple);
                    Put(sh, r, 7, Fmt.Chainage(s.CJ2), "FFFFFF", true, false, true, Purple);
                    Put(sh, r, 8, Fmt.Chainage(s.CJ3), "FFFFFF", true, false, true, Purple);
                    r++;
                }
            }

            // span lengths
            if (d.SpanLengths != null && d.SpanLengths.Count > 0)
            {
                r++;
                Groups(sh, r, 1, new Grp("SPAN LENGTHS", "DCE6F1", "Span", "Length", "Method"));
                r += 2;
                foreach (SpanLength sl in d.SpanLengths)
                {
                    Put(sh, r, 1, sl.Name, "FFFFFF", false, true, true);
                    Put(sh, r, 2, D(sl.Length), "FFFFFF", false, false, false);
                    Put(sh, r, 3, sl.FromCenterline ? "Centerline" + (string.IsNullOrEmpty(sl.Note) ? "" : " - " + sl.Note) : "Auto (center between edges)", "FFFFFF", false, false, false);
                    r++;
                }
            }

            // warnings
            r++;
            sh.Merge(r, 1, r, 8, "WARNINGS", new XStyle { Fill = "FDE2E2", Bold = true, Color = Navy, HAlign = "center" });
            r++;
            if (d.Warnings.Count == 0)
            {
                sh.Merge(r, 1, r, 8, "None", St("FFFFFF"));
            }
            foreach (string w in d.Warnings)
            {
                sh.Merge(r, 1, r, 8, w, St("FFFFFF"));
                r++;
            }
            Widths(sh, 14, 14, 10, 11, 10, 10, 12, 12);
            return sh;
        }

        private static void Widths(XSheet sh, params double[] w)
        {
            for (int i = 0; i < w.Length; i++) sh.Widths[i + 1] = w[i];
        }
    }
}
