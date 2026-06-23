param(
    [switch]$Startup,
    [switch]$Desktop
)

$ErrorActionPreference = "Stop"

function Resolve-DesktopDirectory {
    $desktopPath = [Environment]::GetFolderPath("Desktop")
    if (-not [string]::IsNullOrWhiteSpace($desktopPath) -and (Test-Path $desktopPath)) {
        return $desktopPath
    }

    $fallbacks = @(
        (Join-Path $env:USERPROFILE "OneDrive\\Desktop"),
        (Join-Path $env:USERPROFILE "Desktop")
    )

    foreach ($fallbackPath in $fallbacks) {
        if (-not [string]::IsNullOrWhiteSpace($fallbackPath) -and (Test-Path $fallbackPath)) {
            return $fallbackPath
        }
    }

    throw "Unable to resolve the Desktop folder path."
}

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
    $desktopShortcut = Join-Path (Resolve-DesktopDirectory) "Performance Tracker.lnk"
    if (Test-Path $desktopShortcut) {
        Remove-Item -LiteralPath $desktopShortcut -Force
        Write-Host "Removed desktop shortcut: $desktopShortcut"
    }
}
