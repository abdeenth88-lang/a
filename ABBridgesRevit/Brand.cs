// AB Bridges - logo, window icon, header bar and the About window (Revit)
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Form = System.Windows.Forms.Form;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;

namespace ABBridgesRevit
{
    internal static class Brand
    {
        public const string Author = "Abdein Badawi";
        public const string Email = "Abdeenth88@gmail.com";
        public const string Version = "2025.11";
        public static readonly Color Navy = Color.FromArgb(31, 58, 95);
        public static readonly Color Accent = Color.FromArgb(232, 121, 43);

        private static Stream Res(string file)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(file, StringComparison.OrdinalIgnoreCase));
            return name == null ? null : asm.GetManifestResourceStream(name);
        }

        // logo picture (embedded AB_logo_256.png), scaled to size x size
        public static Bitmap Logo(int size)
        {
            try
            {
                using (Stream s = Res("AB_logo_256.png"))
                {
                    if (s == null) return null;
                    using (var src = new Bitmap(s))
                    {
                        var bmp = new Bitmap(size, size);
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, size, size);
                        }
                        return bmp;
                    }
                }
            }
            catch { return null; }
        }

        // the AB Bridges header on any dialog: everything moves down by the header height, the window grows by it
        public static void AddHeader(Form f, string page)
        {
            int h = BrandHeader.Height0;
            f.SuspendLayout();
            foreach (Control c in f.Controls.Cast<Control>().ToList())
            {
                if (c.Dock != DockStyle.None) continue;
                bool t = (c.Anchor & AnchorStyles.Top) != 0, b = (c.Anchor & AnchorStyles.Bottom) != 0;
                if (t || !b) c.Top += h;
            }
            f.ClientSize = new Size(f.ClientSize.Width, f.ClientSize.Height + h);
            if (f.MinimumSize.Height > 0) f.MinimumSize = new Size(f.MinimumSize.Width, f.MinimumSize.Height + h);
            f.Controls.Add(new BrandHeader(page));
            SetIcon(f);
            f.ResumeLayout();
        }

        // window icon (embedded AB.ico)
        public static void SetIcon(Form f)
        {
            try
            {
                using (Stream s = Res("AB.ico"))
                    if (s != null) { f.Icon = new Icon(s); f.ShowIcon = true; }
            }
            catch { }
        }
    }

    // navy bar: logo + "AB Bridges" + page name, "Created by ..." on the right
    internal sealed class BrandHeader : Panel
    {
        public const int Height0 = 46;

        public BrandHeader(string page)
        {
            Dock = DockStyle.Top;
            Height = Height0;
            BackColor = Brand.Navy;
            var pic = new PictureBox { Image = Brand.Logo(30), Size = new Size(30, 30), Location = new Point(10, 8), SizeMode = PictureBoxSizeMode.Zoom };
            var title = new Label { Text = "AB Bridges", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 12f), AutoSize = true, Location = new Point(48, 11) };
            // the page name as a white chip (same look as the Civil 3D page chips)
            var sub = new Label { Text = page, ForeColor = Brand.Navy, BackColor = Color.White, Font = new Font("Segoe UI Semibold", 9.5f), AutoSize = true,
                                  Padding = new Padding(9, 5, 9, 5), Location = new Point(150, 9) };
            var by = new Label { Text = "Created by " + Brand.Author, ForeColor = Color.FromArgb(143, 163, 189), Font = new Font("Segoe UI", 8.5f), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            Controls.AddRange(new Control[] { pic, title, sub, by });
            Layout += (s, e) =>
            {
                sub.Left = title.Right + 18;
                by.Location = new Point(Width - by.PreferredWidth - 12, (Height - by.PreferredHeight) / 2);
            };
        }
    }

    internal sealed class AboutForm : Form
    {
        public AboutForm()
        {
            Text = "About AB Bridges";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(380, 210);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);
            Brand.SetIcon(this);
            Controls.Add(new PictureBox { Image = Brand.Logo(72), Size = new Size(72, 72), Location = new Point(20, 20) });
            Controls.Add(new Label { Text = "AB Bridges", Font = new Font("Segoe UI Semibold", 16f), ForeColor = Brand.Navy, AutoSize = true, Location = new Point(106, 22) });
            Controls.Add(new Label { Text = "for Revit 2025   ·   version " + Brand.Version, ForeColor = Color.DimGray, AutoSize = true, Location = new Point(108, 56) });
            Controls.Add(new Label { Text = "Created by " + Brand.Author, ForeColor = Brand.Navy, Font = new Font("Segoe UI Semibold", 9.5f), AutoSize = true, Location = new Point(20, 112) });
            var mail = new LinkLabel { Text = Brand.Email, AutoSize = true, Location = new Point(20, 134), LinkColor = Brand.Accent };
            mail.LinkClicked += (s, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mailto:" + Brand.Email) { UseShellExecute = true }); } catch { } };
            Controls.Add(mail);
            var ok = new Button { Text = "OK", Size = new Size(80, 28), Location = new Point(280, 168), DialogResult = DialogResult.OK, BackColor = Brand.Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            Controls.Add(ok);
            AcceptButton = CancelButton = ok;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            using (var f = new AboutForm()) f.ShowDialog();
            return Result.Succeeded;
        }
    }
}
