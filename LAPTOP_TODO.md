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
