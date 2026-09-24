<#
.SYNOPSIS
  End-to-end tests for Unisono against the real Google Drive and/or Synology NAS of the signed-in user.

.DESCRIPTION
  Runs the published UnisonoCli.exe / Unisono.exe with UNISONO_DATA pointed at throwaway folders, so the real
  settings, pairs and sync state are never touched (sign-ins are shared: they live in %LOCALAPPDATA%\Unisono).
  Two local folders ("computer A" and "computer B") sync with one temporary remote folder, which is removed at the end
  (Drive: moved to the trash; NAS: deleted, i.e. into the shared folder's recycle bin if that is on).

  Suites:
    drive   sync both ways, conflicts, deletions, renames and moves, ignore rules, versions, dry run, selective sync,
            download-only
    nas     the same core on a NAS folder (needs -NasServer, -NasUser and a stored login for them)
    folder  the same core with an ordinary folder as the other side (-OtherFolder, default a scratch folder)
    ondemand  files on demand: placeholders, download on open, keep / free up space, edits, deletes, renames, turning it off
              (registers a temporary sync root in Explorer; removed at the end)
    ui      starts the window on a scratch config and checks that every part of the page renders without errors

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Test-Unisono.ps1
  powershell -ExecutionPolicy Bypass -File tools\Test-Unisono.ps1 -Suite drive,nas,ui -NasServer myid -NasUser me -NasFolder "/home/tests"
#>
param(
    [string[]]$Suite = @("drive", "ui"),
    [string]$Bin = "$env:USERPROFILE\Program\Unisono",
    [string]$NasServer,
    [string]$NasUser,
    [string]$NasFolder,
    [string]$OtherFolder,
    [switch]$KeepFiles
)

$ErrorActionPreference = 'Continue'
# 'powershell -File' passes '-Suite drive,ui' as one string.
$Suite = @($Suite | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
$cli = Join-Path $Bin "UnisonoCli.exe"
$app = Join-Path $Bin "Unisono.exe"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$work = Join-Path $env:TEMP "unisono-tests\$stamp"
New-Item -ItemType Directory -Force $work | Out-Null
$script:pass = 0; $script:fail = 0; $script:failures = @()

function Section($t) { Write-Host "`n=== $t" -ForegroundColor Cyan }
function Check([string]$name, [scriptblock]$test) {
    $ok = $false
    try { $ok = [bool](& $test) } catch { $ok = $false }
    if ($ok) { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else { $script:fail++; $script:failures += $name; Write-Host "  FAIL  $name" -ForegroundColor Red }
}
# (Not named "Dir": that is a built-in alias, and aliases win over functions.)
function New-TestDir([string]$path) { New-Item -ItemType Directory -Force $path | Out-Null; $path }
# Every CLI or app run goes through this: never against the real settings folder.
function Use-Data([string]$dir) {
    if (-not $dir -or -not (Test-Path -LiteralPath $dir) -or -not ([IO.Path]::GetFullPath($dir).StartsWith([IO.Path]::GetFullPath($work), [StringComparison]::OrdinalIgnoreCase))) {
        throw "refusing to run: data folder '$dir' is not a test folder under $work"
    }
    $env:UNISONO_DATA = $dir
}
function Wipe([string]$path) {
    # WebView2's helper processes let go of their profile a moment after the window closes.
    for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath $path); $i++) {
        try { [IO.Directory]::Delete($path, $true) } catch { Start-Sleep -Milliseconds 500 }
    }
    if (Test-Path -LiteralPath $path) { Write-Host "  (could not remove $path; delete it later)" -ForegroundColor DarkGray }
}

# One simulated computer: its own data dir (config, state, log, versions) and local folder.
function Computer([string]$name, [hashtable]$pair) {
    $c = @{ Name = $name; Data = (New-TestDir "$work\$name-data"); Local = (New-TestDir "$work\$name") }
    $p = @{ LocalPath = $c.Local; PollSeconds = 30 } + $pair
    @{ Pairs = @($p); Options = @{ KeepVersions = 5; KeepVersionsDays = 30; PauseOnMetered = $false } } |
        ConvertTo-Json -Depth 6 | Set-Content "$($c.Data)\config.json" -Encoding UTF8
    $c
}
# One sync cycle; returns the log lines it produced.
function Sync($c, [switch]$DryRun) {
    Use-Data $c.Data
    $cmd = @("sync"); if ($DryRun) { $cmd += "--dry-run" }
    $out = & $cli @cmd 2>&1 | ForEach-Object { "$_" }
    $out | Where-Object { $_ -match ' (warn|error) ' } | ForEach-Object { Write-Host "        $($c.Name): $_" -ForegroundColor DarkYellow }
    $out
}
function Both($a, $b) { Sync $a | Out-Null; Start-Sleep 3; Sync $b }
function Has($c, $rel) { Test-Path -LiteralPath (Join-Path $c.Local $rel) }
function Text($c, $rel) { (Get-Content -LiteralPath (Join-Path $c.Local $rel) -Raw).Trim() }
function Put($c, $rel, $text) { $f = Join-Path $c.Local $rel; New-Item -ItemType Directory -Force (Split-Path $f) | Out-Null; Set-Content -LiteralPath $f $text }

# ------------------------------------------------------------------ core
function Test-Core([hashtable]$pair, [string]$label) {
    $a = Computer "$label-A" $pair
    $b = Computer "$label-B" $pair

    Section "${label}: basic sync"
    Put $a "hello.txt" "hello"
    Put $a "sub\note.txt" "in a folder"
    $null = Both $a $b
    Check "$label new file reaches the other computer" { (Text $b "hello.txt") -eq "hello" }
    Check "$label new folder with file reaches the other computer" { (Text $b "sub\note.txt") -eq "in a folder" }

    Put $b "hello.txt" "hello from B"
    $null = Both $b $a
    Check "$label change travels back" { (Text $a "hello.txt") -eq "hello from B" }
    Check "$label the replaced file was kept as a version" { @(Get-ChildItem "$($a.Data)\versions" -Recurse -File -ErrorAction SilentlyContinue).Count -ge 1 }

    Section "${label}: conflict"
    Put $a "hello.txt" "edit on A"; Put $b "hello.txt" "edit on B"
    $null = Sync $a; Start-Sleep 3; $null = Sync $b; Start-Sleep 3; $null = Sync $a
    Check "$label both edits survive as a conflict copy" { @(Get-ChildItem $a.Local -Filter "hello (conflict*" -File).Count -ge 1 }

    Section "${label}: renames and moves"
    Rename-Item (Join-Path $a.Local "sub\note.txt") "renamed.txt"
    $outA = Sync $a; Start-Sleep 3; $outB = Sync $b
    Check "$label file rename is a remote move" { ($outA -join "`n") -match "moved remotely" }
    Check "$label ... and a local move on the other computer" { (($outB -join "`n") -match "moved locally") -and (Has $b "sub\renamed.txt") -and -not (Has $b "sub\note.txt") }
    Rename-Item (Join-Path $a.Local "sub") "folder2"
    $outA = Sync $a; Start-Sleep 3; $outB = Sync $b
    Check "$label folder rename is one remote move" { ($outA -join "`n") -match "moved folder remotely" }
    Check "$label ... and a local folder move on the other computer" { (Has $b "folder2\renamed.txt") -and -not (Has $b "sub") -and -not (($outB -join "`n") -match "downloaded") }

    Section "${label}: deletion"
    [IO.File]::Delete((Join-Path $a.Local "folder2\renamed.txt"))
    $null = Both $a $b
    Check "$label deletion reaches the other computer" { -not (Has $b "folder2\renamed.txt") }

    Section "${label}: ignore rules"
    Put $a ".unisonoignore" "*.log`ncache/"
    Put $a "debug.log" "ignored"; Put $a "cache\tmp.bin" "ignored"; Put $a "kept.txt" "kept"
    $null = Both $a $b
    Check "$label ignored files stay local" { -not (Has $b "debug.log") -and -not (Has $b "cache") }
    Check "$label other files and the ignore file itself sync" { (Has $b "kept.txt") -and (Has $b ".unisonoignore") }

    Section "${label}: dry run"
    Put $a "dry.txt" "only planned"
    $dry = Sync $a -DryRun; Start-Sleep 3; $null = Sync $b
    Check "$label dry run plans the upload" { ($dry -join "`n") -match "would upload: dry.txt" }
    Check "$label dry run changes nothing" { -not (Has $b "dry.txt") }
    $null = Both $a $b
    Check "$label a real run afterwards does it" { Has $b "dry.txt" }

    Section "${label}: one-way (download only)"
    $d = Computer "$label-D" ($pair + @{ Direction = "DownloadOnly" })
    $null = Sync $d
    Put $d "local-only.txt" "stays here"
    Put $d "kept.txt" "edited on D"
    $null = Sync $d; Start-Sleep 3; $null = Sync $a
    Check "$label download-only uploads nothing" { -not (Has $a "local-only.txt") -and (Text $a "kept.txt") -eq "kept" }

    Section "${label}: selective sync"
    Put $a "skip\secret.txt" "not for E"; Put $a "take\ok.txt" "for E"
    $null = Sync $a; Start-Sleep 3
    $e = Computer "$label-E" ($pair + @{ ExcludeFolders = @("skip") })
    $null = Sync $e
    Check "$label excluded folder is not synced" { (Has $e "take\ok.txt") -and -not (Has $e "skip") }
    Put $e "skip\local.txt" "local only"
    $null = Sync $e; Start-Sleep 3; $null = Sync $a
    Check "$label ... nor uploaded from there" { -not (Has $a "skip\local.txt") }

    Section "${label}: idle"
    $idle = Sync $a
    Check "$label an idle cycle changes nothing" { -not (($idle -join "`n") -match "(uploaded|downloaded|moved|deleted|recycled)") }
}

# ------------------------------------------------------------ files on demand
function Attr($c, $rel) { [int][IO.File]::GetAttributes((Join-Path $c.Local $rel)) }
function IsOnline($c, $rel) { $a = Attr $c $rel; ($a -band 0x400) -ne 0 -and ($a -band (0x400000 -bor 0x40000 -bor 0x1000)) -ne 0 }
function Test-OnDemand([hashtable]$pair) {
    $a = Computer "od-A" $pair
    $b = Computer "od-B" ($pair + @{ OnDemand = $true })
    try {
        Section "ondemand: placeholders"
        Put $a "hello.txt" "hello"; Put $a "sub\note.txt" "a note"; Put $a "big\data.bin" ("x" * 100000)
        $null = Sync $a; Start-Sleep 2; $outB = Sync $b
        Check "ondemand remote files appear locally" { (Has $b "hello.txt") -and (Has $b "sub\note.txt") -and (Has $b "big\data.bin") }
        Check "ondemand ... as online-only placeholders, nothing downloaded" { (IsOnline $b "hello.txt") -and (IsOnline $b "big\data.bin") -and -not (($outB -join "`n") -match "downloaded") }
        Check "ondemand ... with the right size" { (Get-Item -LiteralPath (Join-Path $b.Local "big\data.bin")).Length -eq (Get-Item -LiteralPath (Join-Path $a.Local "big\data.bin")).Length }

        Section "ondemand: open an online-only file"
        Use-Data $b.Data
        $run = Start-Process $cli -ArgumentList "run" -PassThru -WindowStyle Hidden
        Start-Sleep 6
        $text = try { (Get-Content -LiteralPath (Join-Path $b.Local "hello.txt") -Raw).Trim() } catch { "ERR $_" }
        Stop-Process -Id $run.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 2
        Check "ondemand opening downloads the content" { $text -eq "hello" }
        Check "ondemand ... and it stays on this computer" { -not (IsOnline $b "hello.txt") }

        Section "ondemand: always keep on this device"
        attrib +P (Join-Path $b.Local "sub") | Out-Null
        $out = Sync $b
        Check "ondemand a kept folder's files are downloaded" { -not (IsOnline $b "sub\note.txt") -and (Text $b "sub\note.txt") -eq "a note" }
        Put $a "sub\later.txt" "added later"; $null = Sync $a; Start-Sleep 2; $null = Sync $b
        Check "ondemand ... new files in it too" { (Has $b "sub\later.txt") -and -not (IsOnline $b "sub\later.txt") }

        Section "ondemand: free up space"
        attrib -P +U (Join-Path $b.Local "sub\note.txt") | Out-Null
        $out = Sync $b
        Check "ondemand freeing up space makes it online-only again" { (IsOnline $b "sub\note.txt") -and (($out -join "`n") -match "freed up space") }

        Section "ondemand: edits, remote changes, deletes, renames"
        Put $b "hello.txt" "edited on B"
        $null = Sync $b; Start-Sleep 2; $null = Sync $a
        Check "ondemand a local edit is uploaded" { (Text $a "hello.txt") -eq "edited on B" }
        Put $a "big\data.bin" ("y" * 200000); $null = Sync $a; Start-Sleep 2; $outB = Sync $b
        Check "ondemand a remote change updates the placeholder only" { (IsOnline $b "big\data.bin") -and (Get-Item -LiteralPath (Join-Path $b.Local "big\data.bin")).Length -eq (Get-Item -LiteralPath (Join-Path $a.Local "big\data.bin")).Length -and -not (($outB -join "`n") -match "downloaded") }
        [IO.File]::Delete((Join-Path $b.Local "big\data.bin"))
        $null = Sync $b; Start-Sleep 2; $null = Sync $a
        Check "ondemand deleting an online-only file deletes it remotely" { -not (Has $a "big\data.bin") }
        Rename-Item (Join-Path $b.Local "sub\note.txt") "renamed.txt"
        $outB = Sync $b; Start-Sleep 2; $null = Sync $a
        Check "ondemand renaming an online-only file is a remote move" { (($outB -join "`n") -match "moved remotely") -and (Has $a "sub\renamed.txt") -and -not (Has $a "sub\note.txt") }
        Check "ondemand ... without downloading it" { IsOnline $b "sub\renamed.txt" }
        Put $b "fromB.txt" "new on B"; $null = Sync $b; Start-Sleep 2; $null = Sync $a
        Check "ondemand a new local file is uploaded" { (Text $a "fromB.txt") -eq "new on B" }
        $idle = Sync $b
        Check "ondemand an idle cycle changes nothing" { -not (($idle -join "`n") -match "(uploaded|downloaded|moved|deleted|recycled|online-only)") }

        Section "ondemand: turning it off"
        Use-Data $b.Data; & $cli ondemand-off 2>&1 | Out-Null
        $null = Sync $b
        Check "ondemand after turning it off every file is a real local file" { (Text $b "sub\renamed.txt") -eq "a note" -and -not (IsOnline $b "sub\renamed.txt") -and (Text $b "hello.txt") -eq "edited on B" }
        Check "ondemand ... and nothing was deleted remotely" { (Has $a "sub\renamed.txt") -and (Has $a "sub\later.txt") -and (Has $a "fromB.txt") }
    }
    finally { Use-Data $b.Data; & $cli ondemand-off 2>&1 | Out-Null }
}

# ------------------------------------------------------------------ UI
function Test-Ui {
    Section "ui: window renders"
    $c = Computer "ui" @{ Kind = "Drive"; RemotePath = "Unisono-tests-$stamp-ui"; Machines = @("NOT-THIS-COMPUTER") }
    # A second pair of the folder kind, also inactive here, so both kinds of card are drawn.
    $cfg = Get-Content "$($c.Data)\config.json" -Raw | ConvertFrom-Json
    $cfg.Pairs += [pscustomobject]@{ Kind = "Folder"; RemotePath = "\\server\share\ui-test"; LocalPath = "$($c.Local)-2"; Machines = @("NOT-THIS-COMPUTER") }
    $cfg | ConvertTo-Json -Depth 6 | Set-Content "$($c.Data)\config.json" -Encoding UTF8
    Use-Data $c.Data; $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=9339"
    $p = Start-Process $app -PassThru; Start-Sleep 15
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = ""
    try {
        $page = (Invoke-RestMethod "http://127.0.0.1:9339/json" -TimeoutSec 5) | Where-Object type -eq 'page' | Select-Object -First 1
        $ws = New-Object Net.WebSockets.ClientWebSocket
        $ws.ConnectAsync([Uri]$page.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
        $js = @"
(() => {
  const errors = [];
  for (const f of [renderHeader, renderAccounts, renderPairs, renderSettings]) { try { f(); } catch (e) { errors.push(f.name + ': ' + e.message); } }
  return JSON.stringify({ errors, pairs: document.querySelectorAll('#pairList .card').length,
    google: document.querySelectorAll('#googleList .card').length, nas: document.querySelectorAll('#nasList .card').length });
})()
"@
        $msg = @{ id = 1; method = "Runtime.evaluate"; params = @{ expression = $js; returnByValue = $true } } | ConvertTo-Json -Depth 5 -Compress
        $ws.SendAsync([ArraySegment[byte]][Text.Encoding]::UTF8.GetBytes($msg), 'Text', $true, [Threading.CancellationToken]::None).Wait()
        $buf = New-Object byte[] 65536; $r = $ws.ReceiveAsync([ArraySegment[byte]]$buf, [Threading.CancellationToken]::None).Result
        $res = ([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count) | ConvertFrom-Json).result.result.value | ConvertFrom-Json
        Check "ui every part renders without errors" { $res.errors.Count -eq 0 }
        Check "ui the scratch pairs are listed" { $res.pairs -eq 2 }
        Check "ui stored accounts are listed" { ($res.google + $res.nas) -ge 1 }
    }
    catch { Check "ui debug connection" { $false } }
    finally { Stop-Process -Id $p.Id -ErrorAction SilentlyContinue }
    $log = Get-ChildItem "$($c.Data)\log\*.log" -ErrorAction SilentlyContinue | Get-Content -ErrorAction SilentlyContinue
    Check "ui no page errors were logged" { -not (($log -join "`n") -match "error window:") }
}

# ------------------------------------------------------------------ run
if (-not (Test-Path $cli)) { throw "UnisonoCli.exe not found in $Bin; run build.ps1 first" }
& $cli version | Select-Object -First 1 | ForEach-Object { Write-Host $_ }

if ($Suite -contains "drive") {
    $remote = "Unisono-tests-$stamp"
    Use-Data (New-TestDir "$work\init-data")
    & $cli init $remote (New-TestDir "$work\init") --create | Out-Null
    try { Test-Core @{ Kind = "Drive"; RemotePath = $remote } "drive" }
    finally { if (-not $KeepFiles) { & $cli drive-trash $remote | Out-Null } }
}
if ($Suite -contains "nas") {
    if (-not $NasServer -or -not $NasUser -or -not $NasFolder) { throw "the nas suite needs -NasServer, -NasUser and -NasFolder (a folder inside a shared folder)" }
    $server = if ($NasServer -notmatch '[./:]') { "https://$($NasServer.ToLower()).quickconnect.to" } else { $NasServer }
    $remote = "$($NasFolder.TrimEnd('/'))/unisono-tests-$stamp"
    Use-Data (New-TestDir "$work\init-nas-data")
    & $cli init $remote (New-TestDir "$work\init-nas") --create --nas $server $NasUser | Out-Null
    try { Test-Core @{ Kind = "Synology"; RemotePath = $remote; Server = $server; User = $NasUser } "nas" }
    finally { if (-not $KeepFiles) { & $cli nas api $server entry.cgi api=SYNO.FileStation.Delete version=2 method=delete "path=$remote" recursive=true | Out-Null } }
}
if ($Suite -contains "folder") {
    # The other side may be a network folder (-OtherFolder \\server\share\tests); a subfolder is created and removed.
    $other = if ($OtherFolder) { Join-Path $OtherFolder "unisono-tests-$stamp" } else { "$work\other-folder" }
    New-TestDir $other | Out-Null
    try {
        Test-Core @{ Kind = "Folder"; RemotePath = $other } "folder"
        Check "folder the files are plain files in the other folder" { (Test-Path -LiteralPath "$other\kept.txt") -and (Test-Path -LiteralPath "$other\folder2") }
        Check "folder no temporary copies are left behind" { @(Get-ChildItem -LiteralPath $other -Recurse -Filter "*.unisono-part").Count -eq 0 }
        Section "folder: previous versions in a folder of your choice"
        $v = Computer "folder-V" @{ Kind = "Folder"; RemotePath = $other }
        $vdir = New-TestDir "$work\my-versions"
        $cfg = Get-Content "$($v.Data)\config.json" -Raw | ConvertFrom-Json; $cfg.Options | Add-Member VersionsFolder $vdir -Force
        $cfg | ConvertTo-Json -Depth 6 | Set-Content "$($v.Data)\config.json" -Encoding UTF8
        $null = Sync $v
        Set-Content -LiteralPath "$other\kept.txt" "changed on the other side"; (Get-Item -LiteralPath "$other\kept.txt").LastWriteTime = (Get-Date).AddMinutes(-1)
        $null = Sync $v
        Check "folder a replaced file is kept in the chosen versions folder" { @(Get-ChildItem $vdir -Recurse -File).Count -ge 1 -and -not (Test-Path "$($v.Data)\versions") }
    }
    finally { if (-not $KeepFiles -and $OtherFolder) { Wipe $other } }
}
if ($Suite -contains "ondemand") {
    $other = "$work\od-other"; New-TestDir $other | Out-Null
    Test-OnDemand @{ Kind = "Folder"; RemotePath = $other }
}
if ($Suite -contains "ui") { Test-Ui }

if (-not $KeepFiles) { Wipe $work }
Write-Host ""
Write-Host ("{0} passed, {1} failed" -f $script:pass, $script:fail) -ForegroundColor $(if ($script:fail) { "Red" } else { "Green" })
$script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit $script:fail
