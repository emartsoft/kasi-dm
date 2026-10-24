using CommunityToolkit.Mvvm.ComponentModel;
using Kasi.App.Controls;
using Kasi.Core;

namespace Kasi.App.ViewModels;

/// <summary>
/// One line of the job list. Shows the file name and the host, never the whole address (it can carry a token). The <c>Sort...</c> numbers are what the grid
/// sorts by, so "Size" sorts by bytes and "Time left" by seconds, not by their text.
/// </summary>
internal sealed partial class JobRowViewModel : ObservableObject
{
    private double _smoothedSpeed;

    [ObservableProperty]
    private JobState _stateValue;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _size;

    [ObservableProperty]
    private string _speed = string.Empty;

    [ObservableProperty]
    private string _timeLeft = string.Empty;

    private long _doneBytes;
    private long? _totalBytes;

    [ObservableProperty]
    private string _downloaded = string.Empty;

    public JobRowViewModel(Job job, string categoryName = "", DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        Id = job.Id;
        FileName = job.FileName;
        Host = Uri.TryCreate(job.Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        QueueId = job.QueueId;
        CategoryId = job.CategoryId;
        CategoryName = categoryName;
        Created = job.CreatedUtc;
        DateAdded = JobText.DateAdded(job.CreatedUtc, now ?? TimeProvider.System.GetUtcNow());
        _stateValue = job.State;
        _doneBytes = job.DoneBytes;
        _totalBytes = job.TotalBytes;
        _size = SizeText(job.TotalBytes);
        _percent = PercentOf(job.DoneBytes, job.TotalBytes);
        _downloaded = DownloadedText(job.DoneBytes, job.TotalBytes);
    }

    public JobId Id { get; }

    public string FileName { get; }

    /// <summary>The site the file comes from (the second line under the name); empty when the address cannot be read.</summary>
    public string Host { get; }

    /// <summary>The colour of the status badge: green while it transfers, purple when done, amber when paused, red when failed.</summary>
    public ChipKind StatusKind => StateValue switch
    {
        JobState.Completed => ChipKind.Accent,
        JobState.Failed => ChipKind.Danger,
        JobState.Paused => ChipKind.Warning,
        JobState.Queued => ChipKind.Neutral,
        _ => ChipKind.Success,
    };

    public int QueueId { get; }

    public int? CategoryId { get; }

    public string CategoryName { get; }

    public DateTimeOffset Created { get; }

    public string DateAdded { get; }

    /// <summary>The status in words.</summary>
    public string State => JobText.State(StateValue);

    public bool IsActive => JobText.IsActive(StateValue);

    public long SortSize => _totalBytes ?? -1;

    public long SortSpeed => (long)_smoothedSpeed;

    /// <summary>Seconds left; a job whose time is unknown sorts last.</summary>
    public long SortTimeLeft => SecondsLeft() ?? long.MaxValue;

    /// <summary>Bytes per second as last reported (smoothed), 0 when nothing moves.</summary>
    public long BytesPerSecond => (long)_smoothedSpeed;

    public override string ToString() => nameof(JobRowViewModel); // the file name and address are the user's business, not a log line's

    public void Progressed(long doneBytes, long? totalBytes, long bytesPerSecond)
    {
        _doneBytes = doneBytes;
        _totalBytes = totalBytes;
        Percent = PercentOf(doneBytes, totalBytes);
        Size = SizeText(totalBytes);
        Downloaded = DownloadedText(doneBytes, totalBytes);
        // a short average, so the numbers do not jitter and "time left" does not leap
        _smoothedSpeed = bytesPerSecond <= 0 ? 0 : _smoothedSpeed <= 0 ? bytesPerSecond : (_smoothedSpeed * 0.7) + (bytesPerSecond * 0.3);
        Refresh();
    }

    public void StateChanged(JobState state)
    {
        StateValue = state;
        if (state != JobState.Downloading)
        {
            _smoothedSpeed = 0;
        }

        Refresh();
    }

    partial void OnStateValueChanged(JobState value)
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(StatusKind));
    }

    private void Refresh()
    {
        Speed = JobText.Speed((long)_smoothedSpeed);
        TimeLeft = StateValue == JobState.Downloading ? JobText.TimeLeft(SecondsLeft()) : string.Empty;
        OnPropertyChanged(nameof(SortSpeed));
        OnPropertyChanged(nameof(BytesPerSecond));
        OnPropertyChanged(nameof(SortTimeLeft));
        OnPropertyChanged(nameof(SortSize));
    }

    private long? SecondsLeft() =>
        _totalBytes is { } total && _smoothedSpeed > 0 && StateValue == JobState.Downloading
            ? (long)Math.Ceiling(Math.Max(0, total - _doneBytes) / _smoothedSpeed)
            : null;

    // "3.66 GB of 5.9 GB", or just the bytes so far when the size is not known
    private static string DownloadedText(long done, long? total) => total is null ? ByteText.Format(done) : ByteText.Format(done) + " / " + ByteText.Format(total.Value);

    private static double PercentOf(long done, long? total) => total is > 0 ? Math.Min(100d, done * 100d / total.Value) : 0d;

    private static string SizeText(long? bytes) => bytes is null ? string.Empty : ByteText.Format(bytes.Value);
}
