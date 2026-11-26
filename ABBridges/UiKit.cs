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
        // screenshots for checking the UI: only when the folder "AB Bridges screens" exists on the desktop
        // (it wrote a picture at every page change and slowed the dialog down)
        public static void Snap(Control c, string name)
        {
            try
            {
                if (!Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "AB Bridges screens"))) return;
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

        internal static Stream Res(string file)
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

        public static byte[] LogoBytes()
        {
            try { using (Stream s = Res("AB_logo_256.png")) { if (s == null) return null; var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); } }
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

    // page chip inside the navy header (6D): outlined, the open one white with an orange number
    internal sealed class Chip : Control
    {
        public string Number = "", Label = "";
        public bool Active;
        private static readonly Color Line = Color.FromArgb(79, 106, 140), Soft = Color.FromArgb(175, 192, 214);

        public Chip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 9.5f);
            Height = 30;
        }

        public int Preferred()
        {
            int w1 = Number.Length == 0 ? 0 : TextRenderer.MeasureText(Number, Font).Width;
            return w1 + TextRenderer.MeasureText(Label, Font).Width + 20;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Navy);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = RoundRect(r, 5))
            {
                if (Active) using (var b = new SolidBrush(Color.White)) g.FillPath(b, p);
                using (var pen = new Pen(Active ? Color.White : Line)) g.DrawPath(pen, p);
            }
            int x = 9;
            if (Number.Length > 0)
            {
                Size n = TextRenderer.MeasureText(Number, Font);
                TextRenderer.DrawText(g, Number, Font, new Rectangle(x, 0, n.Width, Height), Active ? Theme.Accent : Soft, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += n.Width + 2;
            }
            TextRenderer.DrawText(g, Label, Font, new Rectangle(x, 0, Width - x, Height), Active ? Theme.Navy : Soft, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        internal static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // rounded flat button in the look of the page chips: Normal = white + navy line, Primary = navy, Accent = orange, Danger = red line
    internal enum Look { Normal, Primary, Accent, Danger }

    internal sealed class NiceButton : Button
    {
        private Look _look;
        private bool _hot, _down;
        public Look Look { get => _look; set { _look = value; Invalidate(); } }

        public NiceButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI Semibold", 9f);
            Cursor = Cursors.Hand;
            Height = 28;
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Color.White);
            Color fill, line, text;
            switch (_look)
            {
                case Look.Primary: fill = Theme.Navy; line = Theme.Navy; text = Color.White; break;
                case Look.Accent: fill = Theme.Accent; line = Theme.Accent; text = Color.White; break;
                case Look.Danger: fill = Color.White; line = Color.FromArgb(200, 70, 60); text = Color.FromArgb(190, 50, 40); break;
                default: fill = Color.White; line = Color.FromArgb(120, 142, 170); text = Theme.Navy; break;
            }
            if (!Enabled) { fill = Color.FromArgb(238, 240, 243); line = Color.FromArgb(210, 214, 220); text = Color.FromArgb(160, 166, 175); }
            else if (_down) fill = Mix(fill, Theme.Navy, _look == Look.Normal || _look == Look.Danger ? 0.18f : 0.25f);
            else if (_hot) fill = _look == Look.Normal || _look == Look.Danger ? Color.FromArgb(234, 240, 248) : Mix(fill, Color.White, 0.15f);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Chip.RoundRect(r, 5))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                using (var pen = new Pen(line)) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, Text, Font, r, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Color.FromArgb(120, Theme.Accent)) { DashStyle = DashStyle.Dot }) g.DrawRectangle(pen, 3, 3, Width - 7, Height - 7);
        }

        private static Color Mix(Color a, Color b, float t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    // navy header with the logo, the numbered page chips and "Created by ..." (pages live in the header to save room)
    internal sealed class NavBar : Panel
    {
        private readonly List<Chip> _b = new List<Chip>();
        private readonly Label _by;
        private readonly Label _title;
        public event Action<int> PageClicked;

        public NavBar(params string[] pages)
        {
            Dock = DockStyle.Top;
            Height = 48;
            BackColor = Theme.Navy;
            Controls.Add(new PictureBox { Image = Brand.Logo(28), Size = new Size(28, 28), Location = new Point(10, 10), SizeMode = PictureBoxSizeMode.Zoom });
            _title = new Label { Text = "AB Bridges", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 12f), AutoSize = true, Location = new Point(44, 13) };
            Controls.Add(_title);
            for (int i = 0; i < pages.Length; i++)
            {
                int k = i;
                var c = new Chip { Number = (i + 1).ToString(), Label = pages[i] };
                c.Click += (s, e) => PageClicked?.Invoke(k);
                _b.Add(c);
                Controls.Add(c);
            }
            _by = new Label { Text = "Created by " + Brand.Author, ForeColor = Color.FromArgb(143, 163, 189), Font = new Font("Segoe UI", 8.5f), AutoSize = true };
            Controls.Add(_by);
            Resize += (s, e) => Lay();
            Lay();
        }

        private void Lay()
        {
            int x = _title.Right + 18;
            foreach (Chip c in _b) { c.SetBounds(x, 9, c.Preferred(), 30); x += c.Width + 6; }
            _by.Location = new Point(Width - _by.PreferredWidth - 12, (Height - _by.PreferredHeight) / 2);
            _by.Visible = _by.Left > x + 10;            // hidden when the window is too narrow
        }

        public void SetActive(int index)
        {
            for (int i = 0; i < _b.Count; i++) { _b[i].Active = i == index; _b[i].Invalidate(); }
        }
    }

    // Roads Below: the bridge cut along its length (x = station, y = level, levels x3): deck (design surface - depth),
    // the roads under it with pavement + New Jersey barriers, FRL, the min vertical clearance, the NGL in sand, the axes
    internal sealed class RoadsView : Control
    {
        public List<(string name, double sta)> Axes = new List<(string, double)>();
        public List<(double sta, double z)> DeckTop = new List<(double, double)>();
        public List<(double sta, double z)> Ngl = new List<(double, double)>();
        public List<(string name, double depth, List<(double sta, double z)> pts, List<(double sta, double bz)> bridge)> Roads =
            new List<(string, double, List<(double, double)>, List<(double, double)>)>();
        public double DeckDepth = double.NaN;        // where no road is under the deck
        public string Message = "No roads under the bridge";
        public float Exaggerate = 3f;
        private static readonly (double x, double z)[] Barrier = { (0, 0), (0, 0.08), (-0.12, 0.33), (-0.20, 1.20), (-0.40, 1.20), (-0.40, 0) };
        private const double PaveT = 0.25;

        public RoadsView() { DoubleBuffered = true; BackColor = Color.White; ResizeRedraw = true; }

        private double DepthAt(double sta)
        {
            foreach (var r in Roads)
                if (r.pts.Count > 0 && sta >= r.pts.Min(p => p.sta) - 1e-6 && sta <= r.pts.Max(p => p.sta) + 1e-6 && r.depth > 0) return r.depth;
            return double.IsNaN(DeckDepth) || DeckDepth <= 0 ? 0.3 : DeckDepth;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Axes.Count < 2 || DeckTop.Count < 2)
            {
                TextRenderer.DrawText(g, Message, Font, ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            double x0 = Axes.Min(a => a.sta) - 5, x1 = Axes.Max(a => a.sta) + 5;
            var zs = DeckTop.Select(p => p.z).Concat(Ngl.Select(p => p.z)).Concat(Roads.SelectMany(r => r.pts.Select(p => p.z - PaveT))).ToList();
            double z0 = zs.Min() - 1, z1 = zs.Max() + 1;
            const float padT = 66, padB = 26, padL = 14, padR = 14;
            double sx = (Width - padL - padR) / Math.Max(1e-6, x1 - x0);
            double sz = Math.Min(sx * Exaggerate, (Height - padT - padB) / Math.Max(1e-6, z1 - z0));
            Func<double, double, PointF> P = (x, z) => new PointF((float)(padL + (x - x0) * sx), (float)(Height - padB - (z - z0) * sz));
            g.SetClip(new RectangleF(padL, 0, Width - padL - padR, Height));

            using (var f = new Font("Arial", 8f))
            using (var fb = new Font("Arial", 8f, FontStyle.Bold))
            using (var ink = new Pen(Color.Black, 0.9f))
            using (var conc = new HatchBrush(HatchStyle.DottedGrid, Color.FromArgb(150, 150, 150), Color.FromArgb(237, 237, 237)))
            using (var deckB = new HatchBrush(HatchStyle.Percent10, Color.FromArgb(140, 140, 140), Color.FromArgb(242, 242, 242)))
            {
                // NGL: sand under the ground line
                var ng = Ngl.Where(p => p.sta >= x0 && p.sta <= x1).OrderBy(p => p.sta).ToList();
                if (ng.Count > 1)
                {
                    var gp = ng.Select(p => P(p.sta, p.z)).ToList();
                    var poly = new List<PointF>(gp) { new PointF(gp.Last().X, Height), new PointF(gp[0].X, Height) };
                    using (var b = new SolidBrush(Color.FromArgb(90, 239, 159, 39))) g.FillPolygon(b, poly.ToArray());
                    using (var pen = new Pen(Color.FromArgb(133, 79, 11), 1.1f)) g.DrawLines(pen, gp.ToArray());
                    PointF fg = gp[gp.Count / 5];
                    g.DrawString("NGL", f, Brushes.Black, fg.X - 10, fg.Y - 22);
                    g.FillPolygon(Brushes.Black, new[] { new PointF(fg.X - 4, fg.Y - 8), new PointF(fg.X + 4, fg.Y - 8), new PointF(fg.X, fg.Y - 1) });
                }
                // deck: design level to design - depth
                var dk = DeckTop.Where(p => p.sta >= x0 - 1 && p.sta <= x1 + 1).OrderBy(p => p.sta).ToList();
                if (dk.Count > 1)
                {
                    var outline = dk.Select(p => P(p.sta, p.z)).ToList();
                    outline.AddRange(dk.AsEnumerable().Reverse().Select(p => P(p.sta, p.z - DepthAt(p.sta))));
                    g.FillPolygon(deckB, outline.ToArray());
                    g.DrawPolygon(ink, outline.ToArray());
                }
                // roads: pavement + barriers, FRL, min vertical clearance
                foreach (var rd in Roads.Where(r => r.pts.Count > 1))
                {
                    var rp = rd.pts.OrderBy(p => p.sta).ToList();
                    var topL = rp.Select(p => P(p.sta, p.z)).ToList();
                    var pave = new List<PointF>(topL);
                    pave.AddRange(rp.AsEnumerable().Reverse().Select(p => P(p.sta, p.z - PaveT)));
                    using (var asph = new HatchBrush(HatchStyle.Percent50, Color.FromArgb(70, 70, 70), Color.FromArgb(110, 110, 110))) g.FillPolygon(asph, pave.ToArray());
                    g.DrawPolygon(ink, pave.ToArray());
                    foreach (var (px, pz, side) in new[] { (rp[0].sta, rp[0].z, 1.0), (rp[rp.Count - 1].sta, rp[rp.Count - 1].z, -1.0) })
                    {
                        PointF[] bp = Barrier.Select(q => P(px + side * q.x, pz - PaveT + q.z)).ToArray();
                        g.FillPolygon(conc, bp);
                        g.DrawPolygon(ink, bp);
                    }
                    PointF s0 = P(rp[rp.Count / 2].sta, rp[rp.Count / 2].z);
                    g.FillPolygon(Brushes.Black, new[] { new PointF(s0.X - 6, s0.Y - 9), new PointF(s0.X + 6, s0.Y - 9), new PointF(s0.X, s0.Y - 1) });
                    g.DrawString("FRL  " + rd.name, fb, Brushes.Black, s0.X - 10, s0.Y - 22);
                    double best = double.MaxValue, bx = double.NaN, bz = double.NaN, bs = double.NaN;
                    foreach (var p in rp)
                    {
                        var bq = rd.bridge.OrderBy(q => Math.Abs(q.sta - p.sta)).FirstOrDefault();
                        if (rd.bridge.Count == 0 || double.IsNaN(bq.bz)) continue;
                        double soffit = bq.bz - rd.depth;
                        if (soffit - p.z < best) { best = soffit - p.z; bx = p.sta; bz = p.z; bs = soffit; }
                    }
                    if (!double.IsNaN(bx))
                    {
                        PointF a = P(bx, bz), b = P(bx, bs);
                        using (var pen = new Pen(Color.FromArgb(190, 30, 30), 1f) { CustomStartCap = new AdjustableArrowCap(3, 4), CustomEndCap = new AdjustableArrowCap(3, 4) })
                            g.DrawLine(pen, a, b);
                        string t = (rd.depth > 0 ? best.ToString("0.000") : "type the depth") + "  MIN VERTICAL CLEARANCE";
                        using (var br = new SolidBrush(Color.FromArgb(190, 30, 30))) g.DrawString(t, fb, br, a.X + 5, (a.Y + b.Y) / 2 - 6);
                    }
                }
                // axes: bubble + dash-dot line, one span dim each (length + SPAN n)
                g.ResetClip();
                var ax = Axes.OrderBy(a => a.sta).ToList();
                using (var dash = new Pen(Color.Gray, 0.6f) { DashPattern = new[] { 8f, 3f, 2f, 3f } })
                using (var dim = new Pen(Color.Black, 0.7f) { CustomStartCap = new AdjustableArrowCap(3, 4), CustomEndCap = new AdjustableArrowCap(3, 4) })
                    for (int i = 0; i < ax.Count; i++)
                    {
                        float x = P(ax[i].sta, z0).X;
                        g.DrawLine(dash, x, 26, x, Height - padB);
                        var rc = new RectangleF(x - 11, 3, 22, 22);
                        g.FillEllipse(Brushes.White, rc); g.DrawEllipse(ink, rc);
                        TextRenderer.DrawText(g, ax[i].name, f, Rectangle.Round(rc), Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                        if (i + 1 < ax.Count)
                        {
                            float xn = P(ax[i + 1].sta, z0).X;
                            g.DrawLine(dim, x, 44, xn, 44);
                            string t = (ax[i + 1].sta - ax[i].sta).ToString("0.000");
                            SizeF ts = g.MeasureString(t, fb);
                            if (ts.Width < xn - x) g.DrawString(t, fb, Brushes.Black, (x + xn - ts.Width) / 2, 30);
                            string sp = "SPAN " + (i + 1);
                            SizeF s2 = g.MeasureString(sp, f);
                            if (s2.Width < xn - x) g.DrawString(sp, f, Brushes.Black, (x + xn - s2.Width) / 2, 46);
                        }
                    }
            }
        }
    }

    // a drawing in world units (m); fitted to the control, labels moved so they never overlap
    internal sealed class PlanView : Control
    {
        private sealed class PLine { public PointF[] P; public Color C; public float W; public bool Dash; public bool Fill; public bool NoFit; }
        private sealed class PText { public PointF P; public string T; public Color C; public float Size; public float Dx, Dy; public bool Box; }
        private sealed class PDim { public PointF A, B; public string T; public float Off; public string Sub; public Color? C; }

        // mouse on the drawing (Sections page): click = add a section at x, Shift + click = delete the one near x
        public bool Interactive;
        public event Action<float, bool> WorldClick;                 // x (world), shift
        public Func<float, float?> DeletableNear;                  // x of the section Shift + click would delete (null = none)
        private bool _frame;
        private float _ox, _kx, _x0, _oy, _ky, _y1;
        private Point? _mouse;
        private bool _shift;
        public float ToWorldX(int px) => _x0 + (px - _ox) / Math.Max(1e-6f, _kx);
        private float ToScreenX(float x) => _ox + (x - _x0) * _kx;

        private readonly List<PLine> _lines = new List<PLine>();
        private readonly List<PText> _texts = new List<PText>();
        private readonly List<PDim> _dims = new List<PDim>();
        public string Title = "";
        public string Empty = "Pick the objects to see the plan";
        public float ScaleY = 1f;          // >1 exaggerates across (dims stay true)
        public bool FitWidth;              // the drawing always fills the width (across scaled on its own)
        public float? ClipX0, ClipX1;      // only this x range is shown (e.g. first axis - 5 m .. last axis + 5 m)

        public PlanView()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            ResizeRedraw = true;
        }

        public void Clear() { _lines.Clear(); _texts.Clear(); _dims.Clear(); }
        public void Line(IEnumerable<PointF> pts, Color c, float w = 1f, bool dash = false, bool fit = true) =>
            _lines.Add(new PLine { P = pts.ToArray(), C = c, W = w, Dash = dash, NoFit = !fit });
        public void Poly(IEnumerable<PointF> pts, Color c) => _lines.Add(new PLine { P = pts.ToArray(), C = c, Fill = true });
        public void Text(PointF p, string t, Color c, float size = 8f, float dx = 0, float dy = 0, bool box = false) =>
            _texts.Add(new PText { P = p, T = t, C = c, Size = size, Dx = dx, Dy = dy, Box = box });
        // horizontal dimension between two x positions, drawn 'off' pixels above the top of the drawing
        public void Dim(float x1, float x2, string t, float off, string sub = null, Color? color = null) =>
            _dims.Add(new PDim { A = new PointF(x1, 0), B = new PointF(x2, 0), T = t, Off = off, Sub = sub, C = color });

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Interactive) return;
            _mouse = e.Location; _shift = (ModifierKeys & Keys.Shift) != 0;
            Cursor = Cursors.Cross;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _mouse = null; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!Interactive || !_frame || e.Button != MouseButtons.Left) return;
            WorldClick?.Invoke(ToWorldX(e.X), (ModifierKeys & Keys.Shift) != 0);
        }

        // the hover line: where a click adds a section (navy), or the section Shift + click deletes (red)
        private void PaintHover(Graphics g)
        {
            if (!Interactive || !_frame || _mouse == null) return;
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            float wx = ToWorldX(_mouse.Value.X);
            float yTop = _oy - 4, yBot = Height - 18;
            using (var f = new Font("Segoe UI", 8f))
            {
                if (shift)
                {
                    float? x = DeletableNear?.Invoke(wx);
                    if (x == null) { TextRenderer.DrawText(g, "no section to take away here", f, new Point(_mouse.Value.X + 8, _mouse.Value.Y - 18), Color.Firebrick); return; }
                    float sx = ToScreenX(x.Value);
                    using (var pen = new Pen(Color.FromArgb(220, 40, 30), 2.4f)) g.DrawLine(pen, sx, yTop, sx, yBot);
                    TextRenderer.DrawText(g, "Shift + click: one part less here (the sections are spread again)", f, new Point((int)sx + 6, _mouse.Value.Y - 18), Color.FromArgb(200, 30, 20));
                }
                else
                {
                    float sx = _mouse.Value.X;
                    using (var pen = new Pen(Color.FromArgb(160, Theme.Navy), 1.4f) { DashPattern = new[] { 4f, 3f } }) g.DrawLine(pen, sx, yTop, sx, yBot);
                    TextRenderer.DrawText(g, "click: one more part here (the sections are spread again)", f, new Point((int)sx + 6, _mouse.Value.Y - 18), Theme.Navy);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var tf = new Font("Segoe UI Semibold", 9f)) g.DrawString(Title, tf, Brushes.DimGray, 6, 4);
            var all = _lines.Where(l => !l.NoFit).SelectMany(l => l.P).ToList();
            if (all.Count == 0) all = _lines.SelectMany(l => l.P).ToList();
            _frame = false;
            if (all.Count == 0)
            {
                using (var f = new Font("Segoe UI", 10f))
                    TextRenderer.DrawText(g, Empty, f, ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            if (ClipX0.HasValue && ClipX1.HasValue)
            {
                float c0 = ClipX0.Value, c1 = ClipX1.Value;
                var inside = all.Where(p => p.X >= c0 && p.X <= c1).ToList();
                // the parts of the lines that cross the range also count (a long straight edge has few points)
                foreach (PLine l in _lines.Where(l => !l.NoFit))
                    for (int i = 0; i + 1 < l.P.Length; i++)
                        foreach (float cx in new[] { c0, c1 })
                        {
                            PointF a = l.P[i], b = l.P[i + 1];
                            if ((a.X - cx) * (b.X - cx) < 0) inside.Add(new PointF(cx, a.Y + (b.Y - a.Y) * (cx - a.X) / (b.X - a.X)));
                        }
                if (inside.Count > 0) { all = inside; all.Add(new PointF(c0, inside[0].Y)); all.Add(new PointF(c1, inside[0].Y)); }
            }
            float x0 = all.Min(p => p.X), x1 = all.Max(p => p.X), y0 = all.Min(p => p.Y), y1 = all.Max(p => p.Y);
            float topRoom = _dims.Count == 0 ? 26 : 26 + _dims.Max(d => d.Off) + 18;
            float bottomRoom = _texts.Count == 0 ? 14 : 14 + Math.Max(0, _texts.Max(t => t.Dy)) + 12;
            var area = new RectangleF(22, topRoom, Math.Max(10, Width - 44), Math.Max(10, Height - topRoom - bottomRoom));
            float sx = area.Width / Math.Max(1e-3f, x1 - x0), sy = area.Height / Math.Max(1e-3f, (y1 - y0) * ScaleY);
            float s = Math.Min(sx, sy);
            float kx = s, ky = ScaleY * s;
            if (FitWidth) { kx = sx; ky = Math.Min(area.Height / Math.Max(1e-3f, y1 - y0), sx * 8f); }
            float ox = area.Left + (area.Width - (x1 - x0) * kx) / 2, oy = area.Top + (area.Height - (y1 - y0) * ky) / 2;
            Func<PointF, PointF> T = p => new PointF(ox + (p.X - x0) * kx, oy + (y1 - p.Y) * ky);
            _frame = true; _ox = ox; _kx = kx; _x0 = x0; _oy = oy; _ky = ky; _y1 = y1;
            if (ClipX0.HasValue && ClipX1.HasValue)
            {
                float l = T(new PointF(ClipX0.Value, 0)).X, r = T(new PointF(ClipX1.Value, 0)).X;
                g.SetClip(new RectangleF(l, 0, r - l, Height));
            }

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
                // per row (same Off): a value that would touch the one before it goes one line up (two levels), never on top
                var rowTexts = new Dictionary<float, List<RectangleF>>();
                foreach (PDim d in _dims.OrderBy(d => d.Off).ThenBy(d => Math.Min(d.A.X, d.B.X)))
                {
                    float xa = T(d.A).X, xb = T(d.B).X, y = top - d.Off;
                    if (xb - xa < 4) continue;
                    g.DrawLine(pen, xa, y, xb, y);
                    using (var tick = new Pen(Color.Silver, 0.6f)) { g.DrawLine(tick, xa, y - 4, xa, top); g.DrawLine(tick, xb, y - 4, xb, top); }
                    SizeF sz = g.MeasureString(d.T, f);
                    if (!rowTexts.TryGetValue(d.Off, out var placed)) { placed = new List<RectangleF>(); rowTexts[d.Off] = placed; }
                    for (int lvl = 0; lvl < 2; lvl++)
                    {
                        var r = new RectangleF((xa + xb - sz.Width) / 2, y - sz.Height - lvl * (sz.Height - 1), sz.Width, sz.Height);
                        var pad = RectangleF.Inflate(r, 3, 0);
                        if (placed.Any(p => p.IntersectsWith(pad)) || used.Any(p => p.IntersectsWith(pad))) continue;
                        if (lvl > 0) using (var lead = new Pen(Color.Silver, 0.6f)) g.DrawLine(lead, (xa + xb) / 2, y - 1, (xa + xb) / 2, r.Bottom);
                        using (var br = new SolidBrush(d.C ?? Color.Black)) g.DrawString(d.T, f, br, r.Location);
                        placed.Add(r); used.Add(r);
                        break;
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
            g.ResetClip();
            PaintHover(g);
        }
    }
}
