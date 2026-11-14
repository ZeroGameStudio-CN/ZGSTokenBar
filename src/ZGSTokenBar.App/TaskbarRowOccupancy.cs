using System.Runtime.InteropServices;

namespace ZGSTokenBar.App;

/// <summary>Read-only, cached UI Automation snapshot of the Windows 11 taskbar. Call on an MTA worker.</summary>
internal static unsafe class TaskbarRowOccupancy
{
    private static readonly Guid AutomationClass = new("e22ad333-b25f-460c-83d0-0581107395c9");
    private static readonly Guid Automation2Interface = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");
    private const uint TimeoutMs = 350;
    private const int MaximumNodes = 1024;
    private const int MaximumDepth = 16;
    private static readonly int[] CachedProperties =
    [
        30001, // BoundingRectangle
        30002, // ProcessId
        30003, // ControlType
        30009, // IsKeyboardFocusable
        30011, // AutomationId (only used to identify the taskbar layout)
        30022, // IsOffscreen
    ];

    internal static Rectangle[]? Read(nint taskbar, Rectangle taskbarBounds, Rectangle trayBounds)
    {
        if (trayBounds.IsEmpty || GetWindowThreadProcessId(taskbar, out var shellPid) == 0
            || TaskbarEmbedding.IsHung(taskbar)) return null;
        if (CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */) < 0) return null;
        nint automation = 0, request = 0, rawView = 0, root = 0;
        try
        {
            // Vtable slots below are from the named interfaces in UIAutomationClient.h.
            if (CoCreateInstance(AutomationClass, 0, 1 /* CLSCTX_INPROC_SERVER */, Automation2Interface, out automation) < 0
                || CallUInt(automation, 61 /* put_ConnectionTimeout */, TimeoutMs) < 0
                || CallUInt(automation, 63 /* put_TransactionTimeout */, TimeoutMs) < 0
                || GetPointer(automation, 20 /* CreateCacheRequest */, out request) < 0
                || GetPointer(automation, 17 /* get_RawViewCondition */, out rawView) < 0
                || CallPointer(request, 9 /* put_TreeFilter */, rawView) < 0
                || CallUInt(request, 7 /* put_TreeScope */, 7 /* TreeScope_Subtree */) < 0
                || CallUInt(request, 11 /* put_AutomationElementMode */, 0 /* AutomationElementMode_None */) < 0)
                return null;

            foreach (var property in CachedProperties)
                if (CallUInt(request, 3 /* AddProperty */, (uint)property) < 0) return null;

            if (((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(automation, 10 /* ElementFromHandleBuildCache */))
                    (automation, taskbar, request, &root) < 0 || root == 0)
                return null;

            var layout = new Layout((int)shellPid, taskbarBounds);
            return layout.Read(root, 0) && !layout.Apps.IsEmpty && !layout.Start.IsEmpty
                && layout.Apps.Contains(layout.Start)
                ? layout.Occupied.Append(layout.Apps).Append(trayBounds).Distinct().ToArray()
                : null;
        }
        finally
        {
            Release(root);
            Release(rawView);
            Release(request);
            Release(automation);
            CoUninitialize();
        }
    }

    private sealed class Layout(int shellPid, Rectangle taskbarBounds)
    {
        private int _remaining = MaximumNodes;
        internal Rectangle Apps { get; private set; }
        internal Rectangle Start { get; private set; }
        internal List<Rectangle> Occupied { get; } = [];

        internal bool Read(nint element, int depth)
        {
            if (depth > MaximumDepth || --_remaining < 0 || element == 0) return false;
            if (GetInt(element, 52 /* CachedProcessId */, out var pid) < 0) return false;
            if (pid != shellPid) return depth != 0; // Skip our own embedded window and foreign controls.

            var hasOffscreen = GetInt(element, 70 /* CachedIsOffscreen */, out var offscreen) >= 0;
            _ = GetPointer(element, 61 /* CachedAutomationId */, out var id);
            string? automationId;
            try { automationId = id == 0 ? null : Marshal.PtrToStringBSTR(id); }
            finally { if (id != 0) Marshal.FreeBSTR(id); }

            _ = GetInt(element, 53 /* CachedControlType */, out var controlType);
            _ = GetInt(element, 58 /* CachedIsKeyboardFocusable */, out var focusable);
            // Buttons and keyboard targets outside the main group include Widgets and tray controls.
            var isTarget = focusable != 0
                || controlType is 50000 or 50002 or 50003 or 50004 or 50005 or 50011 or 50013 or 50019 or 50031;
            if ((!hasOffscreen || offscreen == 0) && (isTarget || automationId is "TaskbarFrameRepeater" or "StartButton"))
            {
                NativeRect native;
                if (((delegate* unmanaged[Stdcall]<nint, NativeRect*, int>)Slot(element, 75 /* CachedBoundingRectangle */))
                        (element, &native) < 0) return false;
                var bounds = Rectangle.Intersect(taskbarBounds,
                    Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom));
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    if (automationId == "TaskbarFrameRepeater")
                        Apps = Apps.IsEmpty ? bounds : Rectangle.Union(Apps, bounds);
                    else if (automationId == "StartButton") Start = bounds;
                    else Occupied.Add(bounds);
                }
            }

            _ = GetPointer(element, 19 /* GetCachedChildren */, out var children);
            if (children == 0) return true;
            try
            {
                if (GetInt(children, 3 /* Length */, out var length) < 0 || length > _remaining) return false;
                for (var index = 0; index < length; index++)
                {
                    nint child;
                    if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(children, 4 /* GetElement */))
                            (children, index, &child) < 0) return false;
                    try { if (!Read(child, depth + 1)) return false; }
                    finally { Release(child); }
                }
                return true;
            }
            finally { Release(children); }
        }
    }

    private static void* Slot(nint instance, int index) => (*(void***)instance)[index];

    private static int CallUInt(nint instance, int slot, uint value) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(instance, slot))(instance, value);

    private static int CallPointer(nint instance, int slot, nint value) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(instance, slot))(instance, value);

    private static int GetPointer(nint instance, int slot, out nint result)
    {
        result = 0;
        fixed (nint* address = &result)
            return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, slot))(instance, address);
    }

    private static int GetInt(nint instance, int slot, out int result)
    {
        result = 0;
        fixed (int* address = &result)
            return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(instance, slot))(instance, address);
    }

    private static void Release(nint instance)
    {
        if (instance != 0) _ = Marshal.Release(instance);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
