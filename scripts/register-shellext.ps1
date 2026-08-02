<#
.SYNOPSIS
    Registers (or unregisters) the CopyTool drag-drop handler for the current user.

.DESCRIPTION
    Writes to HKCU\Software\Classes only — no administrator rights needed.
    Explorer caches shell extensions, so it is restarted afterwards.

.EXAMPLE
    .\register-shellext.ps1
    .\register-shellext.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $Unregister,
    [switch] $NoRestartExplorer
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot = Split-Path $PSScriptRoot -Parent
$dll      = Join-Path $repoRoot "build\$Configuration\CopyTool.ShellExt.dll"

if (-not (Test-Path $dll)) { throw "DLL not found: $dll`nRun build-shellext.ps1 first." }

# DllRegisterServer picks its hive from the token: elevated goes machine-wide,
# otherwise per-user. Do NOT run this elevated unless that is what you want.
$verb = if ($Unregister) { 'Unregistering' } else { 'Registering' }
Write-Host "$verb $dll" -ForegroundColor Cyan
Invoke-RegSvr32 -Dll $dll -Unregister:$Unregister | Out-Null

# Verify the registry actually reflects what we asked for.
$clsid   = Get-CopyToolClsid
$hooks   = Get-CopyToolDragDropKeys 'HKCU:'
$present = $hooks | Where-Object { Test-Path $_ }

if ($Unregister) {
    if ($present) { throw "Still registered: $($present -join ', ')" }
    Write-Host 'Unregistered.' -ForegroundColor Green
} else {
    if ($present.Count -ne $hooks.Count) { throw 'Registration did not take effect.' }
    $inproc = "HKCU:\Software\Classes\CLSID\$clsid\InprocServer32"
    Write-Host 'Registered:' -ForegroundColor Green
    Write-Host "  InprocServer32 = $((Get-ItemProperty $inproc).'(default)')"
    $hooks | ForEach-Object { Write-Host "  $_" }
}

if (-not $NoRestartExplorer) { Restart-Explorer }
