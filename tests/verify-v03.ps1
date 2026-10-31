param([switch]$ProbeRuntime)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$outputRoot=Join-Path $taskRoot 'data\qa-v03'
& (Join-Path $taskRoot 'build.ps1') -OutputDirectory $outputRoot
Copy-Item -LiteralPath (Join-Path $taskRoot 'pulse.ico') -Destination $outputRoot
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references=@('System.dll','System.Core.dll','System.Security.dll','System.Web.Extensions.dll','System.Xaml.dll') | ForEach-Object { '/r:'+(Join-Path $framework $_) }
$references+=@('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/r:'+(Join-Path (Join-Path $framework WPF) $_) }
$references+='/r:'+(Join-Path $outputRoot 'CodexPulse.exe')
$verifier=Join-Path $outputRoot 'IndependentVerification.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /utf8output ('/out:'+$verifier) @references (Join-Path $PSScriptRoot 'IndependentVerification.cs')
if($LASTEXITCODE -ne 0) { throw '独立账号验证编译失败' }
if($ProbeRuntime) { & $verifier $outputRoot --runtime } else { & $verifier $outputRoot }
if($LASTEXITCODE -ne 0) { throw 'V0.3 独立账号验证失败' }
