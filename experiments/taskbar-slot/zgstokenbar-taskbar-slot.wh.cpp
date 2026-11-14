// ==WindhawkMod==
// @id              zgstokenbar-taskbar-slot
// @name            ZGSTokenBar taskbar slot (prototype)
// @description     Test a fixed-width XAML column in the Windows 11 notification area.
// @version         0.1.1
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

#include <atomic>
#include <cmath>
#include <cstdint>
#include <memory>
#include <vector>

namespace slot {
using namespace winrt;
using namespace Windows::UI::Xaml;
using namespace Windows::UI::Xaml::Controls;
using namespace Windows::UI::Xaml::Media;

constexpr double Width = 321; // 642 physical pixels at 200% display scaling.
constexpr wchar_t TileName[] = L"ZGSTokenBarTrialSlot";

// The reservation owns the tile, its column and the two layout subscriptions.
// It is created and destroyed on the same UI thread as the tray.
class Reservation {
public:
    Reservation() = default;
    Reservation(Reservation const&) = delete;
    Reservation& operator=(Reservation const&) = delete;

    ~Reservation() {
        // A revoked event source can still hold a delegate. A late call
        // must be harmless even after this reservation is gone.
        if (watch_) watch_->target = nullptr;
    }

    bool Attach(Grid const& tray, Button const& tile) {
        if (tray_ || !tray || !tile) return false;
        auto columns = tray.ColumnDefinitions();
        auto children = tray.Children();
        if (!columns.Size() || columns.Size() >= 32 || children.Size() > 64) return false;
        std::vector<Shift> shifted;
        shifted.reserve(children.Size());
        for (auto const& child : children) {
            auto element = child.try_as<FrameworkElement>();
            if (!element || element.Name() == TileName || element.GetBindingExpression(Grid::ColumnProperty()))
                return false;
            const int index = Grid::GetColumn(element);
            if (index < 0 || static_cast<uint32_t>(index) >= columns.Size()) return false;
            shifted.push_back({element, index,
                element.ReadLocalValue(Grid::ColumnProperty()) == DependencyProperty::UnsetValue()});
        }
        rejected_ = false;
        tile.Visibility(Visibility::Visible);
        ColumnDefinition column;
        column.Width({Width, GridUnitType::Pixel});
        tile.Name(TileName);
        Grid::SetColumn(tile, 0);
        // Publish the rollback receipt before invoking Shell-owned XAML code.
        // Even a partial mutation can then be retried by the owner thread.
        tray_ = tray;
        tile_ = tile;
        column_ = column;
        shifted_ = std::move(shifted);
        try {
            columns.InsertAt(0, column);
            for (auto const& change : shifted_) Grid::SetColumn(change.child, change.before + 1);
            children.Append(tile);
        } catch (...) {
            RevokeForSafety();
            throw;
        }
        return true;
    }

    bool HasFullWidth() const {
        return tile_ && std::abs(tile_.ActualWidth() - Width) <= 1 && tile_.ActualHeight() >= 24;
    }

    bool Active() const noexcept { return static_cast<bool>(tray_); }
    bool Rejected() const noexcept { return rejected_; }
    bool InLayoutCallback() const noexcept { return checking_; }

    void WatchLayout() {
        watch_ = std::make_shared<WatchState>(WatchState{this});
        layout_ = tray_.LayoutUpdated(auto_revoke, [watch = watch_](auto const&, auto const&) noexcept {
            if (auto target = watch->target) target->Recheck();
        });
        unloaded_ = tray_.Unloaded(auto_revoke, [watch = watch_](auto const&, auto const&) noexcept {
            if (auto target = watch->target) {
                // Unloaded can reenter an active layout or rollback. Mark the
                // receipt first, then let the current operation finish.
                target->rejected_ = true;
                target->Recheck();
            }
        });
    }

    // LayoutUpdated also fires for unrelated elements. Keep the pass bounded and
    // check the current arranged bounds, not just the column's requested width.
    void Recheck() noexcept {
        if (!tray_ || checking_) return;
        struct Guard {
            bool& checking;
            ~Guard() { checking = false; }
        } guard{checking_};
        checking_ = true;
        try {
            if (!rejected_ && LayoutIsClear() && !rejected_) return;
        } catch (...) {}
        RevokeForSafety();
    }

    void Remove() {
        if (!tray_) return;
        struct Guard {
            bool& checking;
            bool prior;
            ~Guard() { checking = prior; }
        } guard{checking_, checking_};
        checking_ = true;
        auto columns = tray_.ColumnDefinitions();
        auto children = tray_.Children();
        uint32_t index{};
        if (tile_.Visibility() != Visibility::Collapsed) tile_.Visibility(Visibility::Collapsed);
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
        layout_.revoke();
        unloaded_.revoke();
        if (watch_) {
            watch_->target = nullptr;
            watch_.reset();
        }
        tile_ = nullptr;
        column_ = nullptr;
        tray_ = nullptr;
        shifted_.clear();
    }

private:
    struct Shift { FrameworkElement child; int before; bool inheritedColumn; };
    struct WatchState { Reservation* target; }; // XAML delegates run on the owner thread.

    bool LayoutIsClear() const {
        if (!HasFullWidth()) return false;
        auto columns = tray_.ColumnDefinitions();
        auto children = tray_.Children();
        uint32_t index{};
        if (children.Size() > 64 || !children.IndexOf(tile_, index)
            || !columns.IndexOf(column_, index) || index != 0 || Grid::GetColumn(tile_) != 0)
            return false;
        auto root = tray_.XamlRoot();
        auto content = root ? root.Content() : nullptr;
        if (!content) return false;
        const auto bounds = BoundsIn(tile_, content);
        const auto size = root.Size();
        if (bounds.X < -0.5 || bounds.Y < -0.5 || bounds.Width <= 0 || bounds.Height <= 0
            || bounds.X + bounds.Width > size.Width + 0.5 || bounds.Y + bounds.Height > size.Height + 0.5)
            return false;
        for (auto const& child : children) {
            if (child == tile_ || child.Visibility() != Visibility::Visible) continue;
            auto element = child.try_as<FrameworkElement>();
            if (!element || Grid::GetColumn(element) == 0) return false;
            const auto other = BoundsIn(element, content);
            if (Overlaps(bounds, other)) return false;
        }
        return true;
    }

    static bool Overlaps(Windows::Foundation::Rect const& first, Windows::Foundation::Rect const& second) {
        return second.Width > 0 && second.Height > 0
            && first.X < second.X + second.Width - 0.5 && second.X < first.X + first.Width - 0.5
            && first.Y < second.Y + second.Height - 0.5 && second.Y < first.Y + first.Height - 0.5;
    }

    static Windows::Foundation::Rect BoundsIn(FrameworkElement const& element, UIElement const& root) {
        auto bounds = element.TransformToVisual(root).TransformBounds(
            {0, 0, float(element.ActualWidth()), float(element.ActualHeight())});
        if (!std::isfinite(bounds.X) || !std::isfinite(bounds.Y)
            || !std::isfinite(bounds.Width) || !std::isfinite(bounds.Height))
            throw hresult_error(E_FAIL, L"Non-finite tray bounds");
        return bounds;
    }

    void RevokeForSafety() noexcept {
        rejected_ = true;
        try { Remove(); } catch (...) {
            // Leave the layout subscription in place so a later layout can
            // retry a failed rollback. The tile stays collapsed in the meantime.
        }
    }

    static void RestoreColumn(Shift const& change) {
        if (change.inheritedColumn) change.child.ClearValue(Grid::ColumnProperty());
        else Grid::SetColumn(change.child, change.before);
    }

    Grid tray_{nullptr};
    Button tile_{nullptr};
    ColumnDefinition column_{nullptr};
    std::vector<Shift> shifted_;
    FrameworkElement::LayoutUpdated_revoker layout_;
    FrameworkElement::Unloaded_revoker unloaded_;
    std::shared_ptr<WatchState> watch_;
    bool checking_{};
    bool rejected_{};
};

Button CreateTile() {
    Button tile;
    tile.Content(box_value(L"TokenBar  |  642 px at 200%"));
    tile.HorizontalAlignment(HorizontalAlignment::Stretch);
    tile.VerticalAlignment(VerticalAlignment::Stretch);
    tile.HorizontalContentAlignment(HorizontalAlignment::Center);
    return tile;
}

enum class DispatchResult { Delivered, Failed, TimedOut };

// Successful SendMessageTimeout is also the fence for the native hook. On
// timeout the receiving procedure may still be running; the caller owns the
// callback's lifetime independently of this result.
DispatchResult Dispatch(HWND hwnd, UINT message, unsigned long serial, UINT timeout) noexcept {
    DWORD_PTR ignored{};
    SetLastError(ERROR_SUCCESS);
    if (SendMessageTimeoutW(hwnd, message, serial, 0,
            SMTO_ABORTIFHUNG | SMTO_BLOCK | SMTO_ERRORONEXIT, timeout, &ignored))
        return DispatchResult::Delivered;
    const auto error = GetLastError();
    return error == ERROR_TIMEOUT || error == ERROR_SUCCESS
        ? DispatchResult::TimedOut : DispatchResult::Failed;
}

} // namespace slot

#ifndef ZGS_TASKBAR_SLOT_FIXTURE
#include <windhawk_utils.h>
#include <string_view>

namespace {
using namespace slot;

void* taskListSiteVtable{};
void* (WINAPI* getTaskbarHost)(void*, void*){};
int (WINAPI* frameHeight)(void*){};
void (WINAPI* releaseHost)(void*){};
size_t elementOffset{};

constexpr UINT DispatchTimeoutMs = 750;
constexpr UINT CleanupRetryMs = 200;

// Only the taskbar thread owns the XAML objects. Do not run their destructors
// from an arbitrary thread under the loader lock when Explorer itself exits.
Reservation* reservation{};
UINT_PTR cleanupTimer{};
bool applying{}, removing{}; // Taskbar-thread-only reentrancy guards.
std::atomic<bool> enableAllowed{};
std::atomic<unsigned long> requestSerial{};
std::atomic<unsigned long> completedSerial{};
std::atomic<HWND> owner{};
std::atomic<UINT> enableCommand{}, removeCommand{};
std::atomic<bool> active{};
std::atomic<bool> faulted{};
std::atomic<bool> unloading{};
std::atomic<bool> rollbackOnly{};
SRWLOCK lifecycleLock = SRWLOCK_INIT;

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

void StopTimerOnTaskbar() noexcept {
    if (cleanupTimer) {
        KillTimer(nullptr, cleanupTimer);
        cleanupTimer = 0;
    }
}

// The timer is armed before the first Shell mutation. If a stop times out or
// dispatch cannot reach the owner, it retries rollback when the UI pumps again.
void RemoveOnTaskbar() noexcept {
    // XAML can pump messages while mutating the visual tree. The nested
    // dispatch is not allowed to destroy the reservation being worked on.
    if (removing || (reservation && reservation->InLayoutCallback())) return;
    removing = true;
    try {
        if (reservation) {
            reservation->Remove();
            delete reservation;
            reservation = nullptr;
        }
        active.store(false);
        StopTimerOnTaskbar();
    } catch (...) {
        faulted.store(true);
    }
    removing = false;
}

void CALLBACK CheckCleanup(HWND, UINT, UINT_PTR id, DWORD) noexcept {
    if (id != cleanupTimer || applying) return; // KillTimer can leave a queued tick.
    if (unloading.load() || !enableAllowed.load() || (reservation && reservation->Rejected()))
        RemoveOnTaskbar();
}

void ApplyOnTaskbar(HWND hwnd, bool enable, unsigned long serial) noexcept {
    if (applying) return;
    struct Reset { bool& flag; ~Reset() { flag = false; } } reset{applying};
    applying = true;
    // An expired enable can finish cleanup, but must never make a late change.
    if (enable && (serial != requestSerial.load() || unloading.load() || !enableAllowed.load())) return;
    faulted.store(false);
    try {
        if (!enable) {
            RemoveOnTaskbar();
        } else if (!reservation) {
            RECT bounds{};
            if (GetWindowRect(hwnd, &bounds) && bounds.right - bounds.left > bounds.bottom - bounds.top) {
                auto tray = FindPrimaryTray(hwnd);
                if (tray && enableAllowed.load() && serial == requestSerial.load()) {
                    cleanupTimer = SetTimer(nullptr, 0, CleanupRetryMs, CheckCleanup);
                    if (!cleanupTimer) faulted.store(true);
                    else {
                        reservation = new Reservation();
                        active.store(true);
                        if (reservation->Attach(tray, CreateTile())) {
                            tray.UpdateLayout();
                            reservation->WatchLayout();
                            if (enableAllowed.load() && !unloading.load()) reservation->Recheck();
                        }
                        if (!reservation->Active() || !enableAllowed.load() || unloading.load())
                            RemoveOnTaskbar();
                    }
                }
            }
        }
    } catch (...) {
        faulted.store(true);
        RemoveOnTaskbar();
    }
    completedSerial.store(serial);
}

LRESULT CALLBACK DispatchOnOwner(int code, WPARAM wparam, LPARAM lparam) {
    if (code == HC_ACTION && lparam) {
        auto const& message = *reinterpret_cast<CWPSTRUCT const*>(lparam);
        const bool enable = message.message == enableCommand.load();
        if (message.hwnd == owner.load() && (enable || message.message == removeCommand.load())
            && message.wParam == requestSerial.load())
            ApplyOnTaskbar(message.hwnd, enable, static_cast<unsigned long>(message.wParam));
    }
    return CallNextHookEx(nullptr, code, wparam, lparam);
}

// A timed-out native hook and a queued XAML/timer callback can each outlive
// Windhawk's unload callback. Pin only after an explicit trial enable. This
// keeps code mapped until Explorer exits, even when rollback is still pending.
// After a successful stop there are no live subscriptions or timers.
bool KeepCallbackCodeMapped() {
    HMODULE module{};
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<PCWSTR>(&DispatchOnOwner), &module) != FALSE;
}

void Change(bool enable) {
    if (!enable) enableAllowed.store(false);
    if (!TryAcquireSRWLockExclusive(&lifecycleLock)) {
        Wh_Log(L"TokenBar trial: lifecycle busy; %s", enable ? L"enable not applied." :
            L"stop requested; rollback will run on the taskbar thread.");
        return;
    }
    struct Unlock { ~Unlock() { ReleaseSRWLockExclusive(&lifecycleLock); } } unlock;
    if (enable && (unloading.load() || rollbackOnly.load())) {
        Wh_Log(L"TokenBar trial: enable refused after incomplete dispatch or unload. Restart Explorer before retrying.");
        return;
    }

    HWND hwnd = owner.load();
    if (enable && !active.load()) {
        hwnd = FindWindowW(L"Shell_TrayWnd", nullptr);
        DWORD pid{};
        if (!hwnd || !GetWindowThreadProcessId(hwnd, &pid) || pid != GetCurrentProcessId()
            || IsHungAppWindow(hwnd) || GetSystemMetrics(SM_REMOTESESSION)
            || !IsTrialBuild() || !ResolveShell() || !KeepCallbackCodeMapped()) {
            Wh_Log(L"TokenBar trial: unsupported OS build, taskbar symbols or callback lifetime.");
            return;
        }
        owner.store(hwnd);
    }
    if (!hwnd) return;
    DWORD pid{};
    const DWORD thread = GetWindowThreadProcessId(hwnd, &pid);
    if (!thread || pid != GetCurrentProcessId()) {
        Wh_Log(L"TokenBar trial: primary taskbar unavailable; rollback not confirmed.");
        return;
    }
    if (enable && active.load() && !enableAllowed.load()) {
        Wh_Log(L"TokenBar trial: previous stop still pending; enable refused.");
        return;
    }
    enableAllowed.store(enable);
    const auto serial = ++requestSerial;
    DispatchResult result = DispatchResult::Delivered;
    if (thread == GetCurrentThreadId()) {
        ApplyOnTaskbar(hwnd, enable, serial);
    } else {
        if (!enableCommand.load()) enableCommand.store(RegisterWindowMessageW(L"ZGSTokenBar.TrialSlot.Enable.82e92dfe"));
        if (!removeCommand.load()) removeCommand.store(RegisterWindowMessageW(L"ZGSTokenBar.TrialSlot.Remove.82e92dfe"));
        const auto command = enable ? enableCommand.load() : removeCommand.load();
        HHOOK hook = command ? SetWindowsHookExW(WH_CALLWNDPROC, DispatchOnOwner, nullptr, thread) : nullptr;
        if (hook) {
            result = Dispatch(hwnd, command, serial, DispatchTimeoutMs);
            if (!UnhookWindowsHookEx(hook)) result = DispatchResult::Failed;
        } else {
            result = DispatchResult::Failed;
        }
    }
    if (result != DispatchResult::Delivered || completedSerial.load() != serial) {
        rollbackOnly.store(true);
        enableAllowed.store(false);
        ++requestSerial;
        Wh_Log(L"TokenBar trial: %s; rollback not confirmed. If a slot is active, the taskbar thread will retry when responsive.",
            result == DispatchResult::TimedOut ? L"taskbar dispatch timed out" : L"taskbar dispatch failed");
        return;
    }
    if (faulted.load()) {
        enableAllowed.store(false);
        Wh_Log(L"TokenBar trial: layout operation failed; rollback may still be pending.");
    } else if (!enable && active.load()) {
        Wh_Log(L"TokenBar trial: stop requested; rollback will run after the current layout callback.");
    } else {
        Wh_Log(L"TokenBar trial: %s", active.load() ? L"321 DIP column active." :
            enable ? L"no supported, clear tray slot." : L"column removed.");
    }
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
