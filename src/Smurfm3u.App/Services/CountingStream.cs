namespace Smurfm3u.App.Services;

/// <summary>
/// Passes a stream through untouched and counts what has been read out of it. A playlist is
/// parsed as it arrives, so bytes consumed is the only honest measure of how far through it
/// the refresh is: the entries are not countable until they have been read.
/// </summary>
public sealed class CountingStream(Stream inner) : Stream
{
    private long read;

    /// <summary>Bytes handed out so far.</summary>
    public long BytesRead => Interlocked.Read(ref read);

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        Count(await inner.ReadAsync(buffer, ct));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));

    private int Count(int justRead)
    {
        if (justRead > 0) Interlocked.Add(ref read, justRead);
        return justRead;
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
