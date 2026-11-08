<#
.SYNOPSIS
    Builds the C3D Bridge and installs it as a per-user Civil 3D 2026 bundle, so it loads every time Civil 3D starts
    and agents are always connected to the live session.

.DESCRIPTION
    Copies the build output to %APPDATA%\Autodesk\ApplicationPlugins\C3DProTools.Bridge.bundle. Civil 3D locks the DLL
    while it runs, so close Civil 3D first (the script stops if the DLL is in use). Use -Uninstall to remove it.
    For a quick try without installing, NETLOAD C3DProTools.Bridge\bin\Debug\net10.0-windows\C3DProTools.Bridge.dll.

.EXAMPLE
    .\C3DProTools.Bridge\Install-Bridge.ps1

.EXAMPLE
    .\C3DProTools.Bridge\Install-Bridge.ps1 -Uninstall
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$bundle = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\C3DProTools.Bridge.bundle'

if ($Uninstall) {
    if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force; Write-Host "Removed $bundle" }
    else { Write-Host 'C3D Bridge is not installed.' }
    exit 0
}

$project = Join-Path $PSScriptRoot 'C3DProTools.Bridge.csproj'
& dotnet build $project -c $Configuration -nologo -v:m
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$out = Join-Path $PSScriptRoot "bin\$Configuration\net10.0-windows"
$contents = Join-Path $bundle 'Contents'
New-Item -ItemType Directory -Force $contents | Out-Null

try {
    Copy-Item (Join-Path $out '*') $contents -Recurse -Force
}
catch {
    Write-Error "Could not copy the bridge files (is Civil 3D running? Close it and run this again). $_"
    exit 1
}

@'
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="AutoCAD" ProductType="Application" AppVersion="0.1.0"
                    Name="C3D Pro Tools Bridge" Description="MCP bridge from AI agents to the live Civil 3D session" Author="Mohamed Elgarhy">
	<CompanyDetails Name="Mohamed Elgarhy" />
	<Components>
		<RuntimeRequirements OS="Win64" Platform="Civil3D" SeriesMin="R25.1" SeriesMax="R25.1" />
		<ComponentEntry AppName="C3DProToolsBridge" ModuleName="./Contents/C3DProTools.Bridge.dll"
		                AppDescription="C3D Bridge (http://127.0.0.1:47321/mcp)" LoadOnCommandInvocation="False" LoadOnAutoCADStartup="True" />
	</Components>
</ApplicationPackage>
'@ | Set-Content -Path (Join-Path $bundle 'PackageContents.xml') -Encoding utf8

Write-Host "Installed to $bundle. Start Civil 3D 2026: the command line shows 'C3D Bridge: listening ...'."
