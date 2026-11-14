# Windows 11 taskbar slot experiment

This independent Windhawk mod adds a test button to a fixed **321 DIP** column
in the primary taskbar notification area's XAML grid (642 physical pixels at
200%). It only tests reservation. It does not draw live quotas or use the
TokenBar process, credentials, configuration, or ledger. The normal app
continues to use opportunistic same-row placement.

Run the same column insertion and removal code in a separate XAML Island:

    pwsh -NoProfile -File experiments/taskbar-slot/verify-slot.ps1 -OutputDirectory <new-task-output>

This requires Visual Studio C++ x64 build tools and Windows SDK 10.0.26100.0
(override with -WindowsSdkVersion if needed). Add -Preview to display the test
row briefly. The taskbar in this fixture is simulated: XAML measures a fixed tray
column next to a scrollable app-button strip. The test verifies that crowding
preserves the width, four XAML controls stay hit-testable, and disabling restores
the original columns and bounds over repeated cycles. It also recreates a tray
button in the reserved column and checks automatic rollback, then exercises a
cross-thread close that times out while the target callback is still executing.

The mod is off by default. Its .wh.cpp is standalone and compiles with the
official Windhawk 1.7.3 x64 compiler. It is bound to Windows 11 build
26300.9974, the primary horizontal taskbar, and the expected private taskbar
symbols and member signature. Use an explicitly authorized temporary Windhawk
session and turn on **Show the trial slot** for the live check. Compare with the
original bounds, add enough apps to overflow, check Start/app/tray/clock hit
targets, then switch the slot off and verify the exact base geometry. An
overlapping or detached layout withdraws the trial slot rather than moving
any new native controls.

The live trial runs in Explorer. Cross-thread close waits at most 750 ms for
message dispatch. A timeout is reported as unconfirmed rollback; an active slot
is removed on the taskbar thread when it starts processing messages again.
The trial DLL remains mapped until that Explorer process exits so that late
callbacks can finish safely. After disabling or unloading the mod, use a new
Explorer session to try it again. Disabling the setting without unloading the
mod still allows another trial after a confirmed rollback.

If Explorer hangs or exits, file management or the desktop may be interrupted.
The fixture does not verify real Explorer loading, reflow or recovery. The
reservation uses no credentials, elevation, startup registration or permanent
Windows settings changes.

The shell bridge follows the MIT-licensed [Taskbar Media Presence implementation
at e2a6610](https://github.com/MrBoxik/Taskbar-Media-Presence/blob/e2a6610237a214432cc3c245c8a3015cfdb55194/taskbar-media-presence.wh.cpp#L9453-L9578).
Its [tray-column approach](https://github.com/MrBoxik/Taskbar-Media-Presence/blob/e2a6610237a214432cc3c245c8a3015cfdb55194/taskbar-media-presence.wh.cpp#L12735-L12840)
informs this prototype. The upstream notice is included here.
