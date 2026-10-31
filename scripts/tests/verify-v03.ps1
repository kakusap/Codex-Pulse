param([switch]$ProbeRuntime)
. (Join-Path $PSScriptRoot 'Common.ps1')
$context = New-VerificationContext -Name 'accounts'
$verifier = Build-Verification $context 'tests\Accounts\IndependentVerification.cs' 'IndependentVerification.exe'
if ($ProbeRuntime) { & $verifier $context.OutputRoot --runtime } else { & $verifier $context.OutputRoot }
Assert-VerificationExit $LASTEXITCODE $context
