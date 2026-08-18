using System.Buffers.Binary;
using SymphonyRecomp.Automation.Contracts;

namespace SymphonyRecomp.Automation.Tests;

public sealed class AutomationProtocolTests
{
    [Fact]
    public async Task FrameRoundTripsAcrossPartialReads()
    {
        var expected = new AutomationRequest("request-1", "secret", "bridge.status", TimeoutMs: 1200);
        using var encoded = new MemoryStream();
        await AutomationProtocol.WriteAsync(encoded, expected);
        using var chunked = new ChunkedReadStream(encoded.ToArray(), 2);

        AutomationRequest? actual = await AutomationProtocol.ReadAsync<AutomationRequest>(chunked);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforeAllocation()
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, AutomationProtocol.MaxFrameBytes + 1);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await AutomationProtocol.ReadAsync<AutomationRequest>(stream));
    }

    [Fact]
    public async Task EndOfStreamBeforeHeaderReturnsNull()
    {
        using var stream = new MemoryStream();
        Assert.Null(await AutomationProtocol.ReadAsync<AutomationRequest>(stream));
    }

    private sealed class ChunkedReadStream(byte[] data, int chunkSize) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
