param(
    [string]$OutputDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'scripts\build.ps1') @PSBoundParameters
