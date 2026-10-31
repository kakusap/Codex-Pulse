param([string]$OutputDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$references = @('System.dll', 'System.Core.dll', 'System.Security.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$references += @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll') | ForEach-Object { '/r:' + (Join-Path $wpf $_) }
$sources = @('AssemblyInfo.cs','NativeSqlite.cs','DialogTheme.cs','Themes.cs','StartupRegistration.cs','Data.cs','PulseStore.cs','UsageStore.cs','PriceBook.cs','PriceTableWindow.cs','LocalCostSummary.cs','CostDetailsWindow.cs','LocalUsage.cs','RuntimeProbe.cs','IndependentStore.cs','CodexRpc.cs','IndependentRuntime.cs','ProfileFiles.cs','IndependentAccounts.cs','IndependentAccountWindow.cs','AccountSwitch.cs','Backdrop.cs','MonitorDock.cs','PinInteraction.cs','QuotaRing.cs','QuotaSummary.cs','QuotaDetailsPopup.cs','TokenHeatmap.cs','App.cs') | ForEach-Object { Join-Path $taskRoot $_ }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output ("/out:" + (Join-Path $OutputDirectory 'CodexPulse.exe')) ("/win32manifest:" + (Join-Path $taskRoot 'app.manifest')) ("/win32icon:" + (Join-Path $taskRoot 'pulse.ico')) ("/resource:" + (Join-Path $taskRoot 'MainWindow.xaml') + ',MainWindow.xaml') ("/resource:" + (Join-Path $taskRoot 'DialogTheme.xaml') + ',DialogTheme.xaml') ("/resource:" + (Join-Path $taskRoot 'model-prices.builtin.json') + ',model-prices.builtin.json') @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Codex Pulse 编译失败' }
Write-Output (Join-Path $OutputDirectory 'CodexPulse.exe')
