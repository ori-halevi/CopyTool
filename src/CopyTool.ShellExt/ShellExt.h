// ============================================================================
//  CopyTool shell extension — shared declarations
//
//  This DLL is loaded *inside explorer.exe*. Everything here must be:
//    - dependency free (static CRT, no C++ runtime DLL, no ATL/MFC)
//    - allocation light and exception free
//    - fast — InvokeCommand runs on Explorer's UI thread
//
//  It does no copying. It captures (sources, destination) and hands the job to
//  CopyTool.Host.exe, then returns immediately.
// ============================================================================
#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>

// {D8E59DEE-51C8-4D84-979A-0D5004B4F07B}
extern const CLSID CLSID_CopyToolDragDropHandler;
extern const wchar_t* const kClsidString;

extern LONG g_dllRefCount;
extern HINSTANCE g_hInst;

inline void DllAddRef()  { InterlockedIncrement(&g_dllRefCount); }
inline void DllRelease() { InterlockedDecrement(&g_dllRefCount); }

HRESULT CreateDragDropHandler(REFIID riid, void** ppv);
HRESULT CreateClassFactory(REFIID riid, void** ppv);
