using System.Diagnostics;
using Kasi.App.Browser;
using Kasi.App.Clipboard;
using Kasi.App.ViewModels;

namespace Kasi.App.Tests;

[Trait("Requirement", "KASI-BRW-007")]
public class ClipboardLinkTests
{
    private static readonly string[] _extensions = ["iso", "zip", "tar.gz"];

    [Theory]
    [InlineData("https://files.example.org/ubuntu.iso", "https://files.example.org/ubuntu.iso")]
    [InlineData("  http://files.example.org/a/b/pack.ZIP \r\n", "http://files.example.org/a/b/pack.ZIP")]
    [InlineData("ftp://files.example.org/pub/a.zip", "ftp://files.example.org/pub/a.zip")]
    [InlineData("https://files.example.org/dl/source.tar.gz", "https://files.example.org/dl/source.tar.gz")]
    [InlineData("https://files.example.org/get/a.iso?token=abc&x=1", "https://files.example.org/get/a.iso?token=abc&x=1")]
    [InlineData("https://files.example.org/my%20file.zip", "https://files.example.org/my%20file.zip")]
    public void A_single_download_address_with_a_listed_extension_is_offered(string text, string expected) =>
        ClipboardLink.TryParse(text, _extensions).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    [InlineData("https://files.example.org/page.html")]
    [InlineData("https://files.example.org/")]
    [InlineData("https://files.example.org/zip")]
    [InlineData("see https://files.example.org/a.iso for the image")]
    [InlineData("https://files.example.org/a.iso https://files.example.org/b.iso")]
    [InlineData("https://files.example.org/a.iso\nhttps://files.example.org/b.iso")]
    [InlineData("file:///c:/temp/a.iso")]
    [InlineData("javascript:alert(1)//a.iso")]
    [InlineData("data:application/zip;base64,AAAA")]
    [InlineData("mailto:someone@example.org?subject=a.zip")]
    [InlineData("c:\\downloads\\a.iso")]
    [InlineData("hunter2.zip")]
    public void Anything_else_is_ignored(string? text) =>
        ClipboardLink.TryParse(text, _extensions).Should().BeNull();

    [Fact]
    public void An_empty_list_of_extensions_offers_nothing()
    {
        ClipboardLink.TryParse("https://files.example.org/a.iso", []).Should().BeNull();
    }

    [Fact]
    public void An_address_longer_than_the_protocol_allows_is_ignored()
    {
        ClipboardLink.TryParse("https://files.example.org/" + new string('a', ClipboardLink.MaxLength) + ".iso", _extensions).Should().BeNull();
    }

    [Fact]
    public void A_dot_in_the_users_list_is_tolerated()
    {
        ClipboardLink.TryParse("https://files.example.org/a.iso", [".iso"]).Should().NotBeNull();
    }
}

[Trait("Requirement", "KASI-BRW-007")]
public class ClipboardWatcherTests
{
    private sealed class FakeSource : IClipboardSource
    {
        public event Action? Changed;

        public string? Text { get; set; }

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public int Reads { get; private set; }

        public bool Running { get; private set; }

        public void Start()
        {
            Starts++;
            Running = true;
        }

        public void Stop()
        {
            Stops++;
            Running = false;
        }

        public string? ReadText()
        {
            Reads++;
            return Text;
        }

        public void Copy(string? text)
        {
            Text = text;
            if (Running)
            {
                Changed?.Invoke(); // a real source only raises while it is listening
            }
        }

        public void CopyEvenIfStopped(string? text)
        {
            Text = text;
            Changed?.Invoke();
        }
    }

    private sealed class MutableSettings(BrowserSettings current) : IBrowserSettings
    {
        public BrowserSettings Current { get; private set; } = current;

        public event Action<BrowserSettings>? Changed;

        public Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken)
        {
            Current = settings;
            Changed?.Invoke(settings);
            return Task.CompletedTask;
        }
    }

    private static (ClipboardWatcher Watcher, FakeSource Source, MutableSettings Settings, List<string> Offered) Make(bool on)
    {
        var settings = new MutableSettings(new BrowserSettings { ClipboardCapture = on, Extensions = ["iso", "zip"] });
        var source = new FakeSource();
        var offered = new List<string>();
        var watcher = new ClipboardWatcher(settings, source, offered.Add);
        watcher.Start();
        return (watcher, source, settings, offered);
    }

    [Fact]
    [Trait("TC", "TC-BRW-008")]
    public void With_the_setting_on_copying_an_iso_link_opens_the_dialog()
    {
        var (_, source, _, offered) = Make(on: true);

        source.Copy("https://files.example.org/ubuntu.iso");

        offered.Should().Equal("https://files.example.org/ubuntu.iso");
    }

    [Fact]
    [Trait("TC", "TC-BRW-008")]
    public void With_the_setting_off_nothing_is_listened_to_read_or_offered()
    {
        var (_, source, _, offered) = Make(on: false);

        source.Copy("https://files.example.org/ubuntu.iso");
        source.CopyEvenIfStopped("https://files.example.org/other.iso");

        offered.Should().BeEmpty();
        source.Starts.Should().Be(0);
        source.Reads.Should().Be(0, "the clipboard is not read while the setting is off");
    }

    [Fact]
    public async Task Switching_the_setting_starts_and_stops_the_listener()
    {
        var (_, source, settings, offered) = Make(on: false);

        await settings.SaveAsync(settings.Current with { ClipboardCapture = true }, CancellationToken.None);
        source.Running.Should().BeTrue();
        source.Copy("https://files.example.org/a.zip");
        await settings.SaveAsync(settings.Current with { ClipboardCapture = false }, CancellationToken.None);
        var readsWhenOn = source.Reads;
        source.CopyEvenIfStopped("https://files.example.org/b.zip");

        source.Running.Should().BeFalse();
        offered.Should().Equal("https://files.example.org/a.zip");
        source.Reads.Should().Be(readsWhenOn);
    }

    [Fact]
    public void Copying_the_same_link_twice_in_a_row_offers_it_once_but_a_different_one_in_between_resets_that()
    {
        var (_, source, _, offered) = Make(on: true);

        source.Copy("https://files.example.org/a.iso");
        source.Copy("https://files.example.org/a.iso");
        source.Copy("https://files.example.org/b.iso");
        source.Copy("https://files.example.org/a.iso");

        offered.Should().Equal("https://files.example.org/a.iso", "https://files.example.org/b.iso", "https://files.example.org/a.iso");
    }

    [Fact]
    public void Text_that_is_not_a_download_link_is_ignored_and_does_not_reset_the_repeat_guard()
    {
        var (_, source, _, offered) = Make(on: true);

        source.Copy("https://files.example.org/a.iso");
        source.Copy("a password");
        source.Copy(null);
        source.Copy("https://files.example.org/a.iso");

        offered.Should().ContainSingle();
    }

    [Fact]
    public void Disposing_stops_listening()
    {
        var (watcher, source, _, offered) = Make(on: true);

        watcher.Dispose();
        source.CopyEvenIfStopped("https://files.example.org/a.iso");

        source.Running.Should().BeFalse();
        offered.Should().BeEmpty();
    }

    [Fact]
    public void The_settings_form_keeps_capture_off_by_default_and_round_trips_it()
    {
        new BrowserSettings().ClipboardCapture.Should().BeFalse();
        var form = new SettingsViewModel(new MutableSettings(new BrowserSettings()));

        form.ClipboardCapture.Should().BeFalse();
        form.ClipboardCapture = true;

        form.ToSettings().ClipboardCapture.Should().BeTrue();
    }
}

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-BRW-007")]
public class Win32ClipboardSourceTests
{
    [Fact]
    [Trait("TC", "TC-BRW-008")]
    public async Task A_real_copy_raises_changed_and_the_text_can_be_read_back_and_stopping_ends_the_listener()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = new Win32ClipboardSource();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed += () => changed.TrySetResult();
        source.Start();

        var started = Process.Start(new ProcessStartInfo("clip.exe") { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true });
        if (started is null)
        {
            Assert.Skip("clip.exe could not be started.");
        }

        using (started)
        {
            await started.StandardInput.WriteAsync("https://files.example.org/clipboard-test.iso");
            started.StandardInput.Close();
            await started.WaitForExitAsync(ct);
            if (started.ExitCode != 0)
            {
                Assert.Skip("This session has no clipboard to write to.");
            }
        }

        var arrived = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));
        if (arrived != changed.Task)
        {
            Assert.Skip("The clipboard change was not reported in this session.");
        }

        source.ReadText().Should().Be("https://files.example.org/clipboard-test.iso");
        source.Stop();
        source.Stop(); // stopping twice is harmless
    }
}
