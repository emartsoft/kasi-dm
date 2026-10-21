using Kasi.NativeHost.Framing;
using Kasi.NativeHost.Relay;

// Stdout belongs to the protocol: anything that writes to Console.Out lands on stderr instead.
Console.SetOut(Console.Error);

using var stdout = Console.OpenStandardOutput();
using var output = new NativeFrameWriter(stdout);
var launcher = new AppLauncher(AppLauncher.NextToHost);
using var stdin = Console.OpenStandardInput();
using var relay = new HostRelay(stdin, output, launcher, Console.Error);
try
{
    return await relay.RunAsync(CancellationToken.None).ConfigureAwait(false);
}
catch (Exception ex)
{
    // the type only: messages may carry what the browser sent
    await Console.Error.WriteLineAsync($"host: failed ({ex.GetType().Name})").ConfigureAwait(false);
    return 1;
}
