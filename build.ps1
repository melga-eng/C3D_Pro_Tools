<#
.SYNOPSIS
    Builds and tests C3D Pro Tools: the Civil 3D plugin, the Navisworks add-in, the tests and the WiX v5 installer.

.DESCRIPTION
    Everything builds with the .NET SDK (`dotnet`): the installer is an SDK-style WiX v5 project (WixToolset.Sdk from
    NuGet), so neither Visual Studio's MSBuild nor a WiX install is needed. The tests run after the build; a failing test
    fails the script. The MSI lands in C3DProInstaller\bin\<Configuration>\C3DProInstaller.msi and installs the
    C3DProTools.bundle (ApplicationPlugins) and the Navisworks add-in, as before. Used by the tasks in .vscode/tasks.json.

.EXAMPLE
    .\build.ps1 -Configuration Release

.EXAMPLE
    .\build.ps1 -Rebuild
    Cleans first and builds everything again, so the MSI is regenerated even when nothing changed.

.EXAMPLE
    .\build.ps1 -SkipTests
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Rebuild,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'The .NET SDK (dotnet) was not found. Install the .NET 10 SDK.'
    exit 1
}

$solution = Join-Path $PSScriptRoot 'C3D_Pro_Tools.slnx'

if ($Rebuild) {
    & dotnet clean $solution -c $Configuration -nologo -v:m
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$buildArgs = @('build', $solution, '-c', $Configuration, '-nologo', '-v:m')
if ($Rebuild) { $buildArgs += '--no-incremental' }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $SkipTests) {
    & dotnet test (Join-Path $PSScriptRoot 'tests\C3D_Pro_Tools.Tests\C3D_Pro_Tools.Tests.csproj') -c $Configuration --no-build -nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$msi = Join-Path $PSScriptRoot "C3DProInstaller\bin\$Configuration\C3DProInstaller.msi"
if (Test-Path $msi) { Write-Host "Installer: $msi" }

# The public installer: the same code built as the public edition (only the released tools; see C3D_Pro_Tools\Edition.cs),
# then its own MSI. Built every time, so the two editions never drift apart.
& dotnet build (Join-Path $PSScriptRoot 'C3D_Pro_Tools\C3D_Pro_Tools.csproj') -c $Configuration -p:Edition=Public -nologo -v:m
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet build (Join-Path $PSScriptRoot 'C3DProInstaller.Public\C3DProInstaller.Public.wixproj') -c $Configuration -nologo -v:m
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$publicMsi = Join-Path $PSScriptRoot "C3DProInstaller.Public\bin\$Configuration\C3DProTools-Public.msi"
if (Test-Path $publicMsi) { Write-Host "Public installer: $publicMsi" }
exit 0
