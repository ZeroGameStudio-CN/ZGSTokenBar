#define ZGS_TASKBAR_SLOT_FIXTURE
#include "zgstokenbar-taskbar-slot.wh.cpp"

#include <windows.ui.xaml.hosting.desktopwindowxamlsource.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <winrt/Windows.UI.Xaml.Input.h>

#include <cmath>
#include <iostream>
#include <stdexcept>
#include <string_view>

using namespace slot;
using Windows::Foundation::Point;
using Windows::UI::Color;
using Windows::UI::Xaml::Hosting::DesktopWindowXamlSource;
using Windows::UI::Xaml::Input::FocusManager;

namespace {

void Require(bool pass, char const* message) {
    if (!pass) throw std::runtime_error(message);
}

void Near(double actual, double expected, char const* message) {
    if (std::abs(actual - expected) > 1.0) {
        std::cerr << message << ": expected " << expected << ", got " << actual << '\n';
        throw std::runtime_error(message);
    }
}

void Pump() {
    MSG msg{};
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

Button AddButton(Panel const& panel, wchar_t const* text, double width) {
    Button button;
    button.Content(box_value(text));
    button.Width(width);
    button.Height(38);
    panel.Children().Append(button);
    return button;
}

double Left(FrameworkElement const& element, Grid const& root) {
    return element.TransformToVisual(root).TransformPoint({0, 0}).X;
}

LRESULT CALLBACK HostProc(HWND hwnd, UINT msg, WPARAM wparam, LPARAM lparam) {
    if (msg == WM_DESTROY) PostQuitMessage(0);
    return DefWindowProcW(hwnd, msg, wparam, lparam);
}

int Run(bool preview) {
    init_apartment(apartment_type::single_threaded);
    WNDCLASSW cls{};
    cls.hInstance = GetModuleHandleW(nullptr);
    cls.lpfnWndProc = HostProc;
    cls.lpszClassName = L"TokenBarSlotFixture";
    Require(RegisterClassW(&cls) != 0, "Register fixture host");
    HWND hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, cls.lpszClassName, L"TokenBar slot lab",
        WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 1960, 460,
        nullptr, nullptr, cls.hInstance, nullptr);
    Require(hwnd != nullptr, "Create fixture host");

    DesktopWindowXamlSource island;
    auto native = island.as<IDesktopWindowXamlSourceNative>();
    check_hresult(native->AttachToWindow(hwnd));
    HWND child{};
    check_hresult(native->get_WindowHandle(&child));
    const double dpi = GetDpiForWindow(hwnd) / 96.0;
    constexpr double rowWidth = 900;
    constexpr int rowHeight = 48;
    SetWindowPos(child, nullptr, 0, 0, static_cast<int>(rowWidth * dpi),
        static_cast<int>(rowHeight * dpi), SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

    Grid root;
    root.Width(rowWidth);
    root.Height(rowHeight);
    root.HorizontalAlignment(HorizontalAlignment::Left);
    root.VerticalAlignment(VerticalAlignment::Top);
    root.Background(SolidColorBrush(Color{255, 27, 37, 51}));
    ColumnDefinition appsColumn;
    appsColumn.Width({1, GridUnitType::Star});
    root.ColumnDefinitions().Append(appsColumn);
    ColumnDefinition trayColumn;
    trayColumn.Width({1, GridUnitType::Auto});
    root.ColumnDefinitions().Append(trayColumn);

    ScrollViewer scroll;
    scroll.HorizontalScrollMode(ScrollMode::Enabled);
    scroll.HorizontalScrollBarVisibility(ScrollBarVisibility::Hidden);
    scroll.VerticalScrollMode(ScrollMode::Disabled);
    scroll.VerticalScrollBarVisibility(ScrollBarVisibility::Disabled);
    StackPanel apps;
    apps.Orientation(Orientation::Horizontal);
    apps.VerticalAlignment(VerticalAlignment::Center);
    scroll.Content(apps);
    root.Children().Append(scroll);
    auto start = AddButton(apps, L"Start", 60);
    for (int i=0; i<6; ++i) AddButton(apps, L"App", 56);

    Grid tray;
    tray.Name(L"SystemTrayFrameGrid");
    Grid::SetColumn(tray, 1);
    root.Children().Append(tray);
    ColumnDefinition first;
    first.Width({54, GridUnitType::Pixel});
    tray.ColumnDefinitions().Append(first);
    ColumnDefinition second;
    second.Width({76, GridUnitType::Pixel});
    tray.ColumnDefinitions().Append(second);
    auto quick = AddButton(tray, L"Tray", 54);
    auto clock = AddButton(tray, L"Clock", 76);
    Grid::SetColumn(clock, 1);

    island.Content(root);
    // XAML Islands only expose arranged and hit-testable content when shown.
    // Use a tool window and never activate it or change the Explorer taskbar.
    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    auto arrange = [&] {
        for (int i=0; i<6; ++i) {
            root.Measure({rowWidth, rowHeight});
            root.Arrange({0, 0, rowWidth, rowHeight});
            root.UpdateLayout();
            Pump();
            Sleep(12);
        }
    };
    arrange();
    const auto baseline = scroll.ActualWidth();
    const auto trayBaseline = tray.ActualWidth();
    Near(baseline, rowWidth - 130, "Baseline includes the available viewport");
    Near(trayBaseline, 130, "Baseline tray width");

    Reservation reserved;
    auto tile = CreateTile();
    Require(reserved.Attach(tray, tile), "Insert the same reservation as the mod");
    Require(!reserved.Attach(tray, tile), "Repeated enable must not add another column");
    arrange();
    Near(tray.ActualWidth() - trayBaseline, Width, "Tray grows by exactly 321 DIP");
    Near(baseline - scroll.ActualWidth(), Width, "App area yields exactly 321 DIP");
    Require(reserved.HasFullWidth(), "The tile fills its whole reserved column");
    Require(Left(tile, root) >= scroll.ActualWidth()
        && Left(tile, root) + tile.ActualWidth() <= Left(quick, root),
        "The trial button is between the app viewport and native tray buttons");

    for (int i=0; i<26; ++i) AddButton(apps, L"App", 56);
    arrange();
    Require(scroll.ExtentWidth() > scroll.ViewportWidth(), "Crowding causes overflow");
    Near(baseline - scroll.ActualWidth(), Width, "A crowded row still reserves the entire slot");
    Require(reserved.HasFullWidth(), "Crowding does not shrink the trial width");
    // UIElement hit testing exercises XAML's arranged target bounds; no pointer injection.
    auto hit = [&](Button const& button) {
        const auto point = button.TransformToVisual(root).TransformPoint(
            {float(button.ActualWidth() / 2), float(button.ActualHeight() / 2)});
        const auto elements = VisualTreeHelper::FindElementsInHostCoordinates(point, root);
        for (auto const& element : elements) if (element == button) return true;
        return false;
    };
    Require(hit(start) && hit(tile) && hit(quick) && hit(clock),
        "All four XAML button targets remain hit-testable");

    // Explorer can recreate an unrelated button with its own column while the
    // reservation is active. Rollback must leave that child alone.
    auto newTrayButton = AddButton(tray, L"New", 38);
    Grid::SetColumn(newTrayButton, 1);
    reserved.Remove();
    reserved.Remove();
    Require(Grid::GetColumn(newTrayButton) == 1, "Disable leaves newly added controls alone");
    uint32_t newChildIndex{};
    Require(tray.Children().IndexOf(newTrayButton, newChildIndex), "New control is still attached");
    tray.Children().RemoveAt(newChildIndex);
    arrange();
    Require(tray.ColumnDefinitions().Size() == 2
        && tray.ColumnDefinitions().GetAt(0) == first
        && tray.ColumnDefinitions().GetAt(1) == second
        && tray.Children().Size() == 2 && Grid::GetColumn(quick) == 0 && Grid::GetColumn(clock) == 1,
        "Disable restores the same tray columns and controls");
    Require(quick.ReadLocalValue(Grid::ColumnProperty()) == DependencyProperty::UnsetValue()
            && unbox_value<int>(clock.ReadLocalValue(Grid::ColumnProperty())) == 1,
        "Disable restores both local and default column properties");
    Near(scroll.ActualWidth(), baseline, "Disable restores the original app viewport width");
    for (int cycle=0; cycle<3; ++cycle) {
        Require(reserved.Attach(tray, tile), "A removed reservation can be enabled again");
        reserved.Remove();
    }
    arrange();
    Near(scroll.ActualWidth(), baseline, "Repeated enable/disable has no width drift");
    std::cout << "PASS real XAML: viewport " << baseline << " -> " << baseline - Width
              << " -> " << baseline << " DIP; reserved " << Width
              << " DIP (" << Width * dpi << " physical px); 33 app buttons; "
              << "four hit targets; exact rollback over four cycles\n";

    if (preview) {
        Require(reserved.Attach(tray, tile), "Restore the reservation for preview");
        arrange();
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        std::cout << "Preview window " << reinterpret_cast<uintptr_t>(hwnd) << '\n' << std::flush;
        // End on our own; this window does not use or change Explorer's taskbar.
        for (int i=0; i<120 && IsWindow(hwnd); ++i) { Pump(); Sleep(50); }
        reserved.Remove();
    }
    island.Content(nullptr);
    island.Close();
    if (IsWindow(hwnd)) DestroyWindow(hwnd);
    Pump();
    return 0;
}
}

int wmain(int argc, wchar_t** argv) {
    try { return Run(argc == 2 && std::wstring_view(argv[1]) == L"--preview"); }
    catch (hresult_error const& error) {
        std::wcerr << L"XAML fixture failed: " << error.message().c_str() << L'\n';
    } catch (std::exception const& error) {
        std::cerr << "Fixture failed: " << error.what() << '\n';
    }
    return 1;
}
