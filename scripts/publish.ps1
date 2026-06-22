param(
    [string]$Runtime = "",
    [string]$Configuration = "Release",
    [string]$OutputDir = "..\\out\\publish",
    [bool]$SelfContained = $false,
    [bool]$NoRestore = $true
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "..\\src\\PerformanceTracker.Desktop\\PerformanceTracker.Desktop.csproj"
$resolvedProjectPath = (Resolve-Path $projectPath).Path
$resolvedOutputDir = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    [System.IO.Path]::GetFullPath($OutputDir)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDir))
}

Write-Host "Publishing Performance Tracker to $resolvedOutputDir"

$publishArgs = @(
    "publish"
    $resolvedProjectPath
    "-c"
    $Configuration
    "-o"
    $resolvedOutputDir
)

if ($NoRestore) {
    $publishArgs += "--no-restore"
}

if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
    $publishArgs += @(
        "-r"
        $Runtime
        "--self-contained"
        $SelfContained.ToString().ToLowerInvariant()
        "/p:PublishSingleFile=true"
        "/p:IncludeNativeLibrariesForSelfExtract=true"
    )
}

dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Published executable:"
Write-Host "  $(Join-Path $resolvedOutputDir 'PerformanceTracker.Desktop.exe')"
