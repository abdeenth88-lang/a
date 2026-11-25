// AB Bridges - puts the logo (embedded AB_logo_256.png) at the right end of the title row of the sheets
// that AB Bridges makes. Done on the finished .xlsx: media + one drawing per sheet + relations + content types.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace ABBridges
{
    internal static class XlsxLogo
    {
        private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";
        private const string DrawingType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing";
        private const string ImageType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
        private const string RelId = "rIdAbLogo";
        private const long Px = 9525;                 // EMU per pixel
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        // sheets: sheet index in the workbook (0-based) -> last column of its title (1-based); the logo sits in that column
        public static void Stamp(string path, Dictionary<int, int> sheets)
        {
            if (sheets == null || sheets.Count == 0) return;
            byte[] png = Brand.LogoBytes();
            if (png == null) return;

            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                XDocument wb = Load(zip, "xl/workbook.xml"), rels = Load(zip, "xl/_rels/workbook.xml.rels"), types = Load(zip, "[Content_Types].xml");
                if (wb == null || rels == null || types == null) return;
                Save(zip, "xl/media/ab_logo.png", png);

                List<XElement> list = wb.Descendants(M + "sheet").ToList();
                int k = 0;
                foreach (var kv in sheets)
                {
                    if (kv.Key < 0 || kv.Key >= list.Count) continue;
                    string rid = (string)list[kv.Key].Attribute(R + "id");
                    XElement rel = rels.Descendants(PR + "Relationship").FirstOrDefault(e => (string)e.Attribute("Id") == rid);
                    if (rel == null) continue;
                    string target = ((string)rel.Attribute("Target")).Replace('\\', '/');
                    string sheetPath = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
                    XDocument sheet = Load(zip, sheetPath);
                    if (sheet == null || sheet.Root.Element(M + "drawing") != null) continue;     // already has a drawing

                    k++;
                    string drawing = "abdrawing" + k.ToString(CI) + ".xml";
                    Save(zip, "xl/drawings/" + drawing, Drawing(kv.Value - 1));
                    Save(zip, "xl/drawings/_rels/" + drawing + ".rels", new XDocument(
                        new XElement(PR + "Relationships",
                            new XElement(PR + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", ImageType), new XAttribute("Target", "../media/ab_logo.png")))));

                    // sheet -> drawing relation
                    string dir = Path.GetDirectoryName(sheetPath).Replace('\\', '/');
                    string relsPath = dir + "/_rels/" + Path.GetFileName(sheetPath) + ".rels";
                    XDocument sr = Load(zip, relsPath) ?? new XDocument(new XElement(PR + "Relationships"));
                    sr.Root.Elements(PR + "Relationship").Where(e => (string)e.Attribute("Id") == RelId).Remove();
                    sr.Root.Add(new XElement(PR + "Relationship", new XAttribute("Id", RelId), new XAttribute("Type", DrawingType), new XAttribute("Target", "../drawings/" + drawing)));
                    Save(zip, relsPath, sr);

                    // <drawing r:id=.../> goes before these elements (schema order), else at the end
                    var d = new XElement(M + "drawing", new XAttribute(R + "id", RelId));
                    string[] after = { "legacyDrawing", "legacyDrawingHF", "drawingHF", "picture", "oleObjects", "controls", "webPublishItems", "tableParts", "extLst" };
                    XElement next = sheet.Root.Elements().FirstOrDefault(e => after.Contains(e.Name.LocalName));
                    if (next != null) next.AddBeforeSelf(d); else sheet.Root.Add(d);
                    if (sheet.Root.Attribute(XNamespace.Xmlns + "r") == null) sheet.Root.Add(new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName));
                    Save(zip, sheetPath, sheet);

                    types.Root.Add(new XElement(CT + "Override", new XAttribute("PartName", "/xl/drawings/" + drawing),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.drawing+xml")));
                }
                if (!types.Root.Elements(CT + "Default").Any(e => string.Equals((string)e.Attribute("Extension"), "png", StringComparison.OrdinalIgnoreCase)))
                    types.Root.AddFirst(new XElement(CT + "Default", new XAttribute("Extension", "png"), new XAttribute("ContentType", "image/png")));
                Save(zip, "[Content_Types].xml", types);
            }
        }

        // 40 x 40 px logo, 3 px from the top of row 1, in column 'col' (0-based)
        private static XDocument Drawing(int col)
        {
            XNamespace x = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
            XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            long size = 40 * Px;
            return new XDocument(
                new XElement(x + "wsDr", new XAttribute(XNamespace.Xmlns + "xdr", x.NamespaceName), new XAttribute(XNamespace.Xmlns + "a", a.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName),
                    new XElement(x + "oneCellAnchor",
                        new XElement(x + "from",
                            new XElement(x + "col", Math.Max(0, col)), new XElement(x + "colOff", 4 * Px),
                            new XElement(x + "row", 0), new XElement(x + "rowOff", 3 * Px)),
                        new XElement(x + "ext", new XAttribute("cx", size), new XAttribute("cy", size)),
                        new XElement(x + "pic",
                            new XElement(x + "nvPicPr",
                                new XElement(x + "cNvPr", new XAttribute("id", 2), new XAttribute("name", "AB Bridges logo")),
                                new XElement(x + "cNvPicPr", new XElement(a + "picLocks", new XAttribute("noChangeAspect", 1)))),
                            new XElement(x + "blipFill",
                                new XElement(a + "blip", new XAttribute(R + "embed", "rId1")),
                                new XElement(a + "stretch", new XElement(a + "fillRect"))),
                            new XElement(x + "spPr",
                                new XElement(a + "xfrm", new XElement(a + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                                    new XElement(a + "ext", new XAttribute("cx", size), new XAttribute("cy", size))),
                                new XElement(a + "prstGeom", new XAttribute("prst", "rect"), new XElement(a + "avLst")))),
                        new XElement(x + "clientData"))));
        }

        private static XDocument Load(ZipArchive zip, string name)
        {
            ZipArchiveEntry e = zip.GetEntry(name);
            if (e == null) return null;
            using (Stream s = e.Open()) return XDocument.Load(s);
        }

        private static void Save(ZipArchive zip, string name, XDocument doc)
        {
            zip.GetEntry(name)?.Delete();
            using (Stream s = zip.CreateEntry(name).Open()) doc.Save(s);
        }

        private static void Save(ZipArchive zip, string name, byte[] data)
        {
            zip.GetEntry(name)?.Delete();
            using (Stream s = zip.CreateEntry(name).Open()) s.Write(data, 0, data.Length);
        }
    }
}
