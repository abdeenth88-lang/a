// AB Bridges for Revit - Create Deck: builds one adaptive family per family group of the Excel.
//
// For every instance of the group (Family 1 = one instance through every station; corbel / outrigger groups =
// one instance per ☑ row group):
//   - reference points on the left and right edge of every station + a line through each edge
//   - the profile family placed at every station (point 1 = right, point 2 = left), parameters from Excel
//   - the profile loops are read: the outer loop = SOLID, every loop inside = VOID
//   - solid loft through all stations; void lofts only where the void exists (Yes/No ☑) and up to the
//     diaphragm faces (the diaphragm itself stays solid)
// The family is saved, loaded into the project and placed (shared coordinates).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace ABBridgesRevit
{
    internal sealed class BuildOptions
    {
        public bool Swap;              // point 1 = left instead of right
        public bool Shared = true;     // Excel X/Y/Z are shared (survey) coordinates
        public string Template;        // adaptive family template (.rft)
    }

    internal static class DeckBuilder
    {
        private const double Tol = 0.1;     // ft (30 mm), joining profile curves - the nearest end within this is taken

        private static double Ft(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);

        public static string Build(Application app, Document doc, BridgeExcel bx, FamilyGroup grp, Family profile, BuildOptions opt)
        {
            var log = new StringBuilder();
            Transform toInternal = opt.Shared ? doc.ActiveProjectLocation.GetTotalTransform().Inverse : Transform.Identity;
            XYZ P(double x, double y, double z) => toInternal.OfPoint(new XYZ(Ft(x), Ft(y), Ft(z)));

            // instances to build (☐ in Place = not placed)
            var runs = grp.Instances.Where(i => i.place)
                .Select(i => (i.name, nos: i.nos.Where(n => bx.Stations.TryGetValue(n, out StationRow s) && s.HasPoints).ToList()))
                .Where(i => i.nos.Count >= 2).ToList();
            if (runs.Count == 0) return $"Family {grp.Number}: nothing to build (no ☑ instances with points).";

            // local origin near the bridge, so the family geometry stays close to its origin
            StationRow s0 = bx.Stations[runs[0].nos[0]];
            XYZ o = P(s0.LX, s0.LY, s0.LZ);
            XYZ origin = new XYZ(o.X, o.Y, 0);

            // profile family -> temporary file
            string tmpDir = Path.Combine(Path.GetTempPath(), "ABBridges");
            Directory.CreateDirectory(tmpDir);
            string profilePath = Path.Combine(tmpDir, Safe(profile.Name) + ".rfa");
            Document pdoc = doc.EditFamily(profile);
            try { pdoc.SaveAs(profilePath, new SaveAsOptions { OverwriteExistingFile = true }); }
            finally { pdoc.Close(false); }

            string famName = Safe($"AB {bx.BridgeName} - Family {grp.Number} - {profile.Name}");
            string famPath = Path.Combine(tmpDir, famName + ".rfa");

            Document fdoc = app.NewFamilyDocument(opt.Template);
            try
            {
                using (var t = new Transaction(fdoc, "AB Bridges - build"))
                {
                    t.Start();
                    Quiet(t);
                    if (!fdoc.LoadFamily(profilePath, out Family pf) || pf == null) throw new InvalidOperationException("The profile family could not be loaded.");
                    FamilySymbol sym = (FamilySymbol)fdoc.GetElement(pf.GetFamilySymbolIds().First());
                    if (!sym.IsActive) sym.Activate();
                    fdoc.Regenerate();

                    int solids = 0, voids = 0;
                    foreach (var run in runs)
                    {
                        var r = BuildRun(fdoc, sym, bx, grp, run.nos, P, origin, opt, log);
                        solids += r.solids;
                        voids += r.voids;
                        if (r.solids == 0) log.AppendLine($"  {run.name}: the solid loft could not be made.");
                    }
                    t.Commit();
                    log.Insert(0, $"Family {grp.Number} ({profile.Name}): {runs.Count} instance(s), {solids} solid and {voids} void loft(s).\n");
                }

                fdoc.SaveAs(famPath, new SaveAsOptions { OverwriteExistingFile = true });
                Family loaded = fdoc.LoadFamily(doc, new LoadOpts());
                fdoc.Close(false);
                fdoc = null;

                // place it once (a reload updates the instance already placed)
                if (loaded != null)
                {
                    using (var t = new Transaction(doc, "AB Bridges - place " + famName))
                    {
                        t.Start();
                        Quiet(t);
                        FamilySymbol fs = (FamilySymbol)doc.GetElement(loaded.GetFamilySymbolIds().First());
                        if (!fs.IsActive) fs.Activate();
                        bool placed = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                            .Any(fi => fi.Symbol?.Family?.Id == loaded.Id);
                        if (!placed) doc.Create.NewFamilyInstance(origin, fs, StructuralType.NonStructural);
                        t.Commit();
                    }
                }
            }
            finally
            {
                if (fdoc != null) fdoc.Close(false);
            }
            return log.ToString();
        }

        // ---------------------------------------------------------------- build inside the open adaptive / mass family

        // the origin of the deck family, as shared E / N / Z (m):
        //   1. the origin already kept in this family (so later runs - corbel, outrigger, ... - sit on the same points)
        //   2. the base point of the Excel (Export Base Point in Civil)
        //   3. the first left point of the bridge, Z = 0
        private static (double e, double n, double z, string how) OriginFor(Document fdoc, BridgeExcel bx, Dictionary<string, string> map)
        {
            if (map.Keys.Any(k => k.StartsWith("ITEM|")) && TryReadOrigin(fdoc, out double fe, out double fn, out double fz))
                return (fe, fn, fz, "kept in this family");
            if (bx.BasePoint.HasValue)
                return (bx.BasePoint.Value.E, bx.BasePoint.Value.N, bx.BasePoint.Value.Z, "base point of the Excel");
            StationRow s0 = bx.Stations.Values.Where(x => x.HasPoints).OrderBy(x => x.No).First();
            return (s0.LX, s0.LY, 0, "first left point (no base point in the Excel - run Export Base Point in Civil)");
        }

        private static bool TryReadOrigin(Document fdoc, out double e, out double n, out double z)
        {
            e = n = z = 0;
            try
            {
                FamilyManager fm = fdoc.FamilyManager;
                FamilyParameter fp = fm.get_Parameter(OriginParam);
                if (fp == null || fm.CurrentType == null) return false;
                return PlaceDeckCommand.TryParseOrigin(fm.CurrentType.AsString(fp), out e, out n, out z);
            }
            catch { return false; }
        }

        private static HashSet<long> AllIds(Document d) =>
            new HashSet<long>(new FilteredElementCollector(d).WhereElementIsNotElementType().ToElementIds().Select(i => i.Value));

        public static string BuildHere(Document fdoc, BridgeExcel bx, FamilyGroup grp, Family profile, BuildOptions opt)
        {
            var log = new StringBuilder();
            bool isDeck = grp.IsDeckKind;      // a deck: keeps its diaphragm solids (they cut the outriggers)

            var runs = grp.Instances.Where(i => i.place)
                .Select(i => (i.name, nos: i.nos.Where(n => bx.Stations.TryGetValue(n, out StationRow s) && s.HasPoints).ToList()))
                .Where(i => i.nos.Count >= 2).ToList();
            if (runs.Count == 0) return $"Family {grp.Number}: nothing to build (no checked instances with points).";

            var map = AbStore.Load(fdoc);
            var (oE, oN, oZ, how) = OriginFor(fdoc, bx, map);
            // every point is built as (E - E0, N - N0, Z - Z0): the origin of the family is the base point, same north
            XYZ P(double x, double y, double z) => new XYZ(Ft(x), Ft(y), Ft(z - oZ));
            XYZ origin = new XYZ(Ft(oE), Ft(oN), 0);

            using (var t = new Transaction(fdoc, "AB Bridges - Create Deck"))
            {
                t.Start();
                Quiet(t);
                FamilySymbol sym = (FamilySymbol)fdoc.GetElement(profile.GetFamilySymbolIds().First());
                if (!sym.IsActive) sym.Activate();
                fdoc.Regenerate();

                int solids = 0, voids = 0, cuts = 0, replaced = 0;
                DiaphragmSolids.Clear();
                foreach (var run in runs)
                {
                    // re-running an item replaces only that item
                    string key = $"ITEM|{grp.FamilyName}|{run.name}";       // by family name: the group may move in the sheet
                    if (map.TryGetValue(key, out string old))
                    {
                        var ids = AbStore.Ids(old).Where(id => fdoc.GetElement(id) != null).ToList();
                        if (ids.Count > 0) { try { fdoc.Delete(ids); replaced++; } catch (Exception ex) { log.AppendLine($"  {run.name}: the previous one could not be removed ({ex.Message})."); } }
                        map.Remove(key);
                        fdoc.Regenerate();
                    }

                    HashSet<long> before = AllIds(fdoc);
                    var r = BuildRun(fdoc, sym, bx, grp, run.nos, P, origin, opt, log);
                    solids += r.solids;
                    voids += r.voids;
                    if (r.solids == 0) log.AppendLine($"  {run.name}: the solid loft could not be made.");
                    map[key] = AbStore.Ids(new FilteredElementCollector(fdoc).WhereElementIsNotElementType().ToElementIds().Where(id => !before.Contains(id.Value)));

                    if (isDeck && DiaphragmSolids.Count > 0)
                    {
                        foreach (string k in map.Keys.Where(k => k.StartsWith("DIA|")).ToList()) map.Remove(k);
                        foreach (var kv in DiaphragmSolids) map[$"DIA|{kv.Key}"] = kv.Value.Value.ToString(CultureInfo.InvariantCulture);
                    }
                    else if (grp.PerInstance)
                    {
                        // outrigger (an instance at a diaphragm axis): cut by the deck diaphragm at that axis; the corbel stays alone
                        int axisNo = run.nos.FirstOrDefault(n => bx.Stations[n].Type == "Axis");
                        if (axisNo != 0)
                        {
                            map[$"ORC|{axisNo}"] = AbStore.Ids(RunSolids);
                            cuts += CutByDiaphragm(fdoc, map, RunSolids, axisNo, bx.Stations[axisNo].Remark, log);
                        }
                    }
                }

                // a new deck: cut the outriggers already in this family again
                if (isDeck && DiaphragmSolids.Count > 0)
                    foreach (var kv in map.Where(k => k.Key.StartsWith("ORC|")).ToList())
                        if (int.TryParse(kv.Key.Substring(4), out int axisNo) && bx.Stations.TryGetValue(axisNo, out StationRow ax))
                            cuts += CutByDiaphragm(fdoc, map, AbStore.Ids(kv.Value).Where(id => fdoc.GetElement(id) != null).ToList(), axisNo, ax.Remark, log);

                StoreOrigin(fdoc, oE, oN, oZ);
                if (!AbStore.Save(fdoc, map)) log.AppendLine("  Note: what was built could not be kept in the family (re-running will not replace the old items).");
                t.Commit();
                log.Insert(0, $"Family {grp.Number} ({profile.Name}): {runs.Count} instance(s), {solids} solid and {voids} void loft(s)" +
                              (cuts > 0 ? $", {cuts} cut(s) with the deck" : "") + (replaced > 0 ? $", {replaced} replaced" : "") + ".\n" +
                              $"  Origin (0,0,0) = {how}:  E {oE:0.000}  N {oN:0.000}  Z {oZ:0.000}.\n");
            }
            return log.ToString();
        }

        // the outrigger at an axis and the deck diaphragm solid at the same axis: the diaphragm cuts the outrigger
        private static int CutByDiaphragm(Document fdoc, Dictionary<string, string> map, List<ElementId> ids, int axisNo, string axisName, StringBuilder log)
        {
            Element d = null;
            if (map.TryGetValue($"DIA|{axisNo}", out string dv)) d = AbStore.Ids(dv).Select(id => fdoc.GetElement(id)).FirstOrDefault(e => e != null && e.IsValidObject);
            if (d == null)
            {
                log.AppendLine($"  {axisName}: no deck diaphragm in this family yet - the outrigger is not cut (it will be when the deck is built).");
                return 0;
            }
            fdoc.Regenerate();
            int n = 0;
            foreach (ElementId id in ids)
            {
                Element o = fdoc.GetElement(id);
                if (o == null) continue;
                try
                {
                    if (SolidSolidCutUtils.CanElementCutElement(d, o, out CutFailureReason why))
                    {
                        if (!SolidSolidCutUtils.CutExistsBetweenElements(o, d, out _)) { SolidSolidCutUtils.AddCutBetweenSolids(fdoc, o, d); n++; }
                    }
                    else log.AppendLine($"  {axisName}: the outrigger cannot be cut by the diaphragm ({why}).");
                }
                catch (Exception ex) { log.AppendLine($"  {axisName}: outrigger cut failed: {ex.Message}"); }
            }
            return n;
        }

        // the shared (survey) coordinates of the family origin, kept in the family for "Place Deck" in the project
        public const string OriginParam = "AB Origin (shared E;N;Z m)";

        private static void StoreOrigin(Document fdoc, double x, double y, double z)
        {
            try
            {
                FamilyManager fm = fdoc.FamilyManager;
                if (fm.CurrentType == null) fm.NewType("Deck");
                FamilyParameter fp = fm.get_Parameter(OriginParam);
                if (fp != null && fp.IsInstance) { fm.RemoveParameter(fp); fp = null; }      // must be a type parameter (read from the type)
                if (fp == null) fp = fm.AddParameter(OriginParam, GroupTypeId.IdentityData, SpecTypeId.String.Text, false);
                fm.Set(fp, string.Format(CultureInfo.InvariantCulture, "{0:0.0000};{1:0.0000};{2:0.0000}", x, y, z));
            }
            catch { }
        }

        // ---------------------------------------------------------------- one instance (run of stations)

        private sealed class Sec
        {
            public StationRow St;
            public XYZ L, R;
            public FamilyInstance Inst;
            public List<XYZ> Outer;
            public List<List<XYZ>> Voids = new List<List<XYZ>>();
            public List<List<XYZ>> FaceVoids;     // voids read with Yes/No forced on (diaphragm faces)
            public string Why;                    // why the profile could not be read
            public List<(Curve c, Reference r, Reference r2)> OuterSrc;          // the profile's own curves of the outer loop
            public List<List<(Curve c, Reference r, Reference r2)>> VoidSrc = new List<List<(Curve c, Reference r, Reference r2)>>();
            public List<List<(Curve c, Reference r, Reference r2)>> FaceVoidSrc;
        }

        // solids of the last run, and the deck solids (Family 1) of the last build in this family
        private static readonly List<ElementId> RunSolids = new List<ElementId>();
        private static bool LastLoftLinked;
        private static (List<XYZ> pts, List<(Curve c, Reference r, Reference r2)> src, bool useRefs)? DumpRefs;
        private static readonly Dictionary<int, ElementId> DiaphragmSolids = new Dictionary<int, ElementId>();   // axis No -> diaphragm solid

        // which profile curve every point list came from, and which pieces made every closed loop
        private static readonly Dictionary<List<XYZ>, (Curve c, Reference r, Reference r2)> SegSrc = new Dictionary<List<XYZ>, (Curve c, Reference r, Reference r2)>(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<List<XYZ>, List<List<XYZ>>> LoopParts = new Dictionary<List<XYZ>, List<List<XYZ>>>(ReferenceEqualityComparer.Instance);

        private static List<(Curve c, Reference r, Reference r2)> Src(List<XYZ> loop) =>
            loop != null && LoopParts.TryGetValue(loop, out var parts)
                ? parts.Select(q => SegSrc.TryGetValue(q, out var v) ? v : ((Curve)null, (Reference)null, (Reference)null)).ToList()
                : null;

        private static (int solids, int voids) BuildRun(Document fdoc, FamilySymbol sym, BridgeExcel bx, FamilyGroup grp, List<int> nos,
                                                        Func<double, double, double, XYZ> P, XYZ origin, BuildOptions opt, StringBuilder log)
        {
            SegSrc.Clear();
            LoopParts.Clear();
            RunSolids.Clear();
            DumpRefs = null;
            var secs = new List<Sec>();
            var leftPts = new ReferencePointArray();
            var rightPts = new ReferencePointArray();

            foreach (int no in nos)
            {
                StationRow s = bx.Stations[no];
                XYZ l = P(s.LX, s.LY, s.LZ) - origin, r = P(s.RX, s.RY, s.RZ) - origin;
                ReferencePoint rl = fdoc.FamilyCreate.NewReferencePoint(l), rr = fdoc.FamilyCreate.NewReferencePoint(r);
                try { rl.Visible = false; rr.Visible = false; } catch { }     // not shown when the family is in a project
                leftPts.Append(rl);
                rightPts.Append(rr);

                FamilyInstance inst = AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance(fdoc, sym);
                IList<ElementId> ids = AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(inst);
                if (ids.Count >= 2)
                {
                    ((ReferencePoint)fdoc.GetElement(ids[0])).Position = opt.Swap ? l : r;
                    ((ReferencePoint)fdoc.GetElement(ids[1])).Position = opt.Swap ? r : l;
                }
                if (grp.Values.TryGetValue(no, out var vals)) SetParams(inst, sym, grp.Params, vals);
                ForceYesNo(inst, "solid");     // the profile shows its whole shape: solid lines ...
                ForceYesNo(inst, "void");      // ... and void lines, at every station (the diaphragm void just does not cut)
                secs.Add(new Sec { St = s, L = l, R = r, Inst = inst });
            }

            // edge lines
            // edge lines as reference lines (they guide, they are not picked before the deck)
            if (leftPts.Size >= 2) { try { fdoc.FamilyCreate.NewCurveByPoints(leftPts).IsReferenceLine = true; } catch { } }
            if (rightPts.Size >= 2) { try { fdoc.FamilyCreate.NewCurveByPoints(rightPts).IsReferenceLine = true; } catch { } }
            fdoc.Regenerate();

            foreach (Sec s in secs) ReadLoops(s, s.Inst, false);
            Dump(secs);

            // diaphragm faces: read the void too (Yes/No forced on) so the void can stop exactly at the face
            var faces = secs.Where(s => s.St.Type == "Diaphragm face" && s.Voids.Count == 0).ToList();
            if (faces.Count > 0)
            {
                foreach (Sec s in faces) ForceYesNo(s.Inst, "void");
                fdoc.Regenerate();
                foreach (Sec s in faces)
                {
                    var tmp = new Sec { L = s.L, R = s.R };
                    ReadLoops(tmp, s.Inst, false);     // the void lines are shown now
                    s.FaceVoids = tmp.Voids;
                    s.FaceVoidSrc = tmp.VoidSrc;
                }
                // back to the Excel values (the profiles stay in the family, as the Excel says)
                foreach (Sec s in faces)
                    if (grp.Values.TryGetValue(s.St.No, out var fv)) SetParams(s.Inst, sym, grp.Params, fv);
                fdoc.Regenerate();
            }

            int solids = 0, voidsMade = 0, linked = 0;
            var solidForms = new List<Element>();
            var voidForms = new List<(Element form, bool cuts)>();

            foreach (Sec s in secs)
                if (s.Outer == null) log.AppendLine($"  {s.St.Remark}: profile not read ({s.Why ?? "no lines"}) - skipped.");
            // the corbel stations of the deck get their profile only (no solid, no void, no cut): no deck over the corbels
            // Family 1 = the deck (pieces, voids, no deck over the corbels); other families through every station
            // (barriers, asphalt, ...) = ONE solid from the first station to the last; corbel / outrigger = one solid each
            // Type Deck with voids (a box) = the deck: pieces at the diaphragms, voids, no deck over the corbels;
            // Type Deck without voids (barrier, asphalt ...) = one solid from the first station to the last
            bool deck = grp.IsDeckKind && secs.Any(x => x.Voids.Count > 0 || (x.FaceVoids != null && x.FaceVoids.Count > 0));
            bool continuous = !grp.PerInstance && !deck;
            var good = secs.Where(s => s.Outer != null && (!deck || s.St.Type != "Corbel")).ToList();

            // pieces: a new loft starts at every diaphragm face and at the last near-diaphragm station (D2):
            //   [face, axis, face]  [face, D1, D2]  [D2, divisions..., D2]  [D2, D1, face] ...
            var pieces = new List<List<Sec>>();
            var cur = new List<Sec>();
            for (int q = 0; q < good.Count; q++)
            {
                cur.Add(good[q]);
                bool last = q == good.Count - 1;
                if (!last && !continuous && cur.Count >= 2 && IsBreak(good, q))
                {
                    pieces.Add(cur);
                    cur = new List<Sec> { good[q] };
                }
            }
            if (cur.Count >= 2) pieces.Add(cur);

            // like Create Form by hand: the SOLIDS are made from the profiles with their voids hidden (only the outer
            // lines shown), then the voids are shown again and the VOIDS are made from the void lines
            Reread(fdoc, secs, voidsOn: false);
            foreach (var pc in pieces)
            {
                string name = $"{pc[0].St.Remark} → {pc[pc.Count - 1].St.Remark}";
                if (pc.Any(x => x.Outer == null)) { log.AppendLine($"  Solid {name}: a profile has no closed outer loop - not made."); continue; }
                Form solidForm = Loft(fdoc, true, pc.Select(x => (x.Outer, x.OuterSrc, true)).ToList(), out string err);
                if (solidForm != null && deck)
                    foreach (Sec ax in pc.Where(x => x.St.Type == "Axis")) DiaphragmSolids[ax.St.No] = solidForm.Id;   // the diaphragm at this axis
                if (solidForm != null) { solidForms.Add(solidForm); RunSolids.Add(solidForm.Id); solids++; if (LastLoftLinked) linked++; else log.AppendLine($"  Solid {name}: NOT linked to the profiles ({err ?? "copies used"})."); }
                else log.AppendLine($"  Solid {name} failed: {err}");
            }

            Reread(fdoc, secs, voidsOn: true);
            foreach (var pc in pieces)
            {
                if (!deck) break;                                       // corbel / outrigger / barriers / asphalt: all solid
                string name = $"{pc[0].St.Remark} → {pc[pc.Count - 1].St.Remark}";
                bool diaphragm = pc.Any(x => x.St.Type == "Axis");     // [face, axis, face]: its void is made but does not cut
                if (!pc.Any(x => x.Voids.Count > 0)) continue;
                var vs = pc.Select(x => x.Voids).ToList();
                var vsrc = pc.Select(x => x.VoidSrc).ToList();
                if (vs.Any(v => v == null || v.Count == 0) || vs.Select(v => v.Count).Distinct().Count() != 1)
                {
                    log.AppendLine($"  Void {name}: the stations do not have the same number of voids - not made.");
                    continue;
                }
                for (int v = 0; v < vs[0].Count; v++)
                {
                    var prof = new List<(List<XYZ>, List<(Curve c, Reference r, Reference r2)>, bool)>();
                    for (int k = 0; k < vs.Count; k++)
                        prof.Add((vs[k][v], vsrc[k] != null && v < vsrc[k].Count ? vsrc[k][v] : null, true));
                    Form voidForm = Loft(fdoc, false, prof, out string verr);
                    if (voidForm != null) { voidForms.Add((voidForm, !diaphragm)); voidsMade++; if (LastLoftLinked) linked++; else log.AppendLine($"  Void {name}: NOT linked to the profiles ({verr ?? "copies used"})."); }
                    else log.AppendLine($"  Void {name} failed: {verr}");
                }
            }

            // every void cuts the solids it touches
            fdoc.Regenerate();
            int cuts = 0;
            foreach (var (vd, cutsIt) in voidForms)
            {
                if (!cutsIt)
                {
                    // diaphragm: the void stays in the family (visible, editable) but cuts nothing
                    try { Parameter cg = vd.LookupParameter("Cuts Geometry"); if (cg != null && !cg.IsReadOnly) cg.Set(0); } catch { }
                    foreach (Element sd in solidForms)
                        try { if (SolidSolidCutUtils.CutExistsBetweenElements(sd, vd, out _)) SolidSolidCutUtils.RemoveCutBetweenSolids(fdoc, sd, vd); } catch { }
                    continue;
                }
                BoundingBoxXYZ vb = vd.get_BoundingBox(null);
                foreach (Element sd in solidForms)
                {
                    BoundingBoxXYZ sb = sd.get_BoundingBox(null);
                    if (vb != null && sb != null && (vb.Min.X > sb.Max.X || vb.Max.X < sb.Min.X || vb.Min.Y > sb.Max.Y || vb.Max.Y < sb.Min.Y || vb.Min.Z > sb.Max.Z || vb.Max.Z < sb.Min.Z)) continue;
                    try
                    {
                        if (SolidSolidCutUtils.CanElementCutElement(vd, sd, out _) && !SolidSolidCutUtils.CutExistsBetweenElements(sd, vd, out _))
                        {
                            SolidSolidCutUtils.AddCutBetweenSolids(fdoc, sd, vd);
                            cuts++;
                        }
                    }
                    catch (Exception ex) { log.AppendLine("  Cut failed: " + ex.Message); }
                }
            }
            if (voidForms.Any(v => v.cuts) && cuts == 0) log.AppendLine("  Note: the voids could not be set to cut the solids.");
            log.AppendLine($"  {linked} of {solids + voidsMade} form(s) are made from the profile lines themselves (they follow the profiles).");
            DumpRefsFile(fdoc);
            return (solids, voidsMade);
        }

        // switch the Void... Yes/No of every profile and read the lines again (fresh references)
        private static void Reread(Document fdoc, List<Sec> secs, bool voidsOn)
        {
            foreach (Sec x in secs) SetYesNo(x.Inst, "void", voidsOn ? 1 : 0);
            fdoc.Regenerate();
            foreach (Sec x in secs)
            {
                var t = new Sec { St = x.St, L = x.L, R = x.R };
                ReadLoops(t, x.Inst, false);
                if (voidsOn) { x.Voids = t.Voids; x.VoidSrc = t.VoidSrc; }
                else { x.Outer = t.Outer; x.OuterSrc = t.OuterSrc; }
            }
        }

        private static void SetYesNo(FamilyInstance inst, string prefix, int value)
        {
            var names = new List<string>();
            foreach (Parameter p in inst.Parameters)
            {
                try
                {
                    if (p.StorageType != StorageType.Integer || !SpecTypeId.Boolean.YesNo.Equals(p.Definition.GetDataType())) continue;
                    if (p.Definition.Name.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) names.Add(p.Definition.Name);
                }
                catch { }
            }
            foreach (string n in names)
            {
                try { Parameter p = inst.LookupParameter(n); if (p != null && !p.IsReadOnly && p.AsInteger() != value) p.Set(value); } catch { }
            }
        }

        // a loft piece ends here: a diaphragm face, or the near-diaphragm station farthest from the face (D2)
        private static bool IsBreak(List<Sec> secs, int q)
        {
            string t = secs[q].St.Type;
            if (t == "Diaphragm face") return true;
            if (t != "Near diaphragm") return false;
            bool Near(int k) => k >= 0 && k < secs.Count && (secs[k].St.Type == "Near diaphragm" || secs[k].St.Type == "Diaphragm face");
            return !Near(q - 1) || !Near(q + 1);
        }

        // ---------------------------------------------------------------- profile geometry

        // closed loops of the instance.
        // Lines on a subcategory named "solid ..." are the outer boundary (SOLID); lines on "void ..." are the inner
        // boundaries (VOIDS). Without such names: the biggest loop = solid, loops inside it = voids.
        private static void ReadLoops(Sec s, FamilyInstance inst, bool hiddenToo)
        {
            // the shown model lines of the profile; reference lines (hidden) are never part of the shape
            var segs = Segments(inst, hiddenToo).Where(g => !IsRefLine(g.style)).ToList();
            if (segs.Count == 0 && !hiddenToo) segs = Segments(inst, true).Where(g => !IsRefLine(g.style)).ToList();
            bool named = segs.Any(g => IsSolid(g.style) || IsVoid(g.style));

            // every point onto the vertical plane of the section (through L and R)
            {
                XYZ h = new XYZ(s.R.X - s.L.X, s.R.Y - s.L.Y, 0);
                if (h.GetLength() > 1e-9)
                {
                    XYZ n = h.Normalize().CrossProduct(XYZ.BasisZ);
                    XYZ L0 = s.L;
                    segs = segs.Select(g =>
                    {
                        var np = g.pts.Select(p => p - n * n.DotProduct(p - L0)).ToList();
                        if (SegSrc.TryGetValue(g.pts, out var src)) SegSrc[np] = src;
                        return (g.style, np);
                    }).ToList();
                }
            }

            XYZ u = new XYZ(s.R.X - s.L.X, s.R.Y - s.L.Y, 0);
            u = u.GetLength() > 1e-9 ? u.Normalize() : XYZ.BasisX;
            (double, double) To2(XYZ p) => ((p - s.L).DotProduct(u), p.Z);
            var Info = new Func<List<XYZ>, (List<XYZ> lp, List<(double, double)> pts, double area, double cu, double cz)>(lp =>
            {
                var pts = lp.Select(To2).ToList();
                return (lp, pts, Math.Abs(Area(pts)), pts.Average(p => p.Item1), pts.Average(p => p.Item2));
            });

            if (named)
            {
                // solid: the solid lines (hidden ones too if the visible ones do not close)
                var solidSegs = segs.Where(g => IsSolid(g.style)).Select(g => g.pts).ToList();
                var outer = Chain(solidSegs).Select(Info).OrderByDescending(x => x.area).FirstOrDefault();
                if (outer.lp == null && !hiddenToo)
                {
                    var all = Segments(inst, true).Where(g => IsSolid(g.style) && !IsRefLine(g.style)).Select(g => g.pts).ToList();
                    outer = Chain(all).Select(Info).OrderByDescending(x => x.area).FirstOrDefault();
                }
                if (outer.lp == null)
                {
                    s.Why = $"{solidSegs.Count} solid line(s) do not make a closed loop";
                    return;
                }
                s.Outer = outer.lp;
                s.OuterSrc = Src(outer.lp);

                // voids: each void subcategory chained on its own (only the lines shown = Yes/No on)
                var voids = new List<(List<XYZ> lp, List<(double, double)> pts, double area, double cu, double cz)>();
                foreach (var grp in segs.Where(g => IsVoid(g.style)).GroupBy(g => g.style.ToLowerInvariant()))
                    voids.AddRange(Chain(grp.Select(g => g.pts).ToList()).Select(Info).Where(x => x.area > 1e-4));
                s.Voids = voids.OrderBy(x => x.cu).Select(x => x.lp).ToList();
                s.VoidSrc = s.Voids.Select(Src).ToList();
                return;
            }

            // no solid / void names: by shape
            var loops = Chain(segs.Select(g => g.pts).ToList());
            if (loops.Count == 0) { s.Why = $"{segs.Count} line(s), no closed loop (no 'solid' / 'void' subcategories found)"; return; }
            var info = loops.Select(Info).OrderByDescending(x => x.area).ToList();
            s.Outer = info[0].lp;
            s.OuterSrc = Src(info[0].lp);
            var outer2 = info[0].pts;
            s.Voids = info.Skip(1).Where(x => Inside(outer2, x.cu, x.cz) && x.area > 1e-4).OrderBy(x => x.cu).Select(x => x.lp).ToList();
            s.VoidSrc = s.Voids.Select(Src).ToList();
        }

        private static bool IsRefLine(string style) => style != null && style.IndexOf("Reference Line", StringComparison.OrdinalIgnoreCase) >= 0;
        private static bool IsSolid(string style) => style != null && style.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase);
        private static bool IsVoid(string style) => style != null && style.TrimStart().StartsWith("void", StringComparison.OrdinalIgnoreCase);

        // every curve of the instance with the name of its subcategory (line style)
        private static List<(string style, List<XYZ> pts)> Segments(FamilyInstance inst, bool hiddenToo)
        {
            Document d = inst.Document;
            var segs = new List<(string, List<XYZ>)>();
            var opt = new Options { IncludeNonVisibleObjects = hiddenToo, DetailLevel = ViewDetailLevel.Fine, ComputeReferences = true };
            // a reference usable for a form: the curve's own one, or (nested geometry) the symbol curve's reference
            // turned into a reference through this instance
            Reference ViaInstance(Reference symRef)
            {
                if (symRef == null) return null;
                try
                {
                    string sr = symRef.ConvertToStableRepresentation(d);
                    return Reference.ParseFromStableRepresentation(d, $"{inst.UniqueId}:0:INSTANCE:{sr}");
                }
                catch { return null; }
            }
            void Take(GeometryElement ge, List<Curve> symCurves)
            {
                if (ge == null) return;
                int k = -1;
                foreach (GeometryObject go in ge)
                {
                    if (go is Curve c)
                    {
                        k++;
                        IList<XYZ> t = c.Tessellate();
                        if (t.Count < 2) continue;
                        string name = null;
                        try { name = (d.GetElement(c.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name; } catch { }
                        var pts = t.ToList();
                        // two ways to point at this curve of the profile: its own reference, and the symbol curve's
                        // reference made into one through this instance (nested geometry)
                        Reference rf = null, rf2 = null;
                        try { rf = c.Reference; } catch { }
                        if (symCurves != null && k < symCurves.Count) { try { rf2 = ViaInstance(symCurves[k].Reference); } catch { } }
                        SegSrc[pts] = (c.Clone(), rf ?? rf2, rf2 ?? rf);     // a copy now: the geometry changes after the next regenerate
                        segs.Add((name ?? "", pts));
                    }
                    else if (go is GeometryInstance gi)
                    {
                        List<Curve> sym = null;
                        try { sym = gi.GetSymbolGeometry()?.OfType<Curve>().ToList(); } catch { }
                        Take(gi.GetInstanceGeometry(), sym);
                    }
                }
            }
            Take(inst.get_Geometry(opt), null);
            return segs;
        }

        // chain pieces into closed loops (a second, looser try if nothing closes)
        private static List<List<XYZ>> Chain(List<List<XYZ>> segs)
        {
            var r = Chain(segs, Tol);
            return r.Count > 0 ? r : Chain(segs, Tol * 3);
        }

        private static List<List<XYZ>> Chain(List<List<XYZ>> segs, double tol)
        {
            var loops = new List<List<XYZ>>();
            var left = new List<List<XYZ>>(segs);
            while (left.Count > 0)
            {
                var loop = new List<XYZ>(left[0]);
                var parts = new List<List<XYZ>> { left[0] };
                left.RemoveAt(0);
                bool grew = true;
                while (grew && loop[0].DistanceTo(loop[loop.Count - 1]) > tol)
                {
                    grew = false;
                    XYZ end = loop[loop.Count - 1];
                    int best = -1; bool rev = false; double bd = tol;
                    for (int k = 0; k < left.Count; k++)
                    {
                        List<XYZ> sgm = left[k];
                        double d0 = sgm[0].DistanceTo(end), d1 = sgm[sgm.Count - 1].DistanceTo(end);
                        if (d0 < bd) { bd = d0; best = k; rev = false; }
                        if (d1 < bd) { bd = d1; best = k; rev = true; }
                    }
                    if (best >= 0)
                    {
                        var sg = new List<XYZ>(left[best]);
                        if (rev) sg.Reverse();
                        loop.AddRange(sg.Skip(1));
                        parts.Add(left[best]);
                        left.RemoveAt(best);
                        grew = true;
                    }
                }
                if (loop.Count >= 4 && loop[0].DistanceTo(loop[loop.Count - 1]) <= tol)
                {
                    loop.RemoveAt(loop.Count - 1);
                    // drop points closer than the shortest curve Revit accepts
                    var clean = new List<XYZ>();
                    foreach (XYZ p in loop)
                        if (clean.Count == 0 || clean[clean.Count - 1].DistanceTo(p) > 0.003) clean.Add(p);
                    if (clean.Count >= 3 && clean[0].DistanceTo(clean[clean.Count - 1]) <= 0.003) clean.RemoveAt(clean.Count - 1);
                    if (clean.Count >= 3) { loops.Add(clean); LoopParts[clean] = parts; }
                }
            }
            return loops;
        }

        // a loft through the profiles: first through the profile family's own curves (no copies, stays linked to
        // the profiles), then through copies of those curves, then through straight lines between the points
        private static Form Loft(Document fdoc, bool solid, List<(List<XYZ> pts, List<(Curve c, Reference r, Reference r2)> src, bool useRefs)> profs, out string err)
        {
            err = null;
            LastLoftLinked = false;
            if (DumpRefs == null && profs.Count > 0 && profs[0].src != null) DumpRefs = profs[0];
            var errs = new List<string>();

            foreach (int mode in new[] { 1, 0, 2, 3 })
            {
                // 1: the profile's curves seen through the instance (what Create Form uses when the profile is picked
                //    by hand - the joints between lines and arcs stay smooth)   0: the nested curves   2: copies   3: lines
                if (mode == 1 && profs.All(p => p.src == null || p.src.All(q => q.r2 == null || ReferenceEquals(q.r, q.r2)))) continue;
                using (var st = new SubTransaction(fdoc))
                {
                    st.Start();
                    try
                    {
                        var arr = new ReferenceArrayArray();
                        bool allLinked = true;
                        foreach (var p in profs)
                        {
                            bool hasR = p.useRefs && p.src != null && p.src.All(q => (mode == 0 ? q.r : q.r2) != null);
                            bool hasCurves = p.src != null && p.src.All(q => q.c != null);
                            ReferenceArray ra;
                            if (mode <= 1 && hasR) { ra = new ReferenceArray(); foreach (var q in p.src) ra.Append(mode == 0 ? q.r : q.r2); }
                            else
                            {
                                allLinked = false;
                                ra = mode <= 2 && hasCurves ? CurveCopies(fdoc, p.pts, p.src) : Profile(fdoc, p.pts);
                            }
                            arr.Append(ra);
                        }
                        Form f = fdoc.FamilyCreate.NewLoftForm(solid, arr);
                        st.Commit();
                        LastLoftLinked = mode <= 1 && allLinked;
                        if (!LastLoftLinked) err = string.Join(" | ", errs);
                        return f;
                    }
                    catch (Exception ex)
                    {
                        errs.Add($"{(mode == 0 ? "own refs" : mode == 1 ? "refs via instance" : mode == 2 ? "copies" : "lines")}: {ex.Message}");
                        err = string.Join(" | ", errs);
                        st.RollBack();
                    }
                }
            }
            return null;
        }

        // model curves copied from the profile's curves (arcs stay arcs), on the plane of the profile
        private static ReferenceArray CurveCopies(Document fdoc, List<XYZ> loop, List<(Curve c, Reference r, Reference r2)> src)
        {
            int n = loop.Count;
            Plane pl = Plane.CreateByThreePoints(loop[0], loop[n / 3], loop[(2 * n) / 3]);
            SketchPlane sp = SketchPlane.Create(fdoc, pl);
            var refs = new ReferenceArray();
            foreach (var q in src)
            {
                // the copy must sit on the section (else straight lines between the points are used)
                XYZ e0 = q.c.GetEndPoint(0);
                if (loop.Min(p => p.DistanceTo(e0)) > 0.2) throw new InvalidOperationException("profile curve away from the section");
                ModelCurve mc = fdoc.FamilyCreate.NewModelCurve(q.c.Clone(), sp);
                refs.Append(mc.GeometryCurve.Reference);
            }
            return refs;
        }

        // model lines of one closed profile on its own sketch plane; returns their references
        private static ReferenceArray Profile(Document fdoc, List<XYZ> loop)
        {
            int n = loop.Count;
            Plane pl = Plane.CreateByThreePoints(loop[0], loop[n / 3], loop[(2 * n) / 3]);
            SketchPlane sp = SketchPlane.Create(fdoc, pl);
            XYZ nrm = pl.Normal, po = pl.Origin;
            XYZ On(XYZ p) => p - nrm * nrm.DotProduct(p - po);

            var refs = new ReferenceArray();
            for (int i = 0; i < n; i++)
            {
                XYZ a = On(loop[i]), b = On(loop[(i + 1) % n]);
                if (a.DistanceTo(b) < 0.003) continue;
                ModelCurve mc = fdoc.FamilyCreate.NewModelCurve(Line.CreateBound(a, b), sp);
                refs.Append(mc.GeometryCurve.Reference);
            }
            return refs;
        }

        private static double Area(List<(double, double)> p)
        {
            double a = 0;
            for (int i = 0; i < p.Count; i++)
            {
                var q = p[(i + 1) % p.Count];
                a += p[i].Item1 * q.Item2 - q.Item1 * p[i].Item2;
            }
            return a / 2;
        }

        private static bool Inside(List<(double, double)> poly, double x, double y)
        {
            bool c = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var (xi, yi) = poly[i];
                var (xj, yj) = poly[j];
                if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi) + xi)) c = !c;
            }
            return c;
        }

        // ---------------------------------------------------------------- parameters

        private static void SetParams(FamilyInstance inst, FamilySymbol sym, List<ParamColumn> cols, Dictionary<string, string> vals)
        {
            foreach (ParamColumn pc in cols)
            {
                if (!vals.TryGetValue(pc.Name, out string text) || string.IsNullOrWhiteSpace(text)) continue;
                Parameter p = inst.LookupParameter(pc.Name) ?? sym.LookupParameter(pc.Name);
                if (p == null || p.IsReadOnly) continue;
                try
                {
                    switch (p.StorageType)
                    {
                        case StorageType.Double:
                            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) break;
                            p.Set(ToInternal(d, pc.Unit));
                            break;
                        case StorageType.Integer:
                            if (pc.Name.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase)) p.Set(1);   // the solid (outer) lines are always needed
                            else if (text == "☑") p.Set(1);
                            else if (text == "☐") p.Set(0);
                            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double iv)) p.Set((int)Math.Round(iv));
                            break;
                        case StorageType.String:
                            p.Set(text);
                            break;
                    }
                }
                catch { }
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

        // sets the Yes/No parameters on (only those whose name starts with 'prefix', or all if prefix is null)
        private static void ForceYesNo(FamilyInstance inst, string prefix)
        {
            var names = new List<string>();
            foreach (Parameter p in inst.Parameters)
            {
                try
                {
                    if (p.StorageType != StorageType.Integer || !SpecTypeId.Boolean.YesNo.Equals(p.Definition.GetDataType())) continue;
                    string n = p.Definition.Name;
                    if (prefix == null || n.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) names.Add(n);
                }
                catch { }
            }
            foreach (string n in names)
            {
                try
                {
                    Parameter p = inst.LookupParameter(n);
                    if (p != null && !p.IsReadOnly && p.AsInteger() != 1) p.Set(1);
                }
                catch { }
            }
        }

        // debug: the references and the gaps between the curves of the first profile used for a form
        private static void DumpRefsFile(Document fdoc)
        {
            try
            {
                string dir = Path.GetDirectoryName(CreateForm.LastExcel);
                if (DumpRefs == null || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var src = DumpRefs.Value.src;
                var sb = new StringBuilder();
                for (int i = 0; i < src.Count; i++)
                {
                    var (c, r, r2) = src[i];
                    string S(Reference x) { try { return x == null ? "null" : x.ConvertToStableRepresentation(fdoc); } catch (Exception e) { return "ERR " + e.Message; } }
                    var n = src[(i + 1) % src.Count].c;
                    double gap = double.NaN;
                    if (c != null && n != null)
                    {
                        XYZ[] a = { c.GetEndPoint(0), c.GetEndPoint(1) }, b = { n.GetEndPoint(0), n.GetEndPoint(1) };
                        gap = a.SelectMany(x => b.Select(y => x.DistanceTo(y))).Min() * 304.8;
                    }
                    sb.AppendLine($"{i}: {c?.GetType().Name}  gap to next {gap:0.000} mm");
                    sb.AppendLine($"    r : {S(r)}");
                    sb.AppendLine($"    r2: {S(r2)}");
                }
                File.AppendAllText(Path.Combine(dir, "ABBridges_refs.txt"), $"==== {DateTime.Now:HH:mm:ss}\n" + sb);
            }
            catch { }
        }

        // ---------------------------------------------------------------- debug: what the profile looks like at every station
        private static void Dump(List<Sec> secs)
        {
            try
            {
                string dir = Path.GetDirectoryName(CreateForm.LastExcel);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var sb = new StringBuilder();
                double M(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);
                foreach (Sec s in secs)
                {
                    XYZ u = new XYZ(s.R.X - s.L.X, s.R.Y - s.L.Y, 0);
                    u = u.GetLength() > 1e-9 ? u.Normalize() : XYZ.BasisX;
                    string P2(XYZ p) => $"({M((p - s.L).DotProduct(u)):0.###},{M(p.Z - s.L.Z):0.###})";
                    XYZ nn = u.CrossProduct(XYZ.BasisZ);
                    string Off(List<XYZ> pts) => $"off {M(pts.Max(p => Math.Abs(nn.DotProduct(p - s.L)))):0.###}";
                    sb.AppendLine($"=== {s.St.No} {s.St.Remark} [{s.St.Type}]  outer={(s.Outer == null ? "NONE" : s.Outer.Count + " pts")}  voids={s.Voids.Count}  why={s.Why}");
                    var yn = new List<string>();
                    foreach (Parameter p in s.Inst.Parameters)
                    {
                        try
                        {
                            if (p.StorageType == StorageType.Integer && SpecTypeId.Boolean.YesNo.Equals(p.Definition.GetDataType()))
                                yn.Add($"{p.Definition.Name}={p.AsInteger()}");
                        }
                        catch { }
                    }
                    sb.AppendLine("  YesNo: " + string.Join(" ", yn.OrderBy(x => x)));
                    foreach (bool hidden in new[] { false, true })
                    {
                        var segs = Segments(s.Inst, hidden);
                        sb.AppendLine($"  {(hidden ? "with hidden" : "visible")}: {segs.Count} curves");
                        foreach (var g in segs)
                            sb.AppendLine($"    [{g.style}] {g.pts.Count} pts  {P2(g.pts[0])} -> {P2(g.pts[g.pts.Count - 1])}  {Off(g.pts)}");
                    }
                }
                File.WriteAllText(Path.Combine(dir, "ABBridges_debug.txt"), sb.ToString());
            }
            catch { }
        }

        // ---------------------------------------------------------------- helpers

        private static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();

        private static void Quiet(Transaction t)
        {
            FailureHandlingOptions fho = t.GetFailureHandlingOptions();
            fho.SetFailuresPreprocessor(new WarningSwallower());
            t.SetFailureHandlingOptions(fho);
        }

        private sealed class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (FailureMessageAccessor f in a.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        internal sealed class LoadOpts : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Project;          // keep the project's own profile families as they are
                overwriteParameterValues = false;
                return true;
            }
        }

        // ---------------------------------------------------------------- template

        public static string FindTemplate(Application app)
        {
            try
            {
                string root = app.FamilyTemplatePath;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
                var files = Directory.GetFiles(root, "*.rft", SearchOption.AllDirectories);
                return files.FirstOrDefault(f => Path.GetFileName(f).Equals("Metric Generic Model Adaptive.rft", StringComparison.OrdinalIgnoreCase))
                    ?? files.FirstOrDefault(f => Path.GetFileName(f).IndexOf("Generic Model Adaptive", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? files.FirstOrDefault(f => Path.GetFileName(f).IndexOf("Adaptive", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return null; }
        }
    }
}
