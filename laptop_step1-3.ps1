# AB Bridges - laptop steps 1-3 + build (2025 only)
# Run in PowerShell:  powershell -ExecutionPolicy Bypass -File laptop_step1-3.ps1
$ErrorActionPreference = 'Stop'
$root   = 'C:\Users\asus\Desktop\claude'
$tmp    = Join-Path $env:TEMP 'ab_hai'
$stamp  = Get-Date -Format 'yyyyMMdd_HHmm'
$backup = Join-Path $root "_backup_$stamp"

# 1. Download the branch
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item $tmp -ItemType Directory | Out-Null
$zip = Join-Path $tmp 'b.zip'
Invoke-WebRequest 'https://github.com/abdeenth88-lang/a/archive/refs/heads/claude/hai-skssuu.zip' -OutFile $zip
Expand-Archive $zip $tmp
$src = Get-ChildItem $tmp -Directory | Select-Object -First 1

# 2. Backup, then copy (overwrite)
foreach ($p in 'ABBridges','ABBridgesRevit') {
    Copy-Item (Join-Path $root $p) (Join-Path $backup $p) -Recurse
    Copy-Item (Join-Path $src.FullName "$p\*") (Join-Path $root $p) -Recurse -Force
}
Write-Host "Backup: $backup"

# 3. Logo into both projects + EmbeddedResource in .csproj
foreach ($p in 'ABBridges','ABBridgesRevit') {
    $dir = Join-Path $root $p
    Copy-Item (Join-Path $src.FullName 'Logo\AB_logo_256.png') $dir -Force
    Copy-Item (Join-Path $src.FullName 'Logo\AB.ico') $dir -Force
    $csproj = Get-ChildItem $dir -Filter *.csproj | Where-Object { $_.Name -notmatch '2024' } | Select-Object -First 1
    $x = Get-Content $csproj.FullName -Raw
    if ($x -notmatch 'AB_logo_256\.png') {
        $add = "  <ItemGroup>`r`n    <EmbeddedResource Include=`"AB_logo_256.png`" />`r`n    <EmbeddedResource Include=`"AB.ico`" />`r`n  </ItemGroup>`r`n</Project>"
        $x = $x -replace '</Project>\s*$', $add
        Set-Content $csproj.FullName $x -Encoding UTF8
    }
    # Build
    Write-Host "`n=== Build $($csproj.Name) ==="
    dotnet build $csproj.FullName -c Release 2>&1 | Tee-Object (Join-Path $root "build_$p.log") | Select-String 'error|Build succeeded'
}
Write-Host "`nDone. Send me build_ABBridges.log and build_ABBridgesRevit.log from $root"
