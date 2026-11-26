// AB Bridges - small .xlsx writer (no external packages): styles, merges, formulas,
// list validation (☑ / ☐), landscape fit-to-width, all borders.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace ABBridges
{
    internal sealed class XStyle
    {
        public bool Bold;
        public string Color = "1F2937";
        public double Size = 11;
        public string Fill;                // RRGGBB or null
        public string NumFmt;              // e.g. "0.000"
        public string HAlign;              // left / center / right
        public bool VCenter;
        public int Indent;
        public bool ThickLeft;
        public bool Border = true;
        public string BorderColor = "808080";
        public string LeftColor;           // colour of the ThickLeft bar (null = navy)

        public XStyle Copy() => (XStyle)MemberwiseClone();

        public string Key =>
            $"{Bold}|{Color}|{Size}|{Fill}|{NumFmt}|{HAlign}|{VCenter}|{Indent}|{ThickLeft}|{Border}|{BorderColor}|{LeftColor}";
    }

    internal sealed class XCell
    {
        public object Value;               // string, double, int or XFormula
        public XStyle Style;
    }

    internal sealed class XFormula
    {
        public string Text;                // without the leading '='
        public XFormula(string text) { Text = text.TrimStart('='); }
    }

    internal sealed class XSheet
    {
        public string Name;
        public readonly SortedDictionary<int, SortedDictionary<int, XCell>> Rows = new SortedDictionary<int, SortedDictionary<int, XCell>>();
        public readonly List<(int r1, int c1, int r2, int c2)> Merges = new List<(int, int, int, int)>();
        public readonly Dictionary<int, double> Widths = new Dictionary<int, double>();
        public readonly Dictionary<int, double> Heights = new Dictionary<int, double>();
        public readonly List<string> CheckCells = new List<string>();     // cells with the ☑/☐ list
        public bool Landscape = true;

        public XSheet(string name) { Name = name; }

        // row / col are 1-based
        public XCell Set(int r, int c, object v, XStyle st)
        {
            if (!Rows.TryGetValue(r, out var row)) { row = new SortedDictionary<int, XCell>(); Rows[r] = row; }
            var cell = new XCell { Value = v, Style = st };
            row[c] = cell;
            return cell;
        }

        // merged range; every cell gets the style so the borders show
        public void Merge(int r1, int c1, int r2, int c2, object v, XStyle st)
        {
            for (int r = r1; r <= r2; r++)
                for (int c = c1; c <= c2; c++)
                    Set(r, c, r == r1 && c == c1 ? v : null, st);
            if (r2 > r1 || c2 > c1) Merges.Add((r1, c1, r2, c2));
        }

        public static string Col(int c)
        {
            string s = "";
            while (c > 0)
            {
                int m = (c - 1) % 26;
                s = (char)('A' + m) + s;
                c = (c - 1) / 26;
            }
            return s;
        }

        public static string Ref(int r, int c) => Col(c) + r.ToString(CultureInfo.InvariantCulture);
    }

    internal static class XlsxBook
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        public static void Write(string path, List<XSheet> sheets)
        {
            // collect styles
            var styles = new List<XStyle> { new XStyle { Border = false } };
            var index = new Dictionary<string, int> { { styles[0].Key, 0 } };
            foreach (XSheet sh in sheets)
                foreach (var row in sh.Rows.Values)
                    foreach (XCell c in row.Values)
                    {
                        if (c.Style == null) continue;
                        if (!index.ContainsKey(c.Style.Key)) { index[c.Style.Key] = styles.Count; styles.Add(c.Style); }
                    }

            // the company sensitivity label (Public): taken from the file being replaced, else the Public label
            string custom = LabelOf(path) ?? PublicLabel();
            string tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var fs = new FileStream(tmp, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Add(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
                Add(zip, "_rels/.rels", RootRels());
                Add(zip, "docProps/custom.xml", custom);
                Add(zip, "xl/workbook.xml", Workbook(sheets));
                Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
                Add(zip, "xl/styles.xml", Styles(styles));
                for (int i = 0; i < sheets.Count; i++)
                    Add(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i], index, i == 0));
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ---------------------------------------------------------------- sheet

        private static string Sheet(XSheet sh, Dictionary<string, int> index, bool selected)
        {
            var sb = new StringBuilder();
            sb.Append(Head);
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sb.Append($"<sheetViews><sheetView showGridLines=\"0\"{(selected ? " tabSelected=\"1\"" : "")} workbookViewId=\"0\"/></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");
            if (sh.Widths.Count > 0)
            {
                sb.Append("<cols>");
                foreach (var kv in sh.Widths.OrderBy(k => k.Key))
                    sb.Append($"<col min=\"{kv.Key}\" max=\"{kv.Key}\" width=\"{kv.Value.ToString(CI)}\" customWidth=\"1\"/>");
                sb.Append("</cols>");
            }
            sb.Append("<sheetData>");
            foreach (var rkv in sh.Rows)
            {
                int r = rkv.Key;
                sb.Append($"<row r=\"{r}\"");
                if (sh.Heights.TryGetValue(r, out double h)) sb.Append($" ht=\"{h.ToString(CI)}\" customHeight=\"1\"");
                sb.Append(">");
                foreach (var ckv in rkv.Value)
                {
                    string refc = XSheet.Ref(r, ckv.Key);
                    XCell c = ckv.Value;
                    int s = c.Style == null ? 0 : index[c.Style.Key];
                    string sAttr = s != 0 ? $" s=\"{s}\"" : "";
                    switch (c.Value)
                    {
                        case null:
                            sb.Append($"<c r=\"{refc}\"{sAttr}/>");
                            break;
                        case string str:
                            sb.Append($"<c r=\"{refc}\"{sAttr} t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(str)}</t></is></c>");
                            break;
                        case XFormula f:
                            sb.Append($"<c r=\"{refc}\"{sAttr}><f>{SecurityElement.Escape(f.Text)}</f></c>");
                            break;
                        default:
                            double v = Convert.ToDouble(c.Value, CI);
                            if (double.IsNaN(v) || double.IsInfinity(v)) sb.Append($"<c r=\"{refc}\"{sAttr}/>");
                            else sb.Append($"<c r=\"{refc}\"{sAttr}><v>{Math.Round(v, 6).ToString("R", CI)}</v></c>");
                            break;
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");

            if (sh.Merges.Count > 0)
            {
                sb.Append($"<mergeCells count=\"{sh.Merges.Count}\">");
                foreach (var m in sh.Merges)
                    sb.Append($"<mergeCell ref=\"{XSheet.Ref(m.r1, m.c1)}:{XSheet.Ref(m.r2, m.c2)}\"/>");
                sb.Append("</mergeCells>");
            }
            if (sh.CheckCells.Count > 0)
            {
                sb.Append("<dataValidations count=\"1\">");
                sb.Append($"<dataValidation type=\"list\" allowBlank=\"1\" showInputMessage=\"1\" showErrorMessage=\"1\" sqref=\"{string.Join(" ", sh.CheckCells)}\">");
                sb.Append("<formula1>\"☑,☐\"</formula1></dataValidation></dataValidations>");
            }
            sb.Append("<pageMargins left=\"0.4\" right=\"0.4\" top=\"0.5\" bottom=\"0.5\" header=\"0.3\" footer=\"0.3\"/>");
            sb.Append($"<pageSetup paperSize=\"9\" orientation=\"{(sh.Landscape ? "landscape" : "portrait")}\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- styles

        private static string Styles(List<XStyle> styles)
        {
            var numFmts = new List<string>();
            var fonts = new List<string>();
            var fills = new List<string> { "<fill><patternFill patternType=\"none\"/></fill>", "<fill><patternFill patternType=\"gray125\"/></fill>" };
            var borders = new List<string> { "<border><left/><right/><top/><bottom/><diagonal/></border>" };
            var xfs = new StringBuilder();

            foreach (XStyle s in styles)
            {
                int numId = 0;
                if (!string.IsNullOrEmpty(s.NumFmt))
                {
                    int k = numFmts.IndexOf(s.NumFmt);
                    if (k < 0) { numFmts.Add(s.NumFmt); k = numFmts.Count - 1; }
                    numId = 164 + k;
                }

                string font = $"<font>{(s.Bold ? "<b/>" : "")}<sz val=\"{s.Size.ToString(CI)}\"/><color rgb=\"FF{s.Color}\"/><name val=\"Calibri\"/><family val=\"2\"/></font>";
                int fontId = fonts.IndexOf(font);
                if (fontId < 0) { fonts.Add(font); fontId = fonts.Count - 1; }

                int fillId = 0;
                if (!string.IsNullOrEmpty(s.Fill))
                {
                    string fill = $"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF{s.Fill}\"/><bgColor indexed=\"64\"/></patternFill></fill>";
                    fillId = fills.IndexOf(fill);
                    if (fillId < 0) { fills.Add(fill); fillId = fills.Count - 1; }
                }

                int borderId = 0;
                if (s.Border)
                {
                    string thin = $"style=\"thin\"><color rgb=\"FF{s.BorderColor}\"/>";
                    string left = s.ThickLeft ? $"style=\"medium\"><color rgb=\"FF{s.LeftColor ?? "1F3A5F"}\"/>" : thin;
                    string border = $"<border><left {left}</left><right {thin}</right><top {thin}</top><bottom {thin}</bottom><diagonal/></border>";
                    borderId = borders.IndexOf(border);
                    if (borderId < 0) { borders.Add(border); borderId = borders.Count - 1; }
                }

                xfs.Append($"<xf numFmtId=\"{numId}\" fontId=\"{fontId}\" fillId=\"{fillId}\" borderId=\"{borderId}\" xfId=\"0\"");
                if (numId != 0) xfs.Append(" applyNumberFormat=\"1\"");
                xfs.Append(" applyFont=\"1\"");
                if (fillId != 0) xfs.Append(" applyFill=\"1\"");
                if (borderId != 0) xfs.Append(" applyBorder=\"1\"");
                bool align = s.HAlign != null || s.VCenter || s.Indent > 0;
                if (align)
                {
                    xfs.Append(" applyAlignment=\"1\"><alignment");
                    if (s.HAlign != null) xfs.Append($" horizontal=\"{s.HAlign}\"");
                    if (s.VCenter) xfs.Append(" vertical=\"center\"");
                    if (s.Indent > 0) xfs.Append($" indent=\"{s.Indent}\"");
                    xfs.Append("/></xf>");
                }
                else xfs.Append("/>");
            }

            var sb = new StringBuilder();
            sb.Append(Head);
            sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (numFmts.Count > 0)
            {
                sb.Append($"<numFmts count=\"{numFmts.Count}\">");
                for (int i = 0; i < numFmts.Count; i++)
                    sb.Append($"<numFmt numFmtId=\"{164 + i}\" formatCode=\"{SecurityElement.Escape(numFmts[i])}\"/>");
                sb.Append("</numFmts>");
            }
            sb.Append($"<fonts count=\"{fonts.Count}\">{string.Concat(fonts)}</fonts>");
            sb.Append($"<fills count=\"{fills.Count}\">{string.Concat(fills)}</fills>");
            sb.Append($"<borders count=\"{borders.Count}\">{string.Concat(borders)}</borders>");
            sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
            sb.Append($"<cellXfs count=\"{styles.Count}\">{xfs}</cellXfs>");
            sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
            sb.Append("</styleSheet>");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- package parts

        private const string Head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

        private static void Add(ZipArchive zip, string name, string content)
        {
            ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                w.Write(content);
        }

        private static string ContentTypes(int n)
        {
            var sb = new StringBuilder();
            sb.Append(Head);
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            for (int i = 1; i <= n; i++)
                sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            sb.Append("<Override PartName=\"/docProps/custom.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.custom-properties+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string RootRels() =>
            Head +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "<Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/custom-properties\" Target=\"docProps/custom.xml\"/>" +
            "</Relationships>";

        // ---------------------------------------------------------------- sensitivity label
        // The company asks for a label on every Excel; without one Excel asks every time the file is opened.
        // The label the user picks (Public) is kept in docProps/custom.xml as MSIP_Label_<id>_* properties.
        private const string LabelId = "79ae2783-b6e8-45ab-9e71-54c07b2dade0";      // Public
        private const string LabelSite = "8d088ff8-7e52-4d0f-8187-dcd9ca37815a";

        private static string LabelOf(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry e = zip.GetEntry("docProps/custom.xml");
                    if (e == null) return null;
                    using (var r = new StreamReader(e.Open())) { string x = r.ReadToEnd(); return x.Contains("MSIP_Label_") ? x : null; }
                }
            }
            catch { return null; }
        }

        private static string PublicLabel()
        {
            string P(int pid, string name, string v) =>
                $"<property fmtid=\"{{D5CDD505-2E9C-101B-9397-08002B2CF9AE}}\" pid=\"{pid}\" name=\"MSIP_Label_{LabelId}_{name}\"><vt:lpwstr>{v}</vt:lpwstr></property>";
            return Head +
                "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/custom-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
                P(2, "Enabled", "true") + P(3, "SetDate", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CI)) + P(4, "Method", "Privileged") +
                P(5, "Name", LabelId) + P(6, "SiteId", LabelSite) + P(7, "ActionId", Guid.NewGuid().ToString()) +
                P(8, "ContentBits", "0") + P(9, "Tag", "10, 0, 1, 1") +
                "</Properties>";
        }

        private static string Workbook(List<XSheet> sheets)
        {
            var sb = new StringBuilder();
            sb.Append(Head);
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<sheets>");
            for (int i = 0; i < sheets.Count; i++)
                sb.Append($"<sheet name=\"{SecurityElement.Escape(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            sb.Append("</sheets>");
            sb.Append("<calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/>");
            sb.Append("</workbook>");
            return sb.ToString();
        }

        private static string WorkbookRels(int n)
        {
            var sb = new StringBuilder();
            sb.Append(Head);
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 1; i <= n; i++)
                sb.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
            sb.Append($"<Relationship Id=\"rId{n + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }
    }
}
