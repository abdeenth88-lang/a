// AB Bridges for Revit - "Substructure": places the substructure families at every axis of the bridge
// (sheet "Substructure" of the Excel from Civil 3D: axis point, direction, design surface, NGL).
// Any family type of the project can be chosen; it is set by one point (its top): point / work plane / face based,
// one-point adaptive, and columns (top at the point, length kept). Line based and multi-point adaptive families are reported.
// Level = chosen surface (Design / NGL / deck soffit) at its offset from the deck center + vertical offset (+ up).
// The family X axis is laid along the axis (left edge -> right edge), so it follows the skew.
// The settings of every axis and what was placed are kept in the project: re-creating an axis replaces only that axis.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using DialogResult = System.Windows.Forms.DialogResult;

namespace ABBridgesRevit
{
    [Transaction(TransactionMode.Manual)]
    public class SubstructureCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            // back from "Show in Revit": return to the view the user was in, then the dialog opens again
            try
            {
                UIDocument ud = data.Application.ActiveUIDocument;
                View av = ud?.ActiveView;
                if (s_prevView != null && av != null && (av.Name.StartsWith("AB Section - ") || av.Name.StartsWith("AB Plan - ")))
                {
                    if (ud.Document.GetElement(s_prevView) is View back && !back.IsTemplate) ud.ActiveView = back;
                }
                s_prevView = null;
            }
            catch { }
            try { return Run(data); }
            catch (Exception ex)
            {
                new TaskDialog("AB Bridges - error") { MainInstruction = ex.GetType().Name + ": " + ex.Message, ExpandedContent = ex.ToString() }.Show();
                return Result.Cancelled;
            }
        }

        private static ElementId s_prevView;     // the view before "Show in Revit"

        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private static double Ft(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);
        private static double M(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);
        private const string AxKey = "SUB|AX|", IdsKey = "SUB|IDS|", ExcelKey = "SUB|EXCEL", LastKey = "SUB|LAST", LinesKey = "SUB|DL|", PickViewKey = "SUB|PICKVIEW";

        // ---------------------------------------------------------------- picking an offset from a line

        private sealed class LineFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
        {
            public bool AllowElement(Element e) => e is ImportInstance || e is CurveElement || e is RevitLinkInstance;
            public bool AllowReference(Reference r, XYZ p) => true;
        }

        // pick a line (CAD link, model / detail line) in the chosen plan: where it crosses the axis -> the row's offset
        private static string DoPick(UIDocument uidoc, Document doc, Geo geo, BridgeExcel bx, Dictionary<string, SubAxis> axes, SubPick req, string viewName)
        {
            if (bx == null || !axes.TryGetValue(req.Axis, out SubAxis sa) || req.Row < 0 || req.Row >= sa.Rows.Count) return "Nothing to pick for.";
            AxisData a = bx.Axes.FirstOrDefault(x => x.Name == req.Axis);
            if (a == null) return "The axis is not in the Excel.";
            SubRow r = sa.Rows[req.Row];
            View v = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(x => !x.IsTemplate && x.Name == viewName);
            if (v != null && uidoc.ActiveView.Id != v.Id) uidoc.ActiveView = v;

            var offs = new List<double>();
            for (int i = 0; i < req.Picks; i++)
            {
                string prompt = req.Picks == 1 ? $"{req.Axis} - {req.Label}: pick the center line (Esc = cancel)"
                                               : $"{req.Axis} - {req.Label}: pick the line of the {(i == 0 ? "first" : "last")} one (Esc = cancel)";
                Reference rf;
                try { rf = uidoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.PointOnElement, new LineFilter(), prompt); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return "Pick cancelled - nothing changed."; }
                if (!TryLine(doc, rf, out XYZ p, out XYZ d)) return "That was not a line - nothing changed.";
                offs.Add(AxisOffset(geo, a, p, d));
            }
            var (L, R) = SubGeo.Edges(a);
            double c = SubGeo.Center(a);
            if (req.Picks == 1)
            {
                r.HOff = Math.Round(offs[0] - c, 3);
                return $"{req.Axis} - {req.Label}: from center = {r.HOff:0.000} m (picked in {v?.Name ?? "the active view"}).";
            }
            double lo = Math.Min(offs[0], offs[1]), hi = Math.Max(offs[0], offs[1]);
            r.Left = Math.Round(lo - L, 3);
            r.Right = Math.Round(R - hi, 3);
            r.Equal = true;
            return $"{req.Axis} - {req.Label}: from left edge = {r.Left:0.000} m, from right edge = {r.Right:0.000} m, equal spacing.";
        }

        // the picked point and the line's direction there (CAD: the line taken through the link's transform)
        private static bool TryLine(Document doc, Reference rf, out XYZ p, out XYZ d)
        {
            p = rf.GlobalPoint;
            d = null;
            Element e = doc.GetElement(rf);
            Curve c = null;
            if (e is CurveElement ce) c = ce.GeometryCurve;
            else
            {
                GeometryObject go = null;
                try { go = e.GetGeometryObjectFromReference(rf); } catch { }
                Transform t = (e as Instance)?.GetTotalTransform() ?? Transform.Identity;
                if (go is Curve gc)
                {
                    // the transformed curve if it passes nearer the picked point than the plain one
                    Curve tc = gc.CreateTransformed(t);
                    c = p == null || Dist(tc, p) <= Dist(gc, p) ? tc : gc;
                }
                else if (go is PolyLine pl)
                {
                    var pts = pl.GetCoordinates().Select(q => t.OfPoint(q)).ToList();
                    if (p != null && pts.Count >= 2)
                    {
                        int best = 0; double bd = double.MaxValue;
                        for (int i = 0; i + 1 < pts.Count; i++)
                        {
                            if (pts[i].DistanceTo(pts[i + 1]) < 1e-9) continue;
                            double dd = Line.CreateBound(pts[i], pts[i + 1]).Distance(p);
                            if (dd < bd) { bd = dd; best = i; }
                        }
                        d = (pts[best + 1] - pts[best]).Normalize();
                    }
                }
            }
            if (c != null)
            {
                if (p == null) p = c.Evaluate(0.5, true);
                try
                {
                    double prm = c.Project(p).Parameter;
                    d = c.ComputeDerivatives(prm, false).BasisX.Normalize();
                }
                catch { if (c is Line ln) d = ln.Direction; }
            }
            return p != null;
        }

        private static double Dist(Curve c, XYZ p)
        {
            try { return c.Distance(p); } catch { return double.MaxValue; }
        }

        // offset along the axis (from the alignment, m) where the picked line crosses it; parallel / no direction -> square projection
        private static double AxisOffset(Geo geo, AxisData a, XYZ p, XYZ d)
        {
            double len = Math.Sqrt(a.DirX * a.DirX + a.DirY * a.DirY);
            double ux = a.DirX / len, uy = a.DirY / len;
            var (pe, pn) = geo.SharedXY(p);
            double rx = pe - a.X, ry = pn - a.Y;
            if (d != null)
            {
                var (dx, dy) = geo.SharedDir(d);
                double cr = ux * dy - uy * dx;
                if (Math.Abs(cr) > 1e-6) return (rx * dy - ry * dx) / cr;
            }
            return rx * ux + ry * uy;
        }

        // what Substructure placed before is never taken for the deck
        private static HashSet<long> Mine(Dictionary<string, string> map) =>
            new HashSet<long>(map.Where(k => k.Key.StartsWith(IdsKey, StringComparison.Ordinal)).SelectMany(k => AbStore.Ids(k.Value)).Select(id => id.Value));

        private static void DeleteIds(Document doc, Dictionary<string, string> map, string key)
        {
            if (!map.TryGetValue(key, out string text)) return;
            foreach (ElementId id in AbStore.Ids(text))
            {
                if (doc.GetElement(id) == null) continue;
                try { doc.Delete(id); } catch { }
            }
            map.Remove(key);
        }

        // warnings (e.g. overlapping elements) do not stop the run
        // errors are resolved the way Revit proposes, and every message is kept for the report
        private static WarningSwallower Quiet(Transaction t)
        {
            var w = new WarningSwallower();
            FailureHandlingOptions fo = t.GetFailureHandlingOptions();
            fo.SetFailuresPreprocessor(w);
            fo.SetClearAfterRollback(true);
            t.SetFailureHandlingOptions(fo);
            return w;
        }

        // commit, and say in the log if Revit did not take it (with its messages)
        private static void CommitReport(Transaction t, WarningSwallower w, StringBuilder log)
        {
            TransactionStatus st;
            try { st = t.Commit(); }
            catch (Exception ex) { log.AppendLine("Revit did not accept the change: " + ex.Message); return; }
            if (st != TransactionStatus.Committed)
                log.AppendLine("Revit did not accept the change (nothing was placed)." + (w.Errors.Count > 0 ? " " + string.Join(" / ", w.Errors.Distinct()) : ""));
            else if (w.Errors.Count > 0)
                log.AppendLine("Revit messages: " + string.Join(" / ", w.Errors.Distinct()));
        }

        private sealed class WarningSwallower : IFailuresPreprocessor
        {
            public readonly List<string> Errors = new List<string>();

            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                bool resolved = false;
                foreach (FailureMessageAccessor f in fa.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning) { fa.DeleteWarning(f); continue; }
                    try { Errors.Add(f.GetDescriptionText()); } catch { }
                    if (f.HasResolutions())
                    {
                        try { fa.ResolveFailure(f); resolved = true; } catch { }
                    }
                }
                return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
            }
        }

        // the Design surface (4 points) and the NGL as detail lines in the section (temporary: removed by Create)
        private static void SurfaceLines(Document doc, ViewSection vs, Geo geo, AxisData a, Dictionary<string, string> map)
        {
            DeleteIds(doc, map, LinesKey + a.Name);
            double len = Math.Sqrt(a.DirX * a.DirX + a.DirY * a.DirY);
            if (len < 1e-9) return;
            double dx = a.DirX / len, dy = a.DirY / len;
            var ids = new List<ElementId>();
            void Draw(List<(string name, double off, double z)> pts, string style, byte r, byte g, byte b, string label)
            {
                var p = pts.Where(x => !double.IsNaN(x.z) && !double.IsNaN(x.off)).OrderBy(x => x.off).ToList();
                if (p.Count < 2) return;
                GraphicsStyle gs = LineStyle(doc, style, new Autodesk.Revit.DB.Color(r, g, b));
                for (int i = 0; i + 1 < p.Count; i++)
                {
                    XYZ q0 = geo.ToInternal(a.X + dx * p[i].off, a.Y + dy * p[i].off, p[i].z);
                    XYZ q1 = geo.ToInternal(a.X + dx * p[i + 1].off, a.Y + dy * p[i + 1].off, p[i + 1].z);
                    if (q0.DistanceTo(q1) < 1e-4) continue;
                    try
                    {
                        DetailCurve dc = doc.Create.NewDetailCurve(vs, Line.CreateBound(q0, q1));
                        if (gs != null) dc.LineStyle = gs;
                        ids.Add(dc.Id);
                    }
                    catch { }
                }
                try
                {
                    ElementId tt = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                    XYZ at = geo.ToInternal(a.X + dx * p[0].off, a.Y + dy * p[0].off, p[0].z + 0.4);
                    if (tt != null && tt != ElementId.InvalidElementId) ids.Add(TextNote.Create(doc, vs.Id, at, label, tt).Id);
                }
                catch { }
            }
            try { Sand(doc, vs, geo, a, dx, dy, ids); } catch { }        // first: the lines / texts made after it are drawn over it
            Draw(a.Design, "AB Design surface", 37, 99, 235, "Design surface");
            Draw(a.Ngl, "AB NGL", 133, 79, 11, "NGL");
            try { SectionDims(doc, vs, geo, a, dx, dy, ids); } catch { }
            map[LinesKey + a.Name] = AbStore.Ids(ids);
        }

        // the natural ground under the NGL: desert yellow (light, like 35 % on white), with holes where the
        // elements are cut (piles, pile caps ... stay visible: a filled region is drawn over the model)
        private static void Sand(Document doc, ViewSection vs, Geo geo, AxisData a, double dx, double dy, List<ElementId> ids)
        {
            var p = a.Ngl.Where(x => !double.IsNaN(x.z) && !double.IsNaN(x.off)).OrderBy(x => x.off).ToList();
            if (p.Count < 2) return;
            double depth = 6.0;                                              // m under the lowest NGL point
            double zb = p.Min(x => x.z) - depth;
            XYZ At(double off, double z) => geo.ToInternal(a.X + dx * off, a.Y + dy * off, z);
            var pts = p.Select(x => At(x.off, x.z)).ToList();
            pts.Add(At(p[p.Count - 1].off, zb)); pts.Add(At(p[0].off, zb));
            var outer = new CurveLoop();
            for (int i = 0; i < pts.Count; i++) { XYZ q0 = pts[i], q1 = pts[(i + 1) % pts.Count]; if (q0.DistanceTo(q1) > 1e-4) outer.Append(Line.CreateBound(q0, q1)); }

            // the ground as a thin solid in the view plane, minus the cut elements
            XYZ u = geo.Vec(dx, dy), nrm = XYZ.BasisZ.CrossProduct(u).Normalize();
            double th = 0.01;
            Solid ground = GeometryCreationUtilities.CreateExtrudedGeometry(new[] { outer }, nrm, th);
            XYZ p0 = geo.ToInternal(a.X, a.Y, 0);
            Plane pl = Plane.CreateByNormalAndOrigin(nrm, p0);
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            long topo1 = (long)BuiltInCategory.OST_Topography, topo2 = (long)BuiltInCategory.OST_Toposolid;
            foreach (Element e in new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType().ToElements())
            {
                try
                {
                    Category c = e.Category;
                    if (c == null || c.CategoryType != CategoryType.Model || !(e is FamilyInstance)) continue;
                    if (c.Id.Value == topo1 || c.Id.Value == topo2) continue;
                    foreach (var loop in Cut(e, opt, pl, p0, u, nrm, geo))
                    {
                        if (loop.Count < 3) continue;
                        var cl = new CurveLoop();
                        var lp = loop.Select(q => At(q.off, q.z)).ToList();
                        for (int i = 0; i < lp.Count; i++) { XYZ q0 = lp[i], q1 = lp[(i + 1) % lp.Count]; if (q0.DistanceTo(q1) > 1e-4) cl.Append(Line.CreateBound(q0, q1)); }
                        try
                        {
                            Solid hole = GeometryCreationUtilities.CreateExtrudedGeometry(new[] { cl }, nrm, th);
                            ground = BooleanOperationsUtils.ExecuteBooleanOperation(ground, hole, BooleanOperationsType.Difference);
                        }
                        catch { }
                    }
                }
                catch { }
            }
            // the face of the solid in the view plane -> the loops of the filled region
            PlanarFace face = ground.Faces.OfType<PlanarFace>().FirstOrDefault(fc => fc.FaceNormal.IsAlmostEqualTo(nrm.Negate()) || fc.FaceNormal.IsAlmostEqualTo(nrm));
            if (face == null) return;
            IList<CurveLoop> loops = face.GetEdgesAsCurveLoops();
            XYZ back = face.Origin.Subtract(p0).DotProduct(nrm) * nrm;          // onto the section plane
            var flat = loops.Select(l => CurveLoop.CreateViaTransform(l, Transform.CreateTranslation(back.Negate()))).ToList();
            FilledRegionType frt = SandType(doc);
            if (frt == null) return;
            ids.Add(FilledRegion.Create(doc, frt.Id, vs.Id, flat).Id);
        }

        // "AB Sand": solid fill, desert yellow (239,159,39 at 35 % on white), no outline weight change
        private static FilledRegionType SandType(Document doc)
        {
            const string name = "AB Sand";
            var all = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
            FilledRegionType t = all.FirstOrDefault(x => x.Name == name);
            if (t != null) return t;
            FilledRegionType basis = all.FirstOrDefault();
            FillPatternElement solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
            if (basis == null || solid == null) return basis;
            t = basis.Duplicate(name) as FilledRegionType;
            t.ForegroundPatternId = solid.Id;
            t.ForegroundPatternColor = new Autodesk.Revit.DB.Color(250, 227, 190);
            t.BackgroundPatternId = ElementId.InvalidElementId;
            t.IsMasking = false;
            return t;
        }

        // dimensions of every element cut at the axis: width under it, height beside it, top level above it
        private static void SectionDims(Document doc, ViewSection vs, Geo geo, AxisData a, double dx, double dy, List<ElementId> ids)
        {
            ElementId tt = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (tt == null || tt == ElementId.InvalidElementId) return;
            XYZ p0 = geo.ToInternal(a.X, a.Y, 0);
            XYZ u = geo.Vec(dx, dy);
            XYZ nrm = XYZ.BasisZ.CrossProduct(u).Normalize();
            Plane pl = Plane.CreateByNormalAndOrigin(nrm, p0);
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            long topo1 = (long)BuiltInCategory.OST_Topography, topo2 = (long)BuiltInCategory.OST_Toposolid;
            GraphicsStyle gs = LineStyle(doc, "AB Dimension", new Autodesk.Revit.DB.Color(153, 60, 29));
            XYZ At(double off, double z) => geo.ToInternal(a.X + dx * off, a.Y + dy * off, z);
            void Seg(double o1, double z1, double o2, double z2)
            {
                XYZ q0 = At(o1, z1), q1 = At(o2, z2);
                if (q0.DistanceTo(q1) < 1e-3) return;
                try { DetailCurve dc = doc.Create.NewDetailCurve(vs, Line.CreateBound(q0, q1)); if (gs != null) dc.LineStyle = gs; ids.Add(dc.Id); } catch { }
            }
            void Txt(double off, double z, string t)
            {
                try { ids.Add(TextNote.Create(doc, vs.Id, At(off, z), t, new TextNoteOptions(tt) { HorizontalAlignment = HorizontalTextAlignment.Center }).Id); } catch { }
            }
            IEnumerable<Element> els = new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType().ToElements();
            foreach (Element e in els)
            {
                try
                {
                    Category c = e.Category;
                    if (c == null || c.CategoryType != CategoryType.Model || !(e is FamilyInstance)) continue;
                    if (c.Id.Value == topo1 || c.Id.Value == topo2) continue;
                    var loops = Cut(e, opt, pl, p0, u, nrm, geo);
                    if (loops.Count == 0) continue;
                    double o1 = loops.Min(l => l.Min(q => q.off)), o2 = loops.Max(l => l.Max(q => q.off));
                    double z1 = loops.Min(l => l.Min(q => q.z)), z2 = loops.Max(l => l.Max(q => q.z));
                    if (o2 - o1 < 0.05 || o2 - o1 > 60 || z2 - z1 < 0.05) continue;
                    double y = z1 - 0.35;
                    Seg(o1, y, o2, y); Seg(o1, y - 0.12, o1, y + 0.12); Seg(o2, y - 0.12, o2, y + 0.12);
                    Txt((o1 + o2) / 2, y - 0.05, (o2 - o1).ToString("0.00", CI));
                    double x = o2 + 0.35;
                    Seg(x, z1, x, z2); Seg(x - 0.12, z1, x + 0.12, z1); Seg(x - 0.12, z2, x + 0.12, z2);
                    Txt(x + 0.6, (z1 + z2) / 2, (z2 - z1).ToString("0.00", CI));
                    Txt(o1, z2 + 0.45, "+" + z2.ToString("0.00", CI));
                }
                catch { }
            }
        }

        // the look of the section, like the drawings: black lines, the cut pattern of each material (concrete hatch ...),
        // no colours, no crop frame, fine detail, hidden lines off
        private static void StyleSection(Document doc, ViewSection vs, Dictionary<string, string> map)
        {
            try
            {
                vs.DetailLevel = ViewDetailLevel.Fine;
                try { vs.DisplayStyle = DisplayStyle.HLR; } catch { }
                try { vs.CropBoxVisible = false; } catch { }
                FillPatternElement solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
                HashSet<long> mine = Mine(map);
                OverrideGraphicSettings Ogs(byte fr, byte fg, byte fb, byte lr, byte lg, byte lb, int w)
                {
                    var o = new OverrideGraphicSettings();
                    var line = new Autodesk.Revit.DB.Color(lr, lg, lb);
                    o.SetCutLineColor(line);
                    o.SetCutLineWeight(w);
                    o.SetProjectionLineColor(line);
                    if (solid != null)
                    {
                        o.SetCutForegroundPatternId(solid.Id);
                        o.SetCutForegroundPatternColor(new Autodesk.Revit.DB.Color(fr, fg, fb));
                        o.SetCutForegroundPatternVisible(true);
                    }
                    return o;
                }
                // black lines only (the material's own cut pattern stays); what Substructure placed a bit heavier
                OverrideGraphicSettings Black(int w)
                {
                    var o = new OverrideGraphicSettings();
                    var k = new Autodesk.Revit.DB.Color(0, 0, 0);
                    o.SetCutLineColor(k); o.SetCutLineWeight(w); o.SetProjectionLineColor(k);
                    return o;
                }
                OverrideGraphicSettings sub = Black(4), other = Black(3);
                foreach (Element e in new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType().ToElements())
                {
                    try
                    {
                        Category c = e.Category;
                        if (c == null || c.CategoryType != CategoryType.Model) continue;
                        vs.SetElementOverrides(e.Id, mine.Contains(e.Id.Value) ? sub : other);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private const string PlanLinesKey = "SUB|PL|";

        // a plan at the axis, looking down on everything (the cut plane high above), cropped around the axis
        private static ViewPlan AxisPlan(Document doc, Geo geo, AxisData a)
        {
            string name = "AB Plan - " + a.Name;
            ViewPlan old = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            if (old != null) return old;
            ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan) ??
                new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.StructuralPlan);
            Level lv = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).FirstOrDefault();
            if (vft == null || lv == null) return null;
            ViewPlan vp = ViewPlan.Create(doc, vft.Id, lv.Id);
            try { vp.Name = name; } catch { }
            try
            {
                PlanViewRange vr = vp.GetViewRange();
                vr.SetLevelId(PlanViewPlane.TopClipPlane, lv.Id);
                vr.SetLevelId(PlanViewPlane.CutPlane, lv.Id);
                vr.SetLevelId(PlanViewPlane.BottomClipPlane, lv.Id);
                vr.SetLevelId(PlanViewPlane.ViewDepthPlane, lv.Id);
                vr.SetOffset(PlanViewPlane.TopClipPlane, Ft(1000));
                vr.SetOffset(PlanViewPlane.CutPlane, Ft(900));
                vr.SetOffset(PlanViewPlane.BottomClipPlane, Ft(-500));
                vr.SetOffset(PlanViewPlane.ViewDepthPlane, Ft(-500));
                vp.SetViewRange(vr);
            }
            catch { }
            try { vp.Scale = 200; } catch { }
            try { vp.DetailLevel = ViewDetailLevel.Fine; } catch { }
            // crop: a square around the deck at the axis
            try
            {
                double len = Math.Sqrt(a.DirX * a.DirX + a.DirY * a.DirY);
                var (L, R) = SubGeo.Edges(a);
                double cOff = (L + R) / 2;
                XYZ c = geo.ToInternal(a.X + a.DirX / len * cOff, a.Y + a.DirY / len * cOff, 0);
                double h = Ft(Math.Max(15, (R - L) / 2 + 8));
                vp.CropBoxActive = true;
                BoundingBoxXYZ cb = vp.CropBox;
                cb.Min = new XYZ(c.X - h, c.Y - h, cb.Min.Z);
                cb.Max = new XYZ(c.X + h, c.Y + h, cb.Max.Z);
                vp.CropBox = cb;
                vp.CropBoxVisible = false;
            }
            catch { }
            return vp;
        }

        // the axis line in the plan (3 m past each deck edge) with its name
        private static void PlanLines(Document doc, ViewPlan vp, Geo geo, AxisData a, Dictionary<string, string> map)
        {
            DeleteIds(doc, map, PlanLinesKey + a.Name);
            double len = Math.Sqrt(a.DirX * a.DirX + a.DirY * a.DirY);
            if (len < 1e-9) return;
            double dx = a.DirX / len, dy = a.DirY / len;
            var (L, R) = SubGeo.Edges(a);
            double z = vp.GenLevel != null ? vp.GenLevel.ProjectElevation : 0;
            try { if (vp.SketchPlane != null) z = vp.SketchPlane.GetPlane().Origin.Z; } catch { }
            XYZ q0 = geo.ToInternal(a.X + dx * (L - 3), a.Y + dy * (L - 3), 0), q1 = geo.ToInternal(a.X + dx * (R + 3), a.Y + dy * (R + 3), 0);
            q0 = new XYZ(q0.X, q0.Y, z);
            q1 = new XYZ(q1.X, q1.Y, z);
            var ids = new List<ElementId>();
            try
            {
                DetailCurve dc = doc.Create.NewDetailCurve(vp, Line.CreateBound(q0, q1));
                GraphicsStyle gs = LineStyle(doc, "AB Axis", new Autodesk.Revit.DB.Color(220, 38, 38));
                if (gs != null) dc.LineStyle = gs;
                ids.Add(dc.Id);
            }
            catch { }
            try
            {
                ElementId tt = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                if (tt != null && tt != ElementId.InvalidElementId) ids.Add(TextNote.Create(doc, vp.Id, q1, a.Name, tt).Id);
            }
            catch { }
            map[PlanLinesKey + a.Name] = AbStore.Ids(ids);
        }

        // the plan shows everything (what was hidden before is shown again); what Substructure placed in orange
        private static void StylePlan(Document doc, ViewPlan vp, Dictionary<string, string> map)
        {
            try
            {
                HashSet<long> mine = Mine(map);
                FillPatternElement solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
                var o = new OverrideGraphicSettings();
                o.SetProjectionLineColor(new Autodesk.Revit.DB.Color(194, 65, 12));
                o.SetProjectionLineWeight(3);
                if (solid != null)
                {
                    o.SetSurfaceForegroundPatternId(solid.Id);
                    o.SetSurfaceForegroundPatternColor(new Autodesk.Revit.DB.Color(253, 220, 196));
                    o.SetSurfaceForegroundPatternVisible(true);
                }
                var unhide = new List<ElementId>();
                // only around the plan's crop (the whole model can be big)
                BoundingBoxXYZ cb = vp.CropBox;
                var near = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                if (cb != null)
                    near = near.WherePasses(new BoundingBoxIntersectsFilter(new Outline(new XYZ(cb.Min.X, cb.Min.Y, -Ft(2000)), new XYZ(cb.Max.X, cb.Max.Y, Ft(2000)))));
                foreach (Element e in near.ToElements())
                {
                    try
                    {
                        Category c = e.Category;
                        if (c == null || c.CategoryType != CategoryType.Model) continue;
                        if (e.IsHidden(vp)) unhide.Add(e.Id);
                    }
                    catch { }
                }
                if (unhide.Count > 0) vp.UnhideElements(unhide);
                foreach (long v in mine)
                {
                    try { if (doc.GetElement(new ElementId(v)) != null) vp.SetElementOverrides(new ElementId(v), o); } catch { }
                }
            }
            catch { }
        }

        // plan snapping: the corners of what Substructure placed and its location points, and the axis line
        private static void SnapDataPlan(Document doc, ViewPlan vp, Dictionary<string, string> map, List<XYZ> pts, List<(XYZ a, XYZ b)> segs)
        {
            try
            {
                foreach (long v in Mine(map))
                {
                    Element e = doc.GetElement(new ElementId(v));
                    if (e == null) continue;
                    if (e.Location is LocationPoint lp) pts.Add(lp.Point);
                    BoundingBoxXYZ bb = e.get_BoundingBox(vp);
                    if (bb == null) continue;
                    var c = new[] { new XYZ(bb.Min.X, bb.Min.Y, 0), new XYZ(bb.Max.X, bb.Min.Y, 0), new XYZ(bb.Max.X, bb.Max.Y, 0), new XYZ(bb.Min.X, bb.Max.Y, 0) };
                    for (int i = 0; i < 4; i++) { pts.Add(c[i]); segs.Add((c[i], c[(i + 1) % 4])); }
                }
                if (map.TryGetValue(PlanLinesKey + vp.Name.Replace("AB Plan - ", ""), out string text))
                    foreach (ElementId id in AbStore.Ids(text))
                        if (doc.GetElement(id) is CurveElement ce && ce.GeometryCurve is Curve cv && cv.IsBound)
                        {
                            pts.Add(cv.GetEndPoint(0)); pts.Add(cv.GetEndPoint(1));
                            segs.Add((cv.GetEndPoint(0), cv.GetEndPoint(1)));
                        }
            }
            catch { }
        }

        // a line style (sub category of Lines), made once
        internal static GraphicsStyle LineStyle(Document doc, string name, Autodesk.Revit.DB.Color color)
        {
            try
            {
                Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                Category sub = lines.SubCategories.Contains(name) ? lines.SubCategories.get_Item(name) : doc.Settings.Categories.NewSubcategory(lines, name);
                sub.LineColor = color;
                sub.SetLineWeight(3, GraphicsStyleType.Projection);
                return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
            }
            catch { return null; }
        }

        private Result Run(ExternalCommandData data)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("AB Bridges", "Open the bridge project first (Substructure works in a project).");
                return Result.Cancelled;
            }

            Dictionary<string, string> map = AbStore.Load(doc);
            var saved = new Dictionary<string, SubAxis>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in map.Where(k => k.Key.StartsWith(AxKey, StringComparison.Ordinal)))
            {
                string name = kv.Key.Substring(AxKey.Length);
                saved[name] = SubAxis.Decode(name, kv.Value);
            }
            string excel = map.TryGetValue(ExcelKey, out string ex0) && System.IO.File.Exists(ex0) ? ex0 : CreateForm.LastExcel;

            Dictionary<string, FamilySymbol> syms = Families(doc);
            var geo = new Geo(doc);
            List<FamilyInstance> decks = Decks(doc);

            var paramCache = new Dictionary<string, List<SubParam>>(StringComparer.Ordinal);
            Func<string, List<SubParam>> paramsOf = key =>
            {
                if (string.IsNullOrEmpty(key) || !syms.TryGetValue(key, out FamilySymbol sym)) return new List<SubParam>();
                if (!paramCache.TryGetValue(key, out List<SubParam> list))
                {
                    list = ReadParams(doc, sym);
                    paramCache[key] = list;
                }
                return list;
            };
            var boxCache = new Dictionary<string, (double, double, double, double)?>(StringComparer.Ordinal);
            Func<string, (double x0, double x1, double z0, double z1)?> boxOf = key =>
            {
                if (string.IsNullOrEmpty(key) || !syms.TryGetValue(key, out FamilySymbol sym)) return null;
                if (!boxCache.TryGetValue(key, out var b)) { b = Box(sym); boxCache[key] = b; }
                return b;
            };
            // what Substructure placed before is never taken for the deck
            Func<AxisData, SubSection> sectionOf = a => DeckSection(doc, geo, decks, a, Mine(map));

            // the real section at an axis, shown in the dialog (made / reused, with the Design and NGL lines)
            SubstructureForm formRef = null;
            Func<AxisData, string, System.Windows.Forms.Control> previewOf = (a, mode) =>
            {
                var spts = new List<XYZ>();
                var ssegs = new List<(XYZ a, XYZ b)>();
                if (mode == "plan")
                {
                    ViewPlan vp;
                    using (var t = new Transaction(doc, "AB Bridges - plan at " + a.Name))
                    {
                        t.Start();
                        Quiet(t);
                        vp = AxisPlan(doc, geo, a);
                        if (vp != null) { PlanLines(doc, vp, geo, a, map); StylePlan(doc, vp, map); }
                        AbStore.Save(doc, map);
                        t.Commit();
                    }
                    if (vp == null) return null;
                    SnapDataPlan(doc, vp, map, spts, ssegs);
                    return new PreviewHost(doc, vp.Id, spts, ssegs);
                }
                ViewSection vs;
                var plog = new StringBuilder();
                using (var t = new Transaction(doc, "AB Bridges - section at " + a.Name))
                {
                    t.Start();
                    Quiet(t);
                    vs = AxisSection(doc, geo, a, sectionOf(a), plog, true);
                    if (vs != null) { SurfaceLines(doc, vs, geo, a, map); StyleSection(doc, vs, map); }
                    AbStore.Save(doc, map);
                    t.Commit();
                }
                if (vs == null) return null;
                SnapData(doc, vs, geo, a, spts, ssegs);
                return new PreviewHost(doc, vs.Id, spts, ssegs);
            };
            // Apply: place the families of one axis now (the dialog stays open)
            Func<string, string> applyAxis = name =>
            {
                if (formRef?.Excel == null || !formRef.Axes.TryGetValue(name, out SubAxis ax)) return "";
                AxisData a = formRef.Excel.Axes.FirstOrDefault(x => x.Name == name);
                if (a == null) return "";
                foreach (SubRow r in ax.Rows.Where(r => !string.IsNullOrEmpty(r.Family))) paramsOf(r.Family);
                var one = new Dictionary<string, SubAxis>(StringComparer.OrdinalIgnoreCase) { [name] = new SubAxis { Name = name, Create = true, Rows = ax.Rows } };
                var alog = new StringBuilder();
                using (var t = new Transaction(doc, "AB Bridges - Substructure " + name))
                {
                    t.Start();
                    WarningSwallower w = Quiet(t);
                    Create(doc, geo, syms, sectionOf, paramsOf, formRef.Excel, one, map, alog, false);
                    foreach (SubAxis sx in formRef.Axes.Values) map[AxKey + sx.Name] = sx.Encode();
                    map[LastKey] = name;
                    if (!string.IsNullOrEmpty(formRef.ExcelPath)) map[ExcelKey] = formRef.ExcelPath;
                    AbStore.Save(doc, map);
                    CommitReport(t, w, alog);
                }
                return alog.ToString().Trim();
            };

            // the section's depth (far clip) and crop size: null -> read; {depth, width, height} (NaN = keep) -> set
            Func<AxisData, string, double[], double[]> sectionSize = (a, mode, v) =>
            {
                string nm = (mode == "plan" ? "AB Plan - " : "AB Section - ") + a.Name;
                View vs = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(x => !x.IsTemplate && x.Name == nm && (mode == "plan" ? x is ViewPlan : x is ViewSection));
                if (vs == null) return null;
                Parameter far = mode == "plan" ? null : vs.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                if (v != null && v.Length >= 3)
                {
                    using (var t = new Transaction(doc, "AB Bridges - section size " + a.Name))
                    {
                        t.Start();
                        Quiet(t);
                        if (!double.IsNaN(v[0]) && v[0] > 0 && far != null)
                        {
                            try { vs.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING)?.Set(1); } catch { }
                            far.Set(Ft(v[0]));
                        }
                        if ((!double.IsNaN(v[1]) && v[1] > 0) || (!double.IsNaN(v[2]) && v[2] > 0))
                        {
                            vs.CropBoxActive = true;
                            BoundingBoxXYZ cb = vs.CropBox;
                            double cx = (cb.Min.X + cb.Max.X) / 2, cy = (cb.Min.Y + cb.Max.Y) / 2;
                            double hw = !double.IsNaN(v[1]) && v[1] > 0 ? Ft(v[1]) / 2 : (cb.Max.X - cb.Min.X) / 2;
                            double hh = !double.IsNaN(v[2]) && v[2] > 0 ? Ft(v[2]) / 2 : (cb.Max.Y - cb.Min.Y) / 2;
                            cb.Min = new XYZ(cx - hw, cy - hh, cb.Min.Z);
                            cb.Max = new XYZ(cx + hw, cy + hh, cb.Max.Z);
                            vs.CropBox = cb;
                        }
                        t.Commit();
                    }
                }
                BoundingBoxXYZ c2 = vs.CropBox;
                return new[] { far != null ? M(far.AsDouble()) : double.NaN, M(c2.Max.X - c2.Min.X), M(c2.Max.Y - c2.Min.Y) };
            };

            // the plan views a line can be picked in (from a linked CAD or drawn lines)
            List<string> planViews = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate).Select(v => v.Name).Distinct().OrderBy(n => n).ToList();
            string pickView = map.TryGetValue(PickViewKey, out string pv0) && planViews.Contains(pv0) ? pv0
                            : planViews.FirstOrDefault(n => string.Equals(n, "Level 1", StringComparison.OrdinalIgnoreCase)) ?? planViews.FirstOrDefault();
            string startAxis = map.TryGetValue(LastKey, out string last) ? last : null;
            int startRow = -1;
            string pickNote = null;
            var axesNow = saved;
            string excelNow = excel;

            // the dialog closes for a pick (a line in a plan) and opens again where it was
            while (true)
            using (var form = new SubstructureForm(syms.Keys.ToList(), axesNow, excelNow, paramsOf, boxOf, sectionOf, previewOf, applyAxis, sectionSize))
            {
                formRef = form;
                form.LongOf = bxl => BuildLong(doc, geo, bxl, Decks(doc));
                form.LoadFamilies = files =>
                {
                    using (var t = new Transaction(doc, "AB Bridges - load families"))
                    {
                        t.Start();
                        var opts = new AskLoadOpts();
                        foreach (string f in files)
                        {
                            try { doc.LoadFamily(f, opts, out Family _); } catch { }
                        }
                        t.Commit();
                    }
                    Dictionary<string, FamilySymbol> fresh = Families(doc);
                    syms.Clear();
                    foreach (var kv in fresh) syms[kv.Key] = kv.Value;
                    return syms.Keys.ToList();
                };
                form.StartAxis = startAxis;
                form.StartRow = startRow;
                form.PlanViews = planViews;
                form.PickView = pickView;
                form.Note = pickNote;
                if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;

                if (form.Pick != null)
                {
                    axesNow = form.Axes;
                    excelNow = form.ExcelPath;
                    startAxis = form.Pick.Axis;
                    startRow = form.Pick.Row;
                    pickView = form.PickView;
                    try { pickNote = DoPick(uidoc, doc, geo, form.Excel, form.Axes, form.Pick, pickView); }
                    catch (Exception ex) { pickNote = "The pick did not work: " + ex.Message; }
                    // keep the settings in the project
                    using (var t = new Transaction(doc, "AB Bridges - Substructure settings"))
                    {
                        t.Start();
                        Quiet(t);
                        foreach (SubAxis sx in form.Axes.Values) map[AxKey + sx.Name] = sx.Encode();
                        if (!string.IsNullOrEmpty(form.ExcelPath)) map[ExcelKey] = form.ExcelPath;
                        if (!string.IsNullOrEmpty(pickView)) map[PickViewKey] = pickView;
                        map[LastKey] = startAxis;
                        AbStore.Save(doc, map);
                        t.Commit();
                    }
                    continue;
                }

                // parameters of the families used (read before the transaction: reading opens the family)
                if (form.CreateNow)
                    foreach (SubAxis s in form.Axes.Values.Where(x => x.Create))
                        foreach (SubRow r in s.Rows.Where(r => !string.IsNullOrEmpty(r.Family)))
                            paramsOf(r.Family);

                var log = new StringBuilder();
                ViewSection open = null;
                View openView = null;
                using (var t = new Transaction(doc, form.CreateNow ? "AB Bridges - Substructure" : form.SectionAxis != null ? "AB Bridges - section at " + form.SectionAxis : "AB Bridges - Substructure settings"))
                {
                    t.Start();
                    WarningSwallower w = Quiet(t);
                    if (form.SectionAxis != null && form.Excel != null)
                    {
                        AxisData sa = form.Excel.Axes.FirstOrDefault(x => x.Name == form.SectionAxis);
                        if (sa != null && form.SectionMode == "plan")
                        {
                            ViewPlan pv = AxisPlan(doc, geo, sa);
                            if (pv != null) { PlanLines(doc, pv, geo, sa, map); StylePlan(doc, pv, map); openView = pv; }
                        }
                        else
                        {
                            if (sa != null) open = AxisSection(doc, geo, sa, sectionOf(sa), log, true);
                            if (open != null) { SurfaceLines(doc, open, geo, sa, map); StyleSection(doc, open, map); openView = open; }
                        }
                        map[LastKey] = form.SectionAxis;
                    }
                    if (form.CreateNow && form.Excel != null)
                        Create(doc, geo, syms, sectionOf, paramsOf, form.Excel, form.Axes, map, log, true);
                    foreach (SubAxis s in form.Axes.Values) map[AxKey + s.Name] = s.Encode();
                    if (!string.IsNullOrEmpty(form.ExcelPath)) map[ExcelKey] = form.ExcelPath;
                    if (!AbStore.Save(doc, map)) log.AppendLine("Note: the settings could not be kept in the project.");
                    CommitReport(t, w, log);
                }
                if (openView != null)
                {
                    try { if (uidoc.ActiveView != null && !uidoc.ActiveView.Name.StartsWith("AB ")) s_prevView = uidoc.ActiveView.Id; uidoc.ActiveView = openView; }
                    catch (Exception ex) { log.AppendLine("The section was made but could not be opened: " + ex.Message); }
                }
                if (form.CreateNow || (form.SectionAxis != null && openView == null) || (log.Length > 0 && !form.CreateNow))
                    new TaskDialog("AB Bridges") { MainInstruction = "Substructure", MainContent = log.ToString() }.Show();
                return Result.Succeeded;
            }
        }

        // ---------------------------------------------------------------- families

        // every family type of the project that makes model geometry (any category, any placement): "Family : Type"
        // a family already in the project: ask once whether to overwrite it (and its parameter values)
        private sealed class AskLoadOpts : IFamilyLoadOptions
        {
            private bool? _answer;
            private bool Ask()
            {
                if (_answer == null)
                    _answer = System.Windows.Forms.MessageBox.Show("Some families are already in the project.\n\nOverwrite them with the files?",
                        "AB Bridges - load families", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question)
                        == System.Windows.Forms.DialogResult.Yes;
                return _answer.Value;
            }
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { bool y = Ask(); overwriteParameterValues = y; return y; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { bool y = Ask(); source = FamilySource.Family; overwriteParameterValues = y; return y; }
        }

        private static Dictionary<string, FamilySymbol> Families(Document doc)
        {
            var res = new SortedDictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilySymbol s in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
            {
                try
                {
                    Family f = s.Family;
                    if (f == null || f.IsInPlace) continue;
                    if (s.Category != null && s.Category.CategoryType == CategoryType.Annotation) continue;
                    if (!string.IsNullOrEmpty(s.LookupParameter(DeckBuilder.OriginParam)?.AsString())) continue;   // the deck
                    string key = f.Name + " : " + s.Name;
                    if (!res.ContainsKey(key)) res[key] = s;
                }
                catch { }
            }
            return new Dictionary<string, FamilySymbol>(res, StringComparer.Ordinal);
        }

        private static List<SubParam> ReadParams(Document doc, FamilySymbol sym)
        {
            var list = new List<SubParam>();
            try
            {
                FamInfo info = FamilyReader.Read(doc, sym.Family);
                foreach (FamParam p in info.All.Where(p => !p.HasFormula && !p.IsReporting))
                {
                    string def;
                    if (p.IsInstance) def = p.Value is double d ? d.ToString(CI) : Convert.ToString(p.Value, CI) ?? "";
                    else def = Text(sym.LookupParameter(p.Name), p.Unit, p.IsYesNo) ?? (p.Value is double d2 ? d2.ToString(CI) : Convert.ToString(p.Value, CI) ?? "");
                    list.Add(new SubParam { Name = p.Name, Unit = p.Unit ?? "", IsInstance = p.IsInstance, IsYesNo = p.IsYesNo, Default = def });
                }
            }
            catch { }
            return list;
        }

        private static string Text(Parameter p, string unit, bool yesNo)
        {
            if (p == null || !p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.Double: return Math.Round(FromInternal(p.AsDouble(), unit), 6).ToString(CI);
                case StorageType.Integer: return yesNo ? (p.AsInteger() != 0 ? "☑" : "☐") : p.AsInteger().ToString(CI);
                case StorageType.String: return p.AsString() ?? "";
                default: return null;
            }
        }

        private static double ToInternal(double v, string unit)
        {
            switch (unit)
            {
                case "m": return UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Meters);
                case "°": return UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Degrees);
                case "m²": return UnitUtils.ConvertToInternalUnits(v, UnitTypeId.SquareMeters);
                case "m³": return UnitUtils.ConvertToInternalUnits(v, UnitTypeId.CubicMeters);
                case "%": return UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Percentage);
                default: return v;
            }
        }

        private static double FromInternal(double v, string unit)
        {
            switch (unit)
            {
                case "m": return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Meters);
                case "°": return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Degrees);
                case "m²": return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.SquareMeters);
                case "m³": return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.CubicMeters);
                case "%": return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Percentage);
                default: return v;
            }
        }

        private static bool SetValue(Parameter p, string text, string unit)
        {
            if (p == null || p.IsReadOnly || text == null) return false;
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.Double:
                        if (!double.TryParse(text, NumberStyles.Float, CI, out double d)) return false;
                        return p.Set(ToInternal(d, unit));
                    case StorageType.Integer:
                        if (text == "☑") return p.Set(1);
                        if (text == "☐") return p.Set(0);
                        if (double.TryParse(text, NumberStyles.Float, CI, out double iv)) return p.Set((int)Math.Round(iv));
                        return false;
                    case StorageType.String:
                        return p.Set(text);
                }
            }
            catch { }
            return false;
        }

        // size of a family type around its insertion point (m): along its X and in Z
        private static (double x0, double x1, double z0, double z1)? Box(FamilySymbol sym)
        {
            try
            {
                GeometryElement ge = sym.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Coarse });
                BoundingBoxXYZ bb = ge?.GetBoundingBox();
                if (bb == null) return null;
                XYZ a = bb.Transform.OfPoint(bb.Min), b = bb.Transform.OfPoint(bb.Max);
                if (a.DistanceTo(b) > Ft(1000)) return null;
                double z0 = M(Math.Min(a.Z, b.Z)), z1 = M(Math.Max(a.Z, b.Z));
                if (sym.Family.FamilyPlacementType == FamilyPlacementType.TwoLevelsBased) { z0 = -(z1 - z0); z1 = 0; }   // a column hangs from its top
                return (M(Math.Min(a.X, b.X)), M(Math.Max(a.X, b.X)), z0, z1);
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- deck section

        private static List<FamilyInstance> Decks(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(fi => !string.IsNullOrEmpty(fi.Symbol?.LookupParameter(DeckBuilder.OriginParam)?.AsString())).ToList();

        private static IEnumerable<Solid> Solids(GeometryElement ge)
        {
            if (ge == null) yield break;
            foreach (GeometryObject o in ge)
            {
                if (o is Solid s && s.Volume > 1e-9) yield return s;
                else if (o is GeometryInstance gi)
                    foreach (Solid s2 in Solids(gi.GetInstanceGeometry())) yield return s2;
            }
        }

        // the deck cut by the vertical plane of the axis: loops of (offset from the alignment, shared Z).
        // The AB deck family if there is one; otherwise every model element crossing the axis that reaches up to
        // the road (within 0.5 m of the design surface) - so piers, pile caps and the like under the deck are left out.
        private static SubSection DeckSection(Document doc, Geo geo, List<FamilyInstance> decks, AxisData a, HashSet<long> exclude)
        {
            double len = Math.Sqrt(a.DirX * a.DirX + a.DirY * a.DirY);
            if (len < 1e-9) return null;
            double dx = a.DirX / len, dy = a.DirY / len;
            XYZ p0 = geo.ToInternal(a.X, a.Y, 0);
            XYZ u = geo.Vec(dx, dy);
            XYZ nrm = XYZ.BasisZ.CrossProduct(u).Normalize();
            Plane pl = Plane.CreateByNormalAndOrigin(nrm, p0);
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            var sec = new SubSection();

            if (decks.Count > 0)
            {
                foreach (FamilyInstance inst in decks)
                    sec.Loops.AddRange(Cut(inst, opt, pl, p0, u, nrm, geo));
                return sec.Loops.Count > 0 ? sec : null;
            }

            // no AB deck: the elements around the axis
            var dz = a.Design.Where(p => !double.IsNaN(p.z)).Select(p => p.z).ToList();
            if (dz.Count == 0) return null;
            double road = dz.Min() - 0.5;
            var (L, R) = SubGeo.Edges(a);
            var corners = new List<XYZ>();
            foreach (double off in new[] { L - 2, R + 2 })
                foreach (double side in new[] { -0.5, 0.5 })
                    foreach (double z in new[] { dz.Min() - 8, dz.Max() + 2 })
                    {
                        XYZ q = geo.ToInternal(a.X + dx * off, a.Y + dy * off, z);
                        corners.Add(q + nrm * Ft(side));
                    }
            var outline = new Outline(new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z)),
                                      new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z)));
            long topo1 = (long)BuiltInCategory.OST_Topography, topo2 = (long)BuiltInCategory.OST_Toposolid;
            IEnumerable<Element> els;
            try { els = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(outline)).ToElements(); }
            catch { return null; }
            foreach (Element e in els)
            {
                try
                {
                    Category c = e.Category;
                    if (c == null || c.CategoryType != CategoryType.Model) continue;
                    long cid = c.Id.Value;
                    if (cid == topo1 || cid == topo2) continue;
                    if (exclude != null && exclude.Contains(e.Id.Value)) continue;
                    var loops = Cut(e, opt, pl, p0, u, nrm, geo);
                    if (loops.Count == 0 || loops.Max(lp => lp.Max(q => q.z)) < road) continue;   // not up to the road: not the deck
                    sec.Loops.AddRange(loops);
                }
                catch { }
            }
            return sec.Loops.Count > 0 ? sec : null;
        }

        // the whole bridge cut along its length: at every axis a vertical plane along the bridge, kept to half a span each side
        private static LongData BuildLong(Document doc, Geo geo, BridgeExcel bx, List<FamilyInstance> decks)
        {
            var d = new LongData();
            if (bx == null || bx.Axes.Count == 0) return d;
            var axes = bx.Axes.OrderBy(a => a.Station).ToList();
            var deckIds = new HashSet<long>(decks.Select(x => x.Id.Value));
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            long topo1 = (long)BuiltInCategory.OST_Topography, topo2 = (long)BuiltInCategory.OST_Toposolid;
            int n = axes.Count;
            for (int i = 0; i < n; i++)
            {
                AxisData a = axes[i];
                d.Axes.Add((a.Name, a.Station, a.Name.StartsWith("A", StringComparison.OrdinalIgnoreCase) ? "Abutment" : "Pier"));
                double g = AxisData.ZAt(a.Ngl, 0);
                if (!double.IsNaN(g)) d.Ground.Add((a.Station, g));
                double len = Math.Sqrt(a.DirX * a.DirX + a.DirY * a.DirY);
                if (len < 1e-9) continue;
                XYZ p0 = geo.ToInternal(a.X, a.Y, 0);
                XYZ nrm = geo.Vec(a.DirX / len, a.DirY / len);
                XYZ u = XYZ.BasisZ.CrossProduct(nrm).Normalize();
                AxisData other = i + 1 < n ? axes[i + 1] : i > 0 ? axes[i - 1] : null;
                if (other != null)
                {
                    double side = (geo.ToInternal(other.X, other.Y, 0) - p0).DotProduct(u);
                    if ((i + 1 < n && side < 0) || (i + 1 >= n && side > 0)) u = -u;
                }
                double lo = i == 0 ? -25 : -(a.Station - axes[i - 1].Station) / 2;
                double hi = i == n - 1 ? 25 : (axes[i + 1].Station - a.Station) / 2;
                var dz = a.Design.Where(p => !double.IsNaN(p.z)).Select(p => p.z).ToList();
                double zTop = dz.Count > 0 ? dz.Max() + 5 : 100, zBot = (dz.Count > 0 ? dz.Min() : 0) - 60;
                var corners = new List<XYZ>();
                foreach (double off in new[] { lo, hi })
                    foreach (double side in new[] { -20.0, 20.0 })
                        foreach (double z in new[] { zBot, zTop })
                            corners.Add(geo.ToInternal(a.X, a.Y, z) + u * Ft(off) + nrm * Ft(side));
                var outline = new Outline(new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z)),
                                          new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z)));
                Plane pl = Plane.CreateByNormalAndOrigin(nrm, p0);
                IEnumerable<Element> els;
                try { els = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(outline)).ToElements(); }
                catch { continue; }
                foreach (Element e in els)
                {
                    try
                    {
                        Category c = e.Category;
                        if (c == null || c.CategoryType != CategoryType.Model) continue;
                        long cid = c.Id.Value;
                        if (cid == topo1 || cid == topo2) continue;
                        bool deck = deckIds.Contains(e.Id.Value);
                        foreach (var lp in Cut(e, opt, pl, p0, u, nrm, geo))
                        {
                            var clipped = ClipX(lp, lo, hi);
                            if (clipped.Count > 2) d.Loops.Add((deck, clipped.Select(q => (a.Station + q.off, q.z)).ToList()));
                        }
                    }
                    catch { }
                }
            }
            foreach (var grp in bx.Roads.GroupBy(r => r.road))
                d.Roads.Add((grp.Key, grp.Select(r => (r.sta, r.z)).ToList()));
            return d;
        }

        // a closed loop kept between two x values (Sutherland-Hodgman, two cuts)
        private static List<(double off, double z)> ClipX(List<(double off, double z)> loop, double lo, double hi)
        {
            Func<List<(double off, double z)>, Func<(double off, double z), bool>, double, List<(double off, double z)>> cut = (pts, inside, x) =>
            {
                var res = new List<(double off, double z)>();
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                    bool ia = inside(a), ib = inside(b);
                    if (ia) res.Add(a);
                    if (ia != ib && Math.Abs(b.off - a.off) > 1e-12)
                    {
                        double t = (x - a.off) / (b.off - a.off);
                        res.Add((x, a.z + (b.z - a.z) * t));
                    }
                }
                return res;
            };
            var r1 = cut(loop, p => p.off >= lo, lo);
            return r1.Count < 3 ? r1 : cut(r1, p => p.off <= hi, hi);
        }

        private static List<List<(double off, double z)>> Cut(Element e, Options opt, Plane pl, XYZ p0, XYZ u, XYZ nrm, Geo geo) =>
            CutRaw(e, opt, pl, p0, nrm).Select(lp => lp.Select(q => (M((q - p0).DotProduct(u)), geo.SharedZ(q))).ToList()).ToList();

        // the faces of an element's solids on the plane: loops of points (internal)
        private static List<List<XYZ>> CutRaw(Element e, Options opt, Plane pl, XYZ p0, XYZ nrm)
        {
            var res = new List<List<XYZ>>();
            List<Solid> solids;
            try { solids = Solids(e.get_Geometry(opt)).ToList(); } catch { return res; }
            foreach (Solid s in solids)
            {
                Solid cut;
                try { cut = BooleanOperationsUtils.CutWithHalfSpace(s, pl); } catch { continue; }
                if (cut == null || cut.Faces.Size == 0) continue;
                foreach (Face f in cut.Faces)
                {
                    if (!(f is PlanarFace pf)) continue;
                    if (Math.Abs(pf.FaceNormal.DotProduct(nrm)) < 0.999) continue;
                    if (Math.Abs((pf.Origin - p0).DotProduct(nrm)) > Ft(0.001)) continue;
                    foreach (EdgeArray loop in pf.EdgeLoops)
                    {
                        var pts = new List<XYZ>();
                        foreach (Edge ed in loop)
                        {
                            IList<XYZ> tes;
                            try { tes = ed.AsCurveFollowingFace(pf).Tessellate(); } catch { tes = ed.Tessellate(); }
                            for (int k = 0; k + 1 < tes.Count; k++) pts.Add(tes[k]);
                        }
                        if (pts.Count >= 3) res.Add(pts);
                    }
                }
            }
            return res;
        }

        // what the measuring tool snaps to: the corners and edges of everything cut at the axis + the detail lines of the view
        private static void SnapData(Document doc, ViewSection vs, Geo geo, AxisData a, List<XYZ> pts, List<(XYZ a, XYZ b)> segs)
        {
            double len = Math.Sqrt(a.DirX * a.DirX + a.DirY * a.DirY);
            if (len < 1e-9) return;
            XYZ p0 = geo.ToInternal(a.X, a.Y, 0);
            XYZ u = geo.Vec(a.DirX / len, a.DirY / len);
            XYZ nrm = XYZ.BasisZ.CrossProduct(u).Normalize();
            Plane pl = Plane.CreateByNormalAndOrigin(nrm, p0);
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            int n = 0;
            IList<Element> els;
            try { els = new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType().ToElements(); } catch { return; }
            foreach (Element e in els)
            {
                if (++n > 600) break;
                try
                {
                    if (e is CurveElement ce)
                    {
                        Curve c = ce.GeometryCurve;
                        if (c == null || !c.IsBound) continue;
                        XYZ q0 = c.GetEndPoint(0), q1 = c.GetEndPoint(1);
                        pts.Add(q0); pts.Add(q1);
                        segs.Add((q0, q1));
                        continue;
                    }
                    Category cat = e.Category;
                    if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                    foreach (List<XYZ> loop in CutRaw(e, opt, pl, p0, nrm))
                        for (int i = 0; i < loop.Count; i++)
                        {
                            pts.Add(loop[i]);
                            segs.Add((loop[i], loop[(i + 1) % loop.Count]));
                        }
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------- placement

        private static void Create(Document doc, Geo geo, Dictionary<string, FamilySymbol> syms, Func<AxisData, SubSection> sectionOf,
                                   Func<string, List<SubParam>> paramsOf, BridgeExcel bx, Dictionary<string, SubAxis> axes,
                                   Dictionary<string, string> map, StringBuilder log, bool final)
        {
            List<Level> levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            foreach (AxisData a in bx.Axes)
            {
                if (!axes.TryGetValue(a.Name, out SubAxis s) || !s.Create || !s.HasWork) continue;

                // Create: the temporary Design / NGL lines of the section go away
                if (final) { DeleteIds(doc, map, LinesKey + a.Name); DeleteIds(doc, map, PlanLinesKey + a.Name); }

                // what was placed at this axis before goes away
                int removed = 0;
                if (map.TryGetValue(IdsKey + a.Name, out string old))
                    foreach (ElementId id in AbStore.Ids(old))
                    {
                        if (doc.GetElement(id) == null) continue;
                        try { doc.Delete(id); removed++; } catch { }
                    }

                double len = Math.Sqrt(a.DirX * a.DirX + a.DirY * a.DirY);
                if (len < 1e-9) { log.AppendLine($"{a.Name}: no axis direction in the Excel - skipped."); continue; }
                double dx = a.DirX / len, dy = a.DirY / len;
                XYZ dirInt = geo.Vec(dx, dy);
                double angle = Math.Atan2(dirInt.Y, dirInt.X);
                SubSection sec = sectionOf(a);
                XYZ cp0 = geo.ToInternal(a.X, a.Y, 0);
                XYZ cnrm = XYZ.BasisZ.CrossProduct(dirInt).Normalize();
                Plane cpl = Plane.CreateByNormalAndOrigin(cnrm, cp0);
                var copt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };

                var ids = new List<ElementId>();
                int placed = 0;
                var cols = new List<FamilyInstance>();     // piers (columns) and their pile caps / footings at this axis
                var fdns = new List<FamilyInstance>();
                var notes = new List<string>();
                for (int i = 0; i < s.Rows.Count; i++)
                {
                    SubRow r = s.Rows[i];
                    if (string.IsNullOrEmpty(r.Family)) continue;
                    string what = r.IsBearing ? "bearing" : r.IsAbut ? "abutment" : $"{i - SubAxis.First + 1} {r.Family}";
                    if (!syms.TryGetValue(r.Family, out FamilySymbol sym0)) { notes.Add($"{what}: \"{r.Family}\" is not in this project"); continue; }
                    List<SubParam> pars = paramsOf(r.Family);
                    FamilySymbol sym;
                    try { sym = TypeFor(doc, sym0, r, pars, a.Name, r.IsBearing ? "B" : r.IsAbut ? "A" : (i - SubAxis.First + 1).ToString(CI)); }
                    catch (Exception ex) { notes.Add($"{what}: its type could not be set ({ex.Message})"); sym = sym0; }

                    if (r.IsAbut)
                    {
                        // the end section of the bridge (the first one at the start, the last one at the end): its left / right
                        // points, moved square to that section line out of the bridge by the row's HOff (0.10 m) - same skew
                        XYZ p1 = null, p2 = null;
                        XYZ outN = null, endL = null, endR = null;      // out of the bridge (internal) and the deck edge points at the end
                        var rows = bx.Stations.Values.Where(st => st.HasPoints).OrderBy(st => st.No).ToList();
                        StationRow cb = null, nb = null;
                        if (rows.Count >= 2)
                        {
                            StationRow first = rows[0], last = rows[rows.Count - 1];
                            double d0 = Math.Pow((first.LX + first.RX) / 2 - a.X, 2) + Math.Pow((first.LY + first.RY) / 2 - a.Y, 2);
                            double d1 = Math.Pow((last.LX + last.RX) / 2 - a.X, 2) + Math.Pow((last.LY + last.RY) / 2 - a.Y, 2);
                            bool atStart = d0 <= d1;
                            cb = atStart ? first : last;
                            nb = atStart ? rows[1] : rows[rows.Count - 2];
                        }
                        if (cb != null)
                        {
                            // square to the section line (L -> R), towards the outside (away from the next section in)
                            double sx = cb.RX - cb.LX, sy = cb.RY - cb.LY, sl = Math.Sqrt(sx * sx + sy * sy);
                            double nx = sl > 1e-9 ? -sy / sl : 0, ny = sl > 1e-9 ? sx / sl : 0;
                            double inX = (nb.LX + nb.RX) / 2 - (cb.LX + cb.RX) / 2, inY = (nb.LY + nb.RY) / 2 - (cb.LY + cb.RY) / 2;
                            if (nx * inX + ny * inY > 0) { nx = -nx; ny = -ny; }
                            double sh = Math.Abs(r.HOff);
                            XYZ pl = geo.ToInternal(cb.LX + nx * sh, cb.LY + ny * sh, cb.LZ + r.VOff);
                            XYZ pr = geo.ToInternal(cb.RX + nx * sh, cb.RY + ny * sh, cb.RZ + r.VOff);
                            p1 = r.Swap ? pr : pl;
                            p2 = r.Swap ? pl : pr;
                            outN = geo.Vec(nx, ny);
                            endL = geo.ToInternal(cb.LX, cb.LY, cb.LZ);
                            endR = geo.ToInternal(cb.RX, cb.RY, cb.RZ);
                        }
                        else
                        {
                            notes.Add($"{what}: no deck sections in the Excel - set on the axis edges instead");
                            var ap = SubGeo.AbutPoints(r, a, sec);
                            if (ap.Any(q => double.IsNaN(q.z))) { notes.Add($"{what}: no {r.Surface} level at the deck edges"); continue; }
                            p1 = geo.ToInternal(a.X + dx * ap[0].off, a.Y + dy * ap[0].off, ap[0].z);
                            p2 = geo.ToInternal(a.X + dx * ap[1].off, a.Y + dy * ap[1].off, ap[1].z);
                        }
                        try
                        {
                            FamilyInstance ai = PlaceTwo(doc, sym, p1, p2);
                            foreach (var kv in r.Params)
                            {
                                SubParam sp = pars.FirstOrDefault(x => x.Name == kv.Key);
                                if (sp == null || !sp.IsInstance) continue;
                                SetValue(ai.LookupParameter(kv.Key), kv.Value, sp.Unit);
                            }
                            // Skew_Angle from the axis skew (unless typed in the list)
                            if (!r.Params.ContainsKey("Skew_Angle") && !double.IsNaN(a.SkewDeg))
                            {
                                Parameter sk = ai.LookupParameter("Skew_Angle");
                                if (sk != null && !sk.IsReadOnly && sk.StorageType == StorageType.Double)
                                    sk.Set(UnitUtils.ConvertToInternalUnits(a.SkewDeg, UnitTypeId.Degrees));
                            }
                            ids.Add(ai.Id);
                            placed++;
                            // the gap between the deck end and the abutment wall = HOff at both ends (measured on the real faces)
                            if (outN != null)
                            {
                                try { notes.Add($"{what}: " + FitAbutGap(doc, ai, r.Swap, endL, endR, outN, Ft(Math.Abs(r.HOff)), Mine(map))); }
                                catch (Exception ex) { notes.Add($"{what}: the gap could not be checked ({ex.Message})"); }
                            }
                        }
                        catch (Exception ex) { notes.Add($"{what}: not placed ({ex.Message})"); }
                        continue;
                    }

                    List<double> offs = SubGeo.RowOffs(r, a);
                    foreach (double off in offs)
                    {
                        double z = SubGeo.SurfaceZ(a, sec, r.Surface, SubGeo.LevelOff(r, a, off));   // bearings / repeated: one level
                        if (double.IsNaN(z))
                        {
                            notes.Add($"{what}: no {(r.Surface == "Soffit" ? "deck soffit (Create Deck first)" : r.Surface)} level at offset {off:0.000}");
                            continue;
                        }
                        z += r.VOff;
                        XYZ p = geo.ToInternal(a.X + dx * off, a.Y + dy * off, z);
                        try
                        {
                            var made = PlaceOne(doc, sym, p, angle, dirInt, levels);
                            foreach (var kv in r.Params)
                            {
                                SubParam sp = pars.FirstOrDefault(x => x.Name == kv.Key);
                                if (sp == null || !sp.IsInstance) continue;
                                SetValue(made.inst.LookupParameter(kv.Key), kv.Value, sp.Unit);
                            }
                            ids.Add(made.inst.Id);
                            if (made.plane != null) ids.Add(made.plane.Id);
                            placed++;
                            if (sym.Family.FamilyPlacementType == FamilyPlacementType.TwoLevelsBased) cols.Add(made.inst);
                            else if (IsFoundation(sym)) fdns.Add(made.inst);
                            // placed, but is any of it on the axis line (seen in the thin section)?
                            try
                            {
                                doc.Regenerate();
                                if (CutRaw(made.inst, copt, cpl, cp0, cnrm).Count == 0)
                                    notes.Add($"{what}: placed, but nothing of it is on the axis line, so the section (far clip 0.01 m) does not show it - see it in 3D");
                            }
                            catch { }
                        }
                        catch (Exception ex) { notes.Add($"{what}: not placed ({ex.Message})"); }
                    }
                }
                // every pier reaches down to the top of the pile cap / footing under it (real geometry)
                if (cols.Count > 0 && fdns.Count > 0)
                {
                    try { doc.Regenerate(); foreach (FamilyInstance c in cols) notes.Add(PierToCap(doc, c, fdns)); }
                    catch (Exception ex) { notes.Add("Pier to pile cap: " + ex.Message); }
                }
                map[IdsKey + a.Name] = AbStore.Ids(ids);
                log.AppendLine($"{a.Name}: {placed} placed" + (removed > 0 ? " (the previous ones replaced)" : "") + ".");
                foreach (string n in notes) log.AppendLine("   " + n);
            }
            if (log.Length == 0) log.AppendLine("Nothing was placed.");
            if (final)
            {
                log.AppendLine();
                log.AppendLine("Levels: Design / NGL from the Substructure sheet, Soffit from the deck in this project; X of every family along its axis.");
            }
        }

        // the type to use: the chosen one, or (if type parameters were changed) a copy "{type} - {axis}-{row}"
        private static FamilySymbol TypeFor(Document doc, FamilySymbol sym, SubRow r, List<SubParam> pars, string axis, string row)
        {
            var edits = r.Params.Where(kv => pars.Any(p => p.Name == kv.Key && !p.IsInstance)).ToList();
            if (edits.Count == 0) return sym;
            bool differs = edits.Any(kv =>
            {
                SubParam p = pars.First(x => x.Name == kv.Key);
                return Text(sym.LookupParameter(kv.Key), p.Unit, p.IsYesNo) != kv.Value;
            });
            if (!differs) return sym;
            string name = string.Concat($"{sym.Name} - {axis}-{row}".Select(ch => "{}[]|;<>?`~\\:".IndexOf(ch) >= 0 ? '_' : ch));
            FamilySymbol t = sym.Family.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol).FirstOrDefault(x => x != null && x.Name == name)
                             ?? (FamilySymbol)sym.Duplicate(name);
            foreach (var kv in edits)
            {
                SubParam p = pars.First(x => x.Name == kv.Key);
                SetValue(t.LookupParameter(kv.Key), kv.Value, p.Unit);
            }
            return t;
        }

        // the abutment wall face that looks at the deck, and the deck end face: the gap at the left and right ends is set to
        // 'gap' by moving each adaptive point square to the end section (a few rounds, as the family follows its points)
        private static string FitAbutGap(Document doc, FamilyInstance ai, bool swap, XYZ endL, XYZ endR, XYZ outN, double gap, HashSet<long> mine)
        {
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            XYZ n = new XYZ(outN.X, outN.Y, 0).Normalize();
            double zLo = Math.Min(endL.Z, endR.Z) - Ft(3), zHi = Math.Max(endL.Z, endR.Z) + Ft(1);

            // vertical planar faces square to n: (origin, normal), only those around the deck level
            List<(XYZ o, XYZ m)> Faces(Element e, Func<double, bool> facing)
            {
                var res = new List<(XYZ o, XYZ m)>();
                foreach (Solid so in Solids(e.get_Geometry(opt)))
                    foreach (Face f in so.Faces)
                    {
                        if (!(f is PlanarFace pf) || Math.Abs(pf.FaceNormal.Z) > 0.05) continue;
                        double dn = pf.FaceNormal.DotProduct(n);
                        if (!facing(dn)) continue;
                        BoundingBoxUV bb = f.GetBoundingBox();
                        XYZ c = f.Evaluate(new UV((bb.Min.U + bb.Max.U) / 2, (bb.Min.V + bb.Max.V) / 2));
                        XYZ a0 = f.Evaluate(bb.Min), a1 = f.Evaluate(bb.Max);
                        if (Math.Max(a0.Z, a1.Z) < zLo || Math.Min(a0.Z, a1.Z) > zHi) continue;
                        res.Add((c, pf.FaceNormal));
                    }
                return res;
            }

            // the deck end face: the faces of other elements, out-facing, within 1 m of the end section; else the section line
            XYZ mid = (endL + endR) / 2;
            (XYZ o, XYZ m) deck = (mid, n);
            double bestD = Ft(1.0);
            var box = new Outline(new XYZ(Math.Min(endL.X, endR.X) - Ft(3), Math.Min(endL.Y, endR.Y) - Ft(3), zLo),
                                  new XYZ(Math.Max(endL.X, endR.X) + Ft(3), Math.Max(endL.Y, endR.Y) + Ft(3), zHi));
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(box)).ToElements())
            {
                if (e.Id == ai.Id || mine.Contains(e.Id.Value) || e.Category == null || e.Category.CategoryType != CategoryType.Model) continue;
                try
                {
                    foreach (var fc in Faces(e, dn => dn > 0.95))
                    {
                        double d = Math.Abs((fc.o - mid).DotProduct(n));
                        if (d < bestD) { bestD = d; deck = fc; }
                    }
                }
                catch { }
            }

            // gap at a deck edge point: from the deck face (along n) to the abutment face
            double GapAt(XYZ p, (XYZ o, XYZ m) wall)
            {
                XYZ onDeck = p - n * ((p - deck.o).DotProduct(deck.m) / n.DotProduct(deck.m));
                return (wall.o - onDeck).DotProduct(wall.m) / n.DotProduct(wall.m);
            }

            IList<ElementId> refs = AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(ai);
            var rp1 = (ReferencePoint)doc.GetElement(refs[0]);
            var rp2 = (ReferencePoint)doc.GetElement(refs[1]);
            ReferencePoint atL = swap ? rp2 : rp1, atR = swap ? rp1 : rp2;
            double gL = double.NaN, gR = double.NaN;
            for (int round = 0; round < 5; round++)
            {
                doc.Regenerate();
                // the abutment face that looks back at the deck and is nearest to it (on the outside)
                (XYZ o, XYZ m)? wall = null;
                double near = double.MaxValue;
                foreach (var fc in Faces(ai, dn => dn < -0.95))
                {
                    double d = (fc.o - deck.o).DotProduct(n);
                    if (d > -Ft(0.05) && d < near) { near = d; wall = fc; }
                }
                if (wall == null) return "placed; no abutment face facing the deck was found, so the gap was not checked";
                gL = GapAt(endL, wall.Value);
                gR = GapAt(endR, wall.Value);
                double dL = gap - gL, dR = gap - gR;
                if (Math.Abs(dL) < Ft(0.0005) && Math.Abs(dR) < Ft(0.0005)) break;
                atL.Position = atL.Position + n * dL;
                atR.Position = atR.Position + n * dR;
            }
            return $"gap to the deck end: left {M(gL):0.000} m, right {M(gR):0.000} m (asked {M(gap):0.000} m)";
        }

        // a two-point adaptive family (the abutment): point 1 and point 2
        private static FamilyInstance PlaceTwo(Document doc, FamilySymbol sym, XYZ p1, XYZ p2)
        {
            if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
            int n = 0;
            try { n = AdaptiveComponentFamilyUtils.GetNumberOfAdaptivePoints(sym.Family); } catch { }
            if (n != 2) throw new InvalidOperationException($"the abutment family needs 2 adaptive points (it has {n})");
            FamilyInstance inst = AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance(doc, sym);
            IList<ElementId> refs = AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(inst);
            if (refs.Count < 2) throw new InvalidOperationException("the abutment's adaptive points could not be read");
            ((ReferencePoint)doc.GetElement(refs[0])).Position = p1;
            ((ReferencePoint)doc.GetElement(refs[1])).Position = p2;
            return inst;
        }

        private static bool IsFoundation(FamilySymbol sym)
        {
            try
            {
                if (sym.Category != null && sym.Category.Id.Value == (long)BuiltInCategory.OST_StructuralFoundation) return true;
                string n = (sym.Family.Name + " " + sym.Name).ToLowerInvariant();
                return n.Contains("cap") || n.Contains("footing") || n.Contains("foundation");
            }
            catch { return false; }
        }

        // the column's base set to the top of the foundation under it (its top stays where it is)
        private static string PierToCap(Document doc, FamilyInstance col, List<FamilyInstance> fdns)
        {
            if (!(col.Location is LocationPoint lp)) return "Pier: no location";
            XYZ c = lp.Point;
            FamilyInstance best = null; double bestD = double.MaxValue; double top = double.NaN;
            foreach (FamilyInstance f in fdns)
            {
                BoundingBoxXYZ bb = f.get_BoundingBox(null);
                if (bb == null) continue;
                bool inside = c.X >= bb.Min.X && c.X <= bb.Max.X && c.Y >= bb.Min.Y && c.Y <= bb.Max.Y;
                double dd = inside ? 0 : new XYZ((bb.Min.X + bb.Max.X) / 2 - c.X, (bb.Min.Y + bb.Max.Y) / 2 - c.Y, 0).GetLength();
                if (dd < bestD) { bestD = dd; best = f; top = bb.Max.Z; }
            }
            if (best == null) return "Pier: no pile cap / footing found under it";
            Parameter bl = col.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM);
            Parameter bo = col.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
            if (bl == null || bo == null || bo.IsReadOnly || !(doc.GetElement(bl.AsElementId()) is Level lv)) return "Pier: its base cannot be moved";
            bo.Set(top - lv.ProjectElevation);
            doc.Regenerate();
            return $"Pier: base moved to the top of {best.Symbol.FamilyName} (+{M(top):0.000} project)";
        }

        internal static (FamilyInstance inst, SketchPlane plane) PlaceOne(Document doc, FamilySymbol sym, XYZ p, double angle, XYZ dirInt, List<Level> levels)
        {
            if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
            FamilyPlacementType pt = sym.Family.FamilyPlacementType;

            int nAdapt = 0;
            if (pt == FamilyPlacementType.Adaptive || AdaptiveComponentInstanceUtils.IsAdaptiveFamilySymbol(sym))
                try { nAdapt = AdaptiveComponentFamilyUtils.GetNumberOfAdaptivePoints(sym.Family); } catch { nAdapt = 0; }
            if (nAdapt > 1) throw new InvalidOperationException($"adaptive family with {nAdapt} points - the axis gives one point");
            if (nAdapt == 1)
            {
                FamilyInstance ai = AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance(doc, sym);
                IList<ElementId> refs = AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(ai);
                if (refs.Count > 0 && doc.GetElement(refs[0]) is ReferencePoint rp)
                {
                    rp.Position = p;
                    try
                    {
                        Transform cs = Transform.Identity;
                        cs.Origin = p;
                        cs.BasisX = dirInt;
                        cs.BasisY = XYZ.BasisZ.CrossProduct(dirInt).Normalize();
                        cs.BasisZ = XYZ.BasisZ;
                        rp.SetCoordinateSystem(cs);
                    }
                    catch { }
                }
                return (ai, null);
            }
            if (pt == FamilyPlacementType.CurveBased || pt == FamilyPlacementType.CurveBasedDetail || pt == FamilyPlacementType.CurveDrivenStructural)
                throw new InvalidOperationException("this family is placed along a line (like a beam) - the axis gives one point");
            if (pt == FamilyPlacementType.ViewBased)
                throw new InvalidOperationException("this is a view (2D) family");

            Level lv = levels.LastOrDefault(l => l.ProjectElevation <= p.Z + 1e-6) ?? levels.FirstOrDefault();
            FamilyInstance inst;
            SketchPlane plane = null;
            bool rotate = true;
            if (pt == FamilyPlacementType.TwoLevelsBased)
            {
                // a column: its top at the point, its length kept
                if (lv == null) throw new InvalidOperationException("no level in the project");
                inst = doc.Create.NewFamilyInstance(p, sym, lv, StructuralType.Column);
                doc.Regenerate();
                BoundingBoxXYZ bb = inst.get_BoundingBox(null);
                double len = bb != null ? bb.Max.Z - bb.Min.Z : Ft(3);
                if (len < Ft(0.05)) len = Ft(3);
                double top = p.Z - lv.ProjectElevation;
                inst.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(lv.Id);
                inst.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(top);
                inst.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.Set(lv.Id);
                inst.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.Set(top - len);
                doc.Regenerate();
                if (inst.Location is LocationPoint clp)
                {
                    XYZ mv = new XYZ(p.X - clp.Point.X, p.Y - clp.Point.Y, 0);
                    if (mv.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, inst.Id, mv);
                    doc.Regenerate();
                    double now = ((LocationPoint)inst.Location).Rotation;
                    if (Math.Abs(angle - now) > 1e-9)
                        ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(new XYZ(p.X, p.Y, 0), new XYZ(p.X, p.Y, 1)), angle - now);
                }
                return (inst, null);
            }
            StructuralType stype = StructuralType.NonStructural;
            try { if (sym.Category != null && sym.Category.Id.Value == (long)BuiltInCategory.OST_StructuralFoundation) stype = StructuralType.Footing; } catch { }
            if (pt == FamilyPlacementType.OneLevelBased && lv != null)
                inst = doc.Create.NewFamilyInstance(p, sym, lv, stype);
            else
            {
                // work plane / face based and the rest: on a horizontal plane through the point, X along the axis
                plane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, p));
                try
                {
                    inst = doc.Create.NewFamilyInstance(plane.GetPlaneReference(), p, dirInt, sym);
                    rotate = false;
                }
                catch
                {
                    doc.Delete(plane.Id);
                    plane = null;
                    inst = lv != null ? doc.Create.NewFamilyInstance(p, sym, lv, stype)
                                      : doc.Create.NewFamilyInstance(p, sym, stype);
                }
            }
            doc.Regenerate();
            if (inst.Location is LocationPoint lp)
            {
                XYZ mv = p - lp.Point;
                if (mv.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, inst.Id, mv);
                if (rotate)
                {
                    doc.Regenerate();
                    double now = ((LocationPoint)inst.Location).Rotation;
                    if (Math.Abs(angle - now) > 1e-9)
                        ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(p, p + XYZ.BasisZ), angle - now);
                }
            }
            return (inst, plane);
        }

        // ---------------------------------------------------------------- See Section

        // a section view at the axis (looking along the bridge, far clip 0.01 m), named "AB Section - {axis}", opened
        private static ViewSection AxisSection(Document doc, Geo geo, AxisData a, SubSection sec, StringBuilder log, bool reuse)
        {
            ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Section);
            if (vft == null) { log.AppendLine("No section view type in this project."); return null; }
            double len = Math.Sqrt(a.DirX * a.DirX + a.DirY * a.DirY);
            if (len < 1e-9) { log.AppendLine($"{a.Name}: no axis direction in the Excel."); return null; }
            double dx = a.DirX / len, dy = a.DirY / len;
            var (L, R) = SubGeo.Edges(a);

            // levels to show: deck top + 3 m down to the lowest NGL - 8 m
            var zs = a.Design.Select(p => p.z).Concat(a.Ngl.Select(p => p.z)).Where(z => !double.IsNaN(z)).ToList();
            if (sec != null) foreach (var lp in sec.Loops) zs.AddRange(lp.Select(p => p.z));
            if (zs.Count == 0) zs.Add(0);
            double zTop = zs.Max() + 3, zBot = zs.Min() - 8;
            double zMid = (zTop + zBot) / 2, half = (zTop - zBot) / 2;
            double xMid = (L + R) / 2, halfW = (R - L) / 2 + 3;

            XYZ o = geo.ToInternal(a.X + dx * xMid, a.Y + dy * xMid, zMid);
            XYZ u = geo.Vec(dx, dy);
            var t = Transform.Identity;
            t.Origin = o;
            t.BasisX = u;
            t.BasisY = XYZ.BasisZ;
            t.BasisZ = u.CrossProduct(XYZ.BasisZ);     // the view looks along -BasisZ
            var box = new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(-Ft(halfW), -Ft(half), -Ft(0.01)),
                Max = new XYZ(Ft(halfW), Ft(half), 0)
            };

            string name = "AB Section - " + a.Name;
            ViewSection old = new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            if (old != null && reuse) return old;     // kept as it is (with what was done in it)
            if (old != null)
            {
                try { doc.Delete(old.Id); }
                catch { name += " (" + DateTime.Now.ToString("HHmmss", CI) + ")"; }
            }
            ViewSection vs = ViewSection.CreateSection(doc, vft.Id, box);
            try { vs.Name = name; } catch { }
            try { vs.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR)?.Set(Ft(0.01)); } catch { }
            try { vs.Scale = 100; } catch { }
            try { vs.DetailLevel = ViewDetailLevel.Fine; } catch { }
            try { vs.CropBoxActive = true; vs.CropBoxVisible = true; } catch { }
            return vs;
        }
    }

    // shared <-> internal, taken from the survey point (the same way as Place Deck)
    internal sealed class Geo
    {
        private readonly XYZ _spInt, _spSh;
        private readonly Transform _toSh, _toInt;

        public Geo(Document doc)
        {
            BasePoint sp = BasePoint.GetSurveyPoint(doc);
            _spInt = sp.Position;
            _spSh = sp.SharedPosition;
            _toSh = doc.ActiveProjectLocation.GetTotalTransform();
            _toInt = _toSh.Inverse;
        }

        private static double Ft(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);
        private static double M(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);

        // shared E / N / Z (m) -> internal point
        public XYZ ToInternal(double e, double n, double z) => _spInt + _toInt.OfVector(new XYZ(Ft(e), Ft(n), Ft(z)) - _spSh);

        // shared horizontal direction -> internal unit vector
        public XYZ Vec(double dx, double dy) => _toInt.OfVector(new XYZ(dx, dy, 0)).Normalize();

        // internal point -> shared E / N (m)
        public (double e, double n) SharedXY(XYZ p)
        {
            XYZ s = _spSh + _toSh.OfVector(p - _spInt);
            return (M(s.X), M(s.Y));
        }

        // internal direction -> shared horizontal unit direction
        public (double x, double y) SharedDir(XYZ v)
        {
            XYZ s = _toSh.OfVector(new XYZ(v.X, v.Y, 0));
            double l = Math.Sqrt(s.X * s.X + s.Y * s.Y);
            return l < 1e-12 ? (0, 0) : (s.X / l, s.Y / l);
        }

        // internal point -> shared Z (m)
        public double SharedZ(XYZ p) => M(_spSh.Z + _toSh.OfVector(p - _spInt).Z);
    }

}
