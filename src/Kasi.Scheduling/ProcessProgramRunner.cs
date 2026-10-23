using System.Diagnostics;
using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>Launches a user executable, replacing <c>%1</c> with the completed file path (TRD 10.3).</summary>
public sealed class ProcessProgramRunner : IProgramRunner
{
    public Task RunAsync(string programPath, string? arguments, string? completedFilePath = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programPath);

        string? finalArgs = arguments;
        if (!string.IsNullOrEmpty(completedFilePath) && finalArgs is not null)
        {
            finalArgs = finalArgs.Replace("%1", completedFilePath, StringComparison.OrdinalIgnoreCase);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = programPath,
            Arguments = finalArgs ?? string.Empty,
            UseShellExecute = true,
        };

        Process.Start(startInfo);
        return Task.CompletedTask;
    }
}
