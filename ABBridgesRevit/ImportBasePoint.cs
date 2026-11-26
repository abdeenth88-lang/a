// AB Bridges for Revit - "Import Base Point": reads the base point that Civil wrote into the bridge Excel
// (Export Base Point: E / N / Z, sheet Super Structure) and sets up the bridge file around it:
//   - the internal origin of the project = that point in shared coordinates (E, N, Z), angle to true north 0
//     (project north = Civil north, the bridge is not turned)
//   - the project base point and the survey point are put on that point
// The deck (Create Deck) is built with the same point as its (0,0,0), so it sits on the origin, close to it,
// which keeps the geometry sound (Dubai coordinates are ~2,800 km from 0).

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace ABBridgesRevit
{
    [Transaction(TransactionMode.Manual)]
    public class ImportBasePointCommand : IExternalCommand
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
        private static string S(XYZ p) => $"E {M(p.X):0.000}   N {M(p.Y):0.000}   Z {M(p.Z):0.000}";

        private Result Run(ExternalCommandData data)
        {
            Document doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("AB Bridges", "Open the bridge project (Import Base Point works in a project).");
                return Result.Cancelled;
            }

            string path;
            using (var dlg = new System.Windows.Forms.OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", Title = "Bridge Excel with the base point (Export Base Point in Civil)" })
            {
                try { if (File.Exists(CreateForm.LastExcel)) { dlg.InitialDirectory = Path.GetDirectoryName(CreateForm.LastExcel); dlg.FileName = Path.GetFileName(CreateForm.LastExcel); } } catch { }
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
                path = dlg.FileName;
            }
            var bp = BridgeExcel.LoadBasePoint(path);
            if (!bp.HasValue)
            {
                TaskDialog.Show("AB Bridges", "This Excel has no base point.\n\nIn Civil 3D: Export Base Point (pick the point, type its Z, write it into this Excel), then try again.");
                return Result.Cancelled;
            }
            double E = bp.Value.E, N = bp.Value.N, Z = bp.Value.Z;
            CreateForm.LastExcel = path;

            BasePoint sp = BasePoint.GetSurveyPoint(doc);
            BasePoint pbp = BasePoint.GetProjectBasePoint(doc);
            ProjectLocation loc = doc.ActiveProjectLocation;
            ProjectPosition now = loc.GetProjectPosition(XYZ.Zero);

            var ask = new TaskDialog("AB Bridges")
            {
                MainInstruction = "Import Base Point",
                MainContent =
                    $"Base point from the Excel:  E {E:0.000}   N {N:0.000}   Z {Z:0.000}\n\n" +
                    "Now:\n" +
                    $"   internal origin (shared):  E {M(now.EastWest):0.000}   N {M(now.NorthSouth):0.000}   Z {M(now.Elevation):0.000}   angle {now.Angle * 180 / Math.PI:0.0000}°\n" +
                    $"   survey point (shared):  {S(sp.SharedPosition)}\n" +
                    $"   project base point (shared):  {S(pbp.SharedPosition)}\n\n" +
                    "After:\n" +
                    $"   internal origin = survey point = project base point = E {E:0.000}   N {N:0.000}   Z {Z:0.000}\n" +
                    "   angle to true north 0° (project north = Civil north)\n\n" +
                    "Every element already in this project keeps its place in the model but gets new shared coordinates. Use it in the bridge file, not in the team model.",
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Ok
            };
            if (ask.Show() != TaskDialogResult.Ok) return Result.Cancelled;

            var notes = new System.Collections.Generic.List<string>();
            var target = new ProjectPosition(Ft(E), Ft(N), Ft(Z), 0);
            using (var t = new Transaction(doc, "AB Bridges - Import Base Point"))
            {
                t.Start();
                loc.SetProjectPosition(XYZ.Zero, target);
                doc.Regenerate();

                // project base point on the internal origin (it does not change the coordinates)
                try
                {
                    XYZ v = XYZ.Zero - pbp.Position;
                    if (v.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, pbp.Id, v);
                }
                catch (Exception ex) { notes.Add("The project base point could not be moved: " + ex.Message); }

                // survey point on the internal origin: unclipped, so only the marker moves
                try
                {
                    PropertyInfo clip = typeof(BasePoint).GetProperty("Clipped");
                    bool canUnclip = clip != null && clip.CanWrite;
                    if (canUnclip) clip.SetValue(sp, false);
                    XYZ v = XYZ.Zero - sp.Position;
                    if (v.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, sp.Id, v);
                    if (!canUnclip) notes.Add("The survey point could not be unclipped through the API.");
                }
                catch (Exception ex) { notes.Add("The survey point could not be moved: " + ex.Message); }
                doc.Regenerate();

                // the coordinates win: if moving the survey point changed them, set them again
                ProjectPosition after = loc.GetProjectPosition(XYZ.Zero);
                if (Math.Abs(after.EastWest - target.EastWest) > 1e-4 || Math.Abs(after.NorthSouth - target.NorthSouth) > 1e-4 ||
                    Math.Abs(after.Elevation - target.Elevation) > 1e-4 || Math.Abs(after.Angle) > 1e-9)
                {
                    loc.SetProjectPosition(XYZ.Zero, target);
                    doc.Regenerate();
                    notes.Add("Moving the survey point changed the coordinates, so they were set again; the survey point may not sit on the base point.");
                }
                t.Commit();
            }

            ProjectPosition fin = loc.GetProjectPosition(XYZ.Zero);
            sp = BasePoint.GetSurveyPoint(doc);
            pbp = BasePoint.GetProjectBasePoint(doc);
            new TaskDialog("AB Bridges")
            {
                MainInstruction = "Import Base Point - done",
                MainContent =
                    $"Internal origin (shared):  E {M(fin.EastWest):0.000}   N {M(fin.NorthSouth):0.000}   Z {M(fin.Elevation):0.000}   angle {fin.Angle * 180 / Math.PI:0.0000}°\n" +
                    $"Survey point:  {S(sp.SharedPosition)}   (distance from the origin {M(sp.Position.GetLength()):0.000} m)\n" +
                    $"Project base point:  {S(pbp.SharedPosition)}   (distance from the origin {M(pbp.Position.GetLength()):0.000} m)\n" +
                    (notes.Count > 0 ? "\n" + string.Join("\n", notes) + "\n" : "") +
                    "\nNext: Create Deck builds the deck with this point as its (0,0,0)."
            }.Show();
            return Result.Succeeded;
        }
    }
}
