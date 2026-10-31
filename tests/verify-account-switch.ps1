$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $taskRoot 'data\qa-account-switch'
& (Join-Path $taskRoot 'build.ps1') -OutputDirectory $outputRoot
Copy-Item -LiteralPath (Join-Path $taskRoot 'pulse.ico') -Destination $outputRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/r:' + (Join-Path $wpf $_) }
$references += '/r:' + (Join-Path $outputRoot 'CodexPulse.exe')
foreach ($fixtureName in @('desktop-fixture','unrelated-fixture')) {
    $fixture = Join-Path $outputRoot $fixtureName
    New-Item -ItemType Directory -Path (Join-Path $fixture 'resources') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fixture 'chrome_elf.dll') -Value 'test fixture marker'
    & (Join-Path $framework 'csc.exe') /nologo /target:winexe ('/out:' + (Join-Path $fixture 'ChatGPT.exe')) (Join-Path $PSScriptRoot 'FakeCodexDesktop.cs')
    if ($LASTEXITCODE -ne 0) { throw '重启验证夹具编译失败' }
}
$verifier = Join-Path $outputRoot 'AccountSwitchVerification.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /utf8output ('/out:' + $verifier) @references (Join-Path $PSScriptRoot 'AccountSwitchVerification.cs')
if ($LASTEXITCODE -ne 0) { throw '账号切换验证程序编译失败' }
& $verifier $outputRoot
if ($LASTEXITCODE -ne 0) { throw '账号切换验证失败，请检查 data/qa-account-switch/account-switch-report.txt' }
