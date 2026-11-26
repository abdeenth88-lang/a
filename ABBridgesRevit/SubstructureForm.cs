// AB Bridges for Revit - Substructure dialog.
// Top: the Excel and the longitudinal view of the bridge. Under it one small card per axis, under its bubble: the real
// Revit section at the axis (live) and the families of the axis; click a card to edit that axis, double-click to open the
// section big (Measure, Fit, Depth, Crop, Plan). Bottom strip: left the families of the axis (bearing row first, then the
// families in order: surface + vertical offset + offset from the alignment), right the bearing settings and the parameters.
// No Revit types here: the command gives the families, their parameters, their sizes and the deck sections.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ABBridgesRevit
{
    internal sealed class SubstructureForm : Form
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private static readonly Color Navy = Color.FromArgb(31, 58, 95);
        private static readonly Color Blue = Color.FromArgb(37, 99, 235);
        private static readonly Color Green = Color.FromArgb(77, 124, 15);
        private static readonly Color Red = Color.FromArgb(194, 65, 12);
        private static readonly Color Orange = Color.FromArgb(234, 108, 40);
        private static readonly Color Grey = Color.FromArgb(107, 114, 128);
        private const string None = "(none)";

        // from the command
        private readonly List<string> _families;
        private readonly Func<string, List<SubParam>> _paramsOf;
        private readonly Func<string, (double x0, double x1, double z0, double z1)?> _boxOf;
        private readonly Func<AxisData, SubSection> _sectionOf;
        private readonly Func<AxisData, string, Control> _previewOf;   // the real section / plan at the axis (Revit preview); mode "section" / "plan"
        private string _mode = "section";
        private readonly Func<string, string> _applyAxis;        // places the families of one axis now (returns the report)
        private Control _preview;
        // (axis, {depth, crop width, crop height}) -> sets what is not NaN (null = only read); returns the values now (m)
        private readonly Func<AxisData, string, double[], double[]> _sectionSize;
        private NumericUpDown _depth, _cropW, _cropH;
        private string _previewAxis;
        private readonly Label _secHead;
        private CheckBox _measure;
        private Label _measureText;
        private Label _copyLbl, _viewNote;
        private Button _copyBtn, _addBtn, _upBtn, _downBtn, _removeBtn, _loadBtn, _browse, _see, _createBtn, _saveBtn, _closeBtn;
        private Label _xlLbl;
        private Panel _line;
        public Func<string[], List<string>> LoadFamilies;       // loads .rfa files into the project, returns the family types now
        private readonly Dictionary<string, SubSection> _sections = new Dictionary<string, SubSection>(StringComparer.OrdinalIgnoreCase);

        public BridgeExcel Excel { get; private set; }
        public readonly Dictionary<string, SubAxis> Axes;
        public bool CreateNow { get; private set; }
        public string ExcelPath => _tb.Text.Trim();
        public string SectionAxis { get; private set; }     // See Section: the axis to open
        public string SectionMode => _mode;                  // "section" / "plan"
        public int StartRow = -1;                            // the row to select when the dialog opens again
        public List<string> PlanViews;                       // plans where a line can be picked
        public string PickView;                              // the plan chosen for picking
        public string Note;                                  // a message to show when it opens (the pick result)
        public SubPick Pick { get; private set; }            // set when the dialog closes for a pick
        private ComboBox _pickView;
        private Label _pickLbl;
        private static System.Drawing.Rectangle? s_bounds;   // where the dialog was (it reopens there after a pick)
        private static bool s_max;
        public string StartAxis;                             // the tab to open on

        private readonly TextBox _tb;
        private readonly Label _status, _famHead, _detHead, _summary, _spHint;
        private readonly TabControl _tabs;
        private LongView _long;
        public Func<BridgeExcel, LongData> LongOf;          // the longitudinal cut of the bridge (Revit side)
        private System.Windows.Forms.Timer _auto;                                // places the axis by itself after a change (no Apply button)

        // the longitudinal view: the Excel part at once, the Revit cut (slow) once after the window is drawn, then kept
        // per Excel file (path + time). modelChanged = families were placed: cut again.
        private readonly Dictionary<string, LongData> _longCache = new Dictionary<string, LongData>(StringComparer.OrdinalIgnoreCase);
        private string _longPending;

        private string LongKey()
        {
            string p = _tb?.Text.Trim() ?? "";
            try { return p + "|" + File.GetLastWriteTimeUtc(p).Ticks; } catch { return p; }
        }

        private void RefreshLong(bool modelChanged = false)
        {
            if (_long == null) return;
            if (LongOf == null || Excel == null) { _long.Data = null; _long.Invalidate(); return; }
            string key = LongKey();
            if (modelChanged) _longCache.Clear();
            if (_longCache.TryGetValue(key, out LongData hit)) { _long.Data = hit; _long.Invalidate(); return; }
            if (_longPending == key) return;                       // already on its way
            try { _long.Data = LongData.FromExcel(Excel); } catch { _long.Data = null; }
            _long.Message = "Reading the bridge from Revit...";
            _long.Invalidate();
            _longPending = key;
            BridgeExcel bx = Excel;
            Action cut = () =>
            {
                if (_longPending != key || IsDisposed) return;
                Cursor old = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;
                try
                {
                    LongData d = LongOf(bx);
                    _longCache[key] = d;
                    if (Excel == bx) _long.Data = d;
                }
                catch (Exception ex) { _long.Message = "The bridge view could not be drawn: " + ex.Message; }
                finally { _longPending = null; Cursor.Current = old; }
                _long.Invalidate();
                SnapLater();
            };
            if (IsHandleCreated) BeginInvoke(cut); else cut();
        }

        // (no window pictures here: drawing the window to a picture made the Revit preview flicker and reload)
        private void SnapLater() { }
        private readonly CheckBox _create;
        private readonly ComboBox _copyFrom;
        private readonly DataGridView _grid, _params;
        private readonly Panel _bear;
        private readonly NumericUpDown _count;
        private readonly TextBox _left, _right, _spacings;
        private readonly RadioButton _equal, _typed;
        private readonly DoubleBufferedPanel _view;
        private readonly Button _prev, _next;
        private bool _loading, _bearOn, _abutOn;
        private Panel _abutPanel, _repPanel;
        private CheckBox _swap, _rep;
        private NumericUpDown _repCount;
        private TextBox _repLeft, _repRight, _repSp;
        private RadioButton _repEqual, _repTyped, _repCenter;
        private bool _repOn;

        private sealed class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }
        }

        public SubstructureForm(List<string> families, Dictionary<string, SubAxis> saved, string excelPath,
                                Func<string, List<SubParam>> paramsOf,
                                Func<string, (double x0, double x1, double z0, double z1)?> boxOf,
                                Func<AxisData, SubSection> sectionOf,
                                Func<AxisData, string, Control> previewOf = null,
                                Func<string, string> applyAxis = null,
                                Func<AxisData, string, double[], double[]> sectionSize = null)
        {
            _sectionSize = sectionSize;
            _previewOf = previewOf;
            _applyAxis = applyAxis;
            _families = families;
            _paramsOf = paramsOf;
            _boxOf = boxOf;
            _sectionOf = sectionOf;
            Axes = saved ?? new Dictionary<string, SubAxis>(StringComparer.OrdinalIgnoreCase);

            Text = "AB Bridges - Substructure";
            Font = new System.Drawing.Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            MinimumSize = new Size(1100, 860);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1240, 1000);
            BackColor = Color.White;

            // ---- Excel
            _xlLbl = new Label { Text = "Excel file", Location = new Point(12, 15), AutoSize = true };
            Controls.Add(_xlLbl);
            _tb = new TextBox { Location = new Point(80, 11), Size = new Size(520, 25), Text = excelPath ?? "" };
            Controls.Add(_tb);
            Button browse = Flat("Browse...", new Point(608, 9), 90, false);
            _browse = browse;
            browse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Title = "Choose the bridge Excel (from AB Bridges in Civil 3D)", Filter = "Excel (*.xlsx)|*.xlsx" })
                {
                    if (File.Exists(_tb.Text)) dlg.InitialDirectory = Path.GetDirectoryName(_tb.Text);
                    if (dlg.ShowDialog(this) == DialogResult.OK) { _tb.Text = dlg.FileName; LoadExcel(); }
                }
            };
            Controls.Add(browse);
            _tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { LoadExcel(); e.SuppressKeyPress = true; } };
            _status = new Label { Location = new Point(710, 14), Size = new Size(520, 20), ForeColor = Green, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(_status);

            // ---- axes
            _tabs = new TabControl { Location = new Point(12, 44), Size = new Size(506, 26), Padding = new Point(14, 4) };
            _tabs.SelectedIndexChanged += (s, e) => ShowAxis();
            Controls.Add(_tabs);
            _create = new CheckBox { Text = "Create this axis", Location = new Point(14, 78), AutoSize = true, Checked = true };
            _create.CheckedChanged += (s, e) => { if (_loading || Cur == null) return; Cur.Create = _create.Checked; UpdateTabs(); };
            Controls.Add(_create);
            _copyLbl = new Label { Text = "Copy from", Location = new Point(250, 80), AutoSize = true };
            Controls.Add(_copyLbl);
            _copyFrom = new ComboBox { Location = new Point(330, 76), Size = new Size(100, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            Controls.Add(_copyFrom);
            _copyBtn = Flat("Copy", new Point(438, 74), 80, false);
            _copyBtn.Click += (s, e) => CopyFrom();
            Controls.Add(_copyBtn);

            // ---- families of the axis
            _famHead = new Label { Location = new Point(12, 110), Size = new Size(506, 18), Font = new System.Drawing.Font("Segoe UI Semibold", 9.5f), ForeColor = Navy };
            Controls.Add(_famHead);
            _grid = new DataGridView
            {
                Location = new Point(12, 130), Size = new Size(506, 168), AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnEnter, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 26, EnableHeadersVisualStyles = false
            };
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(243, 244, 246);
            _grid.ColumnHeadersDefaultCellStyle.Font = new System.Drawing.Font("Segoe UI Semibold", 9f);
            _grid.RowTemplate.Height = 26;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "No", HeaderText = "#", Width = 32, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            // the family is chosen in a list with a search box (click the cell)
            var fam = new DataGridViewTextBoxColumn { Name = "Fam", HeaderText = "Family : Type  (▾ to choose)", Width = 214, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable };
            fam.DefaultCellStyle.ForeColor = Navy;
            _grid.Columns.Add(fam);
            // the small button that opens the family list (clicking the name only selects the row)
            var pick = new DataGridViewButtonColumn { Name = "Pick", HeaderText = "", Text = "▾", UseColumnTextForButtonValue = true, Width = 26,
                                                      FlatStyle = FlatStyle.Flat, SortMode = DataGridViewColumnSortMode.NotSortable };
            _grid.Columns.Add(pick);
            var surf = new DataGridViewComboBoxColumn { Name = "Surf", HeaderText = "Surface", Width = 72, FlatStyle = FlatStyle.Flat, SortMode = DataGridViewColumnSortMode.NotSortable };
            surf.Items.AddRange("Design", "NGL", "Soffit");
            _grid.Columns.Add(surf);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "V", HeaderText = "Vertical (m)", Width = 82, SortMode = DataGridViewColumnSortMode.NotSortable });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "H", HeaderText = "From center (m)", Width = 102, SortMode = DataGridViewColumnSortMode.NotSortable });
            // pick the offset from a line in a plan (linked CAD, model / detail line)
            _grid.Columns.Add(new DataGridViewButtonColumn { Name = "PickH", HeaderText = "", Text = "⌖", UseColumnTextForButtonValue = true, Width = 28,
                                                             FlatStyle = FlatStyle.Flat, SortMode = DataGridViewColumnSortMode.NotSortable,
                                                             ToolTipText = "Pick the center line in a plan (CAD link / lines): the offset is measured where it crosses the axis" });
            _grid.Columns["V"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.Columns["H"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.DataError += (s, e) => { e.ThrowException = false; };
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewComboBoxCell) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.CellValueChanged += (s, e) => { if (!_loading && e.RowIndex >= 0) ReadRow(e.RowIndex, e.ColumnIndex); };
            _grid.SelectionChanged += (s, e) => { if (!_loading) ShowDetails(); };
            _grid.CellClick += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                string cn = _grid.Columns[e.ColumnIndex].Name;
                if (cn == "Pick") PickFamily(e.RowIndex);
                else if (cn == "PickH") RequestPick(e.RowIndex);
            };
            _grid.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && _grid.CurrentCell != null && _grid.Columns[_grid.CurrentCell.ColumnIndex].Name == "Fam")
                { PickFamily(_grid.CurrentCell.RowIndex); e.Handled = true; }
            };
            Controls.Add(_grid);

            Button add = Flat("+ Add family", new Point(12, 304), 110, false);
            add.Click += (s, e) => { if (Cur == null) return; Cur.Rows.Add(new SubRow()); FillGrid(Cur.Rows.Count - 1); Changed(); };
            Button up = Flat("↑", new Point(128, 304), 40, false);
            up.Click += (s, e) => MoveRow(-1);
            Button down = Flat("↓", new Point(172, 304), 40, false);
            down.Click += (s, e) => MoveRow(+1);
            Button remove = Flat("Remove", new Point(218, 304), 80, false);
            remove.Click += (s, e) =>
            {
                int i = SelRow;
                if (Cur == null || i < SubAxis.First) return;   // the bearing / abutment rows stay (choose (none) for none)
                Cur.Rows.RemoveAt(i);
                FillGrid(Math.Min(i, Cur.Rows.Count - 1));
                Changed();
            };
            Controls.AddRange(new Control[] { add, up, down, remove });
            // load .rfa files into the project without opening them; the new types show in every family list at once
            _loadBtn = Flat("Load family...", new Point(304, 304), 120, true);
            _loadBtn.BackColor = Orange; _loadBtn.FlatAppearance.BorderColor = Orange;
            _loadBtn.Click += (s, e) => LoadFamilyFiles();
            Controls.Add(_loadBtn);
            // the plan where lines are picked
            _pickLbl = new Label { Text = "⌖ pick in", Location = new Point(312, 311), AutoSize = true, ForeColor = Grey };
            _pickView = new ComboBox { Location = new Point(376, 306), Size = new Size(142, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            Controls.Add(_pickLbl);
            Controls.Add(_pickView);
            _addBtn = add; _upBtn = up; _downBtn = down; _removeBtn = remove;

            // ---- details: bearing settings / family parameters
            _detHead = new Label { Location = new Point(12, 346), Size = new Size(506, 18), Font = new System.Drawing.Font("Segoe UI Semibold", 9.5f), ForeColor = Navy };
            Controls.Add(_detHead);

            // bearings: one row centered on the deck center (+ From center in the grid), all at one level (Surface + Vertical)
            _bear = new Panel { Location = new Point(12, 366), Size = new Size(506, 104) };
            _bear.Controls.Add(new Label { Text = "Number of bearings", Location = new Point(0, 6), AutoSize = true });
            _count = new NumericUpDown { Location = new Point(140, 3), Size = new Size(70, 25), Minimum = 1, Maximum = 30, Value = 2, TextAlign = HorizontalAlignment.Right };
            _bear.Controls.Add(_count);
            _left = new TextBox { Visible = false };      // (no edge offsets any more)
            _right = new TextBox { Visible = false };
            _bear.Controls.Add(new Label { Text = "Spacing", Location = new Point(0, 36), AutoSize = true });
            _equal = new RadioButton { Text = "Equal", Location = new Point(140, 34), AutoSize = true, Checked = true };
            _typed = new RadioButton { Text = "Typed one by one", Location = new Point(210, 34), AutoSize = true };
            _bear.Controls.Add(_equal);
            _bear.Controls.Add(_typed);
            _bear.Controls.Add(new Label { Text = "Spacing (m)", Location = new Point(0, 64), AutoSize = true });
            _bear.Controls.Add(new Label { Text = "All bearings at one level: Surface at the deck center + Vertical; centered on the deck center (+ From center).",
                                           Location = new Point(0, 88), Size = new Size(506, 16), ForeColor = Grey });
            _spacings = new TextBox { Location = new Point(140, 61), Size = new Size(240, 25) };
            _bear.Controls.Add(_spacings);
            _spHint = new Label { Location = new Point(388, 64), Size = new Size(118, 18), ForeColor = Grey };
            _bear.Controls.Add(_spHint);
            _count.ValueChanged += (s, e) => ReadBearing(_count);
            _left.TextChanged += (s, e) => ReadBearing(_left);
            _right.TextChanged += (s, e) => ReadBearing(_right);
            _spacings.TextChanged += (s, e) => ReadBearing(_spacings);
            _equal.CheckedChanged += (s, e) => ReadBearing(_equal);
            Controls.Add(_bear);

            // abutment: point 1 / point 2 on the left / right deck edges (Swap flips them), Design at the edges + Vertical
            _abutPanel = new Panel { Location = new Point(12, 366), Size = new Size(506, 50), Visible = false };
            _swap = new CheckBox { Text = "Swap points (point 1 on the right edge, point 2 on the left)", Location = new Point(0, 4), AutoSize = true };
            _swap.CheckedChanged += (s, e) =>
            {
                if (_loading || Cur == null) return;
                Cur.Abut.Swap = _swap.Checked;
                Changed();
            };
            _abutPanel.Controls.Add(_swap);
            _abutPanel.Controls.Add(new Label { Text = "Points: the deck edges at the end section (the first at the start, the last at the end), moved square to it out of the bridge by \"From center\" (0.10) - same skew; + Vertical.",
                                                Location = new Point(0, 28), Size = new Size(506, 30), ForeColor = Grey });
            Controls.Add(_abutPanel);

            // a family row repeated along the axis: count, from the left / right deck edges, equal or typed spacing (one level for all)
            _repPanel = new Panel { Location = new Point(12, 366), Size = new Size(506, 112), Visible = false };
            _rep = new CheckBox { Text = "Repeat along the axis", Location = new Point(0, 2), AutoSize = true };
            _repPanel.Controls.Add(_rep);
            _repPanel.Controls.Add(new Label { Text = "Number", Location = new Point(200, 4), AutoSize = true });
            _repCount = new NumericUpDown { Location = new Point(260, 1), Size = new Size(60, 25), Minimum = 1, Maximum = 50, Value = 2, TextAlign = HorizontalAlignment.Right };
            _repPanel.Controls.Add(_repCount);
            _repPanel.Controls.Add(new Label { Text = "From left edge (m)", Location = new Point(0, 34), AutoSize = true });
            _repLeft = new TextBox { Location = new Point(130, 31), Size = new Size(70, 25), TextAlign = HorizontalAlignment.Right };
            _repPanel.Controls.Add(_repLeft);
            _repPanel.Controls.Add(new Label { Text = "From right edge (m)", Location = new Point(220, 34), AutoSize = true });
            _repRight = new TextBox { Location = new Point(360, 31), Size = new Size(70, 25), TextAlign = HorizontalAlignment.Right };
            _repPanel.Controls.Add(_repRight);
            _repEqual = new RadioButton { Text = "Equal between the edges", Location = new Point(0, 60), AutoSize = true, Checked = true };
            _repTyped = new RadioButton { Text = "Typed from the left", Location = new Point(170, 60), AutoSize = true };
            _repCenter = new RadioButton { Text = "Centered (count + spacing)", Location = new Point(310, 60), AutoSize = true };
            _repPanel.Controls.Add(_repEqual);
            _repPanel.Controls.Add(_repTyped);
            _repPanel.Controls.Add(_repCenter);
            _repPanel.Controls.Add(new Label { Text = "Spacing (m)", Location = new Point(0, 88), AutoSize = true });
            _repSp = new TextBox { Location = new Point(130, 85), Size = new Size(300, 25) };
            _repPanel.Controls.Add(_repSp);
            _rep.CheckedChanged += (s, e) => ReadRepeat(_rep);
            _repCount.ValueChanged += (s, e) => ReadRepeat(_repCount);
            _repLeft.TextChanged += (s, e) => ReadRepeat(_repLeft);
            _repRight.TextChanged += (s, e) => ReadRepeat(_repRight);
            _repEqual.CheckedChanged += (s, e) => { if (_repEqual.Checked) ReadRepeat(_repEqual); };
            _repTyped.CheckedChanged += (s, e) => { if (_repTyped.Checked) ReadRepeat(_repTyped); };
            _repCenter.CheckedChanged += (s, e) => { if (_repCenter.Checked) ReadRepeat(_repCenter); };
            _repSp.TextChanged += (s, e) => ReadRepeat(_repSp);
            Controls.Add(_repPanel);

            _params = new DataGridView
            {
                Location = new Point(12, 480), Size = new Size(506, 200), AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnEnter, ColumnHeadersHeight = 24, EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
            };
            _params.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(243, 244, 246);
            _params.Columns.Add(new DataGridViewTextBoxColumn { Name = "N", HeaderText = "Family parameter", Width = 250, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            _params.Columns.Add(new DataGridViewTextBoxColumn { Name = "K", HeaderText = "", Width = 70, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            _params.Columns.Add(new DataGridViewTextBoxColumn { Name = "Val", HeaderText = "Value", Width = 164, SortMode = DataGridViewColumnSortMode.NotSortable });
            _params.Columns["K"].DefaultCellStyle.ForeColor = Grey;
            _params.Columns["Val"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _params.DataError += (s, e) => { e.ThrowException = false; };
            _params.CurrentCellDirtyStateChanged += (s, e) => { if (_params.IsCurrentCellDirty && _params.CurrentCell is DataGridViewCheckBoxCell) _params.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _params.CellValueChanged += (s, e) => { if (!_loading && e.RowIndex >= 0 && e.ColumnIndex == 2) ReadParam(e.RowIndex); };
            Controls.Add(_params);

            // ---- section
            _view = new DoubleBufferedPanel { Location = new Point(530, 44), Size = new Size(698, 598), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            if (_previewOf == null)
            {
                // no Revit preview: the drawn figure
                _view.Paint += (s, e) => PaintSection(e.Graphics, _view.ClientRectangle);
                _view.MouseDown += ViewDown;
                _view.MouseMove += ViewMove;
                _view.MouseUp += (s, e) => { _drag = null; };
            }
            _secHead = new Label { Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 5, 0, 0), Font = new System.Drawing.Font("Segoe UI Semibold", 9.5f),
                                   ForeColor = Navy, BackColor = Color.FromArgb(243, 244, 246) };
            if (_previewOf != null)
            {
                var bar = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Navy, Padding = new Padding(6, 4, 6, 4) };
                _secHead.Dock = DockStyle.Fill;
                _secHead.BackColor = Navy;
                _secHead.ForeColor = Color.White;
                _secHead.Padding = new Padding(4, 6, 0, 0);
                _measure = new CheckBox { Appearance = Appearance.Button, Text = "Measure", Dock = DockStyle.Right, Width = 90, FlatStyle = FlatStyle.Flat,
                                          BackColor = Color.White, ForeColor = Navy, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
                _measure.CheckedChanged += (s, e) =>
                {
                    _measure.BackColor = _measure.Checked ? Orange : Color.White;
                    _measure.ForeColor = _measure.Checked ? Color.White : Navy;
                    if (_measure.Checked) StartMeasure(); else StopMeasure();
                };
                var fit = new Button { Text = "Fit", Dock = DockStyle.Right, Width = 60, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Navy, Cursor = Cursors.Hand };
                fit.Click += (s, e) => { _measure.Checked = false; if (_preview is IMeasurable m) m.ZoomFit(); };
                var gap = new Panel { Dock = DockStyle.Right, Width = 6, BackColor = Navy };
                // second bar: how deep the section looks behind the cut (far clip, 0.01 m = only what is on the axis line)
                // and the size of the crop (around its middle)
                var bar2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, BackColor = Color.FromArgb(226, 232, 240), Padding = new Padding(6, 4, 6, 0), WrapContents = false };
                NumericUpDown Box(decimal min, decimal max, decimal step, decimal val) =>
                    new NumericUpDown { Width = 78, Minimum = min, Maximum = max, DecimalPlaces = 2, Increment = step, Value = val, TextAlign = HorizontalAlignment.Right,
                                        Enabled = _sectionSize != null, Margin = new Padding(0, 0, 14, 0) };
                Label Lbl(string t) => new Label { Text = t, AutoSize = true, ForeColor = Navy, Margin = new Padding(0, 5, 4, 0) };
                _depth = Box(0.01m, 500m, 0.5m, 0.01m);
                _cropW = Box(1m, 2000m, 1m, 30m);
                _cropH = Box(1m, 2000m, 1m, 20m);
                // the section is made again once, half a second after the last change (not on every arrow click)
                _depth.ValueChanged += (s, e) => SizeLater(0);
                _cropW.ValueChanged += (s, e) => SizeLater(1);
                _cropH.ValueChanged += (s, e) => SizeLater(2);
                bar2.Controls.Add(Lbl("Depth (m)"));
                bar2.Controls.Add(_depth);
                bar2.Controls.Add(Lbl("Crop width (m)"));
                bar2.Controls.Add(_cropW);
                bar2.Controls.Add(Lbl("Crop height (m)"));
                bar2.Controls.Add(_cropH);
                bar.Controls.Add(_secHead);
                bar.Controls.Add(fit);
                bar.Controls.Add(gap);
                bar.Controls.Add(_measure);
                _secHead.BringToFront();      // the title fills what the buttons leave
                _measureText = new Label { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(8, 5, 0, 0), BackColor = Color.FromArgb(255, 247, 237),
                                           ForeColor = Red, Font = new System.Drawing.Font("Segoe UI Semibold", 9.5f) };
                // Section / Plan tabs under the view
                var tabsBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, BackColor = Color.FromArgb(226, 232, 240), Padding = new Padding(6, 3, 6, 0), WrapContents = false };
                RadioButton Tab(string text, string mode)
                {
                    var rb = new RadioButton { Appearance = Appearance.Button, Text = text, Width = 110, Height = 26, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
                                               Checked = mode == _mode, BackColor = mode == _mode ? Navy : Color.White, ForeColor = mode == _mode ? Color.White : Navy,
                                               Margin = new Padding(0, 0, 6, 0), Cursor = Cursors.Hand };
                    rb.CheckedChanged += (s, e) =>
                    {
                        rb.BackColor = rb.Checked ? Navy : Color.White;
                        rb.ForeColor = rb.Checked ? Color.White : Navy;
                        if (rb.Checked && _mode != mode) { _mode = mode; ShowPreview(true); }
                    };
                    return rb;
                }
                tabsBar.Controls.Add(Tab("Section", "section"));
                tabsBar.Controls.Add(Tab("Plan", "plan"));
                _view.Controls.Add(tabsBar);
                _view.Controls.Add(_measureText);
                _view.Controls.Add(bar2);
                _view.Controls.Add(bar);       // the last added docks first: the title bar on top, the size bar under it
                _view.BackColor = Color.FromArgb(248, 250, 252);
            }
            Controls.Add(_view);
            Button apply = Flat("Apply", new Point(962, 648), 120, true);
            apply.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            apply.Click += (s, e) => ApplyNow();    // places the families of this axis and shows them in the section
            _applyBtn = apply;
            Controls.Add(apply);
            FormClosing += (s, e) =>
            {
                s_max = WindowState == FormWindowState.Maximized;
                if (WindowState == FormWindowState.Normal) s_bounds = Bounds;
                PickView = _pickView?.SelectedItem as string ?? PickView;
                DropPreview();
                StopMeasure();
            };
            Button see = Flat("Show in Revit", new Point(1098, 648), 130, true);
            _see = see;
            see.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            see.Click += (s, e) =>
            {
                if (CurAxis == null) { MessageBox.Show(this, "Choose the bridge Excel first.", "AB Bridges"); return; }
                SectionAxis = CurAxis.Name;
                Finish(false);
            };
            Controls.Add(see);
            _viewNote = new Label { Text = "Every change is placed and shown at once.  Show in Revit: opens this section in Revit; run Substructure again to come back.", Location = new Point(530, 656),
                                    Size = new Size(425, 18), ForeColor = Grey, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            Controls.Add(_viewNote);

            // ---- bottom
            var line = new Panel { Location = new Point(0, 690), Size = new Size(1240, 1), BackColor = Color.FromArgb(220, 224, 230), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            _line = line;
            Controls.Add(line);
            _prev = Flat("←", new Point(12, 704), 80, false);
            _prev.Click += (s, e) => { if (_tabs.SelectedIndex > 0) _tabs.SelectedIndex--; };
            _next = Flat("→", new Point(98, 704), 80, false);
            _next.Click += (s, e) => { if (_tabs.SelectedIndex < _tabs.TabCount - 1) _tabs.SelectedIndex++; };
            _prev.Anchor = _next.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(_prev);
            Controls.Add(_next);
            _summary = new Label { Location = new Point(190, 711), Size = new Size(690, 20), ForeColor = Green, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(_summary);
            Button create = Flat("Create", new Point(892, 704), 110, true);
            create.Click += (s, e) => Finish(true);
            Button save = Flat("Save && Close", new Point(1008, 704), 110, false);
            save.Click += (s, e) => Finish(false);
            Button close = Flat("Close", new Point(1124, 704), 104, false);
            close.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            create.Anchor = save.Anchor = close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Controls.AddRange(new Control[] { create, save, close });
            _createBtn = create; _saveBtn = save; _closeBtn = close;
            Resize += (s, e) => { Relayout(); LayoutParams(); };
            // ---- header; the axis chips on top; left: families + parameters; right: the live section of the open axis
            foreach (Control c in Controls.Cast<Control>().ToList()) c.Anchor = AnchorStyles.Top | AnchorStyles.Left;   // all placed by LayoutAll
            Controls.Add(new BrandHeader("Substructure"));
            Brand.SetIcon(this);
            _view.BorderStyle = BorderStyle.FixedSingle;
            _tabs.Visible = true;                     // the axis chips: click one or use ← / → (a hidden TabControl does not change axis)
            _viewNote.Visible = false;
            UiTheme.StyleTabs(_tabs);
            _tabs.SelectedIndexChanged += (s2, e2) => SnapLater();      // ShowAxis shows the section of the new axis

            Load += (s, e) =>
            {
                if (s_bounds.HasValue) { StartPosition = FormStartPosition.Manual; Bounds = s_bounds.Value; }
                if (s_max) WindowState = FormWindowState.Maximized;
                if (_pickView != null && PlanViews != null)
                {
                    _pickView.Items.Clear();
                    foreach (string v in PlanViews) _pickView.Items.Add(v);
                    if (PickView != null && _pickView.Items.Contains(PickView)) _pickView.SelectedItem = PickView;
                    else if (_pickView.Items.Count > 0) _pickView.SelectedIndex = 0;
                }
                Relayout();
                LayoutParams();
            };
            CancelButton = close;

            Shown += (s, e) =>
            {
                if (File.Exists(_tb.Text.Trim())) LoadExcel();
                else { _status.ForeColor = Grey; _status.Text = "Choose the bridge Excel (sheet Substructure)."; RefreshLong(); }
                SnapLater();
                if (StartRow >= 0 && Cur != null && StartRow < Cur.Rows.Count) FillGrid(StartRow);
                if (!string.IsNullOrEmpty(Note))
                {
                    _summary.Text = Note;
                    _summary.ForeColor = Note.Contains("nothing changed") || Note.Contains("did not") ? Red : Green;
                }
            };
        }

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

        // ---------------------------------------------------------------- state

        private AxisData CurAxis => Excel != null && _tabs.SelectedIndex >= 0 && _tabs.SelectedIndex < Excel.Axes.Count ? Excel.Axes[_tabs.SelectedIndex] : null;
        private SubAxis Cur => CurAxis != null && Axes.TryGetValue(CurAxis.Name, out SubAxis s) ? s : null;
        private int SelRow => _grid.CurrentRow?.Index ?? (_grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Index : -1);

        private SubSection Section(AxisData a)
        {
            if (a == null) return null;
            if (!_sections.TryGetValue(a.Name, out SubSection s))
            {
                try { s = _sectionOf?.Invoke(a); } catch { s = null; }
                _sections[a.Name] = s;
            }
            return s;
        }

        private void LoadExcel()
        {
            string p = _tb.Text.Trim();
            Excel = null;
            DropCards();
            _tabs.TabPages.Clear();
            _sections.Clear();
            if (!File.Exists(p)) { _status.ForeColor = Red; _status.Text = "The file is not found."; ShowAxis(); return; }
            try
            {
                Excel = BridgeExcel.Load(p, false);
                CreateForm.LastExcel = p;
            }
            catch (Exception ex) { _status.ForeColor = Red; _status.Text = "Could not read the Excel: " + ex.Message; ShowAxis(); return; }
            if (Excel.Axes.Count == 0)
            {
                _status.ForeColor = Red;
                _status.Text = "No Substructure sheet - run Extract Data again in Civil 3D.";
                ShowAxis();
                return;
            }
            for (int ai = 0; ai < Excel.Axes.Count; ai++)
            {
                AxisData a = Excel.Axes[ai];
                if (!Axes.ContainsKey(a.Name)) Axes[a.Name] = new SubAxis { Name = a.Name };
                // a new abutment row: the last axis has its points the other way (the approach slab away from the bridge)
                SubRow ab = Axes[a.Name].Abut;
                if (ab.AutoSwap) { ab.Swap = Excel.Axes.Count > 1 && ai == Excel.Axes.Count - 1; ab.AutoSwap = false; }
                _tabs.TabPages.Add(new TabPage(a.Name));
            }
            _status.ForeColor = Green;
            _status.Text = $"✓ {Excel.BridgeName} · {Excel.Axes.Count} axes · sheet Substructure found";
            UpdateTabs();
            BuildCards();
            int start = string.IsNullOrEmpty(StartAxis) ? 0 : Math.Max(0, Excel.Axes.FindIndex(x => string.Equals(x.Name, StartAxis, StringComparison.OrdinalIgnoreCase)));
            _tabs.SelectedIndex = start;
            ShowAxis();
            RefreshLong();
        }

        private void UpdateTabs()
        {
            if (Excel == null) return;
            for (int i = 0; i < Excel.Axes.Count && i < _tabs.TabCount; i++)
            {
                SubAxis s = Axes[Excel.Axes[i].Name];
                string t = Excel.Axes[i].Name + (!s.Create ? "  (skip)" : s.HasWork ? "  ✓" : "");
                if (_tabs.TabPages[i].Text != t) _tabs.TabPages[i].Text = t;
            }
            var parts = new List<string>();
            foreach (AxisData a in Excel.Axes)
            {
                SubAxis s = Axes[a.Name];
                if (!s.Create) parts.Add(a.Name + " skipped");
                else if (!s.HasWork) parts.Add(a.Name + " empty");
                else
                {
                    parts.Add($"{a.Name}: " + (string.IsNullOrEmpty(s.Abut.Family) ? "" : "abutment + ") + $"{s.Families} families");
                }
            }
            _summary.Text = string.Join(" · ", parts);
            RefreshCards();
        }

        private void ShowAxis()
        {
            _loading = true;
            try
            {
                AxisData a = CurAxis;
                SubAxis s = Cur;
                _create.Enabled = s != null;
                _create.Checked = s?.Create ?? true;
                _copyFrom.Items.Clear();
                if (Excel != null && a != null)
                    foreach (AxisData o in Excel.Axes) if (o.Name != a.Name) _copyFrom.Items.Add(o.Name);
                if (_copyFrom.Items.Count > 0) _copyFrom.SelectedIndex = 0;
                _famHead.Text = a == null ? "Families" : $"Families at {a.Name}  (bearing first, then in order)";
                int i = _tabs.SelectedIndex;
                _prev.Enabled = i > 0;
                _next.Enabled = Excel != null && i < _tabs.TabCount - 1;
                _prev.Text = i > 0 ? "← " + Excel.Axes[i - 1].Name : "←";
                _next.Text = Excel != null && i >= 0 && i < _tabs.TabCount - 1 ? Excel.Axes[i + 1].Name + " →" : "→";
            }
            finally { _loading = false; }
            FillGrid(0);
            ShowPending();
            ShowPreview(false);
        }

        // ---------------------------------------------------------------- the real section (Revit preview)

        private void DropPreview()
        {
            if (_measure != null && _measure.Checked) _measure.Checked = false;
            if (_preview == null) return;
            try { _view.Controls.Remove(_preview); _preview.Dispose(); } catch { }
            _preview = null;
            _previewAxis = null;
        }

        private void ShowPreview(bool force)
        {
            if (_previewOf == null) return;
            AxisData a = CurAxis;
            if (a == null) { DropPreview(); _secHead.Text = ""; return; }
            if (!force && _preview != null && _previewAxis == a.Name + "|" + _mode) return;
            DropPreview();
            _secHead.Text = _mode == "plan"
                ? $"Plan at {a.Name}  ·  St {SubGeo.StationText(a.Station)}  ·  skew {a.SkewDeg.ToString("0.000", CI)}°   (AB Plan - {a.Name}: everything, the families placed in orange)"
                : $"Section at {a.Name}  ·  St {SubGeo.StationText(a.Station)}  ·  skew {a.SkewDeg.ToString("0.000", CI)}°   (AB Section - {a.Name})";
            if (_depth != null) _depth.Enabled = _sectionSize != null && _mode != "plan";
            Cursor old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                Control c = _previewOf(a, _mode);
                if (c != null)
                {
                    c.Dock = DockStyle.Fill;
                    _view.Controls.Add(c);
                    c.BringToFront();
                    _preview = c;
                    _previewAxis = a.Name + "|" + _mode;
                    if (_sectionSize != null && _depth != null)
                    {
                        double[] now = null;
                        try { now = _sectionSize(a, _mode, null); } catch { }
                        if (now != null && now.Length >= 3)
                        {
                            bool was = _loading;
                            _loading = true;
                            void Put(NumericUpDown b, double v) { if (!double.IsNaN(v)) b.Value = Math.Max(b.Minimum, Math.Min(b.Maximum, (decimal)Math.Round(v, 2))); }
                            Put(_depth, now[0]);
                            Put(_cropW, now[1]);
                            Put(_cropH, now[2]);
                            _loading = was;
                        }
                    }

                }
            }
            catch (Exception ex) { _secHead.Text = "The section could not be shown: " + ex.Message; }
            finally { Cursor.Current = old; }
        }

        // ---------------------------------------------------------------- measuring (on a picture of the preview)

        private DoubleBufferedPanel _shot;
        private Bitmap _shotBmp;
        private int _fl, _ft, _fr, _fb;           // the view window on the screen
        private double _vx0, _vy0, _vx1, _vy1;    // what it shows (view feet)
        private (double x, double y)? _m1, _m2, _mh;
        private bool _mhSnapped;

        private void StartMeasure()
        {
            if (!(_preview is IMeasurable pm) || !pm.Frame(out _fl, out _ft, out _fr, out _fb, out _vx0, out _vy0, out _vx1, out _vy1))
            {
                _measureText.Text = "Measure: the section is not ready yet - try again in a moment.";
                _loading = true; _measure.Checked = false; _loading = false;
                StopMeasure();
                return;
            }
            System.Drawing.Rectangle r = _preview.RectangleToScreen(_preview.ClientRectangle);
            _shotBmp?.Dispose();
            _shotBmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height));
            using (Graphics g = Graphics.FromImage(_shotBmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            if (_shot == null)
            {
                _shot = new DoubleBufferedPanel { Cursor = Cursors.Cross };
                _shot.Paint += (s, e) => PaintShot(e.Graphics);
                _shot.MouseDown += ShotDown;
                _shot.MouseMove += (s, e) => { _mh = ShotSnap(e.Location, out _mhSnapped); _shot.Invalidate(); };
                _shot.MouseLeave += (s, e) => { _mh = null; _shot.Invalidate(); };
                _view.Controls.Add(_shot);
            }
            _shot.Bounds = _preview.Bounds;
            _shot.Visible = true;
            _shot.BringToFront();
            _m1 = _m2 = _mh = null;
            _snapEnd = null;                 // the snap lists are made again for this section
            _measureText.Text = "Measure: click two points (they snap to corners and edges; right click = clear).  Measure again to go back to the section.";
        }

        private void StopMeasure()
        {
            if (_shot != null) _shot.Visible = false;
            _shotBmp?.Dispose();
            _shotBmp = null;
            _m1 = _m2 = _mh = null;
            if (_measureText != null) _measureText.Text = "";
        }

        // screen <-> view feet, through the view window
        private (double x, double y) ToView(Point client)
        {
            Point sc = _shot.PointToScreen(client);
            double w = Math.Max(1, _fr - _fl), h = Math.Max(1, _fb - _ft);
            return (_vx0 + (sc.X - _fl) / w * (_vx1 - _vx0), _vy1 - (sc.Y - _ft) / h * (_vy1 - _vy0));
        }

        private PointF ToClient((double x, double y) v)
        {
            double w = Math.Max(1, _fr - _fl), h = Math.Max(1, _fb - _ft);
            Point sc = new Point((int)Math.Round(_fl + (v.x - _vx0) / (_vx1 - _vx0) * w), (int)Math.Round(_ft + (_vy1 - v.y) / (_vy1 - _vy0) * h));
            Point c = _shot.PointToClient(sc);
            return new PointF(c.X, c.Y);
        }

        // every snap: Endpoint, Intersection, Midpoint (within 12 px, in that order of preference), else Nearest on an edge (8 px)
        private string _mhKind = "";
        private List<(double x, double y)> _snapEnd, _snapMid, _snapInt;

        private void BuildSnapLists()
        {
            _snapEnd = new List<(double x, double y)>(); _snapMid = new List<(double x, double y)>(); _snapInt = new List<(double x, double y)>();
            if (!(_preview is IMeasurable pm)) return;
            _snapEnd.AddRange(pm.SnapPoints);
            var segs = pm.SnapSegments.ToList();
            foreach (var s in segs) { _snapEnd.Add(s.a); _snapEnd.Add(s.b); _snapMid.Add(((s.a.x + s.b.x) / 2, (s.a.y + s.b.y) / 2)); }
            int n = Math.Min(segs.Count, 1500);
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var (a, b) = segs[i]; var (c, d) = segs[j];
                    double rx = b.x - a.x, ry = b.y - a.y, sx = d.x - c.x, sy = d.y - c.y, den = rx * sy - ry * sx;
                    if (Math.Abs(den) < 1e-12) continue;
                    double t = ((c.x - a.x) * sy - (c.y - a.y) * sx) / den, u = ((c.x - a.x) * ry - (c.y - a.y) * rx) / den;
                    if (t > 1e-6 && t < 1 - 1e-6 && u > 1e-6 && u < 1 - 1e-6) _snapInt.Add((a.x + rx * t, a.y + ry * t));
                }
        }

        private (double x, double y) ShotSnap(Point p, out bool snapped)
        {
            snapped = false;
            _mhKind = "";
            var res = ToView(p);
            if (!(_preview is IMeasurable pm)) return res;
            if (_snapEnd == null) BuildSnapLists();
            foreach (var (kind, list) in new[] { ("Endpoint", _snapEnd), ("Intersection", _snapInt), ("Midpoint", _snapMid) })
            {
                double best = 12;
                foreach (var q in list)
                {
                    PointF c = ToClient(q);
                    double d = Math.Sqrt((c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y));
                    if (d < best) { best = d; res = q; snapped = true; }
                }
                if (snapped) { _mhKind = kind; return res; }
            }
            double bestE = 8;
            foreach (var sg in pm.SnapSegments)
            {
                PointF a = ToClient(sg.a), b = ToClient(sg.b);
                double vx = b.X - a.X, vy = b.Y - a.Y, l2 = vx * vx + vy * vy;
                if (l2 < 1e-9) continue;
                double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / l2));
                double cx = a.X + vx * t, cy = a.Y + vy * t;
                double d = Math.Sqrt((cx - p.X) * (cx - p.X) + (cy - p.Y) * (cy - p.Y));
                if (d < bestE) { bestE = d; res = (sg.a.x + (sg.b.x - sg.a.x) * t, sg.a.y + (sg.b.y - sg.a.y) * t); snapped = true; }
            }
            if (snapped) _mhKind = "Nearest";
            return res;
        }

        // the AutoCAD-like snap marks: endpoint square, midpoint triangle, intersection X, nearest hourglass
        private static void SnapMark(Graphics g, PointF h, string kind, Pen pen)
        {
            float r = 6;
            switch (kind)
            {
                case "Endpoint": g.DrawRectangle(pen, h.X - r, h.Y - r, 2 * r, 2 * r); break;
                case "Midpoint": g.DrawPolygon(pen, new[] { new PointF(h.X, h.Y - r), new PointF(h.X + r, h.Y + r), new PointF(h.X - r, h.Y + r) }); break;
                case "Intersection": g.DrawLine(pen, h.X - r, h.Y - r, h.X + r, h.Y + r); g.DrawLine(pen, h.X - r, h.Y + r, h.X + r, h.Y - r); break;
                case "Nearest":
                    g.DrawPolygon(pen, new[] { new PointF(h.X - r, h.Y - r), new PointF(h.X + r, h.Y - r), new PointF(h.X - r, h.Y + r), new PointF(h.X + r, h.Y + r) }); break;
                default: g.DrawEllipse(pen, h.X - 4, h.Y - 4, 8, 8); break;
            }
        }

        private void ShotDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { _m1 = _m2 = null; _measureText.Text = "Measure: click two points."; _shot.Invalidate(); return; }
            if (e.Button != MouseButtons.Left) return;
            var v = ShotSnap(e.Location, out _);
            if (_m1 == null || _m2 != null) { _m1 = v; _m2 = null; _measureText.Text = "Measure: the second point (right click = clear)"; }
            else { _m2 = v; _measureText.Text = MeasureText(_m1.Value, _m2.Value); }
            _shot.Invalidate();
        }

        private static string MeasureText((double x, double y) a, (double x, double y) b)
        {
            const double ft = 0.3048;
            double h = Math.Abs(b.x - a.x) * ft, v = Math.Abs(b.y - a.y) * ft;
            return $"H {h.ToString("0.000", CI)} m    V {v.ToString("0.000", CI)} m    L {Math.Sqrt(h * h + v * v).ToString("0.000", CI)} m";
        }

        private void PaintShot(Graphics g)
        {
            if (_shotBmp != null) g.DrawImageUnscaled(_shotBmp, 0, 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var ink = new Pen(Orange, 2f))
            using (var thin = new Pen(Orange, 1f) { DashStyle = DashStyle.Dash })
            using (var green = new Pen(Color.FromArgb(22, 163, 74), 2f))
            {
                if (_mh.HasValue)
                {
                    PointF h = ToClient(_mh.Value);
                    SnapMark(g, h, _mhSnapped ? _mhKind : "", _mhSnapped ? green : ink);
                    if (_mhSnapped && _mhKind.Length > 0)
                        using (var f = new System.Drawing.Font("Segoe UI", 8f))
                        using (var kb = new SolidBrush(Color.FromArgb(22, 120, 60)))
                            g.DrawString(_mhKind, f, kb, h.X + 9, h.Y + 6);
                }
                if (_m1 == null) return;
                PointF a = ToClient(_m1.Value);
                g.DrawRectangle(ink, a.X - 5, a.Y - 5, 10, 10);
                var endV = _m2 ?? _mh;
                if (!endV.HasValue) return;
                PointF b = ToClient(endV.Value);
                var corner = new PointF(b.X, a.Y);
                g.DrawLine(thin, a, corner);
                g.DrawLine(thin, corner, b);
                g.DrawLine(ink, a, b);
                if (_m2.HasValue) g.DrawRectangle(ink, b.X - 5, b.Y - 5, 10, 10);
                string t = MeasureText(_m1.Value, endV.Value);
                using (var f = new System.Drawing.Font("Segoe UI Semibold", 9.5f))
                {
                    SizeF sz = g.MeasureString(t, f);
                    float tx = (a.X + b.X) / 2 + 8, ty = (a.Y + b.Y) / 2 - sz.Height - 6;
                    using (var back = new SolidBrush(Color.FromArgb(235, 255, 255, 255))) g.FillRectangle(back, tx - 3, ty - 1, sz.Width + 6, sz.Height + 2);
                    using (var br = new SolidBrush(Red)) g.DrawString(t, f, br, tx, ty);
                }
            }
        }

        private System.Windows.Forms.Timer _sizeTimer;
        private readonly HashSet<int> _sizeWhich = new HashSet<int>();
        private void SizeLater(int which)
        {
            if (_loading || _sectionSize == null) return;
            _sizeWhich.Add(which);
            if (_sizeTimer == null)
            {
                _sizeTimer = new System.Windows.Forms.Timer { Interval = 500 };
                _sizeTimer.Tick += (s, e) =>
                {
                    _sizeTimer.Stop();
                    var w = _sizeWhich.ToList(); _sizeWhich.Clear();
                    SetSizes(w);
                };
            }
            _sizeTimer.Stop(); _sizeTimer.Start();
        }

        private void SetSizes(List<int> which)
        {
            if (_loading || _sectionSize == null || which.Count == 0) return;
            AxisData a = CurAxis;
            if (a == null) return;
            var v = new[] { double.NaN, double.NaN, double.NaN };
            if (which.Contains(0)) v[0] = (double)_depth.Value;
            if (which.Contains(1)) v[1] = (double)_cropW.Value;
            if (which.Contains(2)) v[2] = (double)_cropH.Value;
            DropPreview();
            try { _sectionSize(a, _mode, v); } catch (Exception ex) { _secHead.Text = "The section could not be changed: " + ex.Message; }
            ShowPreview(true);
        }

        private void SetSize(int which)
        {
            if (_loading || _sectionSize == null) return;
            AxisData a = CurAxis;
            if (a == null) return;
            var v = new[] { double.NaN, double.NaN, double.NaN };
            v[which] = which == 0 ? (double)_depth.Value : which == 1 ? (double)_cropW.Value : (double)_cropH.Value;
            DropPreview();
            try { _sectionSize(a, _mode, v); } catch (Exception ex) { _secHead.Text = "The section could not be changed: " + ex.Message; }
            ShowPreview(true);
        }

        private void ApplyNow() => ApplyNow(false);

        private void ApplyNow(bool quiet)
        {
            AxisData a = CurAxis;
            SubAxis s = Cur;
            if (a == null || s == null || _applyAxis == null) return;
            if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
            if (_params.IsCurrentCellInEditMode) _params.EndEdit();
            if (!s.HasWork) { if (!quiet) MessageBox.Show(this, $"Choose the families of {a.Name} first.", "AB Bridges"); return; }
            string report;
            Cursor old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try { report = _applyAxis(a.Name); }
            catch (Exception ex) { report = "Failed: " + ex.Message; }
            finally { Cursor.Current = old; }
            _sections.Remove(a.Name);
            _pending.Remove(a.Name);
            ShowPending();
            // the open preview shows the new families by itself: it is not made again (that blinked and zoomed to fit)
            ShowPreview(false);
            QueueCard(a.Name, true);      // the picture of this card again
            if (!string.IsNullOrEmpty(report))
            {
                bool problem = report.Contains("not placed") || report.Contains("no ") || report.StartsWith("Failed");
                if (problem) MessageBox.Show(this, report, "AB Bridges - " + a.Name);
                else _summary.Text = report.Split('\n')[0];
            }
        }

        private void FillGrid(int select)
        {
            _loading = true;
            try
            {
                _grid.Rows.Clear();
                SubAxis s = Cur;
                if (s != null)
                {
                    for (int i = 0; i < s.Rows.Count; i++)
                    {
                        SubRow r = s.Rows[i];
                        string f = string.IsNullOrEmpty(r.Family) ? None : r.Family;
                        if (f != None && !_families.Contains(f)) f += "   (not in this project)";
                        int k = _grid.Rows.Add(r.IsBearing ? "B" : r.IsAbut ? "A" : (i - SubAxis.First + 1).ToString(CI), f, null, r.Surface, r.VOff.ToString("0.000", CI), HText(r));
                        DataGridViewRow row = _grid.Rows[k];
                        if (r.Reserved)
                        {
                            Color mark = r.IsBearing ? Orange : Color.FromArgb(13, 148, 136);
                            row.Cells["No"].Style.BackColor = mark;
                            row.Cells["No"].Style.ForeColor = Color.White;
                            row.Cells["No"].Style.SelectionBackColor = mark;
                            row.Cells["No"].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                            row.Cells["Fam"].Style.Font = new System.Drawing.Font("Segoe UI Semibold", 9f);
                            if (r.IsAbut) row.Cells["H"].ToolTipText = "Abutment: distance out of the bridge, square to the end section (the first at the start, the last at the end), m";

                        }
                    }
                    if (select >= 0 && select < _grid.Rows.Count)
                    {
                        _grid.ClearSelection();
                        _grid.CurrentCell = _grid.Rows[select].Cells["Fam"];
                        _grid.Rows[select].Selected = true;
                    }
                }
            }
            finally { _loading = false; }
            ShowDetails();
            UpdateTabs();
            _view.Invalidate();
        }

        private string HText(SubRow r) =>
            r.Repeat && !r.Reserved && !r.Centered ? $"x{r.Count} edges" : r.HOff.ToString("0.000", CI);

        private static bool Num(object v, out double d)
        {
            string t = (Convert.ToString(v, CI) ?? "").Trim().Replace(',', '.');
            return double.TryParse(t, NumberStyles.Float, CI, out d);
        }

        private void ReadRow(int i, int col)
        {
            SubAxis s = Cur;
            if (s == null || i >= s.Rows.Count) return;
            SubRow r = s.Rows[i];
            DataGridViewRow row = _grid.Rows[i];
            string name = _grid.Columns[col].Name;
            if (name == "Fam")
            {
                string f = Convert.ToString(row.Cells["Fam"].Value) ?? "";
                int cut = f.IndexOf("   (not in this project)", StringComparison.Ordinal);
                if (cut >= 0) f = f.Substring(0, cut);
                f = f == None ? "" : f;
                if (f != r.Family) { r.Family = f; r.Params.Clear(); }
                if (r.IsBearing) { _loading = true; row.Cells["H"].Value = HText(r); _loading = false; }
                ShowDetails();
            }
            else if (name == "Surf") r.Surface = Convert.ToString(row.Cells["Surf"].Value) ?? "Design";
            else if (name == "V") { if (Num(row.Cells["V"].Value, out double v)) r.VOff = v; }
            else if (name == "H" && !(r.Repeat && !r.Reserved && !r.Centered)) { if (Num(row.Cells["H"].Value, out double h)) r.HOff = h; }
            Changed();
        }

        // a change is kept and waits for Apply (nothing is placed while editing)
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Button _applyBtn;

        private void Changed()
        {
            if (_applyAxis != null && !_loading && CurAxis != null) { _pending.Add(CurAxis.Name); ShowPending(); }
            UpdateTabs();
            _view.Invalidate();
        }

        private void ShowPending()
        {
            if (_applyBtn == null) return;
            bool on = CurAxis != null && _pending.Contains(CurAxis.Name);
            _applyBtn.Text = on ? "Apply  ●" : "Apply";
            _applyBtn.BackColor = on ? Orange : Navy;
            _applyBtn.FlatAppearance.BorderColor = on ? Orange : Navy;
        }

        private void MoveRow(int d)
        {
            SubAxis s = Cur;
            int i = SelRow;
            if (s == null || i < SubAxis.First) return;      // the bearing and abutment rows stay first
            int j = i + d;
            if (j < SubAxis.First || j >= s.Rows.Count) return;
            SubRow t = s.Rows[i];
            s.Rows[i] = s.Rows[j];
            s.Rows[j] = t;
            FillGrid(j);
            Changed();
        }

        private void CopyFrom()
        {
            SubAxis s = Cur;
            string from = _copyFrom.SelectedItem as string;
            if (s == null || string.IsNullOrEmpty(from) || !Axes.TryGetValue(from, out SubAxis src)) return;
            if (s.HasWork && MessageBox.Show(this, $"Replace what is set at {s.Name} with the families of {from}?", "AB Bridges",
                                             MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            bool swap = s.Abut.Swap;              // the abutment keeps this axis's point order
            s.Rows = src.Rows.Select(r => r.Clone()).ToList();
            s.Abut.Swap = swap;
            FillGrid(0);
            Changed();
        }

        // close for a pick: one line (center) for a row / the bearings, two lines (first and last) for a repeated row
        private void RequestPick(int i)
        {
            SubAxis s = Cur;
            if (s == null || CurAxis == null || i < 0 || i >= s.Rows.Count) return;
            SubRow r = s.Rows[i];
            if (r.IsAbut) { MessageBox.Show(this, "The abutment is set from the end section of the bridge (its \"From center\" is the gap).", "AB Bridges"); return; }
            if (_pickView == null || _pickView.Items.Count == 0) { MessageBox.Show(this, "There is no plan view in this project to pick a line in.", "AB Bridges"); return; }
            if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
            string label = r.IsBearing ? "bearings" : $"{i - SubAxis.First + 1} {(string.IsNullOrEmpty(r.Family) ? "(no family)" : r.Family.Split(':')[0].Trim())}";
            Pick = new SubPick { Axis = CurAxis.Name, Row = i, Picks = r.Repeat && !r.Reserved && !r.Centered ? 2 : 1, Label = label };
            PickView = _pickView.SelectedItem as string;
            CreateNow = false;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void PickFamily(int i)
        {
            SubAxis s = Cur;
            if (s == null || i < 0 || i >= s.Rows.Count) return;
            using (var dlg = new FamilyPicker(_families, s.Rows[i].Family, s.Rows[i].IsBearing ? "Bearing family" : s.Rows[i].IsAbut ? "Abutment family (2 adaptive points)" : "Family"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _grid.Rows[i].Cells["Fam"].Value = string.IsNullOrEmpty(dlg.Picked) ? None : dlg.Picked;   // -> ReadRow
            }
        }

        // the left side takes about 40 % of the width (at least 506 px), the section the rest
        private int LeftW => Math.Max(506, (int)(ClientSize.Width * 0.40) - 12);

        private void Relayout() => LayoutAll();
        private void LayoutParams() => LayoutAll();

        // everything placed from the window size: Excel row, long view, the cards, the bottom strip, the bottom bar
        private void LayoutAll()
        {
            if (_grid == null || _params == null || _view == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, m = 12;
            int y = BrandHeader.Height0 + 8;
            _xlLbl.Location = new Point(m, y + 4);
            _tb.SetBounds(80, y, Math.Min(520, W / 2 - 100), 25);
            _browse.Location = new Point(_tb.Right + 8, y - 2);
            _status.SetBounds(_browse.Right + 12, y + 3, Math.Max(100, W - _browse.Right - 24), 20);
            y += 34;

            // the axis chips
            _tabs.SetBounds(m, y, W - 2 * m, 32);
            y += 38;

            const int bar = 52;
            int bottom = H - bar - 8;
            // left: families (top) + bearing / abutment settings and parameters (under); right: the section.
            // Both shares come from the two drag bars (kept for the next time)
            EnsureSplitBars();
            int lw = Math.Max(600, Math.Min(W - 3 * m - 260, (int)((W - 3 * m) * s_leftShare))), vx = 2 * m + lw, vw = Math.Max(200, W - vx - m);   // 600: the buttons and the "Create / Copy" row fit
            _view.SetBounds(vx, y, vw, Math.Max(200, bottom - y));
            _vBar.SetBounds(m + lw + 2, y, m - 4, Math.Max(200, bottom - y));
            int top = y, famH = Math.Max(150, Math.Min(bottom - top - 150, (int)((bottom - top) * s_famShare)));
            _hBar.SetBounds(m, top + famH + 1, lw, 6);
            int rx = m, rw = lw;
            _famHead.SetBounds(m, top + 2, Math.Max(120, lw - 420), 18);
            _copyBtn.Location = new Point(m + lw - _copyBtn.Width, top - 2);
            _copyFrom.Location = new Point(_copyBtn.Left - 8 - _copyFrom.Width, top + 1);
            _copyLbl.Location = new Point(_copyFrom.Left - 6 - _copyLbl.PreferredWidth, top + 4);
            _create.Location = new Point(_copyLbl.Left - 18 - _create.PreferredSize.Width, top + 2);
            int by = top + famH - 30;
            _grid.SetBounds(m, top + 26, lw, Math.Max(80, by - top - 32));
            _addBtn.Location = new Point(m, by); _upBtn.Location = new Point(_addBtn.Right + 6, by); _downBtn.Location = new Point(_upBtn.Right + 4, by);
            _removeBtn.Location = new Point(_downBtn.Right + 6, by); _loadBtn.Location = new Point(_removeBtn.Right + 8, by);
            _pickView.Location = new Point(m + lw - _pickView.Width, by + 2);
            _pickLbl.Location = new Point(_pickView.Left - 6 - _pickLbl.PreferredWidth, by + 7);
            int fixedCols = _grid.Columns["No"].Width + _grid.Columns["Pick"].Width + _grid.Columns["Surf"].Width + _grid.Columns["V"].Width + _grid.Columns["H"].Width + _grid.Columns["PickH"].Width + 4;
            _grid.Columns["Fam"].Width = Math.Max(150, lw - fixedCols - SystemInformation.VerticalScrollBarWidth);

            int dtop = top + famH + 8;
            _detHead.SetBounds(rx, dtop + 2, rw, 18);
            int py = dtop + 26;
            foreach (Panel pn in new[] { _bear, _abutPanel, _repPanel }) pn.SetBounds(rx, py, rw, pn.Height);
            int ph = _bearOn ? _bear.Height + 6 : _abutOn ? _abutPanel.Height + 6 : _repOn ? _repPanel.Height + 6 : 0;
            _params.SetBounds(rx, py + ph, rw, Math.Max(80, bottom - py - ph));
            int pw = rw - _params.Columns["K"].Width - SystemInformation.VerticalScrollBarWidth - 4;
            _params.Columns["N"].Width = Math.Max(150, (int)(pw * 0.6));
            _params.Columns["Val"].Width = Math.Max(100, pw - _params.Columns["N"].Width);

            // bottom bar
            _line.SetBounds(0, H - bar, W, 1);
            int yb = H - bar + 10;
            _prev.Location = new Point(m, yb); _next.Location = new Point(_prev.Right + 6, yb);
            _closeBtn.Location = new Point(W - m - _closeBtn.Width, yb);
            _saveBtn.Location = new Point(_closeBtn.Left - 6 - _saveBtn.Width, yb);
            _createBtn.Location = new Point(_saveBtn.Left - 6 - _createBtn.Width, yb);
            _see.Location = new Point(_createBtn.Left - 6 - _see.Width, yb);
            if (_applyBtn != null) { _applyBtn.Location = new Point(_see.Left - 6 - _applyBtn.Width, yb); _applyBtn.BringToFront(); }
            int sumRight = _applyBtn != null ? _applyBtn.Left : _see.Left;
            _summary.SetBounds(_next.Right + 12, yb + 7, Math.Max(100, sumRight - _next.Right - 24), 20);
        }

        // ---- the drag bars: left | section, families / parameters
        private static double s_leftShare = 0.46, s_famShare = 0.5;
        private Panel _vBar, _hBar;

        private void EnsureSplitBars()
        {
            if (_vBar != null) return;
            Panel Bar(Cursor c)
            {
                var p = new Panel { BackColor = Color.FromArgb(226, 232, 240), Cursor = c };
                p.Paint += (s, e) =>
                {
                    using (var b = new SolidBrush(Color.FromArgb(140, 150, 165)))
                        for (int i = -2; i <= 2; i++)
                            if (c == Cursors.VSplit) e.Graphics.FillEllipse(b, p.Width / 2f - 1.5f, p.Height / 2f + i * 8 - 1.5f, 3, 3);
                            else e.Graphics.FillEllipse(b, p.Width / 2f + i * 8 - 1.5f, p.Height / 2f - 1.5f, 3, 3);
                };
                Controls.Add(p);
                p.BringToFront();
                return p;
            }
            _vBar = Bar(Cursors.VSplit);
            _hBar = Bar(Cursors.HSplit);
            bool dragV = false, dragH = false;
            _vBar.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragV = true; _vBar.Capture = true; } };
            _vBar.MouseUp += (s, e) => { dragV = false; _vBar.Capture = false; };      // no fit: only the Fit button fits
            _vBar.MouseMove += (s, e) =>
            {
                if (!dragV) return;
                int x = PointToClient(_vBar.PointToScreen(e.Location)).X;
                s_leftShare = Math.Max(0.2, Math.Min(0.8, (x - 12.0) / Math.Max(1, ClientSize.Width - 36)));
                LayoutAll();
            };
            _hBar.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragH = true; _hBar.Capture = true; } };
            _hBar.MouseUp += (s, e) => { dragH = false; _hBar.Capture = false; };
            _hBar.MouseMove += (s, e) =>
            {
                if (!dragH) return;
                int y = PointToClient(_hBar.PointToScreen(e.Location)).Y;
                int top = _grid.Top - 26, bottom = ClientSize.Height - 60;
                s_famShare = Math.Max(0.2, Math.Min(0.8, (y - top) / (double)Math.Max(1, bottom - top)));
                LayoutAll();
            };
        }

        private void ZoomPreview() { try { if (_preview is IMeasurable m) m.ZoomFit(); } catch { } }

        // ---------------------------------------------------------------- the axis cards (real Revit section at every axis)

        private sealed class AxisCard : Panel
        {
            public string Axis;
            public readonly Label Head = new Label { Dock = DockStyle.Top, Height = 24, Padding = new Padding(8, 4, 0, 0), Font = new System.Drawing.Font("Segoe UI Semibold", 9f), Cursor = Cursors.Hand };
            public readonly Panel Host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            public readonly Label List = new Label { Dock = DockStyle.Bottom, Padding = new Padding(6, 3, 4, 3), Font = new System.Drawing.Font("Segoe UI", 8.5f), Cursor = Cursors.Hand, BackColor = Color.White };
            public Control Preview;
            public bool On;
            public AxisCard()
            {
                BackColor = Color.White;
                Padding = new Padding(2);
                Controls.Add(Host); Controls.Add(List); Controls.Add(Head);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (var pen = new Pen(On ? Orange : Color.FromArgb(207, 213, 221), On ? 2.5f : 1f))
                    e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
            }
        }

        private readonly List<AxisCard> _cards = new List<AxisCard>();
        private Panel _cardsPanel;
        private Form _big;

        private void DropCards()
        {
            _cardQueue.Clear();
            foreach (AxisCard c in _cards) { try { c.Preview?.Dispose(); c.Dispose(); } catch { } }
            _cards.Clear();
            _cardsPanel?.Controls.Clear();
        }

        // one card per axis; the live section of every axis (the command makes / reuses "AB Section - <axis>")
        private void BuildCards()
        {
            DropCards();
            if (Excel == null || _cardsPanel == null) return;
            Cursor old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                for (int i = 0; i < Excel.Axes.Count; i++)
                {
                    AxisData a = Excel.Axes[i];
                    int k = i;
                    var card = new AxisCard { Axis = a.Name };
                    void Sel() { if (_tabs.SelectedIndex != k) _tabs.SelectedIndex = k; }
                    card.Head.Click += (s, e) => Sel(); card.List.Click += (s, e) => Sel(); card.Click += (s, e) => Sel();
                    card.Head.DoubleClick += (s, e) => { Sel(); OpenBig(); };
                    card.List.DoubleClick += (s, e) => { Sel(); OpenBig(); };
                    var wait = new Label { Dock = DockStyle.Fill, Text = "…", ForeColor = Grey, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
                    wait.Click += (s, e) => Sel(); wait.DoubleClick += (s, e) => { Sel(); OpenBig(); };
                    card.Host.Controls.Add(wait);
                    _cards.Add(card);
                    _cardsPanel.Controls.Add(card);
                }
            }
            finally { Cursor.Current = old; }
            RefreshCards();
            LayoutCards();
            // the pictures: the current axis first, then the others, one at a time while the dialog is idle
            _cardQueue.Clear();
            string cur = CurAxis?.Name;
            if (cur != null) _cardQueue.Add(cur);
            foreach (AxisCard c in _cards) if (c.Axis != cur) _cardQueue.Add(c.Axis);
            StartCardTimer();
        }

        private readonly List<string> _cardQueue = new List<string>();
        private System.Windows.Forms.Timer _cardTimer;

        private void QueueCard(string axis, bool first)
        {
            _cardQueue.Remove(axis);
            if (first) _cardQueue.Insert(0, axis); else _cardQueue.Add(axis);
            StartCardTimer();
        }

        private void StartCardTimer()
        {
            if (_previewOf == null || _cardQueue.Count == 0) return;
            if (_cardTimer == null)
            {
                _cardTimer = new System.Windows.Forms.Timer { Interval = 150 };
                FormClosed += (s0, e0) => { try { _cardTimer.Stop(); _cardTimer.Dispose(); } catch { } };
                _cardTimer.Tick += (s, e) =>
                {
                    // wait while the user is working in the dialog (typing / mouse down)
                    if (Control.MouseButtons != MouseButtons.None || (_grid != null && _grid.IsCurrentCellInEditMode)) return;
                    if (_cardQueue.Count == 0) { _cardTimer.Stop(); return; }
                    string ax = _cardQueue[0];
                    _cardQueue.RemoveAt(0);
                    FillCard(ax);
                    if (_cardQueue.Count == 0) _cardTimer.Stop();
                };
            }
            if (!_cardTimer.Enabled) _cardTimer.Start();
        }

        // the picture of one card (made by Revit: the section exported as an image)
        private void FillCard(string axis)
        {
            AxisCard card = _cards.FirstOrDefault(c => c.Axis == axis);
            AxisData a = Excel?.Axes.FirstOrDefault(x => x.Name == axis);
            if (card == null || a == null || card.IsDisposed) return;
            int k = Excel.Axes.IndexOf(a);
            void Sel() { if (k >= 0 && _tabs.SelectedIndex != k) _tabs.SelectedIndex = k; }
            Control c = null;
            string err = null;
            try { c = _previewOf(a, "image"); } catch (Exception ex) { err = ex.Message; }
            foreach (Control old in card.Host.Controls.Cast<Control>().ToList()) { card.Host.Controls.Remove(old); old.Dispose(); }
            card.Preview = null;
            if (c != null)
            {
                c.Dock = DockStyle.Fill; card.Host.Controls.Add(c); card.Preview = c;
                c.Click += (s, e) => Sel(); c.DoubleClick += (s, e) => { Sel(); OpenBig(); };
                c.Cursor = Cursors.Hand;
            }
            else card.Host.Controls.Add(new Label { Dock = DockStyle.Fill, Text = err == null ? "No section" : "Section not shown: " + err, ForeColor = Grey, TextAlign = ContentAlignment.MiddleCenter });
        }

        // each card under the bubble of its axis (equal widths; spread evenly when the axes are too close)
        private void LayoutCards()
        {
            int n = _cards.Count;
            if (n == 0 || _cardsPanel == null) return;
            int W = _cardsPanel.Width, H = _cardsPanel.Height, gap = 8;
            int cw = Math.Max(150, Math.Min(330, (W - gap * (n - 1)) / n));
            var lefts = new int[n];
            bool even = Excel == null;
            for (int i = 0; i < n && !even; i++)
            {
                float x = _long.ScreenX(Excel.Axes[i].Station);
                if (float.IsNaN(x)) { even = true; break; }
                lefts[i] = Math.Max(0, Math.Min(W - cw, (int)(x + _long.Left - _cardsPanel.Left - cw / 2f)));
                if (i > 0 && lefts[i] < lefts[i - 1] + cw + gap) even = true;
            }
            if (even)
            {
                cw = Math.Max(120, (W - gap * (n - 1)) / n);
                for (int i = 0; i < n; i++) lefts[i] = i * (cw + gap);
            }
            for (int i = 0; i < n; i++)
            {
                AxisCard c = _cards[i];
                int rows = Math.Min(6, Math.Max(1, c.List.Text.Split('\n').Length));
                c.List.Height = rows * 15 + 8;
                c.SetBounds(lefts[i], 0, cw, H);
            }
        }

        // the family list and the name of every card
        private void RefreshCards()
        {
            if (Excel == null) return;
            foreach (AxisCard c in _cards)
            {
                AxisData a = Excel.Axes.FirstOrDefault(x => x.Name == c.Axis);
                SubAxis s = Axes.TryGetValue(c.Axis, out SubAxis v) ? v : null;
                c.Head.Text = c.Axis + (s == null ? "" : !s.Create ? "  (skip)" : s.HasWork ? "  ✓" : "");
                var names = s?.Rows.Where(r => !string.IsNullOrEmpty(r.Family)).Select(r => r.Family).ToList() ?? new List<string>();
                if (s != null && !string.IsNullOrEmpty(s.Abut.Family) && !names.Contains(s.Abut.Family)) names.Insert(0, s.Abut.Family);
                c.List.Text = names.Count == 0 ? "(no families)" : string.Join("\n", names.Take(6).Select((f, k) => (k + 1) + "  " + f)) + (names.Count > 6 ? $"\n+ {names.Count - 6} more" : "");
            }
            MarkCards();
            LayoutCards();
        }

        private void MarkCards()
        {
            string cur = CurAxis?.Name;
            foreach (AxisCard c in _cards)
            {
                c.On = c.Axis == cur;
                c.Head.BackColor = c.On ? Navy : Color.FromArgb(243, 244, 246);
                c.Head.ForeColor = c.On ? Color.White : Navy;
                c.Invalidate();
            }
            _long?.Mark(cur);
        }

        // the big section of the axis in its own window, with Measure / Fit / Depth / Crop / Section-Plan
        private void OpenBig()
        {
            if (CurAxis == null) return;
            if (_big != null) { _big.Activate(); ShowPreview(true); return; }
            _big = new Form { Text = "AB Bridges - Section " + CurAxis.Name, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(1000, 720),
                              MinimizeBox = false, Font = Font, BackColor = Color.White, ShowInTaskbar = false };
            _big.Controls.Add(_view);
            Brand.AddHeader(_big, "Section");
            _big.FormClosed += (s, e) =>
            {
                DropPreview();
                try { _big.Controls.Remove(_view); } catch { }
                _big = null;
            };
            _big.Show(this);
            ShowPreview(true);
        }

        // .rfa files loaded into the project without opening them (the command does it); every family list gets the new types
        private static string s_lastDir;
        private void LoadFamilyFiles()
        {
            if (LoadFamilies == null) return;
            using (var d = new OpenFileDialog { Title = "Load families into the project", Filter = "Revit family (*.rfa)|*.rfa", Multiselect = true })
            {
                if (!string.IsNullOrEmpty(s_lastDir) && Directory.Exists(s_lastDir)) d.InitialDirectory = s_lastDir;
                if (d.ShowDialog(this) != DialogResult.OK || d.FileNames.Length == 0) return;
                s_lastDir = Path.GetDirectoryName(d.FileNames[0]);
                int before = _families.Count;
                List<string> now;
                Cursor old = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;
                try { now = LoadFamilies(d.FileNames); }
                catch (Exception ex) { _summary.ForeColor = Red; _summary.Text = "Could not load: " + ex.Message; return; }
                finally { Cursor.Current = old; }
                if (now == null) return;
                var added = now.Except(_families, StringComparer.OrdinalIgnoreCase).ToList();
                _families.Clear(); _families.AddRange(now);
                _summary.ForeColor = Green;
                _summary.Text = $"✓ {d.FileNames.Length} famil{(d.FileNames.Length == 1 ? "y" : "ies")} loaded ({added.Count} new type(s))" + (added.Count > 0 ? ": " + string.Join(", ", added.Take(4)) : "");
            }
        }

        // ---------------------------------------------------------------- details

        private void ShowDetails()
        {
            SubAxis s = Cur;
            int i = SelRow;
            SubRow r = s != null && i >= 0 && i < s.Rows.Count ? s.Rows[i] : null;
            AxisData a = CurAxis;
            _loading = true;
            try
            {
                bool bearing = r != null && r.IsBearing;
                bool abut = r != null && r.IsAbut;
                _bear.Visible = bearing;
                _abutPanel.Visible = abut;
                _bearOn = bearing;
                _abutOn = abut;
                bool normal = r != null && !r.Reserved;
                _repPanel.Visible = normal;
                _repOn = normal;
                if (normal) ShowRepeat(r, null);
                    LayoutParams();
                if (r == null) { _detHead.Text = ""; _params.Rows.Clear(); return; }
                string famText = string.IsNullOrEmpty(r.Family) ? "(no family)" : r.Family;
                _detHead.Text = bearing ? $"Bearing : {famText}  —  bearings at {a?.Name} (one row on the axis)"
                              : abut ? $"Abutment : {famText}  —  at {a?.Name}, two points on the deck edges"
                              : $"{i - SubAxis.First + 1}  {famText}  —  family parameters";
                if (abut) _swap.Checked = r.Swap;
                if (bearing && a != null)
                {
                    _count.Value = Math.Max(_count.Minimum, Math.Min(_count.Maximum, r.Count));
                    _equal.Checked = r.Equal;
                    _typed.Checked = !r.Equal;
                    ShowBearingDerived(null);
                }
                FillParams(r);
            }
            finally { _loading = false; }
        }

        // ---------------------------------------------------------------- repeat along the axis

        private void ShowRepeat(SubRow r, Control editing)
        {
            AxisData a = CurAxis;
            bool was = _loading;
            _loading = true;
            try
            {
                _rep.Checked = r.Repeat;
                foreach (Control c in new Control[] { _repCount, _repLeft, _repRight, _repEqual, _repTyped, _repCenter, _repSp }) c.Enabled = r.Repeat;
                _repLeft.Enabled = _repRight.Enabled = r.Repeat && !r.Centered;
                if (r.Count < 1) r.Count = 1;
                if (editing != _repCount) _repCount.Value = Math.Max(_repCount.Minimum, Math.Min(_repCount.Maximum, r.Count));
                if (editing != _repLeft) _repLeft.Text = r.Left.ToString("0.000", CI);
                _repCenter.Checked = r.Centered;
                _repEqual.Checked = !r.Centered && r.Equal;
                _repTyped.Checked = !r.Centered && !r.Equal;
                if (a != null)
                {
                    var o = SubGeo.RepeatOffs(r, a);
                    double right = SubGeo.Edges(a).right - o[o.Count - 1];
                    if (editing != _repRight) _repRight.Text = (r.Centered ? right : r.Equal ? r.Right : right).ToString("0.000", CI);
                    if (editing != _repLeft && r.Centered) _repLeft.Text = (o[0] - SubGeo.Edges(a).left).ToString("0.000", CI);
                    if (editing != _repSp)
                    {
                        var sp = new List<double>();
                        for (int i = 1; i < o.Count; i++) sp.Add(o[i] - o[i - 1]);
                        _repSp.Text = r.Centered && r.Equal
                            ? (sp.Count > 0 ? sp[0] : (r.Spacings.Count > 0 ? r.Spacings[0] : 2.0)).ToString("0.000", CI)
                            : string.Join("  ", sp.Select(v => v.ToString("0.000", CI)));
                    }
                }
                int k = SelRow;
                if (k >= 0 && k < _grid.Rows.Count) _grid.Rows[k].Cells["H"].Value = HText(r);
            }
            finally { _loading = was; }
        }

        private void ReadRepeat(Control from)
        {
            if (_loading) return;
            SubAxis s = Cur;
            AxisData a = CurAxis;
            int i = SelRow;
            if (s == null || a == null || i < SubAxis.First || i >= s.Rows.Count) return;
            SubRow r = s.Rows[i];
            if (from == _rep)
            {
                r.Repeat = _rep.Checked;
                if (r.Repeat && r.Count < 2) r.Count = 2;
            }
            if (from == _repCount) r.Count = (int)_repCount.Value;
            if (from == _repLeft && !r.Centered && Num(_repLeft.Text, out double l)) r.Left = l;
            if (from == _repRight && !r.Centered && Num(_repRight.Text, out double rr))
            {
                r.Right = rr;
                r.Equal = true;          // typing the right edge -> equal between the edges
            }
            if (from == _repEqual || from == _repTyped || from == _repCenter)
            {
                var o = SubGeo.RepeatOffs(r, a);       // keep where they are now
                var (L, R) = SubGeo.Edges(a);
                var sp = new List<double>();
                for (int k = 1; k < o.Count; k++) sp.Add(Math.Round(o[k] - o[k - 1], 3));
                if (from == _repCenter)
                {
                    r.Centered = true;
                    r.HOff = Math.Round((o[0] + o[o.Count - 1]) / 2 - SubGeo.Center(a), 3);
                    r.Equal = sp.Distinct().Count() <= 1;
                    r.Spacings = r.Equal ? new List<double> { sp.Count > 0 ? sp[0] : 2.0 } : sp;
                }
                else
                {
                    r.Centered = false;
                    r.Left = Math.Round(o[0] - L, 3);
                    r.Right = Math.Round(R - o[o.Count - 1], 3);
                    r.Equal = from == _repEqual;
                    r.Spacings = sp;
                }
            }
            if (from == _repSp)
            {
                var list = new List<double>();
                foreach (string t in _repSp.Text.Split(new[] { ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    if (Num(t, out double v) && v > 0) list.Add(v);
                if (list.Count > 0)
                {
                    r.Spacings = list;
                    r.Equal = r.Centered ? list.Count == 1 : false;   // centered: one value = equal; else typing -> typed from the left
                }
            }
            ShowRepeat(r, from);
            Changed();
        }

        // the values that follow from the others (equal: the spacings; typed: the right offset)
        private void ShowBearingDerived(Control editing)
        {
            SubAxis s = Cur;
            AxisData a = CurAxis;
            if (s == null || a == null) return;
            SubRow b = s.Bearing;
            bool was = _loading;
            _loading = true;
            try
            {
                // all editable: typing the spacings -> typed one by one; typing the right edge -> equal
                _right.ReadOnly = false;
                _spacings.ReadOnly = false;
                _right.BackColor = Color.White;
                _spacings.BackColor = Color.White;
                if (editing != _spacings)
                {
                    var sp = SubGeo.SpacingList(b);
                    _spacings.Text = b.Equal ? (sp.Count > 0 ? sp[0] : (b.Spacings.Count > 0 ? b.Spacings[0] : 2.0)).ToString("0.000", CI)
                                             : string.Join("  ", sp.Select(v => v.ToString("0.000", CI)));
                }
                int n = Math.Max(1, b.Count);
                _spHint.Text = n <= 1 ? "" : n == 2 ? "B1–B2" : n == 3 ? "B1–B2, B2–B3" : $"B1–B2 ... B{n - 1}–B{n}";
                int k = _grid.Rows.Count > 0 ? 0 : -1;
                if (k == 0) _grid.Rows[0].Cells["H"].Value = HText(b);
            }
            finally { _loading = was; }
        }

        private static List<double> PadSpacings(SubRow b)
        {
            var res = new List<double>();
            for (int i = 0; i < Math.Max(0, b.Count - 1); i++)
                res.Add(i < b.Spacings.Count ? b.Spacings[i] : (b.Spacings.Count > 0 ? b.Spacings[b.Spacings.Count - 1] : 1.0));
            return res;
        }

        private void ReadBearing(Control from)
        {
            if (_loading) return;
            SubAxis s = Cur;
            AxisData a = CurAxis;
            if (s == null || a == null) return;
            SubRow b = s.Bearing;
            if (from == _count) b.Count = (int)_count.Value;
            if (from == _equal)
            {
                if (!_equal.Checked && b.Equal) { b.Spacings = SubGeo.SpacingList(b); b.Equal = false; }   // typed starts from the equal ones
                else if (_equal.Checked && !b.Equal) { b.Spacings = new List<double> { SubGeo.SpacingList(b).FirstOrDefault() }; b.Equal = true; }
            }
            if (from == _spacings)
            {
                var list = new List<double>();
                foreach (string t in _spacings.Text.Split(new[] { ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    if (Num(t, out double v) && v > 0) list.Add(v);
                // more than one value typed -> one by one
                if (list.Count > 1 && b.Equal) { b.Equal = false; _loading = true; _typed.Checked = true; _equal.Checked = false; _loading = false; }
                if (list.Count > 0) b.Spacings = list;
            }
            ShowBearingDerived(from);
            Changed();
        }

        private List<SubParam> ParamsOf(string fam)
        {
            if (string.IsNullOrEmpty(fam) || _paramsOf == null) return new List<SubParam>();
            Cursor old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try { return _paramsOf(fam) ?? new List<SubParam>(); }
            catch { return new List<SubParam>(); }
            finally { Cursor.Current = old; }
        }

        private void FillParams(SubRow r)
        {
            _params.Rows.Clear();
            foreach (SubParam p in ParamsOf(r.Family))
            {
                string val = r.Params.TryGetValue(p.Name, out string v) ? v : p.Default;
                string name = p.Name + (string.IsNullOrEmpty(p.Unit) ? "" : $" ({p.Unit})");
                int k = _params.Rows.Add(name, p.IsInstance ? "Instance" : "Type", val);
                DataGridViewRow row = _params.Rows[k];
                row.Tag = p;
                if (p.IsYesNo)
                {
                    row.Cells["Val"] = new DataGridViewCheckBoxCell { Value = val == "☑" || val == "1" };
                    row.Cells["Val"].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                }
            }
        }

        private void ReadParam(int i)
        {
            SubAxis s = Cur;
            int ri = SelRow;
            if (s == null || ri < 0 || ri >= s.Rows.Count) return;
            SubRow r = s.Rows[ri];
            DataGridViewRow row = _params.Rows[i];
            if (!(row.Tag is SubParam p)) return;
            object v = row.Cells["Val"].Value;
            string text = p.IsYesNo ? (v is bool bv && bv ? "☑" : "☐") : (Convert.ToString(v, CI) ?? "").Trim().Replace(',', '.');
            if (text == p.Default || text.Length == 0) r.Params.Remove(p.Name);
            else r.Params[p.Name] = text;
        }

        private void Finish(bool create)
        {
            if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
            if (_params.IsCurrentCellInEditMode) _params.EndEdit();
            if (create)
            {
                if (Excel == null || Excel.Axes.Count == 0) { MessageBox.Show(this, "Choose the bridge Excel (with the Substructure sheet) first.", "AB Bridges"); return; }
                bool any = Excel.Axes.Any(a => Axes.TryGetValue(a.Name, out SubAxis s) && s.Create && s.HasWork);
                if (!any) { MessageBox.Show(this, "Nothing to create: choose the families of at least one axis (and keep \"Create this axis\" ☑).", "AB Bridges"); return; }
            }
            CreateNow = create;
            DialogResult = DialogResult.OK;
            Close();
        }

        // ---------------------------------------------------------------- the section

        private sealed class Hit
        {
            public RectangleF Box;
            public int Row;
            public int Bearing = -1;
        }

        private readonly List<Hit> _hits = new List<Hit>();
        private double _sc = 1, _xm, _zm;
        private float _cx, _cy;

        private sealed class Drag
        {
            public Hit H;
            public Point At;
            public double V0, H0;
            public List<double> Offs0;
        }

        private Drag _drag;

        private float PX(double off) => (float)(_cx + (off - _xm) * _sc);
        private float PY(double z) => (float)(_cy - (z - _zm) * _sc);

        private (double x0, double x1, double z0, double z1) BoxOf(SubRow r)
        {
            (double, double, double, double)? b = null;
            try { b = string.IsNullOrEmpty(r.Family) ? null : _boxOf?.Invoke(r.Family); } catch { }
            if (b.HasValue && b.Value.Item2 - b.Value.Item1 > 1e-3 && b.Value.Item4 - b.Value.Item3 > 1e-3) return b.Value;
            return r.IsBearing ? (-0.3, 0.3, -0.15, 0.0) : (-0.5, 0.5, -0.5, 0.0);
        }

        private void PaintSection(Graphics g, System.Drawing.Rectangle rc)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            _hits.Clear();
            AxisData a = CurAxis;
            SubAxis s = Cur;
            var fTitle = new System.Drawing.Font("Segoe UI Semibold", 10f);
            var fSmall = new System.Drawing.Font("Segoe UI", 8.25f);
            if (a == null || s == null)
            {
                g.DrawString("Choose the bridge Excel to see the section at each axis.", Font, new SolidBrush(Grey), 16, 16);
                return;
            }
            SubSection sec = Section(a);
            g.DrawString($"Section at {a.Name}  ·  St {SubGeo.StationText(a.Station)}  ·  skew {a.SkewDeg.ToString("0.000", CI)}°", fTitle, new SolidBrush(Navy), 12, 10);
            g.DrawString("drag a family to move it (5 cm steps)", fSmall, new SolidBrush(Grey), rc.Width - 210, 13);

            // what is drawn: (row, offset, level of its insertion point, box)
            var items = new List<(int row, int bi, double off, double z, (double x0, double x1, double z0, double z1) box)>();
            var (L, R) = SubGeo.Edges(a);
            for (int i = 0; i < s.Rows.Count; i++)
            {
                SubRow r = s.Rows[i];
                if (string.IsNullOrEmpty(r.Family) || r.IsAbut) continue;
                var box = BoxOf(r);
                List<double> offs = r.IsBearing ? SubGeo.BearingOffs(r, a) : new List<double> { SubGeo.RowOff(r, a) };
                for (int k = 0; k < offs.Count; k++)
                {
                    double z = SubGeo.SurfaceZ(a, sec, r.Surface, offs[k]);
                    if (double.IsNaN(z)) continue;
                    items.Add((i, r.IsBearing ? k : -1, offs[k], z + r.VOff, box));
                }
            }

            // extents
            var xs = new List<double> { L, R, 0, (L + R) / 2 };
            var zs = new List<double>();
            xs.AddRange(a.Design.Select(p => p.off)); zs.AddRange(a.Design.Select(p => p.z));
            xs.AddRange(a.Ngl.Select(p => p.off)); zs.AddRange(a.Ngl.Select(p => p.z));
            if (sec != null) foreach (var lp in sec.Loops) { xs.AddRange(lp.Select(p => p.off)); zs.AddRange(lp.Select(p => p.z)); }
            foreach (var it in items) { xs.Add(it.off + it.box.x0); xs.Add(it.off + it.box.x1); zs.Add(it.z + it.box.z0); zs.Add(it.z + it.box.z1); }
            xs = xs.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
            zs = zs.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
            if (zs.Count == 0) zs.Add(0);
            double xmin = xs.Min() - 1.5, xmax = xs.Max() + 1.5, zmin = zs.Min() - 1.0, zmax = zs.Max() + 1.0;
            var area = new RectangleF(40, 90, rc.Width - 150, rc.Height - 150);
            _sc = Math.Min(area.Width / Math.Max(1e-6, xmax - xmin), area.Height / Math.Max(1e-6, zmax - zmin));
            _xm = (xmin + xmax) / 2;
            _zm = (zmin + zmax) / 2;
            _cx = area.X + area.Width / 2;
            _cy = area.Y + area.Height / 2;

            // alignment
            using (var pen = new Pen(Grey, 1f) { DashStyle = DashStyle.Dash })
                g.DrawLine(pen, PX(0), area.Top - 10, PX(0), area.Bottom + 10);
            g.DrawString("Alignment", fSmall, new SolidBrush(Grey), PX(0) + 4, area.Bottom - 6);
            double cOff = SubGeo.Center(a);
            if (Math.Abs(cOff) > 1e-3)
            {
                using (var pen = new Pen(Orange, 1f) { DashStyle = DashStyle.DashDot })
                    g.DrawLine(pen, PX(cOff), area.Top - 10, PX(cOff), area.Bottom + 10);
                g.DrawString("Center", fSmall, new SolidBrush(Orange), PX(cOff) + 4, area.Bottom - 22);
            }
            else g.DrawString("= Center", fSmall, new SolidBrush(Orange), PX(0) + 4, area.Bottom - 22);

            // deck section (holes stay empty)
            if (sec != null && sec.Loops.Count > 0)
            {
                using (var path = new GraphicsPath(FillMode.Alternate))
                {
                    foreach (var lp in sec.Loops)
                        if (lp.Count >= 3) path.AddPolygon(lp.Select(p => new PointF(PX(p.off), PY(p.z))).ToArray());
                    using (var br = new SolidBrush(Color.FromArgb(229, 231, 235))) g.FillPath(br, path);
                    using (var pen = new Pen(Color.FromArgb(156, 163, 175), 1f)) g.DrawPath(pen, path);
                }
                double top = sec.TopAt(R);
                if (!double.IsNaN(top)) g.DrawString($"Deck at {a.Name}", fSmall, new SolidBrush(Grey), PX(R) + 6, PY(top) + 14);
            }
            else
                g.DrawString("No deck in this project yet (Create Deck) - the deck soffit is not known.", fSmall, new SolidBrush(Red), 12, 34);

            // design surface and NGL
            DrawSurface(g, a.Design, Blue, "Design surface", fSmall, true);
            DrawSurface(g, a.Ngl, Green, "NGL  (≤ 1 m)", fSmall, false);

            // Design - NGL at the left edge, the alignment and the right edge
            foreach (double off in new[] { L, 0.0, R })
            {
                double zd = AxisData.ZAt(a.Design, off), zn = AxisData.ZAt(a.Ngl, off);
                if (double.IsNaN(zd) || double.IsNaN(zn)) continue;
                float x = PX(off) + (off == 0.0 ? 10 : off < 0 ? -18 : 18);
                using (var pen = new Pen(Color.FromArgb(75, 85, 99), 1f))
                {
                    g.DrawLine(pen, x, PY(zd), x, PY(zn));
                    g.DrawLine(pen, x - 4, PY(zd), x + 4, PY(zd));
                    g.DrawLine(pen, x - 4, PY(zn), x + 4, PY(zn));
                }
                string t = (zd - zn).ToString("0.000", CI);
                SizeF sz = g.MeasureString(t, fSmall);
                g.DrawString(t, fSmall, Brushes.Black, off < 0 ? x - sz.Width - 3 : x + 3, (PY(zd) + PY(zn)) / 2 - sz.Height / 2);
            }

            // families and bearings
            using (var fill = new SolidBrush(Color.FromArgb(253, 236, 228)))
            using (var bfill = new SolidBrush(Color.FromArgb(29, 78, 216)))
            using (var pen = new Pen(Red, 1.2f))
            {
                foreach (var it in items)
                {
                    SubRow r = s.Rows[it.row];
                    float x0 = PX(it.off + it.box.x0), x1 = PX(it.off + it.box.x1);
                    float y0 = PY(it.z + it.box.z1), y1 = PY(it.z + it.box.z0);
                    var box = new RectangleF(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(3, Math.Abs(x1 - x0)), Math.Max(3, Math.Abs(y1 - y0)));
                    bool sel = SelRow == it.row;
                    if (r.IsBearing)
                    {
                        g.FillRectangle(bfill, box);
                        if (sel) g.DrawRectangle(new Pen(Orange, 2f), box.X - 2, box.Y - 2, box.Width + 4, box.Height + 4);
                    }
                    else
                    {
                        g.FillRectangle(fill, box);
                        g.DrawRectangle(sel ? new Pen(Orange, 2.2f) : pen, box.X, box.Y, box.Width, box.Height);
                        string label = $"{it.row}  {r.Family.Split(':')[0].Trim()}";
                        SizeF sz = g.MeasureString(label, fSmall);
                        if (sz.Width < box.Width - 4 && sz.Height < box.Height)
                            g.DrawString(label, fSmall, new SolidBrush(Red), box.X + (box.Width - sz.Width) / 2, box.Y + (box.Height - sz.Height) / 2);
                        else
                            g.DrawString(label, fSmall, new SolidBrush(Red), box.Right + 4, box.Y + 2);
                        // distance to its surface
                        double zs0 = SubGeo.SurfaceZ(a, sec, r.Surface, it.off);
                        if (!double.IsNaN(zs0) && Math.Abs(r.VOff) > 1e-6)
                        {
                            float xd = box.Right + 10;
                            g.DrawLine(pen, xd, PY(zs0), xd, PY(it.z));
                            g.DrawString($"{r.VOff.ToString("+0.000;-0.000", CI)} from {(r.Surface == "Soffit" ? "soffit" : r.Surface)}", fSmall, new SolidBrush(Red), xd + 4, (PY(zs0) + PY(it.z)) / 2 - 6);
                        }
                    }
                    _hits.Add(new Hit { Box = RectangleF.Inflate(box, 3, 3), Row = it.row, Bearing = it.bi });
                }
            }

            // bearing chain: L edge | B1 | B2 | ... | R edge
            SubRow b = s.Bearing;
            if (!string.IsNullOrEmpty(b.Family))
            {
                var offs = SubGeo.BearingOffs(b, a);
                var chain = new List<double> { L };
                chain.AddRange(offs);
                chain.Add(R);
                float y = 66;
                using (var pen = new Pen(Blue, 1f))
                {
                    g.DrawLine(pen, PX(chain[0]), y, PX(chain[chain.Count - 1]), y);
                    foreach (double c in chain)
                    {
                        g.DrawLine(pen, PX(c), y - 5, PX(c), y + 5);
                        using (var dash = new Pen(Color.FromArgb(120, Blue), 1f) { DashStyle = DashStyle.Dash })
                            if (c != L && c != R) g.DrawLine(dash, PX(c), y + 5, PX(c), area.Bottom - 40);
                    }
                    for (int i = 0; i + 1 < chain.Count; i++)
                    {
                        string t = (chain[i + 1] - chain[i]).ToString("0.000", CI);
                        SizeF sz = g.MeasureString(t, fSmall);
                        g.DrawString(t, fSmall, new SolidBrush(Blue), (PX(chain[i]) + PX(chain[i + 1])) / 2 - sz.Width / 2, y - sz.Height - 2);
                    }
                }
                g.DrawString("L edge", fSmall, new SolidBrush(Grey), PX(L) - 42, y - 7);
                g.DrawString("R edge", fSmall, new SolidBrush(Grey), PX(R) + 4, y - 7);
                if (b.Surface == "Soffit" && sec == null)
                    g.DrawString("Bearings: no deck soffit here - choose Design / NGL for them, or Create Deck first.", fSmall, new SolidBrush(Red), 12, 50);
            }

            // legend
            float ly = rc.Height - 26, lx = 16;
            foreach (var (c, t) in new[] { (Blue, "Design surface"), (Green, "NGL"), (Color.FromArgb(156, 163, 175), "Deck"), (Red, "Substructure"), (Color.FromArgb(29, 78, 216), "Bearings") })
            {
                using (var pen = new Pen(c, 3f)) g.DrawLine(pen, lx, ly + 8, lx + 22, ly + 8);
                g.DrawString(t, fSmall, Brushes.Black, lx + 28, ly);
                lx += 130;
            }
        }

        private void DrawSurface(Graphics g, List<(string name, double off, double z)> pts, Color c, string label, System.Drawing.Font f, bool named)
        {
            var p = pts.Where(x => !double.IsNaN(x.z) && !double.IsNaN(x.off)).OrderBy(x => x.off).ToList();
            if (p.Count == 0) return;
            var line = new List<PointF> { new PointF(PX(p[0].off - 1.2), PY(p[0].z)) };
            line.AddRange(p.Select(x => new PointF(PX(x.off), PY(x.z))));
            line.Add(new PointF(PX(p[p.Count - 1].off + 1.2), PY(p[p.Count - 1].z)));
            using (var pen = new Pen(c, 2f)) g.DrawLines(pen, line.ToArray());
            using (var br = new SolidBrush(c))
            {
                foreach (var x in p) g.FillEllipse(br, PX(x.off) - 3.5f, PY(x.z) - 3.5f, 7, 7);
                g.DrawString(label, f, br, PX(p[0].off) - 10, PY(p[0].z) - (named ? 20 : -6));
            }
        }

        // ---------------------------------------------------------------- dragging

        private Hit HitAt(Point pt)
        {
            // bearings and the smaller boxes first
            return _hits.OrderBy(h => h.Bearing >= 0 ? 0 : 1).ThenBy(h => h.Box.Width * h.Box.Height).FirstOrDefault(h => h.Box.Contains(pt));
        }

        private void ViewDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Hit h = HitAt(e.Location);
            SubAxis s = Cur;
            AxisData a = CurAxis;
            if (h == null || s == null || a == null) return;
            SubRow r = s.Rows[h.Row];
            _drag = new Drag { H = h, At = e.Location, V0 = r.VOff, H0 = r.HOff, Offs0 = r.IsBearing ? SubGeo.BearingOffs(r, a) : null };
            if (SelRow != h.Row)
            {
                _grid.ClearSelection();
                _grid.CurrentCell = _grid.Rows[h.Row].Cells["Fam"];
                _grid.Rows[h.Row].Selected = true;
            }
        }

        private void ViewMove(object sender, MouseEventArgs e)
        {
            if (_drag == null)
            {
                _view.Cursor = HitAt(e.Location) != null ? Cursors.SizeAll : Cursors.Default;
                return;
            }
            SubAxis s = Cur;
            AxisData a = CurAxis;
            if (s == null || a == null || _sc <= 0) return;
            SubRow r = s.Rows[_drag.H.Row];
            double dx = SubGeo.Snap((e.X - _drag.At.X) / _sc);
            double dz = SubGeo.Snap(-(e.Y - _drag.At.Y) / _sc);
            r.VOff = Math.Round(_drag.V0 + dz, 3);
            if (r.IsBearing)
            {
                if (Math.Abs(dx) > 1e-9 && _drag.Offs0 != null && _drag.H.Bearing >= 0)
                {
                    // moving one bearing -> typed spacings
                    var offs = new List<double>(_drag.Offs0);
                    offs[_drag.H.Bearing] = _drag.Offs0[_drag.H.Bearing] + dx;
                    r.Equal = false;
                    r.HOff = Math.Round((offs[0] + offs[offs.Count - 1]) / 2 - SubGeo.Center(a), 3);
                    r.Spacings = new List<double>();
                    for (int i = 1; i < offs.Count; i++) r.Spacings.Add(Math.Round(offs[i] - offs[i - 1], 3));
                }
            }
            else r.HOff = Math.Round(_drag.H0 + dx, 3);

            _loading = true;
            try
            {
                DataGridViewRow row = _grid.Rows[_drag.H.Row];
                row.Cells["V"].Value = r.VOff.ToString("0.000", CI);
                row.Cells["H"].Value = HText(r);
                if (r.IsBearing)
                {
                    _equal.Checked = r.Equal;
                    _typed.Checked = !r.Equal;
                }
            }
            finally { _loading = false; }
            if (r.IsBearing) ShowBearingDerived(null);
            Changed();
        }
    }

    // choose a family type: a search box over every family type of the project
    internal sealed class FamilyPicker : Form
    {
        public string Picked { get; private set; }
        private readonly List<string> _all;
        private readonly TextBox _search;
        private readonly ListBox _list;
        private readonly Label _count;
        private const string None = "(none)";

        public FamilyPicker(List<string> all, string current, string what)
        {
            _all = all ?? new List<string>();
            Text = "AB Bridges - choose " + what.ToLowerInvariant();
            Font = new System.Drawing.Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ClientSize = new Size(560, 560);
            MinimumSize = new Size(420, 360);
            BackColor = Color.White;

            Controls.Add(new Label { Text = "Search", Location = new Point(12, 15), AutoSize = true });
            _search = new TextBox { Location = new Point(70, 11), Size = new Size(478, 26), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(_search);
            _list = new ListBox { Location = new Point(12, 44), Size = new Size(536, 456), IntegralHeight = false, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(_list);
            _count = new Label { Location = new Point(12, 516), Size = new Size(300, 20), ForeColor = Color.FromArgb(107, 114, 128), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            Controls.Add(_count);
            var ok = new Button { Text = "OK", Location = new Point(368, 510), Size = new Size(85, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            var cancel = new Button { Text = "Cancel", Location = new Point(463, 510), Size = new Size(85, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            ok.Click += (s, e) => Accept();
            _list.DoubleClick += (s, e) => Accept();
            _search.TextChanged += (s, e) => Fill(null);
            _search.KeyDown += (s, e) =>
            {
                // arrows move in the list while typing
                if (e.KeyCode == Keys.Down && _list.Items.Count > 0) { _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1); e.Handled = true; }
                if (e.KeyCode == Keys.Up && _list.Items.Count > 0) { _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); e.Handled = true; }
            };
            Fill(string.IsNullOrEmpty(current) ? None : current);
            Brand.AddHeader(this, "Choose " + what);
            Shown += (s, e) => _search.Focus();
        }

        private void Fill(string select)
        {
            string prev = select ?? _list.SelectedItem as string;
            string[] words = _search.Text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var items = _all.Where(f => words.All(w => f.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            _list.BeginUpdate();
            _list.Items.Clear();
            if (words.Length == 0) _list.Items.Add(None);
            foreach (string f in items) _list.Items.Add(f);
            _list.EndUpdate();
            int k = prev != null ? _list.Items.IndexOf(prev) : -1;
            if (k < 0 && _list.Items.Count > 0) k = words.Length == 0 ? 0 : 0;
            if (k >= 0 && k < _list.Items.Count) _list.SelectedIndex = k;
            _count.Text = $"{items.Count} of {_all.Count} family types";
        }

        private void Accept()
        {
            if (!(_list.SelectedItem is string f)) return;
            Picked = f == None ? "" : f;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
