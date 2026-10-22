# Phase 1 plan (Part 1 — UI + architecture)

Built on the Phase 0 solution. Baseline tests before changes: **12 passed** (`SmartShift.Core.Tests` 5, `SmartShift.Windows.Tests` 7).

## Reuse

- Keep `OleFileDropExtractor`, safe `Copy`/`None` effects, `FileDropInspector`, `DropShelfWindow` (Phase 0 tests + diagnostics).
- Keep `DiagnosticLogger` and app exception handling.
- Do **not** enable hooks or the cursor probe in the normal workflow.
- Do **not** add conversion engines.

## Changes

1. **Main window** becomes the user-facing Drop Shelf (not always-on-top). Diagnostics move behind a menu / `--diagnostics`.
2. **Content-aware classifier** in Core: magic-byte sniff of the file header only. Extensions are hints. Supported *categories* this phase: PNG, JPEG, WebP, PDF.
3. **Recommendation engine** in Core: deterministic actions + goals. Only “Show information” is executable. Planned convert/compress/resize/preview actions are visible but marked unavailable.
4. **Processing + output policy** projects: request/result/progress/cancel abstractions, collision-safe output paths, no fake successful conversions.
5. **Action details UI**: options, validation, source preview when WPF can decode the input, no fabricated output estimates.

## Non-goals

Image/PDF engines, video/audio, accounts, telemetry, global input capture, radial menu.
