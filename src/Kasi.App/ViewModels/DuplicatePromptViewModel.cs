using System.Globalization;
using System.Text;
using Kasi.App.Resources;
using Kasi.Engine;

namespace Kasi.App.ViewModels;

internal sealed record DuplicateChoice(DuplicateAction Action, string Label);

/// <summary>
/// The duplicate prompt (KASI-ENG-013): what the engine offered, as buttons. Shows the file name the engine would use for a rename, never
/// a URL or a folder path.
/// </summary>
internal sealed class DuplicatePromptViewModel
{
    private static readonly CompositeFormat _renameFormat = CompositeFormat.Parse(Strings.DuplicateRename);

    public DuplicatePromptViewModel(DuplicateCheckResult check)
    {
        ArgumentNullException.ThrowIfNull(check);
        Choices =
        [
            .. check.AvailableActions.Select(action => new DuplicateChoice(action, action switch
            {
                DuplicateAction.Resume => Strings.DuplicateResume,
                DuplicateAction.Overwrite => Strings.DuplicateOverwrite,
                _ => string.Format(CultureInfo.CurrentCulture, _renameFormat, check.SuggestedFileName),
            })),
        ];
    }

    public IReadOnlyList<DuplicateChoice> Choices { get; }

    public string Title => Strings.DuplicateTitle;

    public string Message => Strings.DuplicateMessage;

    public string CancelLabel => Strings.AddCancelButton;

    /// <summary>The user's pick; null while open or when cancelled.</summary>
    public DuplicateAction? Chosen { get; private set; }

    public event Action? Closed;

    public void Choose(DuplicateAction action)
    {
        Chosen = action;
        Closed?.Invoke();
    }

    public void Cancel() => Closed?.Invoke();
}
