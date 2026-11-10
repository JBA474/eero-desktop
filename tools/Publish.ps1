<#
.SYNOPSIS
  Packages eero Desktop as one self-contained EeroDesktop.exe (no .NET install needed) in eero\dist\,
  and creates/updates the "eero Desktop" shortcut on the Desktop.
#>
param([switch]$NoShortcut)
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path "$PSScriptRoot\..").Path
$Dist = Join-Path $Root 'dist'
$Exe  = Join-Path $Dist 'EeroDesktop.exe'

# A running copy locks the exe; ask it to quit (it removes its tray icon) before replacing it.
if (Get-Process EeroDesktop -ErrorAction SilentlyContinue) {
    $running = Get-Process EeroDesktop
    $any = @(Get-ChildItem "$Root\EeroDesktop\bin" -Recurse -Filter EeroDesktop.exe) + @(Get-Item $Exe -ErrorAction SilentlyContinue) | Select-Object -First 1
    if ($any) { Start-Process $any.FullName -ArgumentList '--exit' -Wait }
    $running | ForEach-Object { if (-not $_.WaitForExit(10000)) { $_.Kill() } }
}

dotnet publish "$Root\EeroDesktop\EeroDesktop.csproj" -c Release -r win-x64 --self-contained true -nologo `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $Dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Get-ChildItem $Dist -Exclude EeroDesktop.exe | Remove-Item -Recurse -Force   # single-file: nothing else is needed

if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'eero Desktop.lnk'
    $sh = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $sh.TargetPath = $Exe
    $sh.WorkingDirectory = $Dist
    $sh.IconLocation = "$Exe,0"
    $sh.Description = 'eero Desktop — manage your eero network from Windows'
    $sh.Save()
    Write-Host "Shortcut: $lnk"
}
$size = [math]::Round((Get-Item $Exe).Length / 1MB, 1)
Write-Host "Published $Exe ($size MB)"
