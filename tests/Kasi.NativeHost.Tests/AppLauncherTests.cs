using System.Diagnostics;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;
using Kasi.NativeHost.Relay;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.NativeHost.Tests;

[Trait("TC", "TC-BRW-002")]
[Trait("Requirement", "KASI-BRW-006")]
public sealed class AppLauncherTests : IAsyncLifetime
{
    private readonly string _pipeName = "kasi-test-" + Guid.NewGuid().ToString("N");
    private readonly string _dir = Directory.CreateTempSubdirectory("kasi-host-").FullName;
    private readonly FakeTimeProvider _time = new();
    private readonly List<ProcessStartInfo> _started = [];
    private KasiPipeServer? _server;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        Directory.Delete(_dir, recursive: true);
    }

    private string FakeKasiExe()
    {
        var path = Path.Combine(_dir, "Kasi.exe");
        File.WriteAllText(path, "not really an executable");
        return path;
    }

    private void StartServer()
    {
        _server = new KasiPipeServer(_pipeName, (_, m, _) => ValueTask.FromResult<IpcMessage?>(Msg.Ack(m.Id)));
        _server.Start();
    }

    private AppLauncher Launcher(string exe, Action? onStart = null) =>
        new(exe, _pipeName, _time, info =>
        {
            _started.Add(info);
            onStart?.Invoke();
        });

    /// <summary>Drives the fake clock while the launcher waits, as the real clock would.</summary>
    private async Task<T> RunAsync<T>(Task<T> task)
    {
        for (var i = 0; i < 2_000 && !task.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(25));
            await Task.Delay(2);
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_running_Kasi_is_connected_to_without_starting_anything()
    {
        StartServer();

        await using var pipe = await Launcher(FakeKasiExe()).ConnectAsync(CancellationToken.None);

        _started.Should().BeEmpty();
    }

    [Fact]
    public async Task An_absent_pipe_starts_the_sibling_executable_with_tray_and_connects_once_it_appears()
    {
        var exe = FakeKasiExe();

        await using var pipe = await RunAsync(Launcher(exe, StartServer).ConnectAsync(CancellationToken.None));

        var info = _started.Should().ContainSingle().Subject;
        info.FileName.Should().Be(exe);
        info.Arguments.Should().Be("--tray");
        info.UseShellExecute.Should().BeTrue("the child must not inherit the browser's stdin and stdout");
    }

    [Fact]
    public async Task An_executable_that_never_opens_the_pipe_is_APP_NOT_RUNNING_within_five_seconds_and_started_only_once()
    {
        var launcher = Launcher(FakeKasiExe());
        var before = _time.GetTimestamp();

        var failed = async () => await RunAsync(launcher.ConnectAsync(CancellationToken.None));

        (await failed.Should().ThrowAsync<AppUnavailableException>()).Which.Code.Should().Be(IpcErrorCode.AppNotRunning);
        _time.GetElapsedTime(before).Should().BeGreaterThanOrEqualTo(AppLauncher.Window).And.BeLessThan(AppLauncher.Window + TimeSpan.FromMilliseconds(500));
        _started.Should().ContainSingle();
    }

    [Fact]
    public async Task A_missing_executable_is_APP_NOT_INSTALLED_at_once_without_waiting()
    {
        var launcher = Launcher(Path.Combine(_dir, "Kasi.exe"));

        var failed = () => launcher.ConnectAsync(CancellationToken.None);

        (await failed.Should().ThrowAsync<AppUnavailableException>()).Which.Code.Should().Be(IpcErrorCode.AppNotInstalled);
        _time.GetUtcNow().Should().Be(new FakeTimeProvider().GetUtcNow(), "no time was spent waiting");
        _started.Should().BeEmpty();
    }

    [Fact]
    public void The_default_executable_is_the_file_next_to_the_host_never_a_PATH_lookup()
    {
        AppLauncher.NextToHost.Should().Be(Path.Combine(AppContext.BaseDirectory, "Kasi.exe"));
        Path.IsPathRooted(AppLauncher.NextToHost).Should().BeTrue();
    }
}
