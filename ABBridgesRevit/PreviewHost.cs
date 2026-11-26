// AB Bridges for Revit - the Revit preview of the section at an axis, shown inside the Substructure dialog.
// Zoom to fit when shown. For measuring, the dialog takes a picture of the preview and measures on it: this class
// says where the view is on the screen, what part of the view it shows, and the corners / edges to snap to.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WT = System.Windows.Threading;

namespace ABBridgesRevit
{
    internal sealed class PreviewHost : System.Windows.Forms.Integration.ElementHost, IMeasurable
    {
        private PreviewControl _pc;
        private readonly View _view;
        private readonly List<(double x, double y)> _pts = new List<(double x, double y)>();
        private readonly List<((double x, double y) a, (double x, double y) b)> _segs = new List<((double x, double y) a, (double x, double y) b)>();

        public IList<(double x, double y)> SnapPoints => _pts;
        public IList<((double x, double y) a, (double x, double y) b)> SnapSegments => _segs;

        public PreviewHost(Document doc, ElementId viewId, IEnumerable<XYZ> snapPoints, IEnumerable<(XYZ a, XYZ b)> snapSegs)
        {
            _view = doc.GetElement(viewId) as View;
            _pc = new PreviewControl(doc, viewId);
            Child = _pc;
            if (_view != null)
            {
                foreach (XYZ p in snapPoints ?? Enumerable.Empty<XYZ>()) _pts.Add(V(p));
                foreach (var s in snapSegs ?? Enumerable.Empty<(XYZ a, XYZ b)>()) _segs.Add((V(s.a), V(s.b)));
            }
            // the zoom stays where the user left it: Fit only by the Fit button, or the very first time a view is shown;
            // going back to an axis brings back its last zoom (no fit when the window / panes are resized)
            _pc.Loaded += (s, e) => Later(() =>
            {
                if (_view != null && s_zoom.TryGetValue(_view.Id.Value, out var z)) { try { _pc?.UIView?.ZoomAndCenterRectangle(z.a, z.b); return; } catch { } }
                ZoomFit();
            });
        }

        private static readonly Dictionary<long, (XYZ a, XYZ b)> s_zoom = new Dictionary<long, (XYZ, XYZ)>();

        private void KeepZoom()
        {
            try
            {
                IList<XYZ> c = _pc?.UIView?.GetZoomCorners();
                if (_view != null && c != null && c.Count >= 2) s_zoom[_view.Id.Value] = (c[0], c[1]);
            }
            catch { }
        }

        private void Later(Action a)
        {
            try { _pc.Dispatcher.BeginInvoke(a, WT.DispatcherPriority.ApplicationIdle); } catch { }
        }

        // zoom to the crop of the view (not to everything: annotations far outside the crop made the section small and off to a side)
        public void ZoomFit()
        {
            try
            {
                UIView uv = _pc?.UIView;
                if (uv == null) return;
                if (_view != null && _view.CropBoxActive)
                {
                    BoundingBoxXYZ cb = _view.CropBox;
                    Transform t = cb.Transform;
                    XYZ a = t.OfPoint(new XYZ(cb.Min.X, cb.Min.Y, cb.Max.Z));
                    XYZ b = t.OfPoint(new XYZ(cb.Max.X, cb.Max.Y, cb.Max.Z));
                    uv.ZoomAndCenterRectangle(a, b);
                    return;
                }
                uv.ZoomToFit();
            }
            catch { try { _pc?.UIView?.ZoomToFit(); } catch { } }
        }

        // model point -> view coordinates (feet along the view right / up)
        private (double x, double y) V(XYZ p)
        {
            XYZ d = p - _view.Origin;
            return (d.DotProduct(_view.RightDirection), d.DotProduct(_view.UpDirection));
        }

        public bool Frame(out int left, out int top, out int right, out int bottom, out double x0, out double y0, out double x1, out double y1)
        {
            left = top = right = bottom = 0;
            x0 = y0 = x1 = y1 = 0;
            try
            {
                UIView uv = _pc?.UIView;
                if (uv == null || _view == null) return false;
                Rectangle r = uv.GetWindowRectangle();
                left = r.Left; top = r.Top; right = r.Right; bottom = r.Bottom;
                IList<XYZ> c = uv.GetZoomCorners();
                if (c == null || c.Count < 2) return false;
                var a = V(c[0]);
                var b = V(c[1]);
                x0 = Math.Min(a.x, b.x); x1 = Math.Max(a.x, b.x);
                y0 = Math.Min(a.y, b.y); y1 = Math.Max(a.y, b.y);
                return right - left > 2 && bottom - top > 2 && x1 - x0 > 1e-9 && y1 - y0 > 1e-9;
            }
            catch { return false; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _pc != null)
            {
                KeepZoom();
                try { Child = null; _pc.Dispose(); } catch { }
                _pc = null;
            }
            base.Dispose(disposing);
        }
    }
}
