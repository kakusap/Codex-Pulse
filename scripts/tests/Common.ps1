$ErrorActionPreference = 'Stop'
$verificationRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function New-VerificationContext {
    param([string]$Name, [string]$ProgramPath)
    $outputRoot = Join-Path $verificationRepoRoot "artifacts\tests\$Name"
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    if ($ProgramPath) {
        Copy-Item -LiteralPath $ProgramPath -Destination (Join-Path $outputRoot 'CodexPulse.exe') -Force
        Copy-Item -LiteralPath (Join-Path $verificationRepoRoot 'src\CodexPulse\Resources\pulse.ico') -Destination $outputRoot -Force
    } else {
        & (Join-Path $verificationRepoRoot 'build.ps1') -OutputDirectory $outputRoot | Out-Host
    }
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $references = @('System.dll', 'System.Core.dll', 'System.Security.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
    $references += @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll') | ForEach-Object { '/r:' + (Join-Path (Join-Path $framework 'WPF') $_) }
    $references += '/r:' + (Join-Path $outputRoot 'CodexPulse.exe')
    return [PSCustomObject]@{
        RepoRoot = $verificationRepoRoot
        OutputRoot = $outputRoot
        Compiler = Join-Path $framework 'csc.exe'
        References = $references
    }
}

function Build-Verification {
    param($Context, [string]$Source, [string]$OutputName, [switch]$WindowsApplication, [switch]$Manifest)
    $output = Join-Path $Context.OutputRoot $OutputName
    $target = '/target:exe'
    if ($WindowsApplication) { $target = '/target:winexe' }
    $extra = @()
    if ($Manifest) { $extra += '/win32manifest:' + (Join-Path $Context.RepoRoot 'src\CodexPulse\Resources\app.manifest') }
    & $Context.Compiler /nologo $target /platform:x64 /utf8output ("/out:$output") @($Context.References) @extra (Join-Path $Context.RepoRoot $Source)
    if ($LASTEXITCODE -ne 0) { throw "Verification build failed: $Source" }
    return $output
}

function Assert-VerificationExit {
    param([int]$ExitCode, $Context)
    if ($ExitCode -ne 0) { throw "Verification failed. Reports: $($Context.OutputRoot)" }
}
