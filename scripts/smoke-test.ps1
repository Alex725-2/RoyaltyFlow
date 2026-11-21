param(
    [string]$BaseUrl = 'http://127.0.0.1:18080',
    [switch]$CheckFixture
)

$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'smoke_test.py'
$runnerArgs = if ($CheckFixture) { @('--check-fixture') } else { @($BaseUrl) }
$launcher = Get-Command py -ErrorAction SilentlyContinue
if ($launcher) {
    & $launcher.Source -3 $runner @runnerArgs
} else {
    & python $runner @runnerArgs
}
exit $LASTEXITCODE
