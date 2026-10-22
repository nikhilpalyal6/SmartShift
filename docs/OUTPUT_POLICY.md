# Output safety policy

This policy applies before any conversion engine is added. Phase 1 does not write transformed files.

## Originals

- Never overwrite, delete, rename, or edit the input file in place.
- Queue “Remove” and “Clear” affect only in-memory items.
- Inspection re-reads headers and must leave length and last-write time unchanged.

## Output location

Default directory:

`%LOCALAPPDATA%\SmartShift\Output`

The allocator may use a test-specific root. Outputs must stay under that root.

## Collisions

If `name.ext` exists, allocate `name (1).ext`, `name (2).ext`, …  
Never replace an existing file silently.  
Never return a path equal to the original input.

## Processing contract

- Long work runs off the UI thread.
- Operations accept `CancellationToken`.
- Cancelled and failed results report `OriginalUnchanged = true` unless a future engine documents otherwise.
- Unavailable actions return a failure result. They must not report a successful conversion.
- Temporary files created by SmartShift may be deleted; user files may not.

## Logging

Diagnostics append to `%LOCALAPPDATA%\SmartShift\logs\smartshift.log` (local disk only).
