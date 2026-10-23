using Microsoft.Extensions.Logging;

namespace Kasi.App;

internal static partial class AppLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The job list could not be loaded ({ExceptionType}).")]
    public static partial void JobListFailed(ILogger logger, string exceptionType);
}
