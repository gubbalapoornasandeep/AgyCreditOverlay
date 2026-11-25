# Builds AgyCreditOverlay.exe and agy-statusline.exe with the C# compiler built into Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "C# compiler not found (.NET Framework 4.x required)." }

$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

# Stop a running overlay so the exe can be overwritten
Get-Process AgyCreditOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

& $csc /nologo /optimize+ /target:exe /platform:anycpu `
    /reference:System.Web.Extensions.dll `
    /out:"$bin\agy-statusline.exe" "$root\src\StatusLine.cs"
if ($LASTEXITCODE -ne 0) { throw "agy-statusline build failed" }

& $csc /nologo /optimize+ /target:winexe /platform:anycpu `
    /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /out:"$bin\AgyCreditOverlay.exe" "$root\src\Overlay.cs"
if ($LASTEXITCODE -ne 0) { throw "AgyCreditOverlay build failed" }

Write-Host "Built:" -ForegroundColor Green
Get-ChildItem $bin *.exe | ForEach-Object { "  " + $_.FullName }
