# Probe-Environment.ps1
# Checks that this machine has what Unisono needs to sync Google Drive
# without admin rights. Writes a report next to the script, named after
# the computer, so the result syncs back via OneDrive.
#
# Usage (from a normal PowerShell prompt, no elevation):
#   powershell -ExecutionPolicy Bypass -File .\Probe-Environment.ps1

$ErrorActionPreference = 'Continue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$lines = New-Object System.Collections.Generic.List[string]
function Out-Line([string]$text) { $lines.Add($text); Write-Host $text }
function Out-Result([string]$name, [bool]$ok, [string]$detail) {
    $mark = if ($ok) { 'OK  ' } else { 'FAIL' }
    Out-Line ("[{0}] {1,-34} {2}" -f $mark, $name, $detail)
}

Out-Line "Unisono environment probe"
Out-Line ("Computer : {0}   User: {1}\{2}" -f $env:COMPUTERNAME, $env:USERDOMAIN, $env:USERNAME)
Out-Line ("OS       : {0}" -f (Get-CimInstance Win32_OperatingSystem).Caption + ' ' + [Environment]::OSVersion.Version)
Out-Line ("Time     : {0:yyyy-MM-dd HH:mm:ss}" -f (Get-Date))
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Out-Line ("Admin    : {0}" -f $isAdmin)
Out-Line ""

# ---------------------------------------------------------------- .NET
Out-Line "== .NET =="
# Same lookup order as the other projects' build.ps1: user-local SDK first, then machine-wide.
$localDotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
$machineDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$sdkHost = $null; $sdks = @()
foreach ($cand in @($localDotnet, $machineDotnet)) {
    if ($cand -and (Test-Path $cand)) {
        $list = @(& $cand --list-sdks 2>$null | Where-Object { $_ -match '^8\.' })
        if ($list.Count -gt 0) { $sdkHost = $cand; $sdks = $list; break }
    }
}
Out-Result '.NET 8 SDK (build machine only)' ($sdkHost -ne $null) $(if ($sdkHost) { "{0} via {1}" -f (($sdks | ForEach-Object { $_.Split(' ')[0] }) -join ', '), $sdkHost } else { 'none in LocalAppData\dotnet or on PATH' })
if ($machineDotnet) {
    $rts = & $machineDotnet --list-runtimes 2>$null
    $desk8 = @($rts | Where-Object { $_ -match 'WindowsDesktop.App 8\.' })
    Out-Result '.NET 8 WindowsDesktop runtime' ($desk8.Count -gt 0) (($rts | ForEach-Object { ($_ -split ' ')[0..1] -join ' ' } | Select-Object -Unique) -join '; ')
} else {
    Out-Result '.NET 8 WindowsDesktop runtime' $false 'dotnet not on PATH'
}
Out-Line ""

# ---------------------------------------------------------- AppLocker
Out-Line "== AppLocker / program folder =="
$programDir = Join-Path $env:USERPROFILE 'Program'
Out-Result 'Program folder exists' (Test-Path $programDir) $programDir
try {
    $unisonoDir = Join-Path $programDir 'Unisono'
    New-Item -ItemType Directory -Force -Path $unisonoDir | Out-Null
    $probeFile = Join-Path $unisonoDir 'probe.tmp'
    'x' | Set-Content $probeFile
    Remove-Item $probeFile -Force
    Out-Result 'Program\Unisono writable' $true $unisonoDir
} catch { Out-Result 'Program\Unisono writable' $false $_.Exception.Message }

$policy = $null
try { $policy = Get-AppLockerPolicy -Effective -ErrorAction Stop } catch {}
if ($policy) {
    $exeRules = @()
    foreach ($coll in ($policy.RuleCollections | Where-Object { $_.RuleCollectionType -eq 'Exe' })) { $exeRules += @($coll) }
    Out-Result 'AppLocker policy readable' $true ("{0} exe rule(s)" -f $exeRules.Count)
    # Test-AppLockerPolicy chokes on this policy ('*' version pattern), so look for the
    # path rule that covers %USERPROFILE%\Program directly instead.
    $allowProgram = @($exeRules | Where-Object {
        $_.Action -eq 'Allow' -and ($_.PathConditions | Where-Object { $_.Path -match '(?i)^%OSDRIVE%\\Users\\\*\\Program\\\*$' })
    })
    $denyProgram = @($exeRules | Where-Object {
        $_.Action -eq 'Deny' -and ($_.PathConditions | Where-Object { $_.Path -match '(?i)Users\\\*\\Program' })
    })
    $ok = ($allowProgram.Count -gt 0) -and ($denyProgram.Count -eq 0)
    $names = ($allowProgram | ForEach-Object { $_.Name }) -join '; '
    Out-Result 'Unsigned exe in Users\*\Program\*' $ok $(if ($ok) { "allowed by rule '$names'" } else { 'no matching Allow path rule (or a Deny rule) found' })
} else {
    Out-Result 'AppLocker policy readable' $false 'Get-AppLockerPolicy failed or no policy'
}
Out-Line ""

# ------------------------------------------------------------ Network
Out-Line "== Network to Google =="
try {
    $proxy = [System.Net.WebRequest]::DefaultWebProxy
    $p = $proxy.GetProxy('https://www.googleapis.com/')
    $viaProxy = ($p.AbsoluteUri -ne 'https://www.googleapis.com/')
    Out-Line ("Proxy for googleapis.com: {0}" -f $(if ($viaProxy) { $p.AbsoluteUri } else { 'direct' }))
} catch { Out-Line ("Proxy lookup failed: {0}" -f $_.Exception.Message) }

$urls = @(
    @{ Name = 'Drive API discovery';   Url = 'https://www.googleapis.com/discovery/v1/apis/drive/v3/rest'; Accept = @(200) },
    @{ Name = 'OAuth token endpoint';  Url = 'https://oauth2.googleapis.com/tokeninfo';                    Accept = @(200, 400) },
    @{ Name = 'Google accounts';       Url = 'https://accounts.google.com/';                               Accept = @(200, 302) },
    @{ Name = 'Drive upload endpoint'; Url = 'https://www.googleapis.com/upload/drive/v3/files';            Accept = @(200, 401, 403, 404, 405) },
    @{ Name = 'Cloud Console';         Url = 'https://console.cloud.google.com/';                          Accept = @(200, 302) }
)
foreach ($u in $urls) {
    $code = $null; $detail = ''
    try {
        # HttpWebRequest rather than Invoke-WebRequest: PS 5.1 throws a misleading
        # "operation is not valid" on redirects when MaximumRedirection is 0.
        $req = [System.Net.HttpWebRequest]::Create($u.Url)
        $req.Method = 'GET'; $req.Timeout = 20000; $req.AllowAutoRedirect = $false
        $req.UserAgent = 'Unisono-probe'
        $resp = $req.GetResponse()
        $code = [int]$resp.StatusCode
        $resp.Close()
    } catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if ($resp) { $code = [int]$resp.StatusCode; $resp.Close() } else { $detail = $_.Exception.Message }
    } catch { $detail = $_.Exception.Message }
    $ok = ($code -ne $null) -and ($u.Accept -contains $code)
    Out-Result $u.Name $ok ("HTTP {0} {1}" -f $code, $detail)
}
Out-Line ""

# ------------------------------------------------ Loopback for OAuth
Out-Line "== Loopback listener (OAuth redirect) =="
try {
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add('http://127.0.0.1:53682/')
    $l.Start(); $l.Stop()
    Out-Result 'HttpListener 127.0.0.1:53682' $true 'http.sys allows loopback prefix for this user'
} catch { Out-Result 'HttpListener 127.0.0.1:53682' $false $_.Exception.Message }
try {
    $t = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback, 0)
    $t.Start(); $port = $t.LocalEndpoint.Port; $t.Stop()
    Out-Result 'TcpListener loopback' $true ("bound port {0}" -f $port)
} catch { Out-Result 'TcpListener loopback' $false $_.Exception.Message }
Out-Line ""

# ------------------------------------------------------- Autostart
Out-Line "== Autostart / state =="
try {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-ItemProperty -Path $runKey -Name 'UnisonoProbe' -Value 'probe' -PropertyType String -Force | Out-Null
    Remove-ItemProperty -Path $runKey -Name 'UnisonoProbe' -Force
    Out-Result 'HKCU Run key writable' $true $runKey
} catch { Out-Result 'HKCU Run key writable' $false $_.Exception.Message }
try {
    $stateDir = Join-Path $env:LOCALAPPDATA 'Unisono'
    New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
    Out-Result 'LocalAppData\Unisono writable' $true $stateDir
} catch { Out-Result 'LocalAppData\Unisono writable' $false $_.Exception.Message }
try {
    $lp = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -ErrorAction Stop
    Out-Line ("LongPathsEnabled: {0}" -f $lp.LongPathsEnabled)
} catch { Out-Line "LongPathsEnabled: unknown" }
Out-Line ""

# --------------------------------------------------------- Report
$fails = @($lines | Where-Object { $_ -like '`[FAIL`]*' })
Out-Line ("Summary: {0} FAIL" -f $fails.Count)
$report = Join-Path $PSScriptRoot ("probe-{0}.txt" -f $env:COMPUTERNAME)
$lines | Set-Content -Path $report -Encoding UTF8
Write-Host ""
Write-Host "Report written to $report"
