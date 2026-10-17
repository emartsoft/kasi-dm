namespace Kasi.Core;

/// <summary>When a queue's data quota starts again (<c>queue.quota_reset</c>).</summary>
public enum QuotaReset
{
    Daily,
    Weekly,
    Never,
}
