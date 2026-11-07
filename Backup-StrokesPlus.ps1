# Zips the StrokesPlus.net settings file into the backup folder and keeps only the newest ten.
#   .\Backup-StrokesPlus.ps1 [-Label install] [-BackupDir <folder>] [-Keep 10]
# Run it before anything changes StrokesPlus.net.json; Install.ps1 calls it itself.
# Prints the path of the new zip. Fails without touching older backups if the zip can't be made.
# Saved as UTF-8 with BOM: Windows PowerShell 5.1 reads a BOM-less script as ANSI and mangles the Korean path.
param([string]$Label = '', [string]$BackupDir = '', [int]$Keep = 10)
$ErrorActionPreference = 'Stop'

$configPath = [IO.Path]::GetFullPath((Join-Path $env:APPDATA 'StrokesPlus.net\StrokesPlus.net.json'))
if (-not (Test-Path -LiteralPath $configPath)) { throw "StrokesPlus settings not found: $configPath" }

# The owner's settings-backup folder; on a machine without it, backups stay next to this script.
if (-not $BackupDir) {
    $ownerDir = 'D:\관리\Settings\생산성'
    $BackupDir = if (Test-Path -LiteralPath (Split-Path $ownerDir)) { $ownerDir } else { $PSScriptRoot }
}
[IO.Directory]::CreateDirectory($BackupDir) | Out-Null

$name = 'StrokesPlus-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
if ($Label) { $name += '-' + ($Label -replace '[^\w-]', '_') }
$zipPath = Join-Path $BackupDir ($name + '.zip')
for ($i = 2; Test-Path -LiteralPath $zipPath; $i++) { $zipPath = Join-Path $BackupDir ($name + "-$i.zip") }

# Written under a temporary name and renamed, so a failed run never leaves a broken zip counted as a backup.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$tempPath = $zipPath + '.tmp'
try {
    $zip = [IO.Compression.ZipFile]::Open($tempPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $configPath, 'StrokesPlus.net.json',
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    } finally { $zip.Dispose() }
    Move-Item -LiteralPath $tempPath -Destination $zipPath
} finally {
    if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force }
}

# Names start with the timestamp, so name order is age order. Only this script's zips are touched.
Get-ChildItem -LiteralPath $BackupDir -Filter 'StrokesPlus-*.zip' -File |
    Where-Object { $_.Name -match '^StrokesPlus-\d{8}-\d{6}' } |
    Sort-Object Name -Descending |
    Select-Object -Skip $Keep |
    Remove-Item -Force

$zipPath
