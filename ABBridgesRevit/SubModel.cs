// AB Bridges for Revit - Substructure: what is set for every axis (the bearing row first, then the families in
// order), kept in the project (AbStore keys "SUB|AX|{axis}"), and the geometry shared by the dialog and the placement.
// No Revit types here (the dialog uses it too).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ABBridgesRevit
{
    // the section preview: zoom to fit, where the view sits on the screen (and what it shows, in view feet),
    // and what a measuring tool can snap to (view feet: right / up)
    internal interface IMeasurable
    {
        void ZoomFit();
        bool Frame(out int left, out int top, out int right, out int bottom, out double x0, out double y0, out double x1, out double y1);
        IList<(double x, double y)> SnapPoints { get; }
        IList<((double x, double y) a, (double x, double y) b)> SnapSegments { get; }
    }

    // a pick asked from the dialog: the row of an axis, 1 line (center) or 2 lines (first / last of a repeated row)
    internal sealed class SubPick
    {
        public string Axis;
        public int Row;
        public int Picks = 1;
        public string Label;
    }

    internal sealed class SubParam
    {
        public string Name;
        public string Unit = "";
        public bool IsInstance;
        public bool IsYesNo;
        public string Default = "";
    }

    internal sealed class SubRow
    {
        public bool IsBearing;
        public bool IsAbut;                 // the abutment row: a two-point adaptive family on the left / right deck edges
        public bool Swap;                   // abutment: point 1 on the right edge (else on the left)
        public bool AutoSwap;
        public bool Repeat;
        public bool Centered;               // repeated: count + spacing, centered on the deck center (+ HOff); else from the edges                 // a family row repeated along the axis: Count, Left / Right (from the deck edges), Equal / Spacings               // abutment row made now (not read back): the swap is set from the axis position
        public string Family = "";          // "Family : Type" ("" = none)
        public string Surface = "Design";   // Design / NGL / Soffit (under the deck soffit)
        public double VOff;                 // m, + up from the surface
        public double HOff;                 // m, + right along the axis from the deck center (half way between the edges)
        public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.Ordinal);   // only the edited ones

        // bearing row
        public int Count = 2;
        public double Left = 1.0, Right = 1.0;   // from the left / right deck edges
        public bool Equal = true;
        public List<double> Spacings = new List<double>();

        public SubRow Clone()
        {
            var r = (SubRow)MemberwiseClone();
            r.Params = new Dictionary<string, string>(Params, StringComparer.Ordinal);
            r.Spacings = new List<double>(Spacings);
            return r;
        }

        public static SubRow NewBearing() => new SubRow { IsBearing = true, Surface = "Soffit" };
        public static SubRow NewAbut() => new SubRow { IsAbut = true, Surface = "Design", AutoSwap = true, HOff = 0.10 };   // HOff = shift from the corbel section along the bridge
        public bool Reserved => IsBearing || IsAbut;
    }

    internal sealed class SubAxis
    {
        public string Name;
        public bool Create = true;
        // row 0 = bearing, row 1 = abutment, then the families in order
        // row 0 = abutment, then the families in order (bearings are an ordinary repeated row now)
        public const int First = 1;
        public List<SubRow> Rows = new List<SubRow> { SubRow.NewAbut() };

        public SubRow Bearing => Rows.FirstOrDefault(x => x.IsBearing) ?? new SubRow { IsBearing = true };     // (old settings only)
        public bool HasWork => Rows.Any(r => !string.IsNullOrEmpty(r.Family));
        public SubRow Abut => Rows[0];
        public int Families => Rows.Skip(First).Count(r => !string.IsNullOrEmpty(r.Family));

        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private static string E(string s) => Uri.EscapeDataString(s ?? "");
        private static string D(string s) => Uri.UnescapeDataString(s ?? "");
        private static string N(double v) => v.ToString("R", CI);
        private static double P(string s, double def) => double.TryParse(s, NumberStyles.Float, CI, out double v) ? v : def;

        public string Encode()
        {
            var parts = new List<string> { Create ? "1" : "0" };
            foreach (SubRow r in Rows)
            {
                string ps = string.Join(";", r.Params.Select(kv => E(kv.Key) + ":" + E(kv.Value)));
                string sp = string.Join(";", r.Spacings.Select(N));
                parts.Add(string.Join(",", new[]
                {
                    r.IsBearing ? "B" : r.IsAbut ? "A" : "F", E(r.Family), E(r.Surface), N(r.VOff), N(r.HOff), E(ps),
                    r.Count.ToString(CI), N(r.Left), N(r.Right), r.Equal ? "1" : "0", E(sp), r.Swap ? "1" : "0", r.Repeat ? "1" : "0", r.Centered ? "1" : "0"
                }));
            }
            return string.Join("|", parts);
        }

        public static SubAxis Decode(string name, string text)
        {
            var a = new SubAxis { Name = name, Rows = new List<SubRow>() };
            string[] parts = (text ?? "").Split('|');
            a.Create = parts.Length == 0 || parts[0] != "0";
            for (int i = 1; i < parts.Length; i++)
            {
                string[] f = parts[i].Split(',');
                if (f.Length < 11) continue;
                var r = new SubRow
                {
                    IsBearing = f[0] == "B",
                    IsAbut = f[0] == "A",
                    Swap = f.Length > 11 && f[11] == "1",
                    Repeat = f.Length > 12 && f[12] == "1",
                    Centered = f.Length > 13 && f[13] == "1",
                    Family = D(f[1]),
                    Surface = D(f[2]),
                    VOff = P(f[3], 0),
                    HOff = P(f[4], 0),
                    Count = Math.Max(1, (int)P(f[6], 2)),
                    Left = P(f[7], 1),
                    Right = P(f[8], 1),
                    Equal = f[9] != "0"
                };
                foreach (string kv in D(f[5]).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int c = kv.IndexOf(':');
                    if (c > 0) r.Params[D(kv.Substring(0, c))] = D(kv.Substring(c + 1));
                }
                foreach (string v in D(f[10]).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    r.Spacings.Add(P(v, 1));
                if (string.IsNullOrEmpty(r.Surface)) r.Surface = r.IsBearing ? "Soffit" : "Design";
                a.Rows.Add(r);
            }
            // abutment first, then the families; an old bearing row becomes an ordinary repeated, centered row
            SubRow abut = a.Rows.FirstOrDefault(x => x.IsAbut) ?? SubRow.NewAbut();
            var rest = new List<SubRow>();
            foreach (SubRow x in a.Rows.Where(x => !x.IsAbut))
            {
                if (x.IsBearing)
                {
                    if (string.IsNullOrEmpty(x.Family)) continue;
                    x.IsBearing = false;
                    x.Repeat = true;
                    x.Centered = true;
                    if (x.Spacings.Count == 0) x.Spacings.Add(2.0);
                }
                rest.Add(x);
            }
            a.Rows = new List<SubRow> { abut };
            a.Rows.AddRange(rest);
            return a;
        }

        public SubAxis CopyAs(string name) => new SubAxis { Name = name, Create = Create, Rows = Rows.Select(r => r.Clone()).ToList() };
    }

    // the deck cut at an axis: loops of (offset along the axis, shared Z), m
    internal sealed class SubSection
    {
        public List<List<(double off, double z)>> Loops = new List<List<(double off, double z)>>();

        public double SoffitAt(double off) => Cross(off, true);
        public double TopAt(double off) => Cross(off, false);

        private double Cross(double off, bool lowest)
        {
            double best = double.NaN;
            foreach (var loop in Loops)
                for (int i = 0; i < loop.Count; i++)
                {
                    var p = loop[i];
                    var q = loop[(i + 1) % loop.Count];
                    double lo = Math.Min(p.off, q.off), hi = Math.Max(p.off, q.off);
                    if (off < lo - 1e-9 || off > hi + 1e-9) continue;
                    double z = hi - lo < 1e-9 ? (lowest ? Math.Min(p.z, q.z) : Math.Max(p.z, q.z))
                                              : p.z + (q.z - p.z) * (off - p.off) / (q.off - p.off);
                    if (double.IsNaN(best) || (lowest ? z < best : z > best)) best = z;
                }
            return best;
        }
    }

    internal static class SubGeo
    {
        // offsets of the deck edges at an axis (Design points "Left edge" / "Right edge"; else the outer points)
        public static (double left, double right) Edges(AxisData a)
        {
            var all = a.Design.Where(p => !double.IsNaN(p.off)).ToList();
            if (all.Count == 0) all = a.Ngl.Where(p => !double.IsNaN(p.off)).ToList();
            if (all.Count == 0) return (-5, 5);
            double l = all.Where(p => p.name.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0).Select(p => (double?)p.off).FirstOrDefault() ?? all.Min(p => p.off);
            double r = all.Where(p => p.name.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0).Select(p => (double?)p.off).FirstOrDefault() ?? all.Max(p => p.off);
            return (Math.Min(l, r), Math.Max(l, r));
        }

        // the middle of the deck at the axis (half way between the edges): the families are set right / left of it
        public static double Center(AxisData a)
        {
            var (L, R) = Edges(a);
            return (L + R) / 2;
        }

        // offset from the alignment of a family row (its HOff is from the center, + right)
        public static double RowOff(SubRow r, AxisData a) => Center(a) + r.HOff;

        // the spacings between the bearings (Count - 1 values): equal = the first value for all; typed = one by one
        public static List<double> SpacingList(SubRow b)
        {
            int n = Math.Max(1, b.Count);
            double first = b.Spacings.Count > 0 && b.Spacings[0] > 0 ? b.Spacings[0] : 2.0;
            var res = new List<double>();
            for (int i = 0; i < n - 1; i++)
            {
                if (b.Equal) res.Add(first);
                else res.Add(i < b.Spacings.Count ? b.Spacings[i] : (b.Spacings.Count > 0 ? b.Spacings[b.Spacings.Count - 1] : 2.0));
            }
            return res;
        }

        // offsets of the bearings along the axis (from the alignment, + right): one row, centered on the deck center
        // (+ the row's HOff), the spacings between them
        public static List<double> BearingOffs(SubRow b, AxisData a)
        {
            List<double> sp = SpacingList(b);
            double x = RowOff(b, a) - sp.Sum() / 2;
            var res = new List<double> { x };
            foreach (double v in sp) { x += v; res.Add(x); }
            return res;
        }

        public static List<double> SpacingsOf(SubRow b, AxisData a) => SpacingList(b);

        // every bearing at one level: the chosen surface at the bearing row's center, + its vertical offset
        public static double BearingZ(SubRow b, AxisData a, SubSection sec) => SurfaceZ(a, sec, b.Surface, RowOff(b, a)) + b.VOff;

        // level of the chosen surface at an offset (NaN = not known)
        public static double SurfaceZ(AxisData a, SubSection sec, string surface, double off)
        {
            switch (surface)
            {
                case "NGL": return AxisData.ZAt(a.Ngl, off);
                case "Soffit": return sec == null ? double.NaN : sec.SoffitAt(off);
                default: return AxisData.ZAt(a.Design, off);
            }
        }

        // a repeated family row: from the left edge (+ Left) to the right edge (- Right), equal, or typed spacings from the left one
        public static List<double> RepeatOffs(SubRow r, AxisData a)
        {
            if (r.Centered) return CenteredOffs(r, a);
            var (L, R) = Edges(a);
            int n = Math.Max(1, r.Count);
            double first = L + r.Left;
            var res = new List<double> { first };
            if (n == 1) return res;
            if (r.Equal)
            {
                double step = (R - r.Right - first) / (n - 1);
                for (int i = 1; i < n; i++) res.Add(first + step * i);
            }
            else
            {
                double x = first;
                for (int i = 1; i < n; i++)
                {
                    x += i - 1 < r.Spacings.Count ? r.Spacings[i - 1] : (r.Spacings.Count > 0 ? r.Spacings[r.Spacings.Count - 1] : 2.0);
                    res.Add(x);
                }
            }
            return res;
        }

        // a centered repeated row: one spacing for all (Equal) or typed one by one, the group centered on the deck center + HOff
        public static List<double> CenteredOffs(SubRow r, AxisData a)
        {
            int n = Math.Max(1, r.Count);
            var sp = new List<double>();
            double first = r.Spacings.Count > 0 && r.Spacings[0] > 0 ? r.Spacings[0] : 2.0;
            for (int i = 0; i < n - 1; i++)
                sp.Add(r.Equal ? first : (i < r.Spacings.Count ? r.Spacings[i] : (r.Spacings.Count > 0 ? r.Spacings[r.Spacings.Count - 1] : 2.0)));
            double x = RowOff(r, a) - sp.Sum() / 2;
            var res = new List<double> { x };
            foreach (double v in sp) { x += v; res.Add(x); }
            return res;
        }

        // where a row's families go along the axis (offsets from the alignment)
        public static List<double> RowOffs(SubRow r, AxisData a) =>
            r.IsBearing ? BearingOffs(r, a) : r.Repeat ? RepeatOffs(r, a) : new List<double> { RowOff(r, a) };

        // the offset whose surface level a row takes: bearings and repeated rows - one level for all (their middle)
        public static double LevelOff(SubRow r, AxisData a, double off)
        {
            if (r.IsBearing) return RowOff(r, a);
            if (r.Repeat) { var o = RepeatOffs(r, a); return (o[0] + o[o.Count - 1]) / 2; }
            return off;
        }

        // the abutment's two points: (offset, level) on the left and right deck edges, point 1 first
        public static List<(double off, double z)> AbutPoints(SubRow r, AxisData a, SubSection sec)
        {
            var (L, R) = Edges(a);
            var pts = new List<(double off, double z)>
            {
                (L, SurfaceZ(a, sec, r.Surface, L) + r.VOff),
                (R, SurfaceZ(a, sec, r.Surface, R) + r.VOff)
            };
            if (r.Swap) pts.Reverse();
            return pts;
        }

        public static double Snap(double v, double step = 0.05) => Math.Round(v / step) * step;

        public static string StationText(double st)
        {
            if (double.IsNaN(st)) return "";
            int km = (int)Math.Floor(st / 1000.0);
            return km.ToString(CultureInfo.InvariantCulture) + "+" + (st - km * 1000.0).ToString("000.000", CultureInfo.InvariantCulture);
        }
    }
}
