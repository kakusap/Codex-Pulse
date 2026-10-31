. (Join-Path $PSScriptRoot 'Common.ps1')
$context = New-VerificationContext -Name 'account-switch'
foreach ($fixtureName in @('desktop-fixture', 'unrelated-fixture')) {
    $fixture = Join-Path $context.OutputRoot $fixtureName
    New-Item -ItemType Directory -Path (Join-Path $fixture 'resources') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fixture 'chrome_elf.dll') -Value 'test fixture marker'
    Build-Verification $context 'tests\Fixtures\FakeCodexDesktop.cs' "$fixtureName\ChatGPT.exe" -WindowsApplication | Out-Null
}
$verifier = Build-Verification $context 'tests\Accounts\AccountSwitchVerification.cs' 'AccountSwitchVerification.exe'
& $verifier $context.OutputRoot
Assert-VerificationExit $LASTEXITCODE $context
