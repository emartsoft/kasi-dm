using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Kasi.Core;

namespace Kasi.App.ViewModels;

/// <summary>One line of the job list. Shows the file name, never the URL (it can carry a token).</summary>
internal sealed partial class JobRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _state;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _size;

    public JobRowViewModel(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Id = job.Id;
        FileName = job.FileName;
        _state = JobStateText.ToText(job.State);
        _size = SizeText(job.TotalBytes);
        _percent = PercentOf(job.DoneBytes, job.TotalBytes);
    }

    public JobId Id { get; }

    public string FileName { get; }

    public void Progressed(long doneBytes, long? totalBytes)
    {
        Percent = PercentOf(doneBytes, totalBytes);
        Size = SizeText(totalBytes);
    }

    public void StateChanged(JobState state) => State = JobStateText.ToText(state);

    private static double PercentOf(long done, long? total) => total is > 0 ? Math.Min(100d, done * 100d / total.Value) : 0d;

    private static string SizeText(long? bytes) => bytes is null ? "" : string.Create(CultureInfo.CurrentCulture, $"{bytes.Value / 1048576d:0.#} MB");
}
