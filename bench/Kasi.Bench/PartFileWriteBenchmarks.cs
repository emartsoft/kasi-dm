using BenchmarkDotNet.Attributes;
using Kasi.Engine;

namespace Kasi.Bench;

/// <summary>
/// Micro-benchmarks of the M1.6 disk writer and 256 KiB coalescing buffer (TRD 6.4, ADR 0005).
/// Run with <c>dotnet run -c Release --project bench/Kasi.Bench -- --filter *PartFileWrite*</c>.
/// </summary>
#pragma warning disable CA1001 // BenchmarkDotNet requires an unsealed class and disposes resources in [GlobalCleanup]
[MemoryDiagnoser]
public class PartFileWriteBenchmarks
#pragma warning restore CA1001
{
    private const int ChunkBytes = 16 * 1024;
    private const int BlockBytes = PartFileWriter.WriteBufferSize; // 256 KiB
    private const long FileBytes = 64L * 1024 * 1024;

    private string _tempDir = null!;
    private PartFileWriter _writer = null!;
    private PooledPartWriteBuffer _buffer = null!;
    private byte[] _blockPayload = null!;
    private long _offset;

    [GlobalSetup]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"kasi-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var opened = (PartFileOpened)PartFileWriter.TryOpen(_tempDir, "bench.bin", FileBytes);
        _writer = opened.Writer;
        _buffer = new PooledPartWriteBuffer(_writer, maxBufferAge: TimeSpan.FromHours(1));
        _buffer.BeginLease(0, FileBytes);
        _blockPayload = new byte[BlockBytes];
        _blockPayload.AsSpan().Fill(0x5A);
        _offset = 0;
    }

    [GlobalCleanup]
    public void CleanUp()
    {
        _buffer?.Dispose();
        _writer?.Abandon();
        try
        {
            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Feeds one 16 KiB network chunk into <see cref="PooledPartWriteBuffer"/>; every 16th call flushes 256 KiB to the OS file handle.
    /// </summary>
    [Benchmark]
    public async ValueTask<PartBufferFlushResult> Buffer16KiBChunkAsync()
    {
        if (!_buffer.HasActiveLease || _buffer.AvailableWindow.Length < ChunkBytes)
        {
            _buffer.DropSuperseded();
            _buffer.BeginLease(0, FileBytes);
        }

        _buffer.AvailableWindow.Span[..ChunkBytes].Fill(0x42);
        return await _buffer.AdvanceBufferedAsync(ChunkBytes).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one full 256 KiB buffer directly through <see cref="PartFileWriter.WriteAsync"/>.
    /// </summary>
    [Benchmark]
    public async ValueTask<DiskStatus> Write256KiBBlockAsync()
    {
        var currentOffset = _offset;
        _offset += BlockBytes;
        if (_offset >= FileBytes)
        {
            _offset = 0;
        }

        return await _writer.WriteAsync(_blockPayload, currentOffset).ConfigureAwait(false);
    }
}
