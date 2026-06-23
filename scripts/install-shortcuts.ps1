param(
    [string]$PublishDir = "..\\out\\publish",
    [string]$BuildDir = "..\\src\\PerformanceTracker.Desktop\\bin\\Release\\net10.0-windows10.0.19041.0",
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

$resolvedPublishDir = if ([System.IO.Path]::IsPathRooted($PublishDir)) {
    [System.IO.Path]::GetFullPath($PublishDir)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $PublishDir))
}

$resolvedBuildDir = if ([System.IO.Path]::IsPathRooted($BuildDir)) {
    [System.IO.Path]::GetFullPath($BuildDir)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $BuildDir))
}

$publishedExePath = Join-Path $resolvedPublishDir "PerformanceTracker.Desktop.exe"
$builtExePath = Join-Path $resolvedBuildDir "PerformanceTracker.Desktop.exe"

if (Test-Path $publishedExePath) {
    $exePath = $publishedExePath
    $workingDirectory = $resolvedPublishDir
}
elseif (Test-Path $builtExePath) {
    $exePath = $builtExePath
    $workingDirectory = $resolvedBuildDir
}
else {
    throw "No executable found. Build the app with 'dotnet build performance-tracker.sln -c Release' or run scripts\\publish.ps1 first."
}

if (-not $Startup -and -not $Desktop) {
    throw "Choose at least one target: -Startup and/or -Desktop."
}

$shell = New-Object -ComObject WScript.Shell

function New-PerformanceTrackerShortcut {
    param(
        [string]$ShortcutPath,
        [string]$Arguments
    )

    $outputIconPath = Join-Path $workingDirectory "performance-tracker.ico"
    $sourceIconPath = Join-Path $PSScriptRoot "..\\src\\PerformanceTracker.Desktop\\Assets\\performance-tracker.ico"
    $resolvedSourceIconPath = [System.IO.Path]::GetFullPath($sourceIconPath)
    $iconLocation =
        if (Test-Path $outputIconPath) { $outputIconPath }
        elseif (Test-Path $resolvedSourceIconPath) { $resolvedSourceIconPath }
        else { "$exePath,0" }

    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $workingDirectory
    $shortcut.IconLocation = $iconLocation
    $shortcut.Arguments = $Arguments
    $shortcut.Save()
}

if ($Startup) {
    $startupShortcut = Join-Path $env:APPDATA "Microsoft\\Windows\\Start Menu\\Programs\\Startup\\Performance Tracker.lnk"
    New-PerformanceTrackerShortcut -ShortcutPath $startupShortcut -Arguments "--minimized"
    Write-Host "Created startup shortcut: $startupShortcut"
}

if ($Desktop) {
    $desktopShortcut = Join-Path (Resolve-DesktopDirectory) "Performance Tracker.lnk"
    New-PerformanceTrackerShortcut -ShortcutPath $desktopShortcut -Arguments ""
    Write-Host "Created desktop shortcut: $desktopShortcut"
}
