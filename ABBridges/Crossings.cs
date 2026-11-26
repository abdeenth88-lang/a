// AB Bridges - roads under the bridge (independent of the bridge stations).
//
// Between the corbels, along our alignment: where the alignment enters each under-bridge road
// surface, where it leaves, and equal divisions <= 2 m between them.
// Clearance = bridge level - road level - bridge depth (depth per road).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace ABBridges
{
    internal sealed class CrossPoint
    {
        public string Name;           // Start, #1.., End
        public double Station;
        public Point3d P;
        public double RoadZ = double.NaN, BridgeZ = double.NaN;
        public double Clearance(double depth) => double.IsNaN(RoadZ) || double.IsNaN(BridgeZ) ? double.NaN : BridgeZ - RoadZ - depth;
    }

    internal sealed class Crossing
    {
        public string Road;
        public double From, To;
        public List<CrossPoint> Points = new List<CrossPoint>();

        public double Depth
        {
            get => BridgeSettings.RoadDepth.TryGetValue(Road, out double d) ? d : 0.0;
            set => BridgeSettings.RoadDepth[Road] = value;
        }

        public double MinClearance
        {
            get
            {
                double d = Depth;
                var v = Points.Select(p => p.Clearance(d)).Where(x => !double.IsNaN(x)).ToList();
                return v.Count == 0 ? double.NaN : v.Min();
            }
        }
    }

    internal static class Crossings
    {
        private const double Scan = 0.10;      // m, scanning step along the alignment
        private const double MaxPart = 2.00;   // m, max distance between points

        // range = first and last station of the bridge (corbel to corbel)
        public static List<Crossing> Compute(Transaction tr, BridgeModel m, double from, double to, List<string> warn)
        {
            var res = new List<Crossing>();
            if (BridgeSettings.UnderIds.Count(BridgeSettings.Valid) == 0 || to <= from) return res;

            var design = new SurfaceSet(tr, BridgeSettings.DesignIds);
            var under = new SurfaceSet(tr, BridgeSettings.UnderIds);
            int noBridge = 0;

            for (int si = 0; si < under.Count; si++)
            {
                string sname = under.Names[si];
                var runs = new List<(double a, double b)>();
                int n = Math.Max(2, (int)Math.Ceiling((to - from) / Scan));
                bool inside = false;
                double runStart = 0, prevSta = from;
                for (int k = 0; k <= n; k++)
                {
                    double st = from + (to - from) * k / n;
                    bool now = Inside(under, si, m, st);
                    if (now && !inside)
                        runStart = k == 0 ? st : Refine(under, si, m, prevSta, st);
                    else if (!now && inside)
                        runs.Add((runStart, Refine(under, si, m, st, prevSta)));
                    inside = now;
                    prevSta = st;
                }
                if (inside) runs.Add((runStart, to));

                int idx = 0;
                foreach (var (a, b) in runs)
                {
                    if (b - a < 0.05) continue;
                    idx++;
                    var c = new Crossing
                    {
                        Road = runs.Count(r => r.b - r.a >= 0.05) > 1 ? sname + " (" + idx.ToString(CultureInfo.InvariantCulture) + ")" : sname,
                        From = a,
                        To = b
                    };
                    int parts = Math.Max(1, (int)Math.Ceiling((b - a) / MaxPart - 1e-9));
                    for (int k = 0; k <= parts; k++)
                    {
                        double st = a + (b - a) * k / parts;
                        Point3d p = m.Al.PointAt(st);
                        var cp = new CrossPoint
                        {
                            Name = k == 0 ? "Start" : (k == parts ? "End" : "#" + k.ToString(CultureInfo.InvariantCulture)),
                            Station = st,
                            P = p
                        };
                        cp.RoadZ = RoadZ(under, si, m, st, a, b);
                        double? bz = design.Z(p.X, p.Y);
                        if (bz.HasValue) cp.BridgeZ = bz.Value; else noBridge++;
                        c.Points.Add(cp);
                    }
                    res.Add(c);
                }
            }

            if (noBridge > 0) warn.Add($"{noBridge} under-bridge point(s) have no bridge level (alignment outside the design surface there).");
            return res.OrderBy(c => c.From).ToList();
        }

        private static bool Inside(SurfaceSet s, int i, BridgeModel m, double st)
        {
            Point3d p = m.Al.PointAt(st);
            return s.ZOne(i, p.X, p.Y).HasValue;
        }

        // boundary between an outside station and an inside station (1 mm)
        private static double Refine(SurfaceSet s, int i, BridgeModel m, double outSta, double inSta)
        {
            for (int k = 0; k < 30 && Math.Abs(inSta - outSta) > 0.001; k++)
            {
                double mid = (outSta + inSta) / 2;
                if (Inside(s, i, m, mid)) inSta = mid; else outSta = mid;
            }
            return inSta;
        }

        // level on the road surface; at the very boundary step 1 mm inside
        private static double RoadZ(SurfaceSet s, int i, BridgeModel m, double st, double a, double b)
        {
            foreach (double d in new[] { 0.0, 0.001, -0.001, 0.005, -0.005 })
            {
                double x = Math.Max(a, Math.Min(b, st + d));
                Point3d p = m.Al.PointAt(x);
                double? z = s.ZOne(i, p.X, p.Y);
                if (z.HasValue) return z.Value;
            }
            return double.NaN;
        }
    }
}
