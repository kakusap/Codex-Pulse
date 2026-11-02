param([switch]$Live)
. (Join-Path $PSScriptRoot 'Common.ps1')
$context = New-VerificationContext -Name 'updates'
$fixtureSource = Join-Path $context.RepoRoot 'tests\Fixtures\FakeUpdateClient.cs'
$fixtures = Join-Path $context.OutputRoot 'fixtures'
New-Item -ItemType Directory -Path (Join-Path $fixtures 'old'),(Join-Path $fixtures 'new') -Force | Out-Null
$oldClient = Join-Path $fixtures 'old\CodexPulse.exe'
$newClient = Join-Path $fixtures 'new\CodexPulse.exe'
& $context.Compiler /nologo /target:exe /platform:x64 ("/out:$oldClient") $fixtureSource
if ($LASTEXITCODE -ne 0) { throw 'Old update fixture build failed.' }
& $context.Compiler /nologo /target:exe /platform:x64 /define:UPDATED ("/out:$newClient") $fixtureSource
if ($LASTEXITCODE -ne 0) { throw 'New update fixture build failed.' }
$verifier = Build-Verification $context 'tests\Updates\UpdateVerification.cs' 'UpdateVerification.exe'
$extra = @()
if ($Live) { $extra += '--live' }
& $verifier (Join-Path $context.OutputRoot ('run-' + [guid]::NewGuid().ToString('N'))) $oldClient $newClient @extra
Assert-VerificationExit $LASTEXITCODE $context
