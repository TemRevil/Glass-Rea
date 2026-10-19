# Builds "publish\store\Glass-Rea_<version>_x64.msix", the Microsoft Store package: the app and the recording service,
# both self-contained (the Store has no .NET to install), with the identity Partner Center gave the reserved name.
# Upload it unsigned: the Store signs it. Run from the repo:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-store.ps1
#
# -TestSign also signs it with a test certificate for the Store's publisher name, made once in your user certificate
# store, and puts beside the package what testing it in Windows Sandbox takes: the certificate's public half, the
# service as setup's copy runs it (classic\), scripts\sandbox\test-store.ps1, and "Glass-Rea test.wsb", which opens a
# Sandbox that runs that test. Its results land in publish\store\sandbox-results.txt.
param([switch]$TestSign)
$ErrorActionPreference = 'Stop'

$root      = Split-Path $PSScriptRoot -Parent
$out       = Join-Path $root 'publish\store'
$work      = Join-Path $root 'obj\store'
$publisher = 'CN=B99259A6-8006-4D29-8939-B5661113C076'
$version   = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$version4  = "$version.0" # the Store wants four parts, the last one 0

# Only this folder: publish\ also holds the setups build-setup.ps1 makes.
foreach ($dir in $out, $work) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
New-Item -ItemType Directory -Force $out, $work | Out-Null

Write-Host 'Building the service...'
# As GlassRea.Service.exe: the copy setup installs runs "dotnet.exe GlassRea.Service.dll" for Smart App Control, but
# the Store's signature covers every file in its package.
& dotnet publish (Join-Path $root 'src\GlassRea.Service\GlassRea.Service.csproj') -c Release -r win-x64 --self-contained true `
    -p:UseAppHost=true -o (Join-Path $work 'Service') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build of the service failed.' }

$manifest = Join-Path $work 'Package.appxmanifest'
$text = Get-Content (Join-Path $root 'src\GlassRea.App\Store\Package.appxmanifest') -Raw
[IO.File]::WriteAllText($manifest, $text.Replace('Version="0.0.0.0"', "Version=`"$version4`""))

Write-Host 'Building the app and the package...'
& dotnet publish (Join-Path $root 'src\GlassRea.App\GlassRea.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:StorePackage=true "-p:StoreManifest=$manifest" "-p:StoreService=$(Join-Path $work 'Service')" "-p:StoreOut=$(Join-Path $work 'package')" `
    -o (Join-Path $work 'app') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build of the package failed.' }

$built = Get-ChildItem (Join-Path $work 'package') -Recurse -Filter '*.msix' | Select-Object -First 1
if (-not $built) { throw 'The build made no .msix.' }
$msix = Join-Path $out "Glass-Rea_$($version4)_x64.msix"
Move-Item $built.FullName $msix

if ($TestSign) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher -FriendlyName 'Glass-Rea test packages' -KeyAlgorithm RSA -KeyLength 3072 `
            -KeyExportPolicy NonExportable -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(2) `
            -TextExtension @('2.5.29.19={text}')
        Write-Host "Made the test certificate ($($cert.Thumbprint))."
    }
    $signtool = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') -Recurse -Filter signtool.exe |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found; build the app once so NuGet brings the Windows SDK build tools.' }
    & $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint /q $msix
    if ($LASTEXITCODE -ne 0) { throw 'Signing the package failed.' }
    Export-Certificate -Cert $cert -FilePath (Join-Path $out 'Glass-Rea test.cer') | Out-Null

    Write-Host 'Building setup''s copy of the service for the Sandbox...'
    & dotnet publish (Join-Path $root 'src\GlassRea.Service\GlassRea.Service.csproj') -c Release -r win-x64 --self-contained true `
        -p:UseAppHost=true -o (Join-Path $out 'classic') --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build of the service failed.' }
    Copy-Item (Join-Path $PSScriptRoot 'sandbox\test-store.ps1') $out
    $wsb = @"
<Configuration>
  <MappedFolders>
    <MappedFolder><HostFolder>$out</HostFolder><SandboxFolder>C:\Store</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Store\test-store.ps1</Command></LogonCommand>
</Configuration>
"@
    [IO.File]::WriteAllText((Join-Path $out 'Glass-Rea test.wsb'), $wsb)
    Write-Host 'Test-signed. Open "Glass-Rea test.wsb" to test it in Windows Sandbox.'
}

Remove-Item $work -Recurse -Force
Write-Host ("Done: {0} ({1:N0} MB)" -f $msix, ((Get-Item $msix).Length / 1MB))
