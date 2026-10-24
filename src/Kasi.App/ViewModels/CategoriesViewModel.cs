using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kasi.App.Engine;
using Kasi.App.Resources;
using Kasi.Core;
using Kasi.Engine;

namespace Kasi.App.ViewModels;

internal sealed record RuleKindChoice(CategoryRuleKind Kind, string Label);

/// <summary>One line of the rules list: a rule, what it is matched against, and whether it is on.</summary>
internal sealed partial class RuleItem(CategoryRule rule, string kindLabel) : ObservableObject
{
    public CategoryRule Rule { get; private set; } = rule;

    public string KindLabel { get; } = kindLabel;

    public string Pattern => Rule.Pattern;

    public string Origin => Rule.IsBuiltin ? Strings.CategoryBuiltinMark : Strings.RuleOriginYours;

    public string Summary => string.Format(CultureInfo.CurrentCulture, "{0}: {1} ({2})", KindLabel, Pattern, Origin);

    [ObservableProperty]
    private bool _isEnabled = rule.IsEnabled;

    /// <summary>Raised when the person switches the rule on or off; the page saves it at once.</summary>
    public event Action<RuleItem>? EnabledChanged;

    partial void OnIsEnabledChanged(bool value)
    {
        Rule = Rule with { IsEnabled = value };
        EnabledChanged?.Invoke(this);
    }
}

/// <summary>
/// The Categories page of Settings (KASI-CAT-002, plan M2.11c): the categories and the rules that route a download to one. Every change is
/// saved at once, which is why this page has no Save button. A "try a link" box shows the category, the folder and the rule that would decide.
/// </summary>
internal sealed partial class CategoriesViewModel : ObservableObject
{
    private const int PositionStep = 10;

    private static readonly CompositeFormat _becauseRule = CompositeFormat.Parse(Strings.TryBecauseRule);
    private static readonly CompositeFormat _resultFormat = CompositeFormat.Parse(Strings.TryResultFormat);

    private readonly ICategoryRepository _categories;
    private readonly ICategoryRuleRepository _rules;
    private readonly string _downloadsFolder;
    private IReadOnlyList<CategoryRule> _allRules = [];
    private bool _loading;

    [ObservableProperty]
    private Category? _selectedCategory;

    [ObservableProperty]
    private RuleItem? _selectedRule;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _editFolder = string.Empty;

    [ObservableProperty]
    private string _newCategoryName = string.Empty;

    [ObservableProperty]
    private string _newCategoryFolder = string.Empty;

    [ObservableProperty]
    private RuleKindChoice _selectedRuleKind;

    [ObservableProperty]
    private string _newRulePattern = string.Empty;

    [ObservableProperty]
    private string _tryLink = string.Empty;

    [ObservableProperty]
    private string _tryResult = string.Empty;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    public CategoriesViewModel(ICategoryRepository categories, ICategoryRuleRepository rules, string downloadsFolder)
    {
        _categories = categories;
        _rules = rules;
        _downloadsFolder = downloadsFolder;
        RuleKinds =
        [
            new(CategoryRuleKind.Extension, Strings.RuleKindExtension),
            new(CategoryRuleKind.Domain, Strings.RuleKindDomain),
            new(CategoryRuleKind.Url, Strings.RuleKindUrl),
        ];
        _selectedRuleKind = RuleKinds[0];
    }

    public ObservableCollection<Category> Categories { get; } = [];

    public ObservableCollection<RuleItem> Rules { get; } = [];

    public IReadOnlyList<RuleKindChoice> RuleKinds { get; }

    /// <summary>A built-in category keeps its name and cannot be deleted; only its folder and its rules change.</summary>
    public bool CanRename => SelectedCategory is { IsBuiltin: false };

    public bool CanDelete => SelectedCategory is { IsBuiltin: false };

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var keep = SelectedCategory?.Id;
        _loading = true;
        try
        {
            Categories.Clear();
            foreach (var category in await _categories.ListAsync(cancellationToken).ConfigureAwait(true))
            {
                Categories.Add(category);
            }

            _allRules = await _rules.ListAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _loading = false;
        }

        SelectedCategory = Categories.FirstOrDefault(c => c.Id == keep) ?? Categories.FirstOrDefault();
        ShowRules();
    }

    partial void OnSelectedCategoryChanged(Category? value)
    {
        EditName = value?.Name ?? string.Empty;
        EditFolder = value?.Folder ?? string.Empty;
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanDelete));
        if (!_loading)
        {
            ShowRules();
        }
    }

    [RelayCommand]
    private async Task AddCategoryAsync()
    {
        var name = NewCategoryName.Trim();
        var folder = NewCategoryFolder.Trim();
        if (Check(CategoryInput.ValidateName(name, Categories, null), CategoryInput.ValidateFolder(folder, builtin: false)) is not null)
        {
            return;
        }

        var id = await _categories.AddAsync(new Category { Id = 0, Name = name, Folder = folder }, CancellationToken.None).ConfigureAwait(true);
        NewCategoryName = string.Empty;
        NewCategoryFolder = string.Empty;
        await ReloadAsync(id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveCategoryAsync()
    {
        if (SelectedCategory is not { } category)
        {
            return;
        }

        var name = category.IsBuiltin ? category.Name : EditName.Trim();
        var folder = EditFolder.Trim();
        if (Check(category.IsBuiltin ? null : CategoryInput.ValidateName(name, Categories, category.Id), CategoryInput.ValidateFolder(folder, category.IsBuiltin, allowEmpty: category.IsBuiltin)) is not null)
        {
            return;
        }

        await _categories.UpdateAsync(category with { Name = name, Folder = folder }, CancellationToken.None).ConfigureAwait(true);
        await ReloadAsync(category.Id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteCategoryAsync()
    {
        if (SelectedCategory is not { IsBuiltin: false } category)
        {
            return;
        }

        await _categories.DeleteAsync(category.Id, CancellationToken.None).ConfigureAwait(true);
        SelectedCategory = null;
        await ReloadAsync(null).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        if (SelectedCategory is not { } category)
        {
            return;
        }

        var kind = SelectedRuleKind.Kind;
        var pattern = NewRulePattern.Trim();
        if (Check(CategoryInput.ValidatePattern(kind, pattern)) is not null)
        {
            return;
        }

        var position = _allRules.Where(r => r.Kind == kind).Select(r => r.Position).DefaultIfEmpty(0).Max() + PositionStep;
        await _rules.AddAsync(new CategoryRule { CategoryId = category.Id, Kind = kind, Pattern = pattern, Position = position }, CancellationToken.None).ConfigureAwait(true);
        NewRulePattern = string.Empty;
        await ReloadAsync(category.Id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule is not { Rule.IsBuiltin: false } item)
        {
            return;
        }

        await _rules.DeleteAsync(item.Rule.Id, CancellationToken.None).ConfigureAwait(true);
        await ReloadAsync(SelectedCategory?.Id).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task MoveRuleUpAsync() => MoveRuleAsync(-1);

    [RelayCommand]
    private Task MoveRuleDownAsync() => MoveRuleAsync(1);

    [RelayCommand]
    private void CheckLink()
    {
        if (!Uri.TryCreate(TryLink.Trim(), UriKind.Absolute, out var uri))
        {
            TryResult = Strings.TryNoLink;
            return;
        }

        var match = CategoryResolver.Resolve(uri, null, null, _allRules, [.. Categories]);
        if (match is null)
        {
            TryResult = Strings.TryNoLink;
            return;
        }

        var folder = CategoryFolder.For(_downloadsFolder, match.Category);
        var why = match.Rule is { } rule
            ? string.Format(CultureInfo.CurrentCulture, _becauseRule, RuleKinds.First(k => k.Kind == rule.Kind).Label, rule.Pattern)
            : Strings.TryBecauseNothing;
        TryResult = string.Format(CultureInfo.CurrentCulture, _resultFormat, match.Category.Name, folder, why);
    }

    private async Task MoveRuleAsync(int direction)
    {
        if (SelectedRule is not { } item)
        {
            return;
        }

        var shown = Rules.ToList();
        var index = shown.IndexOf(item);
        var neighbour = direction < 0
            ? shown.Take(index).LastOrDefault(r => r.Rule.Kind == item.Rule.Kind)
            : shown.Skip(index + 1).FirstOrDefault(r => r.Rule.Kind == item.Rule.Kind);
        if (neighbour is null)
        {
            return;
        }

        // swap the two positions; equal positions are told apart by one step in the direction of the move
        var (mine, theirs) = (item.Rule.Position, neighbour.Rule.Position);
        if (mine == theirs)
        {
            theirs = direction < 0 ? mine + 1 : mine - 1;
        }

        await _rules.UpdateAsync(item.Rule with { Position = theirs }, CancellationToken.None).ConfigureAwait(true);
        await _rules.UpdateAsync(neighbour.Rule with { Position = mine }, CancellationToken.None).ConfigureAwait(true);
        var keep = item.Rule.Id;
        await ReloadAsync(SelectedCategory?.Id).ConfigureAwait(true);
        SelectedRule = Rules.FirstOrDefault(r => r.Rule.Id == keep);
    }

    private async Task SaveEnabledAsync(RuleItem item)
    {
        await _rules.UpdateAsync(item.Rule, CancellationToken.None).ConfigureAwait(true);
        _allRules = await _rules.ListAsync(CancellationToken.None).ConfigureAwait(true);
        Status = Strings.CategoriesSaved;
    }

    private async Task ReloadAsync(int? categoryId)
    {
        Error = string.Empty;
        await LoadAsync().ConfigureAwait(true);
        if (categoryId is { } id && Categories.FirstOrDefault(c => c.Id == id) is { } category)
        {
            SelectedCategory = category;
        }

        Status = Strings.CategoriesSaved;
    }

    private void ShowRules()
    {
        foreach (var old in Rules)
        {
            old.EnabledChanged -= OnEnabledChanged;
        }

        Rules.Clear();
        if (SelectedCategory is { } category)
        {
            foreach (var rule in _allRules.Where(r => r.CategoryId == category.Id).OrderBy(r => r.Kind).ThenBy(r => r.Position).ThenBy(r => r.Id))
            {
                var item = new RuleItem(rule, RuleKinds.First(k => k.Kind == rule.Kind).Label);
                item.EnabledChanged += OnEnabledChanged;
                Rules.Add(item);
            }
        }
    }

    private void OnEnabledChanged(RuleItem item) => _ = SaveEnabledAsync(item);

    /// <summary>Shows the first problem and returns it, or clears the message and returns null.</summary>
    private string? Check(params string?[] problems)
    {
        var first = problems.FirstOrDefault(p => p is not null);
        Error = first ?? string.Empty;
        if (first is not null)
        {
            Status = string.Empty;
        }

        return first;
    }

    // labels the page binds to, so the view holds no strings
    public string TabTitle => Strings.CategoriesTab;

    public string Note => Strings.CategoriesNote;

    public string CategoriesLabel => Strings.CategoriesListLabel;

    public string NameLabel => Strings.CategoryNameLabel;

    public string FolderLabel => Strings.CategoryFolderLabel;

    public string AddCategoryLabel => Strings.CategoryAddButton;

    public string SaveCategoryLabel => Strings.CategorySaveButton;

    public string DeleteCategoryLabel => Strings.CategoryDeleteButton;

    public string NewCategoryLabel => Strings.CategoryNewLabel;

    public string NewFolderLabel => Strings.CategoryNewFolderLabel;

    public string RulesLabel => Strings.RulesLabel;

    public string RuleKindLabel => Strings.RuleKindLabel;

    public string RulePatternLabel => Strings.RulePatternLabel;

    public string AddRuleLabel => Strings.RuleAddButton;

    public string DeleteRuleLabel => Strings.RuleDeleteButton;

    public string MoveUpLabel => Strings.RuleMoveUp;

    public string MoveDownLabel => Strings.RuleMoveDown;

    public string RuleOnLabel => Strings.RuleEnabledLabel;

    public string TryLabel => Strings.TryLabel;

    public string TryButtonLabel => Strings.TryButton;
}

/// <summary>What may be typed on the Categories page; every message is a resource string.</summary>
internal static class CategoryInput
{
    private const int NameMaxLength = 64;
    private const int FolderMaxLength = 200;

    public static string? ValidateName(string name, IEnumerable<Category> existing, int? ignoreId)
    {
        if (name.Length is 0 or > NameMaxLength || name.Any(char.IsControl))
        {
            return Strings.CategoryErrorName;
        }

        return existing.Any(c => c.Id != ignoreId && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ? Strings.CategoryErrorNameTaken
            : null;
    }

    /// <summary>
    /// A built-in category's folder is a plain path under Downloads. A user category may also take a full path (drive or network share).
    /// Neither may climb out with <c>..</c>, use a name Windows reserves, or contain a character Windows does not allow. Only General has no folder.
    /// </summary>
    public static string? ValidateFolder(string folder, bool builtin, bool allowEmpty = false)
    {
        if (folder.Length == 0)
        {
            return allowEmpty ? null : Strings.CategoryErrorFolder;
        }

        if (folder.Length > FolderMaxLength || folder.Any(char.IsControl) || folder.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
        {
            return Strings.CategoryErrorFolder;
        }

        var rooted = Path.IsPathRooted(folder);
        if (rooted && (builtin || folder.StartsWith('\\') && !folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith('/')))
        {
            return Strings.CategoryErrorFolder;
        }

        var root = rooted ? Path.GetPathRoot(folder) ?? string.Empty : string.Empty;
        var segments = folder[root.Length..].TrimStart('\\', '/').Split(['\\', '/'], StringSplitOptions.None);
        if (rooted && root.Length == 0)
        {
            return Strings.CategoryErrorFolder;
        }

        foreach (var segment in segments.Where((s, i) => !(s.Length == 0 && i == segments.Length - 1)))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment != segment.TrimEnd(' ', '.') || segment.Contains(':', StringComparison.Ordinal)
                || FileNameSanitiser.Sanitise(segment) != segment)
            {
                return Strings.CategoryErrorFolder;
            }
        }

        return null;
    }

    public static string? ValidatePattern(CategoryRuleKind kind, string pattern) => GlobPattern.Validate(kind, pattern) switch
    {
        null => null,
        CategoryRulePatternError.Empty => Strings.RuleErrorEmpty,
        CategoryRulePatternError.TooLong => Strings.RuleErrorTooLong,
        CategoryRulePatternError.MatchesEverything => Strings.RuleErrorEverything,
        CategoryRulePatternError.BadExtension => Strings.RuleErrorExtension,
        _ => Strings.RuleErrorChars,
    };
}
