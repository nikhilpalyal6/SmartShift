# Gesture and Drop Shelf tests

## Automated (executed 2026-10-20)

Command: `dotnet test SmartShift.sln -c Release`

| Case | Result |
| --- | --- |
| Valid file path extraction | Pass (`Extract_FileDropDataObject_ReturnsPaths`, `Inspect_SingleSupportedFile_*`) |
| Multiple files | Pass |
| Empty / invalid payload | Pass |
| Unsupported extension classified, originals unchanged | Pass |
| Directory rejected | Pass |
| Drop Shelf show/hide | Pass |
| Safe effects never `Move` | Pass |
| Hook stop without start | Pass |
| Hook start then uninstall | Pass |
| Logger error path | Pass |
| App shutdown | Pass (`CloseMainWindow` → exit 0) |

Unit tests use synthetic `DataObject` instances. They **do not** prove Explorer or third-party compatibility.

## Manual Windows integration (procedure)

Create two folders and copy (do not cut) throwaway files. Record hashes or last-write times before each drag. **Do not enable the cursor probe** unless the test is specifically about interception.

| ID | Scenario | Expected if Drop Shelf is the model | Status this session |
| --- | --- | --- | --- |
| M1 | Explorer → Drop Shelf, one image/PDF | Names listed; source file stays | **Not run** |
| M2 | Explorer → Drop Shelf, multi-select | Count matches; sources stay | **Not run** |
| M3 | Explorer → Drop Shelf, `.txt` or similar | Listed as unsupported; sources stay | **Not run** |
| M4 | Explorer folder A → folder B, SmartShift running, shelf hidden, probe off | Native move/copy unchanged | **Not run** |
| M5 | Explorer → third-party drop target, SmartShift running, probe off | Third-party still receives the drop | **Not run** |
| M6 | Same as M4/M5 with SmartShift closed | Native behavior | **Not run** |
| M7 | Shift+drag over Drop Shelf | Prototype reports Shift; files not moved by Explorer into SmartShift (effect is Copy/None) | **Not run** |
| M8 | Ctrl+drag over Drop Shelf | Prototype reports Ctrl; native copy semantics elsewhere unchanged if probe off | **Not run** |
| M9 | Middle-button while dragging | Hook (if enabled) may log MMB; no paths; risk of app-specific MMB behavior | **Not run** |
| M10 | Cursor probe ON, Explorer → Explorer | Probe may intercept; **do not** use as default | **Not run** |

## Gesture comparison (evidence vs risk)

| Candidate | Detectable without being a drop target? | File paths without a drop target? | Conflict with Windows | Phase 0 verdict |
| --- | --- | --- | --- | --- |
| **Shift + drag** | Only modifiers (keys/hook). Not the payload. | No | Explorer uses Shift for **move** when the drop target allows it | Unsuitable as a global product gesture |
| **Ctrl + drag** | Same as Shift | No | Explorer uses Ctrl for **copy** | Unsuitable as a global product gesture |
| **Middle mouse** | Hook can see MMB | No | Conflicts with autoscroll, browser-tab close/open, some mice | Unsuitable as default; invasive if hooked globally |
| **Drop Shelf** | N/A (user shows a target) | Yes, via `IDataObject` / `FileDrop` | Only if the cursor is over the shelf | **Recommended default** |

Shift/Ctrl *can* be read **on the shelf** during `DragOver` (`GetAsyncKeyState`). That is useful for diagnostics, not for replacing Explorer modifiers globally.

## Recommended interaction

**Default: explicit Drop Shelf.** Show it from the control panel (or later a tray/hotkey that does not sit under the cursor). Keep any cursor-following drop target off unless the user is in a dedicated capture mode.
