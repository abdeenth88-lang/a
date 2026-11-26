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
        public List<string> Problems = new List<string>();                   // found while reading the sheet
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
            // the workbook open in Excel: what is on the screen is read (no need to save first)
            Dictionary<(int r, int c), string> cells = (IsLocked(path) ? XlLive.Cells(path, Name) : null) ?? BridgeExcel.ReadCells(path, Name);
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
                    if (x == SplitRow || DataRows.Contains(x, StringComparer.OrdinalIgnoreCase)) break;
                    if (x.Length == 0)
                    {
                        if (T(r, c + 1).Length > 0) set.Problems.Add($"Path {whole.Path}, row {r}: a Y without its X");
                        continue;                                       // an empty row between the anchors
                    }
                    if (!TendonGeo.IsAnchor(x) && g != null && double.IsNaN(g.StationOf(x)))
                        set.Problems.Add($"Path {whole.Path}, row {r}: \"{x}\" is not a place on the bridge (write it like 0.4L1 or 0.725L2)");
                    whole.Pts.Add((x, N(r, c + 1)));
                    if (x.Trim().Equals("Anchor2", StringComparison.OrdinalIgnoreCase)) { r++; break; }
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

        // rows of X that are left from a split that moved: not a 0.1 point, not an anchor, not a split now, and either
        // a split before (lastSplits) or a row with no Y typed. Per path.
        public static Dictionary<int, List<string>> Stale(TendonSet set, TendonGeo g, Func<int, string> lastSplits)
        {
            var res = new Dictionary<int, List<string>>();
            var grid = new HashSet<long>(DefaultX(g.Spans).Where(x => !TendonGeo.IsAnchor(x)).Select(x => Key(g.StationOf(x))));
            foreach (TendonProfile w in set.Whole)
            {
                var now = new HashSet<long>(SplitList(set, w.Path).Select(x => Key(g.StationOf(x))));
                var before = new HashSet<long>((lastSplits?.Invoke(w.Path) ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                               .Select(x => g.StationOf(x.Trim())).Where(st => !double.IsNaN(st)).Select(Key));
                var drop = w.Pts.Where(q => !TendonGeo.IsAnchor(q.x))
                                .Where(q => { double st = g.StationOf(q.x); if (double.IsNaN(st)) return false; long k = Key(st);
                                              return !grid.Contains(k) && !now.Contains(k); })
                                .Select(q => q.x).ToList();
                if (drop.Count > 0) res[w.Path] = drop;
            }
            return res;
        }

        private static long Key(double st) => (long)Math.Round(st * 1000);

        public static List<string> SplitList(TendonSet set, int path) =>
            set.Splits.TryGetValue(path, out string s) ? s.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList() : new List<string>();

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
        public static List<string> SplitList(string v) =>
            (v ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        // ---------------- the new sheet (button "Tendon Excel"): per path Anchor1, empty rows, Anchor2, then the data.
        // empty rows between the anchors = 10 x spans + the splits of that path (the user types every X and Y)
        public static void Template(string path, int spans, List<int> splitsPerPath)
        {
            var rows = new SortedDictionary<int, SortedDictionary<int, (string v, bool num, int style)>>();
            void Put(int r, int c, string v, int style, bool num = false)
            {
                if (!rows.TryGetValue(r, out var row)) rows[r] = row = new SortedDictionary<int, (string, bool, int)>();
                row[c] = (v, num, StylePlaceholder + style);
            }
            int paths = Math.Max(1, splitsPerPath.Count);
            Put(1, 1, "AB Bridges - Tendon Profile: type X and Y of every point between Anchor1 and Anchor2 (X like 0.4L1, 0.725L2); Y = top of box to tendon CG (m). The splits are chosen in Revit (Draw Tendon).", 1);
            Put(2, 1, "Number of paths", 2); Put(2, 2, paths.ToString(CI), 3, true);
            int col = 1;
            for (int p = 1; p <= paths; p++)
            {
                int n = 10 * spans + Math.Max(0, splitsPerPath[p - 1]);
                Put(5, col, $"Path {p}", 1); Put(5, col + 1, $"{splitsPerPath[p - 1]} split(s)", 1);
                Put(6, col, "X", 2); Put(6, col + 1, "Y-Tendon (m)", 2);
                int r = 7;
                Put(r, col, "Anchor1", 4); Put(r, col + 1, "", 3); r++;
                for (int k = 0; k < n; k++, r++) { Put(r, col, "", 3); Put(r, col + 1, "", 3); }
                Put(r, col, "Anchor2", 4); Put(r, col + 1, "", 3); r++;
                r++;
                for (int k = 0; k < DataRows.Length; k++, r++)
                {
                    Put(r, col, DataRows[k], 4);
                    Put(r, col + 1, DataDefaults[k], 3, double.TryParse(DataDefaults[k], NumberStyles.Float, CI, out _));
                }
                col += 3;
            }
            Output(path, rows, col);
        }

        // the splits chosen in Revit cut every path into its stages
        public static void ApplySplits(TendonSet set, TendonGeo g, Dictionary<int, string> splits)
        {
            set.Splits = new Dictionary<int, string>(splits);
            set.Profiles.Clear();
            foreach (TendonProfile w in set.Whole.Where(w => w.Path <= set.Paths))
                foreach (TendonProfile sp in Cut(w, splits.TryGetValue(w.Path, out string v) ? v : "", g)) set.Profiles.Add(sp);
            set.Stages = set.Profiles.Select(x => x.Stage).DefaultIfEmpty(1).Max();
        }

        // what stops the drawing: points without Y, a split that is not one of the X rows of its path, ...
        public static List<string> Check(TendonSet set, TendonGeo g, Dictionary<int, string> splits)
        {
            var res = new List<string>(set.Problems);
            for (int p = 1; p <= set.Paths; p++)
            {
                TendonProfile w = set.Whole.FirstOrDefault(x => x.Path == p);
                if (w == null) { res.Add($"Path {p}: no block in the sheet (press Tendon Excel again)"); continue; }
                var inner = w.Pts.Where(q => !TendonGeo.IsAnchor(q.x)).ToList();
                if (inner.Count < 2) { res.Add($"Path {p}: type its points (X and Y) between Anchor1 and Anchor2"); continue; }
                var noY = w.Pts.Where(q => double.IsNaN(q.y)).Select(q => q.x).ToList();
                if (noY.Count > 0) res.Add($"Path {p}: no Y at {string.Join(", ", noY.Take(6))}{(noY.Count > 6 ? " ..." : "")}");
                var dup = inner.Where(q => !double.IsNaN(g.StationOf(q.x))).GroupBy(q => Math.Round(g.StationOf(q.x), 4)).Where(x => x.Count() > 1).Select(x => x.First().x).ToList();
                if (dup.Count > 0) res.Add($"Path {p}: the same X twice ({string.Join(", ", dup)})");
                foreach (string sx in SplitList(splits.TryGetValue(p, out string v) ? v : ""))
                {
                    double st = g.StationOf(sx);
                    if (double.IsNaN(st)) res.Add($"Path {p}: the split \"{sx}\" is not a place on the bridge");
                    else if (!inner.Any(q => Math.Abs(g.StationOf(q.x) - st) < 1e-6)) res.Add($"Path {p}: the split {TendonGeo.Clean(sx)} is not one of its X rows");
                }
            }
            return res;
        }

        public static void Write(string path, int paths, int spans, TendonSet old, TendonGeo g = null, Dictionary<int, List<string>> drop = null)
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
                var pts = (was != null && was.Pts.Count > 0 ? was.Pts : DefaultX(spans).Select(x => (x: x, y: double.NaN))).ToList();
                if (drop != null && drop.TryGetValue(p, out var dl)) pts.RemoveAll(q => dl.Contains(q.x));      // the row of a split that moved
                // every split gets its own row in X (in its place along the path), Y empty to be typed
                if (g != null && old != null && old.Splits.TryGetValue(p, out string sps))
                    foreach (string sx in sps.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
                    {
                        double st = g.StationOf(sx);
                        if (double.IsNaN(st) || pts.Any(q => Math.Abs(g.StationOf(q.x) - st) < 1e-6)) continue;
                        int at = pts.FindIndex(q => !TendonGeo.IsAnchor(q.x) && g.StationOf(q.x) > st);
                        if (at < 0) at = Math.Max(0, pts.Count - 1);
                        pts.Insert(at, (TendonGeo.Clean(sx), double.NaN));
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

            Output(path, rows, col);
        }

        // the sheet into the file, or into the workbook open in Excel (then saved there)
        private static void Output(string path, SortedDictionary<int, SortedDictionary<int, (string v, bool num, int style)>> rows, int col)
        {
            if (IsLocked(path))
            {
                var cells = new List<(int r, int c, string v, bool num, int style)>();
                foreach (var row in rows) foreach (var cell in row.Value) cells.Add((row.Key, cell.Key, cell.Value.v, cell.Value.num, cell.Value.style - StylePlaceholder));
                if (XlLive.WriteSheet(path, Name, cells, col)) return;
                throw new IOException("The Excel is open and could not be written. Close it, then try again.");
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

        // true when Excel (or anything) has the file open
        public static bool IsLocked(string path)
        {
            try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        // the same fixes made straight in the workbook open in Excel (COM): new Path blocks (copied from Path 1, its look kept,
        // Y and the first data emptied) and a row for every missing split in its place (Y empty, yellow). Not saved: the user types and saves.
        // Returns what was done, or null when the open workbook could not be reached.
        public static string WriteLive(string path, TendonSet set, TendonGeo g, Dictionary<int, List<string>> drop = null)
        {
            dynamic wb;
            try { wb = System.Runtime.InteropServices.Marshal.BindToMoniker(path); }
            catch { return null; }
            if (wb == null) return null;
            dynamic ws = null;
            try { ws = wb.Worksheets[Name]; } catch { }
            if (ws == null) return null;
            const int Down = -4121;            // xlShiftDown
            const int Yellow = 0x00FFFF;       // BGR
            var done = new List<string>();

            // 1. splits: a row in X / Y of that path only (the data rows of the path move down with it)
            foreach (TendonProfile w in set.Whole)
            {
                int col = 1 + (w.Path - 1) * 3;
                var pts = w.Pts.ToList();
                // first the rows of a split that moved go (from the bottom, so the rows above keep their place)
                if (drop != null && drop.TryGetValue(w.Path, out var dl))
                    for (int i = pts.Count - 1; i >= 0; i--)
                        if (dl.Contains(pts[i].x))
                        {
                            int row = 7 + i;
                            ws.Range[ws.Cells[row, col], ws.Cells[row, col + 1]].Delete(-4162);     // xlShiftUp
                            done.Add($"Path {w.Path}: old split row {pts[i].x} removed");
                            pts.RemoveAt(i);
                        }
                if (!set.Splits.TryGetValue(w.Path, out string sps)) continue;
                foreach (string sx in sps.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
                {
                    double st = g.StationOf(sx);
                    if (double.IsNaN(st) || pts.Any(q => Math.Abs(g.StationOf(q.x) - st) < 1e-6)) continue;
                    int at = pts.FindIndex(q => !TendonGeo.IsAnchor(q.x) && g.StationOf(q.x) > st);
                    if (at < 0) at = Math.Max(0, pts.Count - 1);
                    int row = 7 + at;
                    ws.Range[ws.Cells[row, col], ws.Cells[row, col + 1]].Insert(Down);
                    ws.Cells[row, col].Value2 = TendonGeo.Clean(sx);
                    ws.Cells[row, col + 1].Value2 = "";
                    ws.Cells[row, col + 1].Interior.Color = Yellow;
                    pts.Insert(at, (sx, double.NaN));
                    done.Add($"Path {w.Path}: row {sx} added (row {row}) - type its Y");
                }
            }

            // 2. new paths up to B2: a copy of the Path 1 block, emptied
            TendonProfile p1 = set.Whole.FirstOrDefault(x => x.Path == 1);
            if (p1 != null)
            {
                // the real end of block 1 after the inserts: count its rows again from the sheet
                int last = 7;
                while (!string.IsNullOrEmpty(Convert.ToString(ws.Cells[last, 1].Value2))) last++;
                int xEnd = last - 1;                                   // last X row
                int blockEnd = xEnd + 2 + DataRows.Length;             // empty row, split row, data rows
                for (int p = 1; p <= set.Paths; p++)
                {
                    if (set.Whole.Any(x => x.Path == p)) continue;
                    int col = 1 + (p - 1) * 3;
                    ws.Range[ws.Cells[5, 1], ws.Cells[blockEnd, 2]].Copy(ws.Cells[5, col]);
                    ws.Columns[col].ColumnWidth = ws.Columns[1].ColumnWidth;
                    ws.Columns[col + 1].ColumnWidth = ws.Columns[2].ColumnWidth;
                    ws.Cells[5, col].Value2 = $"Path {p}";
                    for (int r = 7; r <= xEnd; r++) ws.Cells[r, col + 1].Value2 = "";
                    ws.Cells[xEnd + 2, col + 1].Value2 = g.Spans >= 2 ? "0.15L2" : "";
                    for (int k = 0; k < 4; k++) ws.Cells[xEnd + 3 + k, col + 1].Value2 = "";      // PJ, tendons, strands, area: to be typed
                    done.Add($"Path {p} added - type its Y and data");
                }
            }
            // 3. paths above B2 go (their block is cleared)
            foreach (TendonProfile w in set.Whole.Where(x => x.Path > set.Paths).ToList())
            {
                int col = 1 + (w.Path - 1) * 3;
                int lastRow = 7;
                try { lastRow = Math.Max(60, (int)ws.UsedRange.Row + (int)ws.UsedRange.Rows.Count + 2); } catch { lastRow = 200; }
                ws.Range[ws.Cells[4, col], ws.Cells[lastRow, col + 1]].Clear();
                done.Add($"Path {w.Path} removed (B2 = {set.Paths})");
            }
            try { wb.Activate(); ws.Activate(); wb.Application.Visible = true; } catch { }
            return string.Join("\n", done);
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
            using (var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false))) doc.Save(w, System.Xml.Linq.SaveOptions.DisableFormatting);
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

        // "0.725 L2", "0.725l2" -> "0.725L2"
        public static string Clean(string x) => Regex.Replace(x ?? "", @"\s+", "").Replace('l', 'L');

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
    internal sealed class TendonLoad
    {
        public TendonSet Set;
        public TendonGeo Geo;
        public string Message = "";
        public bool Ready;
    }

    internal sealed class TendonForm : Form
    {
        public string Excel;
        public TendonLoad Loaded;
        public string TitleBlock;
        public double VScale = 3;
        public bool Make3D = true;
        public int Stages, Paths;
        public const string NoFamily = "(no family - lines + text)";
        public string TableFamily = NoFamily;          // "Family : Type" or NoFamily
        public string TableFile;                       // a .rfa chosen with Load...

        public Dictionary<int, string> Splits = new Dictionary<int, string>();

        public TendonForm(string excel, Func<string, TendonLoad> prepare, List<string> titleBlocks, bool update, List<string> details = null, string lastTable = null, Dictionary<int, string> lastSplits = null)
        {
            Text = update ? "AB Bridges - Update Tendon" : "AB Bridges - Draw Tendon";
            Font = new System.Drawing.Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 680);
            BackColor = WinColor.White;
            Excel = excel;
            var remember = new Dictionary<int, string>(lastSplits ?? new Dictionary<int, string>());

            // the Excel (read only here: the sheet "Tendon Profile" made by Tendon Excel and filled by hand)
            Controls.Add(new Label { Text = "Excel", Location = new Point(14, 17), AutoSize = true });
            var path = new TextBox { Location = new Point(60, 14), Width = 262, ReadOnly = true, Text = excel ?? "", BackColor = WinColor.White };
            Controls.Add(path);
            var browse = new Button { Text = "Browse...", Location = new Point(328, 12), Size = new Size(86, 26) };
            var reload = new Button { Text = "Reload", Location = new Point(420, 12), Size = new Size(86, 26) };
            Controls.Add(browse); Controls.Add(reload);
            var status = new Label { Location = new Point(14, 44), Size = new Size(492, 70), BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(6, 4, 4, 4), BackColor = WinColor.FromArgb(244, 246, 249) };
            Controls.Add(status);

            // the split(s) of every path: one of its X rows (more than one with ;)
            Controls.Add(new Label { Text = "Splits (one of the X of the path; more than one with ;)", Location = new Point(14, 122), AutoSize = true, ForeColor = UiTheme.Navy });
            var splitPanel = new System.Windows.Forms.Panel { Location = new Point(14, 142), Size = new Size(492, 120), AutoScroll = true, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(splitPanel);
            var boxes = new Dictionary<int, ComboBox>();

            var list = new ListView { Location = new Point(14, 270), Size = new Size(492, 150), View = System.Windows.Forms.View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            list.Columns.Add("Profile", 130); list.Columns.Add("Points", 70); list.Columns.Add("Split after this stage", 270);
            var ro = new Label
            {
                Location = new Point(14, 426), Size = new Size(492, 26), BackColor = WinColor.FromArgb(244, 246, 249), ForeColor = UiTheme.Navy,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), BorderStyle = BorderStyle.FixedSingle
            };
            Button ok = null;
            bool building = false;

            // the splits typed -> stages, checks, the list
            void Recompute()
            {
                if (building) return;
                TendonSet set = Loaded?.Set;
                TendonGeo g = Loaded?.Geo;
                list.Items.Clear();
                if (set == null || g == null) { if (ok != null) ok.Enabled = false; ro.Text = ""; return; }
                Splits = boxes.ToDictionary(kv => kv.Key, kv => string.Join("; ", TendonSheet.SplitList(kv.Value.Text).Select(TendonGeo.Clean)));
                foreach (var kv in Splits) remember[kv.Key] = kv.Value;
                TendonSheet.ApplySplits(set, g, Splits);
                List<string> problems = TendonSheet.Check(set, g, Splits);
                status.Text = problems.Count == 0 ? "Ready: press " + (update ? "Update" : "Draw") + "." : string.Join("\n", problems.Take(6)) + (problems.Count > 6 ? $"\n+ {problems.Count - 6} more" : "") + "\n(fix it in the Excel, save, then Reload)";
                status.ForeColor = problems.Count == 0 ? WinColor.FromArgb(22, 120, 60) : WinColor.Firebrick;
                foreach (TendonProfile p in set.Profiles.OrderBy(x => x.Path).ThenBy(x => x.Stage))
                {
                    double sp = g.SplitAfter(p);
                    string last = p.Pts.Where(q => !TendonGeo.IsAnchor(q.x)).Select(q => q.x).LastOrDefault() ?? "";
                    int empty = p.Pts.Count - p.Filled;
                    var it = new ListViewItem(new[] { p.Title, empty > 0 ? $"{empty} empty" : $"✓ {p.Pts.Count}",
                        p.Stage == set.Stages || p.Pts.LastOrDefault().x == "Anchor2" ? "(last stage - to the deck end)" : $"{last}  =  {sp:0.000}" });
                    if (empty > 0) it.ForeColor = WinColor.Firebrick;
                    list.Items.Add(it);
                }
                ro.Text = $"Stages: {Math.Max(1, set.Stages)}   ·   Paths: {Math.Max(1, set.Paths)}   (paths = B2, stages = the splits)";
                if (ok != null) ok.Enabled = problems.Count == 0;
            }

            void ShowLoad(TendonLoad ld)
            {
                Loaded = ld;
                building = true;
                splitPanel.Controls.Clear();
                boxes.Clear();
                TendonSet set = ld?.Set;
                if (set != null)
                {
                    int y = 6;
                    for (int p = 1; p <= set.Paths; p++)
                    {
                        TendonProfile w = set.Whole.FirstOrDefault(x => x.Path == p);
                        splitPanel.Controls.Add(new Label { Text = $"Path {p}", Location = new Point(8, y + 4), AutoSize = true });
                        var cb = new ComboBox { Location = new Point(70, y), Width = 390, DropDownStyle = ComboBoxStyle.DropDown };
                        if (w != null) cb.Items.AddRange(w.Pts.Where(q => !TendonGeo.IsAnchor(q.x)).Select(q => (object)q.x).ToArray());
                        cb.Text = remember.TryGetValue(p, out string last) ? last : "";
                        cb.TextChanged += (s, e) => Recompute();
                        splitPanel.Controls.Add(cb);
                        boxes[p] = cb;
                        y += 30;
                    }
                }
                building = false;
                if (set == null)
                {
                    status.Text = ld?.Message ?? "";
                    status.ForeColor = WinColor.Firebrick;
                    list.Items.Clear(); ro.Text = "";
                    if (ok != null) ok.Enabled = false;
                }
                else Recompute();
            }
            void LoadExcel(string p)
            {
                Cursor old = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;
                try { Excel = p; path.Text = p ?? ""; ShowLoad(prepare(p)); }
                catch (Exception ex) { ShowLoad(new TendonLoad { Message = "Could not read the Excel: " + ex.Message }); }
                finally { Cursor.Current = old; }
            }
            browse.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Title = "Choose the bridge Excel", Filter = "Excel (*.xlsx)|*.xlsx" })
                {
                    if (!string.IsNullOrEmpty(Excel) && File.Exists(Excel)) { d.InitialDirectory = Path.GetDirectoryName(Excel); d.FileName = Path.GetFileName(Excel); }
                    if (d.ShowDialog(this) == DialogResult.OK) LoadExcel(d.FileName);
                }
            };
            reload.Click += (s, e) => LoadExcel(Excel);
            Shown += (s, e) => { if (!string.IsNullOrEmpty(Excel) && File.Exists(Excel)) LoadExcel(Excel); else ShowLoad(new TendonLoad { Message = "Choose the bridge Excel (Browse...)." }); };
            Controls.Add(list);
            Controls.Add(ro);
            int y = 466;
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
            y += 34;
            // the 2D family of the distance table (default AB Tendon Table Column; any detail item with the same parameters)
            Controls.Add(new Label { Text = "Table family", Location = new Point(14, y + 3), AutoSize = true });
            var tf = new ComboBox { Location = new Point(110, y), Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
            const string Def = TendonDraw.TableFamilyName + " (from the add-in folder)";
            var names = (details ?? new List<string>()).ToList();
            if (!names.Any(n => n.StartsWith(TendonDraw.TableFamilyName + " : "))) names.Insert(0, Def);
            tf.Items.AddRange(names.Cast<object>().ToArray());
            tf.Items.Add(NoFamily);
            int sel = lastTable != null ? tf.Items.IndexOf(lastTable) : -1;
            if (sel < 0) sel = Enumerable.Range(0, tf.Items.Count).FirstOrDefault(i => ((string)tf.Items[i]).StartsWith(TendonDraw.TableFamilyName));
            tf.SelectedIndex = Math.Max(0, sel);
            Controls.Add(tf);
            var load = new Button { Text = "Load...", Location = new Point(416, y - 1), Size = new Size(90, 26) };
            load.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Title = "Choose the table family", Filter = "Revit family (*.rfa)|*.rfa" })
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        TableFile = d.FileName;
                        string n = Path.GetFileNameWithoutExtension(d.FileName) + " (file)";
                        if (!tf.Items.Contains(n)) tf.Items.Insert(0, n);
                        tf.SelectedItem = n;
                    }
            };
            Controls.Add(load);

            ok = new Button { Text = update ? "Update" : "Draw", Location = new Point(316, 630), Size = new Size(92, 32), BackColor = UiTheme.Navy, ForeColor = WinColor.White, FlatStyle = FlatStyle.Flat, Enabled = false };
            var cancel = new Button { Text = "Cancel", Location = new Point(414, 630), Size = new Size(92, 32), DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                TitleBlock = tb.SelectedItem as string; VScale = (double)vs.Value; Make3D = c3.Checked;
                if (Loaded?.Set == null || !ok.Enabled) return;
                Stages = Math.Max(1, Loaded.Set.Stages); Paths = Math.Max(1, Loaded.Set.Paths);
                string pick = tf.SelectedItem as string ?? NoFamily;
                if (!pick.EndsWith(" (file)")) TableFile = null;
                TableFamily = pick == Def ? TendonDraw.TableFamilyName : pick;
                DialogResult = DialogResult.OK; Close();
            };
            Controls.Add(ok); Controls.Add(cancel);
            CancelButton = cancel;
            Brand.AddHeader(this, update ? "Update Tendon" : "Draw Tendon");
            Shown += (s, e) => UiTheme.Snap(this, "Revit " + Text.Replace("AB Bridges - ", ""));
        }
    }

    // ================================================================ the long section view of a path
    internal static class TendonViews
    {
        private static double Ft(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);

        // made once (in an empty place far from the bridge, nothing of the model in it); made again: its dependent
        // views and what was drawn in it go first
        public static ViewSection PathView(Document doc, ViewFamilyType secT, int path, Dictionary<string, string> map, string key, string idsKey)
        {
            ViewSection v = map.TryGetValue(key, out string vid) && long.TryParse(vid, out long l) ? doc.GetElement(new ElementId(l)) as ViewSection : null;
            if (v != null)
            {
                foreach (ElementId d in v.GetDependentViewIds().ToList()) { try { doc.Delete(d); } catch { } }
                try { v.CropBoxActive = false; } catch { }      // all of the new drawing is seen while it is measured
                if (map.TryGetValue(idsKey, out string old))
                    foreach (string sid in old.Split(',')) if (long.TryParse(sid, out long l2)) { try { doc.Delete(new ElementId(l2)); } catch { } }
                return v;
            }
            var t = Transform.Identity;
            t.Origin = new XYZ(-Ft(6000), -Ft(6000) - Ft(300) * path, 0);
            t.BasisX = XYZ.BasisX;
            t.BasisY = XYZ.BasisZ;
            t.BasisZ = XYZ.BasisX.CrossProduct(XYZ.BasisZ);          // the view looks along -BasisZ
            var box = new BoundingBoxXYZ { Transform = t, Min = new XYZ(-Ft(100), -Ft(150), -Ft(0.3)), Max = new XYZ(Ft(2000), Ft(100), 0) };
            v = ViewSection.CreateSection(doc, secT.Id, box);
            try { v.Name = "AB Tendon - Path " + path; } catch { }
            try { v.ViewTemplateId = ElementId.InvalidElementId; } catch { }
            try { v.Scale = 150; } catch { }
            try { v.DetailLevel = ViewDetailLevel.Fine; } catch { }
            try { v.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR)?.Set(Ft(0.3)); } catch { }
            // only the drawing: no levels, grids, other sections, model
            foreach (BuiltInCategory bic in new[] { BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids, BuiltInCategory.OST_Sections, BuiltInCategory.OST_Elev,
                                                     BuiltInCategory.OST_Callouts, BuiltInCategory.OST_CLines, BuiltInCategory.OST_ReferenceViewer, BuiltInCategory.OST_VolumeOfInterest })
            {
                try { Category c = Category.GetCategory(doc, bic); if (c != null && v.CanCategoryBeHidden(c.Id)) v.SetCategoryHidden(c.Id, true); } catch { }
            }
            try { v.AreAnnotationCategoriesHidden = false; } catch { }
            try { v.CropBoxActive = false; } catch { }
            map[key] = v.Id.Value.ToString(CultureInfo.InvariantCulture);
            return v;
        }

        // the plane of the view: x along, y up
        public static Transform Plane(View v)
        {
            var t = Transform.Identity;
            t.Origin = v.Origin; t.BasisX = v.RightDirection; t.BasisY = v.UpDirection; t.BasisZ = v.ViewDirection;
            return t;
        }

        // what the elements cover in the plane of the view
        public static (double x0, double x1, double y0, double y1)? Extent(Document doc, View v, List<ElementId> ids, Transform T)
        {
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            Transform inv = T.Inverse;
            foreach (ElementId id in ids)
            {
                BoundingBoxXYZ bb = doc.GetElement(id)?.get_BoundingBox(v);
                if (bb == null) continue;
                foreach (XYZ q in new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z) })
                {
                    XYZ w = inv.OfPoint(bb.Transform.OfPoint(q));
                    x0 = Math.Min(x0, w.X); x1 = Math.Max(x1, w.X); y0 = Math.Min(y0, w.Y); y1 = Math.Max(y1, w.Y);
                }
            }
            return x0 > x1 ? ((double, double, double, double)?)null : (x0, x1, y0, y1);
        }

        private static BoundingBoxXYZ CropOf(View v, Transform T, double x0, double x1, double y0, double y1)
        {
            BoundingBoxXYZ cb = v.CropBox;
            Transform inv = cb.Transform.Inverse;
            XYZ a = inv.OfPoint(T.OfPoint(new XYZ(x0, y0, 0))), b = inv.OfPoint(T.OfPoint(new XYZ(x1, y1, 0)));
            cb.Min = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), cb.Min.Z);
            cb.Max = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), cb.Max.Z);
            return cb;
        }

        public static void Crop(View v, Transform T, double x0, double x1, double y0, double y1, double margin)
        {
            v.CropBoxActive = true;
            v.CropBox = CropOf(v, T, x0 - margin, x1 + margin, y0 - margin, y1 + margin);
            try { v.CropBoxVisible = true; } catch { }
        }

        // a dependent view of the long view, cut with its crop box to one stage (part)
        public static View Dependent(Document doc, View whole, Transform T, string name, double x0, double x1, double y0, double y1, double margin)
        {
            if (!whole.CanViewBeDuplicated(ViewDuplicateOption.AsDependent)) return null;
            View dep = doc.GetElement(whole.Duplicate(ViewDuplicateOption.AsDependent)) as View;
            if (dep == null) return null;
            try { dep.Name = name; } catch { try { dep.Name = name + " (" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ")"; } catch { } }
            dep.CropBoxActive = true;
            dep.CropBox = CropOf(dep, T, x0 - margin, x1 + margin, y0 - margin, y1 + margin);
            try { dep.CropBoxVisible = false; } catch { }
            return dep;
        }
    }

    // ================================================================ step 1: the sheet
    internal sealed class TendonExcelForm : Form
    {
        public string Excel;
        public List<int> Splits = new List<int>();

        public TendonExcelForm(string excel, Func<string, int> spansOf)
        {
            Text = "AB Bridges - Tendon Excel";
            Font = new System.Drawing.Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 400);
            BackColor = WinColor.White;
            Excel = excel;
            int spans = 0;

            Controls.Add(new Label { Text = "Excel", Location = new Point(14, 17), AutoSize = true });
            var path = new TextBox { Location = new Point(110, 14), Width = 262, ReadOnly = true, Text = excel ?? "", BackColor = WinColor.White };
            var browse = new Button { Text = "Browse...", Location = new Point(380, 12), Size = new Size(86, 26) };
            Controls.Add(path); Controls.Add(browse);
            var info = new Label { Location = new Point(14, 46), Size = new Size(452, 22), ForeColor = UiTheme.Navy };
            Controls.Add(info);
            Controls.Add(new Label { Text = "Number of paths", Location = new Point(14, 81), AutoSize = true });
            var np = new NumericUpDown { Location = new Point(130, 78), Width = 60, Minimum = 1, Maximum = 6, Value = 2 };
            Controls.Add(np);
            var grid = new DataGridView
            {
                Location = new Point(14, 112), Size = new Size(452, 190), AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = WinColor.White, SelectionMode = DataGridViewSelectionMode.CellSelect
            };
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "Path", ReadOnly = true, FillWeight = 25 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Splits", HeaderText = "Number of splits", FillWeight = 30 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Rows", HeaderText = "Rows between the anchors", ReadOnly = true, FillWeight = 45 });
            Controls.Add(grid);
            var note = new Label { Text = "Make sheet clears the sheet \"Tendon Profile\" and makes it again (Anchor1, empty rows, Anchor2 and the data of every path).",
                                   Location = new Point(14, 310), Size = new Size(452, 36), ForeColor = WinColor.Gray };
            Controls.Add(note);

            int SplitsOf(int i) => int.TryParse(Convert.ToString(grid.Rows[i].Cells["Splits"].Value), out int k) && k >= 0 && k <= 9 ? k : 1;
            void Rows()
            {
                for (int i = 0; i < grid.Rows.Count; i++)
                    grid.Rows[i].Cells["Rows"].Value = spans > 0 ? $"{10 * spans + SplitsOf(i)}   (10 x {spans} spans + {SplitsOf(i)})" : "(choose the Excel)";
            }
            void Paths()
            {
                var keep = Enumerable.Range(0, grid.Rows.Count).Select(SplitsOf).ToList();
                grid.Rows.Clear();
                for (int i = 0; i < (int)np.Value; i++) grid.Rows.Add($"Path {i + 1}", (i < keep.Count ? keep[i] : 1).ToString(CultureInfo.InvariantCulture), "");
                Rows();
            }
            void Pick(string p)
            {
                Excel = p; path.Text = p ?? "";
                spans = 0;
                try { spans = string.IsNullOrEmpty(p) || !File.Exists(p) ? 0 : spansOf(p); } catch { spans = 0; }
                info.Text = spans > 0 ? $"{spans} span(s) in the Excel" : "Choose the bridge Excel (made by Civil 3D).";
                info.ForeColor = spans > 0 ? UiTheme.Navy : WinColor.Firebrick;
                Rows();
            }
            np.ValueChanged += (s, e) => Paths();
            grid.CellEndEdit += (s, e) => Rows();
            browse.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Title = "Choose the bridge Excel", Filter = "Excel (*.xlsx)|*.xlsx" })
                {
                    if (!string.IsNullOrEmpty(Excel) && File.Exists(Excel)) { d.InitialDirectory = Path.GetDirectoryName(Excel); d.FileName = Path.GetFileName(Excel); }
                    if (d.ShowDialog(this) == DialogResult.OK) Pick(d.FileName);
                }
            };
            Paths();
            Pick(excel);

            var ok = new Button { Text = "Make sheet", Location = new Point(276, 356), Size = new Size(100, 32), BackColor = UiTheme.Navy, ForeColor = WinColor.White, FlatStyle = FlatStyle.Flat };
            var cancel = new Button { Text = "Cancel", Location = new Point(382, 356), Size = new Size(84, 32), DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                if (grid.IsCurrentCellInEditMode) grid.EndEdit();
                if (spans < 1) { MessageBox.Show(this, "Choose the bridge Excel first (it has no axes).", "AB Bridges"); return; }
                Splits = Enumerable.Range(0, grid.Rows.Count).Select(SplitsOf).ToList();
                DialogResult = DialogResult.OK; Close();
            };
            Controls.Add(ok); Controls.Add(cancel);
            CancelButton = cancel;
            Brand.AddHeader(this, "Tendon Excel");
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class TendonExcelCommand : UI.IExternalCommand
    {
        public UI.Result Execute(UI.ExternalCommandData data, ref string message, ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument.Document;
            try
            {
                Dictionary<string, string> map = AbStore.Load(doc);
                string excel0 = map.TryGetValue("SUB|EXCEL", out string ex0) && File.Exists(ex0) ? ex0 : null;
                using (var f = new TendonExcelForm(excel0, p => new TendonGeo(BridgeExcel.Load(p, false)).Spans))
                {
                    if (f.ShowDialog() != DialogResult.OK) return UI.Result.Cancelled;
                    int spans = new TendonGeo(BridgeExcel.Load(f.Excel, false)).Spans;
                    TendonSheet.Template(f.Excel, spans, f.Splits);
                    using (var t = new Transaction(doc, "AB Bridges - Tendon Excel"))
                    {
                        t.Start();
                        map["SUB|EXCEL"] = f.Excel;
                        AbStore.Save(doc, map);
                        t.Commit();
                    }
                    bool open = TendonSheet.IsLocked(f.Excel);
                    if (!open) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f.Excel) { UseShellExecute = true }); } catch { }
                    UI.TaskDialog.Show("AB Bridges", $"The sheet \"{TendonSheet.Name}\" was made ({f.Splits.Count} path(s)).\n\nType X and Y of every point between Anchor1 and Anchor2 and the data, save, then use Draw Tendon.");
                }
                return UI.Result.Succeeded;
            }
            catch (Exception ex)
            {
                UI.TaskDialog.Show("AB Bridges - error", ex.Message);
                return UI.Result.Cancelled;
            }
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
        private const string ExcelKey = "SUB|EXCEL", IdsKey = "TEN|IDS|", SheetKey = "TEN|SHEET|", ViewKey = "TEN|VIEW|", TableKey = "TEN|TABLEFAM", SplitKey = "TEN|SPLIT|";
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private static double Ft(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);

        // the Excel chosen in the window: only read (it is made by Tendon Excel and filled by hand)
        private static TendonLoad Prepare(string excel)
        {
            var res = new TendonLoad();
            if (string.IsNullOrEmpty(excel) || !File.Exists(excel)) { res.Message = "Choose the bridge Excel."; return res; }
            BridgeExcel bx;
            try { bx = BridgeExcel.Load(excel, false); }
            catch (Exception ex) { res.Message = "The Excel could not be read: " + ex.Message; return res; }
            var g = new TendonGeo(bx);
            res.Geo = g;
            if (g.Spans < 1) { res.Message = "The Excel has no axes (sheet Substructure / Super Structure)."; return res; }
            TendonSet set = TendonSheet.Read(excel, g);
            if (set == null) { res.Message = $"The Excel has no sheet \"{TendonSheet.Name}\". Make it with the button Tendon Excel first."; return res; }
            res.Set = set;
            return res;
        }

        public static UI.Result Run(UI.ExternalCommandData data, bool update)
        {
            UI.UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            try
            {
                Dictionary<string, string> map = AbStore.Load(doc);
                string excel0 = map.TryGetValue(ExcelKey, out string ex0) && File.Exists(ex0) ? ex0 : null;

                // reads the Tendon Profile sheet of an Excel and puts it in order (paths = B2, split rows in X, old split
                // rows out) - in the open workbook when Excel has it open. What is left to do is shown in the window.
                Func<string, TendonLoad> prepare = Prepare;
                var lastSplits = new Dictionary<int, string>();
                foreach (var kv in map) if (kv.Key.StartsWith(SplitKey) && int.TryParse(kv.Key.Substring(SplitKey.Length), out int lp)) lastSplits[lp] = kv.Value;
                List<string> tbs = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>().Select(s => s.FamilyName + " : " + s.Name).OrderBy(n => n).ToList();
                List<string> details = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DetailComponents).OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>().Select(s => s.FamilyName + " : " + s.Name).OrderBy(n => n).ToList();
                TendonForm form;
                using (form = new TendonForm(excel0, prepare, tbs, update, details, map.TryGetValue(TableKey, out string lastT) ? lastT : null, lastSplits))
                    if (form.ShowDialog() != DialogResult.OK) return UI.Result.Cancelled;
                map[TableKey] = form.TableFamily;
                string excel = form.Excel;
                TendonSet set = form.Loaded.Set;
                TendonGeo g = form.Loaded.Geo;
                TendonSheet.ApplySplits(set, g, form.Splits);
                map[ExcelKey] = excel;
                foreach (var kv in form.Splits) map[SplitKey + kv.Key] = kv.Value;

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

                    // one sheet per stage: path 1 over path 2 (each view with its data table on the right), notes at the
                    // bottom left. A stage longer than the sheet goes on more sheets of the same layout (part 1, 2 ...).
                    FamilySymbol tb = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>().FirstOrDefault(x => x.FamilyName + " : " + x.Name == form.TitleBlock);
                    ViewFamilyType secT = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x => x.ViewFamily == ViewFamily.Section);
                    if (secT == null) log.AppendLine("No section view type in the project.");
                    FamilySymbol col = TendonDraw.TableFamily(doc, log, form.TableFamily, form.TableFile);
                    int nSheets = 0;

                    // the views of the old version (one drafting view per stage) go
                    foreach (string k0 in map.Keys.Where(k => k.StartsWith(ViewKey) && !k.StartsWith(ViewKey + "P")).ToList())
                    {
                        if (long.TryParse(map[k0], out long lo) && doc.GetElement(new ElementId(lo)) is ViewDrafting) { try { doc.Delete(new ElementId(lo)); } catch { } }
                        map.Remove(k0);
                    }
                    foreach (string k0 in map.Keys.Where(k => k.StartsWith(IdsKey + "2D|") && !k.StartsWith(IdsKey + "2D|P")).ToList()) map.Remove(k0);

                    // one long section view per path: its stages (and the parts of a long stage) drawn side by side,
                    // every one shown by a dependent view cut with its crop box -> the views on the sheets
                    var views = new Dictionary<(int stage, int path, int part), View>();
                    var partsOf = new Dictionary<int, int>();
                    for (int pth = 1; pth <= set.Paths && secT != null; pth++)
                    {
                        var stages = set.Profiles.Where(x => x.Path == pth).OrderBy(x => x.Stage).ToList();
                        if (stages.Count == 0) continue;
                        ViewSection whole = TendonViews.PathView(doc, secT, pth, map, ViewKey + "P" + pth, IdsKey + "2D|P" + pth);
                        Transform T = TendonViews.Plane(whole);
                        double cursor = 0;
                        var cuts = new List<(int stage, int part, int parts, double x0, double x1, double y0, double y1)>();
                        var drawn = new List<ElementId>();
                        foreach (TendonProfile sp in stages)
                        {
                            var parts = TendonDraw.Parts(g, set, sp, 150);
                            partsOf[sp.Stage] = Math.Max(partsOf.TryGetValue(sp.Stage, out int pc) ? pc : 1, parts.Count);
                            for (int k = 1; k <= parts.Count; k++)
                            {
                                List<ElementId> ids = TendonDraw.Draw(doc, whole, g, set, sp, form.VScale, parts[k - 1].a, parts[k - 1].b, k, parts.Count, col, T);
                                doc.Regenerate();
                                var ex = TendonViews.Extent(doc, whole, ids, T);
                                if (ex == null) continue;
                                double dx = cursor - ex.Value.x0;
                                if (Math.Abs(dx) > 1e-6) { ElementTransformUtils.MoveElements(doc, ids, T.BasisX * dx); doc.Regenerate(); }
                                cuts.Add((sp.Stage, k, parts.Count, ex.Value.x0 + dx, ex.Value.x1 + dx, ex.Value.y0, ex.Value.y1));
                                cursor = ex.Value.x1 + dx + Ft(10);       // a gap of 10 m between the stages in the long view
                                drawn.AddRange(ids);
                            }
                        }
                        map[IdsKey + "2D|P" + pth] = string.Join(",", drawn.Select(i => i.Value.ToString(CI)));
                        if (cuts.Count == 0) continue;
                        TendonViews.Crop(whole, T, cuts.Min(c => c.x0), cuts.Max(c => c.x1), cuts.Min(c => c.y0), cuts.Max(c => c.y1), Ft(3));
                        foreach (var c in cuts)
                        {
                            string nm = $"AB Tendon - Path {pth} - Stage {c.stage}" + (c.parts > 1 ? $" - Part {c.part}" : "");
                            View dep = TendonViews.Dependent(doc, whole, T, nm, c.x0, c.x1, c.y0, c.y1, Ft(1.5));
                            if (dep != null) views[(c.stage, pth, c.part)] = dep;
                        }
                    }

                    for (int stage = 1; stage <= set.Stages && secT != null; stage++)
                    {
                        List<TendonProfile> paths = set.Profiles.Where(x => x.Stage == stage).OrderBy(x => x.Path).ToList();
                        int nParts = partsOf.TryGetValue(stage, out int np0) ? np0 : 1;
                        for (int part = 1; part <= nParts; part++)
                        {
                            // the sheet: made once, kept on Update; missing views are placed, the data tables / notes redrawn
                            string sk = SheetKey + "S" + stage + "|" + part;
                            ViewSheet sheet = map.TryGetValue(sk, out string shv) && long.TryParse(shv, out long l4) ? doc.GetElement(new ElementId(l4)) as ViewSheet : null;
                            if (sheet == null)
                            {
                                if (update) continue;
                                sheet = ViewSheet.Create(doc, tb?.Id ?? ElementId.InvalidElementId);
                                try
                                {
                                    sheet.Name = $"WEB POST-TENSIONING PROFILE - STAGE {stage}" + (nParts > 1 ? $" ({part} OF {nParts})" : "");
                                    sheet.SheetNumber = $"AB-TP-{stage:00}" + (nParts > 1 ? "-" + part : "");
                                }
                                catch { }
                                map[sk] = sheet.Id.Value.ToString(CI);
                                nSheets++;
                            }
                            BoundingBoxUV o = sheet.Outline;
                            double W = o.Max.U - o.Min.U, H = o.Max.V - o.Min.V;
                            if (W < Ft(0.2)) { W = Ft(0.841); H = Ft(0.594); o = new BoundingBoxUV(0, 0, W, H); }
                            double tableW = Ft(0.095);                                   // data table at the right of every view
                            double right = o.Min.U + W * 0.97 - tableW;                  // views end here
                            double zoneTop = o.Max.V - H * 0.05, zoneBot = o.Min.V + H * 0.25;   // notes / title block below
                            var onSheet = paths.Where(q => views.ContainsKey((stage, q.Path, part))).ToList();
                            var placed = new HashSet<ElementId>(sheet.GetAllPlacedViews());
                            string sdk = IdsKey + "SHEET|S" + stage + "|" + part;
                            if (map.TryGetValue(sdk, out string olds))
                                foreach (string sid in olds.Split(',')) if (long.TryParse(sid, out long l5)) { try { doc.Delete(new ElementId(l5)); } catch { } }
                            var sids = new List<ElementId>();
                            for (int k = 0; k < onSheet.Count; k++)
                            {
                                TendonProfile p = onSheet[k];
                                View v = views[(stage, p.Path, part)];
                                double cv = zoneTop - (zoneTop - zoneBot) * (k + 0.5) / onSheet.Count;
                                double cu = (o.Min.U + W * 0.03 + right) / 2;
                                Viewport vp = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(x => x.ViewId == v.Id);
                                if (vp == null && !placed.Contains(v.Id) && Viewport.CanAddViewToSheet(doc, sheet.Id, v.Id))
                                    vp = Viewport.Create(doc, sheet.Id, v.Id, new XYZ(cu, cv, 0));
                                // the data table: right of the view, at the height of the box
                                double top = cv + H * 0.06;
                                if (vp != null) { Outline bo = vp.GetBoxOutline(); top = bo.MaximumPoint.Y - (bo.MaximumPoint.Y - bo.MinimumPoint.Y) * 0.35; }
                                sids.AddRange(TendonDraw.Data(doc, sheet, new XYZ(o.Min.U + W * 0.97 - tableW, top, 0), 1.0 / 1000, p, JackLabel(g, set, p)));
                            }
                            sids.AddRange(TendonDraw.Notes(doc, sheet, new XYZ(o.Min.U + W * 0.04, o.Min.V + H * 0.20, 0), paths.FirstOrDefault()));
                            map[sdk] = string.Join(",", sids.Select(i => i.Value.ToString(CI)));
                        }
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

        // "* DENOTES JACKING END" value: the axis at the jacking end of the first / last stage, else the construction joint
        private static string JackLabel(TendonGeo g, TendonSet set, TendonProfile p)
        {
            bool right = p.Data.TryGetValue("Jacking end (Left / Right)", out string je) && je.Trim().StartsWith("R", StringComparison.OrdinalIgnoreCase);
            if (!right && p.Stage == 1 && g.AxisName.Count > 0) return "@" + g.AxisName[0];
            if (right && p.Stage == set.Stages && g.AxisName.Count > 0) return "@" + g.AxisName[g.AxisName.Count - 1];
            return "@CJ";
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
                    XYZ o0 = origin;
                    var local = pts.Select(q => q - o0).ToList();
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
            double L = st.Last().sta - st.First().sta, max = 0.53 * scale;     // ~80 m at 1:150: the view + the data table fit on A1
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

        public static List<ElementId> Draw(Document doc, View v, TendonGeo g, TendonSet set, TendonProfile p, double vs, int ia, int ib, int part, int parts, FamilySymbol col = null, Transform T = null)
        {
            var ids = new List<ElementId>();
            // drawn in the plane of the view: a drafting view (identity) or the long section of the path (T)
            T = T ?? Transform.Identity;
            XYZ M(XYZ q) => T.OfPoint(q);
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

            void Ln(XYZ a, XYZ b, GraphicsStyle gs) { if (a.DistanceTo(b) < 1e-4) return; try { DetailCurve dc = doc.Create.NewDetailCurve(v, Line.CreateBound(M(a), M(b))); if (gs != null) dc.LineStyle = gs; ids.Add(dc.Id); } catch { } }
            void Tx(XYZ at, string t, double rot = 0, HorizontalTextAlignment ha = HorizontalTextAlignment.Center)
            {
                try { ids.Add(TextNote.Create(doc, v.Id, M(at), t, new TextNoteOptions(tt) { Rotation = rot, HorizontalAlignment = ha }).Id); } catch { }
            }
            void TxT(XYZ at, string t, double rot, ElementId type)
            {
                if (string.IsNullOrEmpty(t)) return;
                try { ids.Add(TextNote.Create(doc, v.Id, M(at), t, new TextNoteOptions(type) { Rotation = rot, HorizontalAlignment = HorizontalTextAlignment.Center }).Id); } catch { }
            }

            // box: grey fill, outline, the section lines, the cells (dashed) between the diaphragms
            var box = new CurveLoop();
            box.Append(Line.CreateBound(P(s0, 0), P(s1, 0))); box.Append(Line.CreateBound(P(s1, 0), P(s1, depth)));
            box.Append(Line.CreateBound(P(s1, depth), P(s0, depth))); box.Append(Line.CreateBound(P(s0, depth), P(s0, 0)));
            try
            {
                FilledRegionType frt = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                    .OrderByDescending(x => x.Name.IndexOf("Solid", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("Gray", StringComparison.OrdinalIgnoreCase) >= 0).FirstOrDefault();
                if (frt != null) ids.Add(FilledRegion.Create(doc, frt.Id, v.Id, new List<CurveLoop> { CurveLoop.CreateViaTransform(box, T) }).Id);
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
                try { ids.Add(doc.Create.NewDetailCurve(v, Arc.Create(M(c), rr, 0, Math.PI, T.OfVector(XYZ.BasisX), T.OfVector(XYZ.BasisY))).Id); ids.Add(doc.Create.NewDetailCurve(v, Arc.Create(M(c), rr, Math.PI, 2 * Math.PI, T.OfVector(XYZ.BasisX), T.OfVector(XYZ.BasisY))).Id); } catch { }
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
            Ln(new XYZ(0, -ys, 0) + new XYZ(-Ft(paper), -Ft(paper), 0), new XYZ(0, -ys, 0) + new XYZ(Ft(paper), Ft(paper), 0), thin);
            Ln(new XYZ(Ft(s1 - s0), -ys, 0) + new XYZ(-Ft(paper), -Ft(paper), 0), new XYZ(Ft(s1 - s0), -ys, 0) + new XYZ(Ft(paper), Ft(paper), 0), thin);
            Tx(new XYZ(Ft((s1 - s0) / 2), -ys + Ft(3 * paper), 0), $"CONSTRUCTION STAGE -{p.Stage}\n{total * 1000:0} ({jack} END JACKING)");

            // the tendon: one smooth curve through the points, * at the jacking end
            var cp = st.Select(q => P(q.sta, double.IsNaN(q.y) ? depth / 2 : q.y)).ToList();
            try
            {
                var cpm = cp.Select(M).ToList();
                Curve tc = cpm.Count == 2 ? (Curve)Line.CreateBound(cpm[0], cpm[1]) : HermiteSpline.Create(cpm, false);
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
            // rows as tall as the longest text (+ margin) so no text goes over the lines; the text sits in the middle of its cell
            const double TextMm = 2.0;
            ElementId tt2 = TextType(doc, TextMm);
            int n = st.Count;
            string[] labs = new string[n], vals = new string[n];
            for (int k = 0; k < n; k++)
            {
                labs[k] = TendonGeo.IsAnchor(st[k].x) ? (st[k].x.EndsWith("2") ? "Anc.2" : "Anc.1") : st[k].x;
                vals[k] = double.IsNaN(st[k].y) ? "" : (st[k].y * 1000).ToString("0", CI);
            }
            double charMm = TextMm * 0.8;                                        // width of one character (Arial ~0.8 x height, kept generous)
            double rh1 = Ft(Math.Max(12, labs.Max(x => x.Length) * charMm + 4) * paper);
            double rh2 = Ft(Math.Max(12, vals.Max(x => x.Length) * charMm + 4) * paper);
            double cw = Ft(Math.Max(8, TextMm * 2 + 4) * paper), x0 = 0, y0 = P(s0, depth).Y - Ft(18 * paper);
            double hw = Ft(50 * paper);
            var head = new[] { "DISTANCE ALONG C/L", "DISTANCE FROM TOP OF BOX\nTO TENDONS CG IN (mm)" };
            // head cells (lines + text)
            Ln(new XYZ(x0 - hw, y0, 0), new XYZ(x0, y0, 0), thin);
            Ln(new XYZ(x0 - hw, y0 - rh1, 0), new XYZ(x0, y0 - rh1, 0), thin);
            Ln(new XYZ(x0 - hw, y0 - rh1 - rh2, 0), new XYZ(x0, y0 - rh1 - rh2, 0), thin);
            Ln(new XYZ(x0 - hw, y0, 0), new XYZ(x0 - hw, y0 - rh1 - rh2, 0), thin);
            TxT(new XYZ(x0 - hw / 2, y0 - rh1 / 2 + Ft(TextMm / 2 * paper), 0), head[0], 0, tt2);
            TxT(new XYZ(x0 - hw / 2, y0 - rh1 - rh2 / 2 + Ft(TextMm * paper), 0), head[1], 0, tt2);
            if (col != null)
            {
                // the 2D family: one column per point (X Label / Y Value), placed side by side
                try
                {
                    if (!col.IsActive) col.Activate();
                    SetLen(col, "Row 1 H", rh1); SetLen(col, "Row 2 H", rh2);
                    for (int k = 0; k < n; k++)
                    {
                        FamilyInstance fi = doc.Create.NewFamilyInstance(M(new XYZ(x0 + k * cw, y0, 0)), col, v);
                        SetText(fi, "X Label", labs[k]); SetText(fi, "Y Value", vals[k]); SetLen(fi, "Width", cw);
                        SetLen(fi, "Row 1 H", rh1); SetLen(fi, "Row 2 H", rh2);          // when they are instance parameters
                        ids.Add(fi.Id);
                    }
                    Ln(new XYZ(x0 + n * cw, y0, 0), new XYZ(x0 + n * cw, y0 - rh1 - rh2, 0), thin);
                }
                catch { col = null; }
            }
            if (col == null)
            {
                // lines + text notes
                foreach (double yy in new[] { y0, y0 - rh1, y0 - rh1 - rh2 }) Ln(new XYZ(x0, yy, 0), new XYZ(x0 + n * cw, yy, 0), thin);
                for (int k = 0; k <= n; k++) Ln(new XYZ(x0 + k * cw, y0, 0), new XYZ(x0 + k * cw, y0 - rh1 - rh2, 0), thin);
                // a note turned 90° hangs to the right of its point by its height: move it left by half the text height
                double half = Ft(TextMm / 2 * paper);
                for (int k = 0; k < n; k++)
                {
                    double xc = x0 + (k + 0.5) * cw - half;
                    TxT(new XYZ(xc, y0 - rh1 / 2, 0), labs[k], Math.PI / 2, tt2);
                    TxT(new XYZ(xc, y0 - rh1 - rh2 / 2, 0), vals[k], Math.PI / 2, tt2);
                }
            }
            double rh = (rh1 + rh2) / 2;                                          // used below (2 * rh = table height)

            // a cut part: the break line and FOR CONTINUATION, SEE BELOW / ABOVE
            double yTop = Ft(36 * paper), yBot = y0 - 2 * rh;
            if (!last)
            {
                double xb = Ft(s1 - s0) + Ft(12 * paper);
                Ln(new XYZ(xb, yTop, 0), new XYZ(xb, yBot, 0), dash);
                Tx(new XYZ(xb + Ft(5 * paper), (yTop + yBot) / 2, 0), "FOR CONTINUATION, SEE NEXT SHEET", Math.PI / 2);
            }
            if (!first)
            {
                double xb = x0 - hw - Ft(4 * paper);
                Ln(new XYZ(xb, yTop, 0), new XYZ(xb, yBot, 0), dash);
                Tx(new XYZ(xb - Ft(5 * paper), (yTop + yBot) / 2, 0), "FOR CONTINUATION, SEE PREVIOUS SHEET", Math.PI / 2);
            }

            // title under the view (underlined) and the scale
            double tyl = y0 - 2 * rh - Ft(12 * paper), xm = Ft((s1 - s0) / 2);
            Tx(new XYZ(xm, tyl, 0), $"WEB POST TENSIONING TENDON PROFILE FOR TENDON PATH {p.Path} - STAGE {p.Stage}" + (parts > 1 ? $" (PART {part} OF {parts})" : ""));
            Ln(new XYZ(xm - Ft(60 * paper), tyl - Ft(4.5 * paper), 0), new XYZ(xm + Ft(60 * paper), tyl - Ft(4.5 * paper), 0), thin);
            Tx(new XYZ(xm - Ft(60 * paper), tyl - Ft(7.5 * paper), 0), "1 : " + v.Scale, 0, HorizontalTextAlignment.Left);
            return ids;
        }

        // the data table, drawn on the sheet (paper = metres per sheet mm)
        public static List<ElementId> Data(Document doc, View v, XYZ at, double paper, TendonProfile p, string jack = "@CJ")
        {
            var ids = new List<ElementId>();
            ElementId tt = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            GraphicsStyle thin = SubstructureCommand.LineStyle(doc, "AB Tendon Thin", new RColor(0, 0, 0));
            void Ln(XYZ a, XYZ b) { try { DetailCurve dc = doc.Create.NewDetailCurve(v, Line.CreateBound(a, b)); if (thin != null) dc.LineStyle = thin; ids.Add(dc.Id); } catch { } }
            void Tx(XYZ q, string t) { try { ids.Add(TextNote.Create(doc, v.Id, q, t, new TextNoteOptions(tt) { HorizontalAlignment = HorizontalTextAlignment.Center }).Id); } catch { } }
            double tw = Ft(72 * paper), tv = Ft(20 * paper), trh = Ft(7 * paper);
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
            Tx(new XYZ(at.X + tw + tv / 2, at.Y - (keys.Length + 0.35) * trh, 0), jack);
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

        // the 2D table column family (Families\AB Tendon Table Column.rfa next to the add-in); null = draw with lines + text
        public const string TableFamilyName = "AB Tendon Table Column";
        private static readonly string[] TableParams = { "X Label", "Y Value", "Width", "Row 1 H", "Row 2 H" };

        // pick = "Family : Type" from the list, the default name, or "(no family ...)"; file = a .rfa chosen with Load...
        public static FamilySymbol TableFamily(Document doc, StringBuilder log, string pick = TableFamilyName, string file = null)
        {
            if (string.IsNullOrEmpty(pick) || pick == TendonForm.NoFamily) return null;
            FamilySymbol sym = null;
            try
            {
                Family f = null;
                if (file != null && File.Exists(file))
                {
                    doc.LoadFamily(file, out f);
                    if (f == null)      // already in the project under the file's name
                        f = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(x => x.Name == Path.GetFileNameWithoutExtension(file));
                }
                if (f == null && pick.Contains(" : "))
                    sym = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DetailComponents).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                        .FirstOrDefault(x => x.FamilyName + " : " + x.Name == pick);
                if (sym == null && f == null)
                {
                    f = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(x => x.Name == TableFamilyName);
                    if (f == null)
                    {
                        string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                        string def = new[] { Path.Combine(dir, "Families", TableFamilyName + ".rfa"), Path.Combine(dir, TableFamilyName + ".rfa") }.FirstOrDefault(File.Exists);
                        if (def == null) { log.AppendLine($"Table family \"{TableFamilyName}.rfa\" not found next to the add-in - table drawn with lines + text."); return null; }
                        doc.LoadFamily(def, out f);
                    }
                }
                if (sym == null && f != null) sym = f.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol).FirstOrDefault(x => x != null);
            }
            catch (Exception e) { log.AppendLine("Table family not loaded: " + e.Message); return null; }
            if (sym == null) return null;
            // the parameters the table needs (type or instance); missing -> lines + text
            if (!sym.IsActive) sym.Activate();
            var missing = TableParams.Where(n => sym.LookupParameter(n) == null && !HasInstanceParam(sym.Family, n)).ToList();
            if (missing.Count > 0) { log.AppendLine($"Table family \"{sym.FamilyName}\" has no parameter(s) {string.Join(", ", missing)} - table drawn with lines + text."); return null; }
            return sym;
        }

        private static bool HasInstanceParam(Family f, string name)
        {
            try
            {
                Document fd = f.Document.EditFamily(f);
                try { return fd.FamilyManager.Parameters.Cast<FamilyParameter>().Any(p => p.Definition.Name == name); }
                finally { fd.Close(false); }
            }
            catch { return true; }      // cannot check: try it
        }

        // text type "AB Tendon 2 mm": known size, so the table cells can be sized to the text
        private static ElementId TextType(Document doc, double mm)
        {
            string name = $"AB Tendon {mm.ToString("0.#", CI)} mm";
            TextNoteType ex = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault(x => x.Name == name);
            if (ex != null) return ex.Id;
            ElementId def = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            try
            {
                TextNoteType t = (doc.GetElement(def) as TextNoteType)?.Duplicate(name) as TextNoteType;
                if (t == null) return def;
                t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters));
                t.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set("Arial");
                t.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(1);
                t.get_Parameter(BuiltInParameter.TEXT_BOX_VISIBILITY)?.Set(0);
                return t.Id;
            }
            catch { return def; }
        }

        private static void SetLen(Element e, string name, double v) { try { Parameter p = e.LookupParameter(name); if (p != null && !p.IsReadOnly) p.Set(v); } catch { } }
        private static void SetText(Element e, string name, string v) { try { Parameter p = e.LookupParameter(name); if (p != null && !p.IsReadOnly) p.Set(v ?? ""); } catch { } }

        private static double Num(TendonProfile p, string key, double def) =>
            p.Data.TryGetValue(key, out string s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : def;
    }
}
