// AB Bridges - drawing the result in the DWG and the temporary preview.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using Autodesk.Civil.ApplicationServices;

namespace ABBridges
{
    internal static class Draw
    {
        public const string LayerAxis = "BRG-AXIS";
        public const string LayerDiaph = "BRG-DIAPHRAGM";
        public const string LayerCorbel = "BRG-CORBEL";
        public const string LayerNear = "BRG-D1-D2";
        public const string LayerCj = "BRG-CJ";
        public const string LayerDiv = "BRG-DIVISION";
        public const string LayerLabel = "BRG-LABEL";
        public const string LayerSpan = "BRG-SPAN-CENTER";

        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        public static string LayerFor(SectionKind k)
        {
            switch (k)
            {
                case SectionKind.Axis: return LayerAxis;
                case SectionKind.DiaphragmFace: return LayerDiaph;
                case SectionKind.Corbel: return LayerCorbel;
                case SectionKind.NearDiaphragm: return LayerNear;
                case SectionKind.ConstructionJoint: return LayerCj;
                default: return LayerDiv;
            }
        }

        public static short AciFor(SectionKind k)
        {
            switch (k)
            {
                case SectionKind.Axis: return 1;
                case SectionKind.DiaphragmFace: return 30;
                case SectionKind.Corbel: return 3;
                case SectionKind.NearDiaphragm: return 5;
                case SectionKind.ConstructionJoint: return 200;
                default: return 4;
            }
        }

        // sections + span lengths; returns every created entity id
        public static List<ObjectId> Result(Transaction tr, Database db, List<Section> sections, List<SpanLength> spans,
                                           string bridgeName, bool featureLines, out int featureLineFailures)
        {
            var created = new List<ObjectId>();
            featureLineFailures = 0;

            EnsureLayer(tr, db, LayerAxis, 1);
            EnsureLayer(tr, db, LayerDiaph, 30);
            EnsureLayer(tr, db, LayerCorbel, 3);
            EnsureLayer(tr, db, LayerNear, 5);
            EnsureLayer(tr, db, LayerCj, 200);
            EnsureLayer(tr, db, LayerDiv, 4);
            EnsureLayer(tr, db, LayerLabel, 7);
            EnsureLayer(tr, db, LayerSpan, 6);

            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            string runTag = DateTime.Now.ToString("HHmmss", CI);
            int idx = 0;
            foreach (Section s in sections)
            {
                idx++;
                if (!s.HasEdges) continue;
                string layer = LayerFor(s.Kind);

                double zl = double.IsNaN(s.LeftZ) ? 0.0 : s.LeftZ;
                double zr = double.IsNaN(s.RightZ) ? 0.0 : s.RightZ;
                var pts = new Point3dCollection
                {
                    new Point3d(s.Left.X, s.Left.Y, zl),
                    new Point3d(s.Right.X, s.Right.Y, zr)
                };
                var p3d = new Polyline3d(Poly3dType.SimplePoly, pts, false) { Layer = layer };
                ObjectId pid = ms.AppendEntity(p3d);
                tr.AddNewlyCreatedDBObject(p3d, true);

                bool done = false;
                if (featureLines)
                {
                    try
                    {
                        ObjectId flId = CreateFeatureLine($"{bridgeName} {idx:000} {s.Remark} {runTag}", pid);
                        var fl = (AcEntity)tr.GetObject(flId, OpenMode.ForWrite);
                        fl.Layer = layer;
                        p3d.Erase();
                        created.Add(flId);
                        done = true;
                    }
                    catch
                    {
                        featureLineFailures++;
                    }
                }
                if (!done) created.Add(pid);
            }

            // span lengths: the automatic center curve as a polyline, and the length next to the line
            foreach (SpanLength sl in spans ?? new List<SpanLength>())
            {
                if (double.IsNaN(sl.Length)) continue;
                if (!sl.FromCenterline && sl.Curve.Count >= 2)
                {
                    var pl = new Polyline { Layer = LayerSpan };
                    for (int i = 0; i < sl.Curve.Count; i++)
                        pl.AddVertexAt(i, new Point2d(sl.Curve[i].X, sl.Curve[i].Y), 0, 0, 0);
                    created.Add(ms.AppendEntity(pl));
                    tr.AddNewlyCreatedDBObject(pl, true);
                }
                Vector3d d = sl.LabelDir;
                double rot = Math.Atan2(d.Y, d.X);
                if (rot > Math.PI / 2 || rot <= -Math.PI / 2) rot += Math.PI;     // keep the text readable
                Vector3d up = new Vector3d(-Math.Sin(rot), Math.Cos(rot), 0);
                var t = new DBText
                {
                    TextString = $"{sl.Name}  L = {sl.Length.ToString("0.000", CI)} m" + (sl.FromCenterline ? " (centerline)" : " (auto)"),
                    Height = 0.50,
                    Layer = LayerSpan,
                    Rotation = rot,
                    HorizontalMode = TextHorizontalMode.TextCenter,
                    VerticalMode = TextVerticalMode.TextBottom
                };
                Point3d at = sl.LabelPoint + up * 0.30;
                t.Position = at;
                t.AlignmentPoint = at;
                created.Add(ms.AppendEntity(t));
                tr.AddNewlyCreatedDBObject(t, true);
                t.AdjustAlignment(db);
            }
            return created;
        }

        public static string KindName(SectionKind k)
        {
            switch (k)
            {
                case SectionKind.Axis: return "Axis";
                case SectionKind.DiaphragmFace: return "Diaphragm";
                case SectionKind.Corbel: return "Corbel";
                case SectionKind.NearDiaphragm: return "D1-D2";
                case SectionKind.ConstructionJoint: return "Construction Joint";
                default: return "Division";
            }
        }

        // one feature line style per station type ("AB Bridges - Axis" ...), every display component in the type color
        private static ObjectId FeatureLineStyle(Transaction tr, SectionKind k)
        {
            try
            {
                string name = "AB Bridges - " + KindName(k);
                dynamic col = CivilApplication.ActiveDocument.Styles.FeatureLineStyles;
                ObjectId id = (bool)col.Contains(name) ? (ObjectId)col[name] : (ObjectId)col.Add(name);
                DBObject st = tr.GetObject(id, OpenMode.ForWrite);
                AcColor color = AcColor.FromColorIndex(AcColorMethod.ByAci, AciFor(k));
                foreach (var m in st.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!m.Name.StartsWith("Get") || !m.Name.Contains("DisplayStyle")) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 0) SetColor(m.Invoke(st, null), color);
                    else if (ps.Length == 1 && ps[0].ParameterType.IsEnum)
                        foreach (object v in Enum.GetValues(ps[0].ParameterType))
                        {
                            try { SetColor(m.Invoke(st, new[] { v }), color); } catch { }
                        }
                }
                return id;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static void SetColor(object displayStyle, AcColor color)
        {
            if (displayStyle == null) return;
            var p = displayStyle.GetType().GetProperty("Color");
            if (p != null && p.CanWrite && p.PropertyType == typeof(AcColor))
            {
                try { p.SetValue(displayStyle, color); } catch { }
            }
        }

        // FeatureLine.Create overloads differ between releases, pick one at run time.
        private static ObjectId CreateFeatureLine(string name, ObjectId entityId)
        {
            var methods = typeof(CivFeatureLine)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name == "Create" && m.ReturnType == typeof(ObjectId))
                .ToList();

            foreach (var m in methods)
            {
                var p = m.GetParameters();
                if (p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(ObjectId))
                    return (ObjectId)m.Invoke(null, new object[] { name, entityId });
            }
            foreach (var m in methods)
            {
                var p = m.GetParameters();
                if (p.Length == 3 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(ObjectId) && p[2].ParameterType == typeof(ObjectId))
                    return (ObjectId)m.Invoke(null, new object[] { name, entityId, ObjectId.Null });
            }
            throw new InvalidOperationException("FeatureLine.Create(name, entityId) is not available.");
        }

        private static void EnsureLayer(Transaction tr, Database db, string name, short aci)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return;
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = name, Color = AcColor.FromColorIndex(AcColorMethod.ByAci, aci) };
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }
    }

    // ---------------- temporary preview graphics ----------------

    internal sealed class PreviewGraphics : IDisposable
    {
        private readonly List<Drawable> _items = new List<Drawable>();

        public void Show(List<Section> sections, List<SpanLength> spans = null)
        {
            Clear();
            var tm = TransientManager.CurrentTransientManager;
            foreach (Section s in sections)
            {
                Point3d a, b;
                if (s.HasEdges) { a = s.Left; b = s.Right; }
                else { a = s.ClPoint - s.Dir * 5.0; b = s.ClPoint + s.Dir * 5.0; }
                var ln = new Line(Geo.Flat(a), Geo.Flat(b))
                {
                    Color = AcColor.FromColorIndex(AcColorMethod.ByAci, Draw.AciFor(s.Kind))
                };
                Add(tm, ln);
            }
            foreach (SpanLength sl in spans ?? new List<SpanLength>())
            {
                if (sl.FromCenterline || sl.Curve.Count < 2) continue;
                var pl = new Polyline { Color = AcColor.FromColorIndex(AcColorMethod.ByAci, 6) };
                for (int i = 0; i < sl.Curve.Count; i++)
                    pl.AddVertexAt(i, new Point2d(sl.Curve[i].X, sl.Curve[i].Y), 0, 0, 0);
                Add(tm, pl);
            }
        }

        private void Add(TransientManager tm, Drawable d)
        {
            tm.AddTransient(d, TransientDrawingMode.Main, 128, new IntegerCollection());
            _items.Add(d);
        }

        public void Clear()
        {
            var tm = TransientManager.CurrentTransientManager;
            foreach (Drawable d in _items)
            {
                try { tm.EraseTransient(d, new IntegerCollection()); } catch { }
                (d as IDisposable)?.Dispose();
            }
            _items.Clear();
        }

        public void Dispose() => Clear();
    }
}
