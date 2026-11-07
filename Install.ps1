param([switch]$DryRun, [switch]$Undo)
$ErrorActionPreference = 'Stop'
$marker = 'CodexClaudeReasoningHotkeys-v1'
$configPath = [IO.Path]::GetFullPath((Join-Path $env:APPDATA 'StrokesPlus.net\StrokesPlus.net.json'))
$programPath = 'C:\Program Files\StrokesPlus.net\StrokesPlus.net.exe'
$originalJson = [IO.File]::ReadAllText($configPath)
$config = $originalJson | ConvertFrom-Json
$oldRecords = @($config.Hotkeys | Where-Object { $_.Comments -ne $marker })
if ($Undo) {
    $config.Hotkeys = $oldRecords
    if (-not @($oldRecords | Where-Object { $_.Category -eq 'AI Reasoning' }).Count) {
        $config.HotkeyCategories = @($config.HotkeyCategories | Where-Object { $_ -ne 'AI Reasoning' })
    }
} else {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'ReasoningSwitch.exe'))) { throw 'ReasoningSwitch.exe is missing.' }
    # Windows PowerShell 5.1 returns the JSON array as one object; enumerate it explicitly.
    $records = @((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Hotkeys.json') -Raw | ConvertFrom-Json) | ForEach-Object { $_ })
    if ($records.Count -ne 11) { throw 'Expected exactly eleven hotkey records.' }
    $nextId = [int](($oldRecords | Measure-Object HotkeyID -Maximum).Maximum) + 1
    $helperPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'ReasoningSwitch.exe'))
    $helperLiteral = $helperPath | ConvertTo-Json -Compress
    foreach ($record in $records) {
        # Bind the portable template to this installation directory, not the
        # developer's Windows account or the default manual installation path.
        $pattern = '(?m)^var reasoningHelper = [^\r\n]*;\r?$'
        if ([regex]::Matches($record.Script, $pattern).Count -ne 1) { throw 'Expected one helper path declaration in each hotkey.' }
        $record.Script = [regex]::Replace($record.Script, $pattern, [Text.RegularExpressions.MatchEvaluator]{param($match) 'var reasoningHelper = ' + $helperLiteral + ';'})
        $collision = @($oldRecords | Where-Object { $_.Active -and $_.Key -eq $record.Key -and [bool]$_.Control -eq [bool]$record.Control -and
            [bool]$_.Alt -eq [bool]$record.Alt -and [bool]$_.Shift -eq [bool]$record.Shift -and [bool]$_.Win -eq [bool]$record.Win })
        if ($collision.Count) { throw "A requested hotkey is already assigned: $($collision[0].Description)" }
        $record.HotkeyID = $nextId
        $nextId++
    }
    $config.Hotkeys = $oldRecords + $records
    if ('AI Reasoning' -notin $config.HotkeyCategories) { $config.HotkeyCategories = @($config.HotkeyCategories) + 'AI Reasoning' }
}
$updatedJson = $config | ConvertTo-Json -Depth 100
$check = $updatedJson | ConvertFrom-Json
$original = $originalJson | ConvertFrom-Json
foreach ($property in $original.PSObject.Properties) {
    if ($property.Name -in @('Hotkeys','HotkeyCategories')) { continue }
    $before = $property.Value | ConvertTo-Json -Depth 100 -Compress
    $after = $check.($property.Name) | ConvertTo-Json -Depth 100 -Compress
    if ($before -cne $after) { throw "Unexpected change outside hotkeys: $($property.Name)" }
}
for ($index = 0; $index -lt $oldRecords.Count; $index++) {
    $oldRecord = $oldRecords[$index]
    $sameRecord = $check.Hotkeys[$index]
    if (($sameRecord | ConvertTo-Json -Depth 100 -Compress) -cne ($oldRecord | ConvertTo-Json -Depth 100 -Compress)) {
        throw "An existing hotkey changed: $($oldRecord.Description)"
    }
}
# Opened by Explorer, StrokesPlus belongs to no terminal or app that ran this installer: started as
# their child it closed with them (the Claude app's restart took it down on 2026-10-20). WMI's
# Win32_Process.Create failed from inside the Claude app, so Explorer opens it instead.
function Start-Detached([string]$path) {
    Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('"' + $path + '"')
    for ($i = 0; $i -lt 40 -and -not (Get-Process -Name 'StrokesPlus.net' -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    if (-not (Get-Process -Name 'StrokesPlus.net' -ErrorAction SilentlyContinue)) { Start-Process -FilePath $path -WindowStyle Hidden }
}
$strokesProcesses = @(Get-Process -Name 'StrokesPlus.net' -ErrorAction SilentlyContinue)
if ($strokesProcesses.Count -gt 1) { throw 'Multiple StrokesPlus processes are running; configuration was not changed.' }
if ($DryRun) {
    [pscustomobject]@{DryRun=$true;ExistingHotkeys=$oldRecords.Count;NewHotkeys=@($check.Hotkeys | Where-Object { $_.Comments -eq $marker }).Count;OtherSettingsUnchanged=$true;RestartNeeded=($strokesProcesses.Count -gt 0)} | ConvertTo-Json
    exit 0
}
if ([IO.File]::ReadAllText($configPath) -cne $originalJson) { throw 'Settings changed during preparation; run this installer again.' }
# The file was just checked to still match $originalJson, so this zip holds exactly what is replaced.
$backupPath = & (Join-Path $PSScriptRoot 'Backup-StrokesPlus.ps1') -Label $(if ($Undo) { 'undo' } else { 'install' })
$stopped = $false
$written = $false
try {
    if ($strokesProcesses.Count) {
        $strokesProcesses | Stop-Process -ErrorAction Stop
        $stopped = $true
        $strokesProcesses | Wait-Process -Timeout 8 -ErrorAction SilentlyContinue
        if (@($strokesProcesses | Where-Object { Get-Process -Id $_.Id -ErrorAction SilentlyContinue }).Count) { throw 'StrokesPlus did not stop within eight seconds.' }
    }
    if ([IO.File]::ReadAllText($configPath) -cne $originalJson) { throw 'Settings changed while stopping StrokesPlus; configuration was not replaced.' }
    [IO.File]::WriteAllText($configPath, $updatedJson, [Text.UTF8Encoding]::new($false))
    $written = $true
    if ($stopped -or -not $Undo) { Start-Detached $programPath }
    [pscustomobject]@{Installed=(-not $Undo);Removed=[bool]$Undo;HotkeyCount=$check.Hotkeys.Count;Backup=$backupPath;Started=($stopped -or -not $Undo)} | ConvertTo-Json
} catch {
    if ($written) { [IO.File]::WriteAllText($configPath, $originalJson, [Text.UTF8Encoding]::new($false)) }
    if ($stopped -and -not (Get-Process -Name 'StrokesPlus.net' -ErrorAction SilentlyContinue)) { Start-Detached $programPath }
    throw
}
