// AB Bridges for Revit - reads a loaded family: name, adaptive points, all its parameters
// (name, instance/type, kind, unit, current value). Built-in parameters are left out.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace ABBridgesRevit
{
    internal sealed class FamParam
    {
        public string Name;
        public bool IsInstance;
        public string Kind;          // e.g. "Length"
        public string Unit;          // e.g. "m", "°", "" (no unit)
        public object Value;         // double, string or null
        public string Formula;
        public bool IsReporting;
        public bool IsYesNo;
        public bool HasFormula => !string.IsNullOrWhiteSpace(Formula);

        public string Header =>
            Name + (string.IsNullOrEmpty(Unit) ? "" : " (" + Unit + ")") + (IsInstance ? "" : " [Type]");
    }

    internal sealed class FamInfo
    {
        public string Name;
        public int AdaptivePoints = -1;
        public List<FamParam> All = new List<FamParam>();      // every parameter (not built-in)
        public List<FamParam> Params = new List<FamParam>();   // the ones written to Excel

        public FamInfo WithSelection(ICollection<string> names)
        {
            return new FamInfo { Name = Name, AdaptivePoints = AdaptivePoints, All = All, Params = All.Where(p => !p.HasFormula && names.Contains(p.Name)).ToList() };
        }

        // default: what he edits - no formulas, no reporting parameters
        public List<string> DefaultSelection() => All.Where(p => !p.HasFormula && !p.IsReporting).Select(p => p.Name).ToList();

        // parameters that can be chosen (formula parameters are never shown or written)
        public List<FamParam> Choosable => All.Where(p => !p.HasFormula).ToList();
    }

    internal static class FamilyReader
    {
        public static FamInfo Read(Document doc, Family fam)
        {
            var info = new FamInfo { Name = fam.Name };
            Document fdoc = doc.EditFamily(fam);
            try
            {
                try { info.AdaptivePoints = AdaptiveComponentFamilyUtils.GetNumberOfAdaptivePoints(fam); } catch { }

                FamilyManager fm = fdoc.FamilyManager;
                FamilyType ft = fm.CurrentType;
                foreach (FamilyParameter p in fm.GetParameters())
                {
                    if (p?.Definition == null) continue;
                    if (p.Definition is InternalDefinition idef && idef.BuiltInParameter != BuiltInParameter.INVALID) continue;

                    var fp = new FamParam
                    {
                        Name = p.Definition.Name,
                        IsInstance = p.IsInstance,
                        Formula = p.Formula
                    };
                    try { fp.IsReporting = p.IsReporting; } catch { }
                    ForgeTypeId spec = null;
                    try { spec = p.Definition.GetDataType(); } catch { }
                    try { fp.Kind = spec != null && !spec.Empty() ? LabelUtils.GetLabelForSpec(spec) : p.StorageType.ToString(); }
                    catch { fp.Kind = p.StorageType.ToString(); }
                    fp.Unit = UnitOf(spec);
                    fp.IsYesNo = Is(spec, SpecTypeId.Boolean.YesNo);
                    fp.Value = ValueOf(ft, p, spec);
                    info.All.Add(fp);
                }
            }
            finally
            {
                fdoc.Close(false);
            }
            info.Params = info.All.Where(p => !p.HasFormula && !p.IsReporting).ToList();
            return info;
        }

        private static bool Is(ForgeTypeId a, ForgeTypeId b) => a != null && b != null && a.Equals(b);

        private static string UnitOf(ForgeTypeId spec)
        {
            if (Is(spec, SpecTypeId.Length)) return "m";
            if (Is(spec, SpecTypeId.Angle)) return "°";
            if (Is(spec, SpecTypeId.Area)) return "m²";
            if (Is(spec, SpecTypeId.Volume)) return "m³";
            if (Is(spec, SpecTypeId.Slope)) return "%";
            return "";
        }

        private static object ValueOf(FamilyType ft, FamilyParameter p, ForgeTypeId spec)
        {
            if (ft == null) return null;
            try
            {
                if (!ft.HasValue(p)) return null;
                switch (p.StorageType)
                {
                    case StorageType.Double:
                        double? d = ft.AsDouble(p);
                        if (!d.HasValue) return null;
                        double v = d.Value;
                        if (Is(spec, SpecTypeId.Length)) v = UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Meters);
                        else if (Is(spec, SpecTypeId.Angle)) v = UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Degrees);
                        else if (Is(spec, SpecTypeId.Area)) v = UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.SquareMeters);
                        else if (Is(spec, SpecTypeId.Volume)) v = UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.CubicMeters);
                        else if (Is(spec, SpecTypeId.Slope)) v = UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Percentage);
                        return Math.Round(v, 6);
                    case StorageType.Integer:
                        int? i = ft.AsInteger(p);
                        if (!i.HasValue) return null;
                        if (Is(spec, SpecTypeId.Boolean.YesNo)) return i.Value != 0 ? "☑" : "☐";
                        return (double)i.Value;
                    case StorageType.String:
                        return ft.AsString(p);
                    default:
                        return null;
                }
            }
            catch { return null; }
        }
    }

    // chosen parameters per family, kept between Revit sessions (%APPDATA%\ABBridges\RevitParams.txt)
    internal static class ParamSelection
    {
        private static Dictionary<string, List<string>> _map;
        private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ABBridges", "RevitParams.txt");

        private static void Load()
        {
            if (_map != null) return;
            _map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    string[] p = line.Split('\t');
                    if (p.Length >= 1 && p[0].Length > 0) _map[p[0]] = p.Skip(1).Where(x => x.Length > 0).ToList();
                }
            }
            catch { }
        }

        public static List<string> Get(FamInfo f)
        {
            Load();
            if (_map.TryGetValue(f.Name, out List<string> names))
            {
                // keep only names that still exist in the family
                var exist = new HashSet<string>(f.Choosable.Select(p => p.Name));
                return names.Where(exist.Contains).ToList();
            }
            return f.DefaultSelection();
        }

        public static void Set(string family, IEnumerable<string> names)
        {
            Load();
            _map[family] = names.ToList();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, _map.Select(kv => kv.Key + "\t" + string.Join("\t", kv.Value)));
            }
            catch { }
        }
    }
}
