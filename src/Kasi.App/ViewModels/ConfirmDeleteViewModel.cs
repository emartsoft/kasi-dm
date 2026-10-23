using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kasi.App.Resources;

namespace Kasi.App.ViewModels;

internal sealed record DeleteChoice(bool AlsoDeleteFiles, bool DontAskAgain);

/// <summary>What the delete window asks (KASI-UI interaction rules): the files too? Do not ask again? Null result means cancelled.</summary>
internal sealed partial class ConfirmDeleteViewModel(string message, bool offerFiles) : ObservableObject
{
    [ObservableProperty]
    private bool _alsoDeleteFiles;

    [ObservableProperty]
    private bool _dontAskAgain;

    public string Message { get; } = message;

    /// <summary>"Delete completed" removes list entries only, so it does not offer to delete files.</summary>
    public bool OfferFiles { get; } = offerFiles;

    public DeleteChoice? Result { get; private set; }

    public event Action? Closed;

    public string Title => Strings.DeleteTitle;

    public string AlsoFilesLabel => Strings.DeleteAlsoFiles;

    public string DontAskLabel => Strings.DeleteDontAsk;

    public string DeleteLabel => Strings.DeleteButton;

    public string CancelLabel => Strings.AddCancelButton;

    [RelayCommand]
    private void Confirm()
    {
        Result = new DeleteChoice(OfferFiles && AlsoDeleteFiles, DontAskAgain);
        Closed?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke();
}
