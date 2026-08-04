; ===========================================================================
;  CopyTool - Inno Setup script
;
;  Produces build\CopyTool-Setup.exe: a double-clickable installer that needs
;  no administrator rights, matching the design rule the whole project is built
;  on. PrivilegesRequired=lowest is what enforces that - it also makes regsvr32
;  land in HKCU rather than HKLM, which is the difference between a per-user
;  install and a machine-wide one.
;
;  Explorer keeps a registered shell extension loaded and locked, so it is
;  restarted before the files are written and again after they are registered.
;  That is unavoidable: without it the upgrade path fails on a locked DLL.
;
;  Build with installer\pack.ps1 -Inno (which finds ISCC and checks the payload).
; ===========================================================================

#define AppName        "CopyTool"
#define AppPublisher   "Ori Halevi"
#define ShellExtDll    "CopyTool.ShellExt.dll"

#ifndef AppVersion
  #define AppVersion   "0.1.0"
#endif
#ifndef BuildDir
  #define BuildDir     "..\build\Release"
#endif
#ifndef OutputName
  #define OutputName   "CopyTool-Setup"
#endif

; /DStandalone builds the variant that carries the .NET runtime with it: hundreds
; of files instead of ten, and no prerequisite to check for.

[Setup]
; Stable and distinct from the shell extension's CLSID: this one identifies the
; installation, that one identifies the COM class.
AppId={{8E4F2A71-6C3D-4B29-9E77-1A05C8D3F6B2}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Same layout the PowerShell installer uses, so the two never disagree about
; where CopyTool lives.
DefaultDirName={localappdata}\CopyTool\bin
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\CopyTool.Host.exe

; No administrator rights, ever. This is the project's first locked decision.
PrivilegesRequired=lowest

; The shell extension is loaded inside explorer.exe and must match its bitness.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Nothing to launch and nothing to choose: CopyTool is a verb in a menu, not an
; application, so the wizard has no reason to ask about folders or shortcuts.
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
CreateAppDir=yes

OutputDir=..\build
OutputBaseFilename={#OutputName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[InstallDelete]
; Everything, not a list of patterns. The two variants install to the same place
; under the same AppId, so one replaces the other - and the standalone build
; spreads 400 files across thirteen culture folders, a runtimes tree, createdump
; and friends. Naming those individually was already wrong once: switching back
; to the small build left 172 files behind because nothing matched "{app}\??-??".
;
; Safe to be this blunt because {app} is a directory CopyTool owns outright and
; nothing else writes to. The shell extension goes too and comes straight back -
; by this point it is unregistered and Explorer has been restarted.
Type: filesandordirs; Name: "{app}\*"

[Files]
#ifdef Standalone
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#else
Source: "{#BuildDir}\CopyTool.ShellExt.dll";              DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\CopyTool.Host.exe";                  DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\CopyTool.Host.dll";                  DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\CopyTool.Host.runtimeconfig.json";   DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\CopyTool.Host.deps.json";            DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\CopyTool.Elevated.exe";              DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\CopyTool.Elevated.dll";              DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\CopyTool.Elevated.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\CopyTool.Elevated.deps.json";        DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\CopyTool.Core.dll";                  DestDir: "{app}"; Flags: ignoreversion
#endif

[Code]

const
  DotNetDownload = 'https://dotnet.microsoft.com/download/dotnet/9.0';

{ ------------------------------------------------------------------ helpers }

function IsProcessRunning(const Image: String): Boolean;
var
  Code: Integer;
begin
  { find sets errorlevel 1 when it matches nothing, which is the whole test. }
  Result := Exec(ExpandConstant('{cmd}'),
                 '/c tasklist /fi "imagename eq ' + Image + '" | find /i "' + Image + '"',
                 '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

procedure StopCopyTool;
var
  Code: Integer;
begin
  { The host locks its own exe, and a running elevated worker locks the rest.
    Both exit on their own soon enough; a copy in flight is on disk as a job
    file either way. }
  Exec(ExpandConstant('{sys}\taskkill.exe'),
       '/f /im CopyTool.Host.exe /im CopyTool.Elevated.exe',
       '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(600);
end;

procedure RestartExplorer;
var
  Code: Integer;
begin
  { Explorer holds a loaded shell extension open, so the DLL cannot be replaced
    or deleted while it is running. It also caches which extensions exist, so
    the restart is what makes a fresh registration take effect. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im explorer.exe',
       '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1200);

  { Windows normally brings the shell back by itself; this covers when it does not. }
  if not IsProcessRunning('explorer.exe') then
  begin
    Exec(ExpandConstant('{win}\explorer.exe'), '', '', SW_SHOW, ewNoWait, Code);
    Sleep(800);
  end;
end;

function RegisterShellExt(const Unregister: Boolean): Boolean;
var
  Params: String;
  Code: Integer;
begin
  Params := '/s ';
  if Unregister then Params := Params + '/u ';
  Params := Params + '"' + ExpandConstant('{app}\' + '{#ShellExtDll}') + '"';

  Result := Exec(ExpandConstant('{sys}\regsvr32.exe'), Params,
                 '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

{ ------------------------------------------------------- prerequisite check }

function HasDotNet9Desktop: Boolean;
var
  Rec: TFindRec;
  Base: String;
begin
  Result := False;
  Base := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');

  if FindFirst(Base + '\9.*', Rec) then
  try
    repeat
      if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

function InitializeSetup: Boolean;
var
  Code: Integer;
begin
  Result := True;

#ifdef Standalone
  { This build carries the runtime, so there is nothing to require. }
  Exit;
#else
  if HasDotNet9Desktop then Exit;

  { The one thing this installer cannot carry: 60 MB, shared, machine-wide. }
  if MsgBox('CopyTool needs the .NET 9 Desktop Runtime (x64), which is not installed.'#13#10#13#10 +
            'Open the download page now?'#13#10#13#10 +
            'Install it, then run this setup again.',
            mbError, MB_YESNO) = IDYES then
    ShellExec('open', DotNetDownload, '', '', SW_SHOW, ewNoWait, Code);

  Result := False;
#endif
end;

{ --------------------------------------------------------------- install }

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    { Let go of the previous version before overwriting it. Unregistering first
      means an upgrade never leaves a stale CLSID pointing at a replaced file. }
    if FileExists(ExpandConstant('{app}\' + '{#ShellExtDll}')) then
      RegisterShellExt(True);

    StopCopyTool;
    RestartExplorer;
  end
  else if CurStep = ssPostInstall then
  begin
    if not RegisterShellExt(False) then
      MsgBox('The drag-drop handler could not be registered.'#13#10 +
             'CopyTool is installed but will not appear in the right-drag menu.',
             mbError, MB_OK);

    RestartExplorer;
  end;
end;

{ -------------------------------------------------------------- uninstall }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { Order matters: unregister while the DLL is still there, then release it
      from Explorer, and only then let Inno delete the files. }
    RegisterShellExt(True);
    StopCopyTool;
    RestartExplorer;
  end;
end;
