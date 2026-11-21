param(
    [string]$BaseUrl = 'http://127.0.0.1:18080',
    [switch]$CheckFixture,
    [switch]$ReadOnly
)

$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'smoke_test.py'
$runnerArgs = if ($CheckFixture) { @('--check-fixture') } elseif ($ReadOnly) { @($BaseUrl, '--read-only') } else { @($BaseUrl) }
$launcher = Get-Command py -ErrorAction SilentlyContinue
if ($launcher) {
    & $launcher.Source -3 $runner @runnerArgs
} else {
    & python $runner @runnerArgs
}
exit $LASTEXITCODE
