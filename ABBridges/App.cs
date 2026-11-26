// AB Bridges - commands, start-up and the "AB Bridges" ribbon tab
//   panel "Bridge Data"    : Extract Data  (ABBRIDGES), Export Base Point (ABBASEPOINT)
//   panel "Drafting Tools" : PL. Start     (PLSTART)

using System;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcCoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(ABBridges.App))]
[assembly: CommandClass(typeof(ABBridges.Commands))]

namespace ABBridges
{
    public class Commands
    {
        [CommandMethod("ABBRIDGES", CommandFlags.Modal)]
        public void ExtractData()
        {
            var doc = AcCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            try
            {
                using (var form = new BridgeForm(doc))
                    AcApp.ShowModalDialog(form);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\nAB Bridges failed: " + ex.Message);
            }
        }

        // old names still work
        [CommandMethod("BRIDGEPTS", CommandFlags.Modal)]
        public void ExtractDataAlias() => ExtractData();

        [CommandMethod("ABBASEPOINT", CommandFlags.Modal)]
        public void ExportBasePoint()
        {
            var doc = AcCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            try { BasePointCmd.Run(doc); }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\nExport Base Point failed: " + ex.Message); }
        }

        [CommandMethod("PLSTART", CommandFlags.Modal)]
        public void PolylineStart()
        {
            var doc = AcCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            try { PlStart.Run(doc); }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\nPL. Start failed: " + ex.Message); }
        }
    }

    public class App : IExtensionApplication
    {
        private const string TabId = "AB_BRIDGES_TAB";

        public void Initialize()
        {
            try
            {
                if (ComponentManager.Ribbon == null)
                    ComponentManager.ItemInitialized += OnItemInitialized;
                else
                    AddTab();

                // the ribbon is rebuilt when the workspace changes
                AcApp.SystemVariableChanged += (s, e) =>
                {
                    if (string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase)) AddTab();
                };
            }
            catch { /* the commands still work without the ribbon */ }
        }

        public void Terminate() { }

        private void OnItemInitialized(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnItemInitialized;
            AddTab();
        }

        private static void AddTab()
        {
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null || ribbon.FindTab(TabId) != null) return;

            var tab = new RibbonTab { Title = "AB Bridges", Id = TabId };
            ribbon.Tabs.Add(tab);

            var data = new RibbonPanelSource { Title = "Bridge Data" };
            tab.Panels.Add(new RibbonPanel { Source = data });
            data.Items.Add(Button("Extract Data", "_ABBRIDGES ", "AB_logo_256.png",
                "Extract Data",
                "Stations at axes, diaphragms, D1/D2, construction joints, corbels and divisions; levels from the design surface(s); Excel + feature lines."));
            data.Items.Add(Button("Export Base Point", "_ABBASEPOINT ", "icon_BP.png",
                "Export Base Point",
                "Pick a point near the bridge and type its Z. It is kept in the drawing, written into the Super Structure sheet, and used in Revit as the survey point / project base point and the (0,0,0) of the deck family."));

            var tools = new RibbonPanelSource { Title = "Drafting Tools" };
            tab.Panels.Add(new RibbonPanel { Source = tools });
            tools.Items.Add(Button("PL. Start", "_PLSTART ", "icon_PL.png",
                "PL. Start",
                "Select a polyline to show its direction (temporary arrows - they disappear when the view moves)."));
        }

        private static RibbonButton Button(string text, string command, string icon, string tipTitle, string tip)
        {
            return new RibbonButton
            {
                Text = text,
                ShowText = true,
                ShowImage = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                LargeImage = Icon(icon, 32),
                Image = Icon(icon, 16),
                CommandParameter = command,
                CommandHandler = new RibbonCommand(),
                ToolTip = new RibbonToolTip { Title = tipTitle, Content = tip }
            };
        }

        // icons are embedded PNG files (Resources\*.png)
        private static ImageSource Icon(string file, int size)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string name = null;
                foreach (string n in asm.GetManifestResourceNames())
                    if (n.EndsWith(file, StringComparison.OrdinalIgnoreCase)) { name = n; break; }
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

    internal sealed class RibbonCommand : ICommand
    {
        public event EventHandler CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter)
        {
            var doc = AcCoreApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            string cmd = (parameter as RibbonButton)?.CommandParameter as string ?? "_ABBRIDGES ";
            doc.SendStringToExecute(cmd, true, false, true);
        }
    }
}
