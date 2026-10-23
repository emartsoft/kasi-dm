using System.Runtime.CompilerServices;
using Kasi.App.Engine;

namespace Kasi.App.Tests;

internal static class TestEnvironment
{
    // CA2255: the point is that no test can reach the real user's database; this runs before any test builds the composition root.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void KeepTestsAwayFromTheRealData() =>
        Environment.SetEnvironmentVariable(AppPaths.DataFolderVariable, Path.Combine(Path.GetTempPath(), "kasi-app-tests-" + Environment.ProcessId));
#pragma warning restore CA2255
}
