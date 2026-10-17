using System.Text;

namespace Kasi.Engine.Tests;

[Trait("TC", "TC-ENG-004")]
[Trait("TC", "TC-ENG-005")]
[Trait("Requirement", "KASI-ENG-003")]
public class FileNameFuzzTests
{
    private static readonly string[] _tokens =
    [
        "attachment", "inline", ";", "=", "\"", "'", "\\", "/", "..", "../", @"..\",
        "filename", "filename*", "UTF-8''", "iso-8859-1'en'", "SHIFT_JIS''",
        "%E2%82%AC", "%2F", "%5C", "%00", "%ZZ", "%FF", "%",
        "CON", "NUL", "PRN", "AUX", "COM1", "LPT9", "COM\u00B9", "LPT\u00B2", "CONIN$",
        ".txt", ".bin", ". . ", "...", "<", ">", ":", "|", "?", "*",
        "\0", "\r", "\n", "\t", "\u007F", "\u009F", "\u202E", "\u2066", "\u200E", "\u061C",
        "\uD83D\uDE00", "\uD800", "\uDC00", "C:", @"C:\Windows\System32\",
    ];

    /// <summary>
    /// Reusable fuzz entry point for <c>SharpFuzz</c> (TPL 7.2, wired into nightly CI in M1.16). Splits raw bytes
    /// into a header and URL pair and verifies all filename parser and sanitiser invariants.
    /// </summary>
    internal static void FuzzTarget(ReadOnlySpan<byte> data)
    {
        var split = data.IndexOf((byte)'\n');
        var header = split >= 0
            ? Encoding.Latin1.GetString(data[..split])
            : Encoding.Latin1.GetString(data);
        var url = split >= 0
            ? Encoding.UTF8.GetString(data[(split + 1)..])
            : header;

        VerifyInvariants(header, url, context: "FuzzTarget(ReadOnlySpan<byte>)");
    }

    [Fact]
    public void Seed_corpus_survives_and_satisfies_all_filename_invariants()
    {
        var seeds = LoadSeedCorpus();
        seeds.Should().NotBeEmpty();

        foreach (var seed in seeds)
        {
            VerifyInvariants(seed, seed, context: $"seed: {seed}");
            FuzzTarget(Encoding.UTF8.GetBytes(seed + "\n" + seed));
        }
    }

    [Theory]
    [InlineData(20261017)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(999999)]
    public void Seeded_mutated_inputs_never_throw_and_always_produce_safe_filenames(int seed)
    {
        var seeds = LoadSeedCorpus();
        var rng = new Random(seed);
        const int iterations = 1_500;

        for (var i = 0; i < iterations; i++)
        {
            var header = MutateInput(rng, seeds);
            var url = MutateInput(rng, seeds);
            VerifyInvariants(header, url, context: $"seed={seed}, iteration={i}");
        }
    }

    private static void VerifyInvariants(string? header, string? url, string context)
    {
        _ = ContentDispositionParser.TryGetFileName(header, out _);

        var sanitised = FileNameSanitiser.Sanitise(header);
        AssertSafeFileName(sanitised, context);

        var resolved = DownloadFileName.Resolve(url, header);
        AssertSafeFileName(resolved, context);
    }

    private static void AssertSafeFileName(string fileName, string context)
    {
        fileName.Length.Should().BeInRange(1, FileNameSanitiser.MaxLength, context);
        FileNameSanitiserTests.IsWellFormedUtf16(fileName).Should().BeTrue(context);
        fileName[^1].Should().NotBe('.', context);
        char.IsWhiteSpace(fileName[0]).Should().BeFalse(context);
        char.IsWhiteSpace(fileName[^1]).Should().BeFalse(context);

        foreach (var c in fileName)
        {
            char.IsControl(c).Should().BeFalse(context);
            (c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '/' or '\\').Should().BeFalse(context);
            (c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200E' or '\u200F' or '\u061C')
                .Should().BeFalse(context);
        }

        FileNameSanitiserTests.AssertStaysInTargetFolder(fileName);
    }

    private static string MutateInput(Random rng, IReadOnlyList<string> seeds)
    {
        var mode = rng.Next(4);
        if (mode == 0)
        {
            // Token soup of RFC 6266, traversal, reserved names, bidi and surrogate edge cases.
            var count = rng.Next(1, 16);
            var sb = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                sb.Append(_tokens[rng.Next(_tokens.Length)]);
            }

            return sb.ToString();
        }

        if (mode == 1)
        {
            // Mutate a corpus seed with random splices, deletions and byte flips.
            var baseSeed = seeds[rng.Next(seeds.Count)];
            var chars = baseSeed.ToCharArray();
            var flips = rng.Next(1, 6);
            for (var i = 0; i < flips && chars.Length > 0; i++)
            {
                chars[rng.Next(chars.Length)] = (char)rng.Next(0, 0xFFFF);
            }

            return new string(chars);
        }

        if (mode == 2)
        {
            // Boundary lengths around 200, 4 KiB and 16 KiB.
            int[] lengths = [0, 1, 199, 200, 201, 300, 4095, 4096, 4097, 16384, 16385];
            var len = lengths[rng.Next(lengths.Length)];
            var fill = (char)('a' + rng.Next(26));
            return "attachment; filename=\"" + new string(fill, len) + ".bin\"";
        }

        // Completely arbitrary UTF-16 code units (including unpaired surrogates and control characters).
        var rawLen = rng.Next(0, 256);
        var buffer = new char[rawLen];
        for (var i = 0; i < rawLen; i++)
        {
            buffer[i] = (char)rng.Next(0, 0xFFFF);
        }

        return new string(buffer);
    }

    private static IReadOnlyList<string> LoadSeedCorpus()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kasi.slnx")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("Kasi.slnx not found."),
            "tests",
            "fixtures",
            "disposition-corpus",
            "seeds.txt");

        return [.. File.ReadAllLines(path).Where(l => l.Length > 0)];
    }
}
