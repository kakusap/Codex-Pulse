param([string]$ProgramPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $taskRoot 'data\clickthrough-verification'
if($ProgramPath) {
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    Copy-Item -LiteralPath $ProgramPath -Destination (Join-Path $outputRoot 'CodexPulse.exe') -Force
} else {
    & (Join-Path $taskRoot 'build.ps1') -OutputDirectory $outputRoot
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'pulse.ico') -Destination $outputRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/r:' + (Join-Path $wpf $_) }
$references += '/r:' + (Join-Path $outputRoot 'CodexPulse.exe')
$verifier = Join-Path $outputRoot 'ClickThroughVerification.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /utf8output ('/out:' + $verifier) @references (Join-Path $PSScriptRoot 'ClickThroughVerification.cs')
if ($LASTEXITCODE -ne 0) { throw '鼠标穿透验证程序编译失败' }
& $verifier --output $outputRoot
if ($LASTEXITCODE -ne 0) { throw '跨进程鼠标穿透验证失败，请检查 data/clickthrough-verification/report.txt' }
$monitors = Join-Path $outputRoot 'MultiMonitorVerification.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /utf8output ('/win32manifest:' + (Join-Path $taskRoot 'app.manifest')) ('/out:' + $monitors) @references (Join-Path $PSScriptRoot 'MultiMonitorVerification.cs')
if ($LASTEXITCODE -ne 0) { throw '多显示器验证程序编译失败' }
& $monitors (Join-Path $outputRoot 'monitors')
if ($LASTEXITCODE -ne 0) { throw '多显示器验证失败，请检查 data/clickthrough-verification/monitors/report.txt' }
