using CommunityToolkit.Mvvm.ComponentModel;
using Kasi.Core;

namespace Kasi.App.ViewModels;

/// <summary>
/// One line of the job list. Shows the file name, never the address (it can carry a token). The <c>Sort...</c> numbers are what the grid
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

    public JobRowViewModel(Job job, string categoryName = "", DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        Id = job.Id;
        FileName = job.FileName;
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
    }

    public JobId Id { get; }

    public string FileName { get; }

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

    private static double PercentOf(long done, long? total) => total is > 0 ? Math.Min(100d, done * 100d / total.Value) : 0d;

    private static string SizeText(long? bytes) => bytes is null ? string.Empty : ByteText.Format(bytes.Value);
}
