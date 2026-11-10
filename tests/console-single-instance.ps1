[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath
)

$ErrorActionPreference = "Stop"

$script:Failed = $false
$script:Instances = @()
$script:TempDir = $null

function Write-Pass {
    param([string]$Message)
    Write-Host ("PASS: " + $Message) -ForegroundColor Green
}

function Write-Fail {
    param([string]$Message)
    Write-Host ("FAIL: " + $Message) -ForegroundColor Red
    $script:Failed = $true
}

function Start-BotInstance {
    param(
        [string]$Exe,
        [string]$WorkingDirectory
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Exe
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo

    if (-not $process.Start()) {
        throw "Failed to start process: $Exe"
    }

    $instance = [pscustomobject]@{
        Process = $process
        StdOut  = $process.StandardOutput.ReadToEndAsync()
        StdErr  = $process.StandardError.ReadToEndAsync()
    }

    $script:Instances += $instance
    return $instance
}

function Get-InstanceOutput {
    param($Instance)

    $stdout = ""
    $stderr = ""

    try {
        if ($Instance.StdOut.Wait(5000)) {
            $stdout = $Instance.StdOut.Result
        }
        else {
            $stdout = "<stdout read timed out>"
        }
    }
    catch {
        $stdout = "<stdout read failed: " + $_.Exception.Message + ">"
    }

    try {
        if ($Instance.StdErr.Wait(5000)) {
            $stderr = $Instance.StdErr.Result
        }
        else {
            $stderr = "<stderr read timed out>"
        }
    }
    catch {
        $stderr = "<stderr read failed: " + $_.Exception.Message + ">"
    }

    return [pscustomobject]@{
        StdOut   = $stdout
        StdErr   = $stderr
        Combined = ($stdout + $stderr)
    }
}

function Remove-TempDirectory {
    param([string]$Path)

    if ([string]::IsNullOrEmpty($Path)) {
        return
    }

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        if (-not (Test-Path -LiteralPath $Path)) {
            return
        }

        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }

    Write-Host ("WARN: could not remove temp directory: " + $Path) -ForegroundColor Yellow
}

try {
    if (-not (Test-Path -LiteralPath $ExePath)) {
        throw "ExePath does not exist: $ExePath"
    }

    $resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
    $sourceDir = Split-Path -Parent $resolvedExe
    $exeName = Split-Path -Leaf $resolvedExe

    $script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("tdb-console-single-instance-" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:TempDir | Out-Null

    $workDir = Join-Path $script:TempDir "app"
    Copy-Item -LiteralPath $sourceDir -Destination $workDir -Recurse -Force
    Write-Host ("Copied application directory to: " + $workDir)

    $configContents = '{"TwitchSettings":{"TwitchUsers":[]},"KickSettings":{"KickUsers":[]}}'
    $configPath = Join-Path $workDir "config.json"
    [System.IO.File]::WriteAllText($configPath, $configContents, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("Wrote isolated config: " + $configPath)

    $workExe = Join-Path $workDir $exeName

    Write-Host "Starting instance #1 (stdin held open)..."
    $first = Start-BotInstance -Exe $workExe -WorkingDirectory $workDir
    Start-Sleep -Seconds 3

    $first.Process.Refresh()
    if ($first.Process.HasExited) {
        $firstOutput = Get-InstanceOutput -Instance $first
        Write-Fail ("instance #1 exited prematurely with exit code " + $first.Process.ExitCode)
        Write-Host "--- instance #1 stdout ---"
        Write-Host $firstOutput.StdOut
        Write-Host "--- instance #1 stderr ---"
        Write-Host $firstOutput.StdErr
        throw "instance #1 is required to stay alive for the second-instance check"
    }
    Write-Pass "instance #1 is still running after 3 seconds"

    Write-Host "Starting instance #2 (stdin held open)..."
    $second = Start-BotInstance -Exe $workExe -WorkingDirectory $workDir

    $exited = $second.Process.WaitForExit(10000)
    if (-not $exited) {
        Write-Fail "instance #2 did not exit within 10 seconds (duplicate instance is still waiting for input)"
        try {
            $second.Process.Kill()
            $second.Process.WaitForExit(5000) | Out-Null
        }
        catch {
        }
        $secondOutput = Get-InstanceOutput -Instance $second
        Write-Host "--- instance #2 combined output ---"
        Write-Host $secondOutput.Combined
    }
    else {
        $second.Process.WaitForExit()
        $secondOutput = Get-InstanceOutput -Instance $second
        Write-Host ("instance #2 exit code: " + $second.Process.ExitCode)
        Write-Host "--- instance #2 combined output ---"
        Write-Host $secondOutput.Combined

        if ($second.Process.ExitCode -eq 0) {
            Write-Pass "instance #2 exited with exit code 0"
        }
        else {
            Write-Fail ("instance #2 exited with non-zero exit code " + $second.Process.ExitCode)
        }

        if ($secondOutput.Combined.IndexOf("already running", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Write-Pass "instance #2 output contains 'already running'"
        }
        else {
            Write-Fail "instance #2 output does not contain 'already running'"
        }
    }
}
catch {
    Write-Fail ("test error: " + $_.Exception.Message)
}
finally {
    foreach ($instance in $script:Instances) {
        if ($null -eq $instance) {
            continue
        }

        try {
            if (-not $instance.Process.HasExited) {
                $instance.Process.Kill()
                $instance.Process.WaitForExit(5000) | Out-Null
            }
        }
        catch {
        }

        try {
            $instance.Process.Dispose()
        }
        catch {
        }
    }

    Remove-TempDirectory -Path $script:TempDir
}

if ($script:Failed) {
    Write-Host "RESULT: FAIL"
    exit 1
}

Write-Host "RESULT: PASS"
exit 0
