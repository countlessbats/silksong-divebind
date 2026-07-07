<#
  DiveBind installer for Hollow Knight: Silksong.

  Finds your Silksong install (Steam auto-detect), or asks you for the folder if it can't, then copies
  DiveBind.dll into BepInEx\plugins. Works no matter where you run it from.

  Type the path when prompted WITHOUT quotes if you like — spaces and parentheses such as
  "C:\Program Files (x86)\..." are handled. (You can paste a quoted path too; quotes are stripped.)

  -GamePath is optional and mainly used internally when the script relaunches elevated; if you pass it by
  hand, DO quote it, e.g.  install.ps1 -GamePath "C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight Silksong"
#>
[CmdletBinding()]
param([string]$GamePath)

$ErrorActionPreference = 'Stop'
$ExeName  = 'Hollow Knight Silksong.exe'
$DataName = 'Hollow Knight Silksong_Data'
$SubPath  = 'steamapps\common\Hollow Knight Silksong'

function Write-Head($t) { Write-Host ''; Write-Host "== $t ==" -ForegroundColor Cyan }
function Test-GameDir([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    try { $dir = [System.IO.Path]::GetFullPath($dir) } catch { return $false }
    return (Test-Path (Join-Path $dir $ExeName)) -or (Test-Path (Join-Path $dir $DataName))
}

# Normalise whatever the user typed/pasted: trim spaces, strip a single layer of surrounding quotes, and if
# they handed us the .exe rather than the folder, use its directory. No quoting required from the user.
function Resolve-Input([string]$raw) {
    if ($null -eq $raw) { return $null }
    $s = $raw.Trim()
    if ($s.Length -ge 2 -and (($s[0] -eq '"' -and $s[-1] -eq '"') -or ($s[0] -eq "'" -and $s[-1] -eq "'"))) {
        $s = $s.Substring(1, $s.Length - 2)
    }
    $s = $s.Trim()
    if ($s -match '\.exe$' -and (Test-Path $s)) { $s = Split-Path $s -Parent }
    return $s
}

# --- Auto-detect candidates, in priority order ---
function Get-Candidates {
    $c = New-Object System.Collections.Generic.List[string]

    # 1. Are we already inside (or beside) a game install? Walk up from the script location.
    try {
        $d = $PSScriptRoot
        for ($i = 0; $i -lt 6 -and $d; $i++) {
            if (Test-GameDir $d) { $c.Add($d) }
            $parent = Split-Path $d -Parent
            if ($parent -eq $d) { break }
            $d = $parent
        }
    } catch {}

    # 2. Steam libraries (registry -> libraryfolders.vdf -> each library's common\...).
    try {
        $steam = $null
        foreach ($k in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')) {
            if (Test-Path $k) {
                $p = (Get-ItemProperty $k -ErrorAction SilentlyContinue).SteamPath
                if (-not $p) { $p = (Get-ItemProperty $k -ErrorAction SilentlyContinue).InstallPath }
                if ($p) { $steam = $p -replace '/', '\'; break }
            }
        }
        $libFiles = @()
        if ($steam) { $libFiles += (Join-Path $steam 'steamapps\libraryfolders.vdf'), (Join-Path $steam 'config\libraryfolders.vdf') }
        foreach ($vdf in $libFiles) {
            if (Test-Path $vdf) {
                foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s*"([^"]+)"')) {
                    $lib = $m.Groups[1].Value -replace '\\\\', '\'
                    $c.Add((Join-Path $lib $SubPath))
                }
            }
        }
        if ($steam) { $c.Add((Join-Path $steam $SubPath)) }
    } catch {}

    # 3. Plain fallbacks across common drives.
    foreach ($drv in 'C', 'D', 'E', 'F') {
        $c.Add("${drv}:\Program Files (x86)\Steam\$SubPath")
        $c.Add("${drv}:\SteamLibrary\$SubPath")
        $c.Add("${drv}:\Games\Steam\$SubPath")
    }
    return $c
}

Write-Head 'DiveBind installer'

# Locate our own DiveBind.dll (beside the script, under BepInEx\plugins, or anywhere below the script).
$dll = $null
foreach ($p in @((Join-Path $PSScriptRoot 'DiveBind.dll'), (Join-Path $PSScriptRoot 'BepInEx\plugins\DiveBind.dll'))) {
    if (Test-Path $p) { $dll = $p; break }
}
if (-not $dll) { $dll = (Get-ChildItem -Path $PSScriptRoot -Filter 'DiveBind.dll' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
if (-not $dll) {
    Write-Host "Could not find DiveBind.dll next to this installer. Keep install.ps1 together with the mod files." -ForegroundColor Red
    Read-Host 'Press Enter to exit'; exit 1
}
Write-Host "Mod file: $dll"

# Resolve the game folder: explicit -GamePath, then auto-detect, then ask.
$game = $null
if ($GamePath) { $gp = Resolve-Input $GamePath; if (Test-GameDir $gp) { $game = [System.IO.Path]::GetFullPath($gp) } }

if (-not $game) {
    foreach ($cand in Get-Candidates) {
        if (Test-GameDir $cand) { $game = [System.IO.Path]::GetFullPath($cand); Write-Host "Found Silksong: $game" -ForegroundColor Green; break }
    }
}

if (-not $game) {
    Write-Host "Couldn't auto-detect your Silksong folder." -ForegroundColor Yellow
    Write-Host "Paste or type the full path to it (no quotes needed; spaces and (parentheses) are fine)."
    while (-not $game) {
        $inp = Read-Host "Silksong folder (or 'q' to quit)"
        if ($inp -eq 'q') { exit 1 }
        $r = Resolve-Input $inp
        if (Test-GameDir $r) { $game = [System.IO.Path]::GetFullPath($r) }
        else { Write-Host "That folder doesn't contain '$ExeName' or '$DataName'. Try again." -ForegroundColor Yellow }
    }
}

$plugins    = Join-Path $game 'BepInEx\plugins'
$bundleCore = Join-Path $PSScriptRoot 'BepInEx\core'
$installedBep = $false

# All writes go in one try so a single elevation (if needed under Program Files) covers everything.
try {
    # Install the bundled BepInEx only if the game doesn't already have it (never clobber an existing one).
    if (-not (Test-Path (Join-Path $game 'BepInEx\core\BepInEx.dll'))) {
        if (Test-Path $bundleCore) {
            Write-Head 'BepInEx not found — installing the bundled copy'
            foreach ($f in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt') {
                $src = Join-Path $PSScriptRoot $f
                if (Test-Path $src) { Copy-Item $src (Join-Path $game $f) -Force }
            }
            New-Item -ItemType Directory -Force -Path (Join-Path $game 'BepInEx\core') | Out-Null
            Copy-Item (Join-Path $bundleCore '*') (Join-Path $game 'BepInEx\core') -Recurse -Force
            $installedBep = $true
        } else {
            Write-Host "NOTE: BepInEx isn't installed and no bundled copy is beside this installer." -ForegroundColor Yellow
            Write-Host "DiveBind won't load until BepInEx is installed. (The release zip bundles it.)"
        }
    } else {
        Write-Host "BepInEx already installed — leaving it untouched." -ForegroundColor DarkGray
    }

    New-Item -ItemType Directory -Force -Path $plugins | Out-Null
    Copy-Item $dll (Join-Path $plugins 'DiveBind.dll') -Force
} catch [System.UnauthorizedAccessException] {
    Write-Host "Write access denied (the game is in a protected folder). Relaunching as administrator..." -ForegroundColor Yellow
    $line = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -GamePath "{1}"' -f $PSCommandPath, $game
    try { Start-Process powershell.exe -Verb RunAs -ArgumentList $line; exit 0 }
    catch { Write-Host "Elevation cancelled. Re-run this installer as administrator, or copy DiveBind.dll into:`n  $plugins" -ForegroundColor Red; Read-Host 'Press Enter to exit'; exit 1 }
}

$dest = Join-Path $plugins 'DiveBind.dll'
if (Test-Path $dest) {
    Write-Host ''
    Write-Host ("Installed " + $(if ($installedBep) { 'BepInEx + DiveBind' } else { 'DiveBind' }) + " ->") -ForegroundColor Green
    Write-Host "  $dest"
    Write-Host "Launch the game and press F4 to configure. Default: R2, airborne only."
} else {
    Write-Host "Copy did not complete. Please copy DiveBind.dll into $plugins manually." -ForegroundColor Red
}
Read-Host 'Press Enter to exit'
