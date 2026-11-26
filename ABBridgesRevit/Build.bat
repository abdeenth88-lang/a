@echo off
rem AB Bridges for Revit - build and deploy (Revit must be closed). The result is written to build_log.txt
cd /d "C:\Users\asus\Desktop\claude\ABBridgesRevit"
dotnet build -c Release > build_log.txt 2>&1
echo EXIT CODE %ERRORLEVEL% >> build_log.txt
