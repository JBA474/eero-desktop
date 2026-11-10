<#
.SYNOPSIS
  Checks Google Play for a newer eero Android app than the one eero Desktop was built from, and when there is one,
  diffs its API against ours with Compare-ApkApi.ps1.

.EXAMPLE
  & C:\Users\Jess\Desktop\eero\tools\Check-EeroApp.ps1
  & C:\Users\Jess\Desktop\eero\tools\Check-EeroApp.ps1 -Apk 'C:\Users\Jess\Downloads\com.eero.android_27.1.0.apk'

.DESCRIPTION
  1. Reads the current version from the eero app's Google Play page (read-only, one request).
  2. Compares it with EeroClient.AppVersion (the version eero Desktop's API calls came from).
  3. If it's newer, looks for the APK in -Apk or your Downloads folder (com.eero.android_<version>*.apk).
     eero doesn't publish APKs, so download it yourself from APKMirror (-Open opens the page). Nothing is downloaded
     automatically.
  4. Runs Compare-ApkApi.ps1 on it, which reports removed endpoints that eero Desktop uses.
#>
param(
    [string]$Apk,
    [switch]$Open
)
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path "$PSScriptRoot\..").Path
$playUrl = 'https://play.google.com/store/apps/details?id=com.eero.android&hl=en_US&gl=US'
$mirrorUrl = 'https://www.apkmirror.com/apk/eero-inc/eero-wifi-system/'

$built = [regex]::Match((Get-Content "$Root\EeroDesktop\Api\EeroClient.cs" -Raw), 'AppVersion\s*=\s*"([\d.]+)"').Groups[1].Value
$html = (Invoke-WebRequest $playUrl -UseBasicParsing -UserAgent 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) EeroDesktop').Content
$m = [regex]::Match($html, '\[\[\["(\d+\.\d+\.\d+)(?:\.\d+)?"\]\],\[\[\[\d+\]\]')
if (-not $m.Success) { throw "Couldn't find the version on the Google Play page (its layout may have changed): $playUrl" }
$latest = $m.Groups[1].Value
$updated = [regex]::Match($html, '>Updated on</div><div[^>]*>([^<]{4,30})</div>').Groups[1].Value

Write-Host "eero Desktop was built from eero app $built."
Write-Host ("Google Play has eero app $latest" + $(if ($updated) { " (updated $updated)." } else { '.' }))
if ([version]$latest -le [version]$built) { Write-Host 'Up to date — nothing to compare.'; return }

if (-not $Apk) {
    $downloads = Join-Path $env:USERPROFILE 'Downloads'
    $Apk = Get-ChildItem $downloads -Filter "com.eero.android_$latest*.apk" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Apk) {
    Write-Host "`nA newer eero app is out. To see what changed in its API:"
    Write-Host "  1. Download the $latest APK (not the bundle) from $mirrorUrl"
    Write-Host "  2. Run this script again (it looks in Downloads), or pass -Apk <path>."
    if ($Open) { Start-Process $mirrorUrl }
    return
}
Write-Host "`nComparing $Apk with the $built baseline…"
& "$PSScriptRoot\Compare-ApkApi.ps1" -Apk $Apk
