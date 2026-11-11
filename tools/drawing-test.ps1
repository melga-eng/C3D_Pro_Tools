<#
.SYNOPSIS
    Runs plugin commands on a COPY of a drawing in a background Civil 3D (accoreconsole), with no window.

.DESCRIPTION
    The drawing you name is copied to a temp folder first; the original and any open Civil 3D session are never touched.
    The plugin DLLs are copied too, so the build output stays unlocked. The script file lists the commands to run, one
    per line, exactly as you would type them (answers to prompts on the following lines). The plugin is loaded for you.

    The test passes when the run finishes in time, every -Expect text appears in the log, and no -Reject text does.
    Commands that open a form or ask the user to pick objects on screen cannot run here; they stop the run at the
    timeout. Use a second Civil 3D window for those.

.EXAMPLE
    .\tools\drawing-test.ps1 -Drawing "C:\Users\melga\Desktop\temp\Test.dwg" -Script tests\drawing\scene-report.scr -Expect "Sheet scene"

.EXAMPLE
    .\tools\drawing-test.ps1 -Drawing D:\work\copy.dwg -Script my.scr -KeepResult
    Keeps the modified copy and prints its path, so the result can be opened and looked at.
#>
param(
    [Parameter(Mandatory)] [string]$Drawing,
    [Parameter(Mandatory)] [string]$Script,
    [string[]]$Expect = @(),
    [string[]]$Reject = @('Unhandled', 'failed:', 'Unknown command'),
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Debug',
    [int]$TimeoutSeconds = 300,
    [switch]$KeepResult
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$acadDir = 'C:\Program Files\Autodesk\AutoCAD 2026'
$console = Join-Path $acadDir 'accoreconsole.exe'
$pluginOut = Join-Path $repo "C3D_Pro_Tools\bin\$Configuration\net10.0-windows"

foreach ($p in @($console, $Drawing, $Script, (Join-Path $pluginOut 'C3D_Pro_Tools.dll'))) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Error "Not found: $p"; exit 2 }
}

# Everything runs in a fresh temp folder: the drawing copy, the plugin copy, the log.
$run = Join-Path ([IO.Path]::GetTempPath()) ("c3dpro-test-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$run\bin", "$run\iso" | Out-Null
$dwg = Join-Path $run 'test.dwg'
Copy-Item -LiteralPath $Drawing $dwg
Copy-Item "$pluginOut\*" "$run\bin" -Recurse

# A project settings file beside the drawing travels with it.
$settings = Join-Path (Split-Path $Drawing -Parent) 'C3DPro.project.json'
if (Test-Path -LiteralPath $settings) { Copy-Item -LiteralPath $settings $run }

$dll = (Join-Path $run 'bin\C3D_Pro_Tools.dll') -replace '\\', '/'
$lines = @('SECURELOAD', '0', 'NETLOAD', "`"$dll`"")
$lines += Get-Content -LiteralPath $Script
if ($KeepResult) { $lines += @('_QSAVE') }
# The marker is joined at run time, so the echoed command line itself never contains it.
$marker = 'C3DPRO-TEST-END'
$lines += @('(princ (strcat "\n" "C3DPRO-" "TEST-END"))', '_QUIT', '_Y')
$scr = Join-Path $run 'run.scr'
Set-Content -LiteralPath $scr -Value $lines -Encoding ascii

$log = Join-Path $run 'log.txt'
$arguments = "/i `"$dwg`" /s `"$scr`" /loadmodule `"$acadDir\AecBase.dbx`" /product C3D /l en-US /isolate c3dprotest `"$run\iso`""
$clock = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $console -ArgumentList $arguments -RedirectStandardOutput $log `
    -RedirectStandardError "$run\err.txt" -WindowStyle Hidden -PassThru
$null = $proc.Handle   # keeps the handle so ExitCode is readable after the process ends

$timedOut = -not $proc.WaitForExit($TimeoutSeconds * 1000)
if ($timedOut) { $proc.Kill(); $proc.WaitForExit() }
$seconds = [int]$clock.Elapsed.TotalSeconds

$text = (Get-Content -LiteralPath $log -Encoding Unicode -ErrorAction SilentlyContinue) -join "`n"
$errText = (Get-Content -LiteralPath "$run\err.txt" -ErrorAction SilentlyContinue) -join "`n"
$problems = @()
if ($timedOut) {
    $problems += "Stopped at the $TimeoutSeconds s timeout (a command is waiting for a form or a pick, or Civil 3D hung)."
}
elseif ($proc.ExitCode -ne 0) {
    $problems += "Civil 3D console exited with code $($proc.ExitCode)."
}
if ($text -notmatch "(?m)^\s*$marker") {
    $problems += 'The script did not reach its end (completion marker missing).'
}
if ($errText.Trim()) { $problems += "Error output: $($errText.Trim())" }
foreach ($e in $Expect) { if ($text -notmatch [regex]::Escape($e)) { $problems += "Expected text missing: $e" } }
foreach ($r in $Reject) { if ($text -match [regex]::Escape($r)) { $problems += "Unwanted text found: $r" } }

# Show what the commands printed, without the Civil 3D loading noise.
$text -split "`n" |
    Where-Object { $_.Trim() -and $_ -notmatch '^(Loading |CoreHeartBeat|AcCoreConsole|Redirect stdout)' } |
    Select-Object -Last 60 | ForEach-Object { Write-Host $_ }

Write-Host ''
if ($problems.Count -eq 0) {
    Write-Host "PASS  ($seconds s)  $(Split-Path $Script -Leaf) on $(Split-Path $Drawing -Leaf)" -ForegroundColor Green
} else {
    Write-Host "FAIL  ($seconds s)  $(Split-Path $Script -Leaf) on $(Split-Path $Drawing -Leaf)" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  - $_" }
}
Write-Host "Log: $log"
if ($KeepResult) { Write-Host "Result drawing: $dwg" }
elseif ($problems.Count -eq 0) { Remove-Item -LiteralPath $dwg -ErrorAction SilentlyContinue }

exit ([int]($problems.Count -gt 0))
