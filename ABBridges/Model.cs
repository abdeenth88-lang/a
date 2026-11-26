// AB Bridges - geometry model: alignment, edges, axes, centerline, surfaces.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace ABBridges
{
    public class AxisInfo
    {
        public ObjectId Id;
        public int Index;
        public string Name;
        public Point3d ClPoint;      // crossing with the alignment
        public double Station;
        public Vector3d Dir;         // unit, left -> right
        public double SkewDeg;
        public double Diaphragm;
        public double BeforeD1, BeforeD2, AfterD1, AfterD2;
    }

    // everything read from the drawing for one bridge (flattened copies of the curves)
    internal sealed class BridgeModel : IDisposable
    {
        public CenterLine Al;
        public List<Curve> Left = new List<Curve>(), Right = new List<Curve>();   // each edge may be several pieces
        public Curve Centerline;            // optional designer's centerline (flattened)
        public List<AxisInfo> Axes = new List<AxisInfo>();

        public static BridgeModel Load(Transaction tr, List<string> warn)
        {
            var m = new BridgeModel();
            try
            {
                m.Al = new CenterLine(Geo.LoadAlignment(tr), BridgeSettings.StartStation);
                foreach (ObjectId id in BridgeSettings.LeftIds.Where(BridgeSettings.Valid)) m.Left.Add(Geo.LoadFlat(tr, id));
                foreach (ObjectId id in BridgeSettings.RightIds.Where(BridgeSettings.Valid)) m.Right.Add(Geo.LoadFlat(tr, id));
                if (BridgeSettings.Valid(BridgeSettings.CenterlineId))
                    m.Centerline = Geo.LoadFlat(tr, BridgeSettings.CenterlineId);

                m.Axes = LoadAxes(tr, m.Al, BridgeSettings.AxisIds.Where(BridgeSettings.Valid), warn);
                foreach (AxisInfo a in m.Axes) BridgeSettings.ApplyAxisParams(a, m.Axes.Count);

                if (m.Axes.Count > 0 && EdgesSwapped(m.Axes[0], m.Left, m.Right))
                {
                    List<Curve> t = m.Left; m.Left = m.Right; m.Right = t;
                    warn.Add("Left and right edges were swapped automatically.");
                }
                return m;
            }
            catch
            {
                m.Dispose();
                throw;
            }
        }

        public static List<AxisInfo> LoadAxes(Transaction tr, CenterLine cl, IEnumerable<ObjectId> ids, List<string> warnings)
        {
            var axes = new List<AxisInfo>();
            foreach (ObjectId id in ids)
            {
                var c = tr.GetObject(id, OpenMode.ForRead) as Curve;
                if (c == null) continue;
                Point3d a = Geo.Flat(c.StartPoint), b = Geo.Flat(c.EndPoint);
                Vector3d dir = b - a;
                if (dir.Length < 1e-6) continue;
                dir = dir.GetNormal();
                Point3d mid = a + (b - a) * 0.5;

                Point3d? hit = Geo.ClosestIntersection(mid, dir, cl.Curve, mid, Geo.LineHalfLength);
                if (hit == null)
                {
                    warnings.Add($"An axis near ({mid.X:0.000}, {mid.Y:0.000}) does not cross the alignment - skipped.");
                    continue;
                }
                double sta = cl.StationAt(hit.Value);
                if (dir.DotProduct(cl.RightNormalAt(sta)) < 0) dir = dir.Negate();
                axes.Add(new AxisInfo { Id = id, ClPoint = hit.Value, Station = sta, Dir = dir });
            }

            axes = axes.OrderBy(x => x.Station).ToList();
            for (int i = 0; i < axes.Count; i++)
            {
                axes[i].Index = i;
                axes[i].Name = i == 0 ? "A1" : (i == axes.Count - 1 ? "A2" : "P" + i.ToString(CultureInfo.InvariantCulture));
                axes[i].SkewDeg = Geo.SkewDeg(cl.RightNormalAt(axes[i].Station), axes[i].Dir);
            }
            return axes;
        }

        // true when the picked "left" edge is actually on the right side
        private static bool EdgesSwapped(AxisInfo a0, List<Curve> left, List<Curve> right)
        {
            Point3d? pl = Geo.ClosestIntersection(a0.ClPoint, a0.Dir, left, a0.ClPoint, Geo.LineHalfLength);
            Point3d? pr = Geo.ClosestIntersection(a0.ClPoint, a0.Dir, right, a0.ClPoint, Geo.LineHalfLength);
            if (pl == null || pr == null) return false;
            double tl = (pl.Value - a0.ClPoint).DotProduct(a0.Dir);
            double trr = (pr.Value - a0.ClPoint).DotProduct(a0.Dir);
            return tl > 0 && trr < 0;
        }

        // edge points of a line through p with direction dir (null when it misses an edge)
        public bool EdgePoints(Point3d p, Vector3d dir, out Point3d left, out Point3d right)
        {
            left = right = Point3d.Origin;
            Point3d? pl = Geo.ClosestIntersection(p, dir, Left, p, Geo.LineHalfLength);
            Point3d? pr = Geo.ClosestIntersection(p, dir, Right, p, Geo.LineHalfLength);
            // a section just past the end of the edge lines (the corbel in front of the deck end):
            // the edge is carried on straight from its end, along its own direction
            if (pl == null) pl = Geo.PastEnd(p, dir, Left, Geo.MaxEdgeExtension);
            if (pr == null) pr = Geo.PastEnd(p, dir, Right, Geo.MaxEdgeExtension);
            if (pl == null || pr == null) return false;
            left = pl.Value;
            right = pr.Value;
            return true;
        }

        public void Dispose()
        {
            Al?.Dispose();
            foreach (Curve c in Left) c.Dispose();
            foreach (Curve c in Right) c.Dispose();
            Centerline?.Dispose();
        }
    }

    // ---------------------------------------------------------------- surfaces

    // one or more surfaces read as one: each point takes the level of the surface under it
    internal sealed class SurfaceSet
    {
        private readonly List<CivSurface> _s = new List<CivSurface>();
        private readonly List<Extents3d?> _ext = new List<Extents3d?>();
        public readonly List<string> Names = new List<string>();

        public int Overlaps;            // points where two surfaces gave different levels (> 2 cm)
        public double MaxOverlapDiff;
        public string OverlapWhere = "";

        public bool IsEmpty => _s.Count == 0;
        public string NameList => Names.Count == 0 ? "" : string.Join(", ", Names);

        public SurfaceSet(Transaction tr, IEnumerable<ObjectId> ids)
        {
            foreach (ObjectId id in ids)
            {
                if (!BridgeSettings.Valid(id)) continue;
                if (!(tr.GetObject(id, OpenMode.ForRead) is CivSurface s)) continue;
                _s.Add(s);
                Names.Add(s.Name);
                Extents3d? e = null;
                try { e = s.GeometricExtents; } catch { }
                _ext.Add(e);
            }
        }

        public int Count => _s.Count;

        public static double? ZOf(CivSurface s, Extents3d? e, double x, double y)
        {
            if (e.HasValue)
            {
                Extents3d b = e.Value;
                if (x < b.MinPoint.X - 0.01 || x > b.MaxPoint.X + 0.01 || y < b.MinPoint.Y - 0.01 || y > b.MaxPoint.Y + 0.01)
                    return null;
            }
            try
            {
                double z = s.FindElevationAtXY(x, y);
                if (double.IsNaN(z) || double.IsInfinity(z)) return null;
                return z;
            }
            catch { return null; }
        }

        public double? ZOne(int i, double x, double y) => ZOf(_s[i], _ext[i], x, y);

        // level at (x, y); 'near' = level of the neighbouring point, used when two surfaces disagree
        public double? Z(double x, double y, double? near = null)
        {
            double? only = null;
            List<double> all = null;
            for (int i = 0; i < _s.Count; i++)
            {
                double? z = ZOf(_s[i], _ext[i], x, y);
                if (!z.HasValue) continue;
                if (only == null) { only = z; continue; }
                if (all == null) all = new List<double> { only.Value };
                all.Add(z.Value);
            }
            if (all == null) return only;

            double min = all.Min(), max = all.Max();
            if (max - min <= 0.005) return all.Average();             // practically the same level

            if (max - min > 0.02)
            {
                Overlaps++;
                if (max - min > MaxOverlapDiff)
                {
                    MaxOverlapDiff = max - min;
                    OverlapWhere = $"({x:0.000}, {y:0.000})";
                }
            }
            // keep continuity with the neighbouring point; with no neighbour take the average
            if (near.HasValue) return all.OrderBy(v => Math.Abs(v - near.Value)).First();
            return all.Average();
        }
    }

    // ---------------------------------------------------------------- alignment

    internal sealed class CenterLine : IDisposable
    {
        public readonly Curve Curve;
        public readonly double StartStation;
        public readonly double Length;

        public CenterLine(Curve flatCurve, double startStation)
        {
            Curve = flatCurve;
            StartStation = startStation;
            Length = flatCurve.GetDistanceAtParameter(flatCurve.EndParam);
        }

        public double StationAt(Point3d p)
        {
            Point3d cp = Curve.GetClosestPointTo(Geo.Flat(p), false);
            return StartStation + Curve.GetDistAtPoint(cp);
        }

        public Point3d PointAt(double station)
        {
            double d = Math.Max(0.0, Math.Min(Length, station - StartStation));
            return Geo.Flat(Curve.GetPointAtDist(d));
        }

        public Vector3d TangentAt(double station)
        {
            double d = station - StartStation;
            double h = Geo.TangentBase / 2.0;
            double d1 = Math.Max(0.0, d - h), d2 = Math.Min(Length, d + h);
            if (d2 - d1 < 1e-6) { d1 = Math.Max(0.0, d2 - 2 * h); d2 = Math.Min(Length, d1 + 2 * h); }
            Vector3d v = Curve.GetPointAtDist(d2) - Curve.GetPointAtDist(d1);
            return new Vector3d(v.X, v.Y, 0).GetNormal();
        }

        public Vector3d RightNormalAt(double station)
        {
            Vector3d t = TangentAt(station);
            return new Vector3d(t.Y, -t.X, 0);
        }

        public void Dispose() => Curve?.Dispose();
    }

    // ---------------------------------------------------------------- helpers

    internal static class Geo
    {
        public const double LineHalfLength = 500.0;
        public const double TangentBase = 0.20;

        public static Point3d Flat(Point3d p) => new Point3d(p.X, p.Y, 0);

        public static Curve Flatten(Curve c) => c.GetOrthoProjectedCurve(new Plane(Point3d.Origin, Vector3d.ZAxis));

        public static Curve LoadFlat(Transaction tr, ObjectId id)
        {
            var c = (Curve)tr.GetObject(id, OpenMode.ForRead);
            return Flatten(c);
        }

        // the alignment, flattened, and reversed when "Reverse" is ticked (the drawing is not changed)
        public static Curve LoadAlignment(Transaction tr)
        {
            Curve c = LoadFlat(tr, BridgeSettings.AlignmentId);
            if (BridgeSettings.ReverseAlignment)
            {
                try { c.ReverseCurve(); }
                catch
                {
                    // curve types that cannot reverse: rebuild as a polyline through dense points
                    double len = c.GetDistanceAtParameter(c.EndParam);
                    int n = Math.Max(2, (int)Math.Ceiling(len / 0.05));
                    var pl = new Polyline();
                    for (int i = 0; i <= n; i++)
                    {
                        Point3d p = c.GetPointAtDist(len * (n - i) / n);
                        pl.AddVertexAt(i, new Point2d(p.X, p.Y), 0, 0, 0);
                    }
                    c.Dispose();
                    c = pl;
                }
            }
            return c;
        }

        public static Point3d? ClosestIntersection(Point3d p, Vector3d dir, Curve c, Point3d near, double half)
        {
            using (var ln = new Line(Flat(p) - dir * half, Flat(p) + dir * half))
            {
                var pts = new Point3dCollection();
                ln.IntersectWith(c, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
                if (pts.Count == 0) return null;
                Point3d best = Point3d.Origin;
                double bestD = double.MaxValue;
                foreach (Point3d q in pts)
                {
                    Point3d f = Flat(q);
                    double d = f.DistanceTo(Flat(near));
                    if (d < bestD) { bestD = d; best = f; }
                }
                return best;
            }
        }

        // closest crossing with any of several curves (an edge made of pieces)
        public static Point3d? ClosestIntersection(Point3d p, Vector3d dir, IEnumerable<Curve> curves, Point3d near, double half)
        {
            Point3d? best = null;
            double bestD = double.MaxValue;
            foreach (Curve c in curves)
            {
                Point3d? q = ClosestIntersection(p, dir, c, near, half);
                if (q == null) continue;
                double d = q.Value.DistanceTo(Flat(near));
                if (d < bestD) { bestD = d; best = q; }
            }
            return best;
        }

        public const double MaxEdgeExtension = 5.0;   // m, how far an edge line is carried on past its end

        // the section line through p (along dir) and the straight continuation of an edge past its start or end
        public static Point3d? PastEnd(Point3d p, Vector3d dir, IEnumerable<Curve> curves, double maxExt)
        {
            Point3d? best = null;
            double bestExt = double.MaxValue;
            Point3d fp = Flat(p);
            Vector3d d = new Vector3d(dir.X, dir.Y, 0);
            if (d.Length < 1e-12) return null;
            d = d.GetNormal();
            foreach (Curve c in curves)
            {
                foreach (bool atStart in new[] { true, false })
                {
                    Point3d e;
                    Vector3d t;
                    try
                    {
                        double par = atStart ? c.StartParam : c.EndParam;
                        e = Flat(c.GetPointAtParameter(par));
                        t = c.GetFirstDerivative(par);
                    }
                    catch { continue; }
                    t = new Vector3d(t.X, t.Y, 0);
                    if (t.Length < 1e-12) continue;
                    t = t.GetNormal();
                    if (atStart) t = -t;                      // pointing out of the curve
                    double den = d.X * t.Y - d.Y * t.X;
                    if (Math.Abs(den) < 1e-9) continue;
                    // fp + a*d = e + s*t
                    double wx = e.X - fp.X, wy = e.Y - fp.Y;
                    double sExt = (wx * d.Y - wy * d.X) / den;    // distance past the end (must be ahead of it)
                    if (sExt < -1e-6 || sExt > maxExt) continue;
                    if (sExt < bestExt) { bestExt = sExt; best = e + t * Math.Max(0, sExt); }
                }
            }
            return best;
        }

        // intersection of the ray p + t*d (t > 0) with the infinite line q + s*e; returns t or null
        public static double? RayLine(Point3d p, Vector3d d, Point3d q, Vector3d e)
        {
            double den = d.X * e.Y - d.Y * e.X;
            if (Math.Abs(den) < 1e-12) return null;
            double wx = q.X - p.X, wy = q.Y - p.Y;
            double t = (wx * e.Y - wy * e.X) / den;
            return t > 1e-9 ? (double?)t : null;
        }

        public static double SkewDeg(Vector3d rightNormal, Vector3d dir)
        {
            double cross = rightNormal.X * dir.Y - rightNormal.Y * dir.X;
            double dot = rightNormal.X * dir.X + rightNormal.Y * dir.Y;
            return Math.Atan2(cross, dot) * 180.0 / Math.PI;
        }

        public static Vector3d Rotate(Vector3d v, double deg)
        {
            double a = deg * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
            return new Vector3d(v.X * c - v.Y * s, v.X * s + v.Y * c, 0).GetNormal();
        }
    }

    internal static class Fmt
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        public static string Chainage(double s)
        {
            if (double.IsNaN(s)) return "";
            string sign = s < 0 ? "-" : "";
            s = Math.Abs(s);
            long km = (long)Math.Floor(s / 1000.0 + 1e-12);
            double m = Math.Round(s - km * 1000.0, 3);
            if (m >= 1000.0) { km++; m -= 1000.0; }
            return sign + km.ToString(CI) + "+" + m.ToString("000.000", CI);
        }

        public static string N3(double v) => double.IsNaN(v) ? "" : v.ToString("0.000", CI);
    }
}
