namespace Kasi.App.ViewModels;

/// <summary>
/// The view model is built before any window exists, so it asks this stand-in; the app points <see cref="Inner"/> at the real window host
/// once it is up. With nothing behind it a delete is refused (null: cancelled) rather than done without asking.
/// </summary>
internal sealed class ConfirmHostProxy : IConfirmHost
{
    public IConfirmHost? Inner { get; set; }

    public Task<DeleteChoice?> ConfirmDeleteAsync(ConfirmDeleteViewModel dialog) =>
        Inner is { } host ? host.ConfirmDeleteAsync(dialog) : Task.FromResult<DeleteChoice?>(null);
}
