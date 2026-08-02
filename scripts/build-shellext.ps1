<#
.SYNOPSIS
    Builds CopyTool.ShellExt.dll (x64).

.DESCRIPTION
    Explorer keeps a loaded shell extension locked, so a rebuild fails with
    LNK1168 while the DLL is in use. -RestartExplorer releases it first.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $RestartExplorer
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot = Split-Path $PSScriptRoot -Parent
$project  = Join-Path $repoRoot 'src\CopyTool.ShellExt\CopyTool.ShellExt.vcxproj'

if ($RestartExplorer) { Restart-Explorer }

$msbuild = Get-MsBuildPath
& $msbuild $project /p:Configuration=$Configuration /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

$dll = Join-Path $repoRoot "build\$Configuration\CopyTool.ShellExt.dll"
Write-Host "`nBuilt: $dll" -ForegroundColor Green
Get-Item $dll | Format-List Name, Length, LastWriteTime
