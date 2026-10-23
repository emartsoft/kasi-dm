using Microsoft.Extensions.Logging;

namespace Kasi.App;

internal static partial class AppLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The job list could not be loaded ({ExceptionType}).")]
    public static partial void JobListFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A download could not be added ({ExceptionType}).")]
    public static partial void AddFailed(ILogger logger, string exceptionType);
}
