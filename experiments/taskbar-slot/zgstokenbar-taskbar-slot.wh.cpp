// ==WindhawkMod==
// @id              zgstokenbar-taskbar-slot
// @name            ZGSTokenBar taskbar slot (prototype)
// @description     Test a fixed-width XAML column in the Windows 11 notification area.
// @version         0.1.0
// @author          ZeroGameStudio
// @include         explorer.exe
// @architecture    x86-64
// @compilerOptions -lole32 -loleaut32 -lruntimeobject -ladvapi32
// ==/WindhawkMod==

// ==WindhawkModSettings==
/*
- reserveColumn: false
  $name: Show the trial slot
  $description: Reserve 321 logical pixels for a clickable test tile on the primary taskbar.
*/
// ==/WindhawkModSettings==

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#undef GetCurrentTime
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.h>

#include <cmath>
#include <cstdint>
#include <vector>

namespace slot {
using namespace winrt;
using namespace Windows::UI::Xaml;
using namespace Windows::UI::Xaml::Controls;
using namespace Windows::UI::Xaml::Media;

constexpr double Width = 321; // 642 physical pixels at 200% display scaling.
constexpr wchar_t TileName[] = L"ZGSTokenBarTrialSlot";

// A receipt owns only this tile and its column. No XAML callbacks need to be
// installed in Explorer, and existing children keep their own handlers/styles.
class Reservation {
public:
    bool Attach(Grid const& tray, Button const& tile) {
        if (tray_ || !tray || !tile) return false;
        auto columns = tray.ColumnDefinitions();
        auto children = tray.Children();
        if (!columns.Size() || columns.Size() >= 32 || children.Size() > 64) return false;
        for (auto const& child : children) {
            auto element = child.try_as<FrameworkElement>();
            if (!element || element.Name() == TileName
                || element.GetBindingExpression(Grid::ColumnProperty())
                || Grid::GetColumn(element) < 0
                || static_cast<uint32_t>(Grid::GetColumn(element)) >= columns.Size())
                return false;
        }

        ColumnDefinition column;
        column.Width({Width, GridUnitType::Pixel});
        tile.Name(TileName);
        Grid::SetColumn(tile, 0);

        std::vector<Shift> shifted;
        shifted.reserve(children.Size());
        bool inserted = false;
        try {
            columns.InsertAt(0, column);
            inserted = true;
            for (auto const& child : children) {
                auto element = child.as<FrameworkElement>();
                const int before = Grid::GetColumn(element);
                shifted.push_back({element, before,
                    element.ReadLocalValue(Grid::ColumnProperty()) == DependencyProperty::UnsetValue()});
                Grid::SetColumn(element, before + 1);
            }
            children.Append(tile);
        } catch (...) {
            uint32_t index{};
            if (children.IndexOf(tile, index)) children.RemoveAt(index);
            for (auto it = shifted.rbegin(); it != shifted.rend(); ++it) RestoreColumn(*it);
            if (inserted && columns.IndexOf(column, index)) columns.RemoveAt(index);
            throw;
        }
        tray_ = tray;
        tile_ = tile;
        column_ = column;
        shifted_ = std::move(shifted);
        return true;
    }

    bool HasFullWidth() const {
        return tile_ && std::abs(tile_.ActualWidth() - Width) <= 1 && tile_.ActualHeight() >= 24;
    }

    void Remove() {
        if (!tray_) return;
        auto columns = tray_.ColumnDefinitions();
        auto children = tray_.Children();
        uint32_t index{};
        if (children.IndexOf(tile_, index)) children.RemoveAt(index);
        if (columns.IndexOf(column_, index)) {
            for (auto const& change : shifted_) {
                uint32_t childIndex{};
                if (children.IndexOf(change.child, childIndex)
                    && !change.child.GetBindingExpression(Grid::ColumnProperty())
                    && Grid::GetColumn(change.child) == change.before + 1)
                    RestoreColumn(change);
            }
            columns.RemoveAt(index);
        }
        tile_ = nullptr;
        column_ = nullptr;
        tray_ = nullptr;
        shifted_.clear();
    }

private:
    struct Shift { FrameworkElement child; int before; bool inheritedColumn; };

    static void RestoreColumn(Shift const& change) {
        if (change.inheritedColumn) change.child.ClearValue(Grid::ColumnProperty());
        else Grid::SetColumn(change.child, change.before);
    }

    Grid tray_{nullptr};
    Button tile_{nullptr};
    ColumnDefinition column_{nullptr};
    std::vector<Shift> shifted_;
};

Button CreateTile() {
    Button tile;
    tile.Content(box_value(L"TokenBar  |  642 px at 200%"));
    tile.HorizontalAlignment(HorizontalAlignment::Stretch);
    tile.VerticalAlignment(VerticalAlignment::Stretch);
    tile.HorizontalContentAlignment(HorizontalAlignment::Center);
    return tile;
}

} // namespace slot

#ifndef ZGS_TASKBAR_SLOT_FIXTURE
#include <windhawk_utils.h>
#include <atomic>
#include <string_view>

namespace {
using namespace slot;

void* taskListSiteVtable{};
void* (WINAPI* getTaskbarHost)(void*, void*){};
int (WINAPI* frameHeight)(void*){};
void (WINAPI* releaseHost)(void*){};
size_t elementOffset{};

Reservation reservation; // Accessed on the primary taskbar's own thread only.
HWND owner{};
UINT command{};
bool attached{};
std::atomic<bool> desired{};
std::atomic<bool> applied{};
std::atomic<bool> unloading{};

// x64 shell bridge based on Taskbar Media Presence (MIT), pinned in README.md.
// Admit only the trial OS build and the known instruction shape; never use a
// guessed object offset.
bool IsTrialBuild() {
    DWORD revision{};
    DWORD size = sizeof(revision);
    constexpr wchar_t key[] = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";
    wchar_t build[16]{};
    DWORD buildSize = sizeof(build);
    return RegGetValueW(HKEY_LOCAL_MACHINE, key, L"CurrentBuild", RRF_RT_REG_SZ,
                        nullptr, build, &buildSize) == ERROR_SUCCESS
        && std::wstring_view(build) == L"26300"
        && RegGetValueW(HKEY_LOCAL_MACHINE, key, L"UBR", RRF_RT_REG_DWORD,
                        nullptr, &revision, &size) == ERROR_SUCCESS
        && revision == 9974;
}

bool Readable(void const* address, size_t length) {
    MEMORY_BASIC_INFORMATION region{};
    if (!address || VirtualQuery(address, &region, sizeof(region)) != sizeof(region)
        || region.State != MEM_COMMIT || (region.Protect & (PAGE_GUARD | PAGE_NOACCESS)))
        return false;
    const auto offset = reinterpret_cast<uintptr_t>(address) - reinterpret_cast<uintptr_t>(region.BaseAddress);
    return offset <= region.RegionSize && length <= region.RegionSize - offset;
}

bool ResolveShell() {
    if (elementOffset) return true;
    auto module = GetModuleHandleW(L"taskbar.dll");
    if (!module) return false; // Only inspect a taskbar already loaded by Explorer.
    WindhawkUtils::SYMBOL_HOOK symbols[]{
        {{L"const CTaskBand::`vftable'{for `ITaskListWndSite'}"}, &taskListSiteVtable},
        {{L"public: virtual class std::shared_ptr<class TaskbarHost> __cdecl CTaskBand::GetTaskbarHost(void)const "}, &getTaskbarHost},
        {{L"public: int __cdecl TaskbarHost::FrameHeight(void)const "}, &frameHeight},
        {{L"public: void __cdecl std::_Ref_count_base::_Decref(void)"}, &releaseHost},
    };
    if (!WindhawkUtils::HookSymbols(module, symbols, ARRAYSIZE(symbols))) return false;
    const auto bytes = reinterpret_cast<unsigned char const*>(frameHeight);
    if (!Readable(bytes, 8)
        || bytes[0] != 0x48 || bytes[1] != 0x83 || bytes[2] != 0xec || bytes[3] != 0x28
        || bytes[4] != 0x48 || bytes[5] != 0x83 || bytes[6] != 0xc1
        || bytes[7] < sizeof(void*) || bytes[7] > 0x7f || bytes[7] % sizeof(void*))
        return false;
    elementOffset = bytes[7];
    return true;
}

Grid FindTrayGrid(DependencyObject const& element, unsigned depth, unsigned& budget) {
    if (!element || depth > 8 || !budget) return nullptr;
    --budget;
    if (auto grid = element.try_as<Grid>(); grid && grid.Name() == L"SystemTrayFrameGrid") return grid;
    for (int i=0, count=VisualTreeHelper::GetChildrenCount(element); i<count && budget; ++i) {
        if (auto grid = FindTrayGrid(VisualTreeHelper::GetChild(element, i), depth+1, budget)) return grid;
    }
    return nullptr;
}

Grid FindPrimaryTray(HWND hwnd) {
    DWORD pid{};
    if (GetWindowThreadProcessId(hwnd, &pid) != GetCurrentThreadId() || pid != GetCurrentProcessId())
        return nullptr;
    auto bandWindow = reinterpret_cast<HWND>(GetPropW(hwnd, L"TaskbandHWND"));
    DWORD bandPid{};
    if (!bandWindow || !GetWindowThreadProcessId(bandWindow, &bandPid) || bandPid != pid)
        return nullptr;
    auto band = reinterpret_cast<void**>(GetWindowLongPtrW(bandWindow, 0));
    if (!band) return nullptr;
    for (unsigned i=0; i<=20; ++i) {
        auto site = band + i;
        if (!Readable(site, sizeof(*site))) return nullptr;
        if (*site != taskListSiteVtable) continue;
        struct Host {
            void* data{};
            void* ref{};
            ~Host() { if (ref) releaseHost(ref); }
        } host;
        getTaskbarHost(site, &host);
        if (!host.data) return nullptr;
        auto member = reinterpret_cast<IUnknown**>(static_cast<unsigned char*>(host.data) + elementOffset);
        if (!Readable(member, sizeof(*member)) || !Readable(*member, sizeof(void*))) return nullptr;
        FrameworkElement taskbar{nullptr};
        if (FAILED((*member)->QueryInterface(guid_of<FrameworkElement>(), put_abi(taskbar))) || !taskbar)
            return nullptr;
        auto root = taskbar.XamlRoot();
        unsigned budget = 350;
        return root ? FindTrayGrid(root.Content(), 0, budget) : nullptr;
    }
    return nullptr;
}

void ApplyOnTaskbar(HWND hwnd) {
    try {
        const bool enable = desired.load();
        if (enable && !attached) {
            RECT bounds{};
            if (GetWindowRect(hwnd, &bounds) && bounds.right - bounds.left > bounds.bottom - bounds.top) {
                if (auto tray = FindPrimaryTray(hwnd); tray && reservation.Attach(tray, CreateTile())) {
                    attached = true;
                    tray.UpdateLayout();
                    if (!reservation.HasFullWidth()) {
                        reservation.Remove();
                        attached = false;
                    }
                }
            }
        } else if (!enable && attached) {
            reservation.Remove();
            attached = false;
        }
    } catch (...) {
        Wh_Log(L"TokenBar trial column mutation failed.");
    }
    applied.store(attached);
}

LRESULT CALLBACK DispatchOnOwner(int code, WPARAM wparam, LPARAM lparam) {
    if (code == HC_ACTION && lparam) {
        auto const& message = *reinterpret_cast<CWPSTRUCT const*>(lparam);
        if (message.message == command && message.hwnd == owner) ApplyOnTaskbar(message.hwnd);
    }
    return CallNextHookEx(nullptr, code, wparam, lparam);
}

void Change(bool enable) {
    if (enable && unloading.load()) return;
    desired.store(enable);
    if (enable && !applied.load()) {
        owner = FindWindowW(L"Shell_TrayWnd", nullptr);
        DWORD pid{};
        if (!owner || !GetWindowThreadProcessId(owner, &pid) || pid != GetCurrentProcessId()
            || IsHungAppWindow(owner) || GetSystemMetrics(SM_REMOTESESSION)
            || !IsTrialBuild() || !ResolveShell()) {
            Wh_Log(L"TokenBar trial: unsupported OS build or taskbar symbols.");
            return;
        }
    }
    if (!owner) return;
    DWORD pid{};
    const DWORD thread = GetWindowThreadProcessId(owner, &pid);
    if (!thread || pid != GetCurrentProcessId()) {
        Wh_Log(L"TokenBar trial: primary taskbar is unavailable.");
        return;
    }
    if (thread == GetCurrentThreadId()) {
        ApplyOnTaskbar(owner);
    } else {
        if (!command) command = RegisterWindowMessageW(L"ZGSTokenBar.TrialSlot.82e92dfe");
        // A one-shot hook on this Explorer thread. No persistent window
        // subclass or queued callback points back into the mod after unload.
        HHOOK hook = command ? SetWindowsHookExW(WH_CALLWNDPROC, DispatchOnOwner, nullptr, thread) : nullptr;
        if (!hook) { Wh_Log(L"TokenBar trial: owner-thread dispatch unavailable."); return; }
        SendMessageW(owner, command, 0, 0);
        UnhookWindowsHookEx(hook);
    }
    const bool result = applied.load();
    if (!result) owner = nullptr;
    Wh_Log(L"TokenBar trial: %s", result ? L"321 DIP column active." :
        enable ? L"no supported tray grid." : L"column removed.");
}
} // namespace

BOOL Wh_ModInit() { return TRUE; }

void Wh_ModAfterInit() {
    try { if (Wh_GetIntSetting(L"reserveColumn")) Change(true); }
    catch (...) { Wh_Log(L"TokenBar trial initialization failed."); }
}

void Wh_ModSettingsChanged() {
    try { Change(Wh_GetIntSetting(L"reserveColumn") != 0); }
    catch (...) { Wh_Log(L"TokenBar trial settings change failed."); }
}

void Wh_ModBeforeUninit() {
    unloading.store(true);
    try { Change(false); }
    catch (...) { Wh_Log(L"TokenBar trial cleanup failed."); }
}
#endif
