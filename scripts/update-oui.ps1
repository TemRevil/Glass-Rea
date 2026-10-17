# Refreshes the MAC address vendor table the device scanner uses to say who made a device.
# Downloads the IEEE registries (MA-L 24-bit, MA-M 28-bit, MA-S 36-bit prefixes) and writes
# src\GlassRea.Service\Devices\oui.tsv.gz: one "PREFIX<TAB>Organization" line per assignment, prefix in hex.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar makes downloads crawl in Windows PowerShell
Add-Type -AssemblyName Microsoft.VisualBasic
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'src\GlassRea.Service\Devices\oui.tsv.gz'
$sources = @(
    'https://standards-oui.ieee.org/oui/oui.csv',
    'https://standards-oui.ieee.org/oui28/mam.csv',
    'https://standards-oui.ieee.org/oui36/oui36.csv'
)
# The IEEE site refuses requests that don't look like a browser.
$headers = @{ 'User-Agent' = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36'; 'Accept' = 'text/csv,*/*' }
$lines = [System.Collections.Generic.List[string]]::new()
foreach ($url in $sources) {
    $content = (Invoke-WebRequest -Uri $url -Headers $headers -UseBasicParsing).Content
    $text = if ($content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($content) } else { $content }
    $parser = [Microsoft.VisualBasic.FileIO.TextFieldParser]::new([IO.StringReader]::new($text))
    $parser.SetDelimiters(',')
    $parser.HasFieldsEnclosedInQuotes = $true
    $null = $parser.ReadFields() # header: Registry, Assignment, Organization Name, Organization Address
    $count = 0
    while (-not $parser.EndOfData) {
        $f = $parser.ReadFields()
        $name = ($f[2] -replace '\s+', ' ').Trim()
        if ($f[1] -and $name) { $lines.Add("$($f[1].ToUpperInvariant())`t$name"); $count++ }
    }
    Write-Host "$url : $count entries"
}
if ($lines.Count -lt 40000) { throw "Only $($lines.Count) entries; the download looks incomplete." }
$lines.Sort([StringComparer]::Ordinal)
$file = [IO.File]::Create($out)
try {
    $gz = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
    $w = [IO.StreamWriter]::new($gz, [Text.UTF8Encoding]::new($false))
    $w.NewLine = "`n"
    foreach ($l in $lines) { $w.WriteLine($l) }
    $w.Dispose()
} finally { $file.Dispose() }
Write-Host "Wrote $($lines.Count) prefixes to $out ($([Math]::Round((Get-Item $out).Length / 1KB)) KB)"
