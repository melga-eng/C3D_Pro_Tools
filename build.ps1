<#
.SYNOPSIS
    Builds the C3D Pro Tools plugin and its WiX v3 installer with Visual Studio's MSBuild.

.DESCRIPTION
    The WiX v3 installer project cannot be built by the `dotnet` CLI (which VS Code uses by default),
    so this script finds Visual Studio's own MSBuild.exe with vswhere and builds C3D_Pro_Tools.slnx,
    exactly as Visual Studio does. Used by the tasks in .vscode/tasks.json.

.EXAMPLE
    .\build.ps1 -Configuration Release

.EXAMPLE
    .\build.ps1 -Configuration Release -Rebuild
    Cleans and rebuilds everything, so the MSI is regenerated even when MSBuild considers it up to date.
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Rebuild
)

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    Write-Error 'vswhere.exe not found. Install Visual Studio (2026 or later for .NET 10 projects).'
    exit 1
}

# -latest picks the newest Visual Studio; .NET 10 projects need Visual Studio 2026 (MSBuild 18) or later.
$msbuild = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    Write-Error 'Visual Studio MSBuild.exe not found.'
    exit 1
}

Write-Host "Using $msbuild"
$solution = Join-Path $PSScriptRoot 'C3D_Pro_Tools.slnx'

# Clean and Build run as separate passes: a single -t:Rebuild lets the installer's Clean also clean the plugin
# project after it was built, leaving C3D_Pro_Tools.dll deleted.
if ($Rebuild) {
    & $msbuild $solution -t:Clean "-p:Configuration=$Configuration" -nologo -v:m
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& $msbuild $solution -restore -t:Build "-p:Configuration=$Configuration" -nologo -v:m
exit $LASTEXITCODE
