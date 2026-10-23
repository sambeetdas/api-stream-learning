<#
.SYNOPSIS
  Run or debug the SSE demo API.

.DESCRIPTION
  Run    - build and run (Ctrl+C to stop).
  Watch  - run with `dotnet watch`: rebuilds and restarts when you edit a file.
  Debug  - build a Debug configuration, start the app in Development, and print its PID so
           you can attach a debugger (VS Code: "Attach to running app", Visual Studio:
           Debug > Attach to Process). Add -WaitForDebugger to pause startup until one attaches,
           which lets you hit breakpoints in Program.cs.

.EXAMPLE
  ./run.ps1
  ./run.ps1 -Mode Watch -Open
  ./run.ps1 -Mode Debug -WaitForDebugger
#>
[CmdletBinding()]
param(
    [ValidateSet('Run', 'Watch', 'Debug')]
    [string]$Mode = 'Run',

    [int]$Port = 5099,

    # Open the demo page in the default browser once the app is up.
    [switch]$Open,

    # Debug mode only: block startup until a debugger attaches.
    [switch]$WaitForDebugger
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$url = "http://localhost:$Port"
$env:ASPNETCORE_ENVIRONMENT = 'Development'

function Open-WhenReady {
    if (-not $Open) { return }
    # Poll in the background so we do not block the app's output.
    Start-Job -ArgumentList $url -ScriptBlock {
        param($url)
        for ($i = 0; $i -lt 60; $i++) {
            try { Invoke-WebRequest "$url/stats" -UseBasicParsing -TimeoutSec 2 | Out-Null; Start-Process $url; return }
            catch { Start-Sleep -Milliseconds 500 }
        }
    } | Out-Null
}

# A previous run of THIS app that is still alive locks bin\SseApi.exe (the build then fails with
# "file is locked by SseApi") and holds the port. Stop it: starting again means you want the new one.
$binDir = Join-Path $PSScriptRoot 'bin'
$stale = Get-CimInstance Win32_Process | Where-Object {
    ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($binDir, [StringComparison]::OrdinalIgnoreCase)) -or
    ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -and $_.CommandLine.Contains($binDir) -and $_.CommandLine.Contains('SseApi.dll'))
}
foreach ($s in $stale) {
    Write-Host "Stopping previous instance of this app (PID $($s.ProcessId))" -ForegroundColor Yellow
    Stop-Process -Id $s.ProcessId -Force -ErrorAction SilentlyContinue
}
if ($stale) { Start-Sleep -Milliseconds 500 }

# Anything else on the port is not ours: leave it alone.
$existing = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($existing) {
    $pids = $existing.OwningProcess | Sort-Object -Unique
    Write-Warning "Port $Port is in use by another program (PID $($pids -join ', ')). Stop it or pass -Port <other>."
    exit 1
}

switch ($Mode) {
    'Run' {
        Write-Host "Running on $url (Ctrl+C to stop)" -ForegroundColor Green
        Open-WhenReady
        dotnet run --urls $url
    }

    'Watch' {
        Write-Host "Watching for changes on $url (Ctrl+C to stop)" -ForegroundColor Green
        Open-WhenReady
        dotnet watch run --urls $url
    }

    'Debug' {
        Write-Host 'Building (Debug)...' -ForegroundColor Cyan
        dotnet build -c Debug --nologo -v q
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        $dll = Get-ChildItem -Path (Join-Path $PSScriptRoot 'bin/Debug') -Filter 'SseApi.dll' -Recurse |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $dll) { throw 'SseApi.dll not found after build.' }

        if ($WaitForDebugger) { $env:WAIT_FOR_DEBUGGER = '1' }

        $proc = Start-Process dotnet -ArgumentList "`"$($dll.FullName)`"", '--urls', $url `
                    -NoNewWindow -PassThru
        Write-Host ''
        Write-Host "App started on $url, PID $($proc.Id)" -ForegroundColor Green
        Write-Host '  VS Code : Run and Debug > "Attach to running app" > choose SseApi / this PID'
        Write-Host '  VS 2022 : Debug > Attach to Process > select this PID'
        if ($WaitForDebugger) { Write-Host '  Startup is paused until a debugger attaches.' -ForegroundColor Yellow }
        Write-Host '  Press Ctrl+C to stop.'
        Write-Host ''

        Open-WhenReady
        try { Wait-Process -Id $proc.Id }
        finally {
            if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
        }
    }
}
