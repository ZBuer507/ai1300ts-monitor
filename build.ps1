[CmdletBinding()]
param(
    [switch]$Test,
    [switch]$Deploy,
    [string]$DeployRoot,
    [switch]$NoHardware
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$assets = Join-Path $src 'assets'
$tests = Join-Path $root 'tests'
$artifacts = Join-Path $root 'artifacts'
$products = Join-Path $artifacts 'products'
$testsOut = Join-Path $artifacts 'tests'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "Missing .NET Framework 4.x csc.exe: $csc" }
foreach ($dir in @($artifacts, $products, $testsOut)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$psuIcon = Join-Path $assets 'PsuMonitor.ico'
$psuManifest = Join-Path $assets 'PsuMonitor.manifest'
$gpuIcon = Join-Path $assets 'GpuMonitor.ico'
$gpuManifest = Join-Path $assets 'GpuMonitor.manifest'

$psuSources = @(
    'PsuMonitor.cs', 'PsuTrayHover.cs', 'PsuTelemetry.cs', 'PsuHidTransport.cs', 'PsuStartup.cs'
) | ForEach-Object { Join-Path $src $_ }
$gpuSources = @($psuSources) + (@(
    'MonitorGpuLevelPanel.cs', 'GpuLevelSense.cs', 'MonitorTrayPopup.cs'
) | ForEach-Object { Join-Path $src $_ })

function Invoke-Csc {
    param(
        [Parameter(Mandatory = $true)][string]$Out,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string[]]$Sources,
        [string[]]$Extra = @()
    )
    $arguments = @('/nologo', '/platform:x64', '/optimize+', "/target:$Target", "/out:$Out") + $Extra + $Sources
    Write-Host ("  csc -> " + (Split-Path -Leaf $Out))
    & $csc @arguments
    if ($LASTEXITCODE -ne 0) { throw ("compile failed: $Out (exit $LASTEXITCODE)") }
}

Write-Host 'Build products:'
$psuExe = Join-Path $products 'Ai1300TS-PsuMonitor.exe'
$gpuExe = Join-Path $products 'Ai1300TS-GpuMonitor.exe'
Invoke-Csc -Out $psuExe -Target 'winexe' -Sources $psuSources -Extra @(
    "/win32icon:$psuIcon",
    "/win32manifest:$psuManifest"
)
Invoke-Csc -Out $gpuExe -Target 'winexe' -Sources $gpuSources -Extra @(
    '/define:GPU_LEVEL',
    "/win32icon:$gpuIcon",
    "/win32manifest:$gpuManifest"
)

Write-Host 'Build test harnesses:'
Invoke-Csc -Out (Join-Path $testsOut 'PsuTelemetrySmoke.exe') -Target 'exe' -Sources @(
    (Join-Path $src 'PsuTelemetry.cs'), (Join-Path $tests 'PsuTelemetrySmoke.cs'))
Invoke-Csc -Out (Join-Path $testsOut 'GpuLevelSenseSmoke.exe') -Target 'exe' -Sources @(
    (Join-Path $src 'GpuLevelSense.cs'), (Join-Path $tests 'GpuLevelSenseSmoke.cs'))
Invoke-Csc -Out (Join-Path $testsOut 'PsuCancellationAudit.exe') -Target 'exe' -Sources @(
    (Join-Path $src 'PsuHidTransport.cs'), (Join-Path $tests 'PsuCancellationAudit.cs'))
Invoke-Csc -Out (Join-Path $testsOut 'PsuDesktopSmoke.exe') -Target 'winexe' `
    -Sources @($psuSources + (Join-Path $tests 'PsuDesktopSmoke.cs')) -Extra @(
        '/main:PsuDesktopSmoke', "/win32manifest:$psuManifest")
Invoke-Csc -Out (Join-Path $testsOut 'GpuMonitorTraySmoke.exe') -Target 'winexe' `
    -Sources @($gpuSources + (Join-Path $tests 'GpuMonitorTraySmoke.cs')) -Extra @(
        '/define:GPU_LEVEL', '/main:GpuMonitorTraySmoke', "/win32manifest:$gpuManifest")

$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
$info = @("built $stamp")
foreach ($exe in @($psuExe, $gpuExe)) {
    $item = Get-Item -LiteralPath $exe
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    $info += ("{0}  {1}  {2} bytes  sha256 {3}" -f
        (Split-Path -Leaf $exe), $item.VersionInfo.FileVersion, $item.Length, $hash)
    Write-Host ("  " + (Split-Path -Leaf $exe) + "  " + $item.VersionInfo.FileVersion)
}
Set-Content -LiteralPath (Join-Path $artifacts 'build-info.txt') -Value $info -Encoding utf8

$failures = @()
if ($Test) {
    Write-Host 'Run tests:'
    foreach ($console in @('PsuTelemetrySmoke.exe', 'GpuLevelSenseSmoke.exe', 'PsuCancellationAudit.exe')) {
        $exe = Join-Path $testsOut $console
        Write-Host "  $console"
        & $exe
        if ($LASTEXITCODE -ne 0) { $failures += "$console exit code $LASTEXITCODE" }
    }

    if ($NoHardware) {
        Write-Host '  skip GUI smokes (-NoHardware: needs local PSU/GPU and a desktop)'
    }
    else {
        $smokes = @(
            @{ Exe = 'PsuDesktopSmoke.exe'; Report = 'psu-desktop-smoke.txt' },
            @{ Exe = 'GpuMonitorTraySmoke.exe'; Report = 'gpu-monitor-tray-smoke.txt' }
        )
        foreach ($smoke in $smokes) {
            $exe = Join-Path $testsOut $smoke.Exe
            $report = Join-Path $testsOut $smoke.Report
            Remove-Item -LiteralPath $report -ErrorAction SilentlyContinue
            Write-Host "  " + $smoke.Exe
            $process = Start-Process -FilePath $exe -PassThru
            if (-not $process.WaitForExit(60000)) {
                $process.Kill()
                $failures += $smoke.Exe + " did not exit within 60 s"
                continue
            }
            if (-not (Test-Path -LiteralPath $report)) {
                $failures += $smoke.Exe + " did not write " + $smoke.Report
                continue
            }
            $first = Get-Content -LiteralPath $report -TotalCount 1
            Write-Host "    $first"
            if ($first -notlike 'PASS*') { $failures += $smoke.Exe + ": " + $first }
        }
    }
}

if ($Deploy) {
    if (-not $DeployRoot) { throw 'Deploy requires -DeployRoot <directory>.' }
    if (-not (Test-Path -LiteralPath $DeployRoot)) { throw "Deploy directory missing: $DeployRoot" }
    $psuTarget = Join-Path $DeployRoot 'Ai1300TS-PsuMonitor.exe'
    $gpuTarget = Join-Path $DeployRoot 'Ai1300TS-GpuMonitor.exe'
    $targets = @($psuTarget, $gpuTarget)
    Write-Host "Deploy to ${DeployRoot}:"
    Get-Process -Name 'Ai1300TS-PsuMonitor', 'Ai1300TS-GpuMonitor' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ($targets -contains $_.Path) } |
        ForEach-Object {
            Write-Host ("  stop running process {0} {1}" -f $_.Id, $_.Path)
            Stop-Process -Id $_.Id -Force
            $null = $_.WaitForExit(5000)
        }
    Copy-Item -LiteralPath $psuExe -Destination $psuTarget -Force
    Copy-Item -LiteralPath $gpuExe -Destination $gpuTarget -Force
    Write-Host "  wrote $psuTarget"
    Write-Host "  wrote $gpuTarget"
    Start-Process -FilePath $gpuTarget -ArgumentList '--background' -WorkingDirectory $DeployRoot
    Start-Sleep -Seconds 4
    $running = @(Get-Process -Name 'Ai1300TS-GpuMonitor' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $gpuTarget })
    if ($running.Count -eq 0) { throw "Monitor did not start after deploy: $gpuTarget" }
    if ($running[0].MainWindowHandle -ne 0) { throw "Monitor shows a main window after deploy." }
    Write-Host ("  started hidden, PID " + $running[0].Id)
}

if ($failures.Count -gt 0) {
    Write-Host 'FAILURES:'
    foreach ($failure in $failures) { Write-Host "  - $failure" }
    exit 1
}
Write-Host 'Done.'
