# AB Bridges (Civil 3D 2025)

Ribbon tab **AB Bridges**
- Bridge Data -> **Extract Data** (command `ABBRIDGES`, old `BRIDGEPTS` still works)
- Drafting Tools -> **PL. Start** (command `PLSTART`)

Build (close Civil 3D first), in PowerShell inside this folder:

    dotnet build -c Release

The build installs to `%APPDATA%\Autodesk\ApplicationPlugins\ABBridges.bundle`
and removes the old `BridgePoints.bundle`.
