using System.Globalization;
using System.Net;
using Kasi.TestServer;

// dotnet run --project tests/Kasi.TestServer -- [port]    (KASI_TESTSERVER_BIND=any to listen on all interfaces, e.g. in a container)
var port = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 5080;
var address = Environment.GetEnvironmentVariable("KASI_TESTSERVER_BIND") == "any" ? IPAddress.Any : IPAddress.Loopback;
await using var app = TestServerApp.Create(address, port);
await app.RunAsync();
