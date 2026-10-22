# SmartShift

Local-first Windows utility centered around a context-aware radial action menu. Drag files onto the Drop Shelf to inspect them, compress, convert to WebP, resize, and remove metadata locally. Originals are never overwritten.

## Requirements

- Windows 10/11
- .NET SDK 10 (LTS), pinned in `global.json`

## Build

```powershell
dotnet restore SmartShift.sln
dotnet build SmartShift.sln -c Release
```

## Run

```powershell
dotnet run --project src\SmartShift.App\SmartShift.App.csproj -c Release
```

`--diagnostics` shows a Diagnostics button and can open the development window (Phase 0 drop shelf, optional observe-only hook, cursor probe).

## Test

```powershell
dotnet test SmartShift.sln -c Release
```

Live Explorer drag-and-drop is still a **manual** check (`docs/GESTURE_TESTS.md`).

## What works today

- **Radial Action Experience**:
  - Context-aware circular menu orbiting the center file preview.
  - Responsive 3-column layout: Left File Shelf, Center Radial Action Workspace, Right Action Details Panel & Result Area.
  - Dark navy palette (`#0A0F1D`, `#111827`, `#162035`, `#22304E`, `#3B82F6`) with crisp vector SVG icons and subtle radial focus glow.
  - Full keyboard accessibility: hotkeys `1`–`5` to select radial actions, `Enter` to run, `Esc` to cancel, `Delete` to remove files from queue, and arrow/Tab key navigation.
- **Left File Shelf**:
  - Drag-and-drop queue with queue count and recognized count.
  - 96px decoded thumbnails with format color badges (`JPG`, `PNG`, `WEBP`, `PDF`).
  - Per-item removal and "Clear queue" action.
- **Classification**: Fast, deterministic header-sniffing (not extension alone).
- **Show information**: Header inspection for images and structural inspection for PDFs (page count, dimensions, version, encryption status, title, author, signatures) without writing files.
- **Compress PDF**:
  - Re-encodes content streams using optimal Flate/Deflate compression (`PdfFlateEncodeMode.BestCompression`).
  - Deflates uncompressed streams across the document object graph without destructive rasterization.
  - Prunes redundant cross-reference tables and unreferenced structural overhead.
  - Preserves selectable text, vector graphics, embedded fonts, page counts, dimensions, annotations, and bookmarks.
  - Validates output page count and page dimensions against source specifications before atomic finalization.
  - Honestly reports actual output size and delta; flags pre-compressed documents when no size reduction occurs.
- **Compress image**:
  - JPEG: lossy quality compression (1–100).
  - PNG: lossless compression (quality maps to zlib effort levels 1–9, transparency preserved).
  - WebP: lossy (1–99) and lossless (100) compression, transparency preserved.
- **Convert to WebP**: High-quality WebP conversion from JPEG, PNG, or WebP with alpha preservation.
- **Resize image**: High-quality Mitchell cubic resampling with aspect-ratio lock, quick dimension presets (50%, 75%, 1080p, 720p), and safety bounds (max 8192px / 40MP).
- **Remove metadata**: Strips EXIF, GPS, and XMP metadata by re-encoding pixel data into a clean container, baking orientation into pixels first.
- **Result Area & Size Comparison**:
  - Displays original vs output size comparison, delta bytes, and percentage reduction badge in teal.
  - Displays explicit enlargement note in amber if the output is larger than source.
  - One-click "Open file" (system default viewer) and "Show in folder" (reveals file in File Explorer).
  - Explicit confirmation that the original file was preserved intact.
- **Output safety**: Writes new files into `%LOCALAPPDATA%\SmartShift\Output` with collision-safe numbered names (`file (1).ext`). Original files are never touched or modified in place.

## What does not work yet

- **PDF preview**: PDF page rendering / rasterized visual preview requires a dedicated rendering engine (e.g. PDFium) and remains unavailable in Phase 1 (clearly disabled and marked as "Not implemented" in the UI).

## Image & PDF Processing Library Details

- **Image Engine**: SkiaSharp `4.153.1` (backed by Google's Skia 2D graphics engine).
  - **Licensing**: SkiaSharp is licensed under the MIT License and Skia under the BSD 3-Clause License. This avoids the restrictive commercial obligations and revenue thresholds of the SixLabors ImageSharp Split License.
  - **Safety limits**: 8,192 px max per dimension, 40,000,000 pixels max decoded image size to prevent memory exhaustion and decompression bombs.
- **PDF Engine**: PDFsharp `6.1.1` (empira Software GmbH).
  - **Licensing**: Licensed under the permissive **MIT License**. We explicitly selected version 6.1.1 to avoid the AGPLv3 copyleft obligations and runtime license-key requirements introduced in PDFsharp 6.5.0+, ensuring full closed-source commercial compatibility.
  - **Preservation & Integrity**: Non-destructive stream and structure compression. Selectable text, vector paths, fonts, and annotations are preserved intact. Digital signatures are detected and flagged (since byte rewriting alters signature ranges). Password-protected files fail cleanly with informative diagnostic feedback.

## Docs

| File | Purpose |
| --- | --- |
| `docs/PHASE1.md` | Phase 1 scope, radial UI experience, image processing capabilities, and limits |
| `docs/OUTPUT_POLICY.md` | Output and original-file safety policy |
| `docs/PHASE1_PLAN.md` | Implementation plan |
| `docs/PHASE0_*.md` / `DRAG_DROP_FINDINGS.md` | Phase 0 spike and drag-and-drop findings |

## Safety & Privacy

- Local only: 100% on-device processing, no network uploads, telemetry, accounts, or cloud dependencies.
- Non-destructive: Original files are never overwritten, renamed, or modified.
- No administrator privileges required.
