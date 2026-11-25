# On the laptop (Nov 25)

1. Copy `ABBridges\*` -> `C:\Users\asus\Desktop\claude\ABBridges\`, `ABBridgesRevit\*` -> `...\ABBridgesRevit\`.
2. Copy `Logo\AB_logo_256.png` and `Logo\AB.ico` into both projects; in both .csproj add:
   `<EmbeddedResource Include="AB_logo_256.png" />` and `<EmbeddedResource Include="AB.ico" />`, and `<ApplicationIcon>`-free (add-in DLLs).
3. Civil ribbon (file not uploaded yet): main button image -> AB_logo_256.png (32/16).
4. Build Civil 3D 2025 + Revit 2025 (.NET 8), fix compile errors, test, screenshots.
5. Installer: logo 4; Welcome page "Created by Abdein Badawi / Abdeenth88@gmail.com";
   pages Welcome -> Install (files + Excel) -> Install in Civil 3D -> Next -> Install in Revit -> Finish.
   Rebuild `Installer\AB Bridges Setup - 2025.exe`.

Done here (not compiled): Brand header + window icon (Civil BridgeForm, Revit Substructure), Revit "About" panel,
SubstructureForm: fixed `else` without `if` in Shown, Relayout/LayoutParams now offset by header + long view.

## Added later (Nov 25)
- UI 6D: Civil pages are chips inside the navy header (`NavBar` in UiKit.cs now draws logo + chips + "Created by").
  Revit: axis tabs drawn as chips (`UiTheme.StyleTabs`), page name as a white chip in `BrandHeader`.
- Excel look "3 + 2 (A)": strong group titles (navy / green / orange) + light header row, black lines, type colour bar
  (ThickLeft + BorderColor), light tints on Left/Right edge columns, axis rows grey + bold, CJ orange. Info sheet is last.
- Logo in the Excel: `XlsxLogo.Stamp` adds the picture to the title row after `XlsxBook.Write`.
- Check on the laptop: XStyle has `BorderColor`, `Indent`, `Size`; how `ThickLeft` uses `BorderColor`; Revit `XlsxPatch`
  keeps the drawings when it rewrites Parameters.
