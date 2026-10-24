namespace Kasi.Core;

/// <summary>What a category rule is matched against (TRD 10.4): the file's extension, the link's host, or the link's address.</summary>
public enum CategoryRuleKind
{
    Extension,
    Domain,
    Url,
}

/// <summary>
/// One row of the <c>category_rule</c> table (TRD 10.4, 11.1, KASI-CAT-001 and 002): if <see cref="Pattern"/> matches, the download belongs to
/// <see cref="CategoryId"/>. Within a kind, a lower <see cref="Position"/> is tried first.
/// </summary>
public sealed record CategoryRule
{
    public int Id { get; init; }

    public required int CategoryId { get; init; }

    public required CategoryRuleKind Kind { get; init; }

    /// <summary>An extension without its dot (<c>mp4</c>, <c>tar.gz</c>), or a glob (<c>*</c> and <c>?</c>) for a host or an address.</summary>
    public required string Pattern { get; init; }

    public int Position { get; init; }

    /// <summary>Shipped with Kasi: it can be switched off but not deleted.</summary>
    public bool IsBuiltin { get; init; }

    public bool IsEnabled { get; init; } = true;
}

public interface ICategoryRuleRepository
{
    /// <summary>All rules, ordered by position and then id.</summary>
    Task<IReadOnlyList<CategoryRule>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a user rule and returns its id. The rule's own <see cref="CategoryRule.Id"/> and <see cref="CategoryRule.IsBuiltin"/> are ignored.</summary>
    Task<int> AddAsync(CategoryRule rule, CancellationToken cancellationToken = default);

    /// <summary>Changes the category, pattern, position and enabled flag of a rule (its kind and origin stay); false if it does not exist.</summary>
    Task<bool> UpdateAsync(CategoryRule rule, CancellationToken cancellationToken = default);

    /// <summary>Deletes a user rule; false if it does not exist or ships with Kasi (switch those off instead).</summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
