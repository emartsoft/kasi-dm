using System.Buffers.Binary;
using Kasi.Ipc.Transport;

namespace Kasi.Ipc.Tests;

[Trait("TC", "TC-BRW-006")]
[Trait("Requirement", "KASI-NFR-010")]
public class FramingTests
{
    private static byte[] Header(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        return header;
    }

    [Fact]
    public async Task A_frame_is_a_little_endian_length_and_the_payload_and_reads_back()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();

        await IpcFraming.WriteAsync(stream, "{\"a\":1}"u8.ToArray(), ct);

        stream.ToArray().Take(4).Should().Equal(7, 0, 0, 0);
        stream.Position = 0;
        (await IpcFraming.ReadAsync(stream, ct)).Should().Equal("{\"a\":1}"u8.ToArray());
        (await IpcFraming.ReadAsync(stream, ct)).Should().BeNull("a clean end of stream before a header is not an error");
    }

    [Fact]
    public async Task A_payload_larger_than_the_chunk_size_is_read_whole()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        using var stream = new MemoryStream();
        await IpcFraming.WriteAsync(stream, payload, ct);
        stream.Position = 0;

        (await IpcFraming.ReadAsync(stream, ct)).Should().Equal(payload);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData((uint)IpcFraming.MaxFrameBytes + 1)]
    [InlineData(uint.MaxValue)]
    public async Task A_length_of_zero_or_over_the_cap_is_an_error(uint length)
    {
        using var stream = new MemoryStream(Header(length));

        var act = async () => await IpcFraming.ReadAsync(stream, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<IpcFramingException>();
    }

    [Fact]
    public async Task The_largest_allowed_length_is_not_allocated_up_front()
    {
        // the header claims 64 MiB and the stream ends: this must fail on the missing bytes, quickly, with a small buffer
        using var stream = new MemoryStream(Header((uint)IpcFraming.MaxFrameBytes));
        var before = GC.GetAllocatedBytesForCurrentThread();

        var act = async () => await IpcFraming.ReadAsync(stream, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<IpcFramingException>();
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(1_000_000);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_stream_that_ends_inside_the_header_is_an_error(int headerBytes)
    {
        using var stream = new MemoryStream(Header(10)[..headerBytes]);

        var act = async () => await IpcFraming.ReadAsync(stream, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<IpcFramingException>();
    }

    [Fact]
    public async Task A_stream_that_ends_inside_the_payload_is_an_error()
    {
        using var stream = new MemoryStream([.. Header(100), .. new byte[10]]);

        var act = async () => await IpcFraming.ReadAsync(stream, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<IpcFramingException>();
    }

    [Fact]
    public async Task An_empty_or_oversized_payload_is_not_written()
    {
        using var stream = new MemoryStream();

        var empty = async () => await IpcFraming.WriteAsync(stream, [], TestContext.Current.CancellationToken);

        await empty.Should().ThrowAsync<IpcFramingException>();
        stream.Length.Should().Be(0);
    }
}
