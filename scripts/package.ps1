param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\releases' }
$outputRoot = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$binaryRoot = Join-Path $outputRoot 'build'
& (Join-Path $repoRoot 'build.ps1') -OutputDirectory $binaryRoot
$executable = Join-Path $binaryRoot 'CodexPulse.exe'
$version = [Version]([Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion)
$tag = 'v' + $version.ToString(3)
$releaseRoot = Join-Path $outputRoot $tag
$packageRoot = Join-Path $releaseRoot 'CodexPulse'
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'docs\releases') -Force | Out-Null
Copy-Item -LiteralPath $executable,(Join-Path $binaryRoot 'pulse.ico') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\user-guide.md'),(Join-Path $repoRoot 'docs\pricing-sources.md') -Destination (Join-Path $packageRoot 'docs') -Force
$releaseNotes = Join-Path $repoRoot "docs\releases\$tag.md"
Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $packageRoot 'docs\releases') -Force
$readme = @"
# Codex Pulse $tag · Windows x64

双击 CodexPulse.exe 运行。需要 Windows x64 和 .NET Framework 4.8。

设置或托盘菜单 → 检查更新，可下载并重启升级。自动检查可在设置中关闭。
v0.4.3 及更早版本需要先手动升级一次：从托盘退出旧版，将 CodexPulse.exe 和 pulse.ico 覆盖到原目录，再启动程序。保留原 data 文件夹即可沿用账号、设置和用量记录。

使用说明：docs/user-guide.md
发布说明：docs/releases/$tag.md
GitHub：https://github.com/kakusap/Codex-Pulse
"@
[IO.File]::WriteAllText((Join-Path $packageRoot 'README.md'),$readme,[Text.UTF8Encoding]::new($false))
$zip = Join-Path $releaseRoot "CodexPulse-$tag-windows-x64.zip"
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zip -CompressionLevel Optimal -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $files = @($archive.Entries | Where-Object Name | ForEach-Object { $_.FullName.Replace('\','/') })
    $expected = @('CodexPulse/CodexPulse.exe','CodexPulse/pulse.ico','CodexPulse/README.md','CodexPulse/docs/user-guide.md','CodexPulse/docs/pricing-sources.md',"CodexPulse/docs/releases/$tag.md")
    if (Compare-Object $expected $files) { throw 'Release ZIP contains unexpected files.' }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $releaseRoot 'SHA256SUMS.txt'),($hash + '  ' + [IO.Path]::GetFileName($zip) + "`n"),[Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath $zip,(Join-Path $releaseRoot 'SHA256SUMS.txt') | Select-Object FullName,Length
