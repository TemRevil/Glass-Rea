# Attaches release.json to GitHub release <Tag>: the release's tag, notes and setup (name, size, GitHub's SHA-256),
# in the same shape as GitHub's API. The app reads it from the latest release's downloads, which GitHub doesn't
# limit the way it limits API requests (see Releases.ManifestUrl). release.ps1 and the release workflow run it
# after creating a release; run it by hand to add one to an existing release:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\write-manifest.ps1 -Tag v1.2.0
param([Parameter(Mandatory)][string]$Tag)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
# Parsed here rather than with --jq: Windows PowerShell drops the quotes inside a jq filter.
$release = gh api "repos/{owner}/{repo}/releases/tags/$Tag" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $release) { throw "GitHub has no release $Tag." }
$setup = @($release.assets | Where-Object { $_.name -like '*Setup.exe' })[0]
if (-not $setup) { throw "Release $Tag has no setup attached." }
if ($setup.digest -notmatch '^sha256:[0-9a-f]{64}$') { throw "GitHub hasn't recorded a SHA-256 for $($setup.name) yet." }

$published = if ($release.published_at -is [datetime]) { $release.published_at.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') } else { [string]$release.published_at }
$manifest = [ordered]@{
    tag_name     = $release.tag_name
    name         = $release.name
    draft        = [bool]$release.draft
    prerelease   = [bool]$release.prerelease
    published_at = $published
    html_url     = $release.html_url
    body         = [string]$release.body
    assets       = @([ordered]@{
        name                 = $setup.name
        size                 = [long]$setup.size
        digest               = $setup.digest
        browser_download_url = $setup.browser_download_url
    })
}
$publish = Join-Path $root 'publish'
New-Item -ItemType Directory -Force $publish | Out-Null
$path = Join-Path $publish 'release.json'
[IO.File]::WriteAllText($path, (ConvertTo-Json $manifest -Depth 5), (New-Object Text.UTF8Encoding $false))
gh release upload $Tag $path --clobber
if ($LASTEXITCODE -ne 0) { throw "Couldn't attach release.json to $Tag." }
Write-Host "Attached release.json to $Tag ($($setup.name), $($setup.digest))"
