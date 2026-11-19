// OBSIDIAN /// native injection agent
//
// A dependency-free x64 DLL whose only job is to start a managed agent inside
// an already running .NET Framework process. It is loaded through
// CreateRemoteThread(LoadLibraryW); DllMain reads a small job description that
// sits next to the DLL and hands it to the CLR hosting API in the *target*
// process (ICLRRuntimeHost::ExecuteInDefaultAppDomain).
//
// The component never writes to the target's memory beyond the DLL itself and
// executes exactly one managed entry point. Designed for .NET Framework 4.x
// targets; CoreCLR targets are handled by the out-of-process engine because a
// live CoreCLR cannot host a second runtime in-process.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <metahost.h>
#include <mscoree.h>
#include <stdio.h>

#pragma comment(lib, "mscoree.lib")

static const wchar_t* kJobFileName = L"obsidian.job";
static const wchar_t* kResultFileName = L"obsidian.result";

static void AppendLog(const wchar_t* dllDir, const char* format, ...)
{
    wchar_t path[MAX_PATH * 2];
    _snwprintf_s(path, _countof(path), _TRUNCATE, L"%s\\obsidian-injector.log", dllDir);

    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE)
        return;

    char buffer[2048];
    va_list args;
    va_start(args, format);
    _vsnprintf_s(buffer, _countof(buffer), _TRUNCATE, format, args);
    va_end(args);

    DWORD written = 0;
    SetFilePointer(file, 0, NULL, FILE_END);
    WriteFile(file, buffer, (DWORD)strlen(buffer), &written, NULL);
    WriteFile(file, "\r\n", 2, &written, NULL);
    CloseHandle(file);
}

static void WriteResult(const wchar_t* dllDir, const char* text)
{
    wchar_t path[MAX_PATH * 2];
    _snwprintf_s(path, _countof(path), _TRUNCATE, L"%s\\%s", dllDir, kResultFileName);

    HANDLE file = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL,
                              CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE)
        return;

    DWORD written = 0;
    WriteFile(file, text, (DWORD)strlen(text), &written, NULL);
    CloseHandle(file);
}

// Reads one UTF-8 line from the job file and converts it to a freshly
// allocated wide string. Caller frees.
static wchar_t* ReadJobLine(HANDLE file)
{
    char line[4096];
    int length = 0;

    while (length < (int)sizeof(line) - 1)
    {
        char c = 0;
        DWORD got = 0;
        if (!ReadFile(file, &c, 1, &got, NULL) || got == 0)
            break;
        if (c == '\n')
            break;
        if (c == '\r')
            continue;
        line[length++] = c;
    }
    line[length] = '\0';

    if (length == 0)
        return NULL;

    int wideLength = MultiByteToWideChar(CP_UTF8, 0, line, length, NULL, 0);
    if (wideLength <= 0)
        return NULL;

    wchar_t* result = (wchar_t*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, (wideLength + 1) * sizeof(wchar_t));
    if (result == NULL)
        return NULL;

    MultiByteToWideChar(CP_UTF8, 0, line, length, result, wideLength);
    result[wideLength] = L'\0';
    return result;
}

static DWORD TryFrameworkHost(HMODULE mscoree, wchar_t* dllDir,
                              const wchar_t* payload, const wchar_t* typeName,
                              const wchar_t* methodName, const wchar_t* argument)
{
    typedef HRESULT(__stdcall * CLRCreateInstanceFn)(REFCLSID, REFIID, LPVOID*);

    CLRCreateInstanceFn createInstance =
        (CLRCreateInstanceFn)GetProcAddress(mscoree, "CLRCreateInstance");
    if (createInstance == NULL)
    {
        AppendLog(dllDir, "CLRCreateInstance not found in mscoree.dll");
        return 0x80004005;
    }

    ICLRMetaHost* metaHost = NULL;
    HRESULT hr = createInstance(CLSID_CLRMetaHost, IID_ICLRMetaHost, (LPVOID*)&metaHost);
    if (FAILED(hr) || metaHost == NULL)
    {
        AppendLog(dllDir, "CLRCreateInstance failed: 0x%08X", hr);
        return hr;
    }

    // Prefer the newest installed CLR 4 runtime; fall back to the loaded one.
    ICLRRuntimeInfo* runtimeInfo = NULL;
    const wchar_t* preferred[] = { L"v4.0.30319", L"v4.0.30319.1" };
    for (int i = 0; i < 2 && runtimeInfo == NULL; i++)
    {
        hr = metaHost->GetRuntime(preferred[i], IID_ICLRRuntimeInfo, (LPVOID*)&runtimeInfo);
    }

    if (runtimeInfo == NULL)
    {
        AppendLog(dllDir, "GetRuntime(v4.0.30319) failed: 0x%08X", hr);
        metaHost->Release();
        return hr;
    }

    ICLRRuntimeHost* host = NULL;
    hr = runtimeInfo->GetInterface(CLSID_CLRRuntimeHost, IID_ICLRRuntimeHost, (LPVOID*)&host);
    runtimeInfo->Release();
    metaHost->Release();

    if (FAILED(hr) || host == NULL)
    {
        AppendLog(dllDir, "GetInterface(CLRRuntimeHost) failed: 0x%08X", hr);
        return hr;
    }

    DWORD exitCode = 0;
    hr = host->ExecuteInDefaultAppDomain(payload, typeName, methodName, argument, &exitCode);
    AppendLog(dllDir, "ExecuteInDefaultAppDomain hr=0x%08X exit=%lu", hr, (unsigned long)exitCode);

    host->Release();

    char result[256];
    _snprintf_s(result, _countof(result), _TRUNCATE, "framework\t0x%08X\t%lu", hr, (unsigned long)exitCode);
    WriteResult(dllDir, result);

    return hr;
}

static DWORD WINAPI AgentThread(LPVOID parameter)
{
    HMODULE self = (HMODULE)parameter;

    wchar_t dllPath[MAX_PATH * 2];
    if (GetModuleFileNameW(self, dllPath, _countof(dllPath)) == 0)
        return 1;

    wchar_t dllDir[MAX_PATH * 2];
    wcscpy_s(dllDir, _countof(dllDir), dllPath);
    wchar_t* slash = wcsrchr(dllDir, L'\\');
    if (slash != NULL)
        *slash = L'\0';

    wchar_t jobPath[MAX_PATH * 2];
    _snwprintf_s(jobPath, _countof(jobPath), _TRUNCATE, L"%s\\%s", dllDir, kJobFileName);

    AppendLog(dllDir, "agent thread started in pid %lu", (unsigned long)GetCurrentProcessId());

    HANDLE job = CreateFileW(jobPath, GENERIC_READ, FILE_SHARE_READ, NULL,
                             OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (job == INVALID_HANDLE_VALUE)
    {
        AppendLog(dllDir, "job file missing (error %lu)", (unsigned long)GetLastError());
        WriteResult(dllDir, "error\tjob-missing");
        return 1;
    }

    wchar_t* payload = ReadJobLine(job);
    wchar_t* typeName = ReadJobLine(job);
    wchar_t* methodName = ReadJobLine(job);
    wchar_t* argument = ReadJobLine(job);
    CloseHandle(job);

    if (payload == NULL || typeName == NULL || methodName == NULL)
    {
        AppendLog(dllDir, "job file incomplete");
        WriteResult(dllDir, "error\tjob-invalid");
    }
    else
    {
        AppendLog(dllDir, "job: payload=%ws type=%ws method=%ws arg=%ws",
                  payload, typeName, methodName, argument != NULL ? argument : L"");

        HMODULE mscoree = GetModuleHandleW(L"mscoree.dll");
        if (mscoree == NULL)
            mscoree = LoadLibraryW(L"mscoree.dll");

        if (mscoree == NULL)
        {
            AppendLog(dllDir, "mscoree.dll unavailable (error %lu)", (unsigned long)GetLastError());
            WriteResult(dllDir, "error\tmscoree-missing");
        }
        else if (GetModuleHandleW(L"coreclr.dll") != NULL && GetModuleHandleW(L"clr.dll") == NULL)
        {
            // Only CoreCLR is present: no in-process hosting is possible.
            AppendLog(dllDir, "coreclr target: managed hosting unsupported");
            WriteResult(dllDir, "unsupported\tcoreclr");
        }
        else
        {
            AppendLog(dllDir, "calling ExecuteInDefaultAppDomain");
            TryFrameworkHost(mscoree, dllDir, payload, typeName, methodName,
                             argument != NULL ? argument : L"");
        }
    }

    if (payload) HeapFree(GetProcessHeap(), 0, payload);
    if (typeName) HeapFree(GetProcessHeap(), 0, typeName);
    if (methodName) HeapFree(GetProcessHeap(), 0, methodName);
    if (argument) HeapFree(GetProcessHeap(), 0, argument);

    AppendLog(dllDir, "agent thread finished");

    // The DLL stays loaded on purpose: unloading from a thread that was created
    // inside DllMain can deadlock on the loader lock. The host cleans the work
    // directory up on a best effort basis.
    return 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(NULL, 0, AgentThread, module, 0, NULL);
        if (thread == NULL)
        {
            // Fail the load deterministically: the injector then reports "loader
            // rejected the DLL" immediately instead of waiting for the result
            // timeout of the deep dump (90 s).
            return FALSE;
        }

        CloseHandle(thread);
    }

    return TRUE;
}

extern "C" __declspec(dllexport) int ObsidianAgentVersion(void)
{
    return 1;
}
