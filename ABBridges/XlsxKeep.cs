// AB Bridges - keeps a sheet that Revit wrote (e.g. "Tendon Profile") when the Excel is generated again:
// values, fill / bold / font colour, column widths and merges are copied into a new XSheet.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace ABBridges
{
    internal static class XlsxKeep
    {
        private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        public static XSheet Read(string path, string name)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    XDocument wb = Load(zip, "xl/workbook.xml"), rels = Load(zip, "xl/_rels/workbook.xml.rels");
                    XElement sh = wb?.Descendants(M + "sheet").FirstOrDefault(e => string.Equals((string)e.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
                    if (sh == null || rels == null) return null;
                    string rid = (string)sh.Attribute(R + "id");
                    string target = ((string)rels.Descendants(PR + "Relationship").First(e => (string)e.Attribute("Id") == rid).Attribute("Target")).Replace('\\', '/');
                    XDocument doc = Load(zip, target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target);
                    if (doc == null) return null;

                    var shared = new List<string>();
                    XDocument ss = Load(zip, "xl/sharedStrings.xml");
                    if (ss != null) foreach (XElement si in ss.Root.Elements(M + "si")) shared.Add(string.Concat(si.Descendants(M + "t").Select(t => t.Value)));
                    List<XStyle> styles = Styles(Load(zip, "xl/styles.xml"));

                    var res = new XSheet((string)sh.Attribute("name")) { Landscape = false };
                    foreach (XElement c in doc.Descendants(M + "c"))
                    {
                        string rf = (string)c.Attribute("r");
                        (int row, int col) = Pos(rf);
                        if (col < 1) continue;
                        string t = (string)c.Attribute("t");
                        object v;
                        if (t == "inlineStr") v = string.Concat(c.Descendants(M + "t").Select(x => x.Value));
                        else
                        {
                            string s = (string)c.Element(M + "v");
                            if (s == null) v = null;
                            else if (t == "s") v = int.TryParse(s, out int i) && i >= 0 && i < shared.Count ? shared[i] : "";
                            else if (t == "str" || t == "b" || t == "e") v = s;
                            else v = double.TryParse(s, NumberStyles.Float, CI, out double d) ? (object)d : s;
                        }
                        int si2 = int.TryParse((string)c.Attribute("s"), out int k) ? k : 0;
                        XStyle st = si2 >= 0 && si2 < styles.Count ? styles[si2] : new XStyle { Border = false };
                        res.Set(row, col, v is string str && str.Length == 0 ? null : v, st);
                    }
                    foreach (XElement col in doc.Descendants(M + "col"))
                        if (int.TryParse((string)col.Attribute("min"), out int a) && int.TryParse((string)col.Attribute("max"), out int b) &&
                            double.TryParse((string)col.Attribute("width"), NumberStyles.Float, CI, out double w))
                            for (int i = a; i <= Math.Min(b, a + 200); i++) res.Widths[i] = w;
                    foreach (XElement m in doc.Descendants(M + "mergeCell"))
                    {
                        string[] p = ((string)m.Attribute("ref") ?? "").Split(':');
                        if (p.Length != 2) continue;
                        var (r1, c1) = Pos(p[0]); var (r2, c2) = Pos(p[1]);
                        if (c1 > 0 && c2 > 0) res.Merges.Add((r1, c1, r2, c2));
                    }
                    return res.Rows.Count > 0 ? res : null;
                }
            }
            catch { return null; }
        }

        private static List<XStyle> Styles(XDocument st)
        {
            var res = new List<XStyle>();
            if (st == null) return res;
            XElement root = st.Root;
            var fonts = root.Element(M + "fonts")?.Elements(M + "font").ToList() ?? new List<XElement>();
            var fills = root.Element(M + "fills")?.Elements(M + "fill").ToList() ?? new List<XElement>();
            var borders = root.Element(M + "borders")?.Elements(M + "border").ToList() ?? new List<XElement>();
            foreach (XElement xf in root.Element(M + "cellXfs")?.Elements(M + "xf") ?? Enumerable.Empty<XElement>())
            {
                var s = new XStyle { Border = false };
                int fo = (int?)xf.Attribute("fontId") ?? 0, fi = (int?)xf.Attribute("fillId") ?? 0, bo = (int?)xf.Attribute("borderId") ?? 0;
                if (fo < fonts.Count)
                {
                    XElement f = fonts[fo];
                    s.Bold = f.Element(M + "b") != null;
                    string rgb = (string)f.Element(M + "color")?.Attribute("rgb");
                    if (rgb != null && rgb.Length >= 6) s.Color = rgb.Substring(rgb.Length - 6);
                    if (double.TryParse((string)f.Element(M + "sz")?.Attribute("val"), NumberStyles.Float, CI, out double sz)) s.Size = sz;
                }
                if (fi < fills.Count)
                {
                    string rgb = (string)fills[fi].Descendants(M + "fgColor").FirstOrDefault()?.Attribute("rgb");
                    if (rgb != null && rgb.Length >= 6) s.Fill = rgb.Substring(rgb.Length - 6);
                }
                if (bo < borders.Count) s.Border = borders[bo].Elements().Any(e => e.Attribute("style") != null);
                XElement al = xf.Element(M + "alignment");
                if (al != null) { s.HAlign = (string)al.Attribute("horizontal"); s.VCenter = (string)al.Attribute("vertical") == "center"; }
                res.Add(s);
            }
            return res;
        }

        private static (int row, int col) Pos(string rf)
        {
            if (string.IsNullOrEmpty(rf)) return (0, 0);
            int i = 0, col = 0;
            while (i < rf.Length && char.IsLetter(rf[i])) { col = col * 26 + (char.ToUpperInvariant(rf[i]) - 'A' + 1); i++; }
            int.TryParse(rf.Substring(i), out int row);
            return (row, col);
        }

        private static XDocument Load(ZipArchive zip, string name)
        {
            ZipArchiveEntry e = zip.GetEntry(name);
            if (e == null) return null;
            using (var s = e.Open()) return XDocument.Load(s);
        }
    }
}
