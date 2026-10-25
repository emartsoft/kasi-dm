namespace Kasi.Core;

/// <summary>Why a job is paused. Only <see cref="NetworkOffline"/> is resumed automatically after a restart.</summary>
public enum PauseReason
{
    UserRequested,
    NotEnoughSpace,
    NetworkOffline,
    CrashRecovery,
    CheckpointFault,
    AuthRequired,
    Quota,
}

/// <summary>The text stored in <c>job.pause_reason</c>; a storage format, so changing a string needs a migration.</summary>
public static class PauseReasonText
{
    public static string ToText(PauseReason reason) => reason switch
    {
        PauseReason.UserRequested => "UserRequested",
        PauseReason.NotEnoughSpace => "NotEnoughSpace",
        PauseReason.NetworkOffline => "NetworkOffline",
        PauseReason.CrashRecovery => "CrashRecovery",
        PauseReason.CheckpointFault => "CheckpointFault",
        PauseReason.AuthRequired => "AuthRequired",
        PauseReason.Quota => "Quota",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a defined pause reason."),
    };

    public static bool TryParse(string? text, out PauseReason reason)
    {
        (var found, reason) = text switch
        {
            "UserRequested" => (true, PauseReason.UserRequested),
            "NotEnoughSpace" => (true, PauseReason.NotEnoughSpace),
            "NetworkOffline" => (true, PauseReason.NetworkOffline),
            "CrashRecovery" => (true, PauseReason.CrashRecovery),
            "CheckpointFault" => (true, PauseReason.CheckpointFault),
            "AuthRequired" => (true, PauseReason.AuthRequired),
            "Quota" => (true, PauseReason.Quota),
            _ => (false, default(PauseReason)),
        };
        return found;
    }
}
