# 0005. Part-file lifecycle, write failure contract and two-phase finalise

- Status: accepted
- Date: 2026-10-17
- Requirements: KASI-ENG-008, KASI-RES-001, TRD 6.4, TRD 6.5, ADR 0002, ADR 0003, ADR 0004

## Context

TRD 6.4 specifies that each download writes to a single `<name>.kasipart` file opened with `FileOptions.Asynchronous`, marked
sparse (`FSCTL_SET_SPARSE`) and pre-sized with `SetLength`, written via `RandomAccess.WriteAsync` from 256 KiB pooled buffers,
and on completion has its sparse flag cleared, its length verified, Mark-of-the-Web written, and its name changed to `<name>`.
Several design questions arise when turning this into code:

1. How finalisation accommodates later verification steps (checksum in M1.12, Mark-of-the-Web in M2.11) before the rename.
2. Why verifying `GetLength == TotalBytes` at completion is insufficient on its own when `SetLength(TotalBytes)` ran at open.
3. What a partial or failed `WriteAsync` call means for segment progress and checkpointing.
4. How long a worker holds its 256 KiB `ArrayPool<byte>.Shared` buffer, and how slow workers still feed timely speed samples to
   the scheduler.

## Decision

**Two-phase finalise (`SealAsync` then `CommitAsync`).** Finalisation is split into two methods on `PartFileWriter`:

1. `SealAsync(expectedLength, bytesWritten)` flushes (`RandomAccess.FlushToDisk` off the caller's thread via `Task.Run`),
   verifies `RandomAccess.GetLength` against `expectedLength ?? TotalBytes` (`LengthMismatch`), verifies completeness
   (`bytesWritten` must equal the file length and on sparse NTFS files `FSCTL_QUERY_ALLOCATED_RANGES` must cover `[0, length)`
   with no unallocated holes, else `IncompleteData`), and clears the sparse attribute via `FSCTL_SET_SPARSE { SetSparse = 0 }`.
   The file handle remains open after `SealAsync` so M1.12 can hash the file and M2.11 can write `Zone.Identifier` before any
   rename occurs.
2. `CommitAsync(overwriteDestination = false)` closes the handle and moves `<name>.kasipart` to `<name>`. If the destination
   exists and `overwriteDestination` is `false`, it returns `TargetExists` while keeping the `.kasipart` file intact.

Disposing `PartFileWriter` without committing leaves the `.kasipart` file on disk for resume (pause, shutdown, crash).
`Abandon()` closes the handle and deletes the `.kasipart` file when a job is cancelled or removed.

**Completeness guard.** Because `SetLength(TotalBytes)` runs when the `.kasipart` file is first opened, `GetLength` equals
`TotalBytes` even if no segment was ever written. Sealing therefore requires both the scheduler's `bytesWritten == length` and,
on sparse NTFS files, a single allocated range `[0, length)` from `FSCTL_QUERY_ALLOCATED_RANGES`. Explicit writes of all-zero
buffers to a sparse NTFS file still allocate disk extents unless explicitly punched with `FSCTL_SET_ZERO_DATA`, which Kasi never
calls; once `[0, length)` is fully allocated, `FSCTL_SET_SPARSE { SetSparse = 0 }` clears `FileAttributes.SparseFile`. On
filesystems that reject `FSCTL_SET_SPARSE` (`ERROR_INVALID_FUNCTION` or `ERROR_INVALID_PARAMETER`), clearing the flag is
best-effort once the length and completeness checks succeed.

**Write failure contract.** When `WriteAsync` returns a non-success `DiskStatus`, none of the bytes in that call count as
written, even if the OS wrote part of the buffer before failing. The caller must not call `SegmentScheduler.AdvanceLease`; a
later retry rewrites the same offset range. Because every segment byte is deterministic by file offset, rewriting an offset
range is idempotent (which also makes endgame duplicate writes safe).

**Free space and disk-full classification.** Before opening or resizing a `.kasipart` file of known size, `PartFileWriter`
queries `IDiskSpaceProvider` (`GetDiskFreeSpaceExW` on the target directory path, which honours folder-mounted VHDs, UNC shares
and per-user quotas). A new download needs `TotalBytes`; a resumed download with an existing `.kasipart` needs
`max(0, TotalBytes - alreadyWrittenBytes)`. During `SetLength`, `WriteAsync` or `FlushAsync`, Win32 `ERROR_DISK_FULL` (`112`),
`ERROR_HANDLE_DISK_FULL` (`39`), `ERROR_DISK_QUOTA_EXCEEDED` (`1295`), `ERROR_NOT_ENOUGH_QUOTA` (`1816`) and `STATUS_DISK_FULL` /
`STATUS_QUOTA_EXCEEDED` map to `DiskErrorKind.NotEnoughSpace`. `DiskStatus` carries no user-facing strings; the UI maps
`DiskErrorKind` to localised resource strings, and `Detail` holds only a redacted diagnostic note (`Redact.Path`).

**Lease-scoped 256 KiB write buffers with a 500 ms max-age flush.** `PooledPartWriteBuffer` rents its 256 KiB array from
`ArrayPool<byte>.Shared` when a lease begins and returns it as soon as the lease completes, is superseded or is abandoned, so
idle workers hold no buffer against the 250 MB memory budget (`KASI-NFR-004`). It flushes to `PartFileWriter.WriteAsync` when
256 KiB has accumulated, when the oldest buffered byte reaches 500 ms (`TimeProvider`), or when the lease ends, so even a
50 KB/s connection reports progress to `SegmentScheduler.AdvanceLease` at least twice per second.

## Consequences

- `SegmentScheduler.AdvanceLease` is called only after `PartFileWriter.WriteAsync` succeeds, and `ICheckpointWriter.Record` is
  called only after `PartFileWriter.FlushAsync` returns on a cursor snapshot taken before the flush (ADR 0003, ADR 0004).
- Concurrent `WriteAsync` calls on one asynchronous `SafeFileHandle` do not allocate per 16 KiB network chunk; each 256 KiB OS
  write uses `PoolingAsyncValueTaskMethodBuilder` and allocates only the small Win32 overlapped structure when completing
  asynchronously.
