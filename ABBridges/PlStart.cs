// AB Bridges - PL. Start: shows the direction of a polyline with temporary arrows.
// The arrows disappear as soon as the view moves (pan / zoom / orbit) or another command starts.

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace ABBridges
{
    internal static class PlStart
    {
        private static readonly List<Drawable> Items = new List<Drawable>();
        private static bool _hooked;
        private static Document _doc;

        public static void Run(Document doc)
        {
            Clear();
            Editor ed = doc.Editor;
            var peo = new PromptEntityOptions("\nSelect polyline: ");
            peo.SetRejectMessage("\nSelect a polyline, line, arc or spline.");
            peo.AddAllowedClass(typeof(Curve), false);
            PromptEntityResult res = ed.GetEntity(peo);
            if (res.Status != PromptStatus.OK) return;

            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var c = (Curve)tr.GetObject(res.ObjectId, OpenMode.ForRead);
                Show(c);
                tr.Commit();
            }
            ed.UpdateScreen();
            ed.WriteMessage("\nDirection shown (start → end). Arrows clear when the view moves.\n");
            Hook(doc);
        }

        // arrows on a curve picked elsewhere (e.g. after reversing the alignment)
        public static void ShowCurve(Document doc, Curve c)
        {
            Clear();
            Show(c);
            Hook(doc);
        }

        private static void Show(Curve c)
        {
            double len = c.GetDistanceAtParameter(c.EndParam);
            if (len <= 1e-9) return;

            // arrow size follows the current view so it looks the same at any zoom
            double view = 100.0;
            try { view = Convert.ToDouble(AcApp.GetSystemVariable("VIEWSIZE")); } catch { }
            double size = Math.Max(view / 45.0, 1e-6);
            double gap = Math.Max(size * 3.2, len / 400.0);
            int n = Math.Max(2, (int)Math.Floor(len / gap));
            gap = len / n;

            var tm = TransientManager.CurrentTransientManager;
            var cyan = AcColor.FromRgb(0, 210, 245);
            for (int i = 0; i < n; i++)
            {
                double d = gap * (i + 0.5);
                Point3d p = c.GetPointAtDist(d);
                Vector3d t = c.GetFirstDerivative(c.GetParameterAtDistance(d));
                t = new Vector3d(t.X, t.Y, 0);
                if (t.Length < 1e-12) continue;
                Add(tm, Arrow(p, t.GetNormal(), size, cyan));
            }

            // start dot + END mark
            Point3d s = c.StartPoint, e = c.EndPoint;
            var dot = new Polyline { Color = AcColor.FromRgb(40, 200, 95), Elevation = s.Z, Closed = true };
            double rr = size * 0.35;
            dot.AddVertexAt(0, new Point2d(s.X - rr, s.Y), 1.0, rr * 2, rr * 2);
            dot.AddVertexAt(1, new Point2d(s.X + rr, s.Y), 1.0, rr * 2, rr * 2);
            Add(tm, dot);

            var st = new DBText { TextString = "START", Height = size * 0.6, Position = new Point3d(s.X + size * 0.6, s.Y - size * 1.2, s.Z), Color = AcColor.FromRgb(60, 220, 110) };
            Add(tm, st);
            var en = new DBText { TextString = "END", Height = size * 0.6, Position = new Point3d(e.X + size * 0.6, e.Y + size * 0.4, e.Z), Color = AcColor.FromRgb(240, 90, 80) };
            Add(tm, en);
        }

        // filled arrow head: a wide-to-zero polyline segment
        private static Polyline Arrow(Point3d p, Vector3d t, double size, AcColor color)
        {
            Point3d a = p - t * (size * 0.5), b = p + t * (size * 0.5);
            var pl = new Polyline { Color = color, Elevation = p.Z };
            pl.AddVertexAt(0, new Point2d(a.X, a.Y), 0, size * 0.75, 0);
            pl.AddVertexAt(1, new Point2d(b.X, b.Y), 0, 0, 0);
            return pl;
        }

        private static void Add(TransientManager tm, Drawable d)
        {
            tm.AddTransient(d, TransientDrawingMode.Main, 128, new IntegerCollection());
            Items.Add(d);
        }

        public static void Clear()
        {
            if (Items.Count > 0)
            {
                var tm = TransientManager.CurrentTransientManager;
                foreach (Drawable d in Items)
                {
                    try { tm.EraseTransient(d, new IntegerCollection()); } catch { }
                    (d as IDisposable)?.Dispose();
                }
                Items.Clear();
                try { _doc?.Editor.UpdateScreen(); } catch { }
            }
            Unhook();
        }

        private static string _viewCtr, _viewSize;

        private static string View(string name)
        {
            try { return Convert.ToString(AcApp.GetSystemVariable(name)); } catch { return ""; }
        }

        private static void Hook(Document doc)
        {
            if (_hooked || Items.Count == 0) return;
            _doc = doc;
            _viewCtr = View("VIEWCTR");
            _viewSize = View("VIEWSIZE");
            AcApp.SystemVariableChanged += OnSysVar;
            AcApp.Idle += OnIdle;
            AcApp.PreTranslateMessage += OnMessage;
            doc.CommandWillStart += OnCommand;
            _hooked = true;
        }

        private static void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            AcApp.SystemVariableChanged -= OnSysVar;
            AcApp.Idle -= OnIdle;
            AcApp.PreTranslateMessage -= OnMessage;
            try { if (_doc != null) _doc.CommandWillStart -= OnCommand; } catch { }
        }

        private static void OnSysVar(object sender, Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e)
        {
            string n = e.Name?.ToUpperInvariant();
            if (n == "VIEWCTR" || n == "VIEWSIZE" || n == "VIEWDIR" || n == "VIEWTWIST") Clear();
        }

        // pan / zoom that do not raise the system variable event
        private static void OnIdle(object sender, EventArgs e)
        {
            if (!_hooked) return;
            if (View("VIEWCTR") != _viewCtr || View("VIEWSIZE") != _viewSize) Clear();
        }

        // Esc, mouse wheel (scroll / zoom) or middle button (pan) clear the arrows at once
        private const int WM_KEYDOWN = 0x0100, WM_MOUSEWHEEL = 0x020A, WM_MBUTTONDOWN = 0x0207, VK_ESCAPE = 0x1B;

        private static void OnMessage(object sender, PreTranslateMessageEventArgs e)
        {
            if (!_hooked) return;
            try
            {
                object msg = e.GetType().GetProperty("Message")?.GetValue(e);
                if (msg == null) return;
                long m = Num(msg, "message");
                if (m == WM_MOUSEWHEEL || m == WM_MBUTTONDOWN || (m == WM_KEYDOWN && Num(msg, "wParam") == VK_ESCAPE))
                    Clear();
            }
            catch { }
        }

        // reads a field or property of the MSG structure as a number
        private static long Num(object o, string name)
        {
            var t = o.GetType();
            object v = t.GetField(name)?.GetValue(o) ?? t.GetProperty(name)?.GetValue(o);
            if (v is IntPtr p) return p.ToInt64();
            if (v is UIntPtr u) return (long)u.ToUInt64();
            return v == null ? -1 : Convert.ToInt64(v);
        }

        private static void OnCommand(object sender, CommandEventArgs e) => Clear();
    }
}
