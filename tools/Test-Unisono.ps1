<#
.SYNOPSIS
  End-to-end tests for Unisono against the real Google Drive and/or Synology NAS of the signed-in user.

.DESCRIPTION
  Runs the published UnisonoCli.exe / Unisono.exe with UNISONO_DATA pointed at throwaway folders, so the real
  settings, pairs and sync state are never touched (sign-ins are shared: they live in %LOCALAPPDATA%\Unisono).
  Two local folders ("computer A" and "computer B") sync with one temporary remote folder, which is removed at the end
  (Drive: moved to the trash; NAS: deleted, i.e. into the shared folder's recycle bin if that is on).

  Suites:
    writeback  Google Docs exports edited locally go back into the document (also part of "drive")
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
    [switch]$KeepFiles,
    # Runs UnisonoCli.dll from -Bin through dotnet: for a build that is not published (where policy blocks exes elsewhere).
    [switch]$ViaDotnet
)

$ErrorActionPreference = 'Continue'
# 'powershell -File' passes '-Suite drive,ui' as one string.
$Suite = @($Suite | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
$cli = Join-Path $Bin "UnisonoCli.exe"
if ($ViaDotnet) {
    $dotnetExe = if (Test-Path "$env:LOCALAPPDATA\dotnet\dotnet.exe") { "$env:LOCALAPPDATA\dotnet\dotnet.exe" } else { "dotnet" }
    $cliDll = Join-Path $Bin "UnisonoCli.dll"
    $cli = { & $dotnetExe $cliDll @args }.GetNewClosure()
}
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
# A second look on the other computer: Drive's change feed can take more than a few seconds to report a change.
function Both($a, $b) { Sync $a | Out-Null; Start-Sleep 3; $first = Sync $b; Start-Sleep 5; @($first) + @(Sync $b) }
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
    # Drive's change feed can take longer than the pauses above: give it a few more rounds before judging.
    for ($i = 0; $i -lt 3 -and @(Get-ChildItem $a.Local -Filter "hello (conflict*" -File).Count -eq 0; $i++) { Start-Sleep 5; $null = Sync $b; $null = Sync $a }
    Check "$label both edits survive as a conflict copy" { @(Get-ChildItem $a.Local -Filter "hello (conflict*" -File).Count -ge 1 }

    Section "${label}: notes changed on both sides are merged"
    $lines = 1..6 | ForEach-Object { "line $_" }
    Put $a "note.md" ($lines -join "`n")
    $null = Both $a $b
    Put $a "note.md" ((@("line 1 from A") + $lines[1..5]) -join "`n")
    Put $b "note.md" (($lines[0..4] + @("line 6 from B")) -join "`n")
    $merged = (@("line 1 from A") + $lines[1..4] + @("line 6 from B")) -join "`n"
    function NoteIs($c, $want) { (Has $c "note.md") -and ((Text $c "note.md") -replace "`r", "") -eq $want }
    Start-Sleep 3 # past the moment a file counts as still being saved
    # Drive keeps earlier versions itself: without the locally kept base, the merge must find it there.
    if ($pair.Kind -eq "Drive" -and (Test-Path -LiteralPath "$($b.Data)\base")) { [IO.Directory]::Delete("\\?\" + "$($b.Data)\base", $true) }
    $null = Sync $a; Start-Sleep 3; $outB = Sync $b; Start-Sleep 3; $null = Sync $a
    for ($i = 0; $i -lt 3 -and -not ((NoteIs $a $merged) -and (NoteIs $b $merged)); $i++) { Start-Sleep 5; $outB += Sync $b; $null = Sync $a } # change feed lag
    Check "$label edits in different places end up in one file on both computers" { (NoteIs $a $merged) -and (NoteIs $b $merged) }
    Check "$label ... without a conflict copy" { @(Get-ChildItem $a.Local, $b.Local -Filter "note (conflict*" -File).Count -eq 0 }
    Check "$label ... and the merge is logged" { ($outB -join "`n") -match "merged \(changed on both sides" }
    Check "$label ... both earlier versions are kept" { @(Get-ChildItem -LiteralPath ("\\?\" + "$($b.Data)\versions") -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Directory.Name -eq "note.md" }).Count -ge 2 } # the long-path prefix: deeper than 260 characters
    Put $a "note.md" ($merged -replace "line 3", "line 3 from A")
    Put $b "note.md" ($merged -replace "line 3", "line 3 from B")
    Start-Sleep 3
    $null = Sync $a; Start-Sleep 3; $null = Sync $b; Start-Sleep 3; $null = Sync $a
    for ($i = 0; $i -lt 3 -and @(Get-ChildItem $a.Local, $b.Local -Filter "note (conflict*" -File).Count -eq 0; $i++) { Start-Sleep 5; $null = Sync $b; $null = Sync $a }
    Check "$label edits of the same line are kept as a conflict copy" { @(Get-ChildItem $a.Local, $b.Local -Filter "note (conflict*" -File).Count -ge 1 }

    Section "${label}: renames and moves"
    Rename-Item (Join-Path $a.Local "sub\note.txt") "renamed.txt"
    $outA = Sync $a; Start-Sleep 3; $outB = Sync $b
    for ($i = 0; $i -lt 3 -and -not (Has $b "sub\renamed.txt"); $i++) { Start-Sleep 5; $outB += Sync $b } # change feed lag
    Check "$label file rename is a remote move" { ($outA -join "`n") -match "moved remotely" }
    Check "$label ... and a local move on the other computer" { (($outB -join "`n") -match "moved locally") -and (Has $b "sub\renamed.txt") -and -not (Has $b "sub\note.txt") }
    Rename-Item (Join-Path $a.Local "sub") "folder2"
    $outA = Sync $a; Start-Sleep 3; $outB = Sync $b
    for ($i = 0; $i -lt 3 -and -not (Has $b "folder2\renamed.txt"); $i++) { Start-Sleep 5; $outB += Sync $b } # change feed lag
    Check "$label folder rename is one remote move" { ($outA -join "`n") -match "moved folder remotely" }
    Check "$label ... and a local folder move on the other computer" { (Has $b "folder2\renamed.txt") -and -not (Has $b "sub") -and -not (($outB -join "`n") -match "downloaded.*renamed\.txt") } # a late download from an earlier step is fine

    Section "${label}: deletion"
    [IO.File]::Delete((Join-Path $a.Local "folder2\renamed.txt"))
    $null = Both $a $b
    for ($i = 0; $i -lt 3 -and (Has $b "folder2\renamed.txt"); $i++) { Start-Sleep 5; $null = Sync $b } # change feed lag
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
    for ($i = 0; $i -lt 3 -and -not (Has $b "dry.txt"); $i++) { Start-Sleep 5; $null = Sync $b } # change feed lag
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

# ------------------------------------------------------------ Google Docs write-back
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function New-Docx([string]$path, [string]$text) {
    if (Test-Path -LiteralPath $path) { Set-ItemProperty -LiteralPath $path -Name IsReadOnly -Value $false; [IO.File]::Delete($path) }
    $zip = [IO.Compression.ZipFile]::Open($path, "Create")
    $parts = [ordered]@{
        "[Content_Types].xml" = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>'
        "_rels/.rels" = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
        "word/document.xml" = "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><w:document xmlns:w=`"http://schemas.openxmlformats.org/wordprocessingml/2006/main`"><w:body><w:p><w:r><w:t>$text</w:t></w:r></w:p></w:body></w:document>"
    }
    foreach ($k in $parts.Keys) { $e = $zip.CreateEntry($k); $w = New-Object IO.StreamWriter($e.Open()); $w.Write($parts[$k]); $w.Dispose() }
    $zip.Dispose()
}
function Docx-Text([string]$path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try { $r = New-Object IO.StreamReader(($zip.Entries | Where-Object FullName -eq "word/document.xml").Open()); $r.ReadToEnd() } finally { $zip.Dispose() }
}
function Docx-Decimals([string]$path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try { ($zip.Entries | Where-Object { $_.FullName -like "word/*.xml" } | ForEach-Object { [regex]::Matches((New-Object IO.StreamReader($_.Open())).ReadToEnd(), 'w:\w+="-?\d+\.\d+"').Count } | Measure-Object -Sum).Sum }
    finally { $zip.Dispose() }
}
function Test-WriteBack([string]$remote) {
    Section "drive: Google Docs exports and local edits"
    Use-Data (Join-Path $work "init-data"); & $cli drive-newdoc $remote "WB doc" | Out-Null
    $w1 = Computer "wb-1" @{ Kind = "Drive"; RemotePath = $remote }
    $null = Sync $w1
    $doc = Join-Path $w1.Local "WB doc.docx"
    Check "drive a Google document is exported as a writable docx" { (Test-Path -LiteralPath $doc) -and -not (Get-Item -LiteralPath $doc).IsReadOnly }
    Check "drive ... repaired for Word (no fractional sizes, which make Word drop styles)" { (Docx-Decimals $doc) -eq 0 }
    New-Docx $doc "written back from wb-1"; (Get-Item -LiteralPath $doc).LastWriteTime = (Get-Date).AddMinutes(-1)
    $out = (Sync $w1) -join "`n"
    $w2 = Computer "wb-2" @{ Kind = "Drive"; RemotePath = $remote }
    Start-Sleep 5; $null = Sync $w1; $null = Sync $w2
    if ($out -match "wrote back into the Google document") {
        Write-Host "        (the Docs API is enabled: the edit went back into the document)" -ForegroundColor DarkGray
        Check "drive ... another computer's export has the new text" { (Docx-Text (Join-Path $w2.Local "WB doc.docx")) -match "written back from wb-1" }
        $idle = Sync $w1
        Check "drive ... without exporting it again over the edited file" { -not (($idle -join "`n") -match "exported|wrote back") }
        # A change made elsewhere must arrive within a cycle, not when Drive's change feed gets round to it (minutes).
        New-Docx (Join-Path $w2.Local "WB doc.docx") "changed on wb-2"; (Get-Item -LiteralPath (Join-Path $w2.Local "WB doc.docx")).LastWriteTime = (Get-Date).AddMinutes(-1)
        $null = Sync $w2; Start-Sleep 3; $null = Sync $w1
        Check "drive a document changed elsewhere is exported again at once" { (Docx-Text $doc) -match "changed on wb-2" }
        # An edit in Google Docs itself: Drive's modified time trails it by a minute or two, the Docs revision does not.
        Use-Data (Join-Path $work "init-data"); & $cli drive-docedit $remote "WB doc" "typed in Google Docs" | Out-Null
        Start-Sleep 2; $null = Sync $w1
        Check "drive an edit in Google Docs reaches the export in the next cycle" { (Docx-Text $doc) -match "typed in Google Docs" }
    }
    else {
        Write-Host "        (the Docs API is not enabled: tabs cannot be checked, so edits are kept as their own file)" -ForegroundColor DarkGray
        Check "drive an edit that cannot safely go back is kept as its own file" { $out -match "not written back into the Google document" -and @(Get-ChildItem -LiteralPath $w1.Local -Filter "WB doc (edited*.docx").Count -eq 1 }
        Check "drive ... which reaches the other computer as an ordinary file" { $f = @(Get-ChildItem -LiteralPath $w2.Local -Filter "WB doc (edited*.docx"); $f.Count -eq 1 -and (Docx-Text $f[0].FullName) -match "written back from wb-1" }
        Check "drive ... and the Google document is unchanged" { -not ((Docx-Text (Join-Path $w2.Local "WB doc.docx")) -match "written back from wb-1") -and -not ((Docx-Text $doc) -match "written back from wb-1") }
    }
}
# Edits the document part of a docx in place with a script block (string in, string out).
function Edit-Docx([string]$path, [scriptblock]$edit) {
    $zip = [IO.Compression.ZipFile]::Open($path, "Update")
    try {
        $e = $zip.GetEntry("word/document.xml"); $r = New-Object IO.StreamReader($e.Open()); $xml = $r.ReadToEnd(); $r.Dispose()
        $new = & $edit $xml; $e.Delete()
        $w = New-Object IO.StreamWriter($zip.CreateEntry("word/document.xml").Open(), (New-Object Text.UTF8Encoding $false)); $w.Write($new); $w.Dispose()
    } finally { $zip.Dispose() }
}
function Test-TabWriteBack([string]$remote) {
    Section "drive: Word edits of a document with tabs go back tab by tab"
    Use-Data (Join-Path $work "init-data"); & $cli drive-newtabdoc $remote "Tabs doc" | Out-Null
    $t1 = Computer "tabs-1" @{ Kind = "Drive"; RemotePath = $remote }
    $null = Sync $t1
    $doc = Join-Path $t1.Local "Tabs doc.docx"
    Check "drive the tabbed document is exported" { Test-Path -LiteralPath $doc }
    Edit-Docx $doc {
        param($xml)
        $xml = $xml.Replace("Text in the second tab", "Text in the second tab, edited in Word")
        # A new paragraph after the first tab's text: a copy of that paragraph with other words.
        $i = $xml.IndexOf("Text in the first tab"); $s = $xml.LastIndexOf("<w:p ", $i); $e = $xml.IndexOf("</w:p>", $i) + 6
        $para = $xml.Substring($s, $e - $s)
        $xml = $xml.Insert($e, $para.Replace("Text in the first tab", "A new paragraph from Word"))
        # The heading one level down: Heading 1 in Word is Heading 1 in Google, so Heading 2 is Heading 2.
        $h = $xml.IndexOf("Heading one"); $hs = $xml.LastIndexOf("<w:p ", $h)
        $xml.Substring(0, $hs) + $xml.Substring($hs, $h - $hs).Replace('w:val="Heading1"', 'w:val="Heading2"') + $xml.Substring($h)
    }
    (Get-Item -LiteralPath $doc).LastWriteTime = (Get-Date).AddMinutes(-1)
    $out = (Sync $t1) -join "`n"
    Check "drive the edit is written back tab by tab" { $out -match "wrote back into the Google document, tab by tab" }
    Use-Data (Join-Path $work "init-data"); $text = (& $cli drive-doctext $remote "Tabs doc") -join "`n"
    Check "drive ... both tabs are still there" { @([regex]::Matches($text, "\[tab\]")).Count -eq 2 -and $text -match "\[tab\] First" -and $text -match "\[tab\] Second" }
    Check "drive ... the second tab has the edited text" { $text -match "Text in the second tab, edited in Word" }
    Check "drive ... the first tab has the new paragraph after its text" { $text -match "NORMAL_TEXT: Text in the first tab\s+NORMAL_TEXT: A new paragraph from Word" }
    Check "drive ... and the heading is one level down" { $text -match "HEADING_2: Heading one" }
    $idle = Sync $t1
    Check "drive ... and nothing is exported again over the edited file" { -not (($idle -join "`n") -match "exported|wrote back") }
}

# ------------------------------------------------------------ files on demand
function Attr($c, $rel) { [int][IO.File]::GetAttributes((Join-Path $c.Local $rel)) }
function IsOnline($c, $rel) { $a = Attr $c $rel; ($a -band 0x400) -ne 0 -and ($a -band (0x400000 -bor 0x40000 -bor 0x1000)) -ne 0 }
# Windows' own view of a placeholder: 1 = in sync (no pending arrows in Explorer), 0 = not, negative = not a placeholder.
function InSync([string]$path) {
    if (-not ("CfProbe.Api" -as [type])) {
        Add-Type -Namespace CfProbe -Name Api -MemberDefinition @"
[DllImport("cldapi.dll", CharSet = CharSet.Unicode)] static extern int CfOpenFileWithOplock(string p, int f, out IntPtr h);
[DllImport("cldapi.dll")] static extern IntPtr CfGetWin32HandleFromProtectedHandle(IntPtr h);
[DllImport("cldapi.dll")] static extern void CfCloseHandle(IntPtr h);
[DllImport("cldapi.dll")] static extern int CfGetPlaceholderInfo(IntPtr h, int c, byte[] b, int l, out int r);
public static int InSync(string p) {
  IntPtr ph; if (CfOpenFileWithOplock(p, 0, out ph) < 0) return -1;
  try { var b = new byte[4096]; int r; if (CfGetPlaceholderInfo(CfGetWin32HandleFromProtectedHandle(ph), 0, b, 4096, out r) != 0) return -2; return BitConverter.ToInt32(b, 4); }
  finally { CfCloseHandle(ph); }
}
"@
    }
    [CfProbe.Api]::InSync($path)
}
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
        Check "ondemand folders are in sync after the cycle (no pending arrows)" { (InSync (Join-Path $b.Local "sub")) -eq 1 -and (InSync $b.Local) -eq 1 }

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
        Put $b "fromB.txt" "new on B"; Put $b "readonly.txt" "read-only"
        (Get-Item -LiteralPath (Join-Path $b.Local "fromB.txt")).LastWriteTime = (Get-Date).AddMinutes(-1); (Get-Item -LiteralPath (Join-Path $b.Local "readonly.txt")).LastWriteTime = (Get-Date).AddMinutes(-1); Set-ItemProperty -LiteralPath (Join-Path $b.Local "readonly.txt") -Name IsReadOnly -Value $true
        $null = Sync $b; Start-Sleep 2; $null = Sync $a
        Check "ondemand a new local file is uploaded" { (Text $a "fromB.txt") -eq "new on B" }
        $idle = Sync $b
        Check "ondemand an idle cycle changes nothing" { -not (($idle -join "`n") -match "(uploaded|downloaded|moved|deleted|recycled|online-only)") }
        Check "ondemand every file and folder is in sync (Explorer shows no pending arrows)" { @(Get-ChildItem -LiteralPath $b.Local -Recurse -Force | Where-Object { (InSync $_.FullName) -ne 1 }).Count -eq 0 }

        Section "ondemand: turning it off"
        Use-Data $b.Data; & $cli ondemand-off 2>&1 | Out-Null
        $null = Sync $b
        Check "ondemand after turning it off every file is a real local file" { (Text $b "sub\renamed.txt") -eq "a note" -and -not (IsOnline $b "sub\renamed.txt") -and (Text $b "hello.txt") -eq "edited on B" }
        Check "ondemand ... and nothing was deleted remotely" { (Has $a "sub\renamed.txt") -and (Has $a "sub\later.txt") -and (Has $a "fromB.txt") }
    }
    finally { Use-Data $b.Data; & $cli ondemand-off 2>&1 | Out-Null }
}

# ------------------------------------------------------------------ sync groups
# Two computers, each with two "vaults" (W, M) synced with a shared cloud folder, and the vaults' .obsidian settings in a
# group whose hub is W's. Each computer has its own home folder (USERPROFILE), so the group file's %USERPROFILE% paths
# mean the same folders on both, as on real computers.
# (The block sees the caller's variables, so this function's own names must not hide them.)
function As([string]$asHome, [scriptblock]$asBlock) {
    $asSaved = $env:USERPROFILE, $env:LOCALAPPDATA
    $env:USERPROFILE = $asHome; $env:LOCALAPPDATA = "$asHome\AppData\Local" # not a prefix of the home's folders
    try { & $asBlock } finally { $env:USERPROFILE, $env:LOCALAPPDATA = $asSaved }
}
function GroupComputer([string]$name, [string]$cloud) {
    $c = @{ Name = $name; Data = (New-TestDir "$work\$name-data"); Home = (New-TestDir "$work\$name-home") }
    $c.Local = $c.Home
    $pairs = @("W", "M") | ForEach-Object { New-TestDir "$($c.Home)\$_" | Out-Null; New-TestDir "$cloud\$_" | Out-Null
        @{ Kind = "Folder"; RemotePath = "$cloud\$_"; LocalPath = "$($c.Home)\$_"; PollSeconds = 30 } }
    @{ Pairs = $pairs; Options = @{ KeepVersions = 5; KeepVersionsDays = 30; PauseOnMetered = $false } } |
        ConvertTo-Json -Depth 6 | Set-Content "$($c.Data)\config.json" -Encoding UTF8
    $c
}
# Files written in the last 2 s are left for the next cycle (still being saved): give them that.
function GSync($c) { Start-Sleep -Milliseconds 2100; As $c.Home { Sync $c } }
function Groups($c, [string[]]$what) { As $c.Home { Use-Data $c.Data; (& $cli groups @what 2>&1 | ForEach-Object { "$_" }) -join "`n" } }
function GroupsOf($c) { @((Get-Content "$($c.Data)\config.json" -Raw | ConvertFrom-Json).Groups) | Where-Object { $_ } }

function Test-Groups {
    Section "groups: several folders kept in step through a hub"
    $cloud = New-TestDir "$work\grp-cloud"
    $a = GroupComputer "grp-A" $cloud
    Put $a "W\.obsidian\hotkeys.json" "hub"; Put $a "W\.obsidian\plugins\p\main.js" "plugin"; Put $a "W\note.md" "a note"
    Put $a "M\.obsidian\hotkeys.json" "M's own"; Put $a "M\.obsidian\only-m.json" "only in M"; Put $a "M\note.md" "M note"
    New-TestDir "$($a.Home)\F" | Out-Null # a vault never opened in Obsidian: no .obsidian yet
    $out = Groups $a @("create", "Settings", "$($a.Home)\W\.obsidian", "$($a.Home)\M\.obsidian", "$($a.Home)\F\.obsidian")
    Check "groups a group is created, with its file in the hub" { $out -match "2 folder" -and (Has $a "W\.obsidian\.unisono-group.json") }
    Check "groups ... which names the folders portably" { (Get-Content "$($a.Home)\W\.obsidian\.unisono-group.json" -Raw) -match '%USERPROFILE%\\\\M\\\\.obsidian' }
    $null = GSync $a; $null = GSync $a
    Check "groups a member's differing file takes the hub's version" { (Text $a "M\.obsidian\hotkeys.json") -eq "hub" }
    Check "groups ... and its own is kept among the previous versions" { @(Get-ChildItem "$($a.Data)\versions" -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.DirectoryName -like "*hotkeys.json" -and (Get-Content -LiteralPath $_.FullName -Raw).Trim() -eq "M's own" }).Count -ge 1 }
    Check "groups a missing member folder is created and filled" { (Text $a "F\.obsidian\plugins\p\main.js") -eq "plugin" }
    Check "groups a file only one member has reaches the hub and the others" { (Text $a "W\.obsidian\only-m.json") -eq "only in M" -and (Text $a "F\.obsidian\only-m.json") -eq "only in M" }
    Check "groups every folder holds the same group file" { $h = Text $a "W\.obsidian\.unisono-group.json"; (Text $a "M\.obsidian\.unisono-group.json") -eq $h -and (Text $a "F\.obsidian\.unisono-group.json") -eq $h }
    Check "groups only the hub goes to the cloud with its vault (the others only with their group file)" {
        (Test-Path "$cloud\W\.obsidian\hotkeys.json") -and (Test-Path "$cloud\M\note.md") -and
        (@(Get-ChildItem "$cloud\M\.obsidian" -Recurse -File | ForEach-Object Name) -join ",") -eq ".unisono-group.json" }
    Put $a "F\.obsidian\hotkeys.json" "changed in F"; $null = GSync $a; $null = GSync $a
    Check "groups a change in one member reaches the others" { (Text $a "M\.obsidian\hotkeys.json") -eq "changed in F" -and (Text $a "W\.obsidian\hotkeys.json") -eq "changed in F" }

    Section "groups: another computer finds the group in the hub and joins it"
    $b = GroupComputer "grp-B" $cloud
    Put $b "M\.obsidian\hotkeys.json" "B's own"
    $null = GSync $b
    $out = Groups $b @()
    Check "groups the other computer offers to join" { $out -match "can join: Settings" -and $out -match "1 of 2 folder" }
    $null = Groups $b @("join", "$($b.Home)\W\.obsidian")
    Check "groups ... joins it" { @(GroupsOf $b).Count -eq 1 }
    $null = GSync $b; $null = GSync $b
    Check "groups ... and its members take the hub's version" { (Text $b "M\.obsidian\hotkeys.json") -eq "changed in F" }
    Check "groups ... folders it does not have are left out" { -not (Has $b "F") }
    Put $b "M\.obsidian\hotkeys.json" "changed on B"; $null = GSync $b; $null = GSync $b
    $null = GSync $a; $null = GSync $a
    Check "groups a change on the other computer reaches every member here" { (Text $a "M\.obsidian\hotkeys.json") -eq "changed on B" -and (Text $a "F\.obsidian\hotkeys.json") -eq "changed on B" }

    Section "groups: the hub moves to another folder"
    $null = Groups $a @("hub", "Settings", "$($a.Home)\M\.obsidian")
    Check "groups the new hub takes the old one's place among the folders" { $g = @(GroupsOf $a)[0]; $g.Hub -like "*\M\.obsidian" -and @($g.Members | Where-Object { $_ -like "*\W\.obsidian" }).Count -eq 1 }
    $null = GSync $a; $null = GSync $a
    $noCopies = { param($c) @(Get-ChildItem "$($c.Home)\*\.obsidian" -Recurse -File | Where-Object Name -match "conflict").Count -eq 0 }
    Check "groups the new hub goes to the cloud, over the older copy there, without conflict copies" { (Get-Content "$cloud\M\.obsidian\hotkeys.json" -Raw).Trim() -eq "changed on B" -and (& $noCopies $a) }
    Put $a "W\.obsidian\hotkeys.json" "after the move"; $null = GSync $a; $null = GSync $a
    Check "groups the old hub, now one of the folders, is still kept in step" { (Text $a "F\.obsidian\hotkeys.json") -eq "after the move" -and (Get-Content "$cloud\M\.obsidian\hotkeys.json" -Raw).Trim() -eq "after the move" }
    $null = GSync $b; $null = Groups $b @(); $null = GSync $b; $null = GSync $b
    Check "groups the other computer follows the move" { (@(GroupsOf $b)[0].Hub) -like "*\M\.obsidian" }
    Check "groups ... and gets the changes, without conflict copies" { (Text $b "M\.obsidian\hotkeys.json") -eq "after the move" -and (Text $b "W\.obsidian\hotkeys.json") -eq "after the move" -and (& $noCopies $b) }

    Section "groups: changes to the group follow it to the other computer"
    $null = Groups $a @("members", "Settings", "$($a.Home)\W\.obsidian")
    $null = GSync $a; $null = GSync $b
    $out = Groups $b @()
    Check "groups a member taken out on one computer is taken out on the other" { $g = @(GroupsOf $b); $g.Count -eq 1 -and @($g[0].Members).Count -eq 1 }
    Check "groups ... and the folder it left keeps its files" { (Text $a "F\.obsidian\hotkeys.json") -eq "after the move" }
    $null = Groups $a @("remove", "Settings", "--everywhere")
    $null = GSync $a; $null = GSync $b
    $out = Groups $b @()
    Check "groups a group removed everywhere is removed on the other computer" { @(GroupsOf $b).Count -eq 0 -and $out -notmatch "can join" }
    # The folders are part of their vaults' pairs again; where the cloud has an older copy from before, both are kept.
    $kept = { param($c) @(Get-ChildItem "$($c.Home)\M\.obsidian" -Filter "hotkeys*.json" | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw).Trim() -eq "after the move" }).Count -ge 1 }
    Check "groups ... and nothing is lost" { (& $kept $a) -and (& $kept $b) }

}

# ------------------------------------------------------------------ UI
function Test-Ui {
    Section "ui: window renders"
    # A Drive pair on an account that is not signed in (it fails without touching any Drive) and a folder pair,
    # so both kinds of card are drawn.
    $c = Computer "ui" @{ Kind = "Drive"; RemotePath = "Unisono-tests-$stamp-ui"; Account = "nobody@example.invalid" }
    $cfg = Get-Content "$($c.Data)\config.json" -Raw | ConvertFrom-Json
    $cfg.Pairs += [pscustomobject]@{ Kind = "Folder"; RemotePath = (New-TestDir "$($c.Local)-other"); LocalPath = (New-TestDir "$($c.Local)-2") }
    # Build output in the folder pair, for the ignore suggestion card.
    New-Item -ItemType Directory -Force "$($c.Local)-2\proj\bin" | Out-Null
    Set-Content "$($c.Local)-2\proj\App.csproj" "<Project />"; Set-Content "$($c.Local)-2\proj\bin\App.dll" "binary"
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
        $js = "(() => { const b = document.querySelector('[data-sugpair]'); if (!b) return 'no card'; b.click(); const n = document.querySelectorAll('#sugList [data-sugpat]:checked').length; document.querySelector('#sugOk').click(); return String(n); })()"
        $msg = @{ id = 2; method = "Runtime.evaluate"; params = @{ expression = $js; returnByValue = $true } } | ConvertTo-Json -Depth 5 -Compress
        $ws.SendAsync([ArraySegment[byte]][Text.Encoding]::UTF8.GetBytes($msg), 'Text', $true, [Threading.CancellationToken]::None).Wait()
        $r = $ws.ReceiveAsync([ArraySegment[byte]]$buf, [Threading.CancellationToken]::None).Result
        $sug = ([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count) | ConvertFrom-Json).result.result.value
        Check "ui build output is suggested on the pair's card, ticked" { $sug -eq "1" }
        Start-Sleep 2
        Check "ui ... and ""Ignore checked"" writes it to .unisonoignore" { (Get-Content -LiteralPath "$($c.Local)-2\.unisonoignore" -Raw -ErrorAction SilentlyContinue) -match "(?m)^/proj/bin/\r?$" }
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
    try { Test-Core @{ Kind = "Drive"; RemotePath = $remote } "drive"; Test-WriteBack $remote; Test-TabWriteBack $remote }
    finally { if (-not $KeepFiles) { & $cli drive-trash $remote | Out-Null } }
}
if ($Suite -contains "writeback" -and $Suite -notcontains "drive") {
    $remote = "Unisono-tests-$stamp-wb"
    Use-Data (New-TestDir "$work\init-data")
    & $cli init $remote (New-TestDir "$work\init") --create | Out-Null
    try { Test-WriteBack $remote; Test-TabWriteBack $remote }
    finally { if (-not $KeepFiles) { Use-Data "$work\init-data"; & $cli drive-trash $remote | Out-Null } }
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
        Section "folder: files open in another program are not replaced"
        $o = Computer "folder-O" @{ Kind = "Folder"; RemotePath = $other }
        $null = Sync $o
        $held = [IO.File]::Open((Join-Path $o.Local "kept.txt"), "Open", "Read", "Read")
        Set-Content -LiteralPath "$other\kept.txt" "newer on the other side"; (Get-Item -LiteralPath "$other\kept.txt").LastWriteTime = (Get-Date).AddMinutes(-2)
        $out = Sync $o
        Check "folder a file open in another program is left alone" { ($out -join "`n") -match "open in another program" -and (Text $o "kept.txt") -ne "newer on the other side" }
        $held.Dispose(); $null = Sync $o
        Check "folder ... and updated once it is closed" { (Text $o "kept.txt") -eq "newer on the other side" }
        Section "folder: a running program gets its new version"
        $pr = Computer "folder-P" @{ Kind = "Folder"; RemotePath = $other }
        Set-Content -LiteralPath "$other\prog.exe" "version 1"; (Get-Item -LiteralPath "$other\prog.exe").LastWriteTime = (Get-Date).AddMinutes(-3)
        $null = Sync $pr
        # Windows holds a running .exe like this: others may read and rename it, but not write it.
        $run = [IO.File]::Open((Join-Path $pr.Local "prog.exe"), "Open", "Read", [IO.FileShare]"Read, Delete")
        Set-Content -LiteralPath "$other\prog.exe" "version 2"; (Get-Item -LiteralPath "$other\prog.exe").LastWriteTime = (Get-Date).AddMinutes(-2)
        $out = (Sync $pr) -join "`n"
        Check "folder a running program's new version is put in place" { $out -match "prog\.exe was running" -and (Text $pr "prog.exe") -eq "version 2" }
        Check "folder ... the running one is parked, not synced" { (Test-Path -LiteralPath (Join-Path $pr.Local "prog.exe.unisono-old")) -and -not (Test-Path -LiteralPath "$other\prog.exe.unisono-old") }
        $run.Dispose(); $null = Sync $pr
        Check "folder ... and deleted once nothing runs from it" { -not (Test-Path -LiteralPath (Join-Path $pr.Local "prog.exe.unisono-old")) }
        Remove-Item -LiteralPath "$other\prog.exe"
        Section "folder: previous versions in a folder of your choice"
        $v = Computer "folder-V" @{ Kind = "Folder"; RemotePath = $other }
        $vdir = New-TestDir "$work\my-versions"
        $cfg = Get-Content "$($v.Data)\config.json" -Raw | ConvertFrom-Json; $cfg.Options | Add-Member VersionsFolder $vdir -Force
        $cfg | ConvertTo-Json -Depth 6 | Set-Content "$($v.Data)\config.json" -Encoding UTF8
        $null = Sync $v
        Set-Content -LiteralPath "$other\kept.txt" "changed on the other side"; (Get-Item -LiteralPath "$other\kept.txt").LastWriteTime = (Get-Date).AddMinutes(-1)
        $null = Sync $v
        Check "folder a replaced file is kept in the chosen versions folder" { @(Get-ChildItem $vdir -Recurse -File).Count -ge 1 -and -not (Test-Path "$($v.Data)\versions") }
        Section "folder: a new computer follows the ignore file from its first sync"
        $io = New-TestDir "$work\ign-other"
        $ia = Computer "folder-IA" @{ Kind = "Folder"; RemotePath = $io }
        Put $ia "keep.txt" "synced"; Put $ia "cache\big.bin" "uploaded before it was ignored"
        $null = Sync $ia
        Put $ia ".unisonoignore" "cache/"; $null = Sync $ia
        $ib = Computer "folder-IB" @{ Kind = "Folder"; RemotePath = $io }
        Put $ib "cache\mine.bin" "the new computer's own cache"
        $null = Sync $ib
        Check "folder a new computer does not download what the ignore file leaves out" { (Has $ib "keep.txt") -and (Has $ib ".unisonoignore") -and -not (Has $ib "cache\big.bin") }
        Check "folder ... nor upload it" { -not (Test-Path -LiteralPath "$io\cache\mine.bin") }
        Section "folder: names Windows cannot open (made in WSL)"
        $wo = New-TestDir "$work\wsl-other"
        $w = Computer "folder-W" @{ Kind = "Folder"; RemotePath = $wo }
        [IO.File]::WriteAllText("\\?\$($w.Local)\junk.", "a log a Linux tool wrote")
        Put $w "fine.txt" "ok"
        $out1 = (Sync $w) -join "`n"; $out2 = (Sync $w) -join "`n"
        Check "folder a name ending with a dot is left out with a warning, not an error each cycle" { $out1 -match "ends with a dot" -and $out1 -notmatch "[1-9]\d* errors" -and $out2 -notmatch "[1-9]\d* errors" -and (Test-Path -LiteralPath "$wo\fine.txt") }
        [IO.File]::Delete("\\?\$($w.Local)\junk.")
        Section "folder: ignore suggestions"
        $s = Computer "folder-S" @{ Kind = "Folder"; RemotePath = (New-TestDir "$work\sug-other") }
        Put $s "proj\App.csproj" "<Project />"; Put $s "proj\bin\App.dll" "binary"; Put $s "proj\obj\cache.json" "{}"; Put $s "run.log" "log"
        Put $s "tool\Tool.csproj" "<Project />"; Put $s "tool\bin\Release\Tool.exe" "the only copy of a program"
        Put $s "repo\.gitignore" "out/`nsecret.json`n"; Put $s "repo\out\gen.txt" "generated"; Put $s "repo\secret.json" "{}"; Put $s "repo\.git\logs\HEAD" "history"
        $out = (Sync $s) -join "`n"
        Check "folder build output, .gitignore entries and logs are suggested" { $out -match "suggests ignoring: .*/proj/bin/" -and $out -match "/proj/obj/" -and $out -match "\*\.log" -and $out -match "/repo/\*\*/out/" }
        Check "folder ... but not the only copy of a program" { $out -notmatch "/tool/bin/" }
        Check "folder ... but not what might be wanted, nor anything in .git" { $line = ($out -split "`n" | Where-Object { $_ -match "suggests ignoring" }) -join " "; $line -and $line -notmatch "secret\.json" -and $line -notmatch "\.git" }
        Check "folder ... and nothing is ignored until the user chooses" { Test-Path -LiteralPath "$work\sug-other\proj\bin\App.dll" }
        Put $s "proj\bin\App2.dll" "more"
        Check "folder ... and each is told once" { ((Sync $s) -join "`n") -notmatch "suggests ignoring" }
        Test-Groups
    }
    finally { if (-not $KeepFiles -and $OtherFolder) { Wipe $other } }
}
if ($Suite -contains "groups" -and $Suite -notcontains "folder") { Test-Groups }
if ($Suite -contains "ondemand") {
    $other = "$work\od-other"; New-TestDir $other | Out-Null
    Test-OnDemand @{ Kind = "Folder"; RemotePath = $other }
    Section "ondemand: a pair with a very long remote path"
    # The pair's id holds the whole remote path; Windows refuses long sync root ids.
    $longOther = New-TestDir ("$work\" + ("long-remote-folder-name-" * 6))
    Set-Content -LiteralPath "$longOther\far.txt" "far away"
    $lp = Computer "od-long" @{ Kind = "Folder"; RemotePath = $longOther; OnDemand = $true }
    $out = (Sync $lp) -join "`n"
    try { Check "ondemand a long remote path still registers and syncs" { $out -notmatch "cycle failed" -and (Has $lp "far.txt") } }
    finally { Use-Data $lp.Data; & $cli ondemand-off 2>&1 | Out-Null }
}
if ($Suite -contains "ui") { Test-Ui }

if (-not $KeepFiles) { Wipe $work }
Write-Host ""
Write-Host ("{0} passed, {1} failed" -f $script:pass, $script:fail) -ForegroundColor $(if ($script:fail) { "Red" } else { "Green" })
$script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit $script:fail
