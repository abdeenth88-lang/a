// AB Bridges - shared look: numbered page bar and a live plan drawing (made from the real data)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ABBridges
{
    internal static class Theme
    {
        public static readonly Color Navy = Color.FromArgb(31, 58, 95);
        public static readonly Color Accent = Color.FromArgb(232, 121, 43);
        public static readonly Color Idle = Color.FromArgb(232, 234, 238);
        public static readonly Color IdleText = Color.FromArgb(120, 126, 136);
        public static readonly Color Page = Color.FromArgb(247, 248, 250);
        public static readonly Font Body = new Font("Segoe UI", 9f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9.5f);

        public static readonly Color Alignment = Color.FromArgb(226, 75, 74);
        public static readonly Color Edge = Color.FromArgb(24, 95, 165);
        public static readonly Color Axis = Color.FromArgb(59, 109, 17);
        public static readonly Color CenterLine = Color.FromArgb(133, 79, 11);
        public static readonly Color Corbel = Color.FromArgb(153, 60, 29);
        public static readonly Color Internal = Color.FromArgb(83, 74, 183);
        public static readonly Color Joint = Color.FromArgb(91, 33, 182);
        public static readonly Color Division = Color.FromArgb(150, 150, 150);
        public static readonly Color Dim = Color.FromArgb(70, 70, 70);

        // saves a picture of the window for the user (Desktop\AB Bridges screens)
        public static void Snap(Control c, string name)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "AB Bridges screens");
                Directory.CreateDirectory(dir);
                using (var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height)))
                {
                    c.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch { }
        }
    }

    // logo, window icon and the header bar (logo + "AB Bridges" + page, "Created by ..." on the right)
    internal static class Brand
    {
        public const string Author = "Abdein Badawi";
        public const string Email = "Abdeenth88@gmail.com";
        public const string Version = "2025.11";

        private static Stream Res(string file)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(file, StringComparison.OrdinalIgnoreCase));
            return name == null ? null : asm.GetManifestResourceStream(name);
        }

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
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, size, size);
                        }
                        return bmp;
                    }
                }
            }
            catch { return null; }
        }

        public static void SetIcon(Form f)
        {
            try { using (Stream s = Res("AB.ico")) if (s != null) { f.Icon = new Icon(s); f.ShowIcon = true; } }
            catch { }
        }
    }

    internal sealed class BrandHeader : Panel
    {
        public BrandHeader(string page)
        {
            Dock = DockStyle.Top;
            Height = 46;
            BackColor = Theme.Navy;
            var pic = new PictureBox { Image = Brand.Logo(30), Size = new Size(30, 30), Location = new Point(10, 8), SizeMode = PictureBoxSizeMode.Zoom };
            var title = new Label { Text = "AB Bridges", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 12f), AutoSize = true, Location = new Point(48, 11) };
            var sub = new Label { Text = page, ForeColor = Color.FromArgb(175, 192, 214), Font = new Font("Segoe UI", 9f), AutoSize = true, Location = new Point(150, 16) };
            var by = new Label { Text = "Created by " + Brand.Author, ForeColor = Color.FromArgb(143, 163, 189), Font = new Font("Segoe UI", 8.5f), AutoSize = true };
            Controls.AddRange(new Control[] { pic, title, sub, by });
            Layout += (s, e) =>
            {
                sub.Left = title.Right + 6;
                by.Location = new Point(Width - by.PreferredWidth - 12, (Height - by.PreferredHeight) / 2);
            };
        }
    }

    // numbered page buttons: active = accent, others grey
    internal sealed class NavBar : Panel
    {
        private readonly List<Button> _b = new List<Button>();
        public event Action<int> PageClicked;

        public NavBar(params string[] pages)
        {
            Dock = DockStyle.Top;
            Height = 46;
            BackColor = Color.White;
            Padding = new Padding(8, 7, 8, 7);
            for (int i = 0; i < pages.Length; i++)
            {
                int k = i;
                var b = new Button
                {
                    Text = (i + 1) + "  " + pages[i],
                    FlatStyle = FlatStyle.Flat,
                    Font = Theme.Bold,
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                b.FlatAppearance.BorderSize = 0;
                b.Click += (s, e) => PageClicked?.Invoke(k);
                _b.Add(b);
                Controls.Add(b);
            }
            Resize += (s, e) => Lay();
            Lay();
        }

        private void Lay()
        {
            int n = _b.Count, gap = 6, w = Math.Max(60, (ClientSize.Width - Padding.Horizontal - gap * (n - 1)) / Math.Max(1, n));
            for (int i = 0; i < n; i++) _b[i].SetBounds(Padding.Left + i * (w + gap), Padding.Top, w, Height - Padding.Vertical);
        }

        public void SetActive(int index)
        {
            for (int i = 0; i < _b.Count; i++)
            {
                bool on = i == index;
                _b[i].BackColor = on ? Theme.Accent : Theme.Idle;
                _b[i].ForeColor = on ? Color.White : Theme.IdleText;
            }
        }
    }

    // a drawing in world units (m); fitted to the control, labels moved so they never overlap
    internal sealed class PlanView : Control
    {
        private sealed class PLine { public PointF[] P; public Color C; public float W; public bool Dash; public bool Fill; }
        private sealed class PText { public PointF P; public string T; public Color C; public float Size; public float Dx, Dy; public bool Box; }
        private sealed class PDim { public PointF A, B; public string T; public float Off; public string Sub; }

        private readonly List<PLine> _lines = new List<PLine>();
        private readonly List<PText> _texts = new List<PText>();
        private readonly List<PDim> _dims = new List<PDim>();
        public string Title = "";
        public string Empty = "Pick the objects to see the plan";
        public float ScaleY = 1f;          // >1 exaggerates across (dims stay true)

        public PlanView()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            ResizeRedraw = true;
        }

        public void Clear() { _lines.Clear(); _texts.Clear(); _dims.Clear(); }
        public void Line(IEnumerable<PointF> pts, Color c, float w = 1f, bool dash = false) =>
            _lines.Add(new PLine { P = pts.ToArray(), C = c, W = w, Dash = dash });
        public void Poly(IEnumerable<PointF> pts, Color c) => _lines.Add(new PLine { P = pts.ToArray(), C = c, Fill = true });
        public void Text(PointF p, string t, Color c, float size = 8f, float dx = 0, float dy = 0, bool box = false) =>
            _texts.Add(new PText { P = p, T = t, C = c, Size = size, Dx = dx, Dy = dy, Box = box });
        // horizontal dimension between two x positions, drawn 'off' pixels above the top of the drawing
        public void Dim(float x1, float x2, string t, float off, string sub = null) =>
            _dims.Add(new PDim { A = new PointF(x1, 0), B = new PointF(x2, 0), T = t, Off = off, Sub = sub });

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var tf = new Font("Segoe UI Semibold", 9f)) g.DrawString(Title, tf, Brushes.DimGray, 6, 4);
            var all = _lines.SelectMany(l => l.P).ToList();
            if (all.Count == 0)
            {
                using (var f = new Font("Segoe UI", 10f))
                    TextRenderer.DrawText(g, Empty, f, ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            float x0 = all.Min(p => p.X), x1 = all.Max(p => p.X), y0 = all.Min(p => p.Y), y1 = all.Max(p => p.Y);
            float topRoom = _dims.Count == 0 ? 26 : 26 + _dims.Max(d => d.Off) + 18;
            float bottomRoom = _texts.Count == 0 ? 14 : 14 + Math.Max(0, _texts.Max(t => t.Dy)) + 12;
            var area = new RectangleF(22, topRoom, Math.Max(10, Width - 44), Math.Max(10, Height - topRoom - bottomRoom));
            float sx = area.Width / Math.Max(1e-3f, x1 - x0), sy = area.Height / Math.Max(1e-3f, (y1 - y0) * ScaleY);
            float s = Math.Min(sx, sy);
            float ox = area.Left + (area.Width - (x1 - x0) * s) / 2, oy = area.Top + (area.Height - (y1 - y0) * ScaleY * s) / 2;
            Func<PointF, PointF> T = p => new PointF(ox + (p.X - x0) * s, oy + (y1 - p.Y) * ScaleY * s);

            foreach (PLine l in _lines.Where(l => l.Fill))
                using (var br = new SolidBrush(l.C)) g.FillPolygon(br, l.P.Select(T).ToArray());
            foreach (PLine l in _lines.Where(l => !l.Fill && l.P.Length > 1))
                using (var pen = new Pen(l.C, l.W))
                {
                    if (l.Dash) pen.DashPattern = new[] { 5f, 3f };
                    g.DrawLines(pen, l.P.Select(T).ToArray());
                }

            var used = new List<RectangleF>();
            float top = oy;
            using (var pen = new Pen(Theme.Dim, 0.8f))
            using (var f = new Font("Segoe UI", 8f))
            using (var fs = new Font("Segoe UI", 7f))
            {
                pen.CustomStartCap = pen.CustomEndCap = new AdjustableArrowCap(3, 4);
                foreach (PDim d in _dims)
                {
                    float xa = T(d.A).X, xb = T(d.B).X, y = top - d.Off;
                    if (xb - xa < 4) continue;
                    g.DrawLine(pen, xa, y, xb, y);
                    using (var tick = new Pen(Color.Silver, 0.6f)) { g.DrawLine(tick, xa, y - 4, xa, top); g.DrawLine(tick, xb, y - 4, xb, top); }
                    SizeF sz = g.MeasureString(d.T, f);
                    if (sz.Width < xb - xa + 6)
                    {
                        var r = new RectangleF((xa + xb - sz.Width) / 2, y - sz.Height, sz.Width, sz.Height);
                        g.DrawString(d.T, f, Brushes.Black, r.Location); used.Add(r);
                    }
                    if (d.Sub != null)
                    {
                        SizeF s2 = g.MeasureString(d.Sub, fs);
                        if (s2.Width < xb - xa) g.DrawString(d.Sub, fs, Brushes.Gray, (xa + xb - s2.Width) / 2, y + 1);
                    }
                }
            }

            foreach (PText t in _texts)
                using (var f = new Font("Segoe UI", t.Size))
                {
                    PointF p = T(t.P);
                    SizeF sz = g.MeasureString(t.T, f);
                    var r = new RectangleF(p.X + t.Dx - sz.Width / 2, p.Y + t.Dy, sz.Width, sz.Height);
                    for (int k = 0; k < 4 && used.Any(u => u.IntersectsWith(r)); k++) r.Y += sz.Height;   // never on top of another label
                    if (used.Any(u => u.IntersectsWith(r))) continue;
                    if (t.Box) using (var bb = new SolidBrush(Color.FromArgb(235, Color.White))) g.FillRectangle(bb, r);
                    using (var br = new SolidBrush(t.C)) g.DrawString(t.T, f, br, r.Location);
                    used.Add(r);
                }
        }
    }
}
