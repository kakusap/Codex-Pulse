param([switch]$Live)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$outputRoot=Join-Path $taskRoot 'data\qa-v04'
& (Join-Path $taskRoot build.ps1) -OutputDirectory $outputRoot
Copy-Item -LiteralPath (Join-Path $taskRoot pulse.ico) -Destination $outputRoot
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references=@('System.dll','System.Core.dll','System.Security.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll') | ForEach-Object { '/r:'+(Join-Path $framework $_) }
$references+=@('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/r:'+(Join-Path (Join-Path $framework WPF) $_) }
$references+='/r:'+(Join-Path $outputRoot CodexPulse.exe)
$verifier=Join-Path $outputRoot UsageVerification.exe
& (Join-Path $framework csc.exe) /nologo /target:exe /platform:x64 /utf8output ('/out:'+$verifier) @references (Join-Path $PSScriptRoot UsageVerification.cs)
if($LASTEXITCODE -ne 0) { throw 'V0.4 用量验证编译失败' }
& $verifier $outputRoot
if($LASTEXITCODE -ne 0) { throw 'V0.4 用量验证失败' }
if($Live) {
    & $verifier $outputRoot --live-settings (Join-Path $taskRoot 'data\settings.json')
    if($LASTEXITCODE -ne 0) { throw 'V0.4 本机真实用量验证失败' }
}
