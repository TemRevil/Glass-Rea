# Runs inside Windows Sandbox, started by "Glass-Rea fresh.wsb" (scripts\build-store.ps1 -TestSign writes both into
# publish\store). Plays what Store certification does: the package installs on a clean Windows and the app is opened
# from Start. Writes what it saw to fresh-results.txt beside the package: the service's state, whether the window still
# shows its loader or the offline bar, screenshots, and the logs.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$results = Join-Path $here 'fresh-results.txt'
$pfn = 'TemRevil.Glass-Rea_m8znxtvwyfwk8'
Set-Content $results "Glass-Rea fresh install in Windows Sandbox, $(Get-Date -Format 'yyyy-MM-dd HH:mm')"

function Say([string]$text) {
    $line = "$(Get-Date -Format HH:mm:ss) $text"
    for ($i = 0; $i -lt 20; $i++) { try { Add-Content $results $line -ErrorAction Stop; return } catch { Start-Sleep -Milliseconds 250 } }
}

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
function Shot([string]$name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save((Join-Path $here "fresh-$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bitmap.Dispose()
}
# What the window shows: its loader and its bars, by their automation names.
function Look([string]$when) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Glass-Rea')
    $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $byName)
    if (-not $window) { Say "$when - no Glass-Rea window"; return }
    $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $shown = foreach ($e in $all) {
        try { if (-not $e.Current.IsOffscreen -and $e.Current.Name) { "$($e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''):$($e.Current.Name)" } } catch { }
    }
    $loading = $shown | Where-Object { $_ -like '*Loading the page*' }
    Say "$when - loader $(if ($loading) { 'SHOWN' } else { 'gone' }); visible: $(($shown | Select-Object -Unique -First 40) -join ' | ')"
}

try {
    Import-Certificate (Join-Path $here 'Glass-Rea test.cer') -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    $local = Join-Path $env:TEMP 'store'
    New-Item -ItemType Directory -Force $local | Out-Null
    Copy-Item (Join-Path $here '*.msix') $local
    Add-AppxPackage (Get-ChildItem $local -Filter *.msix | Select-Object -First 1).FullName
    Say "Installed $((Get-AppxPackage TemRevil.Glass-Rea).PackageFullName) on Windows $([Environment]::OSVersion.Version)"
    foreach ($s in 0, 5, 15) {
        Start-Sleep $s
        $svc = Get-Service GlassReaStore -ErrorAction SilentlyContinue
        Say "Service after +$s s: $(if ($svc) { "$($svc.Status), $($svc.StartType)" } else { 'not there' })"
    }

    Start-Process "shell:AppsFolder\$pfn!App"
    foreach ($at in 5, 15, 45, 90) {
        Start-Sleep ($at - $(if ($at -eq 5) { 0 } else { $last }))
        $last = $at
        Shot "$at"
        Look "+$at s"
    }
    $p = Get-Process GlassRea, GlassRea.Service -ErrorAction SilentlyContinue | ForEach-Object { "$($_.ProcessName) $($_.Id)" }
    Say "Processes: $($p -join ', ')"

    # Again as a person without administrator rights: the Sandbox signs in as an administrator with UAC off, so the app
    # above ran elevated, which the service always lets through. A Basic User token has Administrators as deny-only.
    Get-Process GlassRea -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 2
    $exe = Join-Path (Get-AppxPackage TemRevil.Glass-Rea).InstallLocation 'GlassRea.exe'
    & runas.exe /trustlevel:0x20000 "`"$exe`""
    Say 'Opened again as a basic user'
    foreach ($at in 5, 15, 45, 90) {
        Start-Sleep ($at - $(if ($at -eq 5) { 0 } else { $last }))
        $last = $at
        Shot "user-$at"
        Look "user +$at s"
    }
    Say "Service log, last lines: $((Get-Content 'C:\ProgramData\Glass-Rea\Logs\service.log' -Tail 5) -join ' / ')"
}
catch { Say "STOPPED: $($_.Exception.Message)" }
finally {
    Copy-Item 'C:\ProgramData\Glass-Rea\Logs\service.log' (Join-Path $here 'fresh-service.log') -ErrorAction SilentlyContinue
    Get-ChildItem "$env:LOCALAPPDATA\Packages\$pfn", "$env:LOCALAPPDATA\Glass-Rea" -Recurse -Filter 'app*.log' -ErrorAction SilentlyContinue |
        Select-Object -First 1 | Copy-Item -Destination (Join-Path $here 'fresh-app.log')
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 1, 2 } -MaxEvents 30 -ErrorAction SilentlyContinue |
        ForEach-Object { "$($_.TimeCreated) $($_.ProviderName): $($_.Message)" } | Set-Content (Join-Path $here 'fresh-events.txt')
    Say 'Done'
}
