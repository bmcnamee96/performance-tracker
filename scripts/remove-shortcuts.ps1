param(
    [switch]$Startup,
    [switch]$Desktop
)

$ErrorActionPreference = "Stop"

if (-not $Startup -and -not $Desktop) {
    throw "Choose at least one target to remove: -Startup and/or -Desktop."
}

if ($Startup) {
    $startupShortcut = Join-Path $env:APPDATA "Microsoft\\Windows\\Start Menu\\Programs\\Startup\\Performance Tracker.lnk"
    if (Test-Path $startupShortcut) {
        Remove-Item -LiteralPath $startupShortcut -Force
        Write-Host "Removed startup shortcut: $startupShortcut"
    }
}

if ($Desktop) {
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "Performance Tracker.lnk"
    if (Test-Path $desktopShortcut) {
        Remove-Item -LiteralPath $desktopShortcut -Force
        Write-Host "Removed desktop shortcut: $desktopShortcut"
    }
}

