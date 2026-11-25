# Installs the Antigravity Credit Overlay:
#  1. builds the two exes
#  2. hooks agy-statusline.exe into the Antigravity CLI statusLine setting (keeps the built-in status line)
#  3. optionally registers the overlay to start with Windows
#  4. launches the overlay
param([switch]$NoStartup)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $root 'build.ps1')

$statusExe  = Join-Path $root 'bin\agy-statusline.exe'
$overlayExe = Join-Path $root 'bin\AgyCreditOverlay.exe'

# ---- Antigravity CLI settings.json -----------------------------------------
$cfgDir  = Join-Path $HOME '.gemini\antigravity-cli'
$cfgPath = Join-Path $cfgDir 'settings.json'
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null

$cfg = New-Object PSObject
if (Test-Path $cfgPath) {
    $text = Get-Content $cfgPath -Raw
    if ($text -and $text.Trim()) {
        Copy-Item $cfgPath "$cfgPath.bak" -Force
        $cfg = $text | ConvertFrom-Json
    }
}

$command = if ($statusExe -match '\s') { '"' + $statusExe + '"' } else { $statusExe }
$existing = $cfg.PSObject.Properties['statusLine']
if ($existing -and $existing.Value.command -and $existing.Value.command -ne $command) {
    Write-Warning "Replacing your existing statusLine command: $($existing.Value.command) (backup: settings.json.bak)"
}
$statusLine = [ordered]@{ type = 'command'; command = $command; stack_with_default = $true; enabled = $true }
if ($existing) { $cfg.statusLine = $statusLine } else { $cfg | Add-Member -NotePropertyName statusLine -NotePropertyValue $statusLine }

$json = $cfg | ConvertTo-Json -Depth 32
[IO.File]::WriteAllText($cfgPath, $json, (New-Object Text.UTF8Encoding $false))
Write-Host "Configured Antigravity CLI status line -> $cfgPath" -ForegroundColor Green

# ---- start with Windows -----------------------------------------------------
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (-not $NoStartup) {
    Set-ItemProperty -Path $runKey -Name 'AgyCreditOverlay' -Value ('"' + $overlayExe + '"')
    Write-Host "Overlay will start automatically with Windows (toggle from its right-click menu)." -ForegroundColor Green
}

# ---- launch -----------------------------------------------------------------
Start-Process $overlayExe
Write-Host "Overlay started. Look for it at the top-right of your screen / the 'C' icon in the system tray." -ForegroundColor Cyan
if (-not (Get-Command agy -ErrorAction SilentlyContinue)) {
    Write-Warning "The Antigravity CLI (agy) was not found on PATH. Install it from https://antigravity.google/docs/cli and run 'agy' once to feed live credit data."
}
