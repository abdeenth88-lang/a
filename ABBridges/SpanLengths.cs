// AB Bridges - span lengths for the construction joints.
//
// With a centerline picked: the centerline is cut at the axes and the plan length between two
// axes is the span length; when it does not reach an axis it is extended straight along its
// end direction (like LENGTHEN), in the calculation only.
// Without a centerline: a curve through the midpoints between the left and right edges, from the
// center of one axis to the center of the next (sampled every 0.5 m).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace ABBridges
{
    internal sealed class SpanLength
    {
        public int Index;
        public string Name;
        public string Key;
        public double Length = double.NaN;
        public bool FromCenterline;
        public List<Point3d> Curve = new List<Point3d>();   // automatic center curve (empty when from the centerline)
        public Point3d LabelPoint;
        public Vector3d LabelDir = Vector3d.XAxis;
        public string Note = "";                           // e.g. "centerline extended 0.35 m at A2"
    }

    internal static class SpanLengths
    {
        private const double Step = 0.50;

        public static List<SpanLength> Compute(BridgeModel m, List<string> warn)
        {
            var res = new List<SpanLength>();
            List<AxisInfo> ax = m.Axes;
            if (ax.Count < 2) return res;

            double[] pos = null;
            Point3d[] pts = null;
            string[] notes = null;
            if (m.Centerline != null)
            {
                pos = new double[ax.Count];
                pts = new Point3d[ax.Count];
                notes = new string[ax.Count];
                double clLen = m.Centerline.GetDistanceAtParameter(m.Centerline.EndParam);
                for (int i = 0; i < ax.Count; i++)
                {
                    if (!PositionOnCenterline(m.Centerline, clLen, ax[i], out pos[i], out pts[i], out double ext))
                    {
                        warn.Add($"The centerline cannot reach axis {ax[i].Name} - automatic span length used.");
                        pos = null;
                        break;
                    }
                    notes[i] = ext > 1e-6 ? $"centerline extended {ext:0.000} m at {ax[i].Name}" : "";
                }
            }

            for (int i = 0; i < ax.Count - 1; i++)
            {
                AxisInfo a = ax[i], b = ax[i + 1];
                var sl = new SpanLength { Index = i, Name = a.Name + "-" + b.Name, Key = BridgeSettings.SpanKey(a, b) };
                if (pos != null)
                {
                    sl.FromCenterline = true;
                    sl.Length = Math.Abs(pos[i + 1] - pos[i]);
                    double mid = (pos[i] + pos[i + 1]) / 2.0;
                    double clLen = m.Centerline.GetDistanceAtParameter(m.Centerline.EndParam);
                    double md = Math.Max(0, Math.Min(clLen, mid));
                    Point3d mp = Geo.Flat(m.Centerline.GetPointAtDist(md));
                    Vector3d t = m.Centerline.GetFirstDerivative(m.Centerline.GetParameterAtDistance(md));
                    t = new Vector3d(t.X, t.Y, 0);
                    sl.LabelDir = t.Length > 1e-9 ? t.GetNormal() : (pts[i + 1] - pts[i]).GetNormal();
                    sl.LabelPoint = mp;
                    sl.Note = string.Join("; ", new[] { notes[i], notes[i + 1] }.Where(x => !string.IsNullOrEmpty(x)));
                }
                else
                {
                    AutoCurve(m, a, b, sl);
                }
                if (!string.IsNullOrEmpty(sl.Note)) warn.Add($"Span {sl.Name}: {sl.Note}.");
                res.Add(sl);
            }
            return res;
        }

        // distance along the centerline where the axis line crosses it (negative / > length when extended)
        private static bool PositionOnCenterline(Curve c, double len, AxisInfo a, out double pos, out Point3d pt, out double ext)
        {
            pos = 0; ext = 0; pt = Point3d.Origin;
            Point3d? hit = Geo.ClosestIntersection(a.ClPoint, a.Dir, c, a.ClPoint, Geo.LineHalfLength);
            if (hit != null)
            {
                Point3d cp = c.GetClosestPointTo(hit.Value, false);
                pos = c.GetDistAtPoint(cp);
                pt = Geo.Flat(cp);
                return true;
            }

            // extend straight from the nearer end
            Point3d p0 = Geo.Flat(c.StartPoint), p1 = Geo.Flat(c.EndPoint);
            Vector3d d0 = c.GetFirstDerivative(c.StartParam), d1 = c.GetFirstDerivative(c.EndParam);
            d0 = new Vector3d(-d0.X, -d0.Y, 0);
            d1 = new Vector3d(d1.X, d1.Y, 0);
            double? t0 = d0.Length > 1e-12 ? Geo.RayLine(p0, d0.GetNormal(), a.ClPoint, a.Dir) : null;
            double? t1 = d1.Length > 1e-12 ? Geo.RayLine(p1, d1.GetNormal(), a.ClPoint, a.Dir) : null;
            if (t0 == null && t1 == null) return false;
            if (t1 == null || (t0 != null && t0.Value < t1.Value))
            {
                ext = t0.Value;
                pos = -ext;
                pt = p0 + d0.GetNormal() * ext;
            }
            else
            {
                ext = t1.Value;
                pos = len + ext;
                pt = p1 + d1.GetNormal() * ext;
            }
            return ext < 50.0;    // more than 50 m away: not the bridge centerline
        }

        private static void AutoCurve(BridgeModel m, AxisInfo a, AxisInfo b, SpanLength sl)
        {
            var mids = new List<Point3d>();
            if (m.EdgePoints(a.ClPoint, a.Dir, out Point3d l0, out Point3d r0)) mids.Add(Mid(l0, r0));
            double s0 = a.Station, s1 = b.Station;
            int n = Math.Max(2, (int)Math.Ceiling((s1 - s0) / Step));
            for (int k = 1; k < n; k++)
            {
                double st = s0 + (s1 - s0) * k / n;
                Point3d p = m.Al.PointAt(st);
                if (m.EdgePoints(p, m.Al.RightNormalAt(st), out Point3d l, out Point3d r)) mids.Add(Mid(l, r));
            }
            if (m.EdgePoints(b.ClPoint, b.Dir, out Point3d l1, out Point3d r1)) mids.Add(Mid(l1, r1));

            if (mids.Count < 2) return;
            double len = 0;
            for (int i = 1; i < mids.Count; i++) len += mids[i].DistanceTo(mids[i - 1]);
            sl.Length = len;
            sl.Curve = mids;

            // label at half length
            double half = len / 2, acc = 0;
            for (int i = 1; i < mids.Count; i++)
            {
                double d = mids[i].DistanceTo(mids[i - 1]);
                if (acc + d >= half || i == mids.Count - 1)
                {
                    double f = d > 1e-12 ? (half - acc) / d : 0;
                    f = Math.Max(0, Math.Min(1, f));
                    sl.LabelPoint = mids[i - 1] + (mids[i] - mids[i - 1]) * f;
                    sl.LabelDir = d > 1e-12 ? (mids[i] - mids[i - 1]).GetNormal() : Vector3d.XAxis;
                    break;
                }
                acc += d;
            }
        }

        private static Point3d Mid(Point3d a, Point3d b) => new Point3d((a.X + b.X) / 2, (a.Y + b.Y) / 2, 0);
    }
}
