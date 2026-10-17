namespace Kasi.Engine;

/// <summary>
/// Discriminated outcome of opening a <c>.kasipart</c> file (TRD 6.4, ADR 0005).
/// </summary>
public abstract record PartFileOpenOutcome
{
    private protected PartFileOpenOutcome()
    {
    }
}

/// <summary>
/// Successful open outcome holding the active <see cref="PartFileWriter"/>.
/// </summary>
public sealed record PartFileOpened : PartFileOpenOutcome, IAsyncDisposable, IDisposable
{
    public PartFileOpened(PartFileWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Writer = writer;
    }

    public PartFileWriter Writer { get; }

    public void Dispose() => Writer.Dispose();

    public ValueTask DisposeAsync() => Writer.DisposeAsync();
}

/// <summary>
/// Indicates that opening or pre-allocating the <c>.kasipart</c> file failed with a classified <see cref="DiskStatus"/>.
/// </summary>
public sealed record PartFileOpenFailure : PartFileOpenOutcome
{
    public PartFileOpenFailure(DiskStatus status)
    {
        if (status.IsSuccess || status.ErrorKind is null)
        {
            throw new ArgumentException("Open failure requires a non-successful DiskStatus.", nameof(status));
        }

        Status = status;
    }

    public DiskStatus Status { get; }

    public DiskErrorKind ErrorKind => Status.ErrorKind!.Value;
}
