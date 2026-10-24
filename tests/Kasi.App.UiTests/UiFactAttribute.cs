using System.Runtime.CompilerServices;

namespace Kasi.App.UiTests;

/// <summary>
/// A test that drives the real Kasi.App window through UI Automation. It needs an interactive desktop, so it runs only when
/// <c>KASI_UI=1</c> is set (the UI workflows set it); otherwise it is skipped with that reason.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("KASI_UI") != "1")
        {
            Skip = "UI tests need an interactive desktop; set KASI_UI=1 to run them.";
        }
    }
}

/// <summary>
/// A <see cref="UiFactAttribute"/> test that has to move the mouse (the download list's rows cannot be selected through UI Automation
/// patterns). It also needs <c>KASI_UI_INPUT=1</c>: a session that cannot inject input (a locked or remote desktop) skips it.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UiInputFactAttribute : FactAttribute
{
    public UiInputFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("KASI_UI") != "1" || Environment.GetEnvironmentVariable("KASI_UI_INPUT") != "1")
        {
            Skip = "This UI test clicks with the mouse; set KASI_UI=1 and KASI_UI_INPUT=1 on an interactive desktop to run it.";
        }
    }
}
