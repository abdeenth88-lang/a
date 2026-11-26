// AB Bridges for Revit - "Place Deck": places a deck family built inside an adaptive family (Create Deck) at its
// shared coordinates. The family keeps the shared E / N of its origin (first left point of the Excel) in the type
// parameter "AB Origin (shared E;N m)". The point is taken relative to the SURVEY POINT of the project:
//     internal = SurveyPoint.Position + R * (shared - SurveyPoint.SharedPosition)
// with R the rotation from shared (true north) to the project axes, and the family is rotated by the same angle.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace ABBridgesRevit
{
    [Transaction(TransactionMode.Manual)]
    public class PlaceDeckCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try { return Run(data); }
            catch (Exception ex)
            {
                new TaskDialog("AB Bridges - error") { MainInstruction = ex.GetType().Name + ": " + ex.Message, ExpandedContent = ex.ToString() }.Show();
                return Result.Cancelled;
            }
        }

        private static double Ft(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);
        private static double M(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);

        private Result Run(ExternalCommandData data)
        {
            Document doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("AB Bridges", "Open the bridge project (Place Deck works in a project, after Load into Project of the deck family).");
                return Result.Cancelled;
            }

            // deck families: those that carry the origin parameter
            var decks = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Select(s => (sym: s, origin: s.LookupParameter(DeckBuilder.OriginParam)?.AsString()))
                .Where(x => !string.IsNullOrWhiteSpace(x.origin)).ToList();
            if (decks.Count == 0)
            {
                TaskDialog.Show("AB Bridges", "No deck family to place.\n\nBuild the deck with Create Deck inside the adaptive family, then use Load into Project, then Place Deck.");
                return Result.Cancelled;
            }

            var pick = decks[0];
            if (decks.Count > 1)
            {
                var td = new TaskDialog("AB Bridges") { MainInstruction = "Which deck family?" };
                var ids = new[] { TaskDialogCommandLinkId.CommandLink1, TaskDialogCommandLinkId.CommandLink2, TaskDialogCommandLinkId.CommandLink3, TaskDialogCommandLinkId.CommandLink4 };
                for (int i = 0; i < decks.Count && i < 4; i++) td.AddCommandLink(ids[i], decks[i].sym.FamilyName + " : " + decks[i].sym.Name);
                TaskDialogResult r = td.Show();
                var results = new[] { TaskDialogResult.CommandLink1, TaskDialogResult.CommandLink2, TaskDialogResult.CommandLink3, TaskDialogResult.CommandLink4 };
                int k = Array.IndexOf(results, r);
                if (k < 0) return Result.Cancelled;
                pick = decks[k];
            }

            if (!TryParseOrigin(pick.origin, out double e, out double n, out double z))
            {
                TaskDialog.Show("AB Bridges", "The origin stored in the family is not readable: " + pick.origin);
                return Result.Cancelled;
            }
            new TaskDialog("AB Bridges") { MainInstruction = "Place Deck", MainContent = PlaceAt(doc, pick.sym, e, n, z) }.Show();
            return Result.Succeeded;
        }

        // "E;N;Z" (Z optional, 0)
        internal static bool TryParseOrigin(string text, out double e, out double n, out double z)
        {
            e = n = z = 0;
            string[] parts = (text ?? "").Split(';');
            if (parts.Length < 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out e)
                                 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return false;
            if (parts.Length >= 3 && !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) z = 0;
            return true;
        }

        // places (or moves) the deck family so its origin sits at shared E / N, taken from the survey point
        internal static string PlaceAt(Document doc, FamilySymbol sym, double e, double n, double z)
        {
            BasePoint sp = BasePoint.GetSurveyPoint(doc);
            BasePoint pbp = BasePoint.GetProjectBasePoint(doc);
            XYZ spInt = sp.Position, spSh = sp.SharedPosition;
            Transform toShared = doc.ActiveProjectLocation.GetTotalTransform();
            Transform toInternal = toShared.Inverse;

            XYZ shared = new XYZ(Ft(e), Ft(n), Ft(z));
            XYZ target = spInt + toInternal.OfVector(shared - spSh);
            double angle = Math.Atan2(toInternal.BasisX.Y, toInternal.BasisX.X);
            double diffMm = M(toInternal.OfPoint(shared).DistanceTo(target)) * 1000;

            FamilyInstance inst;
            using (var t = new Transaction(doc, "AB Bridges - place deck"))
            {
                t.Start();
                if (!sym.IsActive) sym.Activate();
                inst = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .FirstOrDefault(fi => fi.Symbol?.Id == sym.Id);
                if (inst == null) inst = doc.Create.NewFamilyInstance(target, sym, StructuralType.NonStructural);
                doc.Regenerate();
                if (inst.Location is LocationPoint lp)
                {
                    double now = lp.Rotation;
                    if (Math.Abs(angle - now) > 1e-9)
                        ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(lp.Point, lp.Point + XYZ.BasisZ), angle - now);
                    doc.Regenerate();
                    XYZ move = target - ((LocationPoint)inst.Location).Point;
                    if (move.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, inst.Id, move);
                }
                t.Commit();
            }

            XYZ placed = (inst.Location as LocationPoint)?.Point ?? target;
            XYZ back = toShared.OfPoint(placed);
            return
                $"{sym.FamilyName} placed at its shared coordinates.\n" +
                $"Family origin (0,0,0):  E {e:0.000}   N {n:0.000}   Z {z:0.000}\n" +
                $"Back to shared from the placed point:  E {M(back.X):0.000}   N {M(back.Y):0.000}   Elev {M(back.Z):0.000}\n" +
                $"Survey point:  E {M(spSh.X):0.000}   N {M(spSh.Y):0.000}   Elev {M(spSh.Z):0.000}\n" +
                $"Project base point (shared):  E {M(pbp.SharedPosition.X):0.000}   N {M(pbp.SharedPosition.Y):0.000}   Elev {M(pbp.SharedPosition.Z):0.000}\n" +
                $"Rotation to the project axes: {angle * 180 / Math.PI:0.0000}°" +
                (diffMm > 1 ? $"\nNote: survey-point way and whole-transform way differ by {diffMm:0} mm (the survey-point way is used)." : "") +
                "\nCheck: Manage > Coordinates > Report Shared Coordinates on the left edge at the first station = the Excel X / Y / Z.";
        }

        // check with two points of the Excel (first and last station, left edge): where they end up in the project,
        // back in shared coordinates, against the Excel; and the direction between them against Civil
        internal static string Check(Document doc, FamilySymbol sym, double e0, double n0, double z0, BridgeExcel bx)
        {
            try
            {
                FamilyInstance inst = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .FirstOrDefault(fi => fi.Symbol?.Id == sym.Id);
                var pts = bx.Stations.Values.Where(x => x.HasPoints).OrderBy(x => x.No).ToList();
                if (inst == null || pts.Count < 2) return "";
                StationRow a = pts[0], b = pts[pts.Count - 1];
                Transform T = inst.GetTotalTransform();
                Transform toShared = doc.ActiveProjectLocation.GetTotalTransform();
                XYZ Sh(StationRow s) => toShared.OfPoint(T.OfPoint(new XYZ(Ft(s.LX - e0), Ft(s.LY - n0), Ft(s.LZ - z0))));
                XYZ pa = Sh(a), pb = Sh(b);
                double errA = Math.Sqrt(Math.Pow(M(pa.X) - a.LX, 2) + Math.Pow(M(pa.Y) - a.LY, 2) + Math.Pow(M(pa.Z) - a.LZ, 2)) * 1000;
                double errB = Math.Sqrt(Math.Pow(M(pb.X) - b.LX, 2) + Math.Pow(M(pb.Y) - b.LY, 2) + Math.Pow(M(pb.Z) - b.LZ, 2)) * 1000;
                double bc = Math.Atan2(b.LX - a.LX, b.LY - a.LY) * 180 / Math.PI;
                double br = Math.Atan2(M(pb.X - pa.X), M(pb.Y - pa.Y)) * 180 / Math.PI;
                double dd = br - bc;
                while (dd > 180) dd -= 360;
                while (dd < -180) dd += 360;
                bool ok = errA < 5 && errB < 5 && Math.Abs(dd) < 0.001;
                return $"Check with two Civil points ({a.Remark} and {b.Remark}, left edge):\n" +
                       $"  position difference {errA:0.0} mm and {errB:0.0} mm\n" +
                       $"  direction Civil {bc:0.0000}°   Revit {br:0.0000}°   difference {dd:0.0000}°\n" +
                       (ok ? "  OK - the deck sits on the Civil coordinates." : "  NOT OK - check the base point / shared coordinates (Import Base Point).");
            }
            catch (Exception ex) { return "Check with two Civil points failed: " + ex.Message; }
        }
    }
}
