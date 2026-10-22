# Phase 1 — Images + PDFs MVP & Radial Action Experience

Phase 1 delivers a premium, context-aware Windows utility built around a circular radial action workspace, alongside the user-facing Drop Shelf, content-aware classification, dynamic recommendations, contextual action details, production-quality local image processing for JPEG, PNG, and WebP files, and a production-quality local PDF processing engine for PDF compression and structure inspection.

PDF documents can be inspected and compressed locally. PDF page preview requires a dedicated rendering engine and remains unavailable (marked as "Not implemented" in the UI).

## UI Design & Radial Action Architecture

The user interface follows a responsive 3-column layout styled with a restrained dark navy theme (`#0A0F1D`, `#111827`, `#162035`, `#22304E`, `#3B82F6`):

### 1. Left File Shelf
- Visually prominent drag-and-drop shelf with dashed border and drag cues.
- Live queue metrics (total queued vs recognized files).
- File cards featuring asynchronous 96px thumbnail generation (`BitmapImage.DecodePixelWidth = 96`), color-coded format badges, dimensions, and file size.
- Per-file removal and "Clear queue" action.
- Resilient multi-file handling with smooth scrolling.

### 2. Center Radial Action Workspace
- **Central Preview Container**: Dynamically sized (160–220 px) centered preview of the selected image, or dedicated document card for PDFs.
- **Orbital Radial Layout**: Math-driven layout calculated by `RadialLayoutCalculator`.
  - Orbit radius dynamically scales from 135 px to 215 px based on canvas dimensions.
  - Actions distribute evenly around the orbital ring starting at 12 o'clock (-90°).
  - Supported image actions: **Compress**, **WebP**, **Resize**, **Strip EXIF**, **Details**.
  - Supported PDF actions: **Compress** (available, hotkey 1), **Details** (available, hotkey 2), and **Preview** (unavailable, labeled "Not implemented").
- **Radial Action Buttons**:
  - Circular icon buttons with resolution-independent vector SVG geometry.
  - Subtle glow rings on selection and smooth hover transitions.
  - Hotkey indicators (`1`–`5`) displayed on each button.
- **Keyboard Navigation**:
  - Hotkeys `1` through `5` select actions instantly.
  - Arrow keys and `Tab` navigate cyclic focus.
  - `Enter` executes the selected action.
  - `Esc` cancels an ongoing operation.
  - `Delete` removes the selected file from the shelf.

### 3. Right Action Details & Configuration Panel
- Format-specific compression options (lossy JPEG slider, PNG zlib effort notice, WebP lossy/lossless modes).
- Quality slider synchronized with numeric textbox input.
- Image resizing with linked width/height inputs, aspect-ratio lock, and quick preset buttons (50%, 75%, 1080p, 720p).
- PDF compression panel with clear explanations of stream deflation, structural cleanup, and preservation guarantees.
- Metadata removal explanation outlining privacy guarantees and format limitations.
- File inspector grid showing MIME type, file size, page counts, dimensions, encryption status, and metadata.
- PDF status banner for unimplemented preview action.

### 4. Result Area & Comparison
- Displays actual output filename, format, and dimensions.
- Comparative file size visualization: original size vs output size and net delta in bytes.
- Clear percentage reduction badge in teal; explicit enlargement note in amber if output is larger.
- Direct actions: **Open file** (launches default Windows viewer via `ShellFolders.OpenFile`) and **Show in folder** (reveals in File Explorer).
- Non-destructive guarantee notice confirming the original file was preserved intact.

## Implemented Processing Operations

### 1. Compress PDF (`compress-pdf`)
- **Engine**: PDFsharp `6.1.1`.
- **Approach**: Local lossless stream and structural optimization.
  - Content streams are re-encoded with optimal Flate/Deflate compression (`PdfFlateEncodeMode.BestCompression`).
  - Uncompressed streams throughout the document object graph are deflated without rasterizing page content.
  - Cross-reference tables and unreferenced structural overhead are compacted upon save.
  - CCITT Group 4 compression is enabled for bilevel images (`EnableCcittCompressionForBilevelImages = true`).
- **Preservation Guarantees**:
  - Text remains fully selectable and searchable (no text flattening or conversion to bitmaps).
  - Vector graphics, shapes, lines, and curves are preserved in native vector format.
  - Embedded fonts and font subsets are preserved without alterations.
  - Page counts and page dimensions (MediaBox/CropBox) are preserved byte-accurate.
  - Interactive annotations, links, and outlines/bookmarks are retained.
  - Original file is never modified in place; outputs are allocated collision-safely.
- **Validation**:
  - Before promoting from `.partial` to the final path, the output is reopened with `PdfReader.Open`.
  - Asserts output page count matches source page count.
  - Asserts page dimensions match source dimensions for every page.
- **Honest Size Reporting**:
  - If output size is smaller, reports bytes saved and reduction percentage.
  - If output size grew (common for tiny or already pre-compressed documents), reports growth honestly in amber and explains that the document did not benefit from re-encoding.
- **Signatures & Encryption**:
  - Detects digital signatures (`/Type /Sig` or `/ByteRange`) and alerts the user that document rewriting invalidates existing cryptographic signatures.
  - Password-protected and encrypted files fail cleanly with an informative message without crashing.

### 2. Compress Image (`compress-image`)
- **JPEG**: Lossy compression using `SKJpegEncoderOptions(quality)` where quality is 1–100.
- **PNG**: Lossless compression. PNG does not support lossy JPEG-style quality degradations without destructive palette quantization; the quality parameter (1–100) maps directly to zlib compression effort levels (1–9). Full alpha transparency is preserved.
- **WebP**: Quality 1–99 uses lossy WebP encoding (`SKWebpEncoderCompression.Lossy`). Quality 100 enables lossless WebP encoding (`SKWebpEncoderCompression.Lossless`). Alpha transparency is preserved in both modes.
- Output sizes are measured after encoding and displayed to the user; no premature size estimates are promised.

### 3. Convert to WebP (`convert-to-webp`)
- Converts any recognized JPEG, PNG, or WebP input into a valid WebP file.
- Preserves 8-bit alpha transparency from RGBA sources.
- Configurable quality setting (1–100) supporting lossy (1–99) and lossless (100) WebP encoding.
- The output extension is strictly `.webp`. Files that are already WebP receive a `-converted.webp` suffix to clearly denote the re-encoded file.
- Re-reads and decodes the output file to guarantee validity before finalizing.

### 4. Resize Image (`resize-image`)
- Configurable target width and height in pixels.
- Aspect-ratio preservation mode (`keepAspect = true`) calculates height automatically from source dimensions.
- High-quality Mitchell cubic resampling (`SKSamplingOptions(SKCubicResampler.Mitchell)`).
- Preserves alpha transparency.
- Enforces strict safety limits: minimum 1 px, maximum 8,192 px per side, and maximum 40,000,000 total pixels (40 MP).
- Reports actual output dimensions and file size.

### 5. Remove Metadata (`remove-image-metadata`)
- Decodes the image pixel matrix and re-encodes it into a clean image container.
- Source EXIF, GPS coordinates, and Adobe XMP metadata chunks are completely omitted from the output.
- **Orientation preservation**: Reads EXIF orientation (`SKEncodedOrigin`, covering all 8 EXIF orientation cases) and applies the necessary rotation/flip/transpose transformations directly to the pixel bitmap *before* metadata removal. The resulting output is upright and requires no EXIF orientation tag.
- Guarantees & limitations: Pure pixel data and format headers (e.g. PNG `IHDR`, WebP `VP8`) are written cleanly. Residual encoder identification metadata may be written by the native encoder, but no source camera, device, location, or user metadata is retained.

### 6. Show Information (`show-image-info`, `show-pdf-info`)
- Displays format, file size, dimensions, page count, PDF version, encryption status, and metadata without writing files or modifying disk contents.

## Supported Formats

| Format | Input | Output | Compression | Content Preservation | Metadata |
|---|---|---|---|---|---|
| **PDF** | Yes | Yes (`.pdf`) | Stream Deflate & Struct Cleanup | Text, vectors, fonts, dimensions preserved | Title, Author, Signatures detected |
| **JPEG** | Yes | Yes (`.jpg`) | Lossy (1–100) | Raster re-encoded | EXIF/GPS/XMP removed on demand |
| **PNG** | Yes | Yes (`.png`) | Lossless (zlib 1–9) | RGBA alpha preserved | EXIF/XMP removed on demand |
| **WebP** | Yes | Yes (`.webp`) | Lossy / Lossless | RGBA alpha preserved | EXIF/XMP removed on demand |
| **GIF / BMP / TIFF** | Detected | No | Not supported | N/A | Not supported |

## Processing Architecture & UI Integration

- **Asynchronous Execution**: Heavy image and PDF processing runs on background threads via `Task.Run` and `IFileProcessor.ProcessAsync`, keeping the WPF UI thread responsive.
- **Responsive Options UI**: Dynamically presents action options. For resize, toggling "Keep aspect ratio" automatically disables the height input and dims it. For PDF compression, displays clear preservation notes and honest compression expectations.
- **Cancellation**: Full cooperative cancellation through `CancellationTokenSource`. Clicking Cancel stops processing immediately and cleans up any temporary `.partial` files.
- **Result Reporting**: Displays actual output file size, dimensions, format, encoder notes, and metadata notes. Provides "Open file" and "Show in folder" buttons.
- **Source File Integrity**: SHA-256 hash of the input file is measured before and after processing to verify the source file remains byte-for-byte unchanged.

## Output Policy & Safety

- Output location: `%LOCALAPPDATA%\SmartShift\Output` (customizable in test environments).
- Output writes use an atomic two-step procedure: encoded data is saved to a `<filename>.partial` file and validated. Only upon passing validation is it renamed to its final name.
- Collision avoidance: If a destination filename already exists, `OutputPathAllocator` appends an incrementing counter (`name (1).ext`, `name (2).ext`). Existing files are never replaced or overwritten.
- Source files are never modified in place, renamed, moved, or deleted.

## Library Selection & Licensing Considerations

- **PDF Engine**: `PDFsharp` version `6.1.1` (empira Software GmbH).
  - **Licensing Evaluation**:
    - `iText 7/8`: AGPLv3 copyleft is legally incompatible with proprietary closed-source distribution.
    - `PDFsharp 6.5.0+`: Dual-licensed under AGPLv3 and commercial paid tiers, requiring a runtime license key.
    - `PDFsharp 6.1.1`: Permissively licensed under the **MIT License**. Fully free for commercial closed-source distribution without royalties, revenue limits, or copyleft obligations.
    - Native .NET 10 compatibility on Windows x64, x86, and ARM64. Pure managed execution with no heavy unmanaged runtime dependencies.
- **Image Engine**: `SkiaSharp` version `4.153.1` (backed by Google Skia, MIT / BSD 3-Clause licenses).
- **Licensing Evaluation**:
  - `SixLabors.ImageSharp`: Uses the SixLabors Split License, requiring paid commercial licenses for organizations with annual gross revenue exceeding $1M USD. This imposes potential compliance and legal risks for closed-source commercial distribution.
  - `SkiaSharp`: Released under the permissive **MIT License** for the C# bindings and runtime packages, with the underlying Google Skia library licensed under the **BSD 3-Clause License**. It is completely free for both commercial and non-commercial use with no revenue thresholds or royalty obligations.
  - SkiaSharp natively supports .NET 10 on Windows x64, x86, and ARM64, and provides native SIMD-accelerated WebP, JPEG, and PNG encoding/decoding.

## Verification & Testing

Run all automated unit and integration tests:

```powershell
dotnet test SmartShift.sln -c Release
```

Test suite coverage includes:
- JPEG compression, dimensions, and validity.
- PNG alpha channel preservation and lossless compression semantics.
- WebP encoding, decoding, and conversion from JPEG, PNG, and WebP.
- Resize with exact dimensions, aspect ratio preservation, and alpha preservation.
- Orientation handling across all 8 `SKEncodedOrigin` cases.
- Metadata stripping and EXIF/GPS absence verification.
- Rejection of corrupt files, unsupported formats, invalid quality/dimension parameters, and oversized images.
- Collision handling and cancellation cleanup.
- Source file immutability (byte-level SHA-256 validation across all operations).

## Manual Checks Still Required

- Live Windows File Explorer drag-and-drop into Drop Shelf (WPF OLE interaction).
- Explorer-to-Explorer file drag operations while SmartShift Drop Shelf is running.
- Explorer drop into third-party applications while SmartShift is active.
