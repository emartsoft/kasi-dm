using System.Text.Json;
using System.Text.Json.Serialization;
using Kasi.App.Browser;
using Kasi.Core;

namespace Kasi.App.Engine;

/// <summary>The versioned, secret-free backup format (KASI-SYS-004, TC-SYS-005).</summary>
internal sealed record BackupDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public DateTimeOffset ExportedUtc { get; init; }

    public BrowserSettings? Browser { get; init; }

    public IReadOnlyList<Category> Categories { get; init; } = [];

    public IReadOnlyList<CategoryRule> Rules { get; init; } = [];

    public IReadOnlyList<Queue> Queues { get; init; } = [];

    public IReadOnlyList<QueueSchedule> Schedules { get; init; } = [];

    public IReadOnlyList<SpeedProfile> SpeedProfiles { get; init; } = [];

    public IReadOnlyList<Job> Jobs { get; init; } = [];
}

internal sealed record BackupImportResult(bool Imported, int Categories, int Rules, int Queues, int Profiles, int Jobs);

internal interface IBackupService
{
    Task ExportAsync(string path, CancellationToken cancellationToken = default);

    Task<BackupImportResult> ImportAsync(string path, bool confirmed, CancellationToken cancellationToken = default);
}

/// <summary>Writes and reads the backup document from the existing repositories. Job secrets are never read or written.</summary>
internal sealed class BackupService(
    ICategoryRepository categories,
    ICategoryRuleRepository rules,
    IQueueRepository queues,
    IQueueScheduleRepository schedules,
    ISpeedProfileRepository speedProfiles,
    IJobRepository jobs,
    IBrowserSettings browserSettings,
    TimeProvider timeProvider) : IBackupService
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var document = new BackupDocument
        {
            ExportedUtc = timeProvider.GetUtcNow(),
            Browser = browserSettings.Current,
            Categories = await categories.ListAsync(cancellationToken).ConfigureAwait(false),
            Rules = await rules.ListAsync(cancellationToken).ConfigureAwait(false),
            Queues = await queues.ListAsync(cancellationToken).ConfigureAwait(false),
            Schedules = await schedules.ListAsync(cancellationToken).ConfigureAwait(false),
            SpeedProfiles = await speedProfiles.ListAsync(cancellationToken).ConfigureAwait(false),
            Jobs = await jobs.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false),
        };

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(document, _json), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<BackupImportResult> ImportAsync(string path, bool confirmed, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!confirmed)
        {
            return new BackupImportResult(false, 0, 0, 0, 0, 0);
        }

        var document = JsonSerializer.Deserialize<BackupDocument>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), _json)
            ?? throw new InvalidDataException("The backup file is empty.");
        if (document.Version > BackupDocument.CurrentVersion)
        {
            throw new InvalidDataException("This backup was made by a newer Kasi version.");
        }

        if (document.Version < 1)
        {
            throw new InvalidDataException("The backup format is not supported.");
        }

        if (document.Browser is { } browser)
        {
            await browserSettings.SaveAsync(browser.Normalized(), cancellationToken).ConfigureAwait(false);
        }

        var categoryMap = new Dictionary<int, int>();
        foreach (var category in document.Categories)
        {
            var existing = await categories.GetAsync(category.Id, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await categories.UpdateAsync(category with { IsBuiltin = existing.IsBuiltin }, cancellationToken).ConfigureAwait(false);
                categoryMap[category.Id] = existing.Id;
            }
            else
            {
                categoryMap[category.Id] = await categories.AddAsync(category, cancellationToken).ConfigureAwait(false);
            }
        }

        var currentRules = await rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var importedRules = 0;
        foreach (var rule in document.Rules)
        {
            if (!categoryMap.TryGetValue(rule.CategoryId, out var categoryId)
                || currentRules.Any(existing => existing.Kind == rule.Kind && existing.Pattern == rule.Pattern && existing.CategoryId == categoryId))
            {
                continue;
            }

            await rules.AddAsync(rule with { CategoryId = categoryId, IsBuiltin = false }, cancellationToken).ConfigureAwait(false);
            importedRules++;
        }

        var queueMap = new Dictionary<int, int>();
        foreach (var queue in document.Queues)
        {
            var existing = await queues.GetAsync(queue.Id, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await queues.UpdateAsync(queue with { Id = existing.Id }, cancellationToken).ConfigureAwait(false);
                queueMap[queue.Id] = existing.Id;
            }
            else
            {
                queueMap[queue.Id] = await queues.AddAsync(queue, cancellationToken).ConfigureAwait(false);
            }
        }

        var profileMap = new Dictionary<int, int>();
        foreach (var profile in document.SpeedProfiles)
        {
            var existing = (await speedProfiles.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.Name == profile.Name);
            profileMap[profile.Id] = existing?.Id
                ?? await speedProfiles.AddAsync(profile with { Id = 0 }, cancellationToken).ConfigureAwait(false);
        }

        foreach (var schedule in document.Schedules)
        {
            if (!queueMap.TryGetValue(schedule.QueueId, out var queueId))
            {
                continue;
            }

            var profileId = schedule.ProfileId is { } oldProfile && profileMap.TryGetValue(oldProfile, out var mapped) ? mapped : (int?)null;
            await schedules.UpsertAsync(schedule with { QueueId = queueId, ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        }

        var importedJobs = 0;
        foreach (var job in document.Jobs)
        {
            if (await jobs.GetAsync(job.Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            var queueId = queueMap.GetValueOrDefault(job.QueueId, job.QueueId);
            var categoryId = job.CategoryId is { } oldCategory && categoryMap.TryGetValue(oldCategory, out var mapped)
                ? mapped
                : job.CategoryId;
            await jobs.AddAsync(job with { QueueId = queueId, CategoryId = categoryId }, cancellationToken).ConfigureAwait(false);
            importedJobs++;
        }

        return new BackupImportResult(
            true,
            document.Categories.Count,
            importedRules,
            document.Queues.Count,
            document.SpeedProfiles.Count,
            importedJobs);
    }
}
