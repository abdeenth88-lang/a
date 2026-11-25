# AB Bridges - handoff to Claude Code (Nov 25, 2026)

Reply to the user in Egyptian Arabic, keep it short. UI text in the add-ins stays English.
Rule: show pictures/mockups first and wait for "نفذ" before coding new features.
Work on the 2025 versions only (2024 copies are paused).

## 1. Copy these files (overwrite)
- `ABBridges\*`      -> `C:\Users\asus\Desktop\claude\ABBridges\`       (Civil 3D 2025, .NET 8)
- `ABBridgesRevit\*` -> `C:\Users\asus\Desktop\claude\ABBridgesRevit\`  (Revit 2025, .NET 8)

## 2. Build both, fix compile errors, test, then send screenshots
None of this code has been compiled yet.

## What changed
### Civil (ABBridges)
- New UI: sizable window, numbered nav bar (Setup / Construction Joint / Internal Diaphragm / Roads Below / Sections), `UiKit.cs` (Theme, NavBar, PlanView).
- Internal diaphragms per span (count 2, gap 0.30, equal between axis CL, station on alignment) -> `Settings.cs`, `Stations.cs` (SectionKind.InternalDiaphragm).
- Near stations editable (NearDist 2.70; 5.40).
- `XlsxKeep.cs` + `Report.cs`: Generate Excel keeps the sheets "Tendon Profile" and "To Revit" (makes an empty "To Revit" if missing).

### Revit (ABBridgesRevit)
- Substructure: `LongView.cs` longitudinal view (bubbles, span dims, cut, FGL sand hatch, FRL roads, MIN VERTICAL CLEARANCE), auto-apply, "Show in Revit" and return, section dims, pier to pile-cap top.
- `XlsxRead.cs`: roads (Station / Road Z), StationRow.Sta, ReadCells.
- `Tendon.cs` + panel "Tendon Profile" (Create / Update Tendon):
  - Excel sheet "Tendon Profile" (vertical): B2 = number of paths; per path a block X | Y-Tendon (m) from Anchor1 to Anchor2, "Split at (X) - stages" (e.g. `0.85L2; 0.85L3`, stages = splits + 1), then data rows (PJ, tendons/web, strands/tendon, strand area, webs, jacking end, web offsets, box depth).
  - A split with no X row -> the row is inserted automatically and the user types its Y. Y at a split is never interpolated: empty -> stop with a message.
  - 3D swept tendon families per web offset; drafting views 1:150, vertical x3.
  - Sheets like the Parsons PDF (R1013 A-3, page 8): one sheet per path x stage (`AB-TP-ss-p`); a long stage is cut into parts (max ~90 m at 1:150) stacked, with "FOR CONTINUATION, SEE BELOW / ABOVE"; title + 1:150 under each view; data table and NOTES drawn on the sheet at the left; title block at the right. Update keeps sheets.
- `ExcelImport.cs` + panel "Excel" (Import Excel / Update Excel):
  - Import: pick the file, Excel opens (sheet "To Revit"), user selects tables (Ctrl for several), Done -> each area becomes a black-and-white table (lines + text, merges, bold, widths/heights, no colors) in its own drafting view 1:1; names, target sheet and text size in a dialog. Stored in AbStore: `XLS|T|<viewId>` = file|sheet|range|textMm.
  - Update: reopens each file (hidden Excel, read-only) and redraws in the same view.
  - Uses late-bound COM (`dynamic`); Excel must be installed. Icons reuse icon_RF / icon_PD.

## Later (not done)
- Substructure lower layout: one section per axis with family pickers under each.
- Optional 2D tendon / table families.
- Resume 2024 copies; rebuild installers (`Installer\AB Bridges Setup - 2025.exe`) and upload to Drive folder 1aRllEhazjhKOQTSfuAmHSV6AiOSkNGcT.
