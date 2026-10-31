param(
    [string]$OutputDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Join-Path $repoRoot 'src\CodexPulse'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "artifacts\bin\$Configuration" }
$outputRoot = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows x64 .NET Framework is required.' }
# Read the same source/resource manifest as Visual Studio, without requiring a targeting pack.
$project = New-Object System.Xml.XmlDocument
$project.Load((Join-Path $projectRoot 'CodexPulse.csproj'))
$namespaces = New-Object System.Xml.XmlNamespaceManager($project.NameTable)
$namespaces.AddNamespace('msb', 'http://schemas.microsoft.com/developer/msbuild/2003')
$references = foreach ($reference in $project.SelectNodes('//msb:Reference', $namespaces)) {
    $referenceRoot = $framework
    if ($reference.Include -in @('WindowsBase', 'PresentationCore', 'PresentationFramework')) { $referenceRoot = Join-Path $framework 'WPF' }
    '/r:' + (Join-Path $referenceRoot ($reference.Include + '.dll'))
}
$sources = foreach ($source in $project.SelectNodes('//msb:Compile', $namespaces)) {
    $sourcePath = Join-Path $projectRoot $source.Include
    if (-not (Test-Path -LiteralPath $sourcePath)) { throw "Missing source: $sourcePath" }
    $sourcePath
}
$resources = foreach ($resource in $project.SelectNodes('//msb:EmbeddedResource', $namespaces)) {
    '/resource:' + (Join-Path $projectRoot $resource.Include) + ',' + $resource.SelectSingleNode('msb:LogicalName', $namespaces).InnerText
}
$properties = $project.SelectSingleNode('/msb:Project/msb:PropertyGroup', $namespaces)
$manifest = Join-Path $projectRoot $properties.ApplicationManifest
$icon = Join-Path $projectRoot $properties.ApplicationIcon
$optimize = '/optimize+'
$defines = '/define:TRACE'
if ($Configuration -eq 'Debug') { $optimize = '/optimize-'; $defines = '/define:DEBUG;TRACE' }
$executable = Join-Path $outputRoot 'CodexPulse.exe'
& $compiler /nologo /target:winexe /platform:x64 /utf8output /debug:pdbonly $optimize $defines ("/out:$executable") ("/win32manifest:$manifest") ("/win32icon:$icon") @references @resources @sources
if ($LASTEXITCODE -ne 0) { throw 'Codex Pulse build failed.' }
Copy-Item -LiteralPath $icon -Destination (Join-Path $outputRoot 'pulse.ico') -Force
Write-Output $executable
