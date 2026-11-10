<#
.SYNOPSIS
  Shows how the eero cloud API changed in a newer eero Android APK, compared with the one eero Desktop was built from (26.13.0).

.EXAMPLE
  & C:\Users\Jess\Desktop\eero\tools\Compare-ApkApi.ps1 -Apk 'C:\Users\Jess\Downloads\com.eero.android_27.1.0.apk'

.DESCRIPTION
  1. Decompiles the APK with the bundled jadx into eero\decompiled-<version>\ (skipped if it already exists).
  2. Extracts every Retrofit endpoint with research\extract_retrofit.py.
  3. Diffs "METHOD path" pairs against research\endpoints.txt and writes research\api-diff-<version>.txt.
  Endpoints eero Desktop calls are flagged with "USED BY APP" so you know which removals matter.
#>
param(
    [Parameter(Mandatory)][string]$Apk,
    [string]$Baseline = "$PSScriptRoot\..\research\endpoints.txt",
    # Use already-decompiled sources instead of running jadx (mainly for testing this script).
    [string]$SourcesDir
)
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path "$PSScriptRoot\..").Path
$Apk = (Resolve-Path $Apk).Path
$version = if ((Split-Path $Apk -Leaf) -match '_(\d+\.\d+\.\d+)') { $Matches[1] } else { (Get-Date -Format 'yyyyMMdd') }
$outDir = Join-Path $Root "decompiled-$version"

if ($SourcesDir) { $outDir = Split-Path $SourcesDir -Parent }
elseif (-not (Test-Path "$outDir\sources")) {
    Write-Host "Decompiling $Apk -> $outDir (takes a few minutes; class errors are normal)…"
    & "$Root\tools\jadx\bin\jadx.bat" --no-res --threads-count 8 -d $outDir $Apk | Out-Null
    if (-not (Test-Path "$outDir\sources")) { throw "jadx produced no sources in $outDir" }
}

$newEndpoints = Join-Path $Root "research\endpoints-$version.txt"
Write-Host "Extracting endpoints…"
& python -I "$Root\research\extract_retrofit.py" "$outDir\sources" $newEndpoints
if (-not (Test-Path $newEndpoints)) { throw "endpoint extraction failed" }

function Get-Pairs([string]$file) {
    Get-Content $file | Where-Object { $_ -match '^(GET|POST|PUT|DELETE|PATCH|HTTP)\s+(.+)$' } |
        ForEach-Object { ('{0} {1}' -f $Matches[1], $Matches[2].Trim().Trim('"')) } | Sort-Object -Unique
}
$old = Get-Pairs $Baseline
$new = Get-Pairs $newEndpoints

# Paths the desktop app calls (literal and interpolated strings), normalized so "{anything}" matches any one segment.
function Norm([string]$p) { (($p.Trim('"', '/') -replace '^2\.\d+/', '') -replace '\{[^}]*\}', '{}').ToLowerInvariant() }
$used = Get-ChildItem "$Root\EeroDesktop" -Recurse -Include *.cs | Where-Object FullName -notmatch '\\(bin|obj)\\' |
    Select-String -Pattern '"(2\.\d+/[^"?]+)' -AllMatches | ForEach-Object { $_.Matches | ForEach-Object { Norm $_.Groups[1].Value } } | Sort-Object -Unique
function Mark([string]$pair) {
    $p = Norm ($pair -replace '^\S+\s+', '')
    $hit = $used | Where-Object { $a = $_ -split '/'; $b = $p -split '/'; $a.Count -eq $b.Count -and -not (0..($a.Count - 1) | Where-Object { $a[$_] -ne $b[$_] -and $a[$_] -ne '{}' -and $b[$_] -ne '{}' }) }
    if ($hit) { "$pair    <-- USED BY APP" } else { $pair }
}
$added   = $new | Where-Object { $_ -notin $old }
$removed = $old | Where-Object { $_ -notin $new }
$report = @(
    "eero API diff: baseline 26.13.0 ($($old.Count) endpoints) vs $version ($($new.Count) endpoints)"
    "Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
    ''
    "REMOVED ($($removed.Count)) — if eero Desktop uses one of these, that feature may break:"
    ($removed | ForEach-Object { '  - ' + (Mark $_) })
    ''
    "ADDED ($($added.Count)) — new features eero Desktop could support:"
    ($added | ForEach-Object { '  + ' + $_ })
)
$reportPath = Join-Path $Root "research\api-diff-$version.txt"
$report | Set-Content $reportPath
$report | Select-Object -First 60 | Write-Host
Write-Host "`nFull report: $reportPath"
Write-Host "If a used endpoint changed, update EeroClient.AppVersion and the affected page, then run tests\E2E.ps1."
