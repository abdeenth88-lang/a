// AB Bridges for Revit - "Create Deck" command and its dialog.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using Form = System.Windows.Forms.Form;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Panel = System.Windows.Forms.Panel;
using TextBox = System.Windows.Forms.TextBox;
using CheckBox = System.Windows.Forms.CheckBox;
using Label = System.Windows.Forms.Label;
using Button = System.Windows.Forms.Button;

namespace ABBridgesRevit
{
    [Transaction(TransactionMode.Manual)]
    public class CreateDeckCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try { return Run(data, ref message); }
            catch (Exception ex)
            {
                var td = new TaskDialog("AB Bridges - error")
                {
                    MainInstruction = ex.GetType().Name + ": " + ex.Message,
                    ExpandedContent = ex.ToString()
                };
                td.Show();
                return Result.Cancelled;
            }
        }

        private Result Run(ExternalCommandData data, ref string message)
        {
            UIApplication uiapp = data.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }
            var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => f.IsEditable && !f.IsInPlace).ToList();

            // inside an adaptive / mass family: build right here (fast, no loading into the project)
            if (doc.IsFamilyDocument)
            {
                using (var form = new CreateForm(families, true))
                {
                    if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    var fopt = new BuildOptions { Swap = form.Swap, Shared = false };
                    var flog = new StringBuilder();
                    foreach (FamilyGroup g in form.Chosen)
                    {
                        Family profile = families.FirstOrDefault(f => f.Name == g.FamilyName);
                        if (profile == null) { flog.AppendLine($"Family {g.Number}: \"{g.FamilyName}\" is not loaded in this family - load it first (Insert > Load Family)."); continue; }
                        try { flog.AppendLine(DeckBuilder.BuildHere(doc, form.Excel, g, profile, fopt)); }
                        catch (Exception ex) { flog.AppendLine($"Family {g.Number} ({g.FamilyName}) failed: {ex.Message}"); }
                    }
                    new TaskDialog("AB Bridges") { MainInstruction = "Create Deck (in this family)", MainContent = flog.ToString() }.Show();
                }
                return Result.Succeeded;
            }

            using (var form = new CreateForm(families, false))
            {
                if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;

                string template = DeckBuilder.FindTemplate(uiapp.Application);
                if (template == null || !File.Exists(template))
                {
                    using (var dlg = new OpenFileDialog { Title = "Choose the adaptive family template (Metric Generic Model Adaptive.rft)", Filter = "Family template (*.rft)|*.rft" })
                    {
                        if (dlg.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                        template = dlg.FileName;
                    }
                }

                // the same build as inside an adaptive family (one family with every group, so the cuts work), then
                // loaded into the project and placed at its shared coordinates from the survey point
                var log = new StringBuilder();
                var fopt = new BuildOptions { Swap = form.Swap, Shared = false, Template = template };
                string tmpDir = Path.Combine(Path.GetTempPath(), "ABBridges");
                Directory.CreateDirectory(tmpDir);
                string famName = string.Concat($"AB {form.Excel.BridgeName} - Deck".Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                string famPath = Path.Combine(tmpDir, famName + ".rfa");

                // the profile families of the project -> files
                var profilePaths = new Dictionary<string, string>();
                foreach (string name in form.Chosen.Select(g => g.FamilyName).Distinct())
                {
                    Family pf = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.IsValidObject && f.Name == name);
                    if (pf == null) { log.AppendLine($"\"{name}\" is not loaded in this project - skipped."); continue; }
                    string pth = Path.Combine(tmpDir, string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)) + ".rfa");
                    Document pdoc = doc.EditFamily(pf);
                    try { pdoc.SaveAs(pth, new SaveAsOptions { OverwriteExistingFile = true }); }
                    finally { pdoc.Close(false); }
                    profilePaths[name] = pth;
                }

                // the deck family already in the project is edited (the chosen groups are added / replaced in it);
                // otherwise a new one is made from the adaptive template
                Family existing = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                    .FirstOrDefault(f => f.IsValidObject && f.Name == famName && f.IsEditable);
                Document fdoc = null;
                if (existing != null)
                {
                    // edited only if it was built by this add-in (it keeps what it made); an older / hand-made one is rebuilt
                    fdoc = doc.EditFamily(existing);
                    bool ours = false;
                    try { ours = AbStore.Load(fdoc).Keys.Any(k => k.StartsWith("ITEM|", StringComparison.Ordinal)); } catch { }
                    if (ours) log.AppendLine($"Editing \"{famName}\" already in this project (only the checked items are replaced).");
                    else
                    {
                        fdoc.Close(false);
                        fdoc = null;
                        log.AppendLine($"\"{famName}\" in this project was not built by this version - a new one is built and replaces it (build every family you need).");
                    }
                }
                if (fdoc == null) fdoc = uiapp.Application.NewFamilyDocument(template);
                Family loaded = null;
                try
                {
                    var inFam = new Dictionary<string, Family>();
                    using (var t = new Transaction(fdoc, "AB Bridges - load profiles"))
                    {
                        t.Start();
                        foreach (var kv in profilePaths)
                        {
                            Family f = null;
                            try { fdoc.LoadFamily(kv.Value, new DeckBuilder.LoadOpts(), out f); } catch { }
                            f = f ?? new FilteredElementCollector(fdoc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(x => x.Name == kv.Key);
                            if (f != null) inFam[kv.Key] = f;
                        }
                        t.Commit();
                    }
                    foreach (FamilyGroup g in form.Chosen)
                    {
                        if (!inFam.TryGetValue(g.FamilyName, out Family prof)) continue;
                        try { log.AppendLine(DeckBuilder.BuildHere(fdoc, form.Excel, g, prof, fopt)); }
                        catch (Exception ex) { log.AppendLine($"Family {g.Number} ({g.FamilyName}) failed: {ex.Message}"); }
                    }
                    fdoc.SaveAs(famPath, new SaveAsOptions { OverwriteExistingFile = true });
                    loaded = fdoc.LoadFamily(doc, new DeckBuilder.LoadOpts());
                }
                finally { fdoc.Close(false); }

                if (loaded == null) log.AppendLine("The deck family could not be loaded into the project.");
                else
                {
                    FamilySymbol sym = loaded.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol).FirstOrDefault(x => x != null);
                    string origin = sym?.LookupParameter(DeckBuilder.OriginParam)?.AsString();
                    // not kept in the type: the Excel base point is the family origin
                    if (!PlaceDeckCommand.TryParseOrigin(origin, out _, out _, out _) && form.Excel.BasePoint.HasValue)
                    {
                        var bp = form.Excel.BasePoint.Value;
                        origin = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0};{1};{2}", bp.E, bp.N, bp.Z);
                    }
                    if (sym == null || !PlaceDeckCommand.TryParseOrigin(origin, out double e, out double n, out double z))
                        log.AppendLine("The deck family has no origin - not placed.");
                    else
                    {
                        log.AppendLine().AppendLine(PlaceDeckCommand.PlaceAt(doc, sym, e, n, z));
                        log.AppendLine().AppendLine(PlaceDeckCommand.Check(doc, sym, e, n, z, form.Excel));
                    }
                }

                new TaskDialog("AB Bridges") { MainInstruction = "Create Deck", MainContent = log.ToString() }.Show();
            }
            return Result.Succeeded;
        }
    }

    internal sealed class CreateForm : Form
    {
        internal static string LastExcel = "";
        internal static bool LastSwap, LastShared = true;

        private static readonly Color Navy = Color.FromArgb(31, 58, 95);
        private readonly List<Family> _families;
        private readonly TextBox _tb;
        private readonly TreeView _tree;
        private bool _syncing;
        private readonly CheckBox _swap, _shared;
        private readonly Label _info;

        public BridgeExcel Excel { get; private set; }
        public List<FamilyGroup> Chosen { get; private set; } = new List<FamilyGroup>();
        public bool Swap => _swap.Checked;
        public bool Shared => _shared.Checked;

        private readonly bool _inFamily;

        public CreateForm(List<Family> families, bool inFamily)
        {
            _inFamily = inFamily;
            _families = families;
            Text = inFamily ? "AB Bridges - Create Deck (in this family)" : "AB Bridges - Create Deck";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new System.Drawing.Font("Segoe UI", 9.5f);
            BackColor = Color.White;
            ShowInTaskbar = false;
            ClientSize = new Size(600, 400);

            Controls.Add(new Label { Text = "Excel file", Font = new System.Drawing.Font("Segoe UI Semibold", 9.5f), Location = new Point(20, 22), AutoSize = true });
            _tb = new TextBox { Location = new Point(100, 18), Size = new Size(380, 25), Text = LastExcel };
            _tb.Leave += (s, e) => LoadExcel();
            var b = Btn("Browse...", new Point(490, 16), 90, false);
            b.Click += (s, e) => Browse();
            Controls.Add(_tb);
            Controls.Add(b);

            var head = new Panel { Location = new Point(20, 60), Size = new Size(560, 28), BackColor = Navy };
            head.Controls.Add(new Label { Text = "Families to build (from the Parameters sheet)", ForeColor = Color.White, Font = new System.Drawing.Font("Segoe UI Semibold", 9.5f), Location = new Point(8, 5), AutoSize = true });
            Controls.Add(head);
            // families, and under the corbel / outrigger families every instance (CB-Start, CB-End / A1, P1, ...): all checked
            _tree = new TreeView { Location = new Point(20, 88), Size = new Size(560, 150), CheckBoxes = true, BorderStyle = BorderStyle.FixedSingle, ShowLines = true, FullRowSelect = false, HideSelection = true };
            _tree.AfterCheck += OnAfterCheck;
            Controls.Add(_tree);

            _info = new Label { Location = new Point(20, 244), Size = new Size(560, 20), ForeColor = Color.FromArgb(107, 114, 128) };
            Controls.Add(_info);
            _swap = new CheckBox { Text = "Swap points (point 1 = left edge instead of right)", Location = new Point(20, 270), AutoSize = true, Checked = LastSwap };
            _shared = new CheckBox { Text = "Excel X / Y / Z are shared coordinates", Location = new Point(20, 296), AutoSize = true, Checked = LastShared };
            Controls.Add(_swap);
            Controls.Add(_shared);
            if (!inFamily)
            {
                // the project build is always placed from the survey point (shared coordinates)
                _shared.Checked = true;
                _shared.Visible = false;
                Controls.Add(new Label { Text = "Built as one family around the Excel base point, placed at its shared coordinates.", Location = new Point(20, 298), Size = new Size(560, 20), ForeColor = Color.FromArgb(107, 114, 128) });
            }
            if (inFamily)
            {
                // a family has no shared coordinates: the deck is built around the first left point of the Excel
                _shared.Checked = false;
                _shared.Visible = false;
                Controls.Add(new Label { Text = "Built in this family around the Excel base point (0,0,0); re-running replaces only the checked items.", Location = new Point(20, 298), Size = new Size(560, 20), ForeColor = Color.FromArgb(107, 114, 128) });
            }

            Controls.Add(new Panel { Location = new Point(0, 340), Size = new Size(600, 1), BackColor = Color.FromArgb(220, 224, 230) });
            var ok = Btn("Create", new Point(394, 352), 100, true);
            var close = Btn("Close", new Point(500, 352), 80, false);
            close.DialogResult = DialogResult.Cancel;
            ok.Click += (s, e) => OnOk();
            Controls.Add(ok);
            Controls.Add(close);
            AcceptButton = ok;
            CancelButton = close;

            if (File.Exists(LastExcel)) LoadExcel();
        }

        private static Button Btn(string text, Point at, int w, bool primary)
        {
            var b = new Button
            {
                Text = text, Location = at, Size = new Size(w, 30), FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Navy : Color.White, ForeColor = primary ? Color.White : Navy, Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Navy;
            return b;
        }

        private void Browse()
        {
            using (var dlg = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", Title = "Bridge Excel (after editing the parameters)" })
            {
                try { if (File.Exists(_tb.Text)) dlg.InitialDirectory = Path.GetDirectoryName(_tb.Text); } catch { }
                if (dlg.ShowDialog(this) == DialogResult.OK) { _tb.Text = dlg.FileName; LoadExcel(); }
            }
        }

        private sealed class Item
        {
            public FamilyGroup G;
            public bool Loaded;
            public override string ToString()
            {
                string what = G.PerInstance ? $"{G.Instances.Count} instance(s)" : $"{G.Instances.FirstOrDefault().nos?.Count ?? 0} stations";
                return $"Family {G.Number}  —  {(string.IsNullOrEmpty(G.FamilyName) ? "(no family name - run Read Family Parameters)" : G.FamilyName)}   ·   {what}" +
                       (Loaded ? "" : "   (not in this project)");
            }
        }

        // "OR-A1" -> "A1" (outriggers are shown by the axis name); corbels keep CB-Start / CB-End
        private static string Show(string inst) => inst.StartsWith("OR-", StringComparison.OrdinalIgnoreCase) ? inst.Substring(3) : inst;

        private void OnAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_syncing || e.Action == TreeViewAction.Unknown) return;
            _syncing = true;
            try
            {
                if (e.Node.Parent == null)
                    foreach (TreeNode c in e.Node.Nodes) c.Checked = e.Node.Checked;            // family on / off -> all its instances
                else
                    e.Node.Parent.Checked = e.Node.Parent.Nodes.Cast<TreeNode>().Any(c => c.Checked);
            }
            finally { _syncing = false; }
        }

        private void LoadExcel()
        {
            _tree.Nodes.Clear();
            Excel = null;
            string p = _tb.Text.Trim();
            if (!File.Exists(p)) { _info.Text = ""; return; }
            try
            {
                Excel = BridgeExcel.Load(p);
                _syncing = true;
                foreach (FamilyGroup g in Excel.Groups)
                {
                    bool loaded = _families.Any(f => f.Name == g.FamilyName);
                    var it = new Item { G = g, Loaded = loaded };
                    var node = new TreeNode(it.ToString()) { Tag = it, Checked = loaded };
                    if (!loaded) node.ForeColor = Color.FromArgb(150, 150, 150);
                    if (g.PerInstance)
                        foreach (var inst in g.Instances)
                            node.Nodes.Add(new TreeNode(Show(inst.name) + $"   ({inst.nos.Count} stations)") { Tag = inst.name, Checked = loaded });
                    _tree.Nodes.Add(node);
                }
                _tree.ExpandAll();
                _syncing = false;
                int pts = Excel.Stations.Values.Count(s => s.HasPoints);
                _info.Text = $"{Excel.BridgeName}: {Excel.Stations.Count} stations ({pts} with left/right points).";
            }
            catch (Exception ex)
            {
                _info.Text = "Could not read the Excel: " + ex.Message;
            }
        }

        private void OnOk()
        {
            if (Excel == null) LoadExcel();
            if (Excel == null) { MessageBox.Show(this, "Choose the bridge Excel first.", "AB Bridges"); return; }
            Chosen = new List<FamilyGroup>();
            foreach (TreeNode node in _tree.Nodes)
            {
                var it = (Item)node.Tag;
                if (!it.Loaded || !node.Checked) continue;
                FamilyGroup g = it.G;
                if (g.PerInstance)
                {
                    // only the checked instances are built (and replaced if they were built before)
                    var on = new HashSet<string>(node.Nodes.Cast<TreeNode>().Where(c => c.Checked).Select(c => (string)c.Tag));
                    if (on.Count == 0) continue;
                    g.Instances = g.Instances.Select(i => (i.name, on.Contains(i.name), i.nos)).ToList();
                }
                Chosen.Add(g);
            }
            if (Chosen.Count == 0) { MessageBox.Show(this, "Tick at least one family (and instance) that is loaded here.", "AB Bridges"); return; }
            LastExcel = _tb.Text.Trim();
            LastSwap = Swap;
            if (!_inFamily) LastShared = Shared;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
