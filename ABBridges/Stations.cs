// AB Bridges - stations (sections) along the alignment, skew, edge points and levels.
//
// Order in every span (axis i -> axis i+1):
//   after face of axis i, D1, D2, divisions ..., -D2, -D1, before face of axis i+1
// D1/D2 follow the diaphragm face directly (no divisions between them).
// Construction joints (CJ1/CJ2/CJ3) are fixed stations too; nothing between CJ1 and CJ2.
// A CJ near a D1/D2 station takes its place (that D1/D2 is dropped).
// The rest of every gap is divided equally, each part <= the max spacing.
// Skew: faces/axes are parallel to the axis (full skew); the ordinary stations next to a
// diaphragm ramp the skew down over N stations: skew * (1 - k/(N+1)); the rest are square.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace ABBridges
{
    public enum SectionKind { Corbel, DiaphragmFace, Axis, NearDiaphragm, ConstructionJoint, Division, InternalDiaphragm }

    public class Section
    {
        public string Remark;
        public SectionKind Kind;
        public double Station;
        public Point3d ClPoint;
        public Vector3d Dir;          // unit, left -> right
        public double SkewDeg;        // 0 = square to the alignment
        public int Span = -1;         // span index for ordinary stations, -1 for faces / axes / corbels
        public bool Edited;

        public bool HasEdges;
        public Point3d Left, Right;
        public double LeftZ = double.NaN, RightZ = double.NaN, CenterZ = double.NaN, NglZ = double.NaN;
        public string LeftZSource = "", RightZSource = "", CenterZSource = "";
        public double Width => HasEdges ? Left.DistanceTo(Right) : double.NaN;

        public bool Ordinary => Kind == SectionKind.NearDiaphragm || Kind == SectionKind.ConstructionJoint || Kind == SectionKind.Division || Kind == SectionKind.InternalDiaphragm;
        public bool Editable => Kind == SectionKind.Division;

        public string TypeName
        {
            get
            {
                switch (Kind)
                {
                    case SectionKind.Axis: return "Axis";
                    case SectionKind.DiaphragmFace: return "Diaphragm face";
                    case SectionKind.Corbel: return "Corbel";
                    case SectionKind.NearDiaphragm: return "Near diaphragm";
                    case SectionKind.ConstructionJoint: return "Construction joint";
                    case SectionKind.InternalDiaphragm: return "Internal diaphragm";
                    default: return "Division";
                }
            }
        }

        public Section Clone() => (Section)MemberwiseClone();
    }

    // span limits: the two diaphragm faces around it
    internal sealed class SpanInfo
    {
        public int Index;
        public string Name;           // "A1-P1"
        public string Key;
        public AxisInfo A, B;
        public Section After, Before; // after face of A, before face of B
        public double CJ1 = double.NaN, CJ2 = double.NaN, CJ3 = double.NaN;
    }

    internal sealed class StationSet
    {
        public List<Section> Sections = new List<Section>();
        public List<SpanInfo> Spans = new List<SpanInfo>();
    }

    internal static class Stations
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        private sealed class Fixed
        {
            public double Sta;
            public string Remark;
            public SectionKind Kind;
            public string Group;      // "S" chain after the start face, "E" chain before the end face, "CJ1".. , "F" faces
        }

        public static StationSet Build(BridgeModel m, List<string> warn)
        {
            var set = new StationSet();
            List<AxisInfo> axes = m.Axes;
            if (axes.Count < 2) return set;
            CenterLine cl = m.Al;
            double sp = BridgeSettings.Spacing;
            AxisInfo first = axes[0], last = axes[axes.Count - 1];

            if (BridgeSettings.CorbelStart > 0)
                AddFace(set.Sections, cl, first, -(first.Diaphragm / 2.0 + BridgeSettings.CorbelStart), "Corbel Start", SectionKind.Corbel, warn);

            for (int i = 0; i < axes.Count; i++)
            {
                AxisInfo ax = axes[i];
                double h = ax.Diaphragm / 2.0;
                Section before = h > 1e-9 ? AddFace(set.Sections, cl, ax, -h, ax.Name + " Before", SectionKind.DiaphragmFace, warn) : null;
                Section axSec = AddFace(set.Sections, cl, ax, 0.0, ax.Name, SectionKind.Axis, warn);
                Section after = h > 1e-9 ? AddFace(set.Sections, cl, ax, h, ax.Name + " After", SectionKind.DiaphragmFace, warn) : axSec;
                if (before == null) before = axSec;

                if (i > 0) set.Spans[i - 1].Before = before;
                if (i < axes.Count - 1)
                {
                    AxisInfo nx = axes[i + 1];
                    set.Spans.Add(new SpanInfo
                    {
                        Index = i,
                        Name = ax.Name + "-" + nx.Name,
                        Key = BridgeSettings.SpanKey(ax, nx),
                        A = ax,
                        B = nx,
                        After = after
                    });
                }
            }

            if (BridgeSettings.CorbelEnd > 0)
                AddFace(set.Sections, cl, last, last.Diaphragm / 2.0 + BridgeSettings.CorbelEnd, "Corbel End", SectionKind.Corbel, warn);

            foreach (SpanInfo s in set.Spans)
                BuildSpan(set, s, cl, sp, warn);

            set.Sections = set.Sections.OrderBy(x => x.Station).ToList();
            ApplySkew(set, cl, BridgeSettings.SkewTransition);
            return set;
        }

        private static void BuildSpan(StationSet set, SpanInfo s, CenterLine cl, double sp, List<string> warn)
        {
            if (s.After == null || s.Before == null) return;
            double a = s.After.Station, b = s.Before.Station;
            if (b - a < -1e-6)
            {
                warn.Add($"Diaphragms of {s.A.Name} and {s.B.Name} overlap ({b - a:0.000} m).");
                return;
            }

            var fx = new List<Fixed>
            {
                new Fixed { Sta = a, Group = "S" },
                new Fixed { Sta = b, Group = "E" }
            };

            // stations near the diaphragm faces (same distances at every face)
            var ds = new List<Fixed>();
            List<double> nd = BridgeSettings.NearDist.Where(d => d > 0).OrderBy(d => d).ToList();
            for (int k = 0; k < nd.Count; k++)
            {
                string tag = "D" + (k + 1).ToString(CI);
                ds.Add(new Fixed { Sta = a + nd[k], Remark = s.A.Name + " +" + tag, Kind = SectionKind.NearDiaphragm, Group = "S" });
                ds.Add(new Fixed { Sta = b - nd[k], Remark = s.B.Name + " -" + tag, Kind = SectionKind.NearDiaphragm, Group = "E" });
            }
            double maxS = ds.Where(d => d.Group == "S").Select(d => d.Sta).DefaultIfEmpty(a).Max();
            double minE = ds.Where(d => d.Group == "E").Select(d => d.Sta).DefaultIfEmpty(b).Min();
            if (maxS >= minE - 0.01 || maxS >= b - 0.01 || minE <= a + 0.01)
            {
                if (ds.Count > 0) warn.Add($"Span {s.Name} is too short for the D1/D2 stations - they were left out.");
                ds.Clear();
            }
            fx.AddRange(ds);

            // construction joints
            CjSpan cj;
            if (BridgeSettings.Cj.TryGetValue(s.Key, out cj) && cj.Enabled)
            {
                double len = cj.Length;
                if (double.IsNaN(len) || len <= 0)
                    warn.Add($"Span {s.Name}: construction joints skipped - span length is not set.");
                else
                {
                    s.CJ1 = s.A.Station + cj.Factor * len;
                    s.CJ2 = s.CJ1 + cj.Dist1;
                    s.CJ3 = s.CJ2 + cj.Dist2;
                    double[] cjs = { s.CJ1, s.CJ2, s.CJ3 };
                    double tol = Math.Max(0.30, sp / 2.0);
                    for (int k = 0; k < 3; k++)
                    {
                        double c = cjs[k];
                        string nm = s.Name + " CJ" + (k + 1).ToString(CI);
                        if (c <= a + 0.01 || c >= b - 0.01)
                        {
                            warn.Add($"{nm} ({Fmt.Chainage(c)}) is outside the span between the diaphragm faces - skipped.");
                            continue;
                        }
                        // a CJ near a D1/D2 station takes its place
                        Fixed near = fx.Where(f => f.Kind == SectionKind.NearDiaphragm && f.Remark != null)
                                       .OrderBy(f => Math.Abs(f.Sta - c)).FirstOrDefault();
                        string group = "CJ" + (k + 1).ToString(CI);
                        if (near != null && Math.Abs(near.Sta - c) < tol)
                        {
                            warn.Add($"{nm} replaces {near.Remark} (they were {Math.Abs(near.Sta - c):0.000} m apart).");
                            fx.Remove(near);
                        }
                        fx.Add(new Fixed { Sta = c, Remark = nm, Kind = SectionKind.ConstructionJoint, Group = group });
                    }
                }
            }

            // internal diaphragms: equally spaced between the axes (CL), two sections Gap apart each
            IdSpan ids = BridgeSettings.IdOf(s.Key);
            if (ids.Count > 0)
            {
                double A0 = s.A.Station, B0 = s.B.Station, half = Math.Max(0, ids.Gap) / 2.0;
                for (int k = 1; k <= ids.Count; k++)
                {
                    double c = A0 + (B0 - A0) * k / (ids.Count + 1);
                    string nm = s.Name + " ID" + k.ToString(CI);
                    if (c - half <= a + 0.01 || c + half >= b - 0.01) { warn.Add($"{nm} is outside the span between the diaphragm faces - skipped."); continue; }
                    // a near-diaphragm station or CJ right on it gives way
                    fx.RemoveAll(f => f.Remark != null && f.Kind != SectionKind.InternalDiaphragm && Math.Abs(f.Sta - c) < half + 0.30);
                    fx.Add(new Fixed { Sta = c - half, Remark = nm + " L", Kind = SectionKind.InternalDiaphragm, Group = "ID" + k.ToString(CI) });
                    fx.Add(new Fixed { Sta = c + half, Remark = nm + " R", Kind = SectionKind.InternalDiaphragm, Group = "ID" + k.ToString(CI) });
                }
            }

            fx = fx.OrderBy(f => f.Sta).ToList();
            int div = 0;
            for (int i = 0; i < fx.Count; i++)
            {
                Fixed f = fx[i];
                if (f.Remark != null)
                {
                    set.Sections.Add(new Section
                    {
                        Remark = f.Remark,
                        Kind = f.Kind,
                        Station = f.Sta,
                        ClPoint = cl.PointAt(f.Sta),
                        Span = s.Index
                    });
                }
                if (i == fx.Count - 1) break;
                Fixed g = fx[i + 1];
                bool noDiv = (f.Group == "S" && g.Group == "S") || (f.Group == "E" && g.Group == "E") ||
                             (f.Group == "CJ1" && g.Group == "CJ2") ||
                             (f.Group.StartsWith("ID") && f.Group == g.Group);
                if (noDiv) continue;
                double L = g.Sta - f.Sta;
                if (L <= 1e-6) continue;
                int n = Math.Max(1, (int)Math.Ceiling(L / sp - 1e-9));
                for (int k = 1; k < n; k++)
                {
                    double st = f.Sta + L * k / n;
                    div++;
                    set.Sections.Add(new Section
                    {
                        Remark = s.Name + " #" + div.ToString(CI),
                        Kind = SectionKind.Division,
                        Station = st,
                        ClPoint = cl.PointAt(st),
                        Span = s.Index
                    });
                }
            }
        }

        // line parallel to the axis, shifted perpendicular to the axis by 'offset' (negative = down-station)
        private static Section FaceSection(CenterLine cl, AxisInfo ax, double offset, string remark, SectionKind kind)
        {
            Vector3d n = new Vector3d(-ax.Dir.Y, ax.Dir.X, 0);
            if (n.DotProduct(cl.TangentAt(ax.Station)) < 0) n = n.Negate();
            Point3d refPt = ax.ClPoint + n * offset;

            Point3d? hit = Math.Abs(offset) < 1e-12
                ? ax.ClPoint
                : Geo.ClosestIntersection(refPt, ax.Dir, cl.Curve, refPt, Geo.LineHalfLength);
            if (hit == null) return null;

            double sta = cl.StationAt(hit.Value);
            return new Section
            {
                Remark = remark,
                Kind = kind,
                Station = sta,
                ClPoint = hit.Value,
                Dir = ax.Dir,
                SkewDeg = Geo.SkewDeg(cl.RightNormalAt(sta), ax.Dir)
            };
        }

        private static Section AddFace(List<Section> list, CenterLine cl, AxisInfo ax, double offset, string remark,
                                       SectionKind kind, List<string> warn)
        {
            Section s = FaceSection(cl, ax, offset, remark, kind);
            if (s == null)
            {
                warn.Add($"{remark}: line does not cross the alignment (outside its ends?).");
                return null;
            }
            list.Add(s);
            return s;
        }

        // ---------------------------------------------------------------- skew

        public static void ApplySkew(StationSet set, CenterLine cl, int n)
        {
            foreach (SpanInfo sp in set.Spans)
            {
                if (sp.After == null || sp.Before == null) continue;
                List<Section> ord = set.Sections.Where(x => x.Ordinary && x.Span == sp.Index).OrderBy(x => x.Station).ToList();
                int m = ord.Count;
                for (int i = 0; i < m; i++)
                {
                    int k = i + 1;          // counted from the start face
                    int j = m - i;          // counted from the end face
                    double skew = 0.0;
                    bool fromA = k <= n, fromB = j <= n;
                    if (fromA && fromB) { if (k <= j) fromB = false; else fromA = false; }
                    if (fromA) skew = sp.After.SkewDeg * (1.0 - (double)k / (n + 1));
                    else if (fromB) skew = sp.Before.SkewDeg * (1.0 - (double)j / (n + 1));
                    Section s = ord[i];
                    s.ClPoint = cl.PointAt(s.Station);
                    s.SkewDeg = skew;
                    s.Dir = Geo.Rotate(cl.RightNormalAt(s.Station), skew);
                }
            }
        }

        // span of a station (between the two faces), -1 when it is not inside a span
        public static int SpanAt(StationSet set, double sta)
        {
            foreach (SpanInfo sp in set.Spans)
                if (sp.After != null && sp.Before != null && sta > sp.After.Station + 1e-4 && sta < sp.Before.Station - 1e-4)
                    return sp.Index;
            return -1;
        }

        // division names A1-P1 #1, #2 ... in station order
        public static void Renumber(StationSet set)
        {
            foreach (SpanInfo sp in set.Spans)
            {
                int k = 0;
                foreach (Section s in set.Sections.Where(x => x.Kind == SectionKind.Division && x.Span == sp.Index).OrderBy(x => x.Station))
                    s.Remark = sp.Name + " #" + (++k).ToString(CI);
            }
        }

        // ---------------------------------------------------------------- edges and levels

        private const double SampleStep = 0.05;     // m, sampling along the section
        private const double SlopeBase = 1.00;      // m, length used for the cross slope

        public static void ComputeEdges(List<Section> sections, BridgeModel m, SurfaceSet design, SurfaceSet ngl, List<string> warn)
        {
            int outside = 0, nglOut = 0;
            double maxExt = 0;
            var missing = new List<string>();
            foreach (Section s in sections)
            {
                s.HasEdges = false;
                s.LeftZ = s.RightZ = s.CenterZ = s.NglZ = double.NaN;
                s.LeftZSource = s.RightZSource = s.CenterZSource = "";
                if (ngl != null && !ngl.IsEmpty)
                {
                    double? zn = ngl.Z(s.ClPoint.X, s.ClPoint.Y);
                    if (zn.HasValue) s.NglZ = zn.Value; else nglOut++;
                }
                if (!m.EdgePoints(s.ClPoint, s.Dir, out Point3d l, out Point3d r))
                {
                    missing.Add(s.Remark);
                    // level at the alignment can still be read
                    if (design != null && !design.IsEmpty)
                    {
                        double? zc = design.Z(s.ClPoint.X, s.ClPoint.Y);
                        if (zc.HasValue) { s.CenterZ = zc.Value; s.CenterZSource = "Surface"; }
                    }
                    continue;
                }
                s.Left = l;
                s.Right = r;
                s.HasEdges = true;

                if (design != null && !design.IsEmpty)
                {
                    double ext = Levels(design, s, warn);
                    if (ext > 0) { outside++; maxExt = Math.Max(maxExt, ext); }
                }
            }
            if (missing.Count > 0)
                warn.Add($"{missing.Count} section(s) do not reach both edges (check that the left/right edges cover the whole bridge - an edge made of several pieces: select all of them): " +
                         string.Join(", ", missing.Take(12)) + (missing.Count > 12 ? " ..." : ""));
            if (outside > 0)
                warn.Add($"{outside} section(s) have edge points outside the design surface: level extended from the cross slope (max {maxExt:0.00} m).");
            if (nglOut > 0)
                warn.Add($"{nglOut} alignment point(s) are outside the NGL surface - NGL left blank there.");
            if (design != null && design.Overlaps > 0)
                warn.Add($"Design surfaces overlap with different levels at {design.Overlaps} point(s) (max {design.MaxOverlapDiff * 100:0.0} cm near {design.OverlapWhere}); the level that keeps continuity was used.");
        }

        // returns the longest extension used (0 when both edges are on the surface)
        private static double Levels(SurfaceSet surface, Section s, List<string> warn)
        {
            double w = s.Left.DistanceTo(s.Right);
            Vector3d u = w > 1e-9 ? (s.Right - s.Left).GetNormal() : s.Dir;

            int n = Math.Max(2, (int)Math.Ceiling(w / SampleStep));
            var ts = new List<double>();
            var zs = new List<double>();
            double? prev = null;
            for (int i = 0; i <= n; i++)
            {
                double t = w * i / n;
                Point3d p = s.Left + u * t;
                double? z = surface.Z(p.X, p.Y, prev);
                if (z.HasValue) { ts.Add(t); zs.Add(z.Value); prev = z; }
            }

            if (ts.Count == 0)
            {
                warn.Add($"{s.Remark}: no level - the whole section is outside the design surface.");
                return 0;
            }

            double maxExt = 0;
            if (ts[0] < 1e-9) { s.LeftZ = zs[0]; s.LeftZSource = "Surface"; }
            else
            {
                s.LeftZ = Extend(ts, zs, true, 0.0);
                s.LeftZSource = $"Extended {ts[0]:0.00} m";
                maxExt = Math.Max(maxExt, ts[0]);
            }

            int last = ts.Count - 1;
            if (Math.Abs(ts[last] - w) < 1e-9) { s.RightZ = zs[last]; s.RightZSource = "Surface"; }
            else
            {
                s.RightZ = Extend(ts, zs, false, w);
                s.RightZSource = $"Extended {w - ts[last]:0.00} m";
                maxExt = Math.Max(maxExt, w - ts[last]);
            }

            // level at the alignment (the alignment point lies on the section line)
            double tc = (s.ClPoint - s.Left).DotProduct(u);
            if (tc >= ts[0] - 1e-9 && tc <= ts[last] + 1e-9)
            {
                s.CenterZ = Interp(ts, zs, tc);
                s.CenterZSource = "Surface";
            }
            else if (tc < ts[0])
            {
                s.CenterZ = Extend(ts, zs, true, tc);
                s.CenterZSource = $"Extended {ts[0] - tc:0.00} m";
            }
            else
            {
                s.CenterZ = Extend(ts, zs, false, tc);
                s.CenterZSource = $"Extended {tc - ts[last]:0.00} m";
            }
            return maxExt;
        }

        private static double Interp(List<double> ts, List<double> zs, double t)
        {
            if (ts.Count == 1) return zs[0];
            for (int i = 1; i < ts.Count; i++)
            {
                if (t <= ts[i] + 1e-12)
                {
                    double t0 = ts[i - 1], t1 = ts[i];
                    if (t1 - t0 < 1e-12) return zs[i];
                    return zs[i - 1] + (zs[i] - zs[i - 1]) * (t - t0) / (t1 - t0);
                }
            }
            return zs[zs.Count - 1];
        }

        // straight-line extension of the cross slope measured over SlopeBase next to the edge
        private static double Extend(List<double> ts, List<double> zs, bool fromStart, double target)
        {
            int i0 = fromStart ? 0 : ts.Count - 1;
            double t0 = ts[i0], z0 = zs[i0];
            int i1 = -1;
            if (fromStart)
            {
                for (int i = 1; i < ts.Count; i++) { i1 = i; if (ts[i] - t0 >= SlopeBase - 1e-9) break; }
            }
            else
            {
                for (int i = ts.Count - 2; i >= 0; i--) { i1 = i; if (t0 - ts[i] >= SlopeBase - 1e-9) break; }
            }
            if (i1 < 0 || Math.Abs(ts[i1] - t0) < 1e-6) return z0;
            double slope = (zs[i1] - z0) / (ts[i1] - t0);
            return z0 + slope * (target - t0);
        }
    }
}
