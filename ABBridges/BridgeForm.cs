// AB Bridges - Extract Data dialog (WinForms, opened with Application.ShowModalDialog)
// Tabs: Setup -> Construction Joint -> Under Bridge Cross -> Stations -> Generate Excel

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcDocument = Autodesk.AutoCAD.ApplicationServices.Document;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace ABBridges
{
    internal sealed class BridgeForm : Form
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private const string Version = "AB Bridges 2.0";

        private readonly AcDocument _doc;
        private readonly Editor _ed;
        private readonly Database _db;
        private readonly PreviewGraphics _preview = new PreviewGraphics();

        // state
        private List<AxisInfo> _axes = new List<AxisInfo>();
        private List<SpanLength> _spanLengths = new List<SpanLength>();
        private List<Crossing> _crossings = new List<Crossing>();
        private StationSet _set;
        private string _setSig;
        private bool _filling;

        // controls
        private TabControl _tabs;
        private TabPage _tpSetup, _tpCj, _tpCross, _tpSt;
        private TextBox _tbName, _tbStartSta, _tbCorbelStart, _tbCorbelEnd, _tbSpacing;
        private Label _lbDesign, _lbNgl, _lbUnder, _lbAlign, _lbCenter, _lbLeft, _lbRight, _lbAxes, _lbFile, _lbStatus;
        private Label _lbCjMode, _lbMinClr;
        private DataGridView _gridAxes, _gridCj, _gridRoads, _gridSt;
        private CheckBox _cbFeature, _cbReplace, _cbReverse;
        private NumericUpDown _nudSkew;
        private NiceButton _bPreview, _bNext, _bClose;

        public BridgeForm(AcDocument doc)
        {
            _doc = doc;
            _ed = doc.Editor;
            _db = doc.Database;
            BridgeSettings.ResetIfOtherDrawing(_db);
            BuildUi();
            LoadSettings();
            RefreshAxes();
        }

        // ================================================================ UI

        private NavBar _nav;
        private TabPage _tpId;
        private PlanView _planSetup, _planId, _planSt, _planCj;
        private RoadsView _planRoads;
        private List<(double sta, double z)> _deckProf = new List<(double, double)>(), _nglProf = new List<(double, double)>();
        private SplitContainer _stSplit;
        private NiceButton _bStList;
        private DataGridView _gridId;
        private CheckBox _cbIdAll;
        private FlowLayoutPanel _nearBoxes;      // one box per distance (D1, D2, ...)
        private NumericUpDown _nudNear;
        private Label _lbEdges;
        private static readonly string[] PageNames = { "Setup", "Construction Joint", "Internal Diaphragm", "Roads Below", "Sections" };

        private void BuildUi()
        {
            Text = "AB Bridges - Civil 3D";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            Font = Theme.Body;
            BackColor = Theme.Page;
            ClientSize = new Size(1100, 820);
            MinimumSize = new Size(900, 680);

            _tabs = new TabControl { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, ItemSize = new Size(0, 1), SizeMode = TabSizeMode.Fixed };
            _tpSetup = new TabPage("Setup");
            _tpCj = new TabPage("Construction Joint");
            _tpId = new TabPage("Internal Diaphragm");
            _tpCross = new TabPage("Roads Below");
            _tpSt = new TabPage("Sections");
            foreach (TabPage tp in new[] { _tpSetup, _tpCj, _tpId, _tpCross, _tpSt }) { tp.BackColor = Theme.Page; tp.Padding = new Padding(8); }
            _tabs.TabPages.AddRange(new[] { _tpSetup, _tpCj, _tpId, _tpCross, _tpSt });
            _tabs.Selecting += OnTabSelecting;
            _tabs.SelectedIndexChanged += (s, e) =>
            {
                UpdateButtons(); _nav.SetActive(_tabs.SelectedIndex); SnapLater();
                if (_tabs.SelectedTab == _tpSetup) RefreshSetupPlan();          // the span lengths typed on the other pages
            };

            Brand.SetIcon(this);
            _nav = new NavBar(PageNames);
            _nav.PageClicked += i => _tabs.SelectedIndex = i;

            var foot = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.White };
            _lbStatus = new Label { Location = new Point(10, 13), Size = new Size(600, 22), ForeColor = Color.DimGray, AutoEllipsis = true, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            foot.Controls.Add(_lbStatus);
            _bPreview = Btn("Preview", 0, 9, (s, e) => DoPreview(), 90);
            _bNext = Btn("Next →", 0, 9, (s, e) => DoNext(), 130, Look.Primary);
            _bClose = Btn("Close", 0, 9, (s, e) => { DialogResult = DialogResult.Cancel; Close(); }, 88);
            _bClose.DialogResult = DialogResult.Cancel;
            foreach (Button b in new[] { _bPreview, _bNext, _bClose }) { b.Anchor = AnchorStyles.Top | AnchorStyles.Right; foot.Controls.Add(b); }
            foot.Resize += (s, e) =>
            {
                _bClose.Left = foot.Width - 98; _bNext.Left = _bClose.Left - 138; _bPreview.Left = _bNext.Left - 98;
                _lbStatus.Width = Math.Max(100, _bPreview.Left - 20);
            };

            Controls.Add(_tabs);
            Controls.Add(foot);
            Controls.Add(_nav);
            CancelButton = _bClose;

            BuildSetup();
            BuildCj();
            BuildInternal();
            BuildCross();
            BuildStations();
            _nav.SetActive(0);
            UpdateButtons();

            Shown += (s, e) => SnapLater();
            // Enter in a box = take the value (go to the next box); it never runs Generate Excel
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                Control f = FindFocused(this);
                if (f == null || f is IDataGridViewEditingControl || f is DataGridView) return;     // the grids keep their own Enter
                bool box = f is TextBox || f is NumericUpDown || f.Parent is NumericUpDown;
                if (!box) return;
                e.SuppressKeyPress = true; e.Handled = true;
                SelectNextControl(f.Parent is NumericUpDown ? f.Parent : f, true, true, true, true);
            };
            FormClosed += (s, e) =>
            {
                try { SaveSettings(); } catch { }
                try { _preview.Dispose(); } catch { }
            };
        }

        private static Control FindFocused(Control c)
        {
            while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
            if (c != null && !c.Focused) foreach (Control k in c.Controls) { Control f = k.Focused ? k : FindFocusedChild(k); if (f != null) return f; }
            return c;
        }

        private static Control FindFocusedChild(Control c)
        {
            foreach (Control k in c.Controls) { if (k.Focused) return k; Control f = FindFocusedChild(k); if (f != null) return f; }
            return null;
        }

        // a picture of every page the user opens (Desktop\AB Bridges screens)
        private void SnapLater()
        {
            var t = new Timer { Interval = 400 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Theme.Snap(this, "Civil " + (_tabs.SelectedIndex + 1) + " " + PageNames[_tabs.SelectedIndex]); };
            t.Start();
        }

        // a bar between the drawing (fill) and the lists under it: drag it to make one bigger and the other smaller;
        // the height is kept for the next time (this Civil 3D session)
        private static readonly Dictionary<string, int> s_split = new Dictionary<string, int>();
        private static void AddSplit(Control parent, Control bottom, string key)
        {
            if (s_split.TryGetValue(key, out int h)) bottom.Height = h;
            var sp = new Splitter { Dock = DockStyle.Bottom, Height = 7, BackColor = Color.FromArgb(214, 220, 228), MinSize = 90, MinExtra = 140, Cursor = Cursors.HSplit };
            sp.Paint += (s, e) =>
            {
                int cx = sp.Width / 2, cy = sp.Height / 2;
                using (var b = new SolidBrush(Color.FromArgb(140, 150, 165)))
                    for (int i = -2; i <= 2; i++) e.Graphics.FillEllipse(b, cx + i * 8 - 1.5f, cy - 1.5f, 3, 3);
            };
            sp.SplitterMoved += (s, e) => s_split[key] = bottom.Height;
            parent.Controls.Add(sp);
            parent.Controls.Add(bottom);
        }

        private static GroupBox Card(string text) =>
            new GroupBox { Text = text, Dock = DockStyle.Fill, Font = Theme.Bold, ForeColor = Theme.Navy, BackColor = Color.White, Padding = new Padding(8) };

        // one "pick" line: button, status, optional List / Clear
        private Label PickRow(Control parent, ref int y, string text, Action pick, Action clear, Action list = null)
        {
            var b = Btn(text, 10, y, (s, e) => pick(), 118);
            parent.Controls.Add(b);
            int x = 134;
            if (list != null) { var bl = Btn("List", x, y, (s, e) => list(), 50); parent.Controls.Add(bl); x += 56; }
            if (clear != null) { var bc = Btn("Clear", x, y, (s, e) => { clear(); UpdateStatusLabels(); RefreshSetupPlan(); }, 56, Look.Danger); parent.Controls.Add(bc); }
            Label lb = Status(10, y + 24, 320);
            lb.Font = Theme.Body;
            lb.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            parent.Controls.Add(lb);
            y += 50;
            return lb;
        }

        // ---------------------------------------------------------------- Setup tab

        private void BuildSetup()
        {
            var p = _tpSetup;
            _planSetup = new PlanView { Dock = DockStyle.Fill, Title = "Plan of the selected objects" };

            var cols = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 218, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 8, 0, 0) };
            for (int i = 0; i < 3; i++) cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));

            // column 1: alignment, CL, edges
            GroupBox g1 = Card("Alignment, CL and Edges");
            int y = 22;
            _lbAlign = PickRow(g1, ref y, "Alignment...", PickAlignment, () => BridgeSettings.AlignmentId = ObjectId.Null);
            _cbReverse = new CheckBox { Text = "Reverse direction", Location = new Point(10, y - 4), AutoSize = true, Font = Theme.Body, ForeColor = Color.Black };
            _cbReverse.CheckedChanged += (s, e) =>
            {
                if (_filling) return;
                BridgeSettings.ReverseAlignment = _cbReverse.Checked;
                ShowAlignmentArrows();
                RefreshAxes();
                RefreshSetupPlan();
            };
            g1.Controls.Add(_cbReverse);
            var ls = Lbl("Start station", 150, y - 2, 80); ls.Font = Theme.Body; ls.ForeColor = Color.Black; ls.AutoSize = true; g1.Controls.Add(ls);
            _tbStartSta = Txt(150 + ls.PreferredWidth + 6, y - 5, 90); _tbStartSta.Font = Theme.Body;
            _tbStartSta.Leave += (s, e) => { RefreshAxes(); RefreshSetupPlan(); };
            g1.Controls.Add(_tbStartSta);
            y += 28;
            _lbCenter = PickRow(g1, ref y, "Centerline...", () => PickCurve(id => BridgeSettings.CenterlineId = id, "\nSelect the bridge centerline (used for the span lengths): "),
                                () => BridgeSettings.CenterlineId = ObjectId.Null);
            _lbEdges = PickRow(g1, ref y, "Edges...", PickEdges, () => { BridgeSettings.LeftIds = new List<ObjectId>(); BridgeSettings.RightIds = new List<ObjectId>(); });
            _lbLeft = _lbEdges; _lbRight = new Label();

            // column 2: surfaces
            GroupBox g2 = Card("Surfaces");
            y = 22;
            _lbDesign = PickRow(g2, ref y, "Design...", () => PickSurfaces(0), () => BridgeSettings.DesignIds = new List<ObjectId>(), () => ListSurfaces(0));
            _lbNgl = PickRow(g2, ref y, "NGL...", () => PickSurfaces(1), () => BridgeSettings.NglIds = new List<ObjectId>(), () => ListSurfaces(1));
            _lbUnder = PickRow(g2, ref y, "Roads below...", () => PickSurfaces(2), () => BridgeSettings.UnderIds = new List<ObjectId>(), () => ListSurfaces(2));

            // top left, over the plan: the bridge name (= the Excel name) and the output options
            var top = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = Theme.Page };
            var ln = new Label { Text = "Name", AutoSize = true, Location = new Point(2, 9), Font = Theme.Bold, ForeColor = Theme.Navy };
            top.Controls.Add(ln);
            _tbName = Txt(ln.PreferredWidth + 10, 5, 280); _tbName.Font = Theme.Body;
            _tbName.TextChanged += (s, e) => UpdateFileLabel();
            top.Controls.Add(_tbName);
            _lbFile = new Label { Location = new Point(_tbName.Right + 12, 9), Size = new Size(620, 20), ForeColor = Color.DimGray, Font = Theme.Body, AutoEllipsis = true };
            top.Controls.Add(_lbFile);
            _cbFeature = Chk("Draw feature lines", 2, 35); _cbFeature.Font = Theme.Body; _cbFeature.ForeColor = Color.Black; top.Controls.Add(_cbFeature);
            _cbReplace = Chk("Replace previous run", 160, 35); _cbReplace.Font = Theme.Body; _cbReplace.ForeColor = Color.Black; top.Controls.Add(_cbReplace);

            // column 3: axes
            GroupBox g3 = Card("Axes");
            y = 22;
            _lbAxes = PickRow(g3, ref y, "Axes...", PickAxes, () => { BridgeSettings.AxisIds = new List<ObjectId>(); RefreshAxes(); });

            cols.Controls.Add(g1, 0, 0); cols.Controls.Add(g2, 1, 0); cols.Controls.Add(g3, 2, 0);
            p.Controls.Add(_planSetup);
            AddSplit(p, cols, "setup");
            p.Controls.Add(top);
        }

        // ---------------------------------------------------------------- Internal Diaphragm tab

        private void BuildInternal()
        {
            var p = _tpId;
            _planId = new PlanView { Dock = DockStyle.Fill, Title = "Internal diaphragms (equal along the CL, stations on the alignment)", FitWidth = true };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 200, Padding = new Padding(0, 6, 0, 0) };
            var bar = new Panel { Dock = DockStyle.Top, Height = 36 };
            _cbIdAll = new CheckBox { Text = "Apply a change to all spans", Location = new Point(4, 9), AutoSize = true, Checked = BridgeSettings.InternalAll };
            _cbIdAll.CheckedChanged += (s, e) => BridgeSettings.InternalAll = _cbIdAll.Checked;
            bar.Controls.Add(_cbIdAll);
            bar.Controls.Add(Lbl("Tick the spans that have internal diaphragms. Every one = 2 sections, Gap apart, equal along the span length (Centerline).", 230, 11, 760));
            var idReset = Btn("Reset lengths", 0, 4, (s, e) =>
            {
                foreach (CjSpan v in BridgeSettings.Cj.Values) v.UserLength = null;          // the shared span lengths
                EnsureStations(true); FillId();
            }, 120);
            idReset.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bar.Controls.Add(idReset);
            bar.Resize += (s, e) => idReset.Left = bar.Width - idReset.Width - 2;

            _gridId = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.CellSelect
            };
            _gridId.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Use", HeaderText = "Use", FillWeight = 6 });
            _gridId.Columns.Add(Col("Span", "Span", 12, true));
            _gridId.Columns.Add(Col("Length", "Length (CL)", 13));
            _gridId.Columns.Add(Col("Count", "Internal diaphragms", 13));
            _gridId.Columns.Add(Col("Gap", "Gap between its 2 sections", 15));
            _gridId.Columns.Add(Col("Spacing", "Spacing", 11, true));
            _gridId.Columns.Add(Col("At", "Centres at", 30, true));
            _gridId.CellEndEdit += (s, e) => ReadIdRow(e.RowIndex, e.ColumnIndex);
            _gridId.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_gridId.IsCurrentCellDirty && _gridId.CurrentCell is DataGridViewCheckBoxCell) _gridId.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _gridId.CellContentClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && _gridId.Columns[e.ColumnIndex].Name == "Use") _gridId.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _gridId.CellValueChanged += (s, e) =>
            {
                if (!_idFill && e.RowIndex >= 0 && _gridId.Columns[e.ColumnIndex].Name == "Use") ReadIdRow(e.RowIndex, e.ColumnIndex);
            };
            bottom.Controls.Add(_gridId);
            bottom.Controls.Add(bar);
            p.Controls.Add(_planId);
            AddSplit(p, bottom, "id");
        }

        // ---------------------------------------------------------------- Construction Joint tab

        private void BuildCj()
        {
            var p = _tpCj;
            _planCj = new PlanView { Dock = DockStyle.Fill, Title = "Construction joints (CJ1 · CJ2 · CJ3 dashed purple)", FitWidth = true };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 250, Padding = new Padding(0, 6, 0, 0) };
            var bar = new Panel { Dock = DockStyle.Top, Height = 54 };
            bar.Controls.Add(Lbl("Tick the spans that need construction joints.  CJ1 = axis station + Factor × Length;  CJ2 = CJ1 + Dist 1;  CJ3 = CJ2 + Dist 2.", 4, 6, 900));
            _lbCjMode = new Label { Location = new Point(4, 30), Size = new Size(700, 18), ForeColor = Color.DimGray };
            bar.Controls.Add(_lbCjMode);
            var reset = Btn("Reset lengths", 0, 22, (s, e) => ResetCjLengths(), 120);
            reset.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bar.Controls.Add(reset);
            bar.Resize += (s, e) => reset.Left = bar.Width - reset.Width - 2;

            _gridCj = Grid(0, 0, 10, 10);
            _gridCj.Dock = DockStyle.Fill;
            _gridCj.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Use", HeaderText = "Use", FillWeight = 6 });
            _gridCj.Columns.Add(Col("Span", "Span", 10, true));
            _gridCj.Columns.Add(Col("FromTo", "From → To", 22, true));
            _gridCj.Columns.Add(Col("Length", "Length", 11));
            _gridCj.Columns.Add(Col("Factor", "Factor", 9));
            _gridCj.Columns.Add(Col("D1", "Dist 1", 9));
            _gridCj.Columns.Add(Col("D2", "Dist 2", 9));
            _gridCj.Columns.Add(Col("CJ1", "CJ1", 13, true));
            _gridCj.Columns.Add(Col("CJ2", "CJ2", 13, true));
            _gridCj.Columns.Add(Col("CJ3", "CJ3", 13, true));
            _gridCj.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_gridCj.IsCurrentCellDirty && _gridCj.CurrentCell is DataGridViewCheckBoxCell) _gridCj.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _gridCj.CellValueChanged += (s, e) =>
            {
                if (_filling || e.RowIndex < 0) return;
                ReadCjRow(e.RowIndex);
                BeginInvoke((Action)(() => { try { DrawCjPlan(); } catch (Exception ex) { SetMessage("Construction joints: " + ex.Message, true); } }));   // after the click is done
            };
            var note = new Label
            {
                Dock = DockStyle.Bottom, Height = 34, ForeColor = Color.DimGray,
                Text = "Length = span length from axis to axis: from the Centerline when it is picked in Setup, otherwise the center " +
                       "between the edges. You can type another length (highlighted); Reset lengths brings back the computed value."
            };
            bottom.Controls.Add(_gridCj);
            bottom.Controls.Add(note);
            bottom.Controls.Add(bar);
            p.Controls.Add(_planCj);
            AddSplit(p, bottom, "cj");
        }

        // ---------------------------------------------------------------- Under Bridge Cross tab

        private void BuildCross()
        {
            var p = _tpCross;
            _planRoads = new RoadsView { Dock = DockStyle.Fill };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 230, Padding = new Padding(0, 6, 0, 0) };
            var bar = new Panel { Dock = DockStyle.Top, Height = 36 };
            bar.Controls.Add(Lbl("Type the bridge depth of every road.  Clearance = bridge level − road level − depth  (the points are in the Excel).", 4, 9, 900));
            var recalc = Btn("Recalculate", 0, 4, (s, e) => RefreshCross(), 110);
            recalc.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bar.Controls.Add(recalc);
            bar.Resize += (s, e) => recalc.Left = bar.Width - recalc.Width - 2;

            _gridRoads = Grid(0, 0, 10, 10);
            _gridRoads.Dock = DockStyle.Fill;
            _gridRoads.Columns.Add(Col("Road", "Road", 34, true));
            _gridRoads.Columns.Add(Col("From", "From", 16, true));
            _gridRoads.Columns.Add(Col("To", "To", 16, true));
            _gridRoads.Columns.Add(Col("Depth", "Depth (m)", 14));
            _gridRoads.Columns.Add(Col("MinClr", "Clearance (m)", 16, true));
            _gridRoads.CellEndEdit += (s, e) => ReadDepth(e.RowIndex);

            _lbMinClr = new Label { Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(190, 30, 30), Font = new System.Drawing.Font("Segoe UI", 9.5f, FontStyle.Bold) };
            bottom.Controls.Add(_gridRoads);
            bottom.Controls.Add(_lbMinClr);
            bottom.Controls.Add(bar);
            p.Controls.Add(_planRoads);
            AddSplit(p, bottom, "roads");
        }

        // ---------------------------------------------------------------- Stations tab

        private void BuildStations()
        {
            var p = _tpSt;
            _planSt = new PlanView { Dock = DockStyle.Fill, Title = "Sections plan (live)   —   click: one more part   ·   Shift + click: one part less (equal spacing)", FitWidth = true, Interactive = true };
            _planSt.WorldClick += (x, shift) => { if (shift) DeleteStationNear(x); else AddStationAt(x); };
            _planSt.DeletableNear = x => { Section s = DeletableAt(x); return s == null ? (float?)null : (float)s.Station; };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 250, Padding = new Padding(0, 4, 0, 0) };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            Func<string, Control> L = t => new Label { Text = t, AutoSize = true, Margin = new Padding(8, 6, 2, 0) };
            _nudNear = new NumericUpDown { Width = 46, Minimum = 0, Maximum = 6, Value = BridgeSettings.NearDist.Count };
            _nearBoxes = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
            _nudNear.ValueChanged += (s, e) => { if (!_filling) NearCountChanged(); };
            BuildNearBoxes();
            _tbCorbelStart = new TextBox { Width = 56 }; _tbCorbelEnd = new TextBox { Width = 56 }; _tbSpacing = new TextBox { Width = 56 };
            foreach (TextBox t in new[] { _tbCorbelStart, _tbCorbelEnd, _tbSpacing }) t.Leave += (s, e) => { SaveSettings(); EnsureStations(true); };
            _nudSkew = new NumericUpDown { Width = 46, Minimum = 0, Maximum = 30 };
            _nudSkew.ValueChanged += (s, e) => { if (!_filling) SkewChanged(); };
            bar.Controls.AddRange(new Control[] {
                L("Stations near diaphragm"), _nudNear, L("at (m from face)"), _nearBoxes,
                L("Corbel start"), _tbCorbelStart, L("end"), _tbCorbelEnd, L("Max spacing"), _tbSpacing, L("Skew transition"), _nudSkew });

            var bar2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false };
            bar2.Controls.Add(Btn("Reset", 0, 0, (s, e) => ResetStations(), 80));
            var bAdd = Btn("Add station", 0, 0, (s, e) => AddStation(), 100);
            var bDel = Btn("Delete", 0, 0, (s, e) => DeleteStations(), 80, Look.Danger);
            _bStList = Btn("Show list ▸", 0, 0, null, 110);
            bar2.Controls.AddRange(new Control[] { bAdd, bDel, _bStList });
            bAdd.Visible = bDel.Visible = false;

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 380, Panel2Collapsed = true };
            _stSplit = split;
            _bStList.Click += (s, e) =>
            {
                split.Panel2Collapsed = !split.Panel2Collapsed;
                bool on = !split.Panel2Collapsed;
                _bStList.Text = on ? "Hide list ◂" : "Show list ▸";
                bAdd.Visible = bDel.Visible = on;
                bottom.Height = on ? 380 : 250;
            };
            _gridAxes = Grid(0, 0, 10, 10); _gridAxes.Dock = DockStyle.Fill;
            _gridAxes.Columns.Add(Col("Name", "Axis", 12));
            _gridAxes.Columns.Add(Col("Chainage", "Chainage", 16, true));
            _gridAxes.Columns.Add(Col("Skew", "Skew (°)", 12, true));
            _gridAxes.Columns.Add(Col("Diaphragm", "Diaphragm", 13));
            foreach (string c in new[] { "BD1", "BD2", "AD1", "AD2" }) { var col = Col(c, c, 1); col.Visible = false; _gridAxes.Columns.Add(col); }
            _gridAxes.CellEndEdit += (s, e) => { ReadAxesGrid(); EnsureStations(true); };
            split.Panel1.Controls.Add(_gridAxes);

            _gridSt = Grid(0, 0, 10, 10); _gridSt.Dock = DockStyle.Fill;
            _gridSt.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _gridSt.MultiSelect = true;
            _gridSt.Columns.Add(Col("No", "No", 6, true));
            _gridSt.Columns.Add(Col("Remark", "Remark", 19, true));
            _gridSt.Columns.Add(Col("Type", "Type", 19, true));
            _gridSt.Columns.Add(Col("Station", "Station", 14));
            _gridSt.Columns.Add(Col("Chainage", "Chainage", 15, true));
            _gridSt.Columns.Add(Col("Skew", "Skew (°)", 11, true));
            _gridSt.CellBeginEdit += (s, e) =>
            {
                if (e.RowIndex < 0 || _set == null) return;
                Section sec = _gridSt.Rows[e.RowIndex].Tag as Section;
                if (sec == null || !sec.Editable || _gridSt.Columns[e.ColumnIndex].Name != "Station") e.Cancel = true;
            };
            _gridSt.CellEndEdit += (s, e) => StationEdited(e.RowIndex);
            split.Panel2.Controls.Add(_gridSt);

            bottom.Controls.Add(split);
            bottom.Controls.Add(bar2);
            bottom.Controls.Add(bar);
            p.Controls.Add(_planSt);
            AddSplit(p, bottom, "st");
        }

        // ---------------------------------------------------------------- small helpers

        private static GroupBox Group(string text, int x, int y, int w, int h) =>
            new GroupBox { Text = text, Location = new Point(x, y), Size = new Size(w, h) };

        private static Label Lbl(string text, int x, int y, int w, int h = 18) =>
            new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h) };

        private static Label Status(int x, int y, int w) =>
            new Label { Text = "not selected", Location = new Point(x, y + 5), Size = new Size(w, 18), ForeColor = Color.Firebrick, AutoEllipsis = true };

        private static TextBox Txt(int x, int y, int w) => new TextBox { Location = new Point(x, y), Size = new Size(w, 23) };

        private static CheckBox Chk(string text, int x, int y) => new CheckBox { Text = text, Location = new Point(x, y), AutoSize = true };

        private static NiceButton Btn(string text, int x, int y, EventHandler click, int w = 122, Look look = Look.Normal)
        {
            var b = new NiceButton { Text = text, Location = new Point(x, y), Size = new Size(w, 28), Look = look };
            b.Click += click;
            return b;
        }

        private static DataGridView Grid(int x, int y, int w, int h) => new DataGridView
        {
            Location = new Point(x, y),
            Size = new Size(w, h),
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window
        };

        private static DataGridViewTextBoxColumn Col(string name, string header, float weight, bool readOnly = false)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (readOnly) c.DefaultCellStyle.BackColor = SystemColors.Control;
            return c;
        }

        private static bool TryNum(string s, out double v) =>
            double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CI, out v);

        private static bool Valid(ObjectId id) => BridgeSettings.Valid(id);

        private void SetMessage(string text, bool warning)
        {
            _lbStatus.Text = text;
            _lbStatus.ForeColor = warning ? Color.DarkOrange : Color.ForestGreen;
            var tip = new ToolTip();
            tip.SetToolTip(_lbStatus, text);
        }

        // ================================================================ settings

        private void LoadSettings()
        {
            _filling = true;
            _tbName.Text = BridgeSettings.BridgeName;
            _tbStartSta.Text = BridgeSettings.StartStation.ToString("0.000", CI);
            _cbReverse.Checked = BridgeSettings.ReverseAlignment;
            _tbCorbelStart.Text = BridgeSettings.CorbelStart.ToString("0.000", CI);
            _tbCorbelEnd.Text = BridgeSettings.CorbelEnd.ToString("0.000", CI);
            _tbSpacing.Text = BridgeSettings.Spacing.ToString("0.000", CI);
            _cbFeature.Checked = BridgeSettings.FeatureLines;
            _cbReplace.Checked = BridgeSettings.ReplacePrevious;
            _nudSkew.Value = Math.Max(0, Math.Min(30, BridgeSettings.SkewTransition));
            _filling = false;
            UpdateStatusLabels();
            UpdateFileLabel();
            RefreshSetupPlan();
        }

        private void SaveSettings()
        {
            BridgeSettings.BridgeName = _tbName.Text.Trim();
            if (TryNum(_tbStartSta.Text, out double v)) BridgeSettings.StartStation = v;
            if (TryNum(_tbCorbelStart.Text, out v) && v >= 0) BridgeSettings.CorbelStart = v;
            if (TryNum(_tbCorbelEnd.Text, out v) && v >= 0) BridgeSettings.CorbelEnd = v;
            if (TryNum(_tbSpacing.Text, out v) && v > 0) BridgeSettings.Spacing = v;
            BridgeSettings.FeatureLines = _cbFeature.Checked;
            BridgeSettings.ReplacePrevious = _cbReplace.Checked;
            BridgeSettings.SkewTransition = (int)_nudSkew.Value;
            ReadAxesGrid();
        }

        private void UpdateStatusLabels()
        {
            SetSurfaceStatus(_lbDesign, BridgeSettings.DesignIds, "not selected", true);
            SetSurfaceStatus(_lbNgl, BridgeSettings.NglIds, "optional - not selected (remembered in this drawing)", false);
            SetSurfaceStatus(_lbUnder, BridgeSettings.UnderIds, "optional - none (no roads under the bridge)", false);
            SetStatus(_lbAlign, BridgeSettings.AlignmentId, "not selected", true);
            SetStatus(_lbCenter, BridgeSettings.CenterlineId, "optional - not selected (span length from the edges)", false);
            int nl = BridgeSettings.LeftIds.Count(Valid), nr = BridgeSettings.RightIds.Count(Valid);
            _lbEdges.Text = nl == 0 || nr == 0 ? "not selected (pick both edges)" : $"✔ left {nl} piece(s), right {nr} piece(s)";
            _lbEdges.ForeColor = nl == 0 || nr == 0 ? Color.Firebrick : Color.ForestGreen;
            int n = BridgeSettings.AxisIds.Count(Valid);
            _lbAxes.Text = n == 0 ? "not selected" : $"✔ {n} axes selected";
            _lbAxes.ForeColor = n == 0 ? Color.Firebrick : Color.ForestGreen;
        }

        private static void SetCount(Label lb, List<ObjectId> ids, string what)
        {
            int n = ids.Count(Valid);
            lb.Text = n == 0 ? "not selected" : $"✔ {n} {what}{(n > 1 ? "s" : "")} selected";
            lb.ForeColor = n == 0 ? Color.Firebrick : Color.ForestGreen;
        }

        private void SetSurfaceStatus(Label lb, List<ObjectId> ids, string empty, bool required)
        {
            var names = new List<string>();
            try
            {
                using (Transaction tr = _db.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (ObjectId id in ids.Where(Valid))
                        if (tr.GetObject(id, OpenMode.ForRead) is CivSurface s) names.Add(s.Name);
                    tr.Commit();
                }
            }
            catch { }
            if (names.Count == 0)
            {
                lb.Text = empty;
                lb.ForeColor = required ? Color.Firebrick : Color.DimGray;
                return;
            }
            lb.Text = "✔ " + string.Join(", ", names);
            lb.ForeColor = Color.ForestGreen;
        }

        private void SetStatus(Label lb, ObjectId id, string empty, bool required)
        {
            if (!Valid(id))
            {
                lb.Text = empty;
                lb.ForeColor = required ? Color.Firebrick : Color.DimGray;
                return;
            }
            string name = "selected";
            try
            {
                using (Transaction tr = _db.TransactionManager.StartOpenCloseTransaction())
                {
                    var o = tr.GetObject(id, OpenMode.ForRead);
                    if (o is Entity en) name = $"{o.GetType().Name} on layer {en.Layer}";
                    tr.Commit();
                }
            }
            catch { }
            lb.Text = "✔ " + name;
            lb.ForeColor = Color.ForestGreen;
        }

        private string SafeName()
        {
            string n = string.IsNullOrWhiteSpace(_tbName.Text) ? "Bridge" : _tbName.Text.Trim();
            return string.Concat(n.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        }

        private string Folder()
        {
            string f = BridgeSettings.OutputFolder;
            if (!string.IsNullOrEmpty(f) && Directory.Exists(f)) return f;
            try
            {
                if (!string.IsNullOrEmpty(_db.Filename) && File.Exists(_db.Filename)) return Path.GetDirectoryName(_db.Filename);
            }
            catch { }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        private void UpdateFileLabel()
        {
            if (_lbFile == null || _tbName == null) return;
            _lbFile.Text = "Excel file:  " + SafeName() + ".xlsx   (you choose where to save it when you press Generate Excel)";
        }

        // ================================================================ picking

        // surfaces from the list of all surfaces in the drawing (also hidden ones)
        private void ListSurfaces(int which)
        {
            List<ObjectId> cur = which == 0 ? BridgeSettings.DesignIds : which == 1 ? BridgeSettings.NglIds : BridgeSettings.UnderIds;
            string title = which == 0 ? "Design surface(s)" : which == 1 ? "NGL surface(s)" : "Under-bridge road surface(s)";
            string hint = which == 0 ? "Tick the design surface(s) of the bridge. Each point takes the level of the surface under it."
                        : which == 1 ? "Tick the NGL surface(s). The choice is saved in this drawing."
                        : "Tick the road surfaces that pass under the bridge (none if there are no roads).";
            List<ObjectId> res;
            try { res = SurfacePicker.Choose(this, _db, title, hint, cur); }
            catch (Exception ex) { SetMessage("Could not list the surfaces: " + ex.Message, true); return; }
            if (res == null) return;
            if (which == 0) BridgeSettings.DesignIds = res;
            else if (which == 1) BridgeSettings.NglIds = res;
            else BridgeSettings.UnderIds = res;
            UpdateStatusLabels();
        }

        // surfaces are picked in the drawing (one or more); other objects in the selection are ignored
        private void PickSurfaces(int which)
        {
            string prompt = which == 0 ? "\nSelect the DESIGN surface(s) of the bridge: "
                          : which == 1 ? "\nSelect the NGL surface(s): "
                          : "\nSelect the road surface(s) that pass UNDER the bridge: ";
            var picked = new List<ObjectId>();
            int other = 0;
            using (EditorUserInteraction ui = _ed.StartUserInteraction(this))
            {
                var pso = new PromptSelectionOptions { MessageForAdding = prompt };
                PromptSelectionResult r = _ed.GetSelection(pso);
                ui.End();
                if (r.Status == PromptStatus.OK)
                {
                    using (Transaction tr = _db.TransactionManager.StartOpenCloseTransaction())
                    {
                        foreach (ObjectId id in r.Value.GetObjectIds())
                        {
                            if (tr.GetObject(id, OpenMode.ForRead) is CivSurface) { if (!picked.Contains(id)) picked.Add(id); }
                            else other++;
                        }
                        tr.Commit();
                    }
                }
            }
            Activate();
            if (picked.Count == 0)
            {
                if (other > 0) SetMessage("No surface in the selection - select the surface itself (its border or triangles).", true);
                return;
            }
            if (which == 0) BridgeSettings.DesignIds = picked;
            else if (which == 1) BridgeSettings.NglIds = picked;
            else BridgeSettings.UnderIds = picked;
            UpdateStatusLabels();
            SetMessage($"{picked.Count} surface(s) selected" + (other > 0 ? $" ({other} other object(s) ignored)." : "."), false);
        }

        private void PickCurve(Action<ObjectId> store, string prompt)
        {
            using (EditorUserInteraction ui = _ed.StartUserInteraction(this))
            {
                var peo = new PromptEntityOptions(prompt);
                peo.SetRejectMessage("\nSelect a polyline, line or arc.");
                peo.AddAllowedClass(typeof(Curve), false);
                PromptEntityResult r = _ed.GetEntity(peo);
                ui.End();
                if (r.Status == PromptStatus.OK) store(r.ObjectId);
            }
            Activate();
            UpdateStatusLabels();
            RefreshAxes();
            RefreshSetupPlan();
        }

        // alignment pick: shows the direction at once so "Reverse" can be ticked if needed
        private void PickAlignment()
        {
            ObjectId picked = ObjectId.Null;
            using (EditorUserInteraction ui = _ed.StartUserInteraction(this))
            {
                var peo = new PromptEntityOptions("\nSelect the alignment (polyline): ");
                peo.SetRejectMessage("\nSelect a polyline, line or arc.");
                peo.AddAllowedClass(typeof(Curve), false);
                PromptEntityResult r = _ed.GetEntity(peo);
                ui.End();
                if (r.Status == PromptStatus.OK) picked = r.ObjectId;
            }
            Activate();
            if (picked.IsNull) return;
            if (picked != BridgeSettings.AlignmentId)
            {
                BridgeSettings.ReverseAlignment = false;
                _filling = true;
                _cbReverse.Checked = false;
                _filling = false;
            }
            BridgeSettings.AlignmentId = picked;
            UpdateStatusLabels();
            ShowAlignmentArrows();
            RefreshAxes();
            SetMessage("The arrows show the alignment direction. Tick Reverse if it runs the wrong way.", false);
        }

        private void ShowAlignmentArrows()
        {
            if (!Valid(BridgeSettings.AlignmentId)) return;
            try
            {
                using (Transaction tr = _db.TransactionManager.StartOpenCloseTransaction())
                using (Curve c = Geo.LoadAlignment(tr))
                {
                    PlStart.ShowCurve(_doc, c);
                    tr.Commit();
                }
                _ed.UpdateScreen();
            }
            catch (Exception ex)
            {
                SetMessage("Could not show the direction: " + ex.Message, true);
            }
        }

        // an edge can be one polyline or several pieces
        // both deck edges in one pick: each piece goes left or right by the side of the alignment it lies on
        private void PickEdges()
        {
            using (EditorUserInteraction ui = _ed.StartUserInteraction(this))
            {
                var pso = new PromptSelectionOptions { MessageForAdding = "\nSelect the two bridge edges (all their pieces): " };
                var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LINE,ARC,LWPOLYLINE,POLYLINE,SPLINE") });
                PromptSelectionResult r = _ed.GetSelection(pso, filter);
                ui.End();
                if (r.Status == PromptStatus.OK)
                {
                    if (!Valid(BridgeSettings.AlignmentId)) { SetMessage("Pick the alignment first, then the edges.", true); }
                    else
                    {
                        var left = new List<ObjectId>(); var right = new List<ObjectId>();
                        try
                        {
                            using (Transaction tr = _db.TransactionManager.StartOpenCloseTransaction())
                            using (var cl = new CenterLine(Geo.LoadAlignment(tr), BridgeSettings.StartStation))
                            {
                                foreach (ObjectId id in r.Value.GetObjectIds())
                                {
                                    using (Curve c = Geo.LoadFlat(tr, id))
                                    {
                                        Point3d mid = c.GetPointAtDist(c.GetDistanceAtParameter(c.EndParam) / 2.0);
                                        double st = cl.StationAt(mid);
                                        double side = (mid - cl.PointAt(st)).DotProduct(cl.RightNormalAt(st));
                                        (side < 0 ? left : right).Add(id);
                                    }
                                }
                                tr.Commit();
                            }
                        }
                        catch (Exception ex) { SetMessage("Could not read the edges: " + ex.Message, true); }
                        BridgeSettings.LeftIds = left;
                        BridgeSettings.RightIds = right;
                        if (left.Count == 0 || right.Count == 0) SetMessage("Both edges are needed: the picked pieces are all on one side of the alignment.", true);
                    }
                }
            }
            Activate();
            UpdateStatusLabels();
            RefreshSetupPlan();
        }

        // ================================================================ live plans

        private static List<PointF> Sample(Curve c, int n = 120)
        {
            var pts = new List<PointF>();
            double len = c.GetDistanceAtParameter(c.EndParam);
            for (int i = 0; i <= n; i++)
            {
                Point3d p = c.GetPointAtDist(len * i / n);
                pts.Add(new PointF((float)p.X, (float)p.Y));
            }
            return pts;
        }

        private void RefreshSetupPlan()
        {
            if (_planSetup == null) return;
            _planSetup.Clear();
            _planSetup.ClipX0 = _planSetup.ClipX1 = null;
            try
            {
                // the bridge turned to lie along the page: x from the first axis towards the last one, y across;
                // only the first axis - 5 m .. the last axis + 5 m is shown
                Func<Point3d, PointF> R = p => new PointF((float)p.X, (float)p.Y);
                var ax = _axes.OrderBy(a => a.Station).ToList();
                if (ax.Count >= 2)
                {
                    Point3d o = ax.First().ClPoint;
                    Vector3d d = ax.Last().ClPoint - o;
                    double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                    if (len > 1e-6)
                    {
                        double ux = d.X / len, uy = d.Y / len;
                        R = p => new PointF((float)((p.X - o.X) * ux + (p.Y - o.Y) * uy), (float)(-(p.X - o.X) * uy + (p.Y - o.Y) * ux));
                        _planSetup.ClipX0 = -5f; _planSetup.ClipX1 = (float)len + 5f;
                    }
                }
                Func<Curve, int, List<PointF>> sample = (c, n) =>
                {
                    var pts = new List<PointF>();
                    double l = c.GetDistanceAtParameter(c.EndParam);
                    for (int i = 0; i <= n; i++) pts.Add(R(c.GetPointAtDist(l * i / n)));
                    return pts;
                };
                using (Transaction tr = _db.TransactionManager.StartOpenCloseTransaction())
                {
                    Action<ObjectId, Color, float, bool, bool> draw = (id, col, w, dash, fit) =>
                    {
                        if (!Valid(id)) return;
                        using (Curve c = Geo.LoadFlat(tr, id)) _planSetup.Line(sample(c, 400), col, w, dash, fit);
                    };
                    foreach (ObjectId id in BridgeSettings.LeftIds) draw(id, Theme.Edge, 1.6f, false, true);
                    foreach (ObjectId id in BridgeSettings.RightIds) draw(id, Theme.Edge, 1.6f, false, true);
                    draw(BridgeSettings.CenterlineId, Theme.CenterLine, 1f, true, true);
                    if (Valid(BridgeSettings.AlignmentId))
                    {
                        using (Curve al = Geo.LoadAlignment(tr))
                        {
                            List<PointF> pts = sample(al, 1000);
                            if (BridgeSettings.ReverseAlignment) pts.Reverse();
                            _planSetup.Line(pts, Theme.Alignment, 1.4f, true);
                            // the direction arrow at the right end of the shown part
                            PointF tip = _planSetup.ClipX1.HasValue
                                ? pts.Where(q => q.X <= _planSetup.ClipX1.Value - 1).OrderByDescending(q => q.X).DefaultIfEmpty(pts.Last()).First()
                                : pts.Last();
                            _planSetup.Text(tip, "direction ▶", Theme.Alignment, 8f, -30, -18);
                        }
                    }
                    foreach (AxisInfo a in _axes)
                    {
                        draw(a.Id, Theme.Axis, 1.8f, false, false);
                        _planSetup.Text(R(a.ClPoint), a.Name, Theme.Axis, 10f, 0, 14, true);
                    }
                    // span lengths (axis to axis along the CL points) on top
                    for (int i = 0; i + 1 < ax.Count; i++)
                    {
                        float x1 = R(ax[i].ClPoint).X, x2 = R(ax[i + 1].ClPoint).X;
                        CjSpan sl = BridgeSettings.CjOf(BridgeSettings.SpanKey(ax[i], ax[i + 1]));     // the span length of the CJ / ID pages
                        double len = !double.IsNaN(sl.Length) && sl.Length > 0 ? sl.Length : ax[i + 1].Station - ax[i].Station;
                        _planSetup.Dim(x1, x2, len.ToString("0.000", CI), 6, "Span " + (i + 1), sl.UserLength.HasValue ? Theme.Accent : (Color?)null);
                    }
                    tr.Commit();
                }
            }
            catch { }
            _planSetup.Title = "Plan of the selected objects   —  alignment (red)  ·  edges (blue)  ·  CL (brown)  ·  axes (green)";
            _planSetup.Invalidate();
        }

        // the bridge unrolled along the alignment: x = station, y = offset (exaggerated across)
        private void DrawSectionPlans()
        {
            if (_set == null) return;
            foreach (PlanView pv in new[] { _planId, _planSt, _planCj })
            {
                if (pv == null) continue;
                bool idOnly = pv == _planId, cjOnly = pv == _planCj;
                pv.Clear();
                List<Section> secs = _set.Sections;
                if (secs.Count == 0) { pv.Invalidate(); continue; }
                float w = 5f;
                float s0 = (float)secs.First().Station, s1 = (float)secs.Last().Station;
                // shown: first axis - 5 m .. last axis + 5 m, the whole page width
                List<Section> axs = secs.Where(x => x.Kind == SectionKind.Axis).ToList();
                if (axs.Count > 0) { pv.ClipX0 = (float)axs.First().Station - 5f; pv.ClipX1 = (float)axs.Last().Station + 5f; }
                else { pv.ClipX0 = s0 - 5f; pv.ClipX1 = s1 + 5f; }
                pv.Line(new[] { new PointF(s0, w), new PointF(s1, w), new PointF(s1, -w), new PointF(s0, -w), new PointF(s0, w) }, Theme.Edge, 1.2f);
                foreach (Section sc in secs)
                {
                    float x = (float)sc.Station;
                    Color c; float lw = 0.8f; bool dash = false;
                    switch (sc.Kind)
                    {
                        case SectionKind.Axis: c = Theme.Axis; lw = 1.8f; break;
                        case SectionKind.DiaphragmFace: c = Theme.Axis; lw = 1f; break;
                        case SectionKind.Corbel: c = Theme.Corbel; lw = 1.4f; break;
                        case SectionKind.InternalDiaphragm: c = Theme.Internal; lw = 1.6f; break;
                        case SectionKind.ConstructionJoint: c = Theme.Joint; lw = 1.6f; dash = true; break;
                        case SectionKind.NearDiaphragm: c = Color.FromArgb(110, 110, 110); break;
                        default: c = Theme.Division; dash = true; break;
                    }
                    // the other sections stay visible on the ID / CJ pages, only lighter
                    bool focus = idOnly ? sc.Kind == SectionKind.InternalDiaphragm : cjOnly ? sc.Kind == SectionKind.ConstructionJoint : true;
                    if (!focus && sc.Kind != SectionKind.Axis && sc.Kind != SectionKind.DiaphragmFace && sc.Kind != SectionKind.Corbel)
                    { c = Color.FromArgb(200, 204, 210); lw = 0.7f; }
                    float ext = sc.Kind == SectionKind.Axis ? 2.2f : 0f;
                    // the skew: the section line turned by its angle (seen in the plan: where the cut goes from / to)
                    float t = (float)Math.Tan(Math.Max(-80, Math.Min(80, sc.SkewDeg)) * Math.PI / 180.0);
                    float top = w + ext, bot = -w - ext;
                    pv.Line(new[] { new PointF(x - t * top, top), new PointF(x - t * bot, bot) }, c, lw, dash);
                }
                // axis to axis (span) dims on top, Span n under the value
                // one dim per span: its length (the length of the table on the CJ / ID pages, orange when typed) + "Span n"
                List<Section> ax = secs.Where(x => x.Kind == SectionKind.Axis).ToList();
                for (int i = 0; i + 1 < ax.Count; i++)
                {
                    double len = ax[i + 1].Station - ax[i].Station;
                    Color? col = null;
                    SpanInfo spi = i < _set.Spans.Count ? _set.Spans[i] : null;
                    if (spi != null)
                    {
                        double tl = BridgeSettings.CjOf(spi.Key).Length;          // one span length for every page
                        bool typed = BridgeSettings.CjOf(spi.Key).UserLength.HasValue;
                        if (!double.IsNaN(tl) && tl > 0) len = tl;
                        if (typed) col = Theme.Accent;
                    }
                    pv.Dim((float)ax[i].Station, (float)ax[i + 1].Station, len.ToString("0.000", CI), 48, "Span " + (i + 1), col);
                }
                // section to section dims (second row) - not axis to axis (that is the span dim above)
                List<Section> chain = idOnly ? secs.Where(x => x.Kind != SectionKind.Division && x.Kind != SectionKind.NearDiaphragm).ToList()
                                    : cjOnly ? secs.Where(x => x.Kind == SectionKind.Axis || x.Kind == SectionKind.ConstructionJoint).ToList() : secs;
                for (int i = 0; i + 1 < chain.Count; i++)
                {
                    if (chain[i].Kind == SectionKind.Axis && chain[i + 1].Kind == SectionKind.Axis) continue;
                    pv.Dim((float)chain[i].Station, (float)chain[i + 1].Station, (chain[i + 1].Station - chain[i].Station).ToString("0.00", CI), 4);
                }
                // names under the deck: axis + diaphragm number, corbels, internal diaphragms; division count in the middle
                int d = 0, cb = 0;
                foreach (Section sc in secs)
                {
                    var at = new PointF((float)sc.Station, -w);
                    if (sc.Kind == SectionKind.Axis) pv.Text(at, sc.Remark + " · D" + (++d), Theme.Axis, 8.5f, 0, 16);
                    else if (sc.Kind == SectionKind.Corbel) pv.Text(at, "C" + (++cb), Theme.Corbel, 8f, 0, 32);
                    else if (sc.Kind == SectionKind.InternalDiaphragm && sc.Remark.EndsWith(" L"))
                        pv.Text(at, sc.Remark.Substring(sc.Remark.LastIndexOf("ID", StringComparison.Ordinal)).Replace(" L", ""), Theme.Internal, 8f, 0, 32);
                    else if (sc.Kind == SectionKind.ConstructionJoint && cjOnly)
                        pv.Text(at, sc.Remark.Contains("CJ") ? sc.Remark.Substring(sc.Remark.LastIndexOf("CJ", StringComparison.Ordinal)) : "CJ", Theme.Joint, 8f, 0, 32);
                }
                if (!idOnly && !cjOnly)
                    foreach (SpanInfo sp in _set.Spans)
                    {
                        int n = secs.Count(x => x.Span == sp.Index && x.Kind == SectionKind.Division);
                        if (sp.After == null || sp.Before == null) continue;
                        pv.Text(new PointF((float)((sp.After.Station + sp.Before.Station) / 2), 0), n + " div", Color.Black, 9f, 0, -8, true);
                    }
                pv.Invalidate();
            }
        }

        // ================================================================ internal diaphragms

        // the rows are updated in place (not cleared and made again while the grid is still finishing a click / edit:
        // that failed, left _filling on and every later tick / count / gap was ignored)
        private void FillId()
        {
            if (_set == null || _gridId == null) return;
            _idFill = true;
            try
            {
                if (_gridId.IsCurrentCellInEditMode) _gridId.EndEdit();
                var spans = _set.Spans.ToList();
                bool same = _gridId.Rows.Count == spans.Count && spans.Select((sp, i) => (_gridId.Rows[i].Tag as string) == sp.Key).All(x => x);
                if (!same)
                {
                    _gridId.CurrentCell = null;
                    _gridId.Rows.Clear();
                    foreach (SpanInfo sp in spans) _gridId.Rows[_gridId.Rows.Add()].Tag = sp.Key;
                }
                for (int i = 0; i < spans.Count; i++)
                {
                    SpanInfo sp = spans[i];
                    IdSpan v = BridgeSettings.IdOf(sp.Key);
                    double L = v.Length;
                    if (double.IsNaN(L) || L <= 0) L = sp.B.Station - sp.A.Station;
                    bool on = v.Enabled && v.Count > 0;
                    string at = string.Join("  ", _set.Sections.Where(x => x.Span == sp.Index && x.Kind == SectionKind.InternalDiaphragm && x.Remark.EndsWith(" L"))
                                                             .Select(x => Fmt.Chainage(x.Station + v.Gap / 2)));
                    DataGridViewRow r = _gridId.Rows[i];
                    void Set(string c, object val) { if (!Equals(r.Cells[c].Value, val)) r.Cells[c].Value = val; }
                    Set("Use", v.Enabled);
                    Set("Span", sp.Name);
                    Set("Length", L.ToString("0.000", CI));
                    Set("Count", v.Count.ToString(CI));
                    Set("Gap", v.Gap.ToString("0.00", CI));
                    Set("Spacing", on ? (L / (v.Count + 1)).ToString("0.000", CI) : "—");
                    Set("At", on ? at : "");
                    r.Cells["Length"].Style.BackColor = v.UserLength.HasValue ? Color.FromArgb(255, 245, 200) : Color.Empty;
                    foreach (string c in new[] { "Count", "Gap", "Spacing", "At" }) r.Cells[c].Style.ForeColor = v.Enabled ? Color.Empty : Color.Gray;
                }
            }
            catch (Exception ex) { SetMessage("Internal diaphragms: " + ex.Message, true); }
            finally { _idFill = false; }
            try { DrawSectionPlans(); } catch (Exception ex) { SetMessage("Internal diaphragms plan: " + ex.Message, true); }
        }

        // Use + Length are per span; Count and Gap go to every span when "Apply a change to all spans" is ticked
        private void ReadIdRow(int row, int col = -1)
        {
            if (_idFill || row < 0 || !(_gridId.Rows[row].Tag is string key)) return;
            DataGridViewRow r = _gridId.Rows[row];
            IdSpan v = BridgeSettings.IdOf(key);
            string colName = col >= 0 ? _gridId.Columns[col].Name : "";
            if (colName == "Use" || colName == "") v.Enabled = Convert.ToBoolean(r.Cells["Use"].Value ?? false);
            if (colName == "Length" && TryNum(Convert.ToString(r.Cells["Length"].Value), out double len) && len > 0)
                v.UserLength = !double.IsNaN(v.AutoLength) && Math.Abs(len - v.AutoLength) < 0.0005 ? (double?)null : len;
            if (colName == "Count" || colName == "Gap")
            {
                if (!int.TryParse(Convert.ToString(r.Cells["Count"].Value), out int n) || n < 0 || n > 20) n = v.Count;
                if (!TryNum(Convert.ToString(r.Cells["Gap"].Value), out double gap) || gap < 0 || gap > 5) gap = v.Gap;
                IEnumerable<IdSpan> targets = BridgeSettings.InternalAll && _set != null ? _set.Spans.Select(sp => BridgeSettings.IdOf(sp.Key)) : new[] { v };
                foreach (IdSpan t in targets) { t.Count = n; t.Gap = gap; }
            }
            // the grid is rebuilt after the click / edit is finished: rebuilding it inside its own cell event
            // fails quietly (the tick was kept but the plan never changed)
            if (_idPending) return;
            _idPending = true;
            BeginInvoke((Action)(() =>
            {
                _idPending = false;
                try { EnsureStations(true); FillId(); }
                catch (Exception ex) { SetMessage("Internal diaphragms: " + ex.Message, true); }
            }));
        }
        private bool _idFill;     // own flag: a stuck _filling elsewhere must not block this page
        private bool _idPending;

        // ================================================================ stations near the diaphragm

        private void NearCountChanged()
        {
            var list = BridgeSettings.NearDist.ToList();
            int n = (int)_nudNear.Value;
            while (list.Count > n) list.RemoveAt(list.Count - 1);
            while (list.Count < n) list.Add((list.Count == 0 ? 0 : list[list.Count - 1]) + 2.70);
            BridgeSettings.NearDist = list;
            BuildNearBoxes();
            EnsureStations(true);
        }

        // one small box per distance: "D1 [2.70]  D2 [5.40] ..."; a value is taken on Enter or when leaving the box
        private void BuildNearBoxes()
        {
            if (_nearBoxes == null) return;
            _nearBoxes.SuspendLayout();
            _nearBoxes.Controls.Clear();
            for (int i = 0; i < BridgeSettings.NearDist.Count; i++)
            {
                _nearBoxes.Controls.Add(new Label { Text = "D" + (i + 1), AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(i == 0 ? 2 : 8, 6, 2, 0) });
                var tb = new TextBox { Width = 54, Text = BridgeSettings.NearDist[i].ToString("0.00", CI), Tag = i, Margin = new Padding(0, 2, 0, 0) };
                tb.Leave += (s, e) => NearChanged();
                _nearBoxes.Controls.Add(tb);
            }
            _nearBoxes.ResumeLayout();
        }

        private void NearChanged()
        {
            var list = new List<double>();
            bool bad = false;
            foreach (TextBox tb in _nearBoxes.Controls.OfType<TextBox>())
            {
                if (TryNum(tb.Text, out double d) && d > 0) list.Add(d);
                else bad = true;
            }
            list = list.OrderBy(x => x).ToList();
            bool same = list.SequenceEqual(BridgeSettings.NearDist);
            BridgeSettings.NearDist = list;
            if (bad || !same) { _filling = true; _nudNear.Value = Math.Min(6, list.Count); _filling = false; BuildNearBoxes(); }
            if (bad) SetMessage("A distance near the diaphragm is not a number - it was left out.", true);
            if (!same) EnsureStations(true);
        }

        private void PickAxes()
        {
            using (EditorUserInteraction ui = _ed.StartUserInteraction(this))
            {
                var pso = new PromptSelectionOptions { MessageForAdding = "\nSelect ALL bearing axes of this bridge (lines / polylines): " };
                var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE,POLYLINE") });
                PromptSelectionResult r = _ed.GetSelection(pso, filter);
                ui.End();
                if (r.Status == PromptStatus.OK) BridgeSettings.AxisIds = r.Value.GetObjectIds().ToList();
            }
            Activate();
            UpdateStatusLabels();
            RefreshAxes();
            RefreshSetupPlan();
        }

        // ================================================================ axes grid

        // the axes are found again -> the Setup plan is drawn again with them (cut to the first .. last axis, turned)
        private void RefreshAxes()
        {
            RefreshAxesCore();
            RefreshSetupPlan();
        }

        private void RefreshAxesCore()
        {
            ReadAxesGrid();
            _axes = new List<AxisInfo>();
            _gridAxes.Rows.Clear();
            if (!Valid(BridgeSettings.AlignmentId) || BridgeSettings.AxisIds.Count(Valid) == 0) return;
            if (!TryNum(_tbStartSta.Text, out double startSta)) { SetMessage("Start station is not a number.", true); return; }
            BridgeSettings.StartStation = startSta;

            var warn = new List<string>();
            try
            {
                using (Transaction tr = _db.TransactionManager.StartOpenCloseTransaction())
                using (var cl = new CenterLine(Geo.LoadAlignment(tr), startSta))
                {
                    _axes = BridgeModel.LoadAxes(tr, cl, BridgeSettings.AxisIds.Where(Valid), warn);
                    tr.Commit();
                }
            }
            catch (Exception ex)
            {
                SetMessage("Could not read the axes: " + ex.Message, true);
                return;
            }

            _filling = true;
            foreach (AxisInfo a in _axes)
            {
                BridgeSettings.ApplyAxisParams(a, _axes.Count);
                bool first = a.Index == 0, last = a.Index == _axes.Count - 1;
                int row = _gridAxes.Rows.Add(a.Name, Fmt.Chainage(a.Station), a.SkewDeg.ToString("0.000", CI), Fmt.N3(a.Diaphragm),
                    first ? "—" : Fmt.N3(a.BeforeD1), first ? "—" : Fmt.N3(a.BeforeD2),
                    last ? "—" : Fmt.N3(a.AfterD1), last ? "—" : Fmt.N3(a.AfterD2));
                DataGridViewRow r = _gridAxes.Rows[row];
                r.Tag = a.Id;
                foreach (string c in first ? new[] { "BD1", "BD2" } : last ? new[] { "AD1", "AD2" } : new string[0])
                {
                    r.Cells[c].ReadOnly = true;
                    r.Cells[c].Style.BackColor = SystemColors.Control;
                    r.Cells[c].Style.ForeColor = Color.Gray;
                }
            }
            _filling = false;
            SetMessage(warn.Count > 0 ? warn[0] : $"{_axes.Count} axes found.", warn.Count > 0);
        }

        private void ReadAxesGrid()
        {
            if (_gridAxes == null || _filling) return;
            foreach (DataGridViewRow row in _gridAxes.Rows)
            {
                if (!(row.Tag is ObjectId id)) continue;
                AxisParams p = BridgeSettings.ParamsOf(id);
                string nm = Convert.ToString(row.Cells["Name"].Value)?.Trim();
                if (!string.IsNullOrEmpty(nm)) p.Name = nm;
                if (TryNum(Convert.ToString(row.Cells["Diaphragm"].Value), out double d) && d >= 0)
                {
                    p.Diaphragm = d;
                    BridgeSettings.DefaultDiaphragm = d;
                }
                if (!row.Cells["BD1"].ReadOnly && TryNum(Convert.ToString(row.Cells["BD1"].Value), out d) && d >= 0) p.BeforeD1 = d;
                if (!row.Cells["BD2"].ReadOnly && TryNum(Convert.ToString(row.Cells["BD2"].Value), out d) && d >= 0) p.BeforeD2 = d;
                if (!row.Cells["AD1"].ReadOnly && TryNum(Convert.ToString(row.Cells["AD1"].Value), out d) && d >= 0) p.AfterD1 = d;
                if (!row.Cells["AD2"].ReadOnly && TryNum(Convert.ToString(row.Cells["AD2"].Value), out d) && d >= 0) p.AfterD2 = d;
            }
            foreach (AxisInfo a in _axes) BridgeSettings.ApplyAxisParams(a, _axes.Count);
        }

        // ================================================================ checks

        // true when the setup has everything needed for stations (and the design surface when asked)
        private bool CheckSetup(bool needDesign)
        {
            SaveSettings();
            string missing =
                needDesign && BridgeSettings.DesignIds.Count(Valid) == 0 ? "design surface" :
                !Valid(BridgeSettings.AlignmentId) ? "alignment" :
                BridgeSettings.LeftIds.Count(Valid) == 0 || BridgeSettings.RightIds.Count(Valid) == 0 ? "two edges" :
                _axes.Count < 2 ? "at least 2 axes" : null;
            if (missing != null) { SetMessage("Select the " + missing + " first (Setup tab).", true); return false; }
            if (!TryNum(_tbStartSta.Text, out _)) { SetMessage("Start station is not a number.", true); return false; }
            if (!TryNum(_tbCorbelStart.Text, out double cs) || cs < 0) { SetMessage("Corbel start must be 0 or more.", true); return false; }
            if (!TryNum(_tbCorbelEnd.Text, out double ce) || ce < 0) { SetMessage("Corbel end must be 0 or more.", true); return false; }
            if (!TryNum(_tbSpacing.Text, out double sp) || sp <= 0) { SetMessage("Max spacing must be more than 0.", true); return false; }
            foreach (DataGridViewRow row in _gridAxes.Rows)
            {
                foreach (string c in new[] { "Diaphragm" })
                {
                    if (row.Cells[c].ReadOnly) continue;
                    if (!TryNum(Convert.ToString(row.Cells[c].Value), out double d) || d < 0)
                    {
                        SetMessage($"{_gridAxes.Columns[c].HeaderText} of {row.Cells["Name"].Value} is not valid.", true);
                        return false;
                    }
                }
            }
            return true;
        }

        // runs 'work' with the bridge model loaded in a read transaction
        private bool WithModel(Action<Transaction, BridgeModel, List<string>> work, List<string> warn)
        {
            try
            {
                using (Transaction tr = _db.TransactionManager.StartOpenCloseTransaction())
                using (BridgeModel m = BridgeModel.Load(tr, warn))
                {
                    _axes = m.Axes;
                    work(tr, m, warn);
                    tr.Commit();
                }
                return true;
            }
            catch (Exception ex)
            {
                SetMessage("Failed: " + ex.Message, true);
                return false;
            }
        }

        // ================================================================ tabs / buttons

        private void OnTabSelecting(object sender, TabControlCancelEventArgs e)
        {
            if (e.TabPage == _tpSetup) return;
            if (!CheckSetup(e.TabPage == _tpCross)) { e.Cancel = true; return; }
            if (e.TabPage == _tpCj) { RefreshCj(); DrawCjPlan(); }
            else if (e.TabPage == _tpCross) RefreshCross();
            else if (e.TabPage == _tpId) { EnsureStations(false); FillId(); }
            else if (e.TabPage == _tpSt) EnsureStations(false);
        }

        private void UpdateButtons()
        {
            if (_bNext == null) return;
            bool last = _tabs.SelectedTab == _tpSt;
            _bNext.Text = last ? "Generate Excel" : "Next →";
            AcceptButton = null;          // Enter only takes the typed value; Generate Excel only by its button
        }

        private void DoNext()
        {
            if (_tabs.SelectedTab == _tpSt) { DoRun(); return; }
            _tabs.SelectedIndex = _tabs.SelectedIndex + 1;
        }

        // ================================================================ Construction Joint tab

        private void RefreshCj()
        {
            var warn = new List<string>();
            WithModel((tr, m, w) => { _spanLengths = SpanLengths.Compute(m, w); }, warn);

            bool fromCl = _spanLengths.Count > 0 && _spanLengths.All(s => s.FromCenterline);
            _lbCjMode.Text = fromCl ? "Span length: from the Centerline (cut at the axes)." : "Span length: automatic (center between the left and right edges).";

            _filling = true;
            _gridCj.Rows.Clear();
            for (int i = 0; i < _spanLengths.Count && i + 1 < _axes.Count; i++)
            {
                SpanLength sl = _spanLengths[i];
                CjSpan cj = BridgeSettings.CjOf(sl.Key);
                cj.AutoLength = sl.Length;
                AxisInfo a = _axes[i], b = _axes[i + 1];
                int row = _gridCj.Rows.Add(cj.Enabled, sl.Name, Fmt.Chainage(a.Station) + " → " + Fmt.Chainage(b.Station),
                    Fmt.N3(cj.Length), cj.Factor.ToString("0.000", CI), Fmt.N3(cj.Dist1), Fmt.N3(cj.Dist2), "", "", "");
                _gridCj.Rows[row].Tag = sl.Key;
                ShowCjRow(row);
            }
            _filling = false;
            SetMessage(warn.Count > 0 ? warn[0] : $"{_spanLengths.Count} span(s).", warn.Count > 0);
        }

        private void ShowCjRow(int row)
        {
            DataGridViewRow r = _gridCj.Rows[row];
            if (!(r.Tag is string key) || row + 1 >= _axes.Count) return;
            CjSpan cj = BridgeSettings.CjOf(key);
            double a = _axes[row].Station;
            double c1 = a + cj.Factor * cj.Length, c2 = c1 + cj.Dist1, c3 = c2 + cj.Dist2;
            bool ok = !double.IsNaN(cj.Length);
            bool was = _filling;
            _filling = true;
            r.Cells["CJ1"].Value = ok ? Fmt.Chainage(c1) : "";
            r.Cells["CJ2"].Value = ok ? Fmt.Chainage(c2) : "";
            r.Cells["CJ3"].Value = ok ? Fmt.Chainage(c3) : "";
            _filling = was;
            r.Cells["Length"].Style.BackColor = cj.UserLength.HasValue ? Color.FromArgb(255, 245, 200) : Color.Empty;
            Color fc = cj.Enabled ? Color.FromArgb(91, 33, 182) : Color.Gray;
            foreach (string c in new[] { "CJ1", "CJ2", "CJ3" }) r.Cells[c].Style.ForeColor = fc;
        }

        private void ReadCjRow(int row)
        {
            DataGridViewRow r = _gridCj.Rows[row];
            if (!(r.Tag is string key)) return;
            CjSpan cj = BridgeSettings.CjOf(key);
            cj.Enabled = Convert.ToBoolean(r.Cells["Use"].Value ?? false);
            if (TryNum(Convert.ToString(r.Cells["Length"].Value), out double len) && len > 0)
                cj.UserLength = !double.IsNaN(cj.AutoLength) && Math.Abs(len - cj.AutoLength) < 0.0005 ? (double?)null : len;
            if (TryNum(Convert.ToString(r.Cells["Factor"].Value), out double f) && f > 0 && f < 1) cj.Factor = f;
            if (TryNum(Convert.ToString(r.Cells["D1"].Value), out double d1) && d1 > 0) cj.Dist1 = d1;
            if (TryNum(Convert.ToString(r.Cells["D2"].Value), out double d2) && d2 > 0) cj.Dist2 = d2;
            ShowCjRow(row);
        }

        private void ResetCjLengths()
        {
            foreach (CjSpan c in BridgeSettings.Cj.Values) c.UserLength = null;
            if (_tabs.SelectedTab == _tpCj) { RefreshCj(); DrawCjPlan(); }
        }

        // the CJ page plan comes from the stations (they hold the joints); the message of the page is kept
        private void DrawCjPlan()
        {
            string msg = _lbStatus.Text; Color col = _lbStatus.ForeColor;
            try { EnsureStations(false); } catch { }
            DrawSectionPlans();
            _lbStatus.Text = msg; _lbStatus.ForeColor = col;
        }

        // ================================================================ Under Bridge Cross tab

        private void RefreshCross()
        {
            _crossings = new List<Crossing>();
            var warn = new List<string>();
            WithModel((tr, m, w) =>
            {
                try { ComputeProfiles(tr, m); } catch { }
                if (BridgeSettings.UnderIds.Count(Valid) == 0) return;
                StationSet tmp = Stations.Build(m, new List<string>());
                if (tmp.Sections.Count < 2) return;
                _crossings = Crossings.Compute(tr, m, tmp.Sections.First().Station, tmp.Sections.Last().Station, w);
            }, warn);

            _filling = true;
            _gridRoads.Rows.Clear();
            foreach (Crossing c in _crossings)
            {
                int row = _gridRoads.Rows.Add(c.Road, Fmt.Chainage(c.From), Fmt.Chainage(c.To), Fmt.N3(c.Depth), Fmt.N3(c.MinClearance));
                _gridRoads.Rows[row].Tag = c;
            }
            _filling = false;
            UpdateMinClearance();
            FillCrossPoints();
            if (BridgeSettings.UnderIds.Count(Valid) == 0) SetMessage("No under-bridge surfaces selected - nothing to show here.", false);
            else if (_crossings.Count == 0) SetMessage("The alignment does not cross the under-bridge surfaces between the corbels.", true);
            else SetMessage(warn.Count > 0 ? warn[0] : $"{_crossings.Count} road(s) under the bridge.", warn.Count > 0);
            if (_crossings.Any(c => c.Depth <= 0)) SetMessage("Type the bridge depth for every road (Depth column).", true);
        }

        private void ReadDepth(int row)
        {
            if (row < 0 || _filling) return;
            DataGridViewRow r = _gridRoads.Rows[row];
            if (!(r.Tag is Crossing c)) return;
            if (TryNum(Convert.ToString(r.Cells["Depth"].Value), out double d) && d >= 0) c.Depth = d;
            _filling = true;
            r.Cells["Depth"].Value = Fmt.N3(c.Depth);
            r.Cells["MinClr"].Value = Fmt.N3(c.MinClearance);
            _filling = false;
            UpdateMinClearance();
            FillCrossPoints();
        }

        private void UpdateMinClearance()
        {
            Crossing worst = null;
            double min = double.NaN;
            foreach (Crossing c in _crossings)
            {
                double v = c.MinClearance;
                if (!double.IsNaN(v) && (double.IsNaN(min) || v < min)) { min = v; worst = c; }
            }
            _lbMinClr.Text = worst == null ? "" : $"Bridge minimum clearance: {min.ToString("0.000", CI)} m   ({worst.Road})";
        }

        // the longitudinal section of Roads Below: deck, roads with barriers, clearance, NGL, axes
        private void FillCrossPoints()
        {
            if (_planRoads == null) return;
            var v = _planRoads;
            v.Axes = _axes.Select(a => (a.Name, a.Station)).ToList();
            v.DeckTop = _deckProf;
            v.Ngl = _nglProf;
            v.Roads = _crossings.Select(c => (c.Road, c.Depth,
                                              c.Points.Where(p => !double.IsNaN(p.RoadZ)).Select(p => (p.Station, p.RoadZ)).ToList(),
                                              c.Points.Select(p => (p.Station, p.BridgeZ)).ToList())).ToList();
            var depths = _crossings.Select(c => c.Depth).Where(d => d > 0).ToList();
            v.DeckDepth = depths.Count > 0 ? depths.Max() : double.NaN;
            v.Message = _axes.Count < 2 ? "Pick the axes in Setup" : _deckProf.Count < 2 ? "Pick the design surface in Setup" : "No roads under the bridge";
            v.Invalidate();
        }

        // design and NGL levels along the alignment, first axis - 6 m .. last axis + 6 m, every 0.5 m
        private void ComputeProfiles(Transaction tr, BridgeModel m)
        {
            _deckProf = new List<(double, double)>();
            _nglProf = new List<(double, double)>();
            if (_axes.Count < 2) return;
            double a = _axes.Min(x => x.Station) - 6, b = _axes.Max(x => x.Station) + 6;
            var design = BridgeSettings.DesignIds.Count(Valid) > 0 ? new SurfaceSet(tr, BridgeSettings.DesignIds) : null;
            var ngl = BridgeSettings.NglIds.Count(Valid) > 0 ? new SurfaceSet(tr, BridgeSettings.NglIds) : null;
            int n = Math.Max(2, (int)Math.Ceiling((b - a) / 0.5));
            for (int k = 0; k <= n; k++)
            {
                double st = a + (b - a) * k / n;
                Point3d p;
                try { p = m.Al.PointAt(st); } catch { continue; }
                double? dz = design?.Z(p.X, p.Y);
                if (dz.HasValue) _deckProf.Add((st, dz.Value));
                double? gz = ngl?.Z(p.X, p.Y);
                if (gz.HasValue) _nglProf.Add((st, gz.Value));
            }
        }

        // ================================================================ Stations tab

        // inputs that change the stations; when it differs the stations are generated again
        private string Signature()
        {
            var sb = new StringBuilder();
            sb.Append(BridgeSettings.AlignmentId.Handle).Append(BridgeSettings.ReverseAlignment ? "|REV" : "");
            foreach (ObjectId id in BridgeSettings.LeftIds) sb.Append("|L").Append(id.Handle);
            foreach (ObjectId id in BridgeSettings.RightIds) sb.Append("|R").Append(id.Handle);
            sb.Append('|').Append(BridgeSettings.StartStation.ToString("R", CI));
            sb.Append('|').Append(BridgeSettings.CorbelStart.ToString("R", CI)).Append('|').Append(BridgeSettings.CorbelEnd.ToString("R", CI));
            sb.Append('|').Append(BridgeSettings.Spacing.ToString("R", CI));
            sb.Append("|N").Append(string.Join(",", BridgeSettings.NearDist.Select(d => d.ToString("R", CI))));
            foreach (var kv in BridgeSettings.DivExtra.OrderBy(k => k.Key)) sb.Append("|P").Append(kv.Key).Append(':').Append(kv.Value);
            foreach (var kv in BridgeSettings.Internal)
                sb.Append("|I").Append(kv.Key).Append(':').Append(kv.Value.Enabled ? kv.Value.Count : 0).Append(':').Append(kv.Value.Gap.ToString("R", CI))
                  .Append(':').Append(kv.Value.Length.ToString("R", CI));
            foreach (AxisInfo a in _axes)
                sb.Append('|').Append(a.Id.Handle).Append(':').Append(a.Diaphragm.ToString("R", CI)).Append(':')
                  .Append(a.BeforeD1.ToString("R", CI)).Append(':').Append(a.BeforeD2.ToString("R", CI)).Append(':')
                  .Append(a.AfterD1.ToString("R", CI)).Append(':').Append(a.AfterD2.ToString("R", CI));
            for (int i = 0; i + 1 < _axes.Count; i++)
            {
                if (!BridgeSettings.Cj.TryGetValue(BridgeSettings.SpanKey(_axes[i], _axes[i + 1]), out CjSpan c) || !c.Enabled) continue;
                sb.Append("|CJ").Append(i).Append(':').Append(c.Length.ToString("R", CI)).Append(':').Append(c.Factor.ToString("R", CI))
                  .Append(':').Append(c.Dist1.ToString("R", CI)).Append(':').Append(c.Dist2.ToString("R", CI));
            }
            return sb.ToString();
        }

        private void EnsureStations(bool force)
        {
            // the CJ and internal diaphragm lengths need the computed span lengths (along the Centerline)
            bool needCj = BridgeSettings.Cj.Values.Any(c => c.Enabled && double.IsNaN(c.AutoLength) && !c.UserLength.HasValue);
            bool needId = _axes.Count > 1 && Enumerable.Range(0, _axes.Count - 1)
                              .Any(i => double.IsNaN(BridgeSettings.IdOf(BridgeSettings.SpanKey(_axes[i], _axes[i + 1])).AutoLength));
            if (needCj || needId)
                WithModel((tr, m, w) =>
                {
                    foreach (SpanLength sl in SpanLengths.Compute(m, new List<string>()))
                    {
                        BridgeSettings.CjOf(sl.Key).AutoLength = sl.Length;
                        BridgeSettings.IdOf(sl.Key).AutoLength = sl.Length;
                    }
                }, new List<string>());

            ReadAxesGrid();
            string sig = Signature();
            if (!force && _set != null && sig == _setSig) { FillStations(); return; }
            if (_set != null && _set.Sections.Any(s => s.Edited) && sig != _setSig && !force)
            {
                var ans = MessageBox.Show(this, "The setup has changed. Generate the stations again?\nYour manual edits on the Stations tab will be lost.",
                                          "AB Bridges", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ans == DialogResult.No) { FillStations(); return; }
            }

            var warn = new List<string>();
            StationSet set = null;
            if (!WithModel((tr, m, w) => { set = Stations.Build(m, w); }, warn) || set == null) return;
            _set = set;
            _setSig = Signature();
            FillStations();
            SetMessage(warn.Count > 0 ? $"{_set.Sections.Count} stations - {warn[0]}" : $"{_set.Sections.Count} stations.", warn.Count > 0);
        }

        private void FillStations()
        {
            if (_set == null) return;
            DrawSectionPlans();
            _filling = true;
            int keepRow = _gridSt.CurrentRow?.Index ?? 0;
            _gridSt.Rows.Clear();
            int i = 0;
            foreach (Section s in _set.Sections)
            {
                int row = _gridSt.Rows.Add((++i).ToString(CI), s.Remark, s.TypeName, s.Station.ToString("0.000", CI), Fmt.Chainage(s.Station), s.SkewDeg.ToString("0.000", CI));
                DataGridViewRow r = _gridSt.Rows[row];
                r.Tag = s;
                Color back;
                switch (s.Kind)
                {
                    case SectionKind.Axis: back = Color.FromArgb(255, 242, 168); break;
                    case SectionKind.DiaphragmFace: back = Color.FromArgb(253, 233, 217); break;
                    case SectionKind.Corbel: back = Color.FromArgb(217, 242, 227); break;
                    case SectionKind.ConstructionJoint: back = Color.FromArgb(233, 223, 251); break;
                    case SectionKind.NearDiaphragm: back = Color.FromArgb(238, 242, 247); break;
                    default: back = s.Edited ? Color.FromArgb(255, 245, 200) : Color.White; break;
                }
                r.DefaultCellStyle.BackColor = back;
                if (s.Kind == SectionKind.ConstructionJoint) r.DefaultCellStyle.ForeColor = Color.FromArgb(91, 33, 182);
                if (!s.Editable) r.Cells["Station"].Style.ForeColor = Color.DimGray;
            }
            if (_gridSt.Rows.Count > 0)
            {
                keepRow = Math.Max(0, Math.Min(_gridSt.Rows.Count - 1, keepRow));
                _gridSt.CurrentCell = _gridSt.Rows[keepRow].Cells["Remark"];
            }
            _filling = false;
        }

        // re-sort, renumber and recompute the skew after a change
        private void Recalc()
        {
            _set.Sections = _set.Sections.OrderBy(s => s.Station).ToList();
            Stations.Renumber(_set);
            WithModel((tr, m, w) => Stations.ApplySkew(_set, m.Al, BridgeSettings.SkewTransition), new List<string>());
            FillStations();
        }

        private void StationEdited(int row)
        {
            if (_filling || row < 0 || _set == null) return;
            DataGridViewRow r = _gridSt.Rows[row];
            if (!(r.Tag is Section s) || !s.Editable) return;
            if (!TryNum(Convert.ToString(r.Cells["Station"].Value), out double v)) { SetMessage("Station is not a number.", true); FillStations(); return; }
            if (Math.Abs(v - s.Station) < 1e-9) { FillStations(); return; }
            int span = Stations.SpanAt(_set, v);
            if (span < 0) { SetMessage("A division station must stay between the diaphragm faces of a span.", true); FillStations(); return; }
            if (_set.Sections.Any(o => o != s && Math.Abs(o.Station - v) < 0.001)) { SetMessage("Another station is already there.", true); FillStations(); return; }
            s.Station = v;
            s.Span = span;
            s.Edited = true;
            Recalc();
            SetMessage($"Station moved to {Fmt.Chainage(v)}.", false);
        }

        private void AddStation()
        {
            if (_set == null || _gridSt.CurrentRow == null) return;
            int i = _gridSt.CurrentRow.Index;
            if (i + 1 >= _set.Sections.Count) { SetMessage("Select a row that has a station after it.", true); return; }
            double mid = (_set.Sections[i].Station + _set.Sections[i + 1].Station) / 2.0;
            int span = Stations.SpanAt(_set, mid);
            if (span < 0) { SetMessage("Stations can be added only inside a span (between the diaphragm faces).", true); return; }
            _set.Sections.Add(new Section { Remark = "", Kind = SectionKind.Division, Station = mid, Span = span, Edited = true });
            Recalc();
            SetMessage($"Station added at {Fmt.Chainage(mid)}.", false);
        }

        private void DeleteStations()
        {
            if (_set == null) return;
            var del = _gridSt.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as Section).Where(s => s != null && s.Editable).ToList();
            if (del.Count == 0 && _gridSt.CurrentRow?.Tag is Section cs && cs.Editable) del.Add(cs);
            if (del.Count == 0) { SetMessage("Only Division stations can be deleted.", true); return; }
            foreach (Section s in del) _set.Sections.Remove(s);
            Recalc();
            SetMessage($"{del.Count} station(s) deleted.", false);
        }

        // ---- on the plan: click adds a section there, Shift + click deletes the nearest division / near-diaphragm one
        // the equally divided part under x (between two fixed stations), or none
        private (string key, double a, double b, int n, int auto)? PartAt(double x)
        {
            if (_set == null) return null;
            foreach (var p in _set.Parts) if (x > p.a + 1e-6 && x < p.b - 1e-6) return p;
            return null;
        }

        // Shift + click: the division nearest to x inside its part goes, the part is divided again equally
        private Section DeletableAt(double x)
        {
            var p = PartAt(x);
            if (p == null || p.Value.n <= 1) return null;
            return _set.Sections.Where(s => s.Kind == SectionKind.Division && s.Station > p.Value.a && s.Station < p.Value.b)
                                .OrderBy(s => Math.Abs(s.Station - x)).FirstOrDefault();
        }

        // click: one more equal part where you click (the sections of that part are spread again)
        private void AddStationAt(double x) => ChangeParts(x, +1);
        private void DeleteStationNear(double x) => ChangeParts(x, -1);

        private void ChangeParts(double x, int d)
        {
            var p = PartAt(x);
            if (p == null) { SetMessage("Click between two sections of a span (not on a diaphragm, internal diaphragm or joint pair).", true); return; }
            int n = p.Value.n + d;
            if (n < 1) { SetMessage("This part has no section in it to take away.", true); return; }
            int extra = n - p.Value.auto;
            if (extra == 0) BridgeSettings.DivExtra.Remove(p.Value.key); else BridgeSettings.DivExtra[p.Value.key] = extra;
            EnsureStations(true);
            double gap = (p.Value.b - p.Value.a) / n;
            string over = gap > BridgeSettings.Spacing + 1e-6 ? $"  (more than Max spacing {BridgeSettings.Spacing:0.00})" : "";
            SetMessage($"{Fmt.Chainage(p.Value.a)} → {Fmt.Chainage(p.Value.b)}: {n} equal parts of {gap:0.000} m{over}.", over.Length > 0);
        }

        private void ResetStations()
        {
            if (_set != null && (_set.Sections.Any(s => s.Edited) || BridgeSettings.DivExtra.Count > 0))
            {
                if (MessageBox.Show(this, "Divide every part again from Max spacing and lose the parts you added / took away?", "AB Bridges",
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            BridgeSettings.DivExtra.Clear();          // back to the parts from Max spacing
            EnsureStations(true);
        }

        private void SkewChanged()
        {
            BridgeSettings.SkewTransition = (int)_nudSkew.Value;
            if (_set != null) Recalc();
        }

        // ================================================================ preview

        private void DoPreview()
        {
            if (!CheckSetup(false)) return;
            var warn = new List<string>();
            List<Section> secs = null;
            List<SpanLength> spans = null;
            bool ok = WithModel((tr, m, w) =>
            {
                StationSet set;
                if (_tabs.SelectedTab == _tpSt && _set != null) set = _set;
                else set = Stations.Build(m, w);
                secs = set.Sections.Select(s => s.Clone()).ToList();
                Stations.ComputeEdges(secs, m, null, null, w);
                spans = SpanLengths.Compute(m, new List<string>());
            }, warn);
            if (!ok || secs == null) return;
            _preview.Show(secs, spans);
            _ed.UpdateScreen();
            int bad = secs.Count(s => !s.HasEdges);
            SetMessage($"Preview: {secs.Count} stations" + (bad > 0 ? $", {bad} without edges" : "") + (warn.Count > 0 ? " - " + warn[0] : ""),
                       bad > 0 || warn.Count > 0);
        }

        // ================================================================ generate

        private void DoRun()
        {
            if (!CheckSetup(true)) return;
            EnsureStations(false);
            if (_set == null || _set.Sections.Count == 0) { SetMessage("No stations.", true); return; }

            string bridgeName = string.IsNullOrWhiteSpace(_tbName.Text) ? "Bridge" : _tbName.Text.Trim();
            string path = AskPath();
            bool saved = false;
            if (path == null) { SetMessage("Cancelled - nothing was drawn or saved.", true); return; }
            var warn = new List<string>();
            List<Section> sections = _set.Sections.Select(s => s.Clone()).ToList();
            List<SpanLength> spans = null;
            List<Crossing> crossings = new List<Crossing>();
            List<AxisSection> axisSections = null;
            string designNames = "", nglNames = "", underNames = "";
            int flFailed = 0;

            Cursor = Cursors.WaitCursor;
            try
            {
                using (_doc.LockDocument())
                {
                    using (Transaction tr = _db.TransactionManager.StartTransaction())
                    {
                        if (_cbReplace.Checked)
                        {
                            foreach (ObjectId id in BridgeSettings.LastRun.Where(Valid))
                                tr.GetObject(id, OpenMode.ForWrite).Erase();
                            BridgeSettings.LastRun.Clear();
                        }

                        using (BridgeModel m = BridgeModel.Load(tr, warn))
                        {
                            _axes = m.Axes;
                            var design = new SurfaceSet(tr, BridgeSettings.DesignIds);
                            var ngl = new SurfaceSet(tr, BridgeSettings.NglIds);
                            designNames = design.NameList;
                            nglNames = ngl.NameList;
                            underNames = new SurfaceSet(tr, BridgeSettings.UnderIds).NameList;

                            Stations.ComputeEdges(sections, m, design, ngl, warn);
                            axisSections = AxisSections.Build(sections, design, ngl, warn);
                            spans = SpanLengths.Compute(m, warn);
                            crossings = Crossings.Compute(tr, m, sections.First().Station, sections.Last().Station, warn);
                            if (crossings.Any(c => c.Depth <= 0)) warn.Add("Depth is 0 for some under-bridge roads - clearance is bridge level minus road level.");

                            BridgeSettings.LastRun = Draw.Result(tr, _db, sections, spans, bridgeName, _cbFeature.Checked, out flFailed);
                        }
                        tr.Commit();
                    }
                    // remember the NGL in this drawing
                    try { DwgStore.SaveNgl(_db, BridgeSettings.NglIds); } catch (Exception ex) { warn.Add("NGL could not be saved in the drawing: " + ex.Message); }
                }
                _preview.Clear();
                _ed.Regen();
                _crossings = crossings;

                if (flFailed > 0) warn.Add($"Feature lines could not be created for {flFailed} section(s); 3D polylines were drawn instead.");

                var info = new List<(string, string)>
                {
                    ("Bridge", bridgeName),
                    ("Drawing", string.IsNullOrEmpty(_db.Filename) ? "(unsaved)" : _db.Filename),
                    ("Alignment start station", Fmt.Chainage(BridgeSettings.StartStation)),
                    ("Design surface", designNames),
                    ("NGL surface", string.IsNullOrEmpty(nglNames) ? "—" : nglNames),
                    ("Under-bridge surfaces", string.IsNullOrEmpty(underNames) ? "—" : underNames),
                    ("Span length", Valid(BridgeSettings.CenterlineId) ? "from the centerline" : "automatic (center between edges)"),
                    ("Corbel start / end (m)", $"{Fmt.N3(BridgeSettings.CorbelStart)} / {Fmt.N3(BridgeSettings.CorbelEnd)}"),
                    ("Max spacing (m)", Fmt.N3(BridgeSettings.Spacing)),
                    ("Skew transition", $"{BridgeSettings.SkewTransition} stations each side"),
                    ("Units", "meters"),
                    ("Created", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CI) + "   (" + Version + ")")
                };
                var data = new ReportData
                {
                    BridgeName = bridgeName,
                    Sections = sections,
                    Axes = _axes,
                    Spans = _set.Spans,
                    SpanLengths = spans,
                    Crossings = crossings,
                    Info = info,
                    DesignNames = designNames,
                    NglNames = nglNames,
                    Warnings = warn,
                    BasePoint = BasePointStore.Load(_db),
                    AxisSections = axisSections
                };
                Report.Write(path, data);

                int bad = sections.Count(s => !s.HasEdges);
                SetMessage($"Saved: {path}" + (bad > 0 ? $"  -  {bad} stations without edges" : "") +
                           (warn.Count > 0 ? $"  ({warn.Count} warnings - see Info sheet)" : ""), bad > 0 || warn.Count > 0);

                _ed.WriteMessage($"\nAB Bridges: {sections.Count} stations - Excel saved to {path}");
                foreach (SpanLength sl in spans ?? new List<SpanLength>())
                    _ed.WriteMessage($"\n  Span {sl.Name}: L = {Fmt.N3(sl.Length)} m" + (string.IsNullOrEmpty(sl.Note) ? "" : "  (" + sl.Note + ")"));
                foreach (string w in warn.Take(20)) _ed.WriteMessage("\n  - " + w);
                _ed.WriteMessage("\n");

                // done: the window closes by itself
                saved = true;

            }
            catch (Exception ex)
            {
                SetMessage("Generate failed: " + ex.Message, true);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
            if (saved)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        // where to save the Excel (Save As dialog); null when cancelled
        private string AskPath()
        {
            while (true)
            {
                using (var dlg = new SaveFileDialog
                {
                    Title = "Save the Excel file",
                    Filter = "Excel workbook (*.xlsx)|*.xlsx",
                    FileName = SafeName() + ".xlsx",
                    OverwritePrompt = true,
                    AddExtension = true,
                    DefaultExt = "xlsx"
                })
                {
                    try { dlg.InitialDirectory = Folder(); } catch { }
                    if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                    string p = dlg.FileName;
                    if (File.Exists(p) && IsLocked(p))
                    {
                        MessageBox.Show(this, "This file is open in Excel. Close it first, or choose another name.", "AB Bridges",
                                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        continue;
                    }
                    BridgeSettings.OutputFolder = Path.GetDirectoryName(p);
                    return p;
                }
            }
        }

        private static bool IsLocked(string path)
        {
            try { using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return false; }
            catch (IOException) { return true; }
        }
    }
}
