@echo off
rem AB Bridges Setup - copies the latest built add-ins into payload, builds one setup exe, puts it in claude\Installer
cd /d "C:\Users\asus\Desktop\claude\ABBridgesSetup"
copy /y "..\ABBridges\PackageContents.xml" "payload\Civil3D\" > build_log.txt 2>&1
copy /y "..\ABBridges\bin\Release\net8.0-windows\ABBridges.dll" "payload\Civil3D\Contents\" >> build_log.txt 2>&1
copy /y "..\ABBridges\bin\Release\net8.0-windows\ABBridges.deps.json" "payload\Civil3D\Contents\" >> build_log.txt 2>&1
copy /y "..\ABBridgesRevit\ABBridges.addin" "payload\Revit\" >> build_log.txt 2>&1
copy /y "..\ABBridgesRevit\bin\Release\net8.0-windows\ABBridgesRevit.dll" "payload\Revit\ABBridges\" >> build_log.txt 2>&1
copy /y "..\ABBridgesRevit\bin\Release\net8.0-windows\ABBridgesRevit.deps.json" "payload\Revit\ABBridges\" >> build_log.txt 2>&1
rem Files page: logo, Revit families, Excel template(s) (put any *.xlsx to ship in ABBridgesSetup\Excel)
if not exist "payload\Files\Families" mkdir "payload\Files\Families"
if not exist "payload\Revit\ABBridges\Families" mkdir "payload\Revit\ABBridges\Families"
copy /y "AB_logo_256.png" "payload\Files\" >> build_log.txt 2>&1
copy /y "..\ABBridgesRevit\Families\*.*" "payload\Files\Families\" >> build_log.txt 2>&1
copy /y "..\ABBridgesRevit\Families\*.rfa" "payload\Revit\ABBridges\Families\" >> build_log.txt 2>&1
if exist "Excel\*.xlsx" copy /y "Excel\*.xlsx" "payload\Files\" >> build_log.txt 2>&1
dotnet publish -c Release -o publish >> build_log.txt 2>&1
if not exist "..\Installer" mkdir "..\Installer"
copy /y "publish\AB Bridges Setup - 2025.exe" "..\Installer\" >> build_log.txt 2>&1
echo EXIT CODE %ERRORLEVEL% >> build_log.txt
