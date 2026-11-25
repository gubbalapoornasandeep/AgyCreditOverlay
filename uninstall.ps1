# Removes the Antigravity Credit Overlay hooks (does not delete this folder).
$ErrorActionPreference = 'Stop'
Get-Process AgyCreditOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AgyCreditOverlay' -ErrorAction SilentlyContinue

$cfgPath = Join-Path $HOME '.gemini\antigravity-cli\settings.json'
if (Test-Path $cfgPath) {
    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    if ($cfg.PSObject.Properties['statusLine'] -and "$($cfg.statusLine.command)" -like '*agy-statusline.exe*') {
        Copy-Item $cfgPath "$cfgPath.bak" -Force
        $cfg.PSObject.Properties.Remove('statusLine')
        [IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding $false))
        Write-Host "Removed statusLine hook from $cfgPath"
    }
}
Remove-Item (Join-Path $env:LOCALAPPDATA 'AgyCreditOverlay') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Uninstalled." -ForegroundColor Green
