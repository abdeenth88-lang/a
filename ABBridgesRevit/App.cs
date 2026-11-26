// AB Bridges for Revit - ribbon tab "AB Bridges": panel "Create" (Create Deck, Substructure) and
// panel "Revit Link" (Read Family Parameters, Import Base Point, Place Deck).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace ABBridgesRevit
{
    public class App : IExternalApplication
    {
        private const string Tab = "AB Bridges";

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                try { app.CreateRibbonTab(Tab); } catch { /* already there */ }
                string asm = Assembly.GetExecutingAssembly().Location;
                RibbonPanel about = app.GetRibbonPanels(Tab).FirstOrDefault(p => p.Name == "AB Bridges") ?? app.CreateRibbonPanel(Tab, "AB Bridges");
                if (about.GetItems().Count == 0)
                    about.AddItem(new PushButtonData("AB_About", "About", asm, typeof(AboutCommand).FullName)
                    {
                        ToolTip = "AB Bridges for Revit 2025 - created by " + Brand.Author + " (" + Brand.Email + ").",
                        LargeImage = Icon("AB_logo_256.png", 32), Image = Icon("AB_logo_256.png", 16)
                    });
                RibbonPanel create = app.GetRibbonPanels(Tab).FirstOrDefault(p => p.Name == "Create") ?? app.CreateRibbonPanel(Tab, "Create");
                RibbonPanel link = app.GetRibbonPanels(Tab).FirstOrDefault(p => p.Name == "Revit Link") ?? app.CreateRibbonPanel(Tab, "Revit Link");
                RibbonPanel tendon = app.GetRibbonPanels(Tab).FirstOrDefault(p => p.Name == "Tendon Profile") ?? app.CreateRibbonPanel(Tab, "Tendon Profile");
                RibbonPanel excel = app.GetRibbonPanels(Tab).FirstOrDefault(p => p.Name == "Excel") ?? app.CreateRibbonPanel(Tab, "Excel");
                excel.AddItem(new PushButtonData("AB_ImportExcel", "Import\nExcel", asm, typeof(ImportExcelCommand).FullName)
                {
                    ToolTip = "Opens the Excel (sheet To Revit), select the tables with the mouse (Ctrl for more than one), press Done: each one becomes a table in a drafting view, black and white.",
                    LargeImage = Icon("icon_RF.png", 32), Image = Icon("icon_RF.png", 16)
                });
                excel.AddItem(new PushButtonData("AB_UpdateExcel", "Update\nExcel", asm, typeof(UpdateExcelCommand).FullName)
                {
                    ToolTip = "Reads every imported table again from its Excel file and range, and redraws it in place.",
                    LargeImage = Icon("icon_PD.png", 32), Image = Icon("icon_PD.png", 16)
                });
                tendon.AddItem(new PushButtonData("AB_TendonExcel", "Tendon\nExcel", asm, typeof(TendonExcelCommand).FullName)
                {
                    ToolTip = "Step 1: choose the bridge Excel, the number of paths and of splits per path. Makes the sheet Tendon Profile again (Anchor1, empty rows, Anchor2 and the data); you type every X and Y.",
                    LargeImage = Icon("icon_RF.png", 32), Image = Icon("icon_RF.png", 16)
                });
                tendon.AddItem(new PushButtonData("AB_CreateTendon", "Draw\nTendon", asm, typeof(CreateTendonCommand).FullName)
                {
                    ToolTip = "Step 2: reads the sheet Tendon Profile, you choose the split(s) of every path, then it draws one long view per path cut by crop boxes into its stages, the 3D tendons and the sheets (one per stage). Run again to redraw: the sheets stay.",
                    LargeImage = Icon("icon_CD.png", 32), Image = Icon("icon_CD.png", 16)
                });

                create.AddItem(new PushButtonData("AB_CreateDeck", "Create\nDeck", asm, typeof(CreateDeckCommand).FullName)
                {
                    ToolTip = "Builds the deck, corbels, outriggers, barriers ... from the bridge Excel. Each family and each corbel / outrigger can be built on its own; re-building replaces only what is checked.",
                    LargeImage = Icon("icon_CD.png", 32),
                    Image = Icon("icon_CD.png", 16)
                });
                create.AddItem(new PushButtonData("AB_Substructure", "Substructure", asm, typeof(SubstructureCommand).FullName)
                {
                    ToolTip = "Bearings, pier caps, piers, pile caps, piles ... at every axis: choose the families per axis, set them from the design surface or the NGL on the axis section, then create.",
                    LargeImage = Icon("icon_SS.png", 32),
                    Image = Icon("icon_SS.png", 16)
                });

                link.AddItem(new PushButtonData("AB_ReadFamilyParams", "Read Family\nParameters", asm, typeof(ReadFamilyParamsCommand).FullName)
                {
                    ToolTip = "Reads the families (names and all parameters) and writes them into the Parameters sheet of the AB Bridges Excel.",
                    LargeImage = Icon("icon_RF.png", 32),
                    Image = Icon("icon_RF.png", 16)
                });
                link.AddItem(new PushButtonData("AB_ImportBasePoint", "Import\nBase Point", asm, typeof(ImportBasePointCommand).FullName)
                {
                    ToolTip = "Reads the base point from the bridge Excel (Export Base Point in Civil) and puts the internal origin, the survey point and the project base point on it (angle to true north 0). Use it in the bridge file.",
                    LargeImage = Icon("icon_BP.png", 32),
                    Image = Icon("icon_BP.png", 16)
                });
                link.AddItem(new PushButtonData("AB_PlaceDeck", "Place\nDeck", asm, typeof(PlaceDeckCommand).FullName)
                {
                    ToolTip = "Places a deck family built by hand in an adaptive family (Create Deck) at its shared coordinates.",
                    LargeImage = Icon("icon_PD.png", 32),
                    Image = Icon("icon_PD.png", 16)
                });
            }
            catch { }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;

        private static ImageSource Icon(string file, int size)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(file, StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;
                using (Stream s = asm.GetManifestResourceStream(name))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = s;
                    bmp.DecodePixelWidth = size;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch { return null; }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ReadFamilyParamsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try { return Run(data, ref message); }
            catch (Exception ex)
            {
                var td = new TaskDialog("AB Bridges - error")
                {
                    MainInstruction = ex.GetType().Name + ": " + ex.Message,
                    ExpandedContent = ex.ToString()
                };
                td.Show();
                return Result.Cancelled;
            }
        }

        private Result Run(ExternalCommandData data, ref string message)
        {
            Document doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }
            if (doc.IsFamilyDocument) { TaskDialog.Show("AB Bridges", "Open the project that has the families loaded (not a family file)."); return Result.Cancelled; }

            var fams = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => f.IsEditable && !f.IsInPlace)
                .Select(f => new FamItem { Name = f.Name, Adaptive = IsAdaptive(f), Family = f })
                .OrderByDescending(f => f.Adaptive).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (fams.Count == 0) { TaskDialog.Show("AB Bridges", "No editable families are loaded in this project."); return Result.Cancelled; }

            using (var form = new ParamsForm(fams, item => item?.Family is Family fam ? FamilyReader.Read(doc, fam) : null))
            {
                if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;

                // Family 1 -> first family group of the sheet, Family 2 -> second ... (extra families add new groups)
                var fills = new List<GroupFill>();
                for (int i = 0; i < form.Result.Count; i++)
                    fills.Add(new GroupFill { Number = i + 1, Family = form.Result[i] });

                string report;
                try { report = XlsxPatch.Write(form.ExcelPath, fills); }
                catch (IOException) { TaskDialog.Show("AB Bridges", "The Excel file is open. Close it in Excel and try again."); return Result.Failed; }
                catch (Exception ex) { TaskDialog.Show("AB Bridges", "Could not write the Excel: " + ex.Message); return Result.Failed; }

                var td = new TaskDialog("AB Bridges")
                {
                    MainInstruction = "Parameters sheet updated",
                    MainContent = report + "\n" + form.ExcelPath
                };
                td.Show();
            }
            return Result.Succeeded;
        }

        private static bool IsAdaptive(Family f)
        {
            try { return AdaptiveComponentFamilyUtils.IsAdaptiveComponentFamily(f); } catch { return false; }
        }
    }
}
