// AB Bridges - values kept between openings of the dialog, and the NGL saved in the drawing.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace ABBridges
{
    internal sealed class AxisParams
    {
        public string Name;
        public double Diaphragm = double.NaN;
        public double BeforeD1 = 2.70, BeforeD2 = 2.70, AfterD1 = 2.70, AfterD2 = 2.70;
    }

    internal sealed class CjSpan
    {
        public bool Enabled;
        public double AutoLength = double.NaN;
        public double? UserLength;          // typed by the user (overrides the computed length)
        public double Factor = 0.275, Dist1 = 1.000, Dist2 = 6.600;
        public double Length => UserLength ?? AutoLength;
    }

    // internal diaphragms of one span: Count pairs of sections, Gap apart, equally spaced between the axes (CL)
    internal sealed class IdSpan
    {
        public int Count = 2;
        public double Gap = 0.30;
    }

    internal static class BridgeSettings
    {
        // stations near every diaphragm face: distance of each from the face (m)
        public static List<double> NearDist = new List<double> { 2.70, 5.40 };
        public static Dictionary<string, IdSpan> Internal = new Dictionary<string, IdSpan>();
        public static bool InternalAll = true;
        public static IdSpan IdOf(string key)
        {
            if (!Internal.TryGetValue(key, out IdSpan v)) { v = new IdSpan(); Internal[key] = v; }
            return v;
        }

        public static Database Db;
        public static string BridgeName = "Bridge";

        public static List<ObjectId> DesignIds = new List<ObjectId>();
        public static List<ObjectId> NglIds = new List<ObjectId>();
        public static List<ObjectId> UnderIds = new List<ObjectId>();
        public static ObjectId AlignmentId, CenterlineId;
        public static List<ObjectId> LeftIds = new List<ObjectId>();     // an edge can be made of several pieces
        public static List<ObjectId> RightIds = new List<ObjectId>();
        public static List<ObjectId> AxisIds = new List<ObjectId>();
        public static double StartStation = 0.0;
        public static bool ReverseAlignment;          // use the alignment in the opposite direction (drawing not changed)

        public static Dictionary<ObjectId, AxisParams> Axis = new Dictionary<ObjectId, AxisParams>();
        public static double DefaultDiaphragm = 1.50;
        public static double CorbelStart = 0.50, CorbelEnd = 0.50, Spacing = 2.00;
        public static int SkewTransition = 5;

        public static Dictionary<string, CjSpan> Cj = new Dictionary<string, CjSpan>();       // key = span key
        public static Dictionary<string, double> RoadDepth = new Dictionary<string, double>();  // key = road name

        public static string OutputFolder = "";
        public static bool FeatureLines = true, ReplacePrevious = true, OpenExcel = true;
        public static List<ObjectId> LastRun = new List<ObjectId>();

        public static bool Valid(ObjectId id) => !id.IsNull && id.IsValid && !id.IsErased;

        // a new drawing starts empty; only the NGL comes back from the drawing itself
        public static void ResetIfOtherDrawing(Database db)
        {
            if (Db == db) return;
            Db = db;
            BridgeName = "Bridge";
            DesignIds = new List<ObjectId>();
            UnderIds = new List<ObjectId>();
            AlignmentId = CenterlineId = ObjectId.Null;
            LeftIds = new List<ObjectId>();
            RightIds = new List<ObjectId>();
            AxisIds = new List<ObjectId>();
            StartStation = 0.0;
            ReverseAlignment = false;
            Axis.Clear();
            Cj.Clear();
            Internal.Clear();
            RoadDepth.Clear();
            LastRun.Clear();
            OutputFolder = "";
            NglIds = DwgStore.LoadNgl(db);
        }

        public static AxisParams ParamsOf(ObjectId id)
        {
            if (!Axis.TryGetValue(id, out AxisParams p))
            {
                p = new AxisParams();
                Axis[id] = p;
            }
            return p;
        }

        public static void ApplyAxisParams(AxisInfo a, int axisCount)
        {
            AxisParams p = ParamsOf(a.Id);
            if (!string.IsNullOrWhiteSpace(p.Name)) a.Name = p.Name;
            a.Diaphragm = double.IsNaN(p.Diaphragm) ? DefaultDiaphragm : p.Diaphragm;
            bool first = a.Index == 0, last = a.Index == axisCount - 1;
            a.BeforeD1 = first ? 0 : p.BeforeD1;
            a.BeforeD2 = first ? 0 : p.BeforeD2;
            a.AfterD1 = last ? 0 : p.AfterD1;
            a.AfterD2 = last ? 0 : p.AfterD2;
        }

        public static string SpanKey(AxisInfo a, AxisInfo b) => a.Id.Handle.ToString() + "-" + b.Id.Handle.ToString();

        public static CjSpan CjOf(string key)
        {
            if (!Cj.TryGetValue(key, out CjSpan c))
            {
                c = new CjSpan();
                Cj[key] = c;
            }
            return c;
        }
    }

    // NGL surface(s) saved inside the drawing (NOD x-record), so the same file remembers them
    internal static class DwgStore
    {
        private const string Key = "AB_BRIDGES_SETTINGS";
        private const string Tag = "NGL";

        public static List<ObjectId> LoadNgl(Database db)
        {
            var res = new List<ObjectId>();
            try
            {
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                    if (nod.Contains(Key))
                    {
                        var xr = (Xrecord)tr.GetObject(nod.GetAt(Key), OpenMode.ForRead);
                        if (xr.Data != null)
                        {
                            bool ngl = false;
                            foreach (TypedValue tv in xr.Data)
                            {
                                string s = Convert.ToString(tv.Value);
                                if (s == Tag) { ngl = true; continue; }
                                if (!ngl) continue;
                                try
                                {
                                    long h = Convert.ToInt64(s, 16);
                                    if (db.TryGetObjectId(new Handle(h), out ObjectId id) && BridgeSettings.Valid(id))
                                        res.Add(id);
                                }
                                catch { }
                            }
                        }
                    }
                    tr.Commit();
                }
            }
            catch { }
            return res;
        }

        // call with the document locked
        public static void SaveNgl(Database db, IEnumerable<ObjectId> ids)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                var rb = new ResultBuffer(new TypedValue((int)DxfCode.Text, Tag));
                foreach (ObjectId id in ids.Where(BridgeSettings.Valid))
                    rb.Add(new TypedValue((int)DxfCode.Text, id.Handle.ToString()));

                if (nod.Contains(Key))
                {
                    var xr = (Xrecord)tr.GetObject(nod.GetAt(Key), OpenMode.ForWrite);
                    xr.Data = rb;
                }
                else
                {
                    nod.UpgradeOpen();
                    var xr = new Xrecord { Data = rb };
                    nod.SetAt(Key, xr);
                    tr.AddNewlyCreatedDBObject(xr, true);
                }
                tr.Commit();
            }
        }
    }
}
