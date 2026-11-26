// AB Bridges for Revit - writes the family names and parameters into the "Parameters" sheet of the
// Excel made by AB Bridges in Civil 3D. Only that sheet is changed; the other sheets stay as they are.
//
// Layout of the sheet (made by Civil):
//   group row   : STATIONS | DECK FAMILY ... | CORBEL FAMILY ... | OUTRIGGER FAMILY ...
//   header row  : No, Remark, Type, Chainage, Station | Point, <params> | Instance, Pt, Place, <params> | Instance, Pt, Place, <params>
//   data rows   : one per station
// The <params> part of every family group is replaced by one column per family parameter.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ABBridgesRevit
{
    internal sealed class GroupFill
    {
        public int Number;            // Family 1, 2, 3 ... = 1st, 2nd, 3rd family group of the sheet
        public FamInfo Family;        // null = leave the group as it is
    }

    internal static class XlsxPatch
    {
        private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        public static string Write(string path, List<GroupFill> groups)
        {
            var report = new StringBuilder();
            string tmp = path + ".abtmp";
            File.Copy(path, tmp, true);
            try
            {
                using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Update))
                {
                    string sheetPath = SheetPath(zip, "Parameters");
                    if (sheetPath == null) throw new InvalidOperationException("The Excel file has no \"Parameters\" sheet (make it with AB Bridges in Civil 3D).");
                    List<string> shared = SharedStrings(zip);

                    XDocument doc;
                    ZipArchiveEntry entry = zip.GetEntry(sheetPath);
                    using (Stream s = entry.Open()) doc = XDocument.Load(s);

                    var sheet = new Sheet(doc, shared);
                    int g = sheet.FindRow(1, "STATIONS");
                    if (g < 0) throw new InvalidOperationException("Could not find the STATIONS header in the Parameters sheet.");
                    int last = sheet.LastRow();

                    // every family group is written again from nothing: the old groups (whatever they were) go,
                    // the chosen families come side by side after STATIONS, each through every station.
                    // Kept by family name: the Type (Deck / Corbel / Outrigger) and the Start/Mid/End inputs.
                    List<(int start, int end)> groupsFound = sheet.FamilyGroups(g);
                    var kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var keepAll = new Dictionary<string, Dictionary<string, Dictionary<int, (string ty, string v)>>>(StringComparer.OrdinalIgnoreCase);
                    int stEnd = sheet.StationsEnd(g);
                    var styleP = new Dictionary<int, string>();   // row -> style of a Point cell / a parameter cell
                    var styleV = new Dictionary<int, string>();
                    foreach (var (gs, ge) in groupsFound)
                    {
                        string name = GroupName(sheet.GetText(g, gs));
                        if (name.Length == 0) continue;
                        string k = sheet.GetText(g - 1, gs).Trim();
                        if (k == "Deck" || k == "Corbel" || k == "Outrigger") kinds[name] = k;
                        else if (sheet.GetText(g + 1, gs).Trim() == "Instance")
                        {
                            for (int r = g + 2; r <= last; r++)
                            {
                                string iname = sheet.GetText(r, gs).Trim().ToUpperInvariant();
                                if (iname.StartsWith("CB")) { kinds[name] = "Corbel"; break; }
                                if (iname.StartsWith("OR")) { kinds[name] = "Outrigger"; break; }
                            }
                        }
                        keepAll[name] = CaptureVary(sheet, g, last, gs, ge);
                    }
                    if (groupsFound.Count > 0)
                    {
                        int fs = groupsFound[0].start, ff = sheet.FixedColumns(g, groupsFound[0].start, groupsFound[0].end);
                        for (int r = g - 1; r <= last; r++)
                        {
                            styleP[r] = sheet.GetStyle(r, fs);
                            styleV[r] = sheet.GetStyle(r, Math.Min(fs + ff, groupsFound[0].end));
                        }
                        int a0 = groupsFound.Min(x => x.start), b0 = groupsFound.Max(x => x.end);
                        sheet.RemoveValidations(a0, b0, 1, last);
                        sheet.RemoveConditional(a0, b0);
                        sheet.DeleteColumns(a0, b0 - a0 + 1);
                    }
                    int dxf = ShadeStyle(zip);

                    int at = stEnd + 1;
                    foreach (GroupFill gf in groups)
                    {
                        if (gf.Family == null) continue;
                        sheet.InsertColumns(at, 2, stEnd, g, last);
                        for (int r = g - 1; r <= last; r++)
                        {
                            if (styleP.TryGetValue(r, out string sp) && sp != null) sheet.SetStyle(r, at, sp);
                            if (styleV.TryGetValue(r, out string sv) && sv != null) sheet.SetStyle(r, at + 1, sv);
                        }
                        sheet.Remove(g - 1, at); sheet.Remove(g - 1, at + 1);
                        sheet.SetText(g + 1, at, "Point");
                        for (int r = g + 2; r <= last; r++)
                        {
                            if (sheet.IsNumber(r, 1)) sheet.SetNumber(r, at, sheet.Number(r, 1));
                            else sheet.Clear(r, at);
                        }
                        keepAll.TryGetValue(gf.Family.Name, out var keep);
                        FillGroup(sheet, gf, g, last, at, at + 1, 1, keep);
                        int end = sheet.MergeEnd(g, at);
                        sheet.SetWidth(at, 11);

                        // Type above the family name: Deck = every station; Corbel / Outrigger = only their stations
                        // (the other rows turn grey and blank - Revit takes only those stations)
                        string kind = kinds.TryGetValue(gf.Family.Name, out string kk) ? kk : "Deck";
                        sheet.SetText(g - 1, at, kind);
                        sheet.CopyStyle(g + 1, at, g - 1, at);
                        sheet.AddList(Sheet.Ref(g - 1, at), "\"Deck,Corbel,Outrigger\"");
                        if (dxf >= 0) sheet.AddShading(at, end, g, last, dxf);

                        report.AppendLine($"Family {gf.Number}: {gf.Family.Name} - {gf.Family.Params.Count} parameter(s), Type {kind}" +
                                          (gf.Family.AdaptivePoints >= 0 ? $", {gf.Family.AdaptivePoints} adaptive point(s)" : ""));
                        at = end + 1;
                    }

                    sheet.Finish();
                    CalcOnLoad(zip);
                    entry.Delete();
                    ZipArchiveEntry ne = zip.CreateEntry(sheetPath, CompressionLevel.Optimal);
                    using (Stream s = ne.Open())
                    using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                        doc.Save(w, SaveOptions.DisableFormatting);
                }
                File.Copy(tmp, path, true);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
            return report.ToString();
        }

        private static string Line(Sheet sh, GroupFill gf, int g, int last, int start, int fixedCols)
        {
            FamInfo f = gf.Family;
            string s = $"Family {gf.Number}: {f.Name} - {f.Params.Count} parameter(s)";
            if (f.AdaptivePoints >= 0) s += $", {f.AdaptivePoints} adaptive point(s)";
            // groups with a Pt column expect that many adaptive points
            if (fixedCols >= 3 && f.AdaptivePoints >= 0)
            {
                int pts = 0;
                for (int r = g + 2; r <= last; r++)
                    if (sh.IsNumber(r, start + 1)) pts = Math.Max(pts, (int)sh.Number(r, start + 1));
                if (pts > 0 && pts != f.AdaptivePoints) s += $"  - note: this group gives {pts} stations per instance";
            }
            return s;
        }

        public const string VarHeader = "variation", VValHeader = "V.value";

        // a number that changes along the bridge: value | variation | V.value (instance numbers only)
        private static bool Varies(FamParam p) => p != null && p.IsInstance && !p.IsYesNo && p.Value is double;

        private static void FillGroup(Sheet sh, GroupFill gf, int g, int last, int start, int end, int fixedCols,
                                      Dictionary<string, Dictionary<int, (string ty, string v)>> keep)
        {
            FamInfo fam = gf.Family;
            int pStart = start + fixedCols;
            int oldCount = Math.Max(1, end - pStart + 1);
            int oldEnd = pStart + oldCount - 1;

            keep = keep ?? new Dictionary<string, Dictionary<int, (string ty, string v)>>(StringComparer.OrdinalIgnoreCase);
            sh.RemoveValidations(pStart, oldEnd, g - 1, last);

            // columns: 1 per parameter, 3 for a number that can vary
            var plist = fam.Params.Count == 0 ? new List<FamParam> { null } : fam.Params.ToList();
            int newCount = plist.Sum(x => Varies(x) ? 3 : 1);
            if (newCount < oldCount) sh.DeleteColumns(pStart + newCount, oldCount - newCount);
            else if (newCount > oldCount) sh.InsertColumns(oldEnd + 1, newCount - oldCount, oldEnd, g, last);
            int newEnd = pStart + newCount - 1;

            // title spans the whole group
            sh.SetMerge(g, start, newEnd);
            sh.SetText(g, start, $"FAMILY {gf.Number}  —  {fam.Name}");

            int c0 = pStart;
            foreach (FamParam p in plist)
            {
                int c = c0;
                bool vary = Varies(p);
                c0 += vary ? 3 : 1;
                for (int k = 0; k < (vary ? 3 : 1); k++) { sh.Remove(g - 1, c + k); sh.CopyStyle(g + 1, pStart, g + 1, c + k); }
                sh.SetText(g + 1, c, p == null ? "(no parameters)" : p.Header);

                // Yes/No: a master ☑/☐ above the column switches the whole column; every cell can still be changed alone
                string master = null;
                if (p != null && p.IsYesNo && g > 1)
                {
                    bool shape = p.Name.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase)
                              || p.Name.TrimStart().StartsWith("void", StringComparison.OrdinalIgnoreCase);
                    string mv = !shape && p.Value as string == "☐" ? "☐" : "☑";
                    sh.SetText(g - 1, c, mv);
                    sh.CopyStyle(g + 1, c, g - 1, c);
                    sh.AddCheckList(Sheet.Ref(g - 1, c));
                    master = "$" + Sheet.Col(c) + "$" + (g - 1).ToString(CI);
                }

                Dictionary<int, (string ty, string v)> old = null;
                if (vary)
                {
                    sh.SetText(g + 1, c + 1, VarHeader);
                    sh.SetText(g + 1, c + 2, VValHeader);
                    keep.TryGetValue(p.Header, out old);
                }

                // rows used by this family; for a varying number: the Start/Mid/End list and the values first
                var used = new List<int>();
                for (int r = g + 2; r <= last; r++)
                {
                    bool u = fixedCols == 1 ? sh.IsNumber(r, 1) : !string.IsNullOrWhiteSpace(sh.GetText(r, start));
                    if (u && p != null) used.Add(r); else { for (int k = 0; k < (vary ? 3 : 1); k++) sh.Clear(r, c + k); }
                }

                if (vary)
                {
                    double def = (double)p.Value;
                    var pts = new List<(int row, string ty, double st, double v)>();
                    foreach (int r in used)
                    {
                        (string ty, string v) o = default;
                        if (old != null) old.TryGetValue(r, out o);
                        sh.SetText(r, c + 1, o.ty ?? "");
                        if (double.TryParse(o.v ?? "", NumberStyles.Float, CI, out double vv)) sh.SetNumber(r, c + 2, vv); else sh.SetText(r, c + 2, o.v ?? "");
                        sh.AddList(Sheet.Ref(r, c + 1), "\"Start,Mid,End\"");
                        pts.Add((r, o.ty ?? "", sh.Number(r, 5), double.IsNaN(vv) ? 0 : vv));
                    }
                    if (used.Count > 0)
                    {
                        int r1 = used.First(), r2 = used.Last();
                        foreach (int r in used)
                            sh.SetArrayFormula(r, c, VaryFormula(Sheet.Col(c + 1), Sheet.Col(c + 2), r1, r2, def), VaryValue(pts, r, def));
                    }
                }
                else
                {
                    foreach (int r in used)
                    {
                        if (master != null)
                        {
                            sh.SetFormula(r, c, master, sh.GetText(g - 1, c));     // the diaphragm void is drawn too (it just does not cut)
                            sh.AddCheckList(Sheet.Ref(r, c));
                        }
                        else if (p.Value is double d) sh.SetNumber(r, c, d);
                        else sh.SetText(r, c, p.Value as string ?? "");
                    }
                }
                sh.SetWidth(c, Math.Max(10, Math.Min(28, (p?.Header.Length ?? 14) + 2)));
                if (vary) { sh.SetWidth(c + 1, 11); sh.SetWidth(c + 2, 9); sh.Outline(c + 1); sh.Outline(c + 2); }
            }
        }

        // ---------------------------------------------------------------- variation (worked out in Excel)
        // In the "variation" column the stations are marked Start / Mid / End and their values typed in "V.value".
        // Between a Start and the next End the value follows the stations:
        //   Start + End            = a straight line
        //   Start + one Mid + End  = a parabola through the three (Mid alone = half way between Start and End,
        //                            "Mid 0+305.5" = at that station)
        //   more Mids              = a smooth curve through all of them (monotone cubic: no overshoot)
        // Before the first Start = the Start value; after an End = that End value; no Start at all = the family value.

        private static string VaryFormula(string ty, string vv, int r1, int r2, double def)
        {
            string R(string col) => $"{col}${r1}:{col}${r2}";
            string f =
                "LET({ty}," + R(ty) + ",{st}," + R("$E") + ",{vv}," + R(vv) + "," +
                "{k},ROW()-" + r1 + "+1,{rn},ROW({ty})-" + r1 + "+1," +
                "{iS},LEFT({ty},5)=\"Start\",{iE},LEFT({ty},3)=\"End\"," +
                "{s},MAX(IF({iS}*({rn}<={k}),{rn},0))," +
                "{e},IF({s}=0,1E9,MIN(IF({iE}*({rn}>={s}),{rn},1E9)))," +
                "IF(AND({s}>0,{e}<1E9,{k}<={e})," +
                  "LET({sS},INDEX({st},{s}),{sE},INDEX({st},{e})," +
                  "{in},({rn}>={s})*({rn}<={e})*(LEN(TRIM({ty}))>0)," +
                  "{px},IF(LEFT({ty},3)=\"Mid\",IF(LEN(TRIM({ty}))<=3,({sS}+{sE})/2,--SUBSTITUTE(SUBSTITUTE(MID({ty},4,99),\"+\",\"\"),\" \",\"\")),{st})," +
                  "{xr},FILTER({px},{in}),{vr},FILTER(IFERROR(--{vv},0),{in})," +
                  "{xs},SORT({xr}),{vs},SORTBY({vr},{xr}),{n},ROWS({xs})," +
                  "{x},MIN(MAX(INDEX({st},{k}),INDEX({xs},1)),INDEX({xs},{n}))," +
                  "IF({n}=1,INDEX({vs},1)," +
                  "IF({n}=3," +
                    "LET({xa},INDEX({xs},1),{xb},INDEX({xs},2),{xc},INDEX({xs},3)," +
                    "IFERROR(INDEX({vs},1)*({x}-{xb})*({x}-{xc})/(({xa}-{xb})*({xa}-{xc}))+INDEX({vs},2)*({x}-{xa})*({x}-{xc})/(({xb}-{xa})*({xb}-{xc}))+INDEX({vs},3)*({x}-{xa})*({x}-{xb})/(({xc}-{xa})*({xc}-{xb}))," +
                    "INDEX({vs},1)+(INDEX({vs},3)-INDEX({vs},1))*({x}-{xa})/({xc}-{xa})))," +
                    "LET({i},MIN(MAX(MATCH({x},{xs},1),1),{n}-1)," +
                    "{h},INDEX({xs},{i}+1)-INDEX({xs},{i})," +
                    "{t},IF({h}=0,0,({x}-INDEX({xs},{i}))/{h})," +
                    "{d},IF({h}=0,0,(INDEX({vs},{i}+1)-INDEX({vs},{i}))/{h})," +
                    "{dp},IF({i}=1,{d},IFERROR((INDEX({vs},{i})-INDEX({vs},{i}-1))/(INDEX({xs},{i})-INDEX({xs},{i}-1)),{d}))," +
                    "{dn},IF({i}+1>={n},{d},IFERROR((INDEX({vs},{i}+2)-INDEX({vs},{i}+1))/(INDEX({xs},{i}+2)-INDEX({xs},{i}+1)),{d}))," +
                    "{ma},IF({i}=1,{d},IF({dp}*{d}<=0,0,2*{dp}*{d}/({dp}+{d})))," +
                    "{mb},IF({i}+1={n},{d},IF({d}*{dn}<=0,0,2*{d}*{dn}/({d}+{dn})))," +
                    "(2*{t}^3-3*{t}^2+1)*INDEX({vs},{i})+({t}^3-2*{t}^2+{t})*{h}*{ma}+(3*{t}^2-2*{t}^3)*INDEX({vs},{i}+1)+({t}^3-{t}^2)*{h}*{mb}))))," +
                  "LET({pe},MAX(IF({iE}*({rn}<{k}),{rn},0)),{fs},MIN(IF({iS},{rn},1E9))," +
                  "IF({pe}>0,IFERROR(--INDEX({vv},{pe}),0),IF({fs}<1E9,IFERROR(--INDEX({vv},{fs}),0)," + def.ToString("R", CI) + ")))))";
            f = Regex.Replace(f, @"\{(\w+)\}", "_xlpm.$1");
            return Regex.Replace(f, @"\b(LET|FILTER|SORTBY|SORT)\(", m =>
                (m.Groups[1].Value == "FILTER" || m.Groups[1].Value == "SORT" ? "_xlfn._xlws." : "_xlfn.") + m.Groups[1].Value + "(");
        }

        // the same sums in C#: the value shown until Excel works the sheet out
        private static double VaryValue(List<(int row, string ty, double st, double v)> rows, int row, double def)
        {
            int k = rows.FindIndex(x => x.row == row);
            if (k < 0) return def;
            bool IsS(int i) => rows[i].ty.StartsWith("Start", StringComparison.OrdinalIgnoreCase);
            bool IsE(int i) => rows[i].ty.StartsWith("End", StringComparison.OrdinalIgnoreCase);
            int s = -1;
            for (int i = 0; i <= k; i++) if (IsS(i)) s = i;
            int e = -1;
            if (s >= 0) for (int i = s; i < rows.Count; i++) if (IsE(i)) { e = i; break; }
            if (s >= 0 && e >= 0 && k <= e)
            {
                double sS = rows[s].st, sE = rows[e].st;
                var pts = new List<(double x, double v)>();
                for (int i = s; i <= e; i++)
                {
                    string ty = rows[i].ty.Trim();
                    if (ty.Length == 0) continue;
                    double x = rows[i].st;
                    if (ty.StartsWith("Mid", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ty.Length <= 3) x = (sS + sE) / 2;
                        else if (!double.TryParse(ty.Substring(3).Replace("+", "").Replace(" ", ""), NumberStyles.Float, CI, out x)) continue;
                    }
                    pts.Add((x, rows[i].v));
                }
                pts = pts.OrderBy(q => q.x).ToList();
                int n = pts.Count;
                double xc = Math.Min(Math.Max(rows[k].st, pts[0].x), pts[n - 1].x);
                if (n == 1) return pts[0].v;
                if (n == 3)
                {
                    double a = pts[0].x, b = pts[1].x, c = pts[2].x;
                    if (Math.Abs(a - b) > 1e-9 && Math.Abs(b - c) > 1e-9 && Math.Abs(a - c) > 1e-9)
                        return pts[0].v * (xc - b) * (xc - c) / ((a - b) * (a - c)) + pts[1].v * (xc - a) * (xc - c) / ((b - a) * (b - c))
                             + pts[2].v * (xc - a) * (xc - b) / ((c - a) * (c - b));
                }
                int j = 0;
                while (j < n - 2 && xc >= pts[j + 1].x) j++;
                double h = pts[j + 1].x - pts[j].x;
                if (Math.Abs(h) < 1e-12) return pts[j].v;
                double t = (xc - pts[j].x) / h, d = (pts[j + 1].v - pts[j].v) / h;
                double dp = j > 0 ? (pts[j].v - pts[j - 1].v) / (pts[j].x - pts[j - 1].x) : d;
                double dn = j + 2 < n ? (pts[j + 2].v - pts[j + 1].v) / (pts[j + 2].x - pts[j + 1].x) : d;
                double m0 = j == 0 ? d : (dp * d <= 0 ? 0 : 2 * dp * d / (dp + d));
                double m1 = j + 1 == n - 1 ? d : (d * dn <= 0 ? 0 : 2 * d * dn / (d + dn));
                return (2 * t * t * t - 3 * t * t + 1) * pts[j].v + (t * t * t - 2 * t * t + t) * h * m0
                     + (3 * t * t - 2 * t * t * t) * pts[j + 1].v + (t * t * t - t * t) * h * m1;
            }
            int pe = -1, fs = -1;
            for (int i = 0; i < k; i++) if (IsE(i)) pe = i;
            for (int i = 0; i < rows.Count; i++) if (IsS(i)) { fs = i; break; }
            if (pe >= 0) return rows[pe].v;
            if (fs >= 0) return rows[fs].v;
            return def;
        }

        private static string GroupName(string title)
        {
            int dash = (title ?? "").IndexOf('—');
            return dash >= 0 ? title.Substring(dash + 1).Trim() : "";
        }

        // Start / Mid / End and their values already typed in a group: parameter header -> row -> (type, value)
        private static Dictionary<string, Dictionary<int, (string ty, string v)>> CaptureVary(Sheet sh, int g, int last, int c1, int c2)
        {
            var keep = new Dictionary<string, Dictionary<int, (string ty, string v)>>(StringComparer.OrdinalIgnoreCase);
            for (int c = c1; c <= c2; c++)
            {
                string h = sh.GetText(g + 1, c).Trim();
                if (h.Length == 0 || h == VarHeader || h == VValHeader) continue;
                if (sh.GetText(g + 1, c + 1).Trim() != VarHeader) continue;
                var rows = new Dictionary<int, (string, string)>();
                for (int r = g + 2; r <= last; r++)
                {
                    string ty = sh.GetText(r, c + 1).Trim(), v = sh.GetText(r, c + 2).Trim();
                    if (ty.Length > 0 || v.Length > 0) rows[r] = (ty, v);
                }
                keep[h] = rows;
            }
            return keep;
        }

        // the grey "not used" look (conditional format): grey fill, grey text, value hidden
        private static int ShadeStyle(ZipArchive zip)
        {
            try
            {
                ZipArchiveEntry e = zip.GetEntry("xl/styles.xml");
                XDocument st = Load(zip, "xl/styles.xml");
                if (e == null || st == null) return -1;
                XElement dxfs = st.Root.Element(M + "dxfs");
                if (dxfs == null)
                {
                    dxfs = new XElement(M + "dxfs", new XAttribute("count", 0));
                    XElement after = st.Root.Element(M + "cellStyles") ?? st.Root.Element(M + "cellXfs");
                    after.AddAfterSelf(dxfs);
                }
                var list = dxfs.Elements(M + "dxf").ToList();
                for (int i = 0; i < list.Count; i++)
                    if ((string)list[i].Element(M + "numFmt")?.Attribute("formatCode") == ";;;") return i;
                dxfs.Add(new XElement(M + "dxf",
                    new XElement(M + "font", new XElement(M + "color", new XAttribute("rgb", "FFBFBFBF"))),
                    new XElement(M + "numFmt", new XAttribute("numFmtId", 190), new XAttribute("formatCode", ";;;")),
                    new XElement(M + "fill", new XElement(M + "patternFill", new XElement(M + "bgColor", new XAttribute("rgb", "FFEDEDED"))))));
                dxfs.SetAttributeValue("count", list.Count + 1);
                e.Delete();
                ZipArchiveEntry ne = zip.CreateEntry("xl/styles.xml", CompressionLevel.Optimal);
                using (Stream s = ne.Open())
                using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                    st.Save(w, SaveOptions.DisableFormatting);
                return list.Count;
            }
            catch { return -1; }
        }

        // ---------------------------------------------------------------- package helpers

        // Excel works every formula out again when the file is opened (the variation columns)
        private static void CalcOnLoad(ZipArchive zip)
        {
            ZipArchiveEntry e = zip.GetEntry("xl/workbook.xml");
            XDocument wb = Load(zip, "xl/workbook.xml");
            if (e == null || wb == null) return;
            XElement calc = wb.Root.Element(M + "calcPr");
            if (calc == null)
            {
                calc = new XElement(M + "calcPr");
                XElement after = wb.Root.Element(M + "definedNames") ?? wb.Root.Element(M + "sheets");
                if (after != null) after.AddAfterSelf(calc); else wb.Root.Add(calc);
            }
            calc.SetAttributeValue("fullCalcOnLoad", "1");
            e.Delete();
            ZipArchiveEntry ne = zip.CreateEntry("xl/workbook.xml", CompressionLevel.Optimal);
            using (Stream s = ne.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                wb.Save(w, SaveOptions.DisableFormatting);
        }

        private static string SheetPath(ZipArchive zip, string name)
        {
            XDocument wb = Load(zip, "xl/workbook.xml");
            XDocument rels = Load(zip, "xl/_rels/workbook.xml.rels");
            if (wb == null || rels == null) return null;
            XElement sh = wb.Descendants(M + "sheet").FirstOrDefault(e => string.Equals((string)e.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
            if (sh == null) return null;
            string rid = (string)sh.Attribute(R + "id");
            XElement rel = rels.Descendants(PR + "Relationship").FirstOrDefault(e => (string)e.Attribute("Id") == rid);
            if (rel == null) return null;
            string target = ((string)rel.Attribute("Target")).Replace('\\', '/');
            if (target.StartsWith("/")) return target.TrimStart('/');
            return "xl/" + target;
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

        // ---------------------------------------------------------------- sheet editing

        private sealed class Sheet
        {
            private readonly XDocument _doc;
            private readonly XElement _ws, _data;
            private readonly List<string> _shared;

            public Sheet(XDocument doc, List<string> shared)
            {
                _doc = doc;
                _ws = doc.Root;
                _data = _ws.Element(M + "sheetData");
                _shared = shared;
            }

            // ---- references
            private static readonly Regex RefRx = new Regex(@"^\$?([A-Z]+)\$?(\d+)$");

            public static int ColOf(string r)
            {
                Match m = RefRx.Match(r ?? "");
                if (!m.Success) return -1;
                int c = 0;
                foreach (char ch in m.Groups[1].Value) c = c * 26 + (ch - 'A' + 1);
                return c;
            }

            public static int RowOf(string r)
            {
                Match m = RefRx.Match(r ?? "");
                return m.Success ? int.Parse(m.Groups[2].Value, CI) : -1;
            }

            public static string Col(int c)
            {
                string s = "";
                while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; }
                return s;
            }

            public static string Ref(int r, int c) => Col(c) + r.ToString(CI);

            // ---- rows and cells
            private XElement Row(int r, bool create)
            {
                XElement row = _data.Elements(M + "row").FirstOrDefault(x => (int?)x.Attribute("r") == r);
                if (row != null || !create) return row;
                row = new XElement(M + "row", new XAttribute("r", r));
                XElement after = _data.Elements(M + "row").LastOrDefault(x => ((int?)x.Attribute("r") ?? 0) < r);
                if (after == null) _data.AddFirst(row); else after.AddAfterSelf(row);
                return row;
            }

            private XElement Cell(int r, int c, bool create)
            {
                XElement row = Row(r, create);
                if (row == null) return null;
                string rf = Ref(r, c);
                XElement cell = row.Elements(M + "c").FirstOrDefault(x => (string)x.Attribute("r") == rf);
                if (cell != null || !create) return cell;
                cell = new XElement(M + "c", new XAttribute("r", rf));
                XElement after = row.Elements(M + "c").LastOrDefault(x => ColOf((string)x.Attribute("r")) < c);
                if (after == null) row.AddFirst(cell); else after.AddAfterSelf(cell);
                return cell;
            }

            public string GetText(int r, int c)
            {
                XElement cell = Cell(r, c, false);
                if (cell == null) return "";
                string t = (string)cell.Attribute("t");
                if (t == "inlineStr") return string.Concat(cell.Descendants(M + "t").Select(x => x.Value));
                string v = (string)cell.Element(M + "v") ?? "";
                if (t == "s" && int.TryParse(v, NumberStyles.Integer, CI, out int i) && i >= 0 && i < _shared.Count) return _shared[i];
                return v;
            }

            public bool IsNumber(int r, int c)
            {
                XElement cell = Cell(r, c, false);
                if (cell == null) return false;
                string t = (string)cell.Attribute("t");
                if (t != null && t != "n") return false;
                return double.TryParse((string)cell.Element(M + "v") ?? "", NumberStyles.Float, CI, out _);
            }

            private static void Reset(XElement cell)
            {
                cell.Attribute("t")?.Remove();
                cell.Elements().Remove();
            }

            public void SetText(int r, int c, string text)
            {
                XElement cell = Cell(r, c, true);
                Reset(cell);
                if (string.IsNullOrEmpty(text)) return;
                cell.SetAttributeValue("t", "inlineStr");
                cell.Add(new XElement(M + "is", new XElement(M + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
            }

            public void SetNumber(int r, int c, double v)
            {
                XElement cell = Cell(r, c, true);
                Reset(cell);
                cell.Add(new XElement(M + "v", v.ToString("R", CI)));
            }

            // formula with its current text result (so it shows before Excel recalculates)
            public void SetFormula(int r, int c, string formula, string cachedText)
            {
                XElement cell = Cell(r, c, true);
                Reset(cell);
                cell.SetAttributeValue("t", "str");
                cell.Add(new XElement(M + "f", formula));
                cell.Add(new XElement(M + "v", cachedText ?? ""));
            }

            // array formula (works the whole Start/Mid/End columns out) with its current result
            public void SetArrayFormula(int r, int c, string formula, double cached)
            {
                XElement cell = Cell(r, c, true);
                Reset(cell);
                cell.Add(new XElement(M + "f", new XAttribute("t", "array"), new XAttribute("ref", Ref(r, c)), formula));
                if (!double.IsNaN(cached) && !double.IsInfinity(cached)) cell.Add(new XElement(M + "v", cached.ToString("R", CI)));
            }

            // a drop-down list on a cell; typing something else is allowed (e.g. "Mid 0+305.5")
            public void AddList(string cellRef, string formula1)
            {
                XElement dvs = _ws.Element(M + "dataValidations");
                if (dvs == null)
                {
                    dvs = new XElement(M + "dataValidations");
                    XElement anchor = _ws.Element(M + "conditionalFormatting") ?? _ws.Element(M + "mergeCells") ?? _data;
                    anchor.AddAfterSelf(dvs);
                }
                XElement dv = dvs.Elements(M + "dataValidation").FirstOrDefault(e => (string)e.Element(M + "formula1") == formula1);
                if (dv == null)
                {
                    dv = new XElement(M + "dataValidation", new XAttribute("type", "list"), new XAttribute("allowBlank", 1),
                                      new XAttribute("showErrorMessage", 0), new XAttribute("sqref", ""),
                                      new XElement(M + "formula1", formula1));
                    dvs.Add(dv);
                }
                var refs = ((string)dv.Attribute("sqref") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (!refs.Contains(cellRef)) refs.Add(cellRef);
                dv.SetAttributeValue("sqref", string.Join(" ", refs));
                dvs.SetAttributeValue("count", dvs.Elements(M + "dataValidation").Count());
            }

            // drop-down cells in columns c1..c2 (rows r1..r2) are forgotten before the columns are rebuilt
            public void RemoveValidations(int c1, int c2, int r1, int r2)
            {
                foreach (XElement dv in _ws.Descendants(M + "dataValidation").ToList())
                {
                    var refs = ((string)dv.Attribute("sqref") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(x => { string a = x.Split(':')[0]; int c = ColOf(a), r = RowOf(a); return !(c >= c1 && c <= c2 && r >= r1 && r <= r2); }).ToList();
                    if (refs.Count == 0) dv.Remove(); else dv.SetAttributeValue("sqref", string.Join(" ", refs));
                }
                XElement dvs = _ws.Element(M + "dataValidations");
                if (dvs != null) { int n = dvs.Elements(M + "dataValidation").Count(); if (n == 0) dvs.Remove(); else dvs.SetAttributeValue("count", n); }
            }

            // columns folded under the value column (the + / - button above them)
            private readonly HashSet<int> _outline = new HashSet<int>();
            public void Outline(int c) => _outline.Add(c);

            public string GetStyle(int r, int c) => (string)Cell(r, c, false)?.Attribute("s");
            public void SetStyle(int r, int c, string s) => Cell(r, c, true).SetAttributeValue("s", s);

            // last column of the STATIONS title on the group row (5 = E when not found)
            public int StationsEnd(int g)
            {
                XElement mc = Merges(false);
                foreach (XElement m in mc?.Elements(M + "mergeCell") ?? Enumerable.Empty<XElement>())
                {
                    string[] p = ((string)m.Attribute("ref") ?? "").Split(':');
                    if (p.Length == 2 && RowOf(p[0]) == g && GetText(g, ColOf(p[0])).Trim().StartsWith("STATIONS", StringComparison.OrdinalIgnoreCase))
                        return ColOf(p[1]);
                }
                return 5;
            }

            // last column of the merged title on row r that starts at c
            public int MergeEnd(int r, int c)
            {
                XElement mc = Merges(false);
                foreach (XElement m in mc?.Elements(M + "mergeCell") ?? Enumerable.Empty<XElement>())
                {
                    string[] p = ((string)m.Attribute("ref") ?? "").Split(':');
                    if (p.Length == 2 && RowOf(p[0]) == r && ColOf(p[0]) == c) return ColOf(p[1]);
                }
                return c;
            }

            // conditional formats lying inside columns c1..c2 are dropped
            public void RemoveConditional(int c1, int c2)
            {
                foreach (XElement cf in _ws.Elements(M + "conditionalFormatting").ToList())
                {
                    var keep = ((string)cf.Attribute("sqref") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(x => { string[] p = x.Split(':'); int a = ColOf(p[0]), b = ColOf(p[p.Length - 1]); return !(a >= c1 && b <= c2); }).ToList();
                    if (keep.Count == 0) cf.Remove(); else cf.SetAttributeValue("sqref", string.Join(" ", keep));
                }
            }

            // a group with Type Corbel / Outrigger: the rows it does not use turn grey and blank
            public void AddShading(int c1, int c2, int g, int last, int dxf)
            {
                int r0 = g + 2;
                string t = "$" + Col(c1) + "$" + (g - 1).ToString(CI);
                string C(int r) => "$C" + r.ToString(CI);
                string f = $"AND({t}<>\"Deck\",{t}<>\"\",NOT(IF({t}=\"Corbel\",OR({C(r0)}=\"Corbel\",{C(r0 - 1)}=\"Corbel\",{C(r0 + 1)}=\"Corbel\")," +
                           $"OR({C(r0)}=\"Axis\",AND({C(r0)}=\"Diaphragm face\",OR({C(r0 - 1)}=\"Axis\",{C(r0 + 1)}=\"Axis\"))))))";
                int prio = _ws.Descendants(M + "cfRule").Select(x => (int?)x.Attribute("priority") ?? 0).DefaultIfEmpty(0).Max() + 1;
                var cf = new XElement(M + "conditionalFormatting", new XAttribute("sqref", Ref(r0, c1) + ":" + Ref(last, c2)),
                    new XElement(M + "cfRule", new XAttribute("type", "expression"), new XAttribute("dxfId", dxf), new XAttribute("priority", prio),
                        new XElement(M + "formula", f)));
                XElement lastCf = _ws.Elements(M + "conditionalFormatting").LastOrDefault();
                XElement anchor = lastCf ?? _ws.Element(M + "phoneticPr") ?? _ws.Element(M + "mergeCells");
                if (anchor != null) anchor.AddAfterSelf(cf);
                else
                {
                    XElement before = _ws.Element(M + "dataValidations") ?? _ws.Element(M + "hyperlinks") ?? _ws.Element(M + "printOptions") ?? _ws.Element(M + "pageMargins");
                    if (before != null) before.AddBeforeSelf(cf); else _data.AddAfterSelf(cf);
                }
            }

            public void Remove(int r, int c) => Cell(r, c, false)?.Remove();

            public void CopyStyle(int fr, int fc, int tr, int tc)
            {
                string s = (string)Cell(fr, fc, false)?.Attribute("s");
                if (s != null) Cell(tr, tc, true).SetAttributeValue("s", s);
            }

            // adds a cell to the ☑ / ☐ drop-down list (the one Civil made for the Place column)
            public void AddCheckList(string cellRef)
            {
                XElement dvs = _ws.Element(M + "dataValidations");
                XElement dv = dvs?.Elements(M + "dataValidation").FirstOrDefault(e => ((string)e.Element(M + "formula1") ?? "").Contains("☑"));
                if (dv == null)
                {
                    if (dvs == null)
                    {
                        dvs = new XElement(M + "dataValidations");
                        XElement anchor = _ws.Element(M + "conditionalFormatting") ?? _ws.Element(M + "mergeCells") ?? _data;
                        anchor.AddAfterSelf(dvs);
                    }
                    dv = new XElement(M + "dataValidation", new XAttribute("type", "list"), new XAttribute("allowBlank", 1),
                                      new XAttribute("showErrorMessage", 1), new XAttribute("sqref", ""),
                                      new XElement(M + "formula1", "\"☑,☐\""));
                    dvs.Add(dv);
                }
                var refs = ((string)dv.Attribute("sqref") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (!refs.Contains(cellRef)) refs.Add(cellRef);
                dv.SetAttributeValue("sqref", string.Join(" ", refs));
                dvs.SetAttributeValue("count", dvs.Elements(M + "dataValidation").Count());
            }

            public void Clear(int r, int c)
            {
                XElement cell = Cell(r, c, false);
                if (cell != null) Reset(cell);
            }

            public int LastRow() => _data.Elements(M + "row").Select(x => (int?)x.Attribute("r") ?? 0).DefaultIfEmpty(0).Max();

            public int FindRow(int c, string text)
            {
                foreach (XElement row in _data.Elements(M + "row"))
                {
                    int r = (int?)row.Attribute("r") ?? 0;
                    if (string.Equals(GetText(r, c).Trim(), text, StringComparison.OrdinalIgnoreCase)) return r;
                }
                return -1;
            }

            // ---- merges
            private XElement Merges(bool create)
            {
                XElement m = _ws.Element(M + "mergeCells");
                if (m == null && create)
                {
                    m = new XElement(M + "mergeCells");
                    _data.AddAfterSelf(m);
                }
                return m;
            }

            // every merged title on the group row after the STATIONS group, left to right
            public List<(int start, int end)> FamilyGroups(int g)
            {
                var res = new List<(int, int)>();
                XElement mc = Merges(false);
                if (mc == null) return res;
                foreach (XElement m in mc.Elements(M + "mergeCell"))
                {
                    string[] p = ((string)m.Attribute("ref") ?? "").Split(':');
                    if (p.Length != 2 || RowOf(p[0]) != g || RowOf(p[1]) != g) continue;
                    int c1 = ColOf(p[0]), c2 = ColOf(p[1]);
                    string t = GetText(g, c1).Trim();
                    if (t.StartsWith("STATIONS", StringComparison.OrdinalIgnoreCase)) continue;
                    res.Add((c1, c2));
                }
                return res.OrderBy(x => x.Item1).ToList();
            }

            // fixed columns of a group = its leading Point / Instance / Pt / Place headers
            public int FixedColumns(int g, int start, int end)
            {
                int n = 0;
                for (int c = start; c <= end; c++)
                {
                    string h = GetText(g + 1, c).Trim();
                    if (h == "Point" || h == "Instance" || h == "Pt" || h == "Place") n++;
                    else break;
                }
                return Math.Max(1, Math.Min(n, end - start + 1));
            }

            public double Number(int r, int c)
            {
                XElement cell = Cell(r, c, false);
                double.TryParse((string)cell?.Element(M + "v") ?? "", NumberStyles.Float, CI, out double v);
                return v;
            }

            public void AddColumns(int at, int n, int styleFrom, int g, int last) => InsertColumns(at, n, styleFrom, g, last);

            // group whose title (on row g) starts with "<key> FAMILY"
            public bool FindGroup(int g, string key, out int start, out int end)
            {
                start = end = -1;
                XElement mc = Merges(false);
                if (mc == null) return false;
                foreach (XElement m in mc.Elements(M + "mergeCell"))
                {
                    string[] p = ((string)m.Attribute("ref") ?? "").Split(':');
                    if (p.Length != 2 || RowOf(p[0]) != g) continue;
                    int c1 = ColOf(p[0]), c2 = ColOf(p[1]);
                    if (GetText(g, c1).Trim().StartsWith(key + " FAMILY", StringComparison.OrdinalIgnoreCase))
                    {
                        start = c1; end = c2;
                        return true;
                    }
                }
                return false;
            }

            public void SetMerge(int r, int c1, int c2)
            {
                XElement mc = Merges(true);
                // drop any merge on this row that starts at c1
                foreach (XElement m in mc.Elements(M + "mergeCell").ToList())
                {
                    string[] p = ((string)m.Attribute("ref") ?? "").Split(':');
                    if (p.Length == 2 && RowOf(p[0]) == r && ColOf(p[0]) == c1) m.Remove();
                }
                if (c2 > c1) mc.Add(new XElement(M + "mergeCell", new XAttribute("ref", Ref(r, c1) + ":" + Ref(r, c2))));
            }

            // ---- column insert / delete (cells, merges, validations, widths)
            public void InsertColumns(int at, int n, int styleFrom, int g, int last)
            {
                Shift(at, n);
                // new cells take the style of the column on their left
                for (int r = g; r <= last; r++)
                {
                    XElement src = Cell(r, styleFrom, false);
                    string s = (string)src?.Attribute("s");
                    if (s == null) continue;
                    for (int k = 0; k < n; k++) Cell(r, at + k, true).SetAttributeValue("s", s);
                }
            }

            public void DeleteColumns(int at, int n)
            {
                // merges: inside the deleted columns = gone; reaching into them = cut back
                XElement mcs = Merges(false);
                if (mcs != null)
                    foreach (XElement m in mcs.Elements(M + "mergeCell").ToList())
                    {
                        string[] p = ((string)m.Attribute("ref") ?? "").Split(':');
                        if (p.Length != 2) continue;
                        int r1 = RowOf(p[0]), r2 = RowOf(p[1]), c1 = ColOf(p[0]), c2 = ColOf(p[1]), d2 = at + n - 1;
                        if (c2 < at || c1 > d2) continue;
                        if (c1 >= at && c2 <= d2) { m.Remove(); continue; }
                        if (c1 < at && c2 <= d2) c2 = at - 1;
                        else if (c1 >= at) c1 = d2 + 1;
                        if (c1 == c2 && r1 == r2) { m.Remove(); continue; }
                        m.SetAttributeValue("ref", Ref(r1, c1) + ":" + Ref(r2, c2));
                    }
                foreach (XElement cell in _data.Descendants(M + "c").ToList())
                {
                    int c = ColOf((string)cell.Attribute("r"));
                    if (c >= at && c < at + n) cell.Remove();
                }
                Shift(at + n, -n);
            }

            // every column >= from moves by delta
            private void Shift(int from, int delta)
            {
                foreach (XElement cell in _data.Descendants(M + "c"))
                {
                    string rf = (string)cell.Attribute("r");
                    int c = ColOf(rf);
                    if (c >= from) cell.SetAttributeValue("r", Ref(RowOf(rf), c + delta));
                }
                foreach (XElement row in _data.Elements(M + "row")) row.Attribute("spans")?.Remove();

                // formulas follow their cells: the references in the text and the array range move too
                foreach (XElement f in _data.Descendants(M + "f"))
                {
                    if (f.Attribute("ref") != null) f.SetAttributeValue("ref", ShiftRange((string)f.Attribute("ref"), from, delta));
                    if (!string.IsNullOrEmpty(f.Value)) f.Value = ShiftFormula(f.Value, from, delta);
                }

                XElement mc = Merges(false);
                if (mc != null)
                    foreach (XElement m in mc.Elements(M + "mergeCell"))
                        m.SetAttributeValue("ref", ShiftRange((string)m.Attribute("ref"), from, delta));

                foreach (XElement dv in _ws.Descendants(M + "dataValidation"))
                {
                    string sq = (string)dv.Attribute("sqref") ?? "";
                    dv.SetAttributeValue("sqref", string.Join(" ", sq.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => ShiftRange(x, from, delta))));
                }
                foreach (XElement cf in _ws.Elements(M + "conditionalFormatting"))
                {
                    string sq = (string)cf.Attribute("sqref") ?? "";
                    cf.SetAttributeValue("sqref", string.Join(" ", sq.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => ShiftRange(x, from, delta))));
                }

                // column widths
                var widths = Widths();
                var moved = new SortedDictionary<int, double>();
                foreach (var kv in widths)
                {
                    if (kv.Key >= from) moved[kv.Key + delta] = kv.Value;
                    else if (delta < 0 && kv.Key >= from + delta) continue;    // deleted
                    else moved[kv.Key] = kv.Value;
                }
                SaveWidths(moved);
            }

            // A1 references in a formula (not inside "text", not part of a name or a number like 1E9)
            private static readonly Regex FRefRx = new Regex(@"(?<![A-Za-z0-9_.])(\$?)([A-Z]{1,3})(\$?)(\d+)(?![A-Za-z0-9_(])");
            private static string ShiftFormula(string f, int from, int delta)
            {
                var sb = new StringBuilder();
                string[] parts = f.Split('"');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) sb.Append('"');
                    if (i % 2 == 1) { sb.Append(parts[i]); continue; }      // inside a "text"
                    sb.Append(FRefRx.Replace(parts[i], m =>
                    {
                        int c = ColOf(m.Groups[2].Value + "1");
                        if (c < from) return m.Value;
                        return m.Groups[1].Value + Col(c + delta) + m.Groups[3].Value + m.Groups[4].Value;
                    }));
                }
                return sb.ToString();
            }

            private static string ShiftRange(string range, int from, int delta)
            {
                if (string.IsNullOrEmpty(range)) return range;
                return string.Join(":", range.Split(':').Select(p =>
                {
                    int c = ColOf(p), r = RowOf(p);
                    if (c < 0) return p;
                    return c >= from ? Ref(r, c + delta) : p;
                }));
            }

            private SortedDictionary<int, double> Widths()
            {
                var w = new SortedDictionary<int, double>();
                XElement cols = _ws.Element(M + "cols");
                if (cols == null) return w;
                foreach (XElement col in cols.Elements(M + "col"))
                {
                    int min = (int?)col.Attribute("min") ?? 0, max = (int?)col.Attribute("max") ?? 0;
                    if (!double.TryParse((string)col.Attribute("width") ?? "", NumberStyles.Float, CI, out double width)) continue;
                    for (int c = min; c <= max && c - min < 200; c++) w[c] = width;
                }
                return w;
            }

            private void SaveWidths(SortedDictionary<int, double> w)
            {
                XElement cols = _ws.Element(M + "cols");
                if (cols == null)
                {
                    if (w.Count == 0) return;
                    cols = new XElement(M + "cols");
                    _data.AddBeforeSelf(cols);
                }
                cols.RemoveNodes();
                foreach (var kv in w)
                    cols.Add(new XElement(M + "col", new XAttribute("min", kv.Key), new XAttribute("max", kv.Key),
                                          new XAttribute("width", kv.Value.ToString(CI)), new XAttribute("customWidth", 1)));
            }

            public void SetWidth(int c, double width)
            {
                var w = Widths();
                w[c] = width;
                SaveWidths(w);
            }

            // cells in column order, no stale dimension
            public void Finish()
            {
                foreach (XElement row in _data.Elements(M + "row"))
                {
                    var cells = row.Elements(M + "c").OrderBy(x => ColOf((string)x.Attribute("r"))).ToList();
                    row.Elements(M + "c").Remove();
                    row.Add(cells);
                }
                _ws.Element(M + "dimension")?.Remove();
                XElement mcAll = _ws.Element(M + "mergeCells");
                if (mcAll != null)
                {
                    int nm = mcAll.Elements(M + "mergeCell").Count();
                    if (nm == 0) mcAll.Remove(); else mcAll.SetAttributeValue("count", nm);
                }

                // variation / V.value columns: folded (outline level 1, hidden); the button sits on the value column
                XElement cols = _ws.Element(M + "cols");
                if (cols != null)
                    foreach (XElement col in cols.Elements(M + "col"))
                    {
                        int min = (int?)col.Attribute("min") ?? 0;
                        if (_outline.Contains(min)) { col.SetAttributeValue("outlineLevel", 1); col.SetAttributeValue("hidden", 1); }
                    }
                if (_outline.Count > 0)
                {
                    XElement fmt = _ws.Element(M + "sheetFormatPr");
                    if (fmt == null) { fmt = new XElement(M + "sheetFormatPr", new XAttribute("defaultRowHeight", "15")); (_ws.Element(M + "cols") ?? _data).AddBeforeSelf(fmt); }
                    fmt.SetAttributeValue("outlineLevelCol", 1);
                    XElement pr = _ws.Element(M + "sheetPr");
                    if (pr == null) { pr = new XElement(M + "sheetPr"); _ws.AddFirst(pr); }
                    XElement op = pr.Element(M + "outlinePr");
                    if (op == null) { op = new XElement(M + "outlinePr"); XElement pg = pr.Element(M + "pageSetUpPr"); if (pg != null) pg.AddBeforeSelf(op); else pr.Add(op); }
                    op.SetAttributeValue("summaryRight", 0);
                }
            }
        }
    }
}
