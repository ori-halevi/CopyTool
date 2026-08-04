<#
.SYNOPSIS
    Installs CopyTool.

.DESCRIPTION
    Per-user by default, into %LOCALAPPDATA%\CopyTool — no administrator rights,
    no service, no scheduled task, nothing left running. That is the whole point
    of the design: the only thing that persists is a registry entry and a folder.

    -AllUsers installs into Program Files and registers machine-wide, which does
    require elevation. It is optional and buys nothing except availability to
    other accounts on the machine.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -AllUsers
    .\install.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $AllUsers,
    [switch] $NoRestartExplorer
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $repoRoot 'scripts\common.ps1')

$source = Join-Path $repoRoot "build\$Configuration"

if (-not (Test-Path (Join-Path $source 'CopyTool.ShellExt.dll'))) {
    throw "Nothing built at $source. Run build.ps1 first."
}

$isElevated = Test-Elevated

if ($AllUsers -and -not $isElevated) {
    throw 'Installing for all users needs an elevated PowerShell. Omit -AllUsers for a per-user install.'
}
if (-not $AllUsers -and $isElevated) {
    # regsvr32 would then write HKLM and the per-user install would silently
    # become machine-wide, which is not what was asked for.
    Write-Warning 'Running elevated without -AllUsers: registration will land in HKLM, not HKCU.'
}

$target = if ($AllUsers) { Join-Path $env:ProgramFiles 'CopyTool' }
          else           { Join-Path $env:LOCALAPPDATA 'CopyTool\bin' }

Write-Host "Installing to $target" -ForegroundColor Cyan

# A previously registered DLL is locked by Explorer; unregister and release first.
$installedDll = Join-Path $target 'CopyTool.ShellExt.dll'
if (Test-Path $installedDll) {
    Write-Host '  unregistering the previous version...'
    Invoke-RegSvr32 -Dll $installedDll -Unregister -IgnoreErrors | Out-Null
}
Stop-CopyToolProcesses | Out-Null

if (-not $NoRestartExplorer) { Restart-Explorer }

New-Item -ItemType Directory -Path $target -Force | Out-Null

$payload = @(
    'CopyTool.ShellExt.dll'
    'CopyTool.Host.exe', 'CopyTool.Host.dll', 'CopyTool.Host.runtimeconfig.json', 'CopyTool.Host.deps.json'
    'CopyTool.Elevated.exe', 'CopyTool.Elevated.dll', 'CopyTool.Elevated.runtimeconfig.json', 'CopyTool.Elevated.deps.json'
    'CopyTool.Core.dll'
)
$missing = @()
foreach ($file in $payload) {
    $from = Join-Path $source $file
    if (Test-Path $from) { Copy-Item $from $target -Force }
    else { $missing += $file }
}
if ($missing) {
    # Registering a half-copied install produces a shell entry that fails at the
    # moment of use, which is far harder to diagnose than refusing here.
    throw "Missing from the build output: $($missing -join ', '). Run build.ps1 first."
}

# The uninstaller has to survive the repo being moved or deleted. It goes to the
# data directory rather than into $target, because $target is what it deletes —
# a script that removes the folder it is running from is asking for trouble.
$dataDir = Join-Path $env:LOCALAPPDATA 'CopyTool'
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1')   $dataDir -Force
Copy-Item (Join-Path $repoRoot 'scripts\common.ps1')  $dataDir -Force

Write-Host '  registering the drag-drop handler...'
Invoke-RegSvr32 -Dll $installedDll | Out-Null

# Uninstall entry, in the hive that matches the install scope.
$uninstallRoot = if ($AllUsers) { 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CopyTool' }
                 else           { 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CopyTool' }
New-Item -Path $uninstallRoot -Force | Out-Null
Set-ItemProperty $uninstallRoot 'DisplayName'     'CopyTool'
Set-ItemProperty $uninstallRoot 'DisplayVersion'  '0.1.0'
Set-ItemProperty $uninstallRoot 'Publisher'       'Ori Halevi'
Set-ItemProperty $uninstallRoot 'InstallLocation' $target
Set-ItemProperty $uninstallRoot 'NoModify'        1 -Type DWord
Set-ItemProperty $uninstallRoot 'NoRepair'        1 -Type DWord
Set-ItemProperty $uninstallRoot 'UninstallString' `
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $dataDir 'uninstall.ps1')`""

$hive   = if ($AllUsers) { 'HKLM:' } else { 'HKCU:' }
$hooked = Get-CopyToolDragDropKeys $hive |
          Where-Object { Test-Path $_ } |
          ForEach-Object { ($_ -split '\\')[3] }

Write-Host ''
Write-Host 'Installed.' -ForegroundColor Green
Write-Host "  location  $target"
Write-Host "  scope     $(if ($AllUsers) { 'all users (HKLM)' } else { 'current user (HKCU)' })"
Write-Host "  hooked    $($hooked -join ', ')"
Write-Host ''
Write-Host 'Right-drag a file or folder onto another folder to use it.'

if (-not $NoRestartExplorer) { Restart-Explorer }
