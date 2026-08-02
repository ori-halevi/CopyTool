<#
.SYNOPSIS
    Builds the whole solution — native and managed — in one pass.

.DESCRIPTION
    Visual Studio's MSBuild is used rather than the dotnet CLI, because the CLI
    cannot import the C++ targets that CopyTool.ShellExt.vcxproj needs (MSB4278).
    The .NET projects target net9.0 specifically so that VS 2022's MSBuild can
    build them too — SDK 10.x would require MSBuild 18+, which VS 2022 does not
    ship, and that would split the build across two toolchains.

    Everything lands in build\<Configuration>\, where CopyTool.Host.exe sits next
    to CopyTool.ShellExt.dll — the handler starts the host from its own directory.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug -RestartExplorer
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    # Explorer keeps the shell extension locked; releasing it lets the link step succeed.
    [switch] $RestartExplorer
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

# A running host locks CopyTool.Host.exe and the build fails with MSB3027.
Stop-CopyToolProcesses | Out-Null

if ($RestartExplorer) { Restart-Explorer }

$msbuild = Get-MsBuildPath

& $msbuild (Join-Path $repoRoot 'CopyTool.slnx') `
    /p:Configuration=$Configuration /p:Platform=x64 /restore /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

Write-Host ''
Write-Host "Output: $repoRoot\build\$Configuration" -ForegroundColor Green
Get-ChildItem (Join-Path $repoRoot "build\$Configuration") -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'CopyTool*' -and $_.Extension -in '.exe', '.dll' } |
    Select-Object Name, @{ N = 'Size'; E = { '{0:N0} KB' -f ($_.Length / 1KB) } } |
    Format-Table -AutoSize
