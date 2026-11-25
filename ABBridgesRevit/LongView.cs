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
        private string _marked;

        // drawing look (like the drawings): concrete hatch, black lines, desert-yellow ground
        private static readonly Color Sand = Color.FromArgb(90, 239, 159, 39);
        private static readonly Color Ink = Color.Black;
        private const float PadT = 64, PadB = 30, PadL = 14, PadR = 14;

        // barrier of the road below (m): 0.40 wide at the bottom, 0.20 at the top, 1.20 high, New Jersey traffic face
        public static readonly (double x, double z)[] Barrier =
        {
            (0, 0), (0, 0.08), (-0.12, 0.33), (-0.20, 1.20), (-0.40, 1.20), (-0.40, 0)
        };
        public const double PaveT = 0.25;            // pavement thickness drawn under the FRL (m)

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

        public void Mark(string axis) { if (_marked != axis) { _marked = axis; Invalidate(); } }

        // the screen x of a station (NaN when nothing is drawn yet); used to put the axis cards under the bubbles
        public float ScreenX(double sta)
        {
            if (!Frame(out double x0, out double sx, out _, out _)) return float.NaN;
            return (float)(PadL + (sta - x0) * sx);
        }

        private bool Frame(out double x0, out double sx, out double z0, out double sz)
        {
            x0 = sx = z0 = sz = 0;
            LongData d = Data;
            var pts = d == null ? new List<(double x, double z)>() : d.Loops.SelectMany(l => l.loop).Concat(d.Ground).ToList();
            if (d == null || pts.Count == 0 || d.Axes.Count == 0) return false;
            x0 = Math.Min(pts.Min(p => p.x), d.Axes.Min(a => a.x));
            double x1 = Math.Max(pts.Max(p => p.x), d.Axes.Max(a => a.x));
            z0 = pts.Min(p => p.z);
            double z1 = pts.Max(p => p.z);
            sx = (Width - PadL - PadR) / Math.Max(1e-6, x1 - x0);
            sz = Math.Min(sx * Exaggerate, (Height - PadT - PadB) / Math.Max(1e-6, z1 - z0));
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            _hits.Clear();
            LongData d = Data;
            if (!Frame(out double x0, out double sx, out double z0, out double sz))
            {
                TextRenderer.DrawText(g, Message, Font, ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            Func<double, double, PointF> P = (x, z) => new PointF((float)(PadL + (x - x0) * sx), (float)(Height - PadB - (z - z0) * sz));
            float yGround = Height - PadB;

            using (var f = new Font("Arial", 8f))
            using (var fb = new Font("Arial", 8f, FontStyle.Bold))
            using (var ink = new Pen(Ink, 0.9f))
            using (var conc = new HatchBrush(HatchStyle.DottedGrid, Color.FromArgb(150, 150, 150), Color.FromArgb(237, 237, 237)))
            using (var deckB = new HatchBrush(HatchStyle.Percent10, Color.FromArgb(140, 140, 140), Color.FromArgb(242, 242, 242)))
            {
                // ground: desert yellow under the FGL line
                if (d.Ground.Count > 1)
                {
                    var gp = d.Ground.OrderBy(p => p.x).Select(p => P(p.x, p.z)).ToList();
                    var poly = new List<PointF>(gp) { new PointF(gp.Last().X, Height), new PointF(gp[0].X, Height) };
                    using (var b = new SolidBrush(Sand)) g.FillPolygon(b, poly.ToArray());
                    using (var pen = new Pen(Color.FromArgb(133, 79, 11), 1.1f)) g.DrawLines(pen, gp.ToArray());
                    PointF fg = gp[gp.Count / 3];
                    g.DrawString("FGL", f, Brushes.Black, fg.X - 8, fg.Y - 22);
                    g.FillPolygon(Brushes.Black, new[] { new PointF(fg.X - 4, fg.Y - 8), new PointF(fg.X + 4, fg.Y - 8), new PointF(fg.X, fg.Y - 1) });
                }
                // the cut elements: concrete hatch, black outline (deck lighter)
                foreach (var l in d.Loops.Where(x => x.loop.Count > 2))
                {
                    PointF[] pp = l.loop.Select(q => P(q.x, q.z)).ToArray();
                    g.FillPolygon(l.deck ? (Brush)deckB : conc, pp);
                    g.DrawPolygon(ink, pp);
                }
                // roads below: pavement + a barrier outside each edge, FRL symbol, min vertical clearance under the deck
                foreach (var rd in d.Roads.Where(r => r.pts.Count > 0))
                {
                    var rp = rd.pts.OrderBy(p => p.x).ToList();
                    if (rp.Count > 1)
                    {
                        var top = rp.Select(p => P(p.x, p.z)).ToList();
                        var pave = new List<PointF>(top);
                        pave.AddRange(rp.AsEnumerable().Reverse().Select(p => P(p.x, p.z - PaveT)));
                        using (var asph = new HatchBrush(HatchStyle.Percent50, Color.FromArgb(70, 70, 70), Color.FromArgb(110, 110, 110))) g.FillPolygon(asph, pave.ToArray());
                        g.DrawPolygon(ink, pave.ToArray());
                        // barriers: traffic face towards the road, outside its first / last point
                        foreach (var (px, pz, side) in new[] { (rp[0].x, rp[0].z, 1.0), (rp[rp.Count - 1].x, rp[rp.Count - 1].z, -1.0) })
                        {
                            PointF[] bp = Barrier.Select(q => P(px + side * q.x, pz - PaveT + q.z)).ToArray();
                            g.FillPolygon(conc, bp);
                            g.DrawPolygon(ink, bp);
                        }
                    }
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
                // axes: bubble + dashed line, span dims on top with "Span n" under, type under the bridge; the open axis in orange
                var ax = d.Axes.OrderBy(a => a.x).ToList();
                using (var dash = new Pen(Color.Gray, 0.6f) { DashPattern = new[] { 8f, 3f, 2f, 3f } })
                using (var dim = new Pen(Ink, 0.7f) { CustomStartCap = new AdjustableArrowCap(3, 4), CustomEndCap = new AdjustableArrowCap(3, 4) })
                using (var mark = new Pen(Color.FromArgb(232, 121, 43), 2.4f))
                {
                    for (int i = 0; i < ax.Count; i++)
                    {
                        float x = P(ax[i].x, z0).X;
                        g.DrawLine(dash, x, 26, x, yGround);
                        var rc = new RectangleF(x - 11, 3, 22, 22);
                        g.FillEllipse(Brushes.White, rc); g.DrawEllipse(ax[i].name == _marked ? mark : ink, rc);
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
                            g.DrawLine(dim, x, 36, xn, 36);
                            string t = (ax[i + 1].x - ax[i].x).ToString("0.000");
                            SizeF ts = g.MeasureString(t, fb);
                            if (ts.Width < xn - x) g.DrawString(t, fb, Brushes.Black, (x + xn - ts.Width) / 2, 22);
                            string sp = "SPAN " + (i + 1);
                            SizeF s2 = g.MeasureString(sp, f);
                            if (s2.Width < xn - x) g.DrawString(sp, f, Brushes.Black, (x + xn - s2.Width) / 2, 38);
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
