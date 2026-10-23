namespace Kasi.Core;

/// <summary>Abstracts execution of user-specified programs upon queue completion (TRD 10.3, KASI-QUE-004).</summary>
public interface IProgramRunner
{
    Task RunAsync(string programPath, string? arguments, string? completedFilePath = null, CancellationToken cancellationToken = default);
}
