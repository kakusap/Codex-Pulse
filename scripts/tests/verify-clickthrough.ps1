param([string]$ProgramPath)
. (Join-Path $PSScriptRoot 'Common.ps1')
$context = New-VerificationContext -Name 'clickthrough' -ProgramPath $ProgramPath
$verifier = Build-Verification $context 'tests\Desktop\ClickThroughVerification.cs' 'ClickThroughVerification.exe'
& $verifier --output $context.OutputRoot
Assert-VerificationExit $LASTEXITCODE $context
$monitors = Build-Verification $context 'tests\Desktop\MultiMonitorVerification.cs' 'MultiMonitorVerification.exe' -Manifest
& $monitors (Join-Path $context.OutputRoot 'monitors')
Assert-VerificationExit $LASTEXITCODE $context
