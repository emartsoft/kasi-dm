using System.Diagnostics;
using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>Launches a user executable, replacing <c>%1</c> with the completed file path (TRD 10.3).</summary>
public sealed class ProcessProgramRunner : IProgramRunner
{
    public Task RunAsync(string programPath, string? arguments, string? completedFilePath = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = programPath,
            Arguments = BuildArguments(arguments, completedFilePath),
            UseShellExecute = true,
        };

        Process.Start(startInfo);
        return Task.CompletedTask;
    }

    /// <summary>Replaces <c>%1</c> with the file path in quotes, so a path with spaces stays one argument.</summary>
    public static string BuildArguments(string? arguments, string? completedFilePath)
    {
        if (arguments is null)
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(completedFilePath))
        {
            return arguments;
        }

        return arguments
            .Replace("\"%1\"", "%1", StringComparison.Ordinal)
            .Replace("%1", $"\"{completedFilePath}\"", StringComparison.Ordinal);
    }
}
