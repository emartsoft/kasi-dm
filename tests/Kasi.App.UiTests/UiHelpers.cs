using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;

namespace Kasi.App.UiTests;

/// <summary>Lookups that wait for the window to show what the test needs, and a tree dump that goes into the failure message.</summary>
internal static class UiHelpers
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

    public static T WaitFor<T>(Func<T?> find, string what, AutomationElement? dumpOf = null, TimeSpan? timeout = null)
        where T : class
    {
        T? found = null;
        var result = Retry.WhileNull(() => found = find(), timeout ?? _wait, TimeSpan.FromMilliseconds(200));
        return result.Success && found is not null
            ? found
            : throw new TimeoutException($"Waited for {what}.{(dumpOf is null ? "" : Environment.NewLine + Dump(dumpOf))}");
    }

    public static AutomationElement Named(this AutomationElement root, string name, string what, TimeSpan? timeout = null) =>
        WaitFor(() => root.FindFirstDescendant(c => c.ByName(name)), $"{what} ('{name}')", root, timeout);

    /// <summary>The control of this type with this name; a label above a text box carries the same name, so the type picks the right one.</summary>
    public static AutomationElement Named(this AutomationElement root, ControlType type, string name, string what) =>
        WaitFor(() => root.FindFirstDescendant(c => c.ByControlType(type).And(c.ByName(name))), $"{what} ('{name}')", root);

    public static AutomationElement? Ancestor(this AutomationElement element, ControlType type)
    {
        for (var parent = element.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.ControlType == type)
            {
                return parent;
            }
        }

        return null;
    }

    public static void Press(this AutomationElement root, string name) => root.Named(ControlType.Button, name, "a button").AsButton().Invoke();

    public static void SetText(this AutomationElement root, string name, string text) =>
        root.Named(ControlType.Edit, name, "a text box").Patterns.Value.Pattern.SetValue(text);

    /// <summary>The download list's rows as lines of cell text, for the assertions and the failure messages.</summary>
    public static IReadOnlyList<string> RowTexts(this Window window)
    {
        var grid = window.FindFirstDescendant(c => c.ByAutomationId("Grid"));
        if (grid is null)
        {
            return [];
        }

        return [.. grid.FindAllDescendants(c => c.ByControlType(ControlType.DataItem))
            .Select(row => string.Join(" | ", row.FindAllDescendants(c => c.ByControlType(ControlType.Text)).Select(t => t.Name)))];
    }

    public static string Dump(AutomationElement root)
    {
        var text = new StringBuilder();
        void Walk(AutomationElement e, int depth)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{new string(' ', depth * 2)}{e.Properties.ControlType.ValueOrDefault} id='{e.Properties.AutomationId.ValueOrDefault}' name='{e.Properties.Name.ValueOrDefault}'");
            if (depth < 8)
            {
                foreach (var child in e.FindAllChildren())
                {
                    Walk(child, depth + 1);
                }
            }
        }

        Walk(root, 0);
        return text.ToString();
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
