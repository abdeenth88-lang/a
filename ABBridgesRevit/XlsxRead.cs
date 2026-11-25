// AB Bridges for Revit - reads the bridge Excel: points (Super Structure) and family parameters (Parameters).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ABBridgesRevit
{
    internal sealed class StationRow
    {
        public int No;
        public string Remark;
        public string Type;                        // Axis / Diaphragm face / ...
        public double LX, LY, LZ, RX, RY, RZ;     // meters (shared coordinates)
        public bool HasPoints;
        public double Sta = double.NaN;            // station (m), column 5
    }

    internal sealed class ParamColumn
    {
        public string Header;   // e.g. "Th_w (m)"
        public string Name;     // e.g. "Th_w"
        public string Unit;     // "m", "°", "m²", "m³", "%", ""
        public bool IsType;
        public int Col;
    }

    internal sealed class FamilyGroup
    {
        public int Number;              // Family 1, 2, ...
        public string FamilyName;
        public bool PerInstance;        // corbel / outrigger: one instance per corbel / per diaphragm
        public string Kind = "Deck";    // Type above the family name: Deck / Corbel / Outrigger
        public bool IsDeckKind => !PerInstance && Kind == "Deck";
        public int Start, End, Fixed;
        public List<ParamColumn> Params = new List<ParamColumn>();
        // instance name -> station rows (in Pt order); Family 1 / extra groups: one instance "ALL" with every station
        public List<(string name, bool place, List<int> nos)> Instances = new List<(string, bool, List<int>)>();
        public Dictionary<int, Dictionary<string, string>> Values = new Dictionary<int, Dictionary<string, string>>();   // No -> param name -> text
    }

    // one axis of the Substructure sheet
    internal sealed class AxisData
    {
        public string Name;
        public double Station, SkewDeg;
        public double X, Y;                 // axis point on the alignment (shared, m)
        public double DirX, DirY;           // unit, left edge -> right edge
        public List<(string name, double off, double z)> Design = new List<(string, double, double)>();
        public List<(string name, double off, double z)> Ngl = new List<(string, double, double)>();

        // level of a surface at an offset (linear between the points; ends held flat)
        public static double ZAt(List<(string name, double off, double z)> pts, double off)
        {
            var p = pts.Where(x => !double.IsNaN(x.z)).OrderBy(x => x.off).ToList();
            if (p.Count == 0) return double.NaN;
            if (off <= p[0].off) return p[0].z;
            if (off >= p[p.Count - 1].off) return p[p.Count - 1].z;
            for (int i = 0; i + 1 < p.Count; i++)
                if (off <= p[i + 1].off)
                {
                    double t = (off - p[i].off) / Math.Max(1e-9, p[i + 1].off - p[i].off);
                    return p[i].z + (p[i + 1].z - p[i].z) * t;
                }
            return p[p.Count - 1].z;
        }
    }

    internal sealed class BridgeExcel
    {
        public string BridgeName = "Bridge";
        public string FilePath;
        public (double E, double N, double Z)? BasePoint;          // Export Base Point (Super Structure, top right)
        public List<AxisData> Axes = new List<AxisData>();         // Substructure sheet (empty if the sheet is missing)
        public Dictionary<int, StationRow> Stations = new Dictionary<int, StationRow>();
        public List<FamilyGroup> Groups = new List<FamilyGroup>();
        public List<(string road, double sta, double z)> Roads = new List<(string, double, double)>();   // roads under the bridge (FRL)

        private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        private sealed class Grid
        {
            public Dictionary<(int r, int c), string> Cells = new Dictionary<(int, int), string>();
            public List<(int r1, int c1, int r2, int c2)> Merges = new List<(int, int, int, int)>();
            public int MaxRow;
            public string T(int r, int c) => Cells.TryGetValue((r, c), out string v) ? v : "";
            public double N(int r, int c) => double.TryParse(T(r, c), NumberStyles.Float, CI, out double v) ? v : double.NaN;
        }

        // needParams = false: the Parameters sheet may be missing (Substructure needs only Super Structure + Substructure)
        public static BridgeExcel Load(string path, bool needParams = true)
        {
            var bx = new BridgeExcel { FilePath = path };
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                List<string> shared = SharedStrings(zip);
                Grid ss = ReadSheet(zip, "Super Structure", shared) ?? throw new InvalidOperationException("The Excel has no \"Super Structure\" sheet.");
                Grid ps = ReadSheet(zip, "Parameters", shared);
                if (ps == null && needParams) throw new InvalidOperationException("The Excel has no \"Parameters\" sheet.");
                bx.ReadPoints(ss);
                bx.ReadBasePoint(ss);
                bx.ReadRoads(ss);
                if (ps != null) bx.ReadGroups(ps);
                Grid sub = ReadSheet(zip, "Substructure", shared);
                if (sub != null) bx.ReadAxes(sub);
            }
            return bx;
        }

        // only the base point of the Super Structure sheet (Import Base Point)
        public static (double E, double N, double Z)? LoadBasePoint(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                Grid ss = ReadSheet(zip, "Super Structure", SharedStrings(zip)) ?? throw new InvalidOperationException("The Excel has no \"Super Structure\" sheet.");
                var bx = new BridgeExcel { FilePath = path };
                bx.ReadBasePoint(ss);
                return bx.BasePoint;
            }
        }

        // ---------------------------------------------------------------- Super Structure

        private void ReadPoints(Grid g)
        {
            for (int r = 1; r <= g.MaxRow; r++)
                if (g.T(r, 1).Trim() == "Bridge") { BridgeName = g.T(r, 3).Trim(); break; }

            int h = -1;
            for (int r = 1; r <= g.MaxRow; r++)
                if (g.T(r, 1).Trim() == "No" && g.T(r, 2).Trim() == "Remark") { h = r; break; }
            if (h < 0) throw new InvalidOperationException("Could not find the header row (No, Remark ...) in Super Structure.");

            // LEFT EDGE X Y Z = columns 7-9, RIGHT EDGE X Y Z = 11-13 (as made by AB Bridges)
            for (int r = h + 1; r <= g.MaxRow; r++)
            {
                double no = g.N(r, 1);
                if (double.IsNaN(no)) continue;
                var s = new StationRow
                {
                    No = (int)Math.Round(no),
                    Remark = g.T(r, 2),
                    Type = g.T(r, 3).Trim(),
                    LX = g.N(r, 7), LY = g.N(r, 8), LZ = g.N(r, 9),
                    RX = g.N(r, 11), RY = g.N(r, 12), RZ = g.N(r, 13),
                    Sta = g.N(r, 5)
                };
                s.HasPoints = !(double.IsNaN(s.LX) || double.IsNaN(s.LY) || double.IsNaN(s.LZ) || double.IsNaN(s.RX) || double.IsNaN(s.RY) || double.IsNaN(s.RZ));
                Stations[s.No] = s;
            }
        }

        // the base point block: a cell "BASE POINT ..." with E / N / Z below it (values one column to the right)
        private void ReadBasePoint(Grid g)
        {
            foreach (var kv in g.Cells)
            {
                if (!kv.Value.Trim().StartsWith("BASE POINT", StringComparison.OrdinalIgnoreCase)) continue;
                int r = kv.Key.r, c = kv.Key.c;
                double e = double.NaN, n = double.NaN, z = double.NaN;
                for (int k = 1; k <= 4; k++)
                {
                    string lab = g.T(r + k, c).Trim().ToUpperInvariant();
                    double v = g.N(r + k, c + 1);
                    if (lab == "E") e = v; else if (lab == "N") n = v; else if (lab == "Z") z = v;
                }
                if (!double.IsNaN(e) && !double.IsNaN(n)) BasePoint = (e, n, double.IsNaN(z) ? 0 : z);
                return;
            }
        }

        // ---------------------------------------------------------------- Substructure

        // the "roads under the bridge" table of the Super Structure sheet: a road name row, then Point | Station | X | Y | Road Z ...
        private void ReadRoads(Grid g)
        {
            int cSta = -1, cZ = -1, c0 = -1;
            for (int r = 1; r <= g.MaxRow && cSta < 0; r++)
                for (int c = 10; c <= 30; c++)
                    if (g.T(r, c).Trim() == "Station" && g.T(r, c + 3).Trim() == "Road Z") { cSta = c; cZ = c + 3; c0 = c - 1; break; }
            if (cSta < 0) return;
            string road = "Road";
            for (int r = 1; r <= g.MaxRow; r++)
            {
                double st = g.N(r, cSta), z = g.N(r, cZ);
                if (!double.IsNaN(st) && !double.IsNaN(z)) { Roads.Add((road, st, z)); continue; }
                string t = g.T(r, c0).Trim();
                if (t.Length > 0 && t != "Point" && double.IsNaN(g.N(r, c0))) road = t.Split(new[] { "  (" }, StringSplitOptions.None)[0].Trim();
            }
        }

        private void ReadAxes(Grid g)
        {
            AxisData a = null;
            List<(string, double, double)> into = null;
            for (int r = 1; r <= g.MaxRow; r++)
            {
                string k = g.T(r, 1).Trim();
                if (k == "AXIS") { a = new AxisData { Name = g.T(r, 2).Trim() }; Axes.Add(a); into = null; continue; }
                if (a == null) continue;
                switch (k)
                {
                    case "Station": a.Station = g.N(r, 2); continue;
                    case "Skew (°)": a.SkewDeg = g.N(r, 2); continue;
                    case "Point X": a.X = g.N(r, 2); continue;
                    case "Point Y": a.Y = g.N(r, 2); continue;
                    case "Dir X": a.DirX = g.N(r, 2); continue;
                    case "Dir Y": a.DirY = g.N(r, 2); continue;
                    case "DESIGN SURFACE": into = a.Design; continue;
                    case "NGL": into = a.Ngl; continue;
                    case "Point": continue;
                }
                if (into == null || k.Length == 0) continue;
                double off = g.N(r, 2), z = g.N(r, 5);
                if (!double.IsNaN(off)) into.Add((k, off, z));
            }
            Axes.RemoveAll(x => string.IsNullOrEmpty(x.Name) || double.IsNaN(x.X) || double.IsNaN(x.Y));
        }

        // ---------------------------------------------------------------- Parameters

        private static readonly Regex HeadRx = new Regex(@"^(?<n>.*?)(\s*\((?<u>[^()]*)\))?(\s*\[Type\])?\s*$");

        private void ReadGroups(Grid g)
        {
            int gr = -1;
            for (int r = 1; r <= g.MaxRow; r++)
                if (g.T(r, 1).Trim().Equals("STATIONS", StringComparison.OrdinalIgnoreCase)) { gr = r; break; }
            if (gr < 0) throw new InvalidOperationException("Could not find the STATIONS header in the Parameters sheet.");

            var spans = g.Merges.Where(m => m.r1 == gr && m.r2 == gr && !g.T(gr, m.c1).Trim().StartsWith("STATIONS", StringComparison.OrdinalIgnoreCase))
                                .OrderBy(m => m.c1).ToList();
            int number = 0;
            foreach (var m in spans)
            {
                number++;
                var fg = new FamilyGroup { Number = number, Start = m.c1, End = m.c2 };
                string title = g.T(gr, m.c1);
                int dash = title.IndexOf('—');
                fg.FamilyName = dash >= 0 ? title.Substring(dash + 1).Trim() : "";
                int fixedCols = 0;
                for (int c = m.c1; c <= m.c2; c++)
                {
                    string hh = g.T(gr + 1, c).Trim();
                    if (hh == "Point" || hh == "Instance" || hh == "Pt" || hh == "Place") fixedCols++; else break;     // "Place": old files
                }
                fg.Fixed = Math.Max(1, fixedCols);
                fg.PerInstance = g.T(gr + 1, m.c1).Trim() == "Instance";       // old files: instances written by Civil
                string kind = gr > 1 ? g.T(gr - 1, m.c1).Trim() : "";
                bool byType = !fg.PerInstance && (kind.Equals("Corbel", StringComparison.OrdinalIgnoreCase) || kind.Equals("Outrigger", StringComparison.OrdinalIgnoreCase));
                if (kind.Equals("Corbel", StringComparison.OrdinalIgnoreCase)) fg.Kind = "Corbel";
                else if (kind.Equals("Outrigger", StringComparison.OrdinalIgnoreCase)) fg.Kind = "Outrigger";
                else if (fg.PerInstance) fg.Kind = "Instances";

                for (int c = m.c1 + fg.Fixed; c <= m.c2; c++)
                {
                    string hh = g.T(gr + 1, c).Trim();
                    if (hh.Length == 0 || hh.StartsWith("(no parameters") || hh.StartsWith("parameters from Revit")) continue;
                    if (hh == "variation" || hh == "V.value") continue;          // Start/Mid/End inputs of the column on their left
                    Match mm = HeadRx.Match(hh);
                    fg.Params.Add(new ParamColumn
                    {
                        Header = hh,
                        Name = mm.Groups["n"].Value.Trim(),
                        Unit = mm.Groups["u"].Success ? mm.Groups["u"].Value.Trim() : "",
                        IsType = hh.EndsWith("[Type]"),
                        Col = c
                    });
                }

                // rows
                var all = new List<int>();
                var types = new List<(int no, string type, string remark)>();
                var inst = new Dictionary<string, (bool place, SortedDictionary<int, int> pts)>();
                for (int r = gr + 2; r <= g.MaxRow; r++)
                {
                    double no = g.N(r, 1);
                    if (double.IsNaN(no)) continue;
                    int n = (int)Math.Round(no);
                    var vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ParamColumn pc in fg.Params) vals[pc.Name] = g.T(r, pc.Col);

                    if (fg.PerInstance)
                    {
                        string name = g.T(r, m.c1).Trim();
                        if (name.Length == 0) continue;
                        double pt = g.N(r, m.c1 + 1);
                        if (!inst.TryGetValue(name, out var e)) e = (true, new SortedDictionary<int, int>());
                        if (fg.Fixed >= 3 && g.T(gr + 1, m.c1 + 2).Trim() == "Place")      // old files only
                        {
                            string place = g.T(r, m.c1 + 2).Trim();
                            if (place == "☐") e.place = false;
                        }
                        e.pts[double.IsNaN(pt) ? e.pts.Count + 1 : (int)pt] = n;
                        inst[name] = e;
                        fg.Values[n] = vals;
                    }
                    else
                    {
                        all.Add(n);
                        types.Add((n, g.T(r, 3).Trim(), g.T(r, 2).Trim()));
                        fg.Values[n] = vals;
                    }
                }
                if (fg.PerInstance)
                    foreach (var kv in inst) fg.Instances.Add((kv.Key, kv.Value.place, kv.Value.pts.Values.ToList()));
                else if (byType)
                {
                    // Type Corbel   : each corbel station + the station next to it (inside the bridge)
                    // Type Outrigger: each axis + its two diaphragm faces
                    fg.PerInstance = true;
                    for (int i = 0; i < types.Count; i++)
                    {
                        if (fg.Kind == "Corbel" && types[i].type.Equals("Corbel", StringComparison.OrdinalIgnoreCase))
                        {
                            bool atStart = i + 1 < types.Count && !types[i + 1].type.Equals("Corbel", StringComparison.OrdinalIgnoreCase) && (i == 0 || i < types.Count / 2);
                            var nos = atStart ? new List<int> { types[i].no, types[i + 1].no }
                                              : (i > 0 ? new List<int> { types[i - 1].no, types[i].no } : new List<int> { types[i].no });
                            string name = atStart ? "CB-Start" : "CB-End";
                            if (fg.Instances.Any(x => x.name == name)) name += "-" + types[i].no.ToString(CI);
                            fg.Instances.Add((name, true, nos));
                        }
                        else if (fg.Kind == "Outrigger" && types[i].type.Equals("Axis", StringComparison.OrdinalIgnoreCase))
                        {
                            var nos = new List<int>();
                            if (i > 0 && types[i - 1].type.Equals("Diaphragm face", StringComparison.OrdinalIgnoreCase)) nos.Add(types[i - 1].no);
                            nos.Add(types[i].no);
                            if (i + 1 < types.Count && types[i + 1].type.Equals("Diaphragm face", StringComparison.OrdinalIgnoreCase)) nos.Add(types[i + 1].no);
                            string ax = types[i].remark.Length > 0 ? types[i].remark : types[i].no.ToString(CI);
                            fg.Instances.Add(("OR-" + ax, true, nos));
                        }
                    }
                }
                else
                    fg.Instances.Add(("ALL", true, all));
                Groups.Add(fg);
            }
        }

        // ---------------------------------------------------------------- xlsx helpers

        // all text of one sheet (row, column) -> value; null when the sheet is missing
        public static Dictionary<(int r, int c), string> ReadCells(string path, string sheet)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                return ReadSheet(zip, sheet, SharedStrings(zip))?.Cells;
        }

        private static Grid ReadSheet(ZipArchive zip, string name, List<string> shared)
        {
            XDocument wb = Load(zip, "xl/workbook.xml");
            XDocument rels = Load(zip, "xl/_rels/workbook.xml.rels");
            XElement sh = wb?.Descendants(M + "sheet").FirstOrDefault(e => string.Equals((string)e.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
            if (sh == null || rels == null) return null;
            string rid = (string)sh.Attribute(R + "id");
            XElement rel = rels.Descendants(PR + "Relationship").FirstOrDefault(e => (string)e.Attribute("Id") == rid);
            if (rel == null) return null;
            string target = ((string)rel.Attribute("Target")).Replace('\\', '/');
            string path = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
            XDocument doc = Load(zip, path);
            if (doc == null) return null;

            var g = new Grid();
            foreach (XElement c in doc.Descendants(M + "c"))
            {
                string rf = (string)c.Attribute("r");
                int col = ColOf(rf), row = RowOf(rf);
                if (col < 0) continue;
                string t = (string)c.Attribute("t");
                string v;
                if (t == "inlineStr") v = string.Concat(c.Descendants(M + "t").Select(x => x.Value));
                else
                {
                    v = (string)c.Element(M + "v") ?? "";
                    if (t == "s" && int.TryParse(v, NumberStyles.Integer, CI, out int i) && i >= 0 && i < shared.Count) v = shared[i];
                }
                g.Cells[(row, col)] = v;
                g.MaxRow = Math.Max(g.MaxRow, row);
            }
            foreach (XElement m in doc.Descendants(M + "mergeCell"))
            {
                string[] p = ((string)m.Attribute("ref") ?? "").Split(':');
                if (p.Length == 2) g.Merges.Add((RowOf(p[0]), ColOf(p[0]), RowOf(p[1]), ColOf(p[1])));
            }
            return g;
        }

        private static List<string> SharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            XDocument sst = Load(zip, "xl/sharedStrings.xml");
            if (sst == null) return list;
            foreach (XElement si in sst.Root.Elements(M + "si"))
                list.Add(string.Concat(si.Descendants(M + "t").Select(t => t.Value)));
            return list;
        }

        private static XDocument Load(ZipArchive zip, string name)
        {
            ZipArchiveEntry e = zip.GetEntry(name);
            if (e == null) return null;
            using (Stream s = e.Open()) return XDocument.Load(s);
        }

        private static readonly Regex RefRx = new Regex(@"^\$?([A-Z]+)\$?(\d+)$");

        private static int ColOf(string r)
        {
            Match m = RefRx.Match(r ?? "");
            if (!m.Success) return -1;
            int c = 0;
            foreach (char ch in m.Groups[1].Value) c = c * 26 + (ch - 'A' + 1);
            return c;
        }

        private static int RowOf(string r)
        {
            Match m = RefRx.Match(r ?? "");
            return m.Success ? int.Parse(m.Groups[2].Value, CI) : -1;
        }
    }
}
