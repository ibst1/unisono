# Builds Unisono as a single-file exe (framework-dependent: needs the .NET 8 Desktop Runtime, or a newer one;
# -SelfContained bundles the runtime instead, for computers without it) and copies it to
# %USERPROFILE%\Program\Unisono (a location unsigned exes may run from on
# locked-down machines) and to dist\ next to this script. The finished files are also copied to the folder a running
# Unisono was started from, to this folder when an Unisono.exe sits here, and to the folder the environment variable
# UNISONO_COPY_TO names (e.g. one a cloud drive shares with other computers): a running Unisono notices the new exe and
# restarts into it by itself.
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    # Also publish this build as a GitHub release (needs a clean, pushed tree and the gh CLI signed in).
    [switch]$Release
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist"
$target = Join-Path $env:USERPROFILE "Program\Unisono"

# Prefer a user-local SDK if the machine-wide dotnet has none.
$localDotnet = Join-Path $env:LOCALAPPDATA "dotnet\dotnet.exe"
$homeDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
$dotnet = if (Test-Path $localDotnet) { $localDotnet } elseif (Test-Path $homeDotnet) { $homeDotnet } else { "dotnet" }

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
# Where else Unisono runs or is installed from: a build that never reaches the running copy is never started.
$installs = @()
if ($env:UNISONO_COPY_TO) { New-Item -ItemType Directory -Force $env:UNISONO_COPY_TO | Out-Null; $installs += $env:UNISONO_COPY_TO }
$installs += Get-Process Unisono, UnisonoCli -ErrorAction SilentlyContinue | Where-Object Path | ForEach-Object { Split-Path -Parent $_.Path }
if (Test-Path (Join-Path $root "Unisono.exe")) { $installs += $root }
$skip = @($target, $dist) | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') }
$installs = $installs | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') } | Sort-Object -Unique | Where-Object { $skip -notcontains $_ }
foreach ($dir in $installs) {
    foreach ($f in "Unisono.exe", "UnisonoCli.exe", "WebView2Loader.dll") {
        $dest = Join-Path $dir $f
        $prev = $null
        try {
            # A running exe is locked but may be renamed: park it as .prev (as above), then copy the new one in.
            if (Test-Path $dest) {
                $base = [IO.Path]::GetFileNameWithoutExtension($f); $ext = [IO.Path]::GetExtension($f)
                $prev = Join-Path $dir "$base.prev$ext"
                Remove-Item $prev -Force -ErrorAction SilentlyContinue
                if (Test-Path $prev) { $prev = Join-Path $dir ("{0}.prev-{1:yyyyMMddHHmmss}{2}" -f $base, (Get-Date), $ext) }
                Move-Item $dest $prev -Force -ErrorAction Stop
            }
            Copy-Item (Join-Path $dist $f) $dest -Force -ErrorAction Stop
        }
        # Locked and not movable: left for the next build rather than failing this one.
        catch {
            Write-Host "note: could not copy $f to $dir ($_)"
            # Never leave the folder without the file: the parked old one goes back.
            if ($prev -and -not (Test-Path $dest) -and (Test-Path $prev)) { Move-Item $prev $dest -ErrorAction SilentlyContinue }
        }
    }
    Write-Host "Copied them to $dir as well"
}

# -Release: the build as a GitHub release (tag v<version>, a zip of dist\), which running copies find in their daily
# check (Settings: 'Look for new versions on GitHub'). Only from a clean, pushed tree, so the tag names what was built.
if ($Release) {
    if ($dirty) { throw "-Release needs a clean tree: commit first" }
    $ErrorActionPreference = "Continue"
    $ahead = (& git -C $root rev-list --count "@{u}..HEAD" 2>$null)
    $ErrorActionPreference = "Stop"
    if ($ahead -ne "0") { throw "-Release needs HEAD pushed to GitHub first (it is $ahead commit(s) ahead, or has no upstream)" }
    $zip = Join-Path $dist "Unisono-$version-win-x64.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $dist "Unisono.exe"), (Join-Path $dist "UnisonoCli.exe"), (Join-Path $dist "WebView2Loader.dll") -DestinationPath $zip
    & gh release create "v$version" $zip --repo ibst1/unisono --target (& git -C $root rev-parse HEAD) --title "Unisono $version" --generate-notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
    Write-Host "Released v$version on GitHub"
}