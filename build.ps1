# Builds Unisono as a self-contained single-file exe and copies it to
# %USERPROFILE%\Program\Unisono (a location unsigned exes may run from on
# locked-down machines) and to dist\ next to this script.
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist"
$target = Join-Path $env:USERPROFILE "Program\Unisono"

# Prefer a user-local SDK if the machine-wide dotnet has none.
$localDotnet = Join-Path $env:LOCALAPPDATA "dotnet\dotnet.exe"
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { "dotnet" }

# Every build gets its own version: 0.2.<commit count>, plus the short hash (and "dirty" when the
# tree has uncommitted changes) and the build time.
$major = "0.2"
Push-Location $root
# git writes "fatal: ..." to stderr in a fresh repo, which $ErrorActionPreference = Stop would turn into an abort.
$ErrorActionPreference = "Continue"
try {
    $count = (& git rev-list --count HEAD 2>$null); if (-not $count) { $count = "0" }
    $hash = (& git rev-parse --short HEAD 2>$null); if (-not $hash) { $hash = "nogit" }
    $dirty = if (& git status --porcelain 2>$null) { ".dirty" } else { "" }
} finally { Pop-Location; $ErrorActionPreference = "Stop" }
$version = "$major.$count"
$info = "$version+$hash$dirty"
$buildTime = Get-Date -Format "yyyy-MM-dd HH:mm"
Write-Host "Building Unisono $info ($buildTime)"

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }
New-Item -ItemType Directory -Force $dist, $target | Out-Null
# Unisono.exe is the tray app, UnisonoCli.exe the command line; both share Unisono.Core.
foreach ($app in @(@{ Proj = "src\Unisono\Unisono.csproj"; Exe = "Unisono" }, @{ Proj = "src\Unisono.Cli\Unisono.Cli.csproj"; Exe = "UnisonoCli" })) {
    # A running exe is locked, but may still be RENAMED: move it aside so publish can write the new one.
    $exe = Join-Path $target "$($app.Exe).exe"
    if (Test-Path $exe) {
        $prev = Join-Path $target "$($app.Exe).prev.exe"
        Remove-Item $prev -Force -ErrorAction SilentlyContinue
        try { Move-Item $exe $prev -Force } catch { Write-Host "note: could not move the old $($app.Exe).exe aside ($_)" }
    }
    & $dotnet publish (Join-Path $root $app.Proj) -c $Configuration -r $Runtime --self-contained $selfContained `
        -p:PublishSingleFile=true -p:Version=$version -p:InformationalVersion=$info -p:BuildTime="$buildTime" -o $target
    if ($LASTEXITCODE -ne 0) { throw "publish of $($app.Exe) failed" }
    Copy-Item $exe (Join-Path $dist "$($app.Exe).exe") -Force
}
Write-Host "Published Unisono.exe and UnisonoCli.exe to $target and $dist"