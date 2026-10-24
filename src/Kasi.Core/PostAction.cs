namespace Kasi.Core;

/// <summary>Kinds of actions triggered when a queue finishes (TRD 10.3, KASI-QUE-004).</summary>
public enum PostActionKind
{
    None,
    Sleep,
    Hibernate,
    Shutdown,
    ExitKasi,
    RunProgram,
}

/// <summary>Parsed configuration for a post-completion action.</summary>
public sealed record PostActionConfig
{
    public PostActionKind Kind { get; init; } = PostActionKind.None;

    public bool ForceIfOtherQueuesBusy { get; init; }

    public string? ProgramPath { get; init; }

    public string? Arguments { get; init; }
}

public static class PostActionParser
{
    public static PostActionConfig Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new PostActionConfig { Kind = PostActionKind.None };
        }

        var parts = text.Split(':', 3, StringSplitOptions.TrimEntries);
        var action = parts[0].ToLowerInvariant();
        bool force = parts.Length > 1 && string.Equals(parts[1], "force", StringComparison.OrdinalIgnoreCase);

        return action switch
        {
            "sleep" => new PostActionConfig { Kind = PostActionKind.Sleep, ForceIfOtherQueuesBusy = force },
            "hibernate" => new PostActionConfig { Kind = PostActionKind.Hibernate, ForceIfOtherQueuesBusy = force },
            "shutdown" => new PostActionConfig { Kind = PostActionKind.Shutdown, ForceIfOtherQueuesBusy = force },
            "exit" => new PostActionConfig { Kind = PostActionKind.ExitKasi, ForceIfOtherQueuesBusy = force },
            "run" => ParseRunAction(parts),
            _ => new PostActionConfig { Kind = PostActionKind.None },
        };
    }

    public static string Format(PostActionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Kind switch
        {
            PostActionKind.Sleep => config.ForceIfOtherQueuesBusy ? "sleep:force" : "sleep",
            PostActionKind.Hibernate => config.ForceIfOtherQueuesBusy ? "hibernate:force" : "hibernate",
            PostActionKind.Shutdown => config.ForceIfOtherQueuesBusy ? "shutdown:force" : "shutdown",
            PostActionKind.ExitKasi => config.ForceIfOtherQueuesBusy ? "exit:force" : "exit",
            PostActionKind.RunProgram when !string.IsNullOrWhiteSpace(config.ProgramPath) =>
                config.ForceIfOtherQueuesBusy
                    ? $"run:force:{config.ProgramPath}{(string.IsNullOrWhiteSpace(config.Arguments) ? "" : " " + config.Arguments)}"
                    : $"run:{config.ProgramPath}{(string.IsNullOrWhiteSpace(config.Arguments) ? "" : " " + config.Arguments)}",
            _ => "none",
        };
    }

    private static PostActionConfig ParseRunAction(string[] parts)
    {
        if (parts.Length < 2)
        {
            return new PostActionConfig { Kind = PostActionKind.None };
        }

        bool force = string.Equals(parts[1], "force", StringComparison.OrdinalIgnoreCase);
        string remainder = force && parts.Length > 2 ? parts[2] : string.Join(':', parts.Skip(1));

        if (string.IsNullOrWhiteSpace(remainder))
        {
            return new PostActionConfig { Kind = PostActionKind.None };
        }

        // Split first token as program path, remainder as arguments
        string programPath;
        string? arguments = null;

        if (remainder.StartsWith('"'))
        {
            int closingQuote = remainder.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                programPath = remainder[1..closingQuote];
                arguments = remainder[(closingQuote + 1)..].Trim();
            }
            else
            {
                programPath = remainder.Trim('"');
            }
        }
        else
        {
            int spaceIndex = remainder.IndexOf(' ');
            if (spaceIndex > 0)
            {
                programPath = remainder[..spaceIndex];
                arguments = remainder[(spaceIndex + 1)..].Trim();
            }
            else
            {
                programPath = remainder;
            }
        }

        return new PostActionConfig
        {
            Kind = PostActionKind.RunProgram,
            ForceIfOtherQueuesBusy = force,
            ProgramPath = programPath,
            Arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments,
        };
    }
}
