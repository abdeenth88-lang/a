// AB Bridges - the bridge Excel while it is open in Excel.
// Writing the .xlsx on disk while Excel has it open does not show in Excel, and Excel writes its old copy back on the
// next save (the split rows "never moved"). So when the file is open, the sheet is read from / written into the open
// workbook through Excel itself, and saved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace ABBridgesRevit
{
    internal static class XlLive
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        // true when another program (Excel) holds the file
        public static bool IsOpen(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false;
            }
            catch (IOException) { return true; }
            catch { return false; }
        }

        // the open workbook (the one Excel shows), or null
        private static dynamic Book(string path)
        {
            try { return Marshal.BindToMoniker(Path.GetFullPath(path)); }
            catch { return null; }
        }

        private static dynamic Sheet(dynamic wb, string name)
        {
            try
            {
                int n = (int)wb.Worksheets.Count;
                for (int i = 1; i <= n; i++)
                {
                    dynamic ws = wb.Worksheets[i];
                    if (string.Equals((string)ws.Name, name, StringComparison.OrdinalIgnoreCase)) return ws;
                }
            }
            catch { }
            return null;
        }

        // the cells of a sheet as text (numbers in invariant culture), like BridgeExcel.ReadCells
        public static Dictionary<(int r, int c), string> Cells(string path, string sheet)
        {
            dynamic wb = Book(path);
            if (wb == null) return null;
            try
            {
                dynamic ws = Sheet(wb, sheet);
                if (ws == null) return null;
                dynamic used = ws.UsedRange;
                int r0 = (int)used.Row, c0 = (int)used.Column;
                object v = used.Value2;
                var res = new Dictionary<(int r, int c), string>();
                void Add(int r, int c, object o)
                {
                    if (o == null) return;
                    string t = o is double d ? d.ToString("R", CI) : Convert.ToString(o, CI);
                    if (!string.IsNullOrEmpty(t)) res[(r, c)] = t;
                }
                if (v is object[,] a)
                {
                    int lr = a.GetLowerBound(0), lc = a.GetLowerBound(1);
                    for (int i = lr; i <= a.GetUpperBound(0); i++)
                        for (int j = lc; j <= a.GetUpperBound(1); j++)
                            Add(r0 + i - lr, c0 + j - lc, a[i, j]);
                }
                else Add(r0, c0, v);
                return res;
            }
            catch { return null; }
            finally { try { Marshal.ReleaseComObject(wb); } catch { } }
        }

        // writes the sheet into the open workbook (made if missing) with the 4 tendon styles, then saves it
        // style: 1 title (navy, white bold), 2 header (amber, bold), 3 input (light orange), 4 label (grey)
        public static bool WriteSheet(string path, string sheet, List<(int r, int c, string v, bool num, int style)> cells, int cols)
        {
            dynamic wb = Book(path);
            if (wb == null) return false;
            try
            {
                dynamic ws = Sheet(wb, sheet);
                if (ws == null)
                {
                    dynamic last = wb.Worksheets[(int)wb.Worksheets.Count];
                    ws = wb.Worksheets.Add(Type.Missing, last);
                    ws.Name = sheet;
                }
                ws.Cells.Clear();
                int Rgb(int r, int g, int b) => r | (g << 8) | (b << 16);
                int[] fill = { 0, Rgb(31, 58, 95), Rgb(250, 199, 117), Rgb(250, 238, 218), Rgb(241, 239, 232) };
                foreach (var x in cells)
                {
                    dynamic cell = ws.Cells[x.r, x.c];
                    if (x.num && double.TryParse(x.v, NumberStyles.Float, CI, out double d)) cell.Value2 = d;
                    else if (!string.IsNullOrEmpty(x.v)) { cell.NumberFormat = "@"; cell.Value2 = x.v; }
                    int st = Math.Max(1, Math.Min(4, x.style));
                    cell.Interior.Color = fill[st];
                    cell.Font.Bold = st <= 2;
                    cell.Font.Color = st == 1 ? Rgb(255, 255, 255) : 0;
                    cell.HorizontalAlignment = -4108;
                    cell.VerticalAlignment = -4108;
                    dynamic b = cell.Borders;
                    b.LineStyle = 1;
                    b.Color = Rgb(158, 158, 158);
                }
                for (int c = 1; c < cols; c++) ws.Columns[c].ColumnWidth = c % 3 == 0 ? 3 : 22;
                wb.Save();
                return true;
            }
            catch { return false; }
            finally { try { Marshal.ReleaseComObject(wb); } catch { } }
        }
    }
}
