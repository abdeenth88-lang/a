@echo off
rem AB Bridges for Civil 3D - build and deploy (Civil 3D must be closed). The result is written to build_log.txt
cd /d "C:\Users\asus\Desktop\claude\ABBridges"
dotnet build -c Release > build_log.txt 2>&1
echo EXIT CODE %ERRORLEVEL% >> build_log.txt
