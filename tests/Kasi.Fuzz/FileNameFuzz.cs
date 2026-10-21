using System.Text;
using Kasi.Engine;

namespace Kasi.Fuzz;

/// <summary>A filename the parser or the sanitiser produced that breaks a rule of KASI-ENG-003 (TC-ENG-004, TC-ENG-005).</summary>
public sealed class FileNameInvariantException(string message) : Exception(message);

/// <summary>
/// The fuzz target for the <c>Content-Disposition</c> and URL filename parser (TPL 7.2) and the one place its invariants live:
/// the SharpFuzz harness, the unit tests and the regression corpus all call <see cref="Run"/>. An input is the header, a newline and
/// the URL; with no newline the same text is used for both. Any exception (including a violated invariant) is a finding.
/// </summary>
public static class FileNameFuzz
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        var split = data.IndexOf((byte)'\n');
        var header = split >= 0 ? Encoding.Latin1.GetString(data[..split]) : Encoding.Latin1.GetString(data);
        var url = split >= 0 ? Encoding.UTF8.GetString(data[(split + 1)..]) : header;
        Check(header, url);
    }

    public static void Check(string? header, string? url)
    {
        _ = ContentDispositionParser.TryGetFileName(header, out _);
        CheckName(FileNameSanitiser.Sanitise(header), "Sanitise(header)");
        CheckName(DownloadFileName.Resolve(url, header), "Resolve(url, header)");
    }

    /// <summary>
    /// A safe filename is 1 to 200 characters, well-formed UTF-16, with no control, bidi-override or reserved character, no leading or
    /// trailing space, no trailing dot, and it stays in the folder it is combined with.
    /// </summary>
    public static void CheckName(string fileName, string produced)
    {
        void Fail(string rule) => throw new FileNameInvariantException($"{produced} returned a name that {rule} (length {fileName?.Length ?? -1}).");

        if (string.IsNullOrWhiteSpace(fileName))
        {
            Fail("is empty");
        }

        if (fileName.Length > FileNameSanitiser.MaxLength)
        {
            Fail("is too long");
        }

        if (!IsWellFormedUtf16(fileName))
        {
            Fail("has an unpaired surrogate");
        }

        if (fileName[^1] == '.' || char.IsWhiteSpace(fileName[0]) || char.IsWhiteSpace(fileName[^1]))
        {
            Fail("ends with a dot or starts or ends with a space");
        }

        foreach (var c in fileName)
        {
            if (char.IsControl(c))
            {
                Fail("contains a control character");
            }

            if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '/' or '\\')
            {
                Fail("contains a reserved or separator character");
            }

            if (c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') or '‎' or '‏' or '؜')
            {
                Fail("contains a bidirectional control character");
            }
        }

        if (Path.GetFileName(fileName) != fileName)
        {
            Fail("is not a bare file name");
        }

        var target = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KasiTarget"));
        if (Path.GetDirectoryName(Path.GetFullPath(Path.Combine(target, fileName))) != target)
        {
            Fail("escapes the target folder");
        }
    }

    public static bool IsWellFormedUtf16(string value)
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
