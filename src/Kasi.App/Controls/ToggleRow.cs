using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace Kasi.App.Controls;

/// <summary>
/// A setting as a card: a title, a line of description and a switch (the rows of the redesigned Settings window). The switch carries the
/// title as its automation name, so a screen reader says what it changes.
/// </summary>
internal sealed class ToggleRow : TemplatedControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<ToggleRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<ToggleRow, string?>(nameof(Description));

    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<ToggleRow, bool>(nameof(IsChecked), defaultBindingMode: BindingMode.TwoWay);

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }
}
