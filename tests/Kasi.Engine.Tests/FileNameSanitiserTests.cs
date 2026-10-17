namespace Kasi.Engine.Tests;

[Trait("TC", "TC-ENG-005")]
[Trait("Requirement", "KASI-ENG-003")]
public class FileNameSanitiserTests
{
    [Theory]
    [InlineData("../../evil.txt", "evil.txt")]
    [InlineData(@"..\..\evil.txt", "evil.txt")]
    [InlineData(@"..\../evil.txt", "evil.txt")]
    [InlineData("a/b/c/report.pdf", "report.pdf")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", "hosts")]
    [InlineData(@"\\server\share\folder\setup.exe", "setup.exe")]
    [InlineData("C:evil.txt", "evil.txt")]
    [InlineData("d:archive.zip", "archive.zip")]
    public void Strips_directory_paths_and_drive_prefixes_to_the_leaf_name(string input, string expected)
    {
        var result = FileNameSanitiser.Sanitise(input);

        result.Should().Be(expected);
        AssertStaysInTargetFolder(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(". . .")]
    [InlineData("../../")]
    [InlineData(@"..\..\")]
    [InlineData("a/b/..")]
    [InlineData("a/b/.")]
    [InlineData(@"C:\")]
    [InlineData("C:")]
    [InlineData("\0\r\n\t")]
    [InlineData("\u202E\u2066")]
    public void Empty_dot_only_or_slash_terminated_inputs_fall_back_to_default(string? input)
    {
        var result = FileNameSanitiser.Sanitise(input);

        result.Should().Be(FileNameSanitiser.DefaultFileName);
        AssertStaysInTargetFolder(result);
    }

    [Theory]
    [InlineData("a<b>c:d|e?f*.txt", "a_b_c_d_e_f_.txt")]
    [InlineData("a \"quoted\" name.txt", "a _quoted_ name.txt")]
    [InlineData("file:stream:$DATA", "file_stream_$DATA")]
    [InlineData("a;b.txt", "a;b.txt")]
    [InlineData("€ rates.txt", "€ rates.txt")]
    [InlineData(".gitignore", ".gitignore")]
    public void Replaces_windows_invalid_characters_and_keeps_valid_unicode_and_semicolons(string input, string expected)
    {
        var result = FileNameSanitiser.Sanitise(input);

        result.Should().Be(expected);
        AssertStaysInTargetFolder(result);
    }

    [Theory]
    [InlineData("re\0port\r\n\t\x1F\x7F\u0080\u009F.txt", "report.txt")]
    [InlineData("invoice\u202Efdp.exe", "invoicefdp.exe")]
    [InlineData("\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069\u200E\u200F\u061Cdoc.pdf", "doc.pdf")]
    [InlineData("family-\uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67.png", "family-\uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67.png")]
    public void Strips_control_and_bidirectional_characters_while_preserving_emoji_joiners(string input, string expected)
    {
        var result = FileNameSanitiser.Sanitise(input);

        result.Should().Be(expected);
        AssertStaysInTargetFolder(result);
    }

    [Theory]
    [InlineData("report.txt. . ", "report.txt")]
    [InlineData("  report.txt...", "report.txt")]
    [InlineData("   spaced name.zip   ", "spaced name.zip")]
    public void Trims_leading_whitespace_and_trailing_dots_and_spaces(string input, string expected)
    {
        var result = FileNameSanitiser.Sanitise(input);

        result.Should().Be(expected);
        AssertStaysInTargetFolder(result);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("CON.tar.gz", "_CON.tar.gz")]
    [InlineData("PRN", "_PRN")]
    [InlineData("AUX.log", "_AUX.log")]
    [InlineData("NUL", "_NUL")]
    [InlineData("NUL.txt", "_NUL.txt")]
    [InlineData("NUL .txt", "_NUL .txt")]
    [InlineData("CON. .txt", "_CON. .txt")]
    [InlineData("CON . .txt", "_CON . .txt")]
    [InlineData("COM0", "_COM0")]
    [InlineData("COM1.txt", "_COM1.txt")]
    [InlineData("com9.bin", "_com9.bin")]
    [InlineData("LPT0", "_LPT0")]
    [InlineData("LPT1.doc", "_LPT1.doc")]
    [InlineData("lpt9", "_lpt9")]
    [InlineData("COM\u00B9.txt", "_COM\u00B9.txt")]
    [InlineData("COM\u00B2", "_COM\u00B2")]
    [InlineData("COM\u00B3", "_COM\u00B3")]
    [InlineData("LPT\u00B9", "_LPT\u00B9")]
    [InlineData("LPT\u00B2.txt", "_LPT\u00B2.txt")]
    [InlineData("LPT\u00B3", "_LPT\u00B3")]
    [InlineData("CONIN$", "_CONIN$")]
    [InlineData("conout$.txt", "_conout$.txt")]
    public void Maps_windows_reserved_device_names_by_prefixing_an_underscore(string input, string expected)
    {
        var result = FileNameSanitiser.Sanitise(input);

        result.Should().Be(expected);
        AssertStaysInTargetFolder(result);
    }

    [Theory]
    [InlineData("CONSOLE.txt")]
    [InlineData("NULL.txt")]
    [InlineData("COM10.txt")]
    [InlineData("LPT10")]
    [InlineData("_CON.txt")]
    public void Does_not_prefix_names_that_merely_start_with_a_reserved_word(string input)
    {
        FileNameSanitiser.Sanitise(input).Should().Be(input);
    }

    [Fact]
    public void Caps_at_200_characters_while_preserving_the_extension()
    {
        var longName = new string('a', 300) + ".bin";

        var result = FileNameSanitiser.Sanitise(longName);

        result.Length.Should().Be(FileNameSanitiser.MaxLength);
        result.Should().Be(new string('a', 196) + ".bin");
        AssertStaysInTargetFolder(result);
    }

    [Fact]
    public void Truncation_does_not_split_a_utf16_surrogate_pair_and_rechecks_reserved_names()
    {
        // 97 'a's + 50 emoji (100 UTF-16 code units) = 197 chars; cutting at 196 lands inside the 50th emoji.
        var emoji = "\uD83D\uDE00";
        var input = new string('a', 97) + string.Concat(Enumerable.Repeat(emoji, 55)) + ".bin";

        var result = FileNameSanitiser.Sanitise(input);

        result.Length.Should().Be(199);
        result.Should().EndWith(".bin");
        IsWellFormedUtf16(result).Should().BeTrue();
        AssertStaysInTargetFolder(result);

        // An overlong extension (> 32 chars) is truncated directly, and if the remaining stem is reserved it is still mapped.
        var reservedAfterCut = "CON." + new string('x', 250);
        var reservedResult = FileNameSanitiser.Sanitise(reservedAfterCut);
        reservedResult.Length.Should().BeLessThanOrEqualTo(FileNameSanitiser.MaxLength);
        reservedResult.Should().StartWith("_CON.");
    }

    [Fact]
    public void Replaces_unpaired_surrogates_so_output_is_always_well_formed_utf16()
    {
        var loneHigh = "bad\uD800name.txt";
        var loneLow = "bad\uDC00name.txt";

        FileNameSanitiser.Sanitise(loneHigh).Should().Be("bad_name.txt");
        FileNameSanitiser.Sanitise(loneLow).Should().Be("bad_name.txt");
    }

    [Fact]
    public void Bounds_input_at_4_kib_and_uses_sanitised_custom_fallback()
    {
        var atLimit = new string('b', FileNameSanitiser.MaxInputLength - 4) + ".zip";
        var overLimit = new string('b', FileNameSanitiser.MaxInputLength + 1);

        FileNameSanitiser.Sanitise(atLimit).Should().Be(new string('b', 196) + ".zip");
        FileNameSanitiser.Sanitise(overLimit).Should().Be(FileNameSanitiser.DefaultFileName);
        FileNameSanitiser.Sanitise(overLimit, "../CON.txt").Should().Be("_CON.txt");
        FileNameSanitiser.Sanitise(overLimit, "...").Should().Be(FileNameSanitiser.DefaultFileName);
    }

    internal static void AssertStaysInTargetFolder(string fileName)
    {
        fileName.Should().NotBeNullOrWhiteSpace();
        fileName.Length.Should().BeLessThanOrEqualTo(FileNameSanitiser.MaxLength);
        Path.GetFileName(fileName).Should().Be(fileName);

        var targetDir = Path.GetFullPath(@"C:\Users\test\Downloads\KasiTarget");
        var combined = Path.GetFullPath(Path.Combine(targetDir, fileName));
        Path.GetDirectoryName(combined).Should().Be(targetDir);
    }

    internal static bool IsWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }
}
