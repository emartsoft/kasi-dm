using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Kasi.App.Controls;

/// <summary>A small coloured pill with a short text: a state ("Downloading", "Completed") or a kind of file (KASI-UI-001 redesign).</summary>
internal class StatusChip : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<StatusChip, string?>(nameof(Text));

    public static readonly StyledProperty<ChipKind> KindProperty = AvaloniaProperty.Register<StatusChip, ChipKind>(nameof(Kind));

    static StatusChip() => KindProperty.Changed.AddClassHandler<StatusChip>((chip, _) => chip.UpdatePseudoClasses());

    public StatusChip() => UpdatePseudoClasses();

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ChipKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private void UpdatePseudoClasses()
    {
        PseudoClasses.Set(":neutral", Kind == ChipKind.Neutral);
        PseudoClasses.Set(":accent", Kind == ChipKind.Accent);
        PseudoClasses.Set(":info", Kind == ChipKind.Info);
        PseudoClasses.Set(":success", Kind == ChipKind.Success);
        PseudoClasses.Set(":warning", Kind == ChipKind.Warning);
        PseudoClasses.Set(":danger", Kind == ChipKind.Danger);
    }
}

/// <summary>The square chip in front of a file name (ISO, MSI, EXE, ZIP): its label and colour come from the file name or extension.</summary>
internal sealed class FileTypeChip : StatusChip
{
    public static readonly StyledProperty<string?> FileNameProperty = AvaloniaProperty.Register<FileTypeChip, string?>(nameof(FileName));

    static FileTypeChip() => FileNameProperty.Changed.AddClassHandler<FileTypeChip>((chip, _) => chip.Apply());

    public string? FileName
    {
        get => GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    private void Apply()
    {
        Text = FileTypeColors.LabelFor(FileName);
        Kind = FileTypeColors.For(FileName);
    }
}
