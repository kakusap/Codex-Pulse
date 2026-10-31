. (Join-Path $PSScriptRoot 'Common.ps1')
$context = New-VerificationContext -Name 'themes'
$verifier = Build-Verification $context 'tests\Desktop\DialogThemeVerification.cs' 'DialogThemeVerification.exe'
& $verifier $context.OutputRoot
Assert-VerificationExit $LASTEXITCODE $context
