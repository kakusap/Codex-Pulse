param([ValidateSet('Core', 'Desktop', 'All', 'Accounts', 'Usage', 'AccountSwitch', 'Updates', 'Themes', 'ClickThrough')][string]$Suite = 'Core')
$ErrorActionPreference = 'Stop'
$suites = @{ Accounts = 'verify-v03.ps1'; Usage = 'verify-v04.ps1'; AccountSwitch = 'verify-account-switch.ps1'; Updates = 'verify-updates.ps1'; Themes = 'verify-dialog-theme.ps1'; ClickThrough = 'verify-clickthrough.ps1' }
$selected = @($Suite)
if ($Suite -eq 'Core') { $selected = @('Accounts', 'Usage', 'AccountSwitch', 'Updates') }
if ($Suite -eq 'Desktop') { $selected = @('Themes', 'ClickThrough') }
if ($Suite -eq 'All') { $selected = @('Accounts', 'Usage', 'AccountSwitch', 'Updates', 'Themes', 'ClickThrough') }
foreach ($name in $selected) {
    Write-Host "Running verification suite: $name"
    & (Join-Path $PSScriptRoot ('tests\' + $suites[$name]))
}
