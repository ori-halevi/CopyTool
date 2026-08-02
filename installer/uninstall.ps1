<#
.SYNOPSIS
    Removes CopyTool completely.

.DESCRIPTION
    Unregisters the shell extension from both hives, stops the host and deletes
    the installed files. Because nothing was ever installed as a service or a
    scheduled task, there is nothing else to undo.

    Job files and the log under %LOCALAPPDATA%\CopyTool are kept unless -Purge is
    given: an unfinished job is still recoverable, and silently discarding one
    would be the wrong default for an uninstaller.
#>
[CmdletBinding()]
param(
    [switch] $Purge,
    [switch] $NoRestartExplorer
)

$ErrorActionPreference = 'Continue'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\common.ps1')

$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'CopyTool\bin')
    (Join-Path $env:ProgramFiles 'CopyTool')
) | Where-Object { Test-Path $_ }

Stop-CopyToolProcesses | Out-Null

foreach ($dir in $candidates) {
    $dll = Join-Path $dir 'CopyTool.ShellExt.dll'
    if (Test-Path $dll) {
        Write-Host "Unregistering $dll"
        Invoke-RegSvr32 -Dll $dll -Unregister -IgnoreErrors | Out-Null
    }
}

if (-not $NoRestartExplorer) { Restart-Explorer }

foreach ($dir in $candidates) {
    Write-Host "Removing $dir"
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $dir) { Write-Warning "could not remove $dir (still in use?)" }
}

foreach ($key in @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CopyTool'
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CopyTool')) {
    if (Test-Path $key) { Remove-Item $key -Recurse -Force -ErrorAction SilentlyContinue }
}

# Belt and braces: DllUnregisterServer already clears these, but it cannot run if
# the DLL was deleted by hand.
$clsid = Get-CopyToolClsid
foreach ($hive in 'HKCU:', 'HKLM:') {
    foreach ($key in (Get-CopyToolDragDropKeys $hive)) {
        if (Test-Path $key) { Remove-Item $key -Recurse -Force -ErrorAction SilentlyContinue }
    }
    $k = "$hive\Software\Classes\CLSID\$clsid"
    if (Test-Path $k) { Remove-Item $k -Recurse -Force -ErrorAction SilentlyContinue }
}

$data = Join-Path $env:LOCALAPPDATA 'CopyTool'
if ($Purge) {
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Removed logs and pending jobs as well.'
} elseif (Test-Path $data) {
    $pending = @(Get-ChildItem (Join-Path $data 'jobs') -Filter '*.json' -ErrorAction SilentlyContinue).Count
    Write-Host ''
    Write-Host "Kept $data (log$(if ($pending) { ", $pending unfinished job(s)" })). Use -Purge to delete it."
}

Write-Host ''
Write-Host 'Uninstalled.' -ForegroundColor Green
