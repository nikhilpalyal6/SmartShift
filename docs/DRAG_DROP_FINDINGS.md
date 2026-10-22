# Drag-and-drop findings (Phase 0)

Environment: Windows 11 Home Single Language (NT 10.0.26200), .NET SDK 10.0.402, WPF `net10.0-windows`.

## How Windows file drag-and-drop works

File Explorer is an OLE **drag source**. It places a data object on the OLE drag clipboard for the lifetime of the drag. Drop targets implement **`IDropTarget`**. During `DragEnter` / `DragOver` / `Drop`, OLE gives that target the **`IDataObject`**.

For files, the classic payload is **`CF_HDROP`** (`DROPFILES`). WPF exposes this as `DataFormats.FileDrop`, typically a `string[]` of paths.

WPF `AllowDrop="True"` plus `DragEnter` / `DragOver` / `Drop` is a managed `IDropTarget`. That is enough to participate as a **drop target**. It is not a drag source and it is not a global OLE observer.

## What each role can see

| Approach | File paths? | Full OLE `IDataObject`? | Can break native drags? |
| --- | --- | --- | --- |
| WPF/OLE drop target (`IDropTarget`) | Yes, when `FileDrop` is present | Yes, the formats the source offered | Only for drops that hit *this* window |
| OLE drag source | Yes (it created the payload) | Yes | N/A (it is the source) |
| `WH_MOUSE_LL` / `WH_KEYBOARD_LL` | **No** | **No** | Yes, if the hook swallows input or is buggy |
| `GetAsyncKeyState` during a drop-target event | No (modifiers only) | No | No |

Low-level hooks can see mouse buttons and, with a keyboard hook, modifiers. They do not receive `CF_HDROP`. Claiming otherwise would be incorrect.

## Shell integration options considered

1. **Explicit Drop Shelf (chosen)** — a small topmost WPF window that is an ordinary drop target. Paths are read from `IDataObject`. Native drags that do not hit the shelf continue unchanged.
2. **Cursor-following probe** — also a drop target, but it sits under/near the cursor, so it **can intercept** Explorer-to-Explorer and Explorer-to-app drops. Implemented as an opt-in experiment only.
3. **Global mouse hook + overlay** — can detect a drag gesture, cannot read files unless a drop target is introduced. Rejected as the default because a cursor overlay *is* an intercepting drop target.
4. **Explorer namespace extension / IDropHandler** — out of Phase 0 scope (Shell registration, installer, higher disruption).

## Safe drop effects

If a drop target returns `DROPEFFECT_MOVE` (`DragDropEffects.Move`) and Explorer is the source, Explorer may **remove the source files** after a successful drop. Phase 0 never copies or moves files, so the shelf and probe always set:

- `Copy` when `FileDrop` can be inspected
- `None` otherwise
- **Never `Move`**

That choice is deliberate. Shift+drag over the shelf therefore does **not** perform a native move into SmartShift.

## Experiments that were executed

| Experiment | Result |
| --- | --- |
| Synthetic WPF `DataObject` with `FileDrop` (one file) | Paths and kinds extracted; original bytes unchanged |
| Synthetic multi-file `FileDrop` | Count and names preserved |
| Empty `FileDrop`, text-only data, null data | Rejected without mutation |
| Drop Shelf show/hide on STA thread | Window visible then closed |
| Observe-only `WH_MOUSE_LL` install + uninstall | Hook started and stopped in-process; no admin |
| App launch (`SmartShift.App.exe --show-shelf`) and `CloseMainWindow` | Process ran with a main window and exited 0 |

## Experiments not executed in this session

These require a human (or a robot that can miss the target and accidentally move files). They were **not** claimed as passing:

- Drag from File Explorer onto the Drop Shelf
- Explorer folder → Explorer folder while SmartShift is running (shelf hidden)
- Explorer → third-party app (for example VS Code, browsers, Office)
- Multi-select from Explorer, unsupported types from Explorer
- SmartShift closed vs background during an Explorer drag

See `GESTURE_TESTS.md` for the manual procedure.

## Conclusion for Phase 0

A standalone WPF app **can** inspect file paths **when it is the drop target**. It **cannot** reliably obtain the OLE payload from hooks alone. The least disruptive product path is an explicit Drop Shelf (or a later user-invoked overlay that is not sitting on the cursor during unrelated drags).
