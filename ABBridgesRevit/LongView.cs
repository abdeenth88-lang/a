// AB Bridges - longitudinal view of the bridge (x = station, y = level), drawn from the real Revit cut
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace ABBridgesRevit
{
    internal sealed class LongData
    {
        public List<(bool deck, List<(double x, double z)> loop)> Loops = new List<(bool, List<(double, double)>)>();
        public List<(string name, double x, string type)> Axes = new List<(string, double, string)>();
        public List<(double x, double z)> Ground = new List<(double, double)>();
        public List<(string road, List<(double x, double z)> pts)> Roads = new List<(string, List<(double, double)>)>();
    }

    internal static class UiTheme
    {
        public static readonly Color Navy = Color.FromArgb(31, 58, 95);
        public static readonly Color Accent = Color.FromArgb(232, 121, 43);
        public static readonly Color Idle = Color.FromArgb(232, 234, 238);
        public static readonly Color IdleText = Color.FromArgb(120, 126, 136);

        // numbered page/axis tabs: active = accent, others grey
        public static void StyleTabs(TabControl tc)
        {
            tc.DrawMode = TabDrawMode.OwnerDrawFixed;
            tc.SizeMode = TabSizeMode.Fixed;
            tc.ItemSize = new Size(96, 28);
            tc.DrawItem += (s, e) =>
            {
                // chips (6D): the open axis navy with an orange number, the others outlined
                bool on = e.Index == tc.SelectedIndex;
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = tc.GetTabRect(e.Index);
                using (var bg = new SolidBrush(tc.Parent?.BackColor ?? Color.White)) g.FillRectangle(bg, r);
                r.Inflate(-2, -2);
                using (var p = new GraphicsPath())
                {
                    int d = 8;
                    p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
                    using (var b = new SolidBrush(on ? Navy : Color.White)) g.FillPath(b, p);
                    using (var pen = new Pen(on ? Navy : Color.FromArgb(207, 213, 221))) g.DrawPath(pen, p);
                }
                using (var f = new Font("Segoe UI Semibold", 9f))
                {
                    string num = (e.Index + 1).ToString(), txt = tc.TabPages[e.Index].Text;
                    int wn = TextRenderer.MeasureText(num, f).Width, wt = TextRenderer.MeasureText(txt, f).Width;
                    int x = r.X + Math.Max(6, (r.Width - wn - wt - 4) / 2);
                    TextRenderer.DrawText(g, num, f, new Rectangle(x, r.Y, wn, r.Height), on ? Accent : IdleText, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, txt, f, new Rectangle(x + wn + 4, r.Y, r.Right - x - wn - 4, r.Height), on ? Color.White : IdleText,
                                          TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                }
            };
        }

        public static void Snap(Control c, string name)
        {
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "AB Bridges screens");
                System.IO.Directory.CreateDirectory(dir);
                using (var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height)))
                {
                    c.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(System.IO.Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch { }
        }
    }

    internal sealed class LongView : Control
    {
        public LongData Data;
        public string Message = "Choose the bridge Excel to see the bridge";
        public float Exaggerate = 3f;                 // levels drawn this many times taller (dims stay true)
        public event Action<string> AxisClicked;
        private readonly List<(RectangleF r, string name)> _hits = new List<(RectangleF, string)>();

        private static readonly Color Sand = Color.FromArgb(90, 239, 159, 39);
        private static readonly Color Concrete = Color.FromArgb(211, 209, 199);
        private static readonly Color Deck = Color.FromArgb(200, 214, 232);
        private static readonly Color Ink = Color.FromArgb(68, 68, 65);

        public LongView()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            ResizeRedraw = true;
            MouseClick += (s, e) =>
            {
                foreach (var h in _hits) if (h.r.Contains(e.Location)) { AxisClicked?.Invoke(h.name); return; }
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            _hits.Clear();
            LongData d = Data;
            var pts = d == null ? new List<(double x, double z)>() : d.Loops.SelectMany(l => l.loop).Concat(d.Ground).ToList();
            if (d == null || pts.Count == 0 || d.Axes.Count == 0)
            {
                TextRenderer.DrawText(g, Message, Font, ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            double x0 = Math.Min(pts.Min(p => p.x), d.Axes.Min(a => a.x)), x1 = Math.Max(pts.Max(p => p.x), d.Axes.Max(a => a.x));
            double z0 = pts.Min(p => p.z), z1 = pts.Max(p => p.z);
            float top = 64, bottom = 30, left = 14, right = 14;
            double sx = (Width - left - right) / Math.Max(1e-6, x1 - x0);
            double sz = Math.Min(sx * Exaggerate, (Height - top - bottom) / Math.Max(1e-6, z1 - z0));
            Func<double, double, PointF> P = (x, z) => new PointF((float)(left + (x - x0) * sx), (float)(Height - bottom - (z - z0) * sz));
            float yGround = Height - bottom;

            using (var f = new Font("Segoe UI", 8f))
            using (var fb = new Font("Segoe UI Semibold", 8f))
            using (var ink = new Pen(Ink, 0.7f))
            {
                // ground: sand under the FGL line
                if (d.Ground.Count > 1)
                {
                    var gp = d.Ground.OrderBy(p => p.x).Select(p => P(p.x, p.z)).ToList();
                    var poly = new List<PointF>(gp) { new PointF(gp.Last().X, Height), new PointF(gp[0].X, Height) };
                    using (var b = new SolidBrush(Sand)) g.FillPolygon(b, poly.ToArray());
                    using (var pen = new Pen(Color.FromArgb(133, 79, 11), 1.1f)) g.DrawLines(pen, gp.ToArray());
                    g.DrawString("FGL", f, Brushes.SaddleBrown, gp[gp.Count / 3].X, gp[gp.Count / 3].Y - 14);
                }
                // the cut elements (deck light blue, substructure concrete)
                foreach (var l in d.Loops.Where(x => x.loop.Count > 2))
                {
                    PointF[] pp = l.loop.Select(q => P(q.x, q.z)).ToArray();
                    using (var b = new SolidBrush(l.deck ? Deck : Concrete)) g.FillPolygon(b, pp);
                    g.DrawPolygon(ink, pp);
                }
                // roads below: FRL line + symbol + min vertical clearance under the deck
                foreach (var rd in d.Roads.Where(r => r.pts.Count > 0))
                {
                    var rp = rd.pts.OrderBy(p => p.x).ToList();
                    if (rp.Count > 1) using (var pen = new Pen(Color.Black, 1.6f)) g.DrawLines(pen, rp.Select(p => P(p.x, p.z)).ToArray());
                    double best = double.MaxValue, bx = double.NaN, bz = double.NaN, bs = double.NaN;
                    foreach (var p in rp)
                    {
                        double soffit = Soffit(d, p.x);
                        if (double.IsNaN(soffit)) continue;
                        if (soffit - p.z < best) { best = soffit - p.z; bx = p.x; bz = p.z; bs = soffit; }
                    }
                    PointF s0 = P(rp[rp.Count / 2].x, rp[rp.Count / 2].z);
                    g.FillPolygon(Brushes.Black, new[] { new PointF(s0.X - 6, s0.Y - 9), new PointF(s0.X + 6, s0.Y - 9), new PointF(s0.X, s0.Y - 1) });
                    g.DrawString("FRL", fb, Brushes.Black, s0.X - 10, s0.Y - 22);
                    if (!double.IsNaN(bx))
                    {
                        PointF a = P(bx, bz), b = P(bx, bs);
                        using (var pen = new Pen(Color.Black, 0.8f) { CustomStartCap = new AdjustableArrowCap(3, 4), CustomEndCap = new AdjustableArrowCap(3, 4) })
                            g.DrawLine(pen, a, b);
                        g.DrawString(best.ToString("0.000") + "  MIN VERTICAL CLEARANCE", f, Brushes.Black, a.X + 4, (a.Y + b.Y) / 2 - 6);
                    }
                }
                // axes: bubble + dashed line, span dims on top with "Span n" under, type under the bridge
                var ax = d.Axes.OrderBy(a => a.x).ToList();
                using (var dash = new Pen(Color.Gray, 0.6f) { DashPattern = new[] { 5f, 3f } })
                using (var dim = new Pen(Ink, 0.7f) { CustomStartCap = new AdjustableArrowCap(3, 4), CustomEndCap = new AdjustableArrowCap(3, 4) })
                {
                    for (int i = 0; i < ax.Count; i++)
                    {
                        float x = P(ax[i].x, z0).X;
                        g.DrawLine(dash, x, 24, x, yGround);
                        var rc = new RectangleF(x - 13, 4, 26, 20);
                        g.FillEllipse(Brushes.White, rc); g.DrawEllipse(ink, rc);
                        TextRenderer.DrawText(g, ax[i].name, f, Rectangle.Round(rc), Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                        _hits.Add((rc, ax[i].name));
                        if (!string.IsNullOrEmpty(ax[i].type))
                        {
                            SizeF ts = g.MeasureString(ax[i].type, f);
                            g.DrawString(ax[i].type, f, Brushes.DimGray, x - ts.Width / 2, Height - 16);
                        }
                        if (i + 1 < ax.Count)
                        {
                            float xn = P(ax[i + 1].x, z0).X;
                            g.DrawLine(dim, x, 34, xn, 34);
                            string t = (ax[i + 1].x - ax[i].x).ToString("0.000");
                            SizeF ts = g.MeasureString(t, fb);
                            if (ts.Width < xn - x) g.DrawString(t, fb, Brushes.Black, (x + xn - ts.Width) / 2, 20 + 1);
                            string sp = "Span " + (i + 1);
                            SizeF s2 = g.MeasureString(sp, f);
                            if (s2.Width < xn - x) g.DrawString(sp, f, Brushes.Gray, (x + xn - s2.Width) / 2, 36);
                        }
                    }
                }
            }
        }

        // lowest deck level at a station (bottom of the cut deck)
        public static double Soffit(LongData d, double x)
        {
            double best = double.NaN;
            foreach (var l in d.Loops.Where(q => q.deck))
            {
                var p = l.loop;
                for (int i = 0; i < p.Count; i++)
                {
                    var a = p[i]; var b = p[(i + 1) % p.Count];
                    if ((a.x - x) * (b.x - x) > 0 || Math.Abs(b.x - a.x) < 1e-9) continue;
                    double z = a.z + (b.z - a.z) * (x - a.x) / (b.x - a.x);
                    if (double.IsNaN(best) || z < best) best = z;
                }
            }
            return best;
        }
    }
}
