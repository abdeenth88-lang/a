// AB Bridges - choose one or more surfaces from the list of surfaces in the drawing.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace ABBridges
{
    internal sealed class SurfacePicker : Form
    {
        private readonly CheckedListBox _list = new CheckedListBox();
        private readonly List<ObjectId> _ids = new List<ObjectId>();

        public List<ObjectId> Selected { get; private set; } = new List<ObjectId>();

        public static List<ObjectId> Choose(IWin32Window owner, Database db, string title, string hint, IEnumerable<ObjectId> current)
        {
            using (var f = new SurfacePicker(db, title, hint, current))
            {
                if (f._ids.Count == 0)
                {
                    MessageBox.Show(owner, "There are no surfaces in this drawing.", "AB Bridges", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return null;
                }
                return f.ShowDialog(owner) == DialogResult.OK ? f.Selected : null;
            }
        }

        private SurfacePicker(Database db, string title, string hint, IEnumerable<ObjectId> current)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Segoe UI", 9f);
            ClientSize = new Size(380, 360);
            ShowInTaskbar = false;

            var lb = new Label { Text = hint, Location = new Point(10, 10), Size = new Size(360, 34) };
            _list.Location = new Point(10, 46);
            _list.Size = new Size(360, 266);
            _list.CheckOnClick = true;

            var cur = new HashSet<ObjectId>(current ?? Enumerable.Empty<ObjectId>());
            var items = new List<(ObjectId id, string name)>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                ObjectIdCollection all = CivilApplication.ActiveDocument.GetSurfaceIds();
                foreach (ObjectId id in all)
                {
                    if (!BridgeSettings.Valid(id)) continue;
                    if (tr.GetObject(id, OpenMode.ForRead) is CivSurface s) items.Add((id, s.Name));
                }
                tr.Commit();
            }
            foreach (var (id, name) in items.OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase))
            {
                _ids.Add(id);
                _list.Items.Add(name, cur.Contains(id));
            }

            var ok = new NiceButton { Look = Look.Primary, Text = "OK", Location = new Point(204, 322), Size = new Size(80, 27), DialogResult = DialogResult.OK };
            var cancel = new NiceButton { Text = "Cancel", Location = new Point(290, 322), Size = new Size(80, 27), DialogResult = DialogResult.Cancel };
            var none = new NiceButton { Look = Look.Danger, Text = "Clear all", Location = new Point(10, 322), Size = new Size(80, 27) };
            none.Click += (s, e) => { for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, false); };
            ok.Click += (s, e) =>
            {
                Selected = new List<ObjectId>();
                foreach (int i in _list.CheckedIndices) Selected.Add(_ids[i]);
            };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { lb, _list, ok, cancel, none });
        }
    }
}
