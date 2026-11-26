// AB Bridges Setup - one installer for both add-ins (wizard):
//   Welcome -> Install (files + Excel) -> Install in Civil 3D -> Install in Revit -> Finish
//   Files         : Documents\AB Bridges  (Excel template, Families, logo)
//   Civil 3D 2025 : %APPDATA%\Autodesk\ApplicationPlugins\ABBridges.bundle
//   Revit 2025    : %APPDATA%\Autodesk\Revit\Addins\2025\ABBridges.addin + ABBridges\ABBridgesRevit.dll (+ Families)
// Installs for the current Windows user (no administrator rights needed). Run again to update; "-uninstall" removes it.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace ABBridgesSetup
{
    internal static class Program
    {
        private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        internal static string CivilDir => Path.Combine(AppData, "Autodesk", "ApplicationPlugins", "ABBridges.bundle");
        internal static string RevitDir => Path.Combine(AppData, "Autodesk", "Revit", "Addins", "2025");
        internal static string FilesDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AB Bridges");

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            bool quietUninstall = args.Any(a => a.Trim('-', '/').Equals("uninstall", StringComparison.OrdinalIgnoreCase));
            if (quietUninstall) { Uninstall(); return; }
            Application.Run(new SetupForm());
        }

        internal static bool Running(string process) => Process.GetProcessesByName(process).Length > 0;

        // copies payload/<part>/... to dir; returns the number of files
        internal static int Install(string part, string dir)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string pre = "payload/" + part + "/";
            int n = 0;
            foreach (string res in asm.GetManifestResourceNames().Where(r => r.StartsWith(pre, StringComparison.OrdinalIgnoreCase)))
            {
                string rel = res.Substring(pre.Length).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                string target = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (Stream s = asm.GetManifestResourceStream(res))
                using (FileStream f = File.Create(target))
                    s.CopyTo(f);
                n++;
            }
            return n;
        }

        internal static Stream Res(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

        internal static string Uninstall()
        {
            int n = 0;
            try { if (Directory.Exists(CivilDir)) { Directory.Delete(CivilDir, true); n++; } } catch { }
            try { string a = Path.Combine(RevitDir, "ABBridges.addin"); if (File.Exists(a)) { File.Delete(a); n++; } } catch { }
            try { string d = Path.Combine(RevitDir, "ABBridges"); if (Directory.Exists(d)) { Directory.Delete(d, true); n++; } } catch { }
            return n > 0 ? "AB Bridges was removed from Civil 3D and Revit." : "AB Bridges was not installed.";
        }
    }

    internal sealed class SetupForm : Form
    {
        private static readonly Color Navy = Color.FromArgb(31, 58, 95);
        private static readonly Color Ink = Color.FromArgb(55, 65, 81);
        private static readonly Color Ok = Color.FromArgb(22, 128, 61);
        private static readonly string[] Steps = { "Welcome", "Install", "Civil 3D", "Revit", "Finish" };

        private readonly Panel _body;
        private readonly Label[] _chips = new Label[Steps.Length];
        private readonly Button _back, _next, _cancel;
        private readonly Image _logo;
        private int _page;
        private string _filesDone, _civilDone, _revitDone;

        public SetupForm()
        {
            Text = "AB Bridges Setup - 2025";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(600, 400);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9.5f);
            try { using (Stream s = Program.Res("AB.ico")) if (s != null) Icon = new Icon(s); } catch { }
            try { using (Stream s = Program.Res("AB_logo_256.png")) if (s != null) _logo = new Bitmap(Image.FromStream(s)); } catch { }

            // navy header: logo + title + step chips
            var head = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Navy };
            if (_logo != null) head.Controls.Add(new PictureBox { Image = _logo, SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(14, 12), Size = new Size(54, 54) });
            head.Controls.Add(new Label { Text = "AB Bridges Setup", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 15f), Location = new Point(76, 10), AutoSize = true });
            int x = 79;
            for (int i = 0; i < Steps.Length; i++)
            {
                var c = new Label { Text = $"{i + 1}  {Steps[i]}", AutoSize = true, Padding = new Padding(7, 3, 7, 3), Location = new Point(x, 46), Font = new Font("Segoe UI", 8.5f) };
                head.Controls.Add(c);
                _chips[i] = c;
                x += TextRenderer.MeasureText(c.Text, c.Font).Width + 22;
            }

            var foot = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(244, 246, 249) };
            _back = Btn("< Back", new Point(300, 12), 90, false);
            _next = Btn("Next >", new Point(398, 12), 90, true);
            _cancel = Btn("Cancel", new Point(496, 12), 90, false);
            _back.Click += (s, e) => Show(_page - 1);
            _next.Click += (s, e) => { if (_page == Steps.Length - 1) Close(); else Show(_page + 1); };
            _cancel.Click += (s, e) => Close();
            foot.Controls.AddRange(new Control[] { _back, _next, _cancel });

            _body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 8) };
            Controls.Add(_body);
            Controls.Add(foot);
            Controls.Add(head);
            Show(0);
        }

        private static Button Btn(string text, Point at, int w, bool primary)
        {
            var b = new Button { Text = text, Location = at, Size = new Size(w, 32), FlatStyle = FlatStyle.Flat };
            b.FlatAppearance.BorderColor = Navy;
            if (primary) { b.BackColor = Navy; b.ForeColor = Color.White; } else { b.BackColor = Color.White; b.ForeColor = Navy; }
            return b;
        }

        private Label Text_(string t, int y, int h, Font f = null, Color? c = null)
        {
            var l = new Label { Text = t, Location = new Point(24, y), Size = new Size(552, h), ForeColor = c ?? Ink, Font = f ?? Font };
            _body.Controls.Add(l);
            return l;
        }

        private void Show(int page)
        {
            _page = Math.Max(0, Math.Min(Steps.Length - 1, page));
            for (int i = 0; i < Steps.Length; i++)
            {
                bool on = i == _page;
                _chips[i].BackColor = on ? Color.White : Color.FromArgb(52, 82, 122);
                _chips[i].ForeColor = on ? Navy : Color.FromArgb(200, 215, 235);
            }
            _back.Enabled = _page > 0 && _page < Steps.Length - 1;
            _next.Text = _page == Steps.Length - 1 ? "Finish" : "Next >";
            _cancel.Visible = _page < Steps.Length - 1;

            _body.Controls.Clear();
            var h1 = new Font("Segoe UI Semibold", 13f);
            switch (_page)
            {
                case 0:   // Welcome
                    if (_logo != null) _body.Controls.Add(new PictureBox { Image = _logo, SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(24, 22), Size = new Size(120, 120) });
                    _body.Controls.Add(new Label { Text = "Welcome to AB Bridges", Font = new Font("Segoe UI Semibold", 16f), ForeColor = Navy, Location = new Point(164, 26), AutoSize = true });
                    _body.Controls.Add(new Label { Text = "Civil 3D 2025  +  Revit 2025", ForeColor = Ink, Location = new Point(166, 62), AutoSize = true });
                    _body.Controls.Add(new Label { Text = "Created by Abdein Badawi", Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = Navy, Location = new Point(166, 96), AutoSize = true });
                    _body.Controls.Add(new Label { Text = "Abdeenth88@gmail.com", ForeColor = Ink, Location = new Point(166, 120), AutoSize = true });
                    Text_("This setup installs the AB Bridges files, then the AB Bridges tab in Civil 3D 2025 and in Revit 2025 " +
                          "for this Windows user. Run it again any time to update.", 160, 60);
                    var un = new LinkLabel { Text = "Uninstall AB Bridges", Location = new Point(24, 222), AutoSize = true, LinkColor = Navy };
                    un.LinkClicked += (s, e) =>
                    {
                        if (!CloseApps("Revit", "Revit") || !CloseApps("acad", "Civil 3D / AutoCAD")) return;
                        MessageBox.Show(this, Program.Uninstall(), "AB Bridges Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    };
                    _body.Controls.Add(un);
                    break;

                case 1:   // Install files + Excel
                    Text_("Install the files", 18, 30, h1, Navy);
                    Text_("The Excel template, the Revit families and the logo are copied to:\n" + Program.FilesDir, 54, 46);
                    Step("Install files", () => { _filesDone = $"{Program.Install("Files", Program.FilesDir)} file(s) -> {Program.FilesDir}"; return _filesDone; }, _filesDone, null);
                    break;

                case 2:   // Civil 3D
                    Text_("Install in Civil 3D 2025", 18, 30, h1, Navy);
                    Text_("Adds the AB Bridges tab (Extract Data, Export Base Point, PL. Start).\n" + Program.CivilDir, 54, 46);
                    Step("Install in Civil 3D", () => { _civilDone = $"{Program.Install("Civil3D", Program.CivilDir)} file(s) installed."; return _civilDone; }, _civilDone, ("acad", "Civil 3D / AutoCAD"));
                    break;

                case 3:   // Revit
                    Text_("Install in Revit 2025", 18, 30, h1, Navy);
                    Text_("Adds the AB Bridges tab in Revit 2025.\n" + Program.RevitDir, 54, 46);
                    Step("Install in Revit", () => { _revitDone = $"{Program.Install("Revit", Program.RevitDir)} file(s) installed."; return _revitDone; }, _revitDone, ("Revit", "Revit"));
                    break;

                default:  // Finish
                    Text_("Setup finished", 18, 30, h1, Navy);
                    Text_("Files:      " + (_filesDone ?? "skipped") + "\n\n" +
                          "Civil 3D:  " + (_civilDone ?? "skipped") + "\n\n" +
                          "Revit:      " + (_revitDone ?? "skipped") + "\n\n" +
                          "Open Civil 3D and Revit: the AB Bridges tab is there.", 58, 180);
                    break;
            }
        }

        // a page action: big button + status line; the app must be closed first
        private void Step(string caption, Func<string> act, string done, (string proc, string name)? app)
        {
            var status = Text_(done ?? "", 160, 60, null, done != null ? Ok : Ink);
            var b = Btn(caption, new Point(24, 114), 200, true);
            b.Click += (s, e) =>
            {
                if (app.HasValue && !CloseApps(app.Value.proc, app.Value.name)) return;
                try
                {
                    Cursor = Cursors.WaitCursor;
                    status.ForeColor = Ok;
                    status.Text = "✔  " + act();
                    _next.Focus();
                }
                catch (Exception ex)
                {
                    status.ForeColor = Color.Firebrick;
                    status.Text = "Failed: " + ex.Message;
                }
                finally { Cursor = Cursors.Default; }
            };
            _body.Controls.Add(b);
        }

        private bool CloseApps(string proc, string name)
        {
            while (Program.Running(proc))
            {
                var r = MessageBox.Show(this, $"Please close {name} first, then press Retry.", "AB Bridges Setup", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.Retry) return false;
            }
            return true;
        }
    }
}
