#include <windows.h>
#include <shobjidl_core.h>
#include <shlobj_core.h>
#include <objectarray.h>

#include <iostream>

#define RETURN_IF_FAILED(expression) \
    do { \
        const HRESULT result = (expression); \
        if (FAILED(result)) { \
            std::cerr << #expression << ": HRESULT=0x" << std::hex << result << "\n"; \
            return 1; \
        } \
    } while (false)

using DllGetClassObjectFn = HRESULT(__stdcall*)(REFCLSID, REFIID, void**);

int wmain(int argc, wchar_t** argv)
{
    if (argc != 3)
    {
        std::wcerr << L"usage: ShellExtensionSmoke DLL_PATH FILE_PATH\n";
        return 2;
    }

    RETURN_IF_FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED));
    const HMODULE module = LoadLibraryW(argv[1]);
    if (!module)
        return static_cast<int>(GetLastError());

    const auto getClassObject = reinterpret_cast<DllGetClassObjectFn>(
        GetProcAddress(module, "DllGetClassObject"));
    if (!getClassObject)
        return static_cast<int>(GetLastError());

    const CLSID commandClsid = {
        0x4b8f4e39,
        0x392d,
        0x4b64,
        {0xa8, 0xb7, 0x7e, 0xa4, 0xd0, 0x5c, 0x57, 0x2f}};

    IClassFactory* factory = nullptr;
    RETURN_IF_FAILED(getClassObject(
        commandClsid,
        IID_PPV_ARGS(&factory)));

    IExplorerCommand* command = nullptr;
    const HRESULT createResult = factory->CreateInstance(
        nullptr,
        IID_PPV_ARGS(&command));
    factory->Release();
    RETURN_IF_FAILED(createResult);

    IShellItem* item = nullptr;
    RETURN_IF_FAILED(SHCreateItemFromParsingName(
        argv[2],
        nullptr,
        IID_PPV_ARGS(&item)));

    IObjectCollection* collection = nullptr;
    RETURN_IF_FAILED(CoCreateInstance(
        CLSID_EnumerableObjectCollection,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&collection)));
    RETURN_IF_FAILED(collection->AddObject(item));
    item->Release();

    IShellItemArray* selection = nullptr;
    RETURN_IF_FAILED(collection->QueryInterface(IID_PPV_ARGS(&selection)));
    collection->Release();

    EXPCMDSTATE state = ECS_DISABLED;
    const HRESULT stateResult = command->GetState(selection, FALSE, &state);
    selection->Release();

    PWSTR title = nullptr;
    const HRESULT titleResult = command->GetTitle(nullptr, &title);
    if (SUCCEEDED(titleResult))
    {
        std::wcout << L"Title=" << title << L"\n";
        CoTaskMemFree(title);
    }
    std::wcout << L"State=" << static_cast<unsigned>(state) << L"\n";

    command->Release();
    FreeLibrary(module);
    CoUninitialize();
    return SUCCEEDED(stateResult) && state == ECS_ENABLED ? 0 : 1;
}
