#pragma once

#include <windows.h>

// Keep this value in sync with packaging/TNovCloud.Package/AppxManifest.xml.
inline constexpr CLSID CLSID_TNovCloudExplorerCommand = {
    0x4b8f4e39,
    0x392d,
    0x4b64,
    {0xa8, 0xb7, 0x7e, 0xa4, 0xd0, 0x5c, 0x57, 0x2f}};

extern HMODULE g_module;
extern volatile long g_objectCount;

void ModuleAddRef() noexcept;
void ModuleRelease() noexcept;
