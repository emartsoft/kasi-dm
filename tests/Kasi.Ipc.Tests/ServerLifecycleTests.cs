using Kasi.Ipc.Transport;

namespace Kasi.Ipc.Tests;

public class ServerLifecycleTests
{
    [Fact]
    public async Task A_server_disposed_right_after_it_started_stops_cleanly_every_time()
    {
        // The accept loop starts on the thread pool; disposing at once closes the listening instance before its first wait. That race
        // made AppLauncherTests fail once on main with "Cannot access a closed pipe", so it is run many times here.
        for (var i = 0; i < 200; i++)
        {
            var server = PipeTestSupport.StartServer(PipeTestSupport.NewPipeName());

            var dispose = async () => await server.DisposeAsync();

            await dispose.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task A_hand_off_server_disposed_right_after_it_started_stops_cleanly_every_time()
    {
        for (var i = 0; i < 200; i++)
        {
#pragma warning disable CA2000 // disposed by the assertion below
            var server = new HandOffServer(PipeTestSupport.NewPipeName(), (_, _) => ValueTask.CompletedTask);
#pragma warning restore CA2000
            server.Start();

            var dispose = async () => await server.DisposeAsync();

            await dispose.Should().NotThrowAsync();
        }
    }
}
