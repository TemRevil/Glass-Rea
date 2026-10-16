# One time, on this PC: makes the "Tem Revil" code-signing certificate that build-setup.ps1 signs with, and tells
# this PC to trust it, so Windows shows "Verified publisher: Tem Revil" when setup asks for admin.
# Other PCs don't trust it (that needs a paid certificate), and Smart App Control ignores it either way.
# The private key stays in your user certificate store and can't be exported. Asks for admin once, to trust it.
param([switch]$Elevated, [string]$Thumbprint)
$ErrorActionPreference = 'Stop'
$Subject = 'CN=Tem Revil'

if ($Elevated) {
    # Admin half: trust the public certificate (no private key) as a root and as a trusted publisher.
    $cer = Join-Path $env:TEMP "tem-revil-$Thumbprint.cer"
    foreach ($store in 'Root', 'TrustedPublisher') {
        Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
    }
    Remove-Item $cer
    exit 0
}

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -KeyAlgorithm RSA -KeyLength 3072 `
        -KeyExportPolicy NonExportable -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
    Write-Host "Made the certificate ($($cert.Thumbprint))."
}

$trusted = Test-Path "Cert:\LocalMachine\TrustedPublisher\$($cert.Thumbprint)"
if ($trusted) { Write-Host 'This PC already trusts it.'; exit 0 }

Export-Certificate -Cert $cert -FilePath (Join-Path $env:TEMP "tem-revil-$($cert.Thumbprint).cer") | Out-Null
Write-Host 'Asking for admin to trust it on this PC...'
$p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated', '-Thumbprint', $cert.Thumbprint)
if ($p.ExitCode -ne 0) { throw "Trusting the certificate failed (exit $($p.ExitCode))." }
Write-Host 'Done. Run build-setup.ps1 and setup will be signed by Tem Revil.'
