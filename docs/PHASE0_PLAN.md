# Phase 0 plan

Phase 0 is a technical spike. It answers whether SmartShift can inspect file drags on Windows without breaking Explorer or third-party drop targets.

## In scope

1. .NET 10 / WPF project foundation, logging, and exception handling.
2. Investigation of OLE drag-and-drop, `IDataObject`, `IDropTarget`, and `CF_HDROP`.
3. Drop Shelf prototype that inspects paths and never mutates files.
4. Gesture experiments: Shift+drag, Ctrl+drag, middle mouse, Drop Shelf.
5. Automated tests for extractable behavior plus a written manual test plan.
6. A Phase 1 readiness decision based on evidence.

## Out of scope

- Image conversion, PDF editing, media pipelines
- Radial menu product UI
- Accounts, payments, AI, telemetry, uploads
- System-wide interception of native drags as a default behavior

## Architecture (minimal)

| Project | Role |
| --- | --- |
| `SmartShift.Core` | Path inspection, file-kind classification, local diagnostics |
| `SmartShift.Windows` | OLE `FileDrop` extraction, safe drop effects, optional observe-only hook, Drop Shelf UI |
| `SmartShift.App` | Control panel, exception handling, lifecycle |
| `*.Tests` | Repeatable inspection, payload, window, and hook cleanup tests |

Windows-specific code stays out of `SmartShift.Core`.

## Safety rules used during the spike

- Do not auto-complete Explorer drops.
- Drop targets report `Copy` or `None`, never `Move`.
- Optional `WH_MOUSE_LL` is off by default, always calls the next hook, and is disposed on shutdown.
- No administrator rights.
- No network communication from the prototype.
