#include "ShellExtension.h"

#include <shlobj_core.h>
#include <shobjidl_core.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <sddl.h>
#include <algorithm>
#include <cstdio>
#include <cwctype>
#include <new>
#include <string>
#include <string_view>
#include <vector>

#ifndef RETURN_IF_FAILED
#define RETURN_IF_FAILED(expression)        \
    do                                      \
    {                                       \
        const HRESULT returnHr = (expression); \
        if (FAILED(returnHr))               \
            return returnHr;                \
    } while (false)
#endif

HINSTANCE g_module = nullptr;
volatile long g_objectCount = 0;

void ModuleAddRef() noexcept
{
    InterlockedIncrement(&g_objectCount);
}

void ModuleRelease() noexcept
{
    InterlockedDecrement(&g_objectCount);
}

namespace
{
constexpr wchar_t kMountKey[] = L"Software\\TNovCloud\\Mount";
constexpr wchar_t kClientExe[] = L"TNovCloud.Client.exe";

enum class CommandId
{
    Root,
    Checkout,
    Checkin,
    Cancel,
    Approve,
    Properties
};

struct CommandDefinition
{
    CommandId id;
    const wchar_t* title;
    const wchar_t* verb;
    const wchar_t* argument;
};

constexpr CommandDefinition kCommands[] = {
    {CommandId::Checkout, L"Checkout", L"checkout", L"--checkout"},
    {CommandId::Checkin, L"Check in", L"checkin", L"--checkin"},
    {CommandId::Cancel, L"Cancel checkout", L"cancel", L"--cancel"},
    {CommandId::Approve, L"Send for approval", L"approve", L"--approve"},
    {CommandId::Properties, L"Properties", L"properties", L"--properties"}};

const CommandDefinition* FindDefinition(CommandId id) noexcept
{
    for (const auto& command : kCommands)
    {
        if (command.id == id)
            return &command;
    }
    return nullptr;
}

HRESULT DuplicateString(const wchar_t* value, PWSTR* result)
{
    if (!result)
        return E_POINTER;
    *result = nullptr;
    return SHStrDupW(value, result);
}

bool ReadRegistryString(HKEY key, const wchar_t* name, std::wstring& value)
{
    DWORD type = 0;
    DWORD size = 0;
    if (RegQueryValueExW(key, name, nullptr, &type, nullptr, &size) != ERROR_SUCCESS ||
        (type != REG_SZ && type != REG_EXPAND_SZ) || size < sizeof(wchar_t))
        return false;

    std::vector<wchar_t> buffer(size / sizeof(wchar_t) + 1);
    if (RegQueryValueExW(
            key, name, nullptr, &type, reinterpret_cast<BYTE*>(buffer.data()), &size) != ERROR_SUCCESS)
        return false;

    buffer.back() = L'\0';
    value.assign(buffer.data());
    if (type == REG_EXPAND_SZ)
    {
        DWORD required = ExpandEnvironmentStringsW(value.c_str(), nullptr, 0);
        if (required)
        {
            std::vector<wchar_t> expanded(required);
            if (ExpandEnvironmentStringsW(value.c_str(), expanded.data(), required))
                value.assign(expanded.data());
        }
    }
    return !value.empty();
}

bool ReadRegistryDword(HKEY key, const wchar_t* name, DWORD& value)
{
    DWORD type = 0;
    DWORD size = sizeof(value);
    return RegQueryValueExW(
               key, name, nullptr, &type, reinterpret_cast<BYTE*>(&value), &size) == ERROR_SUCCESS &&
           type == REG_DWORD && size == sizeof(value);
}

std::wstring NormalizePath(const std::wstring& path)
{
    DWORD required = GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
    if (!required)
        return {};

    std::vector<wchar_t> buffer(required);
    if (!GetFullPathNameW(path.c_str(), required, buffer.data(), nullptr))
        return {};

    std::wstring result(buffer.data());
    while (result.size() > 3 && (result.back() == L'\\' || result.back() == L'/'))
        result.pop_back();
    return result;
}

bool IsAtOrBelow(const std::wstring& path, const std::wstring& root)
{
    if (_wcsicmp(path.c_str(), root.c_str()) == 0)
        return true;
    if (path.size() <= root.size() || _wcsnicmp(path.c_str(), root.c_str(), root.size()) != 0)
        return false;
    return root.back() == L'\\' || path[root.size()] == L'\\';
}

HRESULT SelectedFilePaths(IShellItemArray* selection, std::vector<std::wstring>& paths)
{
    if (!selection)
        return E_INVALIDARG;

    DWORD count = 0;
    RETURN_IF_FAILED(selection->GetCount(&count));
    if (count == 0 || count > 256)
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);

    paths.clear();
    paths.reserve(count);
    for (DWORD index = 0; index < count; ++index)
    {
        IShellItem* item = nullptr;
        RETURN_IF_FAILED(selection->GetItemAt(index, &item));

        PWSTR rawPath = nullptr;
        const HRESULT hr = item->GetDisplayName(SIGDN_FILESYSPATH, &rawPath);
        item->Release();
        if (FAILED(hr))
            return hr;

        paths.emplace_back(rawPath);
        CoTaskMemFree(rawPath);
    }
    return S_OK;
}

bool IsTNovCloudPath(const std::wstring& selectedPath, bool requireFile)
{
    const std::wstring path = NormalizePath(selectedPath);
    if (path.empty())
        return false;

    const DWORD attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES ||
        (requireFile && (attributes & FILE_ATTRIBUTE_DIRECTORY)))
        return false;

    HKEY rawKey = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kMountKey, 0, KEY_QUERY_VALUE, &rawKey) != ERROR_SUCCESS)
        return false;

    std::wstring driveLetter;
    std::wstring rootPath;
    const bool hasDrive = ReadRegistryString(rawKey, L"DriveLetter", driveLetter);
    ReadRegistryString(rawKey, L"RootPath", rootPath);
    DWORD expectedSerial = 0;
    const bool hasSerial = ReadRegistryDword(rawKey, L"VolumeSerial", expectedSerial);
    RegCloseKey(rawKey);

    if (!hasDrive || driveLetter.empty())
        return false;

    const wchar_t letter = static_cast<wchar_t>(towupper(driveLetter[0]));
    if (letter < L'A' || letter > L'Z')
        return false;

    const std::wstring driveRoot = std::wstring(1, letter) + L":\\";
    const std::wstring normalizedDrive = NormalizePath(driveRoot);
    const std::wstring normalizedRoot = rootPath.empty() ? std::wstring() : NormalizePath(rootPath);
    if (!IsAtOrBelow(path, normalizedDrive) &&
        (normalizedRoot.empty() || !IsAtOrBelow(path, normalizedRoot)))
        return false;

    wchar_t volumePath[MAX_PATH] = {};
    if (!GetVolumePathNameW(path.c_str(), volumePath, ARRAYSIZE(volumePath)))
        return false;

    DWORD actualSerial = 0;
    DWORD maximumComponentLength = 0;
    DWORD fileSystemFlags = 0;
    wchar_t volumeName[MAX_PATH] = {};
    wchar_t fileSystemName[MAX_PATH] = {};
    if (!GetVolumeInformationW(
            volumePath,
            volumeName,
            ARRAYSIZE(volumeName),
            &actualSerial,
            &maximumComponentLength,
            &fileSystemFlags,
            fileSystemName,
            ARRAYSIZE(fileSystemName)))
        return false;

    return !hasSerial || expectedSerial == actualSerial;
}

std::string ToUtf8(std::wstring_view value)
{
    if (value.empty())
        return {};
    const int required = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
        nullptr, 0, nullptr, nullptr);
    if (required <= 0)
        return {};

    std::string result(static_cast<size_t>(required), '\0');
    if (!WideCharToMultiByte(
            CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
            result.data(), required, nullptr, nullptr))
        return {};
    return result;
}

std::string JsonEscape(std::wstring_view value)
{
    const std::string utf8 = ToUtf8(value);
    std::string result;
    result.reserve(utf8.size() + 8);
    for (const unsigned char ch : utf8)
    {
        switch (ch)
        {
        case '"':
            result += "\\\"";
            break;
        case '\\':
            result += "\\\\";
            break;
        case '\b':
            result += "\\b";
            break;
        case '\f':
            result += "\\f";
            break;
        case '\n':
            result += "\\n";
            break;
        case '\r':
            result += "\\r";
            break;
        case '\t':
            result += "\\t";
            break;
        default:
            if (ch < 0x20)
            {
                char encoded[7] = {};
                sprintf_s(encoded, "\\u%04x", ch);
                result += encoded;
            }
            else
            {
                result.push_back(static_cast<char>(ch));
            }
        }
    }
    return result;
}

std::wstring CurrentUserSid()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        return {};

    DWORD size = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    std::vector<BYTE> buffer(size);
    if (!size || !GetTokenInformation(token, TokenUser, buffer.data(), size, &size))
    {
        CloseHandle(token);
        return {};
    }

    CloseHandle(token);
    LPWSTR sidText = nullptr;
    if (!ConvertSidToStringSidW(
            reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &sidText))
        return {};

    std::wstring result(sidText);
    LocalFree(sidText);
    return result;
}

bool WriteAll(HANDLE pipe, const void* data, DWORD size)
{
    const BYTE* cursor = static_cast<const BYTE*>(data);
    while (size)
    {
        DWORD written = 0;
        if (!WriteFile(pipe, cursor, size, &written, nullptr) || written == 0)
            return false;
        cursor += written;
        size -= written;
    }
    return true;
}

bool SendPipeCommand(
    const CommandDefinition& command,
    const std::vector<std::wstring>& paths)
{
    const std::wstring sid = CurrentUserSid();
    if (sid.empty())
        return false;

    const std::wstring pipeName = L"\\\\.\\pipe\\TNovCloud.Client.Commands." + sid;
    if (!WaitNamedPipeW(pipeName.c_str(), 200))
        return false;

    HANDLE pipe = CreateFileW(
        pipeName.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
    if (pipe == INVALID_HANDLE_VALUE)
        return false;

    std::string payload =
        "{\"command\":\"" + std::string(ToUtf8(command.verb)) + "\",\"paths\":[";
    for (size_t index = 0; index < paths.size(); ++index)
    {
        if (index)
            payload += ',';
        payload += "\"" + JsonEscape(paths[index]) + "\"";
    }
    payload += "]}";
    if (payload.empty() || payload.size() > 1024 * 1024)
    {
        CloseHandle(pipe);
        return false;
    }

    const auto length = static_cast<UINT32>(payload.size());
    const bool sent =
        WriteAll(pipe, &length, sizeof(length)) &&
        WriteAll(pipe, payload.data(), length);
    CloseHandle(pipe);
    return sent;
}

std::wstring ModuleDirectory()
{
    std::vector<wchar_t> buffer(MAX_PATH);
    for (;;)
    {
        const DWORD length =
            GetModuleFileNameW(g_module, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (!length)
            return {};
        if (length < buffer.size() - 1)
        {
            std::wstring path(buffer.data(), length);
            const size_t slash = path.find_last_of(L"\\/");
            return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash);
        }
        buffer.resize(buffer.size() * 2);
    }
}

std::wstring FindClientPath()
{
    HKEY key = nullptr;
    std::wstring path;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kMountKey, 0, KEY_QUERY_VALUE, &key) == ERROR_SUCCESS)
    {
        ReadRegistryString(key, L"ClientPath", path);
        RegCloseKey(key);
        if (!path.empty() && GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES)
            return path;
    }

    path = ModuleDirectory() + L"\\" + kClientExe;
    return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES ? path : std::wstring();
}

HRESULT LaunchClient()
{
    const std::wstring clientPath = FindClientPath();
    if (clientPath.empty())
        return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);

    SHELLEXECUTEINFOW execute = {sizeof(execute)};
    execute.fMask = SEE_MASK_FLAG_NO_UI | SEE_MASK_NOASYNC;
    execute.lpFile = clientPath.c_str();
    execute.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExW(&execute))
        return HRESULT_FROM_WIN32(GetLastError());
    return S_OK;
}

class ExplorerCommand final : public IExplorerCommand
{
public:
    explicit ExplorerCommand(CommandId id) noexcept : id_(id)
    {
        ModuleAddRef();
    }

    ~ExplorerCommand()
    {
        ModuleRelease();
    }

    IFACEMETHODIMP QueryInterface(REFIID iid, void** object) override
    {
        if (!object)
            return E_POINTER;
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_IExplorerCommand)
        {
            *object = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(InterlockedIncrement(&references_));
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const ULONG remaining = static_cast<ULONG>(InterlockedDecrement(&references_));
        if (!remaining)
            delete this;
        return remaining;
    }

    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override
    {
        if (id_ == CommandId::Root)
            return DuplicateString(L"TNovCloud", title);
        const auto* command = FindDefinition(id_);
        return command ? DuplicateString(command->title, title) : E_UNEXPECTED;
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        if (!icon)
            return E_POINTER;
        *icon = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tip) override
    {
        if (id_ == CommandId::Root)
            return DuplicateString(L"TNovCloud file commands", tip);
        const auto* command = FindDefinition(id_);
        return command ? DuplicateString(command->title, tip) : E_UNEXPECTED;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* commandName) override
    {
        if (!commandName)
            return E_POINTER;
        *commandName = CLSID_TNovCloudExplorerCommand;
        commandName->Data1 += static_cast<unsigned long>(id_);
        return S_OK;
    }

    IFACEMETHODIMP GetState(
        IShellItemArray* selection, BOOL, EXPCMDSTATE* state) override
    {
        if (!state)
            return E_POINTER;

        std::vector<std::wstring> paths;
        const HRESULT hr = SelectedFilePaths(selection, paths);
        const bool valid = SUCCEEDED(hr) && std::all_of(
            paths.begin(),
            paths.end(),
            [this](const std::wstring& path)
            {
                return IsTNovCloudPath(path, id_ != CommandId::Root);
            });
        *state = valid
                     ? ECS_ENABLED
                     : (id_ == CommandId::Root ? ECS_HIDDEN : ECS_DISABLED);
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* selection, IBindCtx*) override
    {
        const auto* command = FindDefinition(id_);
        if (!command)
            return E_NOTIMPL;

        std::vector<std::wstring> paths;
        RETURN_IF_FAILED(SelectedFilePaths(selection, paths));
        if (!std::all_of(
                paths.begin(),
                paths.end(),
                [](const std::wstring& path) { return IsTNovCloudPath(path, true); }))
            return HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED);

        if (SendPipeCommand(*command, paths))
            return S_OK;

        RETURN_IF_FAILED(LaunchClient());
        for (unsigned attempt = 0; attempt < 50; ++attempt)
        {
            Sleep(100);
            if (SendPipeCommand(*command, paths))
                return S_OK;
        }
        return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        if (!flags)
            return E_POINTER;
        *flags = id_ == CommandId::Root ? ECF_HASSUBCOMMANDS : ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override;

private:
    volatile long references_ = 1;
    CommandId id_;
};

class CommandEnumerator final : public IEnumExplorerCommand
{
public:
    CommandEnumerator() noexcept
    {
        ModuleAddRef();
    }

    ~CommandEnumerator()
    {
        ModuleRelease();
    }

    IFACEMETHODIMP QueryInterface(REFIID iid, void** object) override
    {
        if (!object)
            return E_POINTER;
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_IEnumExplorerCommand)
        {
            *object = static_cast<IEnumExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(InterlockedIncrement(&references_));
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const ULONG remaining = static_cast<ULONG>(InterlockedDecrement(&references_));
        if (!remaining)
            delete this;
        return remaining;
    }

    IFACEMETHODIMP Next(
        ULONG requested, IExplorerCommand** commands, ULONG* fetched) override
    {
        if (!commands || (requested != 1 && !fetched))
            return E_POINTER;
        if (fetched)
            *fetched = 0;

        ULONG produced = 0;
        while (produced < requested && index_ < ARRAYSIZE(kCommands))
        {
            auto* command = new (std::nothrow) ExplorerCommand(kCommands[index_++].id);
            if (!command)
            {
                for (ULONG i = 0; i < produced; ++i)
                    commands[i]->Release();
                return E_OUTOFMEMORY;
            }
            commands[produced++] = command;
        }

        if (fetched)
            *fetched = produced;
        return produced == requested ? S_OK : S_FALSE;
    }

    IFACEMETHODIMP Skip(ULONG count) override
    {
        const size_t remaining = ARRAYSIZE(kCommands) - index_;
        const size_t skipped = std::min<size_t>(count, remaining);
        index_ += skipped;
        return skipped == count ? S_OK : S_FALSE;
    }

    IFACEMETHODIMP Reset() override
    {
        index_ = 0;
        return S_OK;
    }

    IFACEMETHODIMP Clone(IEnumExplorerCommand** clone) override
    {
        if (!clone)
            return E_POINTER;
        *clone = nullptr;
        auto* result = new (std::nothrow) CommandEnumerator();
        if (!result)
            return E_OUTOFMEMORY;
        result->index_ = index_;
        *clone = result;
        return S_OK;
    }

private:
    volatile long references_ = 1;
    size_t index_ = 0;
};

HRESULT ExplorerCommand::EnumSubCommands(IEnumExplorerCommand** commands)
{
    if (!commands)
        return E_POINTER;
    *commands = nullptr;
    if (id_ != CommandId::Root)
        return E_NOTIMPL;

    auto* result = new (std::nothrow) CommandEnumerator();
    if (!result)
        return E_OUTOFMEMORY;
    *commands = result;
    return S_OK;
}

class ClassFactory final : public IClassFactory
{
public:
    ClassFactory() noexcept
    {
        ModuleAddRef();
    }

    ~ClassFactory()
    {
        ModuleRelease();
    }

    IFACEMETHODIMP QueryInterface(REFIID iid, void** object) override
    {
        if (!object)
            return E_POINTER;
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_IClassFactory)
        {
            *object = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(InterlockedIncrement(&references_));
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const ULONG remaining = static_cast<ULONG>(InterlockedDecrement(&references_));
        if (!remaining)
            delete this;
        return remaining;
    }

    IFACEMETHODIMP CreateInstance(
        IUnknown* outer, REFIID iid, void** object) override
    {
        if (outer)
            return CLASS_E_NOAGGREGATION;
        if (!object)
            return E_POINTER;
        *object = nullptr;

        auto* command = new (std::nothrow) ExplorerCommand(CommandId::Root);
        if (!command)
            return E_OUTOFMEMORY;
        const HRESULT hr = command->QueryInterface(iid, object);
        command->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        lock ? ModuleAddRef() : ModuleRelease();
        return S_OK;
    }

private:
    volatile long references_ = 1;
};
} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** object)
{
    if (clsid != CLSID_TNovCloudExplorerCommand)
        return CLASS_E_CLASSNOTAVAILABLE;
    if (!object)
        return E_POINTER;
    *object = nullptr;

    auto* factory = new (std::nothrow) ClassFactory();
    if (!factory)
        return E_OUTOFMEMORY;
    const HRESULT hr = factory->QueryInterface(iid, object);
    factory->Release();
    return hr;
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return InterlockedCompareExchange(&g_objectCount, 0, 0) == 0 ? S_OK : S_FALSE;
}
