// ============================================================================
//  CopyTool — right-drag drop handler
//
//  Explorer calls us like this:
//    1. IShellExtInit::Initialize(pidlFolder = drop target, pdtobj = dragged items)
//    2. IContextMenu::QueryContextMenu  -> we append our verbs
//    3. IContextMenu::InvokeCommand     -> user picked one
//
//  Step 3 runs on Explorer's UI thread, so it must not block: we serialise the
//  job to disk, poke the host (or start it) and return.
// ============================================================================
#include "ShellExt.h"
#include <shellapi.h>
#include <strsafe.h>
#include <new>
#include <string>
#include <vector>

namespace {

// --- small helpers ----------------------------------------------------------

// Explorer paths can exceed MAX_PATH; every buffer here is sized dynamically or
// to the 32k extended-path ceiling. Never assume 260.
constexpr DWORD kPathMax = 32768;

std::wstring GetLocalAppDataDir()
{
    PWSTR raw = nullptr;
    std::wstring result;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &raw)))
    {
        result = raw;
        CoTaskMemFree(raw);
    }
    return result;
}

std::wstring GetModuleDir()
{
    std::vector<wchar_t> buf(kPathMax);
    DWORD n = GetModuleFileNameW(g_hInst, buf.data(), static_cast<DWORD>(buf.size()));
    if (n == 0 || n >= buf.size()) return std::wstring();
    std::wstring path(buf.data(), n);
    size_t slash = path.find_last_of(L'\\');
    return (slash == std::wstring::npos) ? std::wstring() : path.substr(0, slash);
}

// CreateDirectoryW only makes the leaf, so walk the components.
void EnsureDirectory(const std::wstring& path)
{
    for (size_t i = 3; i <= path.size(); ++i)
    {
        if (i == path.size() || path[i] == L'\\')
        {
            std::wstring part = path.substr(0, i);
            CreateDirectoryW(part.c_str(), nullptr);
        }
    }
}

std::wstring MakeGuidString()
{
    GUID g{};
    if (FAILED(CoCreateGuid(&g))) return L"job";
    wchar_t buf[64]{};
    StringFromGUID2(g, buf, ARRAYSIZE(buf));   // yields "{...}"
    std::wstring s(buf);
    if (!s.empty() && s.front() == L'{') s = s.substr(1, s.size() - 2);
    return s;
}

// UTF-16 -> UTF-8. JSON is written as UTF-8 so Hebrew paths survive verbatim.
std::string ToUtf8(const std::wstring& w)
{
    if (w.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()),
                                nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), static_cast<int>(w.size()),
                        out.data(), n, nullptr, nullptr);
    return out;
}

std::string JsonEscape(const std::wstring& w)
{
    std::string s = ToUtf8(w);
    std::string out;
    out.reserve(s.size() + 8);
    for (char c : s)
    {
        switch (c)
        {
        case '"':  out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\n': out += "\\n";  break;
        case '\r': out += "\\r";  break;
        case '\t': out += "\\t";  break;
        default:
            if (static_cast<unsigned char>(c) < 0x20)
            {
                char esc[8];
                wsprintfA(esc, "\\u%04X", static_cast<unsigned char>(c));
                out += esc;
            }
            else out += c;
        }
    }
    return out;
}

bool WriteAllBytes(const std::wstring& path, const std::string& data)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    BOOL ok = WriteFile(h, data.data(), static_cast<DWORD>(data.size()), &written, nullptr);
    CloseHandle(h);
    return ok && written == data.size();
}

void AppendLog(const std::wstring& line)
{
    std::wstring dir = GetLocalAppDataDir();
    if (dir.empty()) return;
    dir += L"\\CopyTool";
    EnsureDirectory(dir);

    HANDLE h = CreateFileW((dir + L"\\shellext.log").c_str(), FILE_APPEND_DATA,
                           FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME st{};
    GetLocalTime(&st);
    wchar_t stamp[64];
    wsprintfW(stamp, L"[%04d-%02d-%02d %02d:%02d:%02d] ",
              st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);

    std::string bytes = ToUtf8(std::wstring(stamp) + line + L"\r\n");
    DWORD written = 0;
    WriteFile(h, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr);
    CloseHandle(h);
}

// --- the handler ------------------------------------------------------------

enum Verb { kVerbCopy = 0, kVerbMove = 1, kVerbCount = 2 };

class DragDropHandler : public IShellExtInit, public IContextMenu
{
public:
    DragDropHandler() : m_ref(1) { DllAddRef(); }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IShellExtInit)
            *ppv = static_cast<IShellExtInit*>(this);
        else if (riid == IID_IContextMenu)
            *ppv = static_cast<IContextMenu*>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        ULONG n = InterlockedDecrement(&m_ref);
        if (n == 0) delete this;
        return n;
    }

    // IShellExtInit — capture the dragged items and the drop target
    IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE pidlFolder,
                              IDataObject* pdtobj, HKEY) override
    {
        m_sources.clear();
        m_destination.clear();

        if (pidlFolder)
        {
            std::vector<wchar_t> buf(kPathMax);
            if (SHGetPathFromIDListEx(pidlFolder, buf.data(),
                                      static_cast<DWORD>(buf.size()), GPFIDL_DEFAULT))
                m_destination = buf.data();
        }

        if (pdtobj)
        {
            FORMATETC fmt = { CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL };
            STGMEDIUM stg{};
            if (SUCCEEDED(pdtobj->GetData(&fmt, &stg)))
            {
                if (HDROP hDrop = static_cast<HDROP>(GlobalLock(stg.hGlobal)))
                {
                    UINT count = DragQueryFileW(hDrop, 0xFFFFFFFF, nullptr, 0);
                    m_sources.reserve(count);
                    for (UINT i = 0; i < count; ++i)
                    {
                        // Ask for the length first — these can exceed MAX_PATH.
                        UINT len = DragQueryFileW(hDrop, i, nullptr, 0);
                        if (len == 0) continue;
                        std::vector<wchar_t> item(len + 1);
                        if (DragQueryFileW(hDrop, i, item.data(), len + 1))
                            m_sources.emplace_back(item.data());
                    }
                    GlobalUnlock(stg.hGlobal);
                }
                ReleaseStgMedium(&stg);
            }
        }

        // Nothing to act on -> decline, so we never show a dead menu item.
        if (m_sources.empty() || m_destination.empty()) return E_INVALIDARG;
        return S_OK;
    }

    // IContextMenu
    IFACEMETHODIMP QueryContextMenu(HMENU hmenu, UINT indexMenu,
                                    UINT idCmdFirst, UINT, UINT uFlags) override
    {
        if (uFlags & CMF_DEFAULTONLY) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);

        InsertMenuW(hmenu, indexMenu++, MF_BYPOSITION | MF_SEPARATOR, 0, nullptr);
        InsertMenuW(hmenu, indexMenu++, MF_BYPOSITION | MF_STRING,
                    idCmdFirst + kVerbCopy, L"Copy here with CopyTool");
        InsertMenuW(hmenu, indexMenu++, MF_BYPOSITION | MF_STRING,
                    idCmdFirst + kVerbMove, L"Move here with CopyTool");

        return MAKE_HRESULT(SEVERITY_SUCCESS, 0, kVerbCount);
    }

    IFACEMETHODIMP InvokeCommand(LPCMINVOKECOMMANDINFO pici) override
    {
        if (!pici) return E_INVALIDARG;

        UINT verb;
        if (HIWORD(pici->lpVerb) == 0)
        {
            verb = LOWORD(pici->lpVerb);
        }
        else if (lstrcmpiA(pici->lpVerb, "CopyToolCopy") == 0) verb = kVerbCopy;
        else if (lstrcmpiA(pici->lpVerb, "CopyToolMove") == 0) verb = kVerbMove;
        else return E_FAIL;

        if (verb >= kVerbCount) return E_INVALIDARG;
        return DispatchJob(verb == kVerbMove ? L"move" : L"copy");
    }

    IFACEMETHODIMP GetCommandString(UINT_PTR idCmd, UINT uFlags, UINT*,
                                    LPSTR pszName, UINT cchMax) override
    {
        if (idCmd >= kVerbCount) return E_INVALIDARG;

        const wchar_t* canonical = (idCmd == kVerbMove) ? L"CopyToolMove" : L"CopyToolCopy";
        const wchar_t* help      = (idCmd == kVerbMove)
                                 ? L"Move the dragged items here using CopyTool"
                                 : L"Copy the dragged items here using CopyTool";

        switch (uFlags)
        {
        case GCS_VERBW:   return StringCchCopyW(reinterpret_cast<PWSTR>(pszName), cchMax, canonical);
        case GCS_HELPTEXTW: return StringCchCopyW(reinterpret_cast<PWSTR>(pszName), cchMax, help);
        case GCS_VERBA:
        case GCS_HELPTEXTA:
        {
            const wchar_t* src = (uFlags == GCS_VERBA) ? canonical : help;
            WideCharToMultiByte(CP_ACP, 0, src, -1, pszName, cchMax, nullptr, nullptr);
            return S_OK;
        }
        default: return E_NOTIMPL;
        }
    }

private:
    // Serialise the job, then hand it off. Must stay fast — Explorer is blocked.
    HRESULT DispatchJob(const wchar_t* operation)
    {
        m_lastOperation = operation;

        std::wstring root = GetLocalAppDataDir();
        if (root.empty()) return E_FAIL;
        root += L"\\CopyTool";
        std::wstring jobsDir = root + L"\\jobs";
        EnsureDirectory(jobsDir);

        std::wstring jobId   = MakeGuidString();
        std::wstring jobPath = jobsDir + L"\\" + jobId + L".json";

        std::string json;
        json.reserve(256 + m_sources.size() * 128);
        json += "{\n  \"schema\": 1,\n";
        json += "  \"id\": \""        + JsonEscape(jobId)         + "\",\n";
        json += "  \"operation\": \"" + JsonEscape(operation)     + "\",\n";
        json += "  \"destination\": \"" + JsonEscape(m_destination) + "\",\n";
        json += "  \"sources\": [\n";
        for (size_t i = 0; i < m_sources.size(); ++i)
        {
            json += "    \"" + JsonEscape(m_sources[i]) + "\"";
            json += (i + 1 < m_sources.size()) ? ",\n" : "\n";
        }
        json += "  ]\n}\n";

        if (!WriteAllBytes(jobPath, json)) return E_FAIL;

        wchar_t summary[256];
        wsprintfW(summary, L"%s: %d item(s) -> job %s", operation,
                  static_cast<int>(m_sources.size()), jobId.c_str());
        AppendLog(summary);

        // Fast path: an already-running host is listening.
        if (NotifyRunningHost(jobPath)) return S_OK;

        // Cold path: start the host.
        if (StartHost(jobPath)) return S_OK;

        // Spike / broken install: prove what we captured.
        ReportNoHost(jobPath);
        return S_OK;
    }

    bool NotifyRunningHost(const std::wstring& jobPath)
    {
        HANDLE pipe = CreateFileW(L"\\\\.\\pipe\\CopyTool.Host", GENERIC_WRITE, 0,
                                  nullptr, OPEN_EXISTING, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) return false;

        std::string msg = ToUtf8(jobPath) + "\n";
        DWORD written = 0;
        BOOL ok = WriteFile(pipe, msg.data(), static_cast<DWORD>(msg.size()), &written, nullptr);
        CloseHandle(pipe);
        return ok && written == msg.size();
    }

    bool StartHost(const std::wstring& jobPath)
    {
        std::wstring dir = GetModuleDir();
        if (dir.empty()) return false;
        std::wstring exe = dir + L"\\CopyTool.Host.exe";
        if (GetFileAttributesW(exe.c_str()) == INVALID_FILE_ATTRIBUTES) return false;

        std::wstring cmd = L"\"" + exe + L"\" --job \"" + jobPath + L"\"";
        std::vector<wchar_t> mutableCmd(cmd.begin(), cmd.end());
        mutableCmd.push_back(L'\0');

        STARTUPINFOW si{}; si.cb = sizeof(si);
        PROCESS_INFORMATION pi{};
        BOOL ok = CreateProcessW(exe.c_str(), mutableCmd.data(), nullptr, nullptr, FALSE,
                                 CREATE_UNICODE_ENVIRONMENT, nullptr, dir.c_str(), &si, &pi);
        if (ok) { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
        return ok != FALSE;
    }

    void ReportNoHost(const std::wstring& jobPath)
    {
        AppendLog(L"CopyTool.Host.exe not found - job left on disk");

        std::wstring text = L"CopyTool captured the drop, but CopyTool.Host.exe was not found.\n\n";
        text += L"Operation:   " + m_lastOperation + L"\n";
        text += L"Destination: " + m_destination + L"\n\n";
        text += L"Sources (" + std::to_wstring(m_sources.size()) + L"):\n";
        for (size_t i = 0; i < m_sources.size() && i < 15; ++i)
            text += L"  " + m_sources[i] + L"\n";
        if (m_sources.size() > 15) text += L"  ...\n";
        text += L"\nJob file:\n  " + jobPath;

        MessageBoxW(nullptr, text.c_str(), L"CopyTool", MB_OK | MB_ICONINFORMATION);
    }

    // Balances the DllAddRef in the constructor. Without it g_dllRefCount only
    // ever climbed, so DllCanUnloadNow could never return S_OK and the DLL stayed
    // pinned inside explorer.exe for the life of the process -- which is also why
    // a rebuild needed Explorer restarted. ClassFactory got this right; this did
    // not.
    ~DragDropHandler() { DllRelease(); }

    LONG                      m_ref;
    std::vector<std::wstring> m_sources;
    std::wstring              m_destination;
    std::wstring              m_lastOperation;
};

} // namespace

HRESULT CreateDragDropHandler(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;

    DragDropHandler* h = new (std::nothrow) DragDropHandler();
    if (!h) return E_OUTOFMEMORY;

    HRESULT hr = h->QueryInterface(riid, ppv);
    h->Release();
    return hr;
}
