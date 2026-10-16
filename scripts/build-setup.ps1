# Builds "publish\Glass-Rea Setup.exe": publishes the service and the app, packs them inside the installer,
# and makes the installer a single file that runs on any 64-bit Windows 10 (19041+) or 11 PC.
# Run from the repo:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-setup.ps1
$ErrorActionPreference = 'Stop'

$root    = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'publish'
$stage   = Join-Path $publish 'stage'
$zip     = Join-Path $publish 'payload.zip'
$setupOut = Join-Path $publish 'setup'

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

# Framework-dependent: both run on the shared .NET 10 runtime, which setup installs if it's missing.
foreach ($p in @(
    @{ Project = 'src\GlassRea.Service\GlassRea.Service.csproj'; Out = 'Service' },
    @{ Project = 'src\GlassRea.App\GlassRea.App.csproj';         Out = 'App' })) {
    Write-Host "Building $($p.Out)..."
    & dotnet publish (Join-Path $root $p.Project) -c Release -r win-x64 --self-contained false -o (Join-Path $stage $p.Out) --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build of $($p.Out) failed." }
}

# The app can't start without its compiled XAML; never pack a build that lost it.
if (-not (Test-Path (Join-Path $stage 'App\GlassRea.pri'))) { throw 'The app build has no GlassRea.pri; the installed app would crash on start.' }

# Signed as Tem Revil when scripts\make-cert.ps1 has made the certificate; unsigned otherwise.
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq 'CN=Tem Revil' -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
function Sign([string[]]$files) {
    if (-not $cert) { return }
    foreach ($f in $files) {
        $r = Set-AuthenticodeSignature -FilePath $f -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' -ErrorAction SilentlyContinue
        # Offline: sign without the timestamp (the signature then lasts as long as the certificate).
        if (-not $r -or $r.Status -eq 'UnknownError') { $r = Set-AuthenticodeSignature -FilePath $f -Certificate $cert -HashAlgorithm SHA256 }
        if ($r.Status -notin 'Valid', 'UnknownError') { throw "Signing $f failed: $($r.StatusMessage)" }
    }
}
if ($cert) { Write-Host 'Signing as Tem Revil...' } else { Write-Host 'No Tem Revil certificate (scripts\make-cert.ps1); building unsigned.' }
Sign @(
    (Join-Path $stage 'App\GlassRea.exe'), (Join-Path $stage 'App\GlassRea.dll'), (Join-Path $stage 'App\GlassRea.Core.dll'),
    (Join-Path $stage 'Service\GlassRea.Service.dll'), (Join-Path $stage 'Service\GlassRea.Core.dll'))

Write-Host 'Packing...'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host 'Building setup...'
& dotnet publish (Join-Path $root 'src\GlassRea.Setup\GlassRea.Setup.csproj') -c Release -r win-x64 `
    -p:PublishSingleFile=true "-p:PayloadZip=$zip" -o $setupOut --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build of setup failed.' }

$exe = Join-Path $publish 'Glass-Rea Setup.exe'
Move-Item (Join-Path $setupOut 'GlassRea.Setup.exe') $exe
Sign @($exe)
Remove-Item $stage, $zip, $setupOut -Recurse -Force
Write-Host ("Done: {0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
