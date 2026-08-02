// ============================================================================
//  CopyTool shell extension — COM entry points, class factory, registration.
//
//  Registration goes to HKCU\Software\Classes on purpose: installing CopyTool
//  never requires administrator rights.
// ============================================================================
#include "ShellExt.h"
#include <strsafe.h>
#include <new>
#include <string>
#include <vector>

// {D8E59DEE-51C8-4D84-979A-0D5004B4F07B}
const CLSID CLSID_CopyToolDragDropHandler =
    { 0xd8e59dee, 0x51c8, 0x4d84, { 0x97, 0x9a, 0x0d, 0x50, 0x04, 0xb4, 0xf0, 0x7b } };

const wchar_t* const kClsidString = L"{D8E59DEE-51C8-4D84-979A-0D5004B4F07B}";

static const wchar_t* const kFriendlyName = L"CopyTool Drag-Drop Handler";
static const wchar_t* const kHandlerName  = L"CopyTool";

LONG      g_dllRefCount = 0;
HINSTANCE g_hInst       = nullptr;

// ---------------------------------------------------------------------------
// Class factory
// ---------------------------------------------------------------------------
namespace {

class ClassFactory : public IClassFactory
{
public:
    ClassFactory() : m_ref(1) { DllAddRef(); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        ULONG n = InterlockedDecrement(&m_ref);
        if (n == 0) delete this;
        return n;
    }

    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override
    {
        if (pUnkOuter) return CLASS_E_NOAGGREGATION;
        return CreateDragDropHandler(riid, ppv);
    }
    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) DllAddRef(); else DllRelease();
        return S_OK;
    }

private:
    ~ClassFactory() { DllRelease(); }
    LONG m_ref;
};

// --- registry helpers ------------------------------------------------------

LSTATUS SetKeyValue(HKEY root, const std::wstring& subKey,
                    const wchar_t* valueName, const std::wstring& data)
{
    HKEY key = nullptr;
    LSTATUS s = RegCreateKeyExW(root, subKey.c_str(), 0, nullptr,
                                REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &key, nullptr);
    if (s != ERROR_SUCCESS) return s;

    s = RegSetValueExW(key, valueName, 0, REG_SZ,
                       reinterpret_cast<const BYTE*>(data.c_str()),
                       static_cast<DWORD>((data.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return s;
}

std::wstring ThisModulePath()
{
    std::vector<wchar_t> buf(32768);
    DWORD n = GetModuleFileNameW(g_hInst, buf.data(), static_cast<DWORD>(buf.size()));
    return (n == 0 || n >= buf.size()) ? std::wstring() : std::wstring(buf.data(), n);
}

// Drop targets we hook. "Directory" covers folders, "Drive" covers drive roots.
const wchar_t* const kDropTargets[] = { L"Directory", L"Drive" };

/// Elevated registration goes machine-wide, ordinary registration goes per-user.
/// This mirrors what regsvr32 callers expect, and keeps the default install free
/// of any need for administrator rights.
HKEY RegistrationRoot()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        return HKEY_CURRENT_USER;

    TOKEN_ELEVATION elevation{};
    DWORD size = sizeof(elevation);
    BOOL ok = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size);
    CloseHandle(token);

    return (ok && elevation.TokenIsElevated) ? HKEY_LOCAL_MACHINE : HKEY_CURRENT_USER;
}

} // namespace

HRESULT CreateClassFactory(REFIID riid, void** ppv)
{
    ClassFactory* f = new (std::nothrow) ClassFactory();
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

// ---------------------------------------------------------------------------
// DLL exports
// ---------------------------------------------------------------------------
BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hInst = hInst;
        DisableThreadLibraryCalls(hInst);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (rclsid != CLSID_CopyToolDragDropHandler) return CLASS_E_CLASSNOTAVAILABLE;
    return CreateClassFactory(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return (g_dllRefCount == 0) ? S_OK : S_FALSE;
}

STDAPI DllRegisterServer()
{
    std::wstring dll = ThisModulePath();
    if (dll.empty()) return E_FAIL;

    HKEY root = RegistrationRoot();
    const std::wstring clsidKey = std::wstring(L"Software\\Classes\\CLSID\\") + kClsidString;

    if (SetKeyValue(root, clsidKey, nullptr, kFriendlyName) != ERROR_SUCCESS)
        return E_ACCESSDENIED;
    if (SetKeyValue(root, clsidKey + L"\\InprocServer32", nullptr, dll) != ERROR_SUCCESS)
        return E_ACCESSDENIED;
    if (SetKeyValue(root, clsidKey + L"\\InprocServer32", L"ThreadingModel", L"Apartment") != ERROR_SUCCESS)
        return E_ACCESSDENIED;

    for (const wchar_t* target : kDropTargets)
    {
        std::wstring key = std::wstring(L"Software\\Classes\\") + target +
                           L"\\shellex\\DragDropHandlers\\" + kHandlerName;
        if (SetKeyValue(root, key, nullptr, kClsidString) != ERROR_SUCCESS)
            return E_ACCESSDENIED;
    }

    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    // Clean both hives regardless of how this was registered: a machine-wide
    // install left behind by a later per-user one would keep loading the old DLL.
    HKEY roots[] = { HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE };

    for (HKEY root : roots)
    {
        for (const wchar_t* target : kDropTargets)
        {
            std::wstring key = std::wstring(L"Software\\Classes\\") + target +
                               L"\\shellex\\DragDropHandlers\\" + kHandlerName;
            RegDeleteTreeW(root, key.c_str());
        }

        const std::wstring clsidKey = std::wstring(L"Software\\Classes\\CLSID\\") + kClsidString;
        RegDeleteTreeW(root, clsidKey.c_str());
    }

    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}
