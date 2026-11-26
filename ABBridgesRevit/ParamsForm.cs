// AB Bridges for Revit - dialog (table style): Excel file + Family 1, Family 2, ... (more can be added).
// Every family can be searched by name; "Parameters" opens the list of its parameters to choose which go to Excel.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ABBridgesRevit
{
    internal sealed class FamItem
    {
        public string Name;
        public bool Adaptive;
        public object Family;        // Autodesk.Revit.DB.Family
        public override string ToString() => Adaptive ? Name : Name + "   (not adaptive)";
    }

    internal sealed class ParamsForm : Form
    {
        internal static string LastExcel = "";
        internal static List<string> LastFamilies = new List<string>();

        private static readonly Color Navy = Color.FromArgb(31, 58, 95);
        private static readonly Color Line = Color.FromArgb(220, 224, 230);
        private static readonly Color Zebra = Color.FromArgb(247, 249, 252);

        private readonly List<FamItem> _fams;
        private readonly Func<FamItem, FamInfo> _read;                       // reads a family (Revit)
        private readonly Dictionary<string, FamInfo> _cache = new Dictionary<string, FamInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _sel = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private readonly TextBox _tbExcel;
        private readonly Panel _table;
        private readonly LinkLabel _lAdd;
        private readonly Panel _bottom;
        private readonly List<Row> _rows = new List<Row>();

        private sealed class Row
        {
            public Panel P;
            public Label No, Pts;
            public ComboBox Cb;
            public Button Params, Del;
            public FamItem Item;
        }

        public string ExcelPath => _tbExcel.Text.Trim();

        public ParamsForm(List<FamItem> families, Func<FamItem, FamInfo> read)
        {
            _fams = families;
            _read = read;
            Text = "AB Bridges - Read Family Parameters";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new System.Drawing.Font("Segoe UI", 9.5f);
            BackColor = Color.White;
            ShowInTaskbar = false;
            ClientSize = new Size(600, 380);

            Controls.Add(new Label { Text = "Excel file", Font = new System.Drawing.Font("Segoe UI Semibold", 9.5f), Location = new Point(20, 22), AutoSize = true });
            _tbExcel = new TextBox { Location = new Point(100, 18), Size = new Size(380, 25), Text = LastExcel };
            var bBrowse = Flat("Browse...", new Point(490, 16), 90, false);
            bBrowse.Click += (s, e) => Browse();
            Controls.Add(_tbExcel);
            Controls.Add(bBrowse);

            // table header
            var head = new Panel { Location = new Point(20, 60), Size = new Size(560, 28), BackColor = Navy };
            head.Controls.Add(HeadLbl("#", 8));
            head.Controls.Add(HeadLbl("Family (type to search)", 40));
            head.Controls.Add(HeadLbl("Adaptive pts", 330));
            head.Controls.Add(HeadLbl("Parameters", 430));
            Controls.Add(head);

            _table = new Panel { Location = new Point(20, 88), Size = new Size(560, 10), BackColor = Color.White };
            Controls.Add(_table);
            _lAdd = new LinkLabel { Text = "+ Add family", LinkColor = Navy, ActiveLinkColor = Navy, AutoSize = true, Font = new System.Drawing.Font("Segoe UI Semibold", 9.5f) };
            _lAdd.LinkBehavior = LinkBehavior.HoverUnderline;
            _lAdd.LinkClicked += (s, e) => { AddRow(null); Relayout(); };
            Controls.Add(_lAdd);

            _bottom = new Panel { Size = new Size(600, 92), BackColor = Color.White };
            _bottom.Controls.Add(new Label
            {
                Text = "Family 1 = all stations, Family 2 = corbel stations, Family 3 = outrigger stations, more families = all stations.\n" +
                       "Formula parameters are not ticked by default - open Parameters to add or remove any parameter.",
                Location = new Point(20, 4), Size = new Size(560, 36), ForeColor = Color.FromArgb(107, 114, 128)
            });
            var sep = new Panel { Location = new Point(0, 44), Size = new Size(600, 1), BackColor = Line };
            _bottom.Controls.Add(sep);
            var ok = Flat("Read && Write", new Point(374, 54), 120, true);
            var close = Flat("Close", new Point(500, 54), 80, false);
            close.DialogResult = DialogResult.Cancel;
            ok.Click += (s, e) => OnOk();
            _bottom.Controls.Add(ok);
            _bottom.Controls.Add(close);
            Controls.Add(_bottom);
            AcceptButton = ok;
            CancelButton = close;

            int n = Math.Max(3, LastFamilies.Count);
            for (int i = 0; i < n; i++) AddRow(i < LastFamilies.Count ? LastFamilies[i] : null);
            Relayout();
        }

        private static Label HeadLbl(string t, int x) =>
            new Label { Text = t, ForeColor = Color.White, Font = new System.Drawing.Font("Segoe UI Semibold", 9.5f), Location = new Point(x, 5), AutoSize = true, BackColor = Color.Transparent };

        private static Button Flat(string text, Point at, int w, bool primary)
        {
            var b = new Button
            {
                Text = text, Location = at, Size = new Size(w, 30), FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Navy : Color.White, ForeColor = primary ? Color.White : Navy, Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Navy;
            return b;
        }

        // ---------------------------------------------------------------- rows

        private void AddRow(string selectName)
        {
            var r = new Row();
            r.P = new Panel { Size = new Size(560, 36) };
            r.No = new Label { Location = new Point(8, 9), AutoSize = true, ForeColor = Navy, Font = new System.Drawing.Font("Segoe UI Semibold", 10f) };
            r.Cb = new ComboBox { Location = new Point(36, 5), Size = new Size(280, 25), DropDownStyle = ComboBoxStyle.DropDown, IntegralHeight = false, MaxDropDownItems = 14 };
            FillCombo(r.Cb, "");
            r.Pts = new Label { Location = new Point(332, 9), Size = new Size(80, 20), ForeColor = Color.FromArgb(55, 65, 81) };
            r.Params = new Button { Location = new Point(428, 4), Size = new Size(96, 27), Text = "—", FlatStyle = FlatStyle.Flat, ForeColor = Navy, BackColor = Color.White, Cursor = Cursors.Hand, Enabled = false };
            r.Params.FlatAppearance.BorderColor = Color.FromArgb(190, 196, 205);
            r.Del = new Button { Location = new Point(530, 5), Size = new Size(26, 26), Text = "✕", FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(150, 150, 160), BackColor = Color.Transparent, Cursor = Cursors.Hand };
            r.Del.FlatAppearance.BorderSize = 0;
            new ToolTip().SetToolTip(r.Params, "Choose which parameters go to Excel");
            new ToolTip().SetToolTip(r.Del, "Remove this family");

            // search: the list shows only the families whose name contains the typed text
            r.Cb.TextUpdate += (s, e) =>
            {
                string t = r.Cb.Text;
                FillCombo(r.Cb, t);
                r.Cb.DroppedDown = true;
                r.Cb.Text = t;
                r.Cb.SelectionStart = t.Length;
                Cursor.Current = Cursors.Default;
            };
            r.Cb.SelectionChangeCommitted += (s, e) => Pick(r, r.Cb.SelectedItem as FamItem);
            r.Cb.Leave += (s, e) =>
            {
                FamItem exact = _fams.FirstOrDefault(f => string.Equals(f.Name, r.Cb.Text.Trim(), StringComparison.OrdinalIgnoreCase));
                if (exact != null && exact != r.Item) Pick(r, exact);
                else if (exact == null && r.Item != null) r.Cb.Text = r.Item.Name;
                else if (exact == null && r.Item == null) r.Cb.Text = "";
            };
            r.Params.Click += (s, e) => EditParams(r);
            r.Del.Click += (s, e) =>
            {
                _table.Controls.Remove(r.P);
                _rows.Remove(r);
                if (_rows.Count == 0) AddRow(null);
                Relayout();
            };

            r.P.Controls.AddRange(new Control[] { r.No, r.Cb, r.Pts, r.Params, r.Del });
            _rows.Add(r);
            _table.Controls.Add(r.P);

            if (!string.IsNullOrEmpty(selectName))
            {
                FamItem f = _fams.FirstOrDefault(x => x.Name == selectName);
                if (f != null) Pick(r, f);
            }
        }

        private void FillCombo(ComboBox cb, string filter)
        {
            cb.BeginUpdate();
            cb.Items.Clear();
            foreach (FamItem f in _fams)
                if (string.IsNullOrEmpty(filter) || f.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    cb.Items.Add(f);
            cb.EndUpdate();
        }

        private void Pick(Row r, FamItem f)
        {
            r.Item = f;
            if (f == null) { r.Pts.Text = ""; r.Params.Text = "—"; r.Params.Enabled = false; return; }
            if (!r.Cb.Items.Contains(f)) FillCombo(r.Cb, "");
            r.Cb.SelectedItem = f;
            FamInfo info = Info(f);
            if (info == null) { r.Pts.Text = "?"; r.Params.Text = "—"; r.Params.Enabled = false; return; }
            r.Pts.Text = info.AdaptivePoints >= 0 ? info.AdaptivePoints.ToString() : "—";
            UpdateParamsButton(r, info);
        }

        private void UpdateParamsButton(Row r, FamInfo info)
        {
            int n = Selection(info).Count;
            r.Params.Text = $"{n} of {info.Choosable.Count}  ▸";
            r.Params.Enabled = true;
        }

        private FamInfo Info(FamItem f)
        {
            if (f == null) return null;
            if (_cache.TryGetValue(f.Name, out FamInfo i)) return i;
            Cursor = Cursors.WaitCursor;
            try { i = _read(f); }
            catch (Exception ex) { MessageBox.Show(this, $"Could not read {f.Name}:\n{ex.Message}", "AB Bridges"); i = null; }
            finally { Cursor = Cursors.Default; }
            if (i != null) _cache[f.Name] = i;
            return i;
        }

        private List<string> Selection(FamInfo info)
        {
            if (!_sel.TryGetValue(info.Name, out List<string> s))
            {
                s = ParamSelection.Get(info);
                _sel[info.Name] = s;
            }
            return s;
        }

        private void EditParams(Row r)
        {
            FamInfo info = Info(r.Item);
            if (info == null) return;
            List<string> chosen = ParamPicker.Choose(this, info, Selection(info));
            if (chosen == null) return;
            _sel[info.Name] = chosen;
            ParamSelection.Set(info.Name, chosen);
            UpdateParamsButton(r, info);
        }

        private void Relayout()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                r.P.Location = new Point(0, i * 36);
                r.P.BackColor = i % 2 == 0 ? Color.White : Zebra;
                r.No.Text = (i + 1).ToString();
            }
            _table.Height = _rows.Count * 36;
            _lAdd.Location = new Point(24, _table.Bottom + 8);
            _bottom.Location = new Point(0, _lAdd.Bottom + 12);
            ClientSize = new Size(600, _bottom.Bottom);
        }

        private void Browse()
        {
            using (var dlg = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", Title = "Excel made by AB Bridges" })
            {
                try { if (File.Exists(_tbExcel.Text)) dlg.InitialDirectory = Path.GetDirectoryName(_tbExcel.Text); } catch { }
                if (dlg.ShowDialog(this) == DialogResult.OK) _tbExcel.Text = dlg.FileName;
            }
        }

        // ---------------------------------------------------------------- result

        // one entry per row in order (Family 1, 2, ...); null when the row is empty
        public List<FamInfo> Result { get; private set; } = new List<FamInfo>();

        private void OnOk()
        {
            if (!File.Exists(ExcelPath)) { MessageBox.Show(this, "Choose the Excel file first.", "AB Bridges"); return; }
            var res = new List<FamInfo>();
            foreach (Row r in _rows)
            {
                FamInfo info = r.Item == null ? null : Info(r.Item);
                res.Add(info?.WithSelection(Selection(info)));
            }
            if (res.All(x => x == null)) { MessageBox.Show(this, "Choose at least one family.", "AB Bridges"); return; }
            Result = res;
            LastExcel = ExcelPath;
            LastFamilies = _rows.Select(r => r.Item?.Name ?? "").ToList();
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // ---------------------------------------------------------------- parameter picker

    internal sealed class ParamPicker : Form
    {
        private readonly CheckedListBox _list = new CheckedListBox();
        private readonly TextBox _search = new TextBox();
        private readonly FamInfo _info;
        private readonly HashSet<string> _on;
        private bool _filling;

        public static List<string> Choose(IWin32Window owner, FamInfo info, List<string> current)
        {
            using (var f = new ParamPicker(info, current))
                return f.ShowDialog(owner) == DialogResult.OK ? info.Choosable.Where(p => f._on.Contains(p.Name)).Select(p => p.Name).ToList() : null;
        }

        private ParamPicker(FamInfo info, List<string> current)
        {
            _info = info;
            _on = new HashSet<string>(current);
            Text = "Parameters - " + info.Name;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Segoe UI", 9.5f);
            BackColor = Color.White;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 520);

            Controls.Add(new Label { Text = "Ticked parameters go to the Excel. Parameters with a formula are not listed (the family computes them).", Location = new Point(14, 10), Size = new Size(492, 36), ForeColor = Color.FromArgb(107, 114, 128) });
            _search.Location = new Point(14, 48);
            _search.Size = new Size(492, 25);
            _search.PlaceholderText = "Search parameters...";
            _search.TextChanged += (s, e) => Fill();
            Controls.Add(_search);

            _list.Location = new Point(14, 80);
            _list.Size = new Size(492, 370);
            _list.CheckOnClick = true;
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.ItemCheck += (s, e) =>
            {
                if (_filling) return;
                string name = ((Item)_list.Items[e.Index]).P.Name;
                if (e.NewValue == CheckState.Checked) _on.Add(name); else _on.Remove(name);
            };
            Controls.Add(_list);

            var all = new Button { Text = "All", Location = new Point(14, 470), Size = new Size(60, 30), FlatStyle = FlatStyle.Flat };
            var none = new Button { Text = "None", Location = new Point(80, 470), Size = new Size(60, 30), FlatStyle = FlatStyle.Flat };
            var def = new Button { Text = "Default", Location = new Point(146, 470), Size = new Size(80, 30), FlatStyle = FlatStyle.Flat };
            all.Click += (s, e) => { foreach (var p in Visible()) _on.Add(p.Name); Fill(); };
            none.Click += (s, e) => { foreach (var p in Visible()) _on.Remove(p.Name); Fill(); };
            def.Click += (s, e) => { _on.Clear(); foreach (string n in _info.DefaultSelection()) _on.Add(n); Fill(); };
            var ok = new Button { Text = "OK", Location = new Point(340, 470), Size = new Size(80, 30), DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(31, 58, 95), ForeColor = Color.White };
            var cancel = new Button { Text = "Cancel", Location = new Point(426, 470), Size = new Size(80, 30), DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
            Controls.AddRange(new Control[] { all, none, def, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            Fill();
        }

        private sealed class Item
        {
            public FamParam P;
            public override string ToString() =>
                P.Name + "    (" + (P.Kind ?? "") + (P.IsInstance ? ", instance" : ", type") + ")" + (P.IsReporting ? "    [reporting]" : "");
        }

        private IEnumerable<FamParam> Visible()
        {
            string t = _search.Text.Trim();
            return _info.Choosable.Where(p => t.Length == 0 || p.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void Fill()
        {
            _filling = true;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (FamParam p in Visible())
                _list.Items.Add(new Item { P = p }, _on.Contains(p.Name));
            _list.EndUpdate();
            _filling = false;
        }
    }
}
