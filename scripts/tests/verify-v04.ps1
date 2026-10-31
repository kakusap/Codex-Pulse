param([switch]$Live, [string]$SettingsPath)
. (Join-Path $PSScriptRoot 'Common.ps1')
if ($Live -and (-not $SettingsPath -or -not (Test-Path -LiteralPath $SettingsPath))) { throw 'Live verification requires -SettingsPath pointing to an existing installation settings file.' }
$context = New-VerificationContext -Name 'usage'
$verifier = Build-Verification $context 'tests\Usage\UsageVerification.cs' 'UsageVerification.exe'
& $verifier $context.OutputRoot
Assert-VerificationExit $LASTEXITCODE $context
if ($Live) { & $verifier $context.OutputRoot --live-settings $SettingsPath; Assert-VerificationExit $LASTEXITCODE $context }
