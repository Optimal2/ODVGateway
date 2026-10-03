namespace ODVGateway.Services.Signatures;

/// <summary>Checks the file budget on parser reads and seeks, including parser initialization.</summary>
internal sealed class CancellablePdfStream(byte[] bytes, CancellationToken token) : MemoryStream(bytes, writable: false)
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        token.ThrowIfCancellationRequested();
        return base.Read(buffer, offset, count);
    }
    public override int Read(Span<byte> buffer)
    {
        token.ThrowIfCancellationRequested();
        return base.Read(buffer);
    }
    public override int ReadByte()
    {
        token.ThrowIfCancellationRequested();
        return base.ReadByte();
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        token.ThrowIfCancellationRequested();
        return base.Seek(offset, origin);
    }
}
