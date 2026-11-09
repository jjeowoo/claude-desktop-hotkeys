param([string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
if(-not (Test-Path -LiteralPath $compiler)){throw 'The x64 .NET Framework C# compiler is missing.'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$references=@('Claude-effort-reference.png','GPT-effort-reference.png','GPT-effort-high-reference.png','GPT-closed-reference.png')
if($output -ine [IO.Path]::GetFullPath($PSScriptRoot)){
    foreach($name in $references){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $output $name) -Force}
}
$sources=@('ReasoningSwitch.cs','GptImageSlider.cs','ClaudeImageSlider.cs','StrokesImageSearch.cs','TypeAhead.cs','Translate.cs','PinnedImage.cs','BacktickClick.cs','Program.cs') | ForEach-Object {Join-Path $PSScriptRoot $_}
# The build name is defined once, in ReasoningSwitch.cs.
$build=[regex]::Match((Get-Content -LiteralPath $sources[0] -Raw),'BuildName\s*=\s*"([^"]+)"').Groups[1].Value
if(-not $build){throw 'BuildName was not found in ReasoningSwitch.cs.'}
$next=Join-Path $output 'ReasoningSwitch.build.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ ('/out:'+$next) ('/reference:'+(Join-Path $wpf 'UIAutomationClient.dll')) ('/reference:'+(Join-Path $wpf 'UIAutomationTypes.dll')) ('/reference:'+(Join-Path $wpf 'WindowsBase.dll')) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll $sources
if($LASTEXITCODE -ne 0){throw 'Compilation failed; the existing helper was not replaced.'}
$test=Start-Process -FilePath $next -ArgumentList '--selftest' -WindowStyle Hidden -PassThru -Wait
if($test.ExitCode -ne 0){throw 'Self-test failed; see selftest-error.txt. The existing helper was not replaced.'}
$target=Join-Path $output 'ReasoningSwitch.exe'
# The capture server ("pin-image --serve") keeps running, which locks the helper. Windows still lets
# a running executable be renamed: move it aside, and the server retires on its next capture.
Get-ChildItem -LiteralPath $output -Filter 'ReasoningSwitch.exe.old-*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
try{Move-Item -LiteralPath $next -Destination $target -Force -ErrorAction Stop}
catch{
    Move-Item -LiteralPath $target -Destination ($target+'.old-'+[DateTime]::Now.Ticks)
    Move-Item -LiteralPath $next -Destination $target
}
[pscustomobject]@{Build=$build;SelfTest='passed';Executable=(Join-Path $output 'ReasoningSwitch.exe')}
