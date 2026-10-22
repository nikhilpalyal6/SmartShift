# Phase 0 decision

**Outcome: CONDITIONAL PASS**

The prototype meets the engineering bar for a Drop Shelf inspector on .NET 10 WPF. It does **not** yet have executed Explorer or third-party drag evidence, so Phase 1 must not assume a global Shift/Ctrl/middle-mouse capture gesture.

## Evidence for the decision

- Solution builds: `dotnet build` / `dotnet test` on SDK **10.0.402**.
- WPF app launched (`SmartShift Phase 0`) and shut down with exit code **0**.
- Supported and unsupported paths can be inspected from a `FileDrop` data object without modifying files (hash/content checks in tests).
- Drop Shelf window can be shown and hidden; it never advertises `Move`.
- Gesture alternatives were analyzed; global OLE-less gestures cannot provide file paths.
- Optional mouse hook installs and uninstalls in-process, observe-only.
- No admin elevation, no telemetry, no uploads, no conversion engines.

## Conditions before Phase 1 UI work

1. Run the manual matrix in `GESTURE_TESTS.md` (M1–M6 at minimum) on this Windows 11 machine and record pass/fail.
2. Keep the product default as **Drop Shelf / explicit overlay**, not Shift+drag or Ctrl+drag.
3. If a near-cursor menu is required later, it must be **user-armed** (hotkey or button) so it is not an always-on intercepting `IDropTarget`.

## Next actions

1. Execute M1–M6; paste results into `GESTURE_TESTS.md`.
2. If M1 works, Phase 1 can add a radial menu **on the Drop Shelf** (or a user-armed overlay) without global hooks.
3. Do not implement converters until the manual Explorer drop path is confirmed.
