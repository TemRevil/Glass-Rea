# Runs inside Windows Sandbox, started by "Glass-Rea test.wsb" (scripts\build-store.ps1 -TestSign writes both into
# publish\store). Plays the move from setup's copy to the Store's on a clean Windows, then checks blocking, and writes
# what it found to sandbox-results.txt beside the package, with the logs.
#   1. setup's copy records (its service, run from classic\, as setup would register it)
#   2. the Store package installs; its service waits while the other copy is there
#   3. the other copy blocks curl, then is removed the way its uninstaller does it
#   4. the Store service takes over within two minutes, curl still blocked
#   5. blocks: back after a restart, gone while the service is stopped, undone by unblocking
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$results = Join-Path $here 'sandbox-results.txt'
$pfn = 'TemRevil.Glass-Rea_m8znxtvwyfwk8'
Set-Content $results "Glass-Rea in Windows Sandbox, $(Get-Date -Format 'yyyy-MM-dd HH:mm')"

function Say([string]$text) { Add-Content $results "$(Get-Date -Format HH:mm:ss) $text" }
function Check([string]$what, [scriptblock]$test) {
    try { if (& $test) { Say "PASS $what" } else { Say "FAIL $what" } }
    catch { Say "FAIL $what ($($_.Exception.Message))" }
}
function WaitFor([int]$seconds, [scriptblock]$test) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) { try { if (& $test) { return $true } } catch { }; Start-Sleep 3 }
    return $false
}
# One request to the recording service over its pipe, as the app sends them.
function Call([hashtable]$request) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'GlassRea', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
        $writer = New-Object System.IO.StreamWriter($pipe); $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($pipe)
        $writer.WriteLine(($request | ConvertTo-Json -Compress))
        $response = $reader.ReadLine() | ConvertFrom-Json
        if (-not $response.ok) { throw $response.error }
        return $response.data
    }
    finally { $pipe.Dispose() }
}
function Reaches { & curl.exe -s -o NUL -m 10 https://www.microsoft.com; return $LASTEXITCODE -eq 0 }
function StoreServicePid { (Get-CimInstance Win32_Service -Filter "Name='GlassReaStore'").ProcessId }

try {
    # 1. Setup's copy.
    $classic = 'C:\GlassRea-classic'
    Copy-Item (Join-Path $here 'classic') $classic -Recurse -Force
    & sc.exe create GlassRea binPath= "$classic\GlassRea.Service.exe" start= auto | Out-Null
    & sc.exe sidtype GlassRea unrestricted | Out-Null
    Start-Service GlassRea
    Check 'setup''s copy records' { WaitFor 90 { (Call @{ op = 'status' }).capturing } }

    # 2. The Store package.
    Import-Certificate (Join-Path $here 'Glass-Rea test.cer') -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    $local = Join-Path $env:TEMP 'store'
    New-Item -ItemType Directory -Force $local | Out-Null
    Copy-Item (Join-Path $here '*.msix') $local
    Add-AppxPackage (Get-ChildItem $local -Filter *.msix | Select-Object -First 1).FullName
    Say "Installed $((Get-AppxPackage TemRevil.Glass-Rea).PackageFullName)"
    Check 'the Store service runs' { WaitFor 60 { (Get-Service GlassReaStore).Status -eq 'Running' } }
    Check 'Windows restarts the Store service on failure' { (& sc.exe qfailure GlassReaStore) -match 'RESTART' }
    Check 'the Store service waits while setup''s copy is here' {
        WaitFor 30 { Select-String -Path 'C:\ProgramData\Glass-Rea\Logs\service.log' -Pattern 'this copy waits until it' -Quiet } }

    # 3. Setup's copy blocks curl, then goes as its uninstaller does: stop, remove its blocks, remove the service.
    $null = Reaches
    $curl = $null
    $null = WaitFor 180 {
        $null = Reaches
        $script:curl = (Call @{ op = 'usage'; from = 0 }).apps | Where-Object { $_.path -like '*\curl.exe' } | Select-Object -First 1
        $script:curl
    }
    if (-not $curl) { throw 'curl never showed up in the recorded apps.' }
    Check 'curl reaches the internet' { Reaches }
    Call @{ op = 'block'; appId = $curl.appId; value = $true } | Out-Null
    Check 'setup''s copy blocks curl' { -not (Reaches) }
    Say 'Waiting for the Store service to note the block'
    Start-Sleep 75
    Stop-Service GlassRea
    $policy = New-Object -ComObject HNetCfg.FwPolicy2
    @($policy.Rules | Where-Object { $_.Grouping -eq 'Glass-Rea' }) | ForEach-Object { $policy.Rules.Remove($_.Name) }
    & sc.exe delete GlassRea | Out-Null
    Remove-Item $classic -Recurse -Force
    Say 'Removed setup''s copy'

    # 4. The take-over.
    $before = StoreServicePid
    Check 'the Store service takes over' { WaitFor 150 { (StoreServicePid) -ne $before -and (Call @{ op = 'status' }).capturing } }
    Check 'the block carried over' { -not (Reaches) }
    Check 'the block shows as blocked' { ((Call @{ op = 'usage'; from = 0 }).apps | Where-Object { $_.appId -eq $curl.appId }).blocked }

    # 5. Blocks held by the service.
    Restart-Service GlassReaStore
    $null = WaitFor 60 { (Call @{ op = 'status' }).version }
    Check 'the block is back after a restart' { -not (Reaches) }
    Stop-Service GlassReaStore
    Check 'a stopped service leaves nothing blocked' { Reaches }
    Start-Service GlassReaStore
    $null = WaitFor 60 { (Call @{ op = 'status' }).version }
    Call @{ op = 'block'; appId = $curl.appId; value = $false } | Out-Null
    Check 'unblocking lets curl through' { Reaches }

    # The app.
    Start-Process "shell:AppsFolder\$pfn!App"
    Check 'the app opens' { WaitFor 30 { Get-Process GlassRea -ErrorAction SilentlyContinue } }
    Start-Sleep 10
}
catch { Say "STOPPED: $($_.Exception.Message)" }
finally {
    Copy-Item 'C:\ProgramData\Glass-Rea\Logs\service.log' (Join-Path $here 'sandbox-service.log') -ErrorAction SilentlyContinue
    Get-ChildItem "$env:LOCALAPPDATA\Packages\$pfn" -Recurse -Filter 'app*.log' -ErrorAction SilentlyContinue |
        Select-Object -First 1 | Copy-Item -Destination (Join-Path $here 'sandbox-app.log')
    Say 'Done'
}
