[CmdletBinding(DefaultParameterSetName = 'Publish')]
param(
    [Parameter(ParameterSetName = 'Preflight')]
    [switch]$PreflightOnly,

    [Parameter(ParameterSetName = 'Diagnose')]
    [switch]$DiagnoseMigration,

    [Parameter(ParameterSetName = 'Publish')]
    [switch]$SeedPortfolio
)

$ErrorActionPreference = 'Stop'
$ssh = Join-Path $env:WINDIR 'System32\OpenSSH\ssh.exe'
$key = 'C:\Users\Alex\.ssh\fornex_codex'
$knownHosts = 'C:\Users\Alex\.ssh\known_hosts'
$repository = Split-Path -Parent $PSScriptRoot

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Program failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Fornex([string]$Mode, [string]$Revision, [string]$Archive) {
    if ($Mode -in @('deploy', 'deploy-seed')) {
        $remoteCommand = 'set -eu; umask 077; install -d -m 700 /opt/royaltyflow-releases; release=$(mktemp -d /opt/royaltyflow-releases/release.XXXXXXXX); tar --no-same-owner --no-same-permissions -xzf - -C $release; exec bash $release/scripts/deploy-release.sh $release __REVISION__ __MODE__'
    } else {
        $remoteCommand = 'set -eu; umask 077; release=$(mktemp -d /tmp/royaltyflow-check.XXXXXXXX); trap cleanup EXIT; cleanup() { rm -rf -- $release; }; tar --no-same-owner --no-same-permissions -xzf - -C $release; bash $release/scripts/deploy-release.sh $release __REVISION__ __MODE__'
    }
    $remoteCommand = "bash -c '" + $remoteCommand.Replace('__REVISION__', $Revision).Replace('__MODE__', $Mode) + "'"
    $sshArguments = @(
        '-T', '-p', '22', '-i', $key,
        '-o', "UserKnownHostsFile=$knownHosts",
        '-o', 'StrictHostKeyChecking=yes', '-o', 'BatchMode=yes',
        '-o', 'IdentitiesOnly=yes', '-o', 'ConnectTimeout=10',
        '-o', 'ServerAliveInterval=15', '-o', 'ServerAliveCountMax=4',
        'root@89.127.218.118', $remoteCommand
    )
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $ssh
    $startInfo.Arguments = ($sshArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $inputFile = [IO.File]::OpenRead($Archive)
    try {
        $process = [Diagnostics.Process]::Start($startInfo)
        try {
            $copyFailure = $null
            try {
                # Windows PowerShell's native pipeline is text. Keep the tar.gz stream binary.
                $inputFile.CopyTo($process.StandardInput.BaseStream)
            } catch {
                $copyFailure = $_
            } finally {
                $process.StandardInput.BaseStream.Close()
            }
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) {
                throw "Fornex $Mode failed with SSH exit code $($process.ExitCode)."
            }
            if ($copyFailure) {
                throw "Could not send the complete source archive to Fornex: $copyFailure"
            }
        } finally {
            $process.Dispose()
        }
    } finally {
        $inputFile.Dispose()
    }
}

Push-Location -LiteralPath $repository
$archive = $null
try {
    $revision = & git rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or $revision -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Could not resolve the current Git commit.'
    }
    $mode = if ($DiagnoseMigration) { 'diagnose' } else { 'preflight' }
    if (-not ($PreflightOnly -or $DiagnoseMigration)) {
        $branch = & git branch --show-current
        if ($LASTEXITCODE -ne 0 -or $branch -cne 'main') { throw 'Switch to main before publishing.' }
        $changes = & git status --porcelain --untracked-files=normal
        if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Commit or set aside all working-tree changes before publishing.' }
        $pushUrl = & git remote get-url --push origin
        if ($LASTEXITCODE -ne 0 -or $pushUrl -cnotmatch '^(https://github\.com/Alex725-2/RoyaltyFlow(?:\.git)?|git@github\.com:Alex725-2/RoyaltyFlow(?:\.git)?)$') {
            throw 'The origin push URL does not point to Alex725-2/RoyaltyFlow.'
        }
        Invoke-Checked git @('show', '--format=', '--check', $revision)
        Invoke-Checked dotnet @('restore', 'RoyaltyFlow.sln')
        Invoke-Checked dotnet @('build', 'RoyaltyFlow.sln', '-c', 'Release', '--no-restore')
        Invoke-Checked dotnet @('test', 'RoyaltyFlow.sln', '-c', 'Release', '--no-build', '--no-restore')
        Invoke-Checked py @('-3', '-m', 'unittest', 'discover', '-s', 'scripts')
        if ((& git rev-parse --verify HEAD) -cne $revision -or (& git status --porcelain --untracked-files=normal)) {
            throw 'The checkout changed during verification.'
        }
    }

    foreach ($path in @($ssh, $key, $knownHosts)) {
        try {
            $file = [IO.File]::OpenRead($path)
            $file.Dispose()
        } catch {
            throw "Cannot read $path. Run this script from ordinary Windows PowerShell with access to the existing SSH files."
        }
    }

    $archive = Join-Path ([IO.Path]::GetTempPath()) ("royaltyflow-" + [Guid]::NewGuid().ToString('N') + '.tar.gz')
    Invoke-Checked git @('archive', '--format=tar.gz', '-o', $archive, $revision)
    Invoke-Fornex $mode $revision $archive
    if ($PreflightOnly -or $DiagnoseMigration) { return }

    Invoke-Checked git @('push', $pushUrl, ($revision + ':refs/heads/main'))
    $published = & git ls-remote --exit-code $pushUrl refs/heads/main
    if ($LASTEXITCODE -ne 0 -or $published -cne "$revision`trefs/heads/main") {
        throw 'GitHub main no longer points to the tested commit. Release skipped.'
    }
    $publishMode = if ($SeedPortfolio) { 'deploy-seed' } else { 'deploy' }
    Invoke-Fornex $publishMode $revision $archive
} finally {
    if ($archive -and (Test-Path -LiteralPath $archive)) {
        Remove-Item -LiteralPath $archive
    }
    Pop-Location
}
