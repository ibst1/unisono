# Builds Unisono as a single-file exe (framework-dependent: needs the .NET 8 Desktop Runtime, or a newer one;
# -SelfContained bundles the runtime instead, for computers without it) and copies it to
# %USERPROFILE%\Program\Unisono (a location unsigned exes may run from on
# locked-down machines) and to dist\ next to this script. When the environment variable UNISONO_COPY_TO names a folder
# (e.g. one a cloud drive shares with other computers), the finished files are copied there as well.
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
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

# Framework-dependent starts in a fraction of a second; a self-contained single file (~60-90 MB) takes seconds to
# unpack and scan on every start, which Explorer's "Open in..." and the command line feel.
$selfContainedArg = if ($SelfContained) { "true" } else { "false" }
New-Item -ItemType Directory -Force $dist, $target | Out-Null
# Unisono.exe is the tray app, UnisonoCli.exe the command line; both share Unisono.Core.
foreach ($app in @(@{ Proj = "src\Unisono\Unisono.csproj"; Exe = "Unisono" }, @{ Proj = "src\Unisono.Cli\Unisono.Cli.csproj"; Exe = "UnisonoCli" })) {
    # A running exe is locked, but may still be RENAMED: move it aside so publish can write the new one.
    $exe = Join-Path $target "$($app.Exe).exe"
    # Copies parked by earlier builds: gone once nothing runs from them any more.
    Get-ChildItem $target -Filter "$($app.Exe).prev*.exe" -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Delete() } catch { } }
    if (Test-Path $exe) {
        $prev = Join-Path $target "$($app.Exe).prev.exe"
        Remove-Item $prev -Force -ErrorAction SilentlyContinue
        # An older instance may still run from .prev.exe (locked): park this one under a unique name instead.
        if (Test-Path $prev) { $prev = Join-Path $target ("{0}.prev-{1:yyyyMMddHHmmss}.exe" -f $app.Exe, (Get-Date)) }
        try { Move-Item $exe $prev -Force } catch { Write-Host "note: could not move the old $($app.Exe).exe aside ($_)" }
    }
    & $dotnet publish (Join-Path $root $app.Proj) -c $Configuration -r $Runtime --self-contained $selfContainedArg `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=$selfContainedArg -p:Version=$version -p:InformationalVersion=$info -p:BuildTime="$buildTime" -o $target
    if ($LASTEXITCODE -ne 0) { throw "publish of $($app.Exe) failed" }
    Copy-Item $exe (Join-Path $dist "$($app.Exe).exe") -Force
    # WebView2Loader.dll (native) is not bundled into a single-file exe; it has to sit next to Unisono.exe.
    if ($app.Exe -eq "Unisono") { Copy-Item (Join-Path $target "WebView2Loader.dll") $dist -Force }
}
Write-Host "Published Unisono.exe and UnisonoCli.exe to $target and $dist"
if ($env:UNISONO_COPY_TO) {
    New-Item -ItemType Directory -Force $env:UNISONO_COPY_TO | Out-Null
    foreach ($f in "Unisono.exe", "UnisonoCli.exe", "WebView2Loader.dll") {
        # A computer may run Unisono from there: a locked file is left for the next build rather than failing this one.
        try { Copy-Item (Join-Path $dist $f) $env:UNISONO_COPY_TO -Force -ErrorAction Stop }
        catch { Write-Host "note: could not copy $f to $($env:UNISONO_COPY_TO) ($_)" }
    }
    Write-Host "Copied them to $($env:UNISONO_COPY_TO) as well"
}