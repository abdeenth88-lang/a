// AB Bridges for Revit - a small key / value map kept inside a document (Extensible Storage on a DataStorage element).
// Used by Create Deck (what each run created, the diaphragm solid of every axis, the outrigger solids) and by
// Substructure (the settings of every axis and what was placed).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace ABBridgesRevit
{
    internal static class AbStore
    {
        private static readonly Guid SchemaId = new Guid("8A3F2C6E-5B1D-4E7A-9C24-1F6B3D8E0A57");
        private const string Field = "Map";

        private static Schema GetSchema()
        {
            Schema s = Schema.Lookup(SchemaId);
            if (s != null) return s;
            var b = new SchemaBuilder(SchemaId);
            b.SetSchemaName("ABBridgesMap");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.SetVendorId("ABDEEN");
            b.AddSimpleField(Field, typeof(string));
            return b.Finish();
        }

        private static DataStorage Find(Document doc)
        {
            Schema s = Schema.Lookup(SchemaId);
            if (s == null) return null;
            foreach (DataStorage ds in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
            {
                try
                {
                    Entity e = ds.GetEntity(s);
                    if (e != null && e.IsValid()) return ds;
                }
                catch { }
            }
            return null;
        }

        public static Dictionary<string, string> Load(Document doc)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                DataStorage ds = Find(doc);
                if (ds == null) return map;
                string text = ds.GetEntity(Schema.Lookup(SchemaId)).Get<string>(Field) ?? "";
                foreach (string line in text.Split('\n'))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0) map[line.Substring(0, tab)] = line.Substring(tab + 1);
                }
            }
            catch { }
            return map;
        }

        // inside an open transaction
        public static bool Save(Document doc, Dictionary<string, string> map)
        {
            try
            {
                Schema s = GetSchema();
                DataStorage ds = Find(doc) ?? DataStorage.Create(doc);
                var sb = new StringBuilder();
                foreach (var kv in map.Where(kv => !string.IsNullOrEmpty(kv.Key)))
                    sb.Append(kv.Key.Replace("\t", " ").Replace("\n", " ")).Append('\t').Append((kv.Value ?? "").Replace("\n", " ")).Append('\n');
                var e = new Entity(s);
                e.Set(Field, sb.ToString());
                ds.SetEntity(e);
                return true;
            }
            catch { return false; }
        }

        // element ids <-> text
        public static string Ids(IEnumerable<ElementId> ids) => string.Join(",", ids.Select(i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public static List<ElementId> Ids(string text)
        {
            var res = new List<ElementId>();
            foreach (string p in (text ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(p.Trim(), out long v)) res.Add(new ElementId(v));
            return res;
        }
    }
}
