using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Kasi.Core;
using Kasi.Engine;

namespace Kasi.Media;

/// <summary>
/// Downloads a <see cref="MediaSourceLoader.Playable"/> plan as whole segments (M3.3): per stream, at most
/// <see cref="MediaJobOptions.Parallelism"/> in flight, each written to a part file, flushed, renamed, and only then marked
/// complete in SQLite through a 1 s batch writer. Initialization segments and AES-128 are handled before the ordered join.
/// </summary>
public sealed class MediaJobRunner(
    IMediaRepository media,
    IJobRepository jobs,
    TimeProvider? timeProvider = null)
{
    /// <summary>The largest segment or init body held in memory; an HLS/DASH segment is seconds of media, never this large.</summary>
    internal const long MaxSegmentBytes = 64L * 1024 * 1024;

    /// <summary>An AES-128 key is 16 bytes; anything much larger is not a key.</summary>
    internal const long MaxKeyBytes = 1024;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<MediaJobResult> RunAsync(
        MediaSourceLoader.Playable playable,
        MediaJobOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playable);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var job = await jobs.GetAsync(options.JobId, cancellationToken).ConfigureAwait(false)
            ?? throw new MediaJobException(MediaJobErrorKind.InvalidJob, "The media job does not exist.");
        if (job.Kind != JobKind.Media)
        {
            throw new MediaJobException(MediaJobErrorKind.InvalidJob, "The job is not a media job.");
        }

        if (job.State is not (JobState.Downloading or JobState.Muxing))
        {
            throw new MediaJobException(MediaJobErrorKind.InvalidJob, "The media job is not downloading.");
        }

        var variant = options.SelectedVariant ?? playable.Presentation.SelectDefault()
            ?? throw new MediaJobException(MediaJobErrorKind.InvalidJob, "The presentation has no video variant.");

        var tracks = new List<MediaTrack> { variant.Video };
        if (variant.Audio is { } audio)
        {
            tracks.Add(audio);
        }

        if (tracks.Any(track => track.IsLive))
        {
            throw new MediaJobException(MediaJobErrorKind.InvalidJob, "Live media is not supported in M3.");
        }

        var root = options.MediaRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kasi", "media");
        var jobDirectory = Path.Combine(root, options.JobId.ToString());
        try
        {
            Directory.CreateDirectory(jobDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MediaJobException(Classify(exception), "The media folder could not be created.", exception);
        }

        var plan = BuildPlan(tracks, variant.EstimatedBytes);
        var keys = tracks.SelectMany((track, streamIndex) => track.Segments.Select(segment => (Stream: streamIndex, segment.Index, segment.Key)))
            .Where(item => item.Key is not null)
            .ToDictionary(item => (item.Stream, item.Index), item => item.Key!);
        var existingStreams = await media.LoadStreamsAsync(options.JobId, cancellationToken).ConfigureAwait(false);
        var existingSegments = await media.LoadSegmentsAsync(options.JobId, cancellationToken).ConfigureAwait(false);
        if (!Matches(existingStreams, existingSegments, plan.Streams, plan.Segments))
        {
            if (Directory.Exists(jobDirectory))
            {
                Directory.Delete(jobDirectory, recursive: true);
            }

            Directory.CreateDirectory(jobDirectory);
            await media.ReplaceAsync(options.JobId, plan.Streams, plan.Segments, plan.EstimatedTotal, cancellationToken).ConfigureAwait(false);
            existingStreams = plan.Streams;
            existingSegments = plan.Segments;
        }

        foreach (var stream in existingStreams)
        {
            Directory.CreateDirectory(StreamDirectory(jobDirectory, stream.Index));
        }

        CleanStrayParts(jobDirectory, existingStreams);

        using var run = new RunState(options.Parallelism, cancellationToken);
        using var pool = new HttpHostPool(options.PoolOptions, proxy: options.Proxy);
        var client = new SegmentClient(pool, options, _time);
        var progress = new ProgressTracker(options.Progress, existingSegments.Count, options.Events, options.JobId, plan.EstimatedTotal, _time);
        var completionWriter = new CompletionWriter(media, options.JobId, plan.EstimatedTotal, options.CompletionInterval, _time, run);
        try
        {
            await DownloadInitsAsync(existingStreams, jobDirectory, client, run).ConfigureAwait(false);
            var byStream = existingSegments.GroupBy(segment => segment.StreamIndex)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<MediaSegmentRecord>)[.. group.OrderBy(segment => segment.Index)]);
            using var keyCache = new KeyCache(client);
            try
            {
                await Task.WhenAll(existingStreams.Select(stream =>
                    DownloadStreamAsync(stream, byStream.GetValueOrDefault(stream.Index, []), keys, jobDirectory, client, keyCache, completionWriter, progress, run))).ConfigureAwait(false);
            }
            catch when (run.Failure is { } failure && !cancellationToken.IsCancellationRequested)
            {
                // The first real failure cancelled its siblings; surface it, not a sibling's cancellation.
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            await completionWriter.FlushAsync().ConfigureAwait(false);
            var joined = await JoinAsync(existingStreams, existingSegments, jobDirectory, cancellationToken).ConfigureAwait(false);
            if (job.State == JobState.Downloading)
            {
                if (await jobs.TryChangeStateAsync(options.JobId, JobState.Downloading, JobState.Muxing, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    options.Events?.Invoke(new JobStateChanged(options.JobId, JobState.Downloading, JobState.Muxing, null));
                }
            }

            return new MediaJobResult(options.JobId, joined, joined.Sum(stream => stream.Bytes), variant, jobDirectory);
        }
        catch (OperationCanceledException)
        {
            await completionWriter.TryFlushAsync().ConfigureAwait(false);
            throw;
        }
        catch (MediaJobException)
        {
            await completionWriter.TryFlushAsync().ConfigureAwait(false);
            await FailAsync(job, options, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await completionWriter.TryFlushAsync().ConfigureAwait(false);
            var error = Classify(exception);
            await FailAsync(job, options, cancellationToken).ConfigureAwait(false);
            throw new MediaJobException(error, "The media download failed.", exception);
        }
        finally
        {
            await completionWriter.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task FailAsync(Job job, MediaJobOptions options, CancellationToken cancellationToken)
    {
        const string Message = "Media segment download failed.";
        if (job.State == JobState.Downloading
            && await jobs.TryChangeStateAsync(job.Id, JobState.Downloading, JobState.Failed, Message, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            options.Events?.Invoke(new JobStateChanged(job.Id, JobState.Downloading, JobState.Failed, Message));
        }
    }

    private static MediaJobErrorKind Classify(Exception exception)
    {
        if (exception is IOException io)
        {
            var code = io.HResult & 0xFFFF;
            return code is 0x70 or 0x27 ? MediaJobErrorKind.DiskFull : MediaJobErrorKind.Disk;
        }

        return MediaJobErrorKind.Network;
    }

    private async Task DownloadInitsAsync(
        IReadOnlyList<MediaStreamRecord> streams,
        string jobDirectory,
        SegmentClient client,
        RunState run)
    {
        foreach (var stream in streams.Where(stream => stream.InitUrl is not null))
        {
            var path = Path.Combine(StreamDirectory(jobDirectory, stream.Index), "init");
            if (File.Exists(path))
            {
                continue;
            }

            var bytes = await client.GetBytesAsync(
                new Uri(stream.InitUrl!), run.NextWorker(0), stream.InitRangeStart, stream.InitRangeEnd, MaxSegmentBytes, run.Token).ConfigureAwait(false);
            var part = path + ".part";
            try
            {
                await WriteFlushedAsync(part, bytes, run.Token).ConfigureAwait(false);
                File.Move(part, path, overwrite: true);
            }
            catch
            {
                TryDelete(part);
                throw;
            }
        }
    }

    private async Task DownloadStreamAsync(
        MediaStreamRecord stream,
        IReadOnlyList<MediaSegmentRecord> segments,
        Dictionary<(int Stream, int Index), MediaKey> keys,
        string jobDirectory,
        SegmentClient client,
        KeyCache keyCache,
        CompletionWriter completionWriter,
        ProgressTracker progress,
        RunState run)
    {
        using var gate = new SemaphoreSlim(Math.Min(segments.Count, run.Parallelism));
        var directory = StreamDirectory(jobDirectory, stream.Index);
        var tasks = segments.Select(async segment =>
        {
            await gate.WaitAsync(run.Token).ConfigureAwait(false);
            try
            {
                keys.TryGetValue((stream.Index, segment.Index), out var key);
                await DownloadSegmentAsync(stream, segment, key, directory, client, keyCache, completionWriter, progress, run).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                run.Fail(exception); // stop the siblings; Release below runs after the token is cancelled
                throw;
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task DownloadSegmentAsync(
        MediaStreamRecord stream,
        MediaSegmentRecord segment,
        MediaKey? key,
        string directory,
        SegmentClient client,
        KeyCache keyCache,
        CompletionWriter completionWriter,
        ProgressTracker progress,
        RunState run)
    {
        var final = Path.Combine(directory, segment.Index.ToString(CultureInfo.InvariantCulture));
        if (File.Exists(final))
        {
            var length = new FileInfo(final).Length;
            completionWriter.Enqueue(new MediaSegmentCompletion(stream.Index, segment.Index, length));
            progress.Complete(length);
            return;
        }

        var part = final + ".part";
        try
        {
            var cipher = await client.GetBytesAsync(
                new Uri(segment.Url), run.NextWorker(stream.Index * run.Parallelism), segment.RangeStart, segment.RangeEnd, MaxSegmentBytes, run.Token).ConfigureAwait(false);
            var plain = await keyCache.DecryptAsync(key, segment.Iv, segment.Index, cipher, run.NextWorker(stream.Index * run.Parallelism), run.Token).ConfigureAwait(false);
            await WriteFlushedAsync(part, plain, run.Token).ConfigureAwait(false);
            File.Move(part, final, overwrite: true);
            completionWriter.Enqueue(new MediaSegmentCompletion(stream.Index, segment.Index, plain.Length));
            progress.Complete(plain.Length);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

#pragma warning disable CA2000, CA2007 // await using owns these streams; their disposal has no ConfigureAwait
    private async Task<IReadOnlyList<MediaJoinedTrack>> JoinAsync(
        IReadOnlyList<MediaStreamRecord> streams,
        IReadOnlyList<MediaSegmentRecord> segments,
        string jobDirectory,
        CancellationToken cancellationToken)
    {
        var joinedDirectory = Path.Combine(jobDirectory, "joined");
        Directory.CreateDirectory(joinedDirectory);
        var result = new List<MediaJoinedTrack>();
        foreach (var stream in streams)
        {
            var path = Path.Combine(joinedDirectory, $"{stream.Index}.bin");
            await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var init = Path.Combine(StreamDirectory(jobDirectory, stream.Index), "init");
                if (File.Exists(init))
                {
                    await using var input = File.OpenRead(init);
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }

                foreach (var segment in segments.Where(segment => segment.StreamIndex == stream.Index).OrderBy(segment => segment.Index))
                {
                    var file = Path.Combine(StreamDirectory(jobDirectory, stream.Index), segment.Index.ToString(CultureInfo.InvariantCulture));
                    if (!File.Exists(file))
                    {
                        throw new MediaJobException(MediaJobErrorKind.SegmentMismatch, "A completed media segment file is missing.");
                    }

                    await using var input = File.OpenRead(file);
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            result.Add(new MediaJoinedTrack(stream.Index, stream.Role, path, new FileInfo(path).Length, stream.Container, InputFormatFor(stream, segments)));
        }

        return result;
    }
#pragma warning restore CA2000, CA2007

    private static void CleanStrayParts(string jobDirectory, IReadOnlyList<MediaStreamRecord> streams)
    {
        foreach (var stream in streams)
        {
            foreach (var part in Directory.EnumerateFiles(StreamDirectory(jobDirectory, stream.Index), "*.part"))
            {
                TryDelete(part);
            }
        }
    }

    /// <summary>True when the saved structure is the one this plan would create (same streams, init and segment URLs). O(n).</summary>
    internal static bool Matches(
        IReadOnlyList<MediaStreamRecord> existingStreams,
        IReadOnlyList<MediaSegmentRecord> existingSegments,
        IReadOnlyList<MediaStreamRecord> plannedStreams,
        IReadOnlyList<MediaSegmentRecord> plannedSegments)
    {
        if (existingStreams.Count != plannedStreams.Count || existingSegments.Count != plannedSegments.Count)
        {
            return false;
        }

        var streams = plannedStreams.ToDictionary(stream => stream.Index);
        if (existingStreams.Any(stream => !streams.TryGetValue(stream.Index, out var planned)
            || planned.Role != stream.Role
            || planned.InitUrl != stream.InitUrl
            || planned.InitRangeStart != stream.InitRangeStart
            || planned.InitRangeEnd != stream.InitRangeEnd))
        {
            return false;
        }

        var urls = plannedSegments.ToDictionary(segment => (segment.StreamIndex, segment.Index), segment => segment.Url);
        return existingSegments.All(segment => urls.TryGetValue((segment.StreamIndex, segment.Index), out var url) && url == segment.Url);
    }

    internal static (IReadOnlyList<MediaStreamRecord> Streams, IReadOnlyList<MediaSegmentRecord> Segments, long EstimatedTotal) BuildPlan(
        List<MediaTrack> tracks,
        long estimatedTotal)
    {
        var streams = new List<MediaStreamRecord>();
        var segments = new List<MediaSegmentRecord>();
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            if (track.Segments.Count == 0)
            {
                throw new MediaJobException(MediaJobErrorKind.InvalidJob, "A selected stream has no segments.");
            }

            streams.Add(new MediaStreamRecord(
                index,
                track.Role.ToString().ToLowerInvariant(),
                track.Language,
                track.Codecs,
                track.MimeType,
                track.Id,
                track.EstimatedBytes,
                track.Initialization?.Uri.AbsoluteUri,
                track.Initialization?.Range?.Length,
                track.Initialization?.Range?.Offset,
                track.Initialization?.Range?.EndInclusive));
            foreach (var segment in track.Segments.OrderBy(segment => segment.Index))
            {
                segments.Add(new MediaSegmentRecord(
                    index,
                    segment.Index,
                    segment.Uri.AbsoluteUri,
                    Iv: segment.Key?.Iv ?? ImplicitIv(segment),
                    Discontinuity: segment.Discontinuity,
                    RangeStart: segment.Range?.Offset,
                    RangeEnd: segment.Range?.EndInclusive));
            }
        }

        return (streams, segments, estimatedTotal);
    }

    /// <summary>RFC 8216: an AES-128 key without an IV uses the segment's media sequence number as a 16-byte big-endian IV.</summary>
    private static string? ImplicitIv(MediaSegment segment) =>
        segment.Key is { } key && key.Method.Trim().Equals("AES-128", StringComparison.OrdinalIgnoreCase) && segment.Sequence is { } sequence
            ? SequenceIv(sequence)
            : null;

    internal static string SequenceIv(long sequence)
    {
        Span<byte> iv = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(iv[8..], sequence);
        return Convert.ToHexString(iv);
    }

    /// <summary>The demuxer FFmpeg must use for a joined stream, so it never sniffs attacker-controlled bytes into a playlist or script format.</summary>
    internal static string? InputFormatFor(MediaStreamRecord stream, IReadOnlyList<MediaSegmentRecord> segments)
    {
        if (stream.InitUrl is not null)
        {
            return "mp4";
        }

        var first = segments.Where(segment => segment.StreamIndex == stream.Index).OrderBy(segment => segment.Index).FirstOrDefault();
        if (first is null || !Uri.TryCreate(first.Url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
        {
            ".ts" or ".m2ts" => "mpegts",
            ".m4s" or ".mp4" or ".m4a" or ".m4v" or ".cmfv" or ".cmfa" => "mp4",
            ".aac" or ".adts" => "aac",
            ".mp3" => "mp3",
            _ => null,
        };
    }

    private static string StreamDirectory(string jobDirectory, int streamIndex) =>
        Path.Combine(jobDirectory, streamIndex.ToString(CultureInfo.InvariantCulture));

    private static async Task WriteFlushedAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);
        await RandomAccess.WriteAsync(handle, bytes, 0, cancellationToken).ConfigureAwait(false);
        RandomAccess.FlushToDisk(handle);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ProgressTracker(
        Action<MediaJobProgress>? report,
        int total,
        Action<EngineEvent>? events,
        JobId jobId,
        long estimatedTotal,
        TimeProvider time)
    {
        private readonly long _started = time.GetTimestamp();
        private int _completed;
        private long _bytes;

        public void Complete(long bytes)
        {
            var completed = Interlocked.Increment(ref _completed);
            var done = Interlocked.Add(ref _bytes, bytes);
            report?.Invoke(new MediaJobProgress(completed, total, done));
            if (events is not null)
            {
                var seconds = Math.Max(time.GetElapsedTime(_started).TotalSeconds, 0.001);
                events(new JobProgressed(jobId, done, estimatedTotal > 0 ? estimatedTotal : null, (long)(done / seconds)));
            }
        }
    }

    private sealed class CompletionWriter : IAsyncDisposable
    {
        private readonly IMediaRepository _media;
        private readonly JobId _jobId;
        private readonly long? _estimatedTotal;
        private readonly TimeSpan _interval;
        private readonly TimeProvider _time;
        private readonly RunState _run;
        private readonly Channel<MediaSegmentCompletion> _channel = Channel.CreateUnbounded<MediaSegmentCompletion>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private Exception? _fault;

        public CompletionWriter(IMediaRepository media, JobId jobId, long? estimatedTotal, TimeSpan interval, TimeProvider time, RunState run)
        {
            _media = media;
            _jobId = jobId;
            _estimatedTotal = estimatedTotal;
            _interval = interval;
            _time = time;
            _run = run;
            _loop = LoopAsync();
        }

        public void Enqueue(MediaSegmentCompletion completion) => _channel.Writer.TryWrite(completion);

        /// <summary>Writes what is queued, then reports a save failure as a disk error.</summary>
        public async Task FlushAsync()
        {
            await StopAsync().ConfigureAwait(false);
            if (_fault is { } fault)
            {
                throw new MediaJobException(MediaJobErrorKind.Disk, "Finished segments could not be saved.", fault);
            }
        }

        /// <summary>For failure paths: writes what is queued and never throws, so it cannot hide the original error.</summary>
        public Task TryFlushAsync() => StopAsync();

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _stop.Dispose();
        }

#pragma warning disable VSTHRD003 // the writer loop belongs to this runner and is awaited only after it is completed
        private async Task StopAsync()
        {
            _channel.Writer.TryComplete();
            await _stop.CancelAsync().ConfigureAwait(false); // ends the batch pause at once
            await _loop.ConfigureAwait(false);
        }
#pragma warning restore VSTHRD003

        private async Task LoopAsync()
        {
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var batch = new List<MediaSegmentCompletion>();
                while (reader.TryRead(out var completion))
                {
                    batch.Add(completion);
                }

                if (batch.Count > 0)
                {
                    try
                    {
                        await _media.MarkCompletedAsync(_jobId, batch, _estimatedTotal, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        _fault = exception;
                        _run.Fail(new MediaJobException(MediaJobErrorKind.Disk, "Finished segments could not be saved.", exception));
                        return;
                    }
                }

                try
                {
                    await Task.Delay(_interval, _time, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // flush requested: drain what is left without waiting
                }
            }
        }
    }

    /// <summary>Per-run state, so one runner can serve several jobs at once. Cancelled on the first real failure.</summary>
    private sealed class RunState(int parallelism, CancellationToken parent) : IDisposable
    {
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        private int _worker;
        private Exception? _failure;

        public int Parallelism { get; } = parallelism;

        public CancellationToken Token => _cts.Token;

        public Exception? Failure => Volatile.Read(ref _failure);

        public int NextWorker(int baseId) => baseId + (int)((uint)Interlocked.Increment(ref _worker) % Parallelism);

        public void Fail(Exception exception)
        {
            Interlocked.CompareExchange(ref _failure, exception, null);
            _cts.Cancel();
        }

        public void Dispose() => _cts.Dispose();
    }

    private sealed class SegmentClient(HttpHostPool pool, MediaJobOptions options, TimeProvider time)
    {
        public async Task<byte[]> GetBytesAsync(Uri uri, int worker, long? rangeStart, long? rangeEnd, long maxBytes, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(options.SegmentTimeout);
                    using var response = await SendAsync(uri, worker, rangeStart, rangeEnd, timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var retryAfter = RetryAfter(response);
                        var decision = options.RetryPolicy.Decide(
                            TransferErrorKind.HttpStatusError,
                            response.StatusCode,
                            retryAfter,
                            failedAttempts: attempt);
                        if (decision.Retry)
                        {
                            await Task.Delay(decision.Delay, time, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        throw new MediaJobException(MediaJobErrorKind.Network, $"The segment server returned HTTP {(int)response.StatusCode}.");
                    }

                    if (rangeStart is { } rangeStartValue && rangeEnd is { } rangeEndValue
                        && response.StatusCode == HttpStatusCode.OK
                        && response.Content.Headers.ContentLength != rangeEndValue - rangeStartValue + 1)
                    {
                        throw new MediaJobException(MediaJobErrorKind.Network, "The segment server ignored a byte range.");
                    }

                    var length = response.Content.Headers.ContentLength;
                    if (length > maxBytes)
                    {
                        throw new MediaJobException(MediaJobErrorKind.Network, "A media segment is larger than the allowed size.");
                    }

                    using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    if (length is { } known)
                    {
                        // Known length: read straight into the final array, no second copy.
                        var exact = new byte[known];
                        var filled = 0;
                        while (filled < exact.Length)
                        {
                            var read = await ReadAsync(body, exact.AsMemory(filled), timeout.Token).ConfigureAwait(false);
                            if (read == 0)
                            {
                                throw new IOException("The segment body was truncated.");
                            }

                            filled += read;
                        }

                        return exact;
                    }

                    var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
                    try
                    {
                        using var memory = new MemoryStream();
                        int count;
                        while ((count = await ReadAsync(body, buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
                        {
                            if (memory.Length + count > maxBytes)
                            {
                                throw new MediaJobException(MediaJobErrorKind.Network, "A media segment is larger than the allowed size.");
                            }

                            await memory.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        }

                        return memory.ToArray();
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    var decision = options.RetryPolicy.Decide(TransferErrorKind.TransientNetworkError, failedAttempts: attempt);
                    if (decision.Retry)
                    {
                        await Task.Delay(decision.Delay, time, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new MediaJobException(MediaJobErrorKind.Network, "A media segment timed out.");
                }
                catch (HttpRequestException exception)
                {
                    var decision = options.RetryPolicy.Decide(TransferErrorKind.TransientNetworkError, failedAttempts: attempt);
                    if (decision.Retry)
                    {
                        await Task.Delay(decision.Delay, time, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new MediaJobException(MediaJobErrorKind.Network, "A media segment request failed.", exception);
                }
                catch (IOException exception)
                {
                    var decision = options.RetryPolicy.Decide(TransferErrorKind.ProtocolError, failedAttempts: attempt);
                    if (decision.Retry)
                    {
                        await Task.Delay(decision.Delay, time, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new MediaJobException(MediaJobErrorKind.Network, "A media segment body was incomplete.", exception);
                }
            }
        }

        private async Task<int> ReadAsync(Stream body, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (options.BandwidthLimiter is not { } limiter)
            {
                return await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            var granted = await limiter.AcquireAsync(buffer.Length, options.JobBandwidthLimit, cancellationToken).ConfigureAwait(false);
            var read = await body.ReadAsync(buffer[..granted], cancellationToken).ConfigureAwait(false);
            limiter.ReturnUnused(granted - read, options.JobBandwidthLimit);
            return read;
        }

        private async Task<HttpResponseMessage> SendAsync(Uri uri, int worker, long? rangeStart, long? rangeEnd, CancellationToken cancellationToken)
        {
            var current = uri;
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                // The referrer goes to this resource's own origin (CDNs check it) but not to a cross-origin redirect target.
                using var request = MediaSourceLoader.BuildRequest(current, uri, options.RequestContext);
                if (rangeStart is { } start && rangeEnd is { } end)
                {
                    request.Headers.Range = new RangeHeaderValue(start, end);
                }

                var response = await pool.GetInvoker(current, worker).SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location;
                    response.Dispose();
                    if (location is null
                        || !Uri.TryCreate(current, location, out var next)
                        || !next.IsAbsoluteUri
                        || next.Scheme is not ("http" or "https"))
                    {
                        throw new MediaJobException(MediaJobErrorKind.Network, "A media segment redirect was invalid.");
                    }

                    current = next;
                    continue;
                }

                return response;
            }

            throw new MediaJobException(MediaJobErrorKind.Network, "A media segment had too many redirects.");
        }

        private TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var retry = response.Headers.RetryAfter;
            if (retry?.Delta is { } delta)
            {
                return delta;
            }

            return retry?.Date is { } date ? date - time.GetUtcNow() : null;
        }
    }

    private sealed class KeyCache(SegmentClient client) : IDisposable
    {
        private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _keyGate = new(1, 1);

        public async Task<byte[]> DecryptAsync(
            MediaKey? key,
            string? ivText,
            int index,
            byte[] cipher,
            int worker,
            CancellationToken cancellationToken)
        {
            if (key is null)
            {
                return cipher;
            }

            var method = key.Method.Trim().ToUpperInvariant();
            if (method == "NONE")
            {
                return cipher;
            }

            if (method != "AES-128")
            {
                throw new MediaJobException(MediaJobErrorKind.InvalidJob, "The segment uses unsupported encryption.");
            }

            if (key.Uri is not { IsAbsoluteUri: true, Scheme: "https" })
            {
                throw new MediaJobException(MediaJobErrorKind.InvalidJob, "The AES-128 key URI is not HTTPS.");
            }

            var keyUri = key.Uri.AbsoluteUri;
            byte[] keyBytes;
            await _keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_keys.TryGetValue(keyUri, out keyBytes!))
                {
                    keyBytes = await client.GetBytesAsync(key.Uri, worker, null, null, MaxKeyBytes, cancellationToken).ConfigureAwait(false);
                    _keys[keyUri] = keyBytes;
                }
            }
            finally
            {
                _keyGate.Release();
            }
            if (keyBytes.Length != 16)
            {
                throw new MediaJobException(MediaJobErrorKind.InvalidJob, "The AES-128 key has the wrong length.");
            }

            using var aes = Aes.Create();
            aes.Key = keyBytes;
            aes.IV = ParseIv(ivText, index);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        }

        public void Dispose() => _keyGate.Dispose();

        private static byte[] ParseIv(string? text, int index)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                var hex = text.Trim();
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    hex = hex[2..];
                }

                try
                {
                    var iv = Convert.FromHexString(hex);
                    if (iv.Length == 16)
                    {
                        return iv;
                    }
                }
                catch (FormatException)
                {
                }

                throw new MediaJobException(MediaJobErrorKind.InvalidJob, "The segment IV is not 16 bytes of hexadecimal.");
            }

            // RFC 8216: when EXT-X-KEY has no IV, the media sequence number is the IV, big-endian in 16 bytes.
            var sequence = new byte[16];
            sequence[12] = (byte)((uint)index >> 24);
            sequence[13] = (byte)((uint)index >> 16);
            sequence[14] = (byte)((uint)index >> 8);
            sequence[15] = (byte)(uint)index;
            return sequence;
        }
    }
}
