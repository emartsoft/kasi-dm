using System.Globalization;

namespace Kasi.Core;

/// <summary>A UUIDv7 job id: sorts by creation time (to the millisecond) and is stored as lower-case text.</summary>
public readonly record struct JobId(Guid Value)
{
    public static JobId New(TimeProvider time) => new(Guid.CreateVersion7(time.GetUtcNow()));

    /// <summary>Accepts only the 36-character form and only version 7, which is all this application writes.</summary>
    public static bool TryParse(string? text, out JobId id)
    {
        if (Guid.TryParseExact(text, "D", out var guid) && guid.Version == 7)
        {
            id = new JobId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}
