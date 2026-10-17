# Publishes a GitHub release from this PC: builds "publish\Glass-Rea Setup.exe" signed with the Tem Revil
# certificate, then creates release v<Version> (the <Version> in Directory.Build.props) with that version's
# CHANGELOG.md section as its notes and the setup attached. The app's update check (Settings > About) finds it there.
# Releases are normally made by .github/workflows/release.yml and signed by SignPath Foundation, which Smart App
# Control trusts; this is the fallback, and only PCs that trust the Tem Revil certificate run its setup unwarned.
# Run from the repo, on main, with everything committed and pushed:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$tag = "v$version"

if (git status --porcelain) { throw 'Commit everything first: the release is built from what is committed.' }
if ((git rev-parse --abbrev-ref HEAD) -ne 'main') { throw 'Release from main.' }
git fetch -q origin main
if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { throw 'Push main first, so the release points at what was built.' }
$existing = gh release list --json tagName --jq '.[].tagName'
if ($existing -contains $tag) { throw "$tag is already released. Raise <Version> in Directory.Build.props for the next one." }

# The notes are this version's section of the changelog, without its heading.
$lines = Get-Content (Join-Path $root 'CHANGELOG.md')
$start = [Array]::IndexOf($lines, "## $version")
if ($start -lt 0) { throw "CHANGELOG.md has no '## $version' section." }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i].StartsWith('## ')) { $end = $i; break } }
$notes = ($lines[($start + 1)..($end - 1)] -join "`n").Trim()
if (-not $notes) { throw "The '## $version' section of CHANGELOG.md is empty." }

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-setup.ps1')
if ($LASTEXITCODE -ne 0) { throw 'The setup build failed.' }
$exe = Join-Path $root 'publish\Glass-Rea Setup.exe'

# The app installs an update only if it's signed with this exact certificate.
$pinned = [regex]::Match((Get-Content (Join-Path $root 'src\GlassRea.Core\Updates\Releases.cs') -Raw), 'SignerThumbprint = "([0-9A-F]{40})"').Groups[1].Value
$signer = (Get-AuthenticodeSignature $exe).SignerCertificate
if (-not $signer -or $signer.Thumbprint -ne $pinned) { throw "The setup isn't signed with the Tem Revil certificate ($pinned); the app would refuse it as an update." }

$notesFile = Join-Path $root 'publish\release-notes.md'
[IO.File]::WriteAllText($notesFile, $notes, (New-Object Text.UTF8Encoding $false)) # no BOM: it would start the notes on GitHub
gh release create $tag $exe --title "Glass-Rea $version" --notes-file $notesFile --target main
if ($LASTEXITCODE -ne 0) { throw 'GitHub refused the release.' }

# What GitHub recorded must be byte for byte what was built and signed here.
# (Parsed here rather than with --jq: Windows PowerShell drops the quotes inside a jq filter.)
$asset = (gh api "repos/{owner}/{repo}/releases/tags/$tag" | ConvertFrom-Json).assets | Where-Object { $_.name -like '*Setup.exe' } | Select-Object -First 1
$digest = $asset.digest
$local = 'sha256:' + (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($digest -ne $local) { throw "GitHub recorded '$digest' for the setup, but the file here is $local." }
Write-Host "Released $tag with $([math]::Round((Get-Item $exe).Length / 1MB)) MB setup, $digest"

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'write-manifest.ps1') -Tag $tag
if ($LASTEXITCODE -ne 0) { throw 'Attaching release.json failed; run scripts\write-manifest.ps1 -Tag ' + $tag + ' again.' }
