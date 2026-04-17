using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SyncthingTray.Tests;

[TestClass]
public class SyncthingApiTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SyncthingApi_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [TestMethod]
    public void IsReachable_PortOpen_ReturnsTrue()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var config = new AppConfig(_tempDir) { WebUI = $"http://127.0.0.1:{port}" };
            using var api = new SyncthingApi(config);

            Assert.IsTrue(api.IsReachable());
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public void IsReachable_PortClosed_ReturnsFalse()
    {
        int port = GrabUnusedPort();
        var config = new AppConfig(_tempDir) { WebUI = $"http://127.0.0.1:{port}" };
        using var api = new SyncthingApi(config);

        Assert.IsFalse(api.IsReachable());
    }

    [TestMethod]
    public void IsReachable_PortClosed_FailsFast()
    {
        int port = GrabUnusedPort();
        var config = new AppConfig(_tempDir) { WebUI = $"http://127.0.0.1:{port}" };
        using var api = new SyncthingApi(config);

        var sw = Stopwatch.StartNew();
        bool ok = api.IsReachable(timeoutMs: 300);
        sw.Stop();

        Assert.IsFalse(ok);
        // Generous bound — TCP RST on loopback should be near-instant, but CI can be slow.
        Assert.IsTrue(sw.ElapsedMilliseconds < 1000,
            $"IsReachable took {sw.ElapsedMilliseconds}ms for a closed port, expected <1000ms");
    }

    [TestMethod]
    public void IsReachable_CustomTimeoutHonored_ForUnreachableHost()
    {
        // TEST-NET-1 per RFC 5737 — guaranteed not routable. Connect attempt should
        // be cancelled by our timeout, not wait for the OS default (~21s on Windows).
        var config = new AppConfig(_tempDir) { WebUI = "http://192.0.2.1:8384" };
        using var api = new SyncthingApi(config);

        var sw = Stopwatch.StartNew();
        bool ok = api.IsReachable(timeoutMs: 200);
        sw.Stop();

        Assert.IsFalse(ok);
        Assert.IsTrue(sw.ElapsedMilliseconds < 1500,
            $"IsReachable took {sw.ElapsedMilliseconds}ms for unreachable host, expected timeout-bounded");
    }

    [TestMethod]
    public void IsReachable_LocalhostHostname_TriesAllAddressFamilies()
    {
        // Regression test for the Win11 IPv6-localhost bug: `localhost` resolves to
        // [::1, 127.0.0.1]. Old code used a single IPv4 socket and would throw
        // SocketException 10047 on ::1, swallowed to false. Fix: walk all addresses.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var config = new AppConfig(_tempDir) { WebUI = $"http://localhost:{port}" };
            using var api = new SyncthingApi(config);

            Assert.IsTrue(api.IsReachable(),
                "IsReachable must succeed for localhost even if ::1 precedes 127.0.0.1 in DNS");
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public void IsReachable_MalformedUrl_ReturnsFalse()
    {
        var config = new AppConfig(_tempDir) { WebUI = "not a url" };
        using var api = new SyncthingApi(config);

        Assert.IsFalse(api.IsReachable());
    }

    [TestMethod]
    public void DoRequest_WithTimeoutMs_ReturnsMinusOneOnSlowResponse()
    {
        // Listener accepts TCP but never writes an HTTP response — forces the
        // CancellationTokenSource in DoRequest to fire.
        using var server = StubHttpServer.StartStalled();
        var config = new AppConfig(_tempDir)
        {
            WebUI = $"http://127.0.0.1:{server.Port}",
            ApiKey = "testkey",
        };
        using var api = new SyncthingApi(config);

        var sw = Stopwatch.StartNew();
        var (status, _) = api.Get("/rest/system/status", timeoutMs: 150);
        sw.Stop();

        Assert.AreEqual(-1, status);
        Assert.IsTrue(sw.ElapsedMilliseconds < 2000,
            $"Timeout branch took {sw.ElapsedMilliseconds}ms, expected <2000ms");
    }

    [TestMethod]
    public void DoRequest_WithTimeoutMs_SucceedsOnFastResponse()
    {
        using var server = StubHttpServer.StartWithResponse("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");
        var config = new AppConfig(_tempDir)
        {
            WebUI = $"http://127.0.0.1:{server.Port}",
            ApiKey = "testkey",
        };
        using var api = new SyncthingApi(config);

        var (status, body) = api.Get("/rest/system/status", timeoutMs: 1500);

        Assert.AreEqual(200, status);
        Assert.AreEqual("hello", body);
    }

    private static int GrabUnusedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// Minimal TCP-level HTTP server for exercising the DoRequest timeout branch.
    /// One request, one response (or no response, for the stall case), then done.
    /// </summary>
    private sealed class StubHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }

        private StubHttpServer(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public static StubHttpServer StartStalled()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var server = new StubHttpServer(listener);
            // Accept but never reply — holds the client's socket open so the
            // CancellationToken is the only way out.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(server._cts.Token);
                    await Task.Delay(Timeout.Infinite, server._cts.Token);
                }
                catch { /* cancelled on dispose */ }
            });
            return server;
        }

        public static StubHttpServer StartWithResponse(string rawHttpResponse)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var server = new StubHttpServer(listener);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(server._cts.Token);
                    using var stream = client.GetStream();
                    // Drain request headers up to the blank line, then reply.
                    var buf = new byte[4096];
                    int total = 0;
                    while (total < buf.Length)
                    {
                        int n = await stream.ReadAsync(buf.AsMemory(total), server._cts.Token);
                        if (n == 0) break;
                        total += n;
                        var s = System.Text.Encoding.ASCII.GetString(buf, 0, total);
                        if (s.Contains("\r\n\r\n")) break;
                    }
                    var response = System.Text.Encoding.ASCII.GetBytes(rawHttpResponse);
                    await stream.WriteAsync(response, server._cts.Token);
                }
                catch { /* cancelled on dispose or client gone */ }
            });
            return server;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
