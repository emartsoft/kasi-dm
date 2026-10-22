using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Kasi.NativeHost.Tests;

/// <summary>
/// scripts/native-host/Register-NativeHost.ps1 against a private registry root (HKCU\Software\KasiTest\&lt;guid&gt;) and a temp manifest
/// folder: the real browser keys are never touched (M2.6 plan 2.3). The allow-list written here is what the browser enforces
/// (KASI-NFR-010, TC-BRW-007; the browser side is extension/tests/e2e/allowlist.e2e.test.ts).
/// </summary>
[Trait("Requirement", "KASI-NFR-010")]
[Trait("TC", "TC-BRW-007")]
[Trait("Category", "Integration")]
public sealed class RegistrationScriptTests : IDisposable
{
    private const string Host = "com.martsoft.kasi";
    private const string ChromeStore = "biihacogjoggaalmfdbmacakklaagdbo";
    private const string EdgeStore = "idaeblamiiaofpigmccbkpcbckoddgjd";
    private const string Developer = "abcdefghijklmnopabcdefghijklmnop";
    private const string FirefoxStore = "kasi@martsoft.co.ke";

    private static readonly string _repo = FindRepo();
    private readonly string _rootName = @"Software\KasiTest\" + Guid.NewGuid().ToString("N");
    private readonly string _work = Directory.CreateTempSubdirectory("kasi-reg-").FullName;

    private string Manifests => Path.Combine(_work, "manifests");

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);
        Directory.Delete(_work, recursive: true);
    }

    [Fact]
    public void Each_browser_gets_its_own_key_and_a_manifest_with_exactly_its_ids()
    {
        Run().ExitCode.Should().Be(0);

        Allowed("Chrome", "allowed_origins").Should().Equal($"chrome-extension://{ChromeStore}/");
        Allowed("Edge", "allowed_origins").Should().Equal($"chrome-extension://{EdgeStore}/");
        Allowed("Chromium", "allowed_origins").Should().Equal($"chrome-extension://{ChromeStore}/");
        Allowed("Firefox", "allowed_extensions").Should().Equal(FirefoxStore);
        KeyValue("Google", "Chrome").Should().Be(ManifestFile("chrome"));
        KeyValue("Microsoft", "Edge").Should().Be(ManifestFile("edge"));
        KeyValue("Chromium").Should().Be(ManifestFile("chromium"));
        KeyValue("Mozilla").Should().Be(ManifestFile("firefox"));
    }

    [Fact]
    public void Ids_given_on_the_command_line_are_added_to_their_own_browser_only()
    {
        Run("-ChromeExtensionId", Developer, "-FirefoxExtensionId", "dev@example.test").ExitCode.Should().Be(0);

        Allowed("Chrome", "allowed_origins").Should().Equal($"chrome-extension://{ChromeStore}/", $"chrome-extension://{Developer}/");
        Allowed("Chromium", "allowed_origins").Should().Equal($"chrome-extension://{ChromeStore}/", $"chrome-extension://{Developer}/");
        Allowed("Edge", "allowed_origins").Should().Equal($"chrome-extension://{EdgeStore}/");
        Allowed("Firefox", "allowed_extensions").Should().Equal(FirefoxStore, "dev@example.test");
    }

    [Fact]
    public void The_manifest_is_the_template_with_the_ids_filled_in()
    {
        Run().ExitCode.Should().Be(0);

        var template = File.ReadAllText(Path.Combine(_repo, "src", "Kasi.NativeHost", "Registration", "com.martsoft.kasi.json"));
        var expected = template
            .Replace("@HOST_PATH@", HostExe.Replace(@"\", @"\\"))
            .Replace("@ALLOW_KEY@", "allowed_origins")
            .Replace("@ALLOWED@", $"\"chrome-extension://{ChromeStore}/\"");
        File.ReadAllText(ManifestFile("chrome")).TrimEnd().Should().Be(expected.TrimEnd());
    }

    [Fact]
    public void A_second_run_changes_nothing()
    {
        Run().ExitCode.Should().Be(0);
        var before = Snapshot();

        Run().ExitCode.Should().Be(0);

        Snapshot().Should().Equal(before);
    }

    [Fact]
    public void Unregister_removes_what_it_wrote_leaves_foreign_keys_and_files_alone_and_can_run_twice()
    {
        using (var foreign = Registry.CurrentUser.CreateSubKey($@"{_rootName}\Google\Chrome\NativeMessagingHosts\com.other.host"))
        {
            foreign.SetValue("", "other.json");
        }

        Directory.CreateDirectory(Manifests);
        File.WriteAllText(Path.Combine(Manifests, "other.json"), "{}");
        Run().ExitCode.Should().Be(0);

        Run("-Unregister").ExitCode.Should().Be(0);
        Run("-Unregister").ExitCode.Should().Be(0);

        foreach (var browser in new[] { @"Google\Chrome", @"Microsoft\Edge", "Chromium", "Mozilla" })
        {
            Registry.CurrentUser.OpenSubKey($@"{_rootName}\{browser}\NativeMessagingHosts\{Host}").Should().BeNull(browser);
        }

        Registry.CurrentUser.OpenSubKey($@"{_rootName}\Google\Chrome\NativeMessagingHosts\com.other.host").Should().NotBeNull();
        Directory.GetFiles(Manifests).Select(Path.GetFileName).Should().Equal("other.json");
    }

    [Fact]
    public void Unregister_removes_the_manifest_folder_when_nothing_else_is_in_it()
    {
        Run().ExitCode.Should().Be(0);

        Run("-Unregister").ExitCode.Should().Be(0);

        Directory.Exists(Manifests).Should().BeFalse();
    }

    [Theory]
    [InlineData("-ChromeExtensionId", "ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP")] // upper case
    [InlineData("-ChromeExtensionId", "abcdefghijklmnopabcdefghijklmnoq")] // q is not a Chromium id letter
    [InlineData("-ChromeExtensionId", "short")]
    [InlineData("-EdgeExtensionId", "*")]
    [InlineData("-FirefoxExtensionId", "bad id\"")]
    public void An_invalid_id_is_refused_before_anything_is_written(string parameter, string id)
    {
        var result = Run(parameter, id);

        result.ExitCode.Should().NotBe(0);
        Registry.CurrentUser.OpenSubKey(_rootName).Should().BeNull();
        Directory.Exists(Manifests).Should().BeFalse();
    }

    [Fact]
    public void A_browser_with_no_id_is_skipped()
    {
        File.WriteAllText(StoreIds, """{ "chrome": [], "edge": [ "idaeblamiiaofpigmccbkpcbckoddgjd" ], "firefox": [] }""");

        Run().ExitCode.Should().Be(0);

        Registry.CurrentUser.OpenSubKey($@"{_rootName}\Google\Chrome\NativeMessagingHosts\{Host}").Should().BeNull();
        Registry.CurrentUser.OpenSubKey($@"{_rootName}\Mozilla\NativeMessagingHosts\{Host}").Should().BeNull();
        KeyValue("Microsoft", "Edge").Should().Be(ManifestFile("edge"));
    }

    [Fact]
    public void The_store_ids_file_holds_valid_ids_and_the_firefox_id_the_manifest_ships()
    {
        using var ids = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repo, "extension", "store-ids.json")));
        foreach (var name in new[] { "chrome", "edge" })
        {
            ids.RootElement.GetProperty(name).EnumerateArray().Select(e => e.GetString()).Should().NotBeEmpty().And.OnlyContain(id => System.Text.RegularExpressions.Regex.IsMatch(id!, "^[a-p]{32}$"));
        }

        using var firefox = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repo, "extension", "manifests", "manifest.firefox.json")));
        firefox.RootElement.GetProperty("browser_specific_settings").GetProperty("gecko").GetProperty("id").GetString()
            .Should().Be(ids.RootElement.GetProperty("firefox")[0].GetString());
    }

    private string HostExe => Path.Combine(_work, "Kasi.NativeHost.exe");

    private string StoreIds => Path.Combine(_work, "store-ids.json");

    private string ManifestFile(string browser) => Path.Combine(Manifests, $"{Host}.{browser}.json");

    private (int ExitCode, string Output) Run(params string[] extra)
    {
        File.WriteAllText(HostExe, "");
        if (!File.Exists(StoreIds))
        {
            File.WriteAllText(StoreIds, $$"""{ "chrome": ["{{ChromeStore}}"], "edge": ["{{EdgeStore}}"], "firefox": ["{{FirefoxStore}}"] }""");
        }

        var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-File", Path.Combine(_repo, "scripts", "native-host", "Register-NativeHost.ps1"),
            "-HostPath", HostExe, "-StoreIdsFile", StoreIds, "-RegistryRoot", @"HKCU:\" + _rootName, "-ManifestDirectory", Manifests, .. extra])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private string[] Allowed(string browser, string property)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(ManifestFile(browser.ToLowerInvariant())));
        manifest.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("name", "description", "path", "type", property);
        manifest.RootElement.GetProperty("name").GetString().Should().Be(Host);
        manifest.RootElement.GetProperty("path").GetString().Should().Be(HostExe);
        return [.. manifest.RootElement.GetProperty(property).EnumerateArray().Select(e => e.GetString()!)];
    }

    private string? KeyValue(params string[] path)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{_rootName}\{string.Join('\\', path)}\NativeMessagingHosts\{Host}");
        return key?.GetValue("") as string;
    }

    private List<string> Snapshot()
    {
        List<string> all = [];
        foreach (var browser in new[] { @"Google\Chrome", @"Microsoft\Edge", "Chromium", "Mozilla" })
        {
            all.Add(KeyValue(browser.Split('\\')) ?? "(none)");
        }

        all.AddRange(Directory.GetFiles(Manifests).Order().Select(f => f + File.ReadAllText(f)));
        return all;
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kasi.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Kasi.slnx not found above the test output folder.");
    }
}
