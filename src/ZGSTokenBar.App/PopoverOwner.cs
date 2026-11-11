using System.Runtime.InteropServices;

namespace ZGSTokenBar.App;

/// <summary>
/// Hidden top-level window that owns popovers while the bar is a child of Shell_TrayWnd.
/// <para>
/// <see cref="Form.Show(IWin32Window?)"/> with a null owner silently borrows
/// <c>GetActiveWindow()</c> as the owner, and throws "a form cannot own itself or its owner" when
/// that active window is itself owned by the form being shown (a popover's extension flyout,
/// context menu or file dialog). It also ties popovers to whatever app happens to be active.
/// A dedicated owner removes both problems.
/// </para>
/// </summary>
internal sealed class PopoverOwnerWindow : NativeWindow, IWin32Window
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private static PopoverOwnerWindow? _instance;

    private PopoverOwnerWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "ZGSTokenBar.PopoverOwner",
            Style = WsPopup,
            ExStyle = WsExToolWindow | WsExNoActivate,
            X = -32000,
            Y = -32000,
            Width = 1,
            Height = 1,
        });
    }

    /// <summary>Created lazily on the UI thread; lives for the process.</summary>
    internal static PopoverOwnerWindow Instance => _instance ??= new PopoverOwnerWindow();
}

internal static class PopoverShow
{
    private const int GwlHwndParent = -8;

    /// <summary>
    /// Shows <paramref name="form"/> owned by <paramref name="owner"/>, never by itself or by a window
    /// it owns. Anything WinForms would reject falls back to the hidden popover owner, so a hover can
    /// never raise the unhandled-exception dialog.
    /// </summary>
    internal static void ShowPopover(this Form form, IWin32Window? owner)
    {
        if (form.IsDisposed || form.Visible) return;
        var safe = SafeOwner(form, owner);
        try
        {
            form.Show(safe);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException && !form.IsDisposed && !form.Visible)
        {
            AppDiagnostics.Record("popover-owner", error);
            form.Show(PopoverOwnerWindow.Instance);
        }
    }

    internal static IWin32Window SafeOwner(Form form, IWin32Window? owner)
    {
        if (owner is null || ReferenceEquals(owner, form)) return PopoverOwnerWindow.Instance;
        if (!form.IsHandleCreated) return Owns(owner, form) ? PopoverOwnerWindow.Instance : owner;
        var handle = owner.Handle;
        if (handle == 0 || handle == form.Handle) return PopoverOwnerWindow.Instance;
        // A window owned (directly or further up) by the form cannot become its owner.
        for (var walk = handle; walk != 0; walk = GetWindowLongPtr(walk, GwlHwndParent))
        {
            if (walk == form.Handle) return PopoverOwnerWindow.Instance;
        }

        return owner;
    }

    private static bool Owns(IWin32Window owner, Form form)
    {
        for (var walk = owner as Form; walk is not null; walk = walk.Owner)
        {
            if (ReferenceEquals(walk, form)) return true;
        }

        return false;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
}
