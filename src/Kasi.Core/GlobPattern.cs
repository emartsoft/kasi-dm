namespace Kasi.Core;

/// <summary>Why a category rule's pattern was refused.</summary>
public enum CategoryRulePatternError
{
    Empty,
    TooLong,
    MatchesEverything,
    BadExtension,
    BadCharacters,
}

/// <summary>
/// The patterns of category rules (TRD 10.4): <c>*</c> matches any run of characters, <c>?</c> one character, everything else itself, ignoring
/// case. Matched with two cursors, so a pattern cannot make a match take more than the pattern's length times the text's (no backtracking
/// engine, nothing a hostile pattern can blow up).
/// </summary>
public static class GlobPattern
{
    public const int MaxLength = 256;

    public static bool IsMatch(string pattern, string text)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(text);

        int p = 0, t = 0, star = -1, resume = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++resume;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>Null when the pattern may be saved for this kind of rule, otherwise the reason it may not.</summary>
    public static CategoryRulePatternError? Validate(CategoryRuleKind kind, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return CategoryRulePatternError.Empty;
        }

        if (pattern.Length > MaxLength)
        {
            return CategoryRulePatternError.TooLong;
        }

        if (pattern.All(c => c == '*'))
        {
            return CategoryRulePatternError.MatchesEverything;
        }

        if (pattern.Any(char.IsControl) || pattern.Any(char.IsWhiteSpace))
        {
            return CategoryRulePatternError.BadCharacters;
        }

        if (kind == CategoryRuleKind.Extension)
        {
            // an extension is letters, digits and inner dots, without the leading dot a person might type
            return pattern.StartsWith('.') || pattern.EndsWith('.') || pattern.Contains("..", StringComparison.Ordinal) || !pattern.All(c => char.IsLetterOrDigit(c) || c == '.')
                ? CategoryRulePatternError.BadExtension
                : null;
        }

        return null;
    }
}
