// AB Bridges - Import Excel / Update Excel
// Import: opens the workbook in Excel (sheet "To Revit"), the user selects the tables (Ctrl for more than one),
// Done -> every selected area becomes a black and white table (lines + text) in its own drafting view (1:1),
// optionally placed on a sheet. Update: reads every stored file / sheet / range again and redraws it in place.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using UI = Autodesk.Revit.UI;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;
using RColor = Autodesk.Revit.DB.Color;
using WinColor = System.Drawing.Color;

namespace ABBridgesRevit
{
    [Transaction(TransactionMode.Manual)]
    public class ImportExcelCommand : UI.IExternalCommand
    {
        public UI.Result Execute(UI.ExternalCommandData data, ref string message, ElementSet elements) => XlTables.Import(data);
    }

    [Transaction(TransactionMode.Manual)]
    public class UpdateExcelCommand : UI.IExternalCommand
    {
        public UI.Result Execute(UI.ExternalCommandData data, ref string message, ElementSet elements) => XlTables.Update(data);
    }

    // one table read from Excel: text, bold, merges, column widths (mm) and row heights (mm)
    internal sealed class XlTable
    {
        public string File, Sheet, Address, Name;
        public int Rows, Cols;
        public string[,] Text;
        public bool[,] Bold;
        public int[,] Align;                                  // -1 left, 0 center, 1 right
        public double[] ColW, RowH;
        public readonly List<(int r1, int c1, int r2, int c2)> Merges = new List<(int, int, int, int)>();
    }

    internal static class XlTables
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private const string Key = "XLS|T|", IdsKey = "XLS|IDS|";
        private const string SheetName = "To Revit";
        private const int MaxCells = 6000;
        private static double Mm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        // ---------------------------------------------------------------- import
        public static UI.Result Import(UI.ExternalCommandData data)
        {
            Document doc = data.Application.ActiveUIDocument.Document;
            Dictionary<string, string> map = AbStore.Load(doc);
            string file;
            using (var ofd = new OpenFileDialog { Filter = "Excel|*.xlsx;*.xlsm;*.xls", Title = "Import Excel" })
            {
                if (map.TryGetValue("SUB|EXCEL", out string last) && File.Exists(last)) { ofd.InitialDirectory = Path.GetDirectoryName(last); ofd.FileName = Path.GetFileName(last); }
                if (ofd.ShowDialog() != DialogResult.OK) return UI.Result.Cancelled;
                file = ofd.FileName;
            }

            dynamic xl = null, wb = null;
            var tables = new List<XlTable>();
            try
            {
                Type t = Type.GetTypeFromProgID("Excel.Application");
                if (t == null) { UI.TaskDialog.Show("AB Bridges", "Excel is not installed on this computer."); return UI.Result.Failed; }
                xl = Activator.CreateInstance(t);
                xl.Visible = true;
                wb = xl.Workbooks.Open(file);
                try { wb.Worksheets[SheetName].Activate(); } catch { }
                try { xl.WindowState = -4137; } catch { }      // maximized

                using (var f = new DoneForm())
                {
                    if (f.ShowDialog() != DialogResult.OK) { Close(xl, wb, false); return UI.Result.Cancelled; }
                }
                dynamic sel = xl.Selection;
                string sheet = (string)xl.ActiveSheet.Name;
                int n = (int)sel.Areas.Count;
                for (int i = 1; i <= n; i++)
                {
                    dynamic area = sel.Areas[i];
                    XlTable tb = Read(area, file, sheet);
                    if (tb != null) tables.Add(tb);
                }
            }
            catch (Exception ex)
            {
                UI.TaskDialog.Show("AB Bridges", "Could not read the selection from Excel.\n" + ex.Message);
                Close(xl, wb, false);
                return UI.Result.Failed;
            }
            Close(xl, wb, true);            // Excel stays open for the user, only the COM links go
            if (tables.Count == 0) { UI.TaskDialog.Show("AB Bridges", "Nothing selected."); return UI.Result.Cancelled; }

            List<ViewSheet> sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber).ToList();
            using (var f = new NamesForm(tables, sheets))
            {
                if (f.ShowDialog() != DialogResult.OK) return UI.Result.Cancelled;
                using (var tr = new Transaction(doc, "AB Import Excel"))
                {
                    tr.Start();
                    ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x => x.ViewFamily == ViewFamily.Drafting);
                    if (vft == null) { tr.RollBack(); UI.TaskDialog.Show("AB Bridges", "No drafting view type in the project."); return UI.Result.Failed; }
                    ViewSheet sh = f.SheetIndex > 0 ? sheets[f.SheetIndex - 1] : null;
                    double y = 0;
                    foreach (XlTable tb in tables)
                    {
                        ViewDrafting v = ViewDrafting.Create(doc, vft.Id);
                        v.Scale = 1;
                        v.Name = Unique(doc, tb.Name);
                        List<ElementId> ids = Draw(doc, v, tb, f.TextMm);
                        string vid = v.Id.Value.ToString(CI);
                        map[Key + vid] = string.Join("|", tb.File, tb.Sheet, tb.Address, f.TextMm.ToString(CI));
                        map[IdsKey + vid] = AbStore.Ids(ids);
                        if (sh != null && Viewport.CanAddViewToSheet(doc, sh.Id, v.Id))
                        {
                            BoundingBoxUV o = sh.Outline;
                            double w = Mm(tb.ColW.Sum()), h = Mm(tb.RowH.Sum());
                            double cu = o.Min.U + (o.Max.U - o.Min.U) * 0.05 + w / 2;
                            double top = o.Max.V - (o.Max.V - o.Min.V) * 0.06 - y;
                            Viewport.Create(doc, sh.Id, v.Id, new XYZ(cu, top - h / 2, 0));
                            y += h + Mm(15);
                        }
                    }
                    AbStore.Save(doc, map);
                    tr.Commit();
                }
                UI.TaskDialog.Show("AB Bridges", $"{tables.Count} table(s) imported" + (f.SheetIndex > 0 ? " and placed on the sheet." : "."));
            }
            return UI.Result.Succeeded;
        }

        // ---------------------------------------------------------------- update
        public static UI.Result Update(UI.ExternalCommandData data)
        {
            Document doc = data.Application.ActiveUIDocument.Document;
            Dictionary<string, string> map = AbStore.Load(doc);
            var items = map.Where(kv => kv.Key.StartsWith(Key)).ToList();
            if (items.Count == 0) { UI.TaskDialog.Show("AB Bridges", "No imported Excel tables in this project. Use Import Excel first."); return UI.Result.Cancelled; }

            var log = new List<string>();
            int done = 0;
            dynamic xl = null;
            try
            {
                Type t = Type.GetTypeFromProgID("Excel.Application");
                if (t == null) { UI.TaskDialog.Show("AB Bridges", "Excel is not installed on this computer."); return UI.Result.Failed; }
                xl = Activator.CreateInstance(t);
                xl.Visible = false; xl.DisplayAlerts = false;
                var books = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
                using (var tr = new Transaction(doc, "AB Update Excel"))
                {
                    tr.Start();
                    foreach (var kv in items)
                    {
                        string vid = kv.Key.Substring(Key.Length);
                        View v = long.TryParse(vid, out long l) ? doc.GetElement(new ElementId(l)) as View : null;
                        if (v == null) { map.Remove(kv.Key); map.Remove(IdsKey + vid); continue; }
                        string[] p = kv.Value.Split('|');
                        if (p.Length < 4) continue;
                        if (!File.Exists(p[0])) { log.Add($"{v.Name}: file not found ({p[0]})."); continue; }
                        try
                        {
                            if (!books.TryGetValue(p[0], out dynamic wb)) { wb = xl.Workbooks.Open(p[0], 0, true); books[p[0]] = wb; }
                            dynamic area = wb.Worksheets[p[1]].Range[p[2]];
                            XlTable tb = Read(area, p[0], p[1]);
                            if (tb == null) continue;
                            if (map.TryGetValue(IdsKey + vid, out string old))
                                foreach (ElementId id in AbStore.Ids(old)) { try { doc.Delete(id); } catch { } }
                            double mm = double.TryParse(p[3], NumberStyles.Float, CI, out double m) ? m : 2.5;
                            map[IdsKey + vid] = AbStore.Ids(Draw(doc, v, tb, mm));
                            done++;
                        }
                        catch (Exception ex) { log.Add($"{v.Name}: {ex.Message}"); }
                    }
                    AbStore.Save(doc, map);
                    tr.Commit();
                }
                foreach (dynamic wb in books.Values) { try { wb.Close(false); } catch { } }
            }
            finally
            {
                try { xl?.Quit(); } catch { }
                if (xl != null) try { Marshal.FinalReleaseComObject(xl); } catch { }
            }
            UI.TaskDialog.Show("AB Bridges", $"{done} table(s) updated." + (log.Count > 0 ? "\n\n" + string.Join("\n", log) : ""));
            return UI.Result.Succeeded;
        }

        // ---------------------------------------------------------------- read one area through COM
        private static XlTable Read(dynamic area, string file, string sheet)
        {
            int rows = (int)area.Rows.Count, cols = (int)area.Columns.Count;
            if (rows < 1 || cols < 1) return null;
            if ((long)rows * cols > MaxCells) throw new InvalidOperationException($"The selection is too big ({rows} x {cols}).");
            int r0 = (int)area.Row, c0 = (int)area.Column;
            var tb = new XlTable
            {
                File = file, Sheet = sheet, Rows = rows, Cols = cols,
                Address = ((string)area.Address).Replace("$", ""),
                Text = new string[rows, cols], Bold = new bool[rows, cols], Align = new int[rows, cols],
                ColW = new double[cols], RowH = new double[rows]
            };
            for (int c = 1; c <= cols; c++) tb.ColW[c - 1] = Math.Max(3, ((double)area.Columns[c].ColumnWidth * 7 + 5) * 25.4 / 96);
            for (int r = 1; r <= rows; r++) tb.RowH[r - 1] = Math.Max(3, (double)area.Rows[r].RowHeight * 25.4 / 72);
            var seen = new HashSet<string>();
            for (int r = 1; r <= rows; r++)
                for (int c = 1; c <= cols; c++)
                {
                    dynamic cell = area.Cells[r, c];
                    string txt = "";
                    try { txt = (string)cell.Text ?? ""; } catch { }
                    if (txt.StartsWith("#")) { try { txt = Convert.ToString(cell.Value2, CI) ?? txt; } catch { } }   // ### when the column is narrow
                    tb.Text[r - 1, c - 1] = txt;
                    try { object b = cell.Font.Bold; tb.Bold[r - 1, c - 1] = b is bool bb && bb; } catch { }
                    int al = 0;
                    try { al = Convert.ToInt32(cell.HorizontalAlignment); } catch { }
                    bool num = false;
                    try { object v2 = cell.Value2; num = v2 is double; } catch { }
                    tb.Align[r - 1, c - 1] = al == -4131 ? -1 : al == -4152 ? 1 : al == -4108 || al == 7 ? 0 : num ? 0 : -1;
                    bool merged = false;
                    try { merged = (bool)cell.MergeCells; } catch { }
                    if (merged)
                    {
                        dynamic ma = cell.MergeArea;
                        string ad = (string)ma.Address;
                        if (seen.Add(ad))
                        {
                            int a1 = (int)ma.Row - r0, b1 = (int)ma.Column - c0;
                            int a2 = a1 + (int)ma.Rows.Count - 1, b2 = b1 + (int)ma.Columns.Count - 1;
                            a1 = Math.Max(0, a1); b1 = Math.Max(0, b1); a2 = Math.Min(rows - 1, a2); b2 = Math.Min(cols - 1, b2);
                            if (a2 > a1 || b2 > b1)
                            {
                                tb.Merges.Add((a1, b1, a2, b2));
                                // the text of a merge lives in its top-left cell (may be outside the selection)
                                if (string.IsNullOrEmpty(tb.Text[a1, b1])) { try { tb.Text[a1, b1] = (string)ma.Cells[1, 1].Text ?? ""; } catch { } }
                            }
                        }
                    }
                }
            return tb;
        }

        // ---------------------------------------------------------------- draw (view 1:1, sizes in paper mm)
        private static List<ElementId> Draw(Document doc, View v, XlTable tb, double textMm)
        {
            var ids = new List<ElementId>();
            GraphicsStyle thin = SubstructureCommand.LineStyle(doc, "AB Table", new RColor(0, 0, 0));
            ElementId tn = TextType(doc, textMm, false), tbd = TextType(doc, textMm, true);
            double[] xs = new double[tb.Cols + 1], ys = new double[tb.Rows + 1];
            for (int c = 0; c < tb.Cols; c++) xs[c + 1] = xs[c] + tb.ColW[c];
            for (int r = 0; r < tb.Rows; r++) ys[r + 1] = ys[r] - tb.RowH[r];

            // owner of each cell (top-left of its merge)
            var own = new (int r, int c)[tb.Rows, tb.Cols];
            for (int r = 0; r < tb.Rows; r++) for (int c = 0; c < tb.Cols; c++) own[r, c] = (r, c);
            foreach (var m in tb.Merges) for (int r = m.r1; r <= m.r2; r++) for (int c = m.c1; c <= m.c2; c++) own[r, c] = (m.r1, m.c1);

            void Ln(double x1, double y1, double x2, double y2)
            {
                if (Math.Abs(x1 - x2) + Math.Abs(y1 - y2) < 1e-6) return;
                try { DetailCurve dc = doc.Create.NewDetailCurve(v, Line.CreateBound(new XYZ(Mm(x1), Mm(y1), 0), new XYZ(Mm(x2), Mm(y2), 0))); if (thin != null) dc.LineStyle = thin; ids.Add(dc.Id); } catch { }
            }
            // horizontal lines, joined in runs; skipped inside a merge
            for (int r = 0; r <= tb.Rows; r++)
            {
                int start = -1;
                for (int c = 0; c <= tb.Cols; c++)
                {
                    bool on = c < tb.Cols && (r == 0 || r == tb.Rows || own[r - 1, c] != own[r, c]);
                    if (on && start < 0) start = c;
                    if (!on && start >= 0) { Ln(xs[start], ys[r], xs[c], ys[r]); start = -1; }
                }
            }
            for (int c = 0; c <= tb.Cols; c++)
            {
                int start = -1;
                for (int r = 0; r <= tb.Rows; r++)
                {
                    bool on = r < tb.Rows && (c == 0 || c == tb.Cols || own[r, c - 1] != own[r, c]);
                    if (on && start < 0) start = r;
                    if (!on && start >= 0) { Ln(xs[c], ys[start], xs[c], ys[r]); start = -1; }
                }
            }
            // text
            var spans = tb.Merges.ToDictionary(m => (m.r1, m.c1), m => m);
            for (int r = 0; r < tb.Rows; r++)
                for (int c = 0; c < tb.Cols; c++)
                {
                    if (own[r, c] != (r, c)) continue;
                    string t = tb.Text[r, c];
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    var m = spans.TryGetValue((r, c), out var mm) ? mm : (r1: r, c1: c, r2: r, c2: c);
                    double x1 = xs[m.c1], x2 = xs[m.c2 + 1], y1 = ys[m.r1], y2 = ys[m.r2 + 1];
                    double w = Math.Max(2, x2 - x1 - 1.5);
                    int lines = t.Split('\n').Length;
                    double th = lines * textMm * 1.7;
                    double ty = (y1 + y2) / 2 + th / 2;
                    int al = tb.Align[r, c];
                    double tx = al < 0 ? x1 + 0.75 : al > 0 ? x2 - 0.75 : (x1 + x2) / 2;
                    var opt = new TextNoteOptions(tb.Bold[r, c] ? tbd : tn)
                    {
                        HorizontalAlignment = al < 0 ? HorizontalTextAlignment.Left : al > 0 ? HorizontalTextAlignment.Right : HorizontalTextAlignment.Center,
                        VerticalAlignment = VerticalTextAlignment.Top
                    };
                    try { ids.Add(TextNote.Create(doc, v.Id, new XYZ(Mm(tx), Mm(ty), 0), Mm(w), t, opt).Id); }
                    catch { try { ids.Add(TextNote.Create(doc, v.Id, new XYZ(Mm(tx), Mm(ty), 0), t, opt).Id); } catch { } }
                }
            return ids;
        }

        // text type "AB Table 2.5 mm" (and Bold), no border, transparent, tight offset
        private static ElementId TextType(Document doc, double mm, bool bold)
        {
            string name = $"AB Table {mm.ToString("0.##", CI)} mm" + (bold ? " Bold" : "");
            TextNoteType ex = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault(x => x.Name == name);
            if (ex != null) return ex.Id;
            TextNoteType baseT = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType;
            if (baseT == null) return doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            TextNoteType t = baseT.Duplicate(name) as TextNoteType;
            void Set(BuiltInParameter bip, object val)
            {
                try
                {
                    Parameter p = t.get_Parameter(bip);
                    if (p == null || p.IsReadOnly) return;
                    if (val is double d) p.Set(d); else if (val is int i) p.Set(i); else if (val is string s) p.Set(s);
                }
                catch { }
            }
            Set(BuiltInParameter.TEXT_SIZE, Mm(mm));
            Set(BuiltInParameter.TEXT_STYLE_BOLD, bold ? 1 : 0);
            Set(BuiltInParameter.TEXT_BACKGROUND, 1);                 // transparent
            Set(BuiltInParameter.TEXT_BOX_VISIBILITY, 0);
            Set(BuiltInParameter.LEADER_OFFSET_SHEET, Mm(0.5));
            Set(BuiltInParameter.TEXT_COLOR, 0);
            return t.Id;
        }

        private static string Unique(Document doc, string name)
        {
            foreach (char ch in "{}[]|;<>?`~:\\") name = name.Replace(ch, '-');
            var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(x => x.Name));
            string n = name; int k = 2;
            while (names.Contains(n)) n = name + " (" + k++ + ")";
            return n;
        }

        private static void Close(dynamic xl, dynamic wb, bool keepOpen)
        {
            try
            {
                if (!keepOpen) { try { wb?.Close(false); } catch { } try { xl?.Quit(); } catch { } }
                else { try { xl.Visible = true; xl.UserControl = true; } catch { } }
            }
            finally
            {
                if (wb != null) try { Marshal.FinalReleaseComObject(wb); } catch { }
                if (xl != null) try { Marshal.FinalReleaseComObject(xl); } catch { }
            }
        }

        // ---------------------------------------------------------------- forms
        private sealed class DoneForm : Form
        {
            public DoneForm()
            {
                Text = "AB Bridges - Import Excel"; TopMost = true; FormBorderStyle = FormBorderStyle.FixedToolWindow;
                StartPosition = FormStartPosition.Manual; ClientSize = new Size(330, 92); Font = new Font("Segoe UI", 9f);
                Rectangle wa = Screen.PrimaryScreen.WorkingArea; Location = new Point(wa.Right - Width - 20, wa.Top + 120);
                Controls.Add(new Label { Text = "Select the tables in Excel (hold Ctrl for more than one), then press Done.", Location = new Point(10, 8), Size = new Size(310, 36) });
                var ok = new Button { Text = "Done", Location = new Point(150, 52), Size = new Size(80, 28), DialogResult = DialogResult.OK, BackColor = WinColor.FromArgb(232, 121, 43), ForeColor = WinColor.White, FlatStyle = FlatStyle.Flat };
                var no = new Button { Text = "Cancel", Location = new Point(238, 52), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
                Controls.Add(ok); Controls.Add(no); AcceptButton = ok; CancelButton = no;
                Brand.SetIcon(this);
            }
        }

        private sealed class NamesForm : Form
        {
            public int SheetIndex => _sheet.SelectedIndex;
            public double TextMm => (double)_size.Value;
            private readonly ComboBox _sheet;
            private readonly NumericUpDown _size;

            public NamesForm(List<XlTable> tables, List<ViewSheet> sheets)
            {
                Text = "AB Bridges - Import Excel"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(520, 330); Font = new Font("Segoe UI", 9f);
                var grid = new DataGridView { Location = new Point(10, 10), Size = new Size(500, 170), AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = WinColor.White };
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Range", ReadOnly = true, FillWeight = 35 });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Table name (drafting view)", FillWeight = 65 });
                foreach (XlTable t in tables) grid.Rows.Add(t.Sheet + "!" + t.Address, t.Sheet + " " + t.Address);
                Controls.Add(new Label { Text = "Place on sheet", Location = new Point(10, 228), AutoSize = true });
                _sheet = new ComboBox { Location = new Point(110, 224), Width = 400, DropDownStyle = ComboBoxStyle.DropDownList };
                _sheet.Items.Add("(none - drafting views only)");
                foreach (ViewSheet s in sheets) _sheet.Items.Add(s.SheetNumber + " - " + s.Name);
                _sheet.SelectedIndex = 0;
                Controls.Add(new Label { Text = "Text size (mm)", Location = new Point(10, 262), AutoSize = true });
                _size = new NumericUpDown { Location = new Point(110, 258), Width = 70, DecimalPlaces = 1, Increment = 0.5m, Minimum = 1, Maximum = 10, Value = 2.5m };
                var ok = new Button { Text = "Import", Location = new Point(340, 292), Size = new Size(80, 28), BackColor = WinColor.FromArgb(232, 121, 43), ForeColor = WinColor.White, FlatStyle = FlatStyle.Flat };
                var no = new Button { Text = "Cancel", Location = new Point(430, 292), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
                ok.Click += (s, e) =>
                {
                    grid.EndEdit();
                    for (int i = 0; i < tables.Count; i++)
                    {
                        string n = Convert.ToString(grid.Rows[i].Cells[1].Value)?.Trim();
                        tables[i].Name = string.IsNullOrEmpty(n) ? tables[i].Sheet + " " + tables[i].Address : n;
                    }
                    DialogResult = DialogResult.OK;
                };
                foreach (var c in new System.Windows.Forms.Control[] { _sheet, _size, ok, no }) c.Top -= 34;
                foreach (var c in Controls.Cast<System.Windows.Forms.Control>()) if (c is Label) c.Top -= 34;
                ClientSize = new Size(ClientSize.Width, ClientSize.Height - 34);
                Controls.AddRange(new System.Windows.Forms.Control[] { grid, _sheet, _size, ok, no });
                AcceptButton = ok; CancelButton = no;
                Brand.AddHeader(this, "Import Excel");
            }
        }
    }
}
