# Kasi.TestServer

Deterministic HTTP server for tests (TPL 3.1 and 3.2). Contract: [ADR 0001](../../docs/adr/0001-test-server-content-and-chaos-contract.md).

```csharp
await using var host = await TestServerHost.StartAsync();     // in-process, free loopback port
var id = host.Chaos.Register(new ChaosProfile { Seed = 1, ResetAfterBytes = 100_000 });
// send  X-Kasi-Chaos: <id>  on requests that should misbehave; compare downloads with GeneratedContent.Sha256Hex(seed, size)
```

As a process: `dotnet run --project tests/Kasi.TestServer -- 5080` (`KASI_TESTSERVER_BIND=any` to listen on all interfaces).

| Endpoint | Behaviour |
| --- | --- |
| `/files/{seed}/{size}` | Range, If-Range, ETag, Last-Modified, HEAD |
| `/norange/{seed}/{size}` | Ignores Range, always 200 |
| `/chunked/{seed}/{size}` | No Content-Length |
| `/redirect/{hops}/…` | `hops` 302s, then the wrapped path |
| `/auth/basic/…` | Basic auth (`PathGates.BasicUser` / `BasicPassword`), else 401 |
| `/auth/cookie/…` | `GET /auth/cookie/login` sets the session cookie; else 401 |
| `/signed/…?exp=<unix seconds>` | 403 after `exp` or without it |
| `/mutable/{id}` | `host.Mutable.Change(id)` or `POST /_mutable/{id}/change?size=` changes bytes and ETag |
| `/disposition/{case}` | Edge-case Content-Disposition values (`DispositionEndpoints.Cases`) |
| `/hls/{kind}/master.m3u8` | Kinds in `StreamFixtures.HlsKinds`: clear, aes128, SAMPLE-AES and DRM key formats |
| `/dash/{kind}/manifest.mpd` | Kinds in `StreamFixtures.DashKinds`: clear, segment-list, Widevine, PlayReady, FairPlay, ClearKey |
| `/_chaos` | `POST` a `ChaosProfile` as JSON, returns `{ "id": … }`; `DELETE` clears all |

Gates nest: `/redirect/2/signed/files/a/10?exp=…`. HLS and DASH segments are opaque deterministic bytes, not decodable media.
Not yet served: HTTP/2 and TLS, live (no `ENDLIST`) playlists, SegmentBase addressing.
