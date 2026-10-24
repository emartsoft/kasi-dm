using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FluentAvalonia.Styling;
using Kasi.App.Controls;

namespace Kasi.App.Tests;

/// <summary>UIR-0 (docs/plans/ui-redesign.md): the design tokens, the shared controls and what they promise. KASI-UI-006, KASI-UI-010.</summary>
[Trait("Requirement", "KASI-UI-006")]
public class DesignSystemTests
{
    private static readonly string[] _colorKeys =
    [
        "KasiBaseColor", "KasiMicaColor", "KasiCardColor", "KasiCardHoverColor", "KasiBorderColor", "KasiSubtleColor", "KasiAccentColor",
        "KasiAccentHoverColor", "KasiAccentTextColor", "KasiTextPrimaryColor", "KasiTextSecondaryColor", "KasiSuccessColor", "KasiWarningColor",
        "KasiDangerColor", "KasiInfoColor",
    ];

    private static Color Colour(string key, ThemeVariant variant)
    {
        Application.Current!.TryGetResource(key, variant, out var value).Should().BeTrue($"{key} must exist for {variant}");
        return (Color)value!;
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double Contrast(Color a, Color b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    public static TheoryData<string> Variants => ["Dark", "Light"];

    private static ThemeVariant Variant(string name) => name == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void Every_token_exists_in_both_themes(string name)
    {
        foreach (var key in _colorKeys)
        {
            Colour(key, Variant(name));
        }

        foreach (var key in new[] { "KasiCardCornerRadius", "KasiControlCornerRadius", "KasiChipCornerRadius", "KasiSpace1", "KasiSpace5", "KasiTextFont", "KasiMonoFont", "KasiIconFont" })
        {
            Application.Current!.TryGetResource(key, Variant(name), out var value).Should().BeTrue(key);
            value.Should().NotBeNull(key);
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void Text_and_the_state_colours_can_be_read_on_a_card_in_both_themes(string name)
    {
        var card = Colour("KasiCardColor", Variant(name));
        var baseColor = Colour("KasiBaseColor", Variant(name));

        foreach (var key in new[] { "KasiTextPrimaryColor", "KasiTextSecondaryColor", "KasiAccentTextColor", "KasiSuccessColor", "KasiWarningColor", "KasiDangerColor", "KasiInfoColor" })
        {
            Contrast(Colour(key, Variant(name)), card).Should().BeGreaterThanOrEqualTo(4.5, $"{key} on a card in {name}");
        }

        Contrast(Colour("KasiTextPrimaryColor", Variant(name)), baseColor).Should().BeGreaterThanOrEqualTo(4.5);
        Contrast(Colour("KasiTextSecondaryColor", Variant(name)), baseColor).Should().BeGreaterThanOrEqualTo(4.5);
        Contrast(Colour("KasiAccentColor", Variant(name)), card).Should().BeGreaterThanOrEqualTo(3.0, "the accent as a button or a bar must stand out from the card");
    }

    [AvaloniaFact]
    public void The_accent_is_the_designs_indigo_whatever_accent_windows_has()
    {
        var theme = Application.Current!.Styles.OfType<FluentAvaloniaTheme>().Single();

        theme.CustomAccentColor.Should().Be(Color.Parse("#6366f1"));
        theme.PreferUserAccentColor.Should().BeFalse();
        Colour("KasiAccentColor", ThemeVariant.Dark).Should().Be(Color.Parse("#6366f1"));
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void The_brushes_follow_the_theme_they_are_asked_in(string name)
    {
        var window = new Window { Content = new Border { Classes = { "kasi-card" } }, RequestedThemeVariant = Variant(name) };
        window.Show();

        var border = (Border)window.Content!;

        ((ISolidColorBrush)border.Background!).Color.Should().Be(Colour("KasiCardColor", Variant(name)));
        ((ISolidColorBrush)border.BorderBrush!).Color.Should().Be(Colour("KasiBorderColor", Variant(name)));
        border.CornerRadius.Should().Be(new CornerRadius(8));
        window.Close();
    }

    [AvaloniaFact]
    public void A_chip_takes_the_colour_of_its_kind()
    {
        var window = new Window { RequestedThemeVariant = ThemeVariant.Dark };
        var chip = new StatusChip { Text = "Downloading", Kind = ChipKind.Success };
        window.Content = chip;
        window.Show();
        window.UpdateLayout();

        chip.Classes.Should().Contain(":success");
        ((ISolidColorBrush)chip.Foreground!).Color.Should().Be(Colour("KasiSuccessColor", ThemeVariant.Dark));
        chip.Kind = ChipKind.Danger;
        chip.Classes.Should().Contain(":danger").And.NotContain(":success");
        chip.GetVisualDescendants().OfType<TextBlock>().Single().Text.Should().Be("Downloading");
        window.Close();
    }

    [AvaloniaFact]
    public void A_file_type_chip_reads_its_label_and_colour_from_the_file_name()
    {
        var chip = new FileTypeChip { FileName = "ubuntu-24.04.iso" };

        chip.Text.Should().Be("ISO");
        chip.Kind.Should().Be(ChipKind.Warning);
        chip.FileName = "movie.mkv";
        chip.Text.Should().Be("MKV");
        chip.Kind.Should().Be(ChipKind.Danger);
        chip.FileName = "README";
        chip.Text.Should().Be("FILE");
        chip.Kind.Should().Be(ChipKind.Neutral);
    }

    [AvaloniaFact]
    public void A_toggle_row_shows_its_title_and_description_and_its_switch_carries_the_title_as_its_name()
    {
        var window = new Window();
        var row = new ToggleRow { Title = "Take downloads from the browser", Description = "Automatically catch downloads.", IsChecked = true };
        window.Content = row;
        window.Show();
        window.UpdateLayout();

        var texts = row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        texts.Should().Contain(["Take downloads from the browser", "Automatically catch downloads."]);
        var toggle = row.GetVisualDescendants().OfType<ToggleSwitch>().Single();
        AutomationProperties.GetName(toggle).Should().Be("Take downloads from the browser");
        toggle.IsChecked.Should().BeTrue();
        toggle.IsChecked = false;
        row.IsChecked.Should().BeFalse("the switch and the row are one value");
        window.Close();
    }

    [Fact]
    public void A_file_type_has_a_label_and_a_colour_family()
    {
        FileTypeColors.LabelFor("a.tar.gz").Should().Be("GZ");
        FileTypeColors.LabelFor("a.docx").Should().Be("DOCX");
        FileTypeColors.LabelFor("a.sevenzip").Should().Be("SEVE", "four letters at most");
        FileTypeColors.LabelFor("zip").Should().Be("FILE", "a name with no dot has no extension");
        FileTypeColors.LabelFor(".gitignore").Should().Be("FILE");
        FileTypeColors.LabelFor(null).Should().Be("FILE");
        FileTypeColors.For("setup.EXE").Should().Be(ChipKind.Success);
        FileTypeColors.For("disk.iso").Should().Be(ChipKind.Warning);
        FileTypeColors.For("pack.msi").Should().Be(ChipKind.Info);
        FileTypeColors.For("pack.zip").Should().Be(ChipKind.Accent);
        FileTypeColors.For("film.mp4").Should().Be(ChipKind.Danger);
        FileTypeColors.For("notes.xyz").Should().Be(ChipKind.Neutral);
        FileTypeColors.For("").Should().Be(ChipKind.Neutral);
    }

    [Fact]
    public void The_icons_are_distinct_single_private_use_code_points()
    {
        Icons.All.Should().OnlyHaveUniqueItems();
        Icons.All.Should().OnlyContain(g => g.Length == 1 && g[0] >= '' && g[0] <= '');
    }

    [Fact]
    public void A_young_history_grows_in_from_the_right_and_a_full_one_fills_the_width()
    {
        var young = SparklineGeometry.Points([0, 5, 10], width: 100, height: 40, capacity: 5, maximum: 10);

        young.Select(p => (p.X, p.Y)).Should().Equal((50.0, 40.0), (75.0, 20.0), (100.0, 0.0));
        var full = SparklineGeometry.Points([1, 2, 3, 4, 5], 100, 40, capacity: 5, maximum: 10);
        full[0].X.Should().Be(0);
        full[^1].X.Should().Be(100);
    }

    [Fact]
    public void The_oldest_samples_drop_off_the_left_and_the_scale_follows_the_largest_when_no_maximum_is_given()
    {
        var points = SparklineGeometry.Points([100, 1, 2, 3], 30, 10, capacity: 3);

        points.Should().HaveCount(3, "only the last three fit");
        points.Select(p => Math.Round(p.Y, 3)).Should().Equal(Math.Round(10 - (1 / 3.0 * 10), 3), Math.Round(10 - (2 / 3.0 * 10), 3), 0.0);
    }

    [Fact]
    public void Nothing_and_a_flat_zero_line_do_not_break_the_chart()
    {
        SparklineGeometry.Points([], 100, 40, 60).Should().BeEmpty();
        SparklineGeometry.Points([1, 2], 0, 40, 60).Should().BeEmpty();
        SparklineGeometry.Points([0, 0, 0], 60, 20, 3).Should().OnlyContain(p => p.Y == 20, "zero sits on the floor");
        SparklineGeometry.Points([5, 50], 10, 10, 2, maximum: 10).Select(p => p.Y).Should().Equal(5.0, 0.0);
    }
}
