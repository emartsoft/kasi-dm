using Kasi.BrowserHarness;
using Kasi.TestServer;

// The end-to-end harness for TC-BRW-001 to 004 (docs/plans/m2-4.md section 3.1, ADR 0019).
//   Kasi.BrowserHarness serve --config <file>    test server, pages, admin API and (unless "stub": false) the stub Kasi on the pipe
//   Kasi.BrowserHarness --tray                   what the native host runs as Kasi.exe: the stub plus the admin API only
// It prints one JSON line {"admin": "...", "testServer": "..."} when ready. Nothing here ever touches the public internet.
var tray = args.Contains("--tray");
var configIndex = Array.IndexOf(args, "--config");
var config = tray
    ? Path.Combine(AppContext.BaseDirectory, "kasi-harness.json")
    : configIndex >= 0 && configIndex + 1 < args.Length ? args[configIndex + 1] : null;
var options = config is null ? new HarnessOptions() : HarnessOptions.Load(config);

using var quit = new CancellationTokenSource();
await using var stub = new StubKasi(options);
if (tray)
{
    // the host has to retry its connection for this long before Kasi is there
    await Task.Delay(options.LaunchDelayMs);
}

if (options.Stub)
{
    try
    {
        stub.Start();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // another Kasi (or harness) owns the pipe; the test must not run against it
        await Console.Error.WriteLineAsync("The Kasi pipe is already in use.");
        return 1;
    }
}

await using var testServer = tray ? null : await TestServerHost.StartAsync();
var testServerUrl = testServer?.BaseAddress.ToString().TrimEnd('/') ?? "http://127.0.0.1:1";
await using var app = PageServer.Create(options, stub, testServerUrl, quit.Cancel);
await app.StartAsync();
Console.WriteLine($"{{\"admin\":\"{app.Urls.First()}\",\"testServer\":\"{(tray ? string.Empty : testServerUrl)}\"}}");

if (!tray)
{
    // the Node side closes our stdin when it ends, so a crashed run leaves no process behind
    _ = Task.Run(async () =>
    {
        await Console.In.ReadToEndAsync();
        await quit.CancelAsync();
    });
}

try
{
    await Task.Delay(Timeout.Infinite, quit.Token);
}
catch (OperationCanceledException)
{
    // asked to stop
}

return 0;
