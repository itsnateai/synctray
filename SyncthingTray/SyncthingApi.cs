using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace SyncthingTray;

/// <summary>
/// Synchronous HTTP client for the Syncthing REST API.
/// Uses a shared HttpClient for connection pooling (important for 24/7 operation).
/// </summary>
internal sealed class SyncthingApi : IDisposable
{
    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private bool _disposed;

    // Log-flood guard: during a Syncthing restart the UI-thread + 10s poll-tick can
    // fire 2-4 HTTP calls per tick. Without dedupe, a recurring unknown exception type
    // would log at the full fire rate (~360/hr). We dedupe per-type with a 60s window:
    // the dictionary lets two alternating types both respect their own windows
    // independently (a single-slot design would let A,B,A,B,... defeat the guard).
    private static readonly object WarnLock = new();
    private static readonly Dictionary<string, long> _lastWarnTicks = new();

    public SyncthingApi(AppConfig config)
    {
        _config = config;
        var handler = new HttpClientHandler
        {
            // Accept self-signed certs (Syncthing uses self-signed HTTPS by default)
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public (int StatusCode, string Body) Get(string endpoint, int? timeoutMs = null)
    {
        return DoRequest(HttpMethod.Get, endpoint, null, timeoutMs);
    }

    public (int StatusCode, string Body) Post(string endpoint, string? body = null, int? timeoutMs = null)
    {
        return DoRequest(HttpMethod.Post, endpoint, body, timeoutMs);
    }

    public (int StatusCode, string Body) Patch(string endpoint, string body, int? timeoutMs = null)
    {
        return DoRequest(HttpMethod.Patch, endpoint, body, timeoutMs);
    }

    /// <summary>
    /// Fast TCP connect probe. Returns true if the Syncthing WebUI port is accepting
    /// connections. Used to skip full HTTP calls on the UI thread when Syncthing is
    /// off — saves the 5s HttpClient timeout every time the settings dialog opens.
    /// </summary>
    public bool IsReachable(int timeoutMs = 300)
    {
        if (!Uri.TryCreate(_config.WebUI, UriKind.Absolute, out var uri))
            return false;

        IPAddress[] addresses;
        try
        {
            // IP literal ("127.0.0.1", "::1") → no DNS. Hostname ("localhost") → hosts
            // file lookup, which is what surfaces the dual IPv4/IPv6 behavior that broke
            // the single-AddressFamily Socket ctor on Win11.
            addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? [literal]
                : Dns.GetHostAddresses(uri.Host);
        }
        catch
        {
            return false;
        }

        // Try each resolved address with its matching AddressFamily. On Win11,
        // `localhost` typically resolves to [::1, 127.0.0.1] — we must try both
        // since Syncthing's listener may be IPv4-only or IPv6-only.
        foreach (var addr in addresses)
        {
            if (TryConnect(addr, uri.Port, timeoutMs))
                return true;
        }
        return false;
    }

    private static bool TryConnect(IPAddress address, int port, int timeoutMs)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var iar = socket.BeginConnect(address, port, null, null);
            if (!iar.AsyncWaitHandle.WaitOne(timeoutMs))
                return false;
            socket.EndConnect(iar);
            return socket.Connected;
        }
        catch
        {
            return false;
        }
    }

    private (int StatusCode, string Body) DoRequest(HttpMethod method, string endpoint, string? body, int? timeoutMs)
    {
        try
        {
            using var request = new HttpRequestMessage(method, _config.WebUI + endpoint);
            request.Headers.Add("X-API-Key", _config.ApiKey);

            if (body is not null)
                request.Content = new StringContent(body, Utf8NoBom, "application/json");

            HttpResponseMessage response;
            if (timeoutMs is int t && t > 0)
            {
                using var cts = new CancellationTokenSource(t);
                response = _http.Send(request, cts.Token);
            }
            else
            {
                response = _http.Send(request);
            }

            using (response)
            {
                // Fully synchronous body read — no Task, no SynchronizationContext
                // capture, no sync-over-async deadlock surface on the WinForms UI
                // thread. HttpClient buffers the content by default (Send returns
                // after headers-AND-body) so this is a fast MemoryStream drain.
                // HttpClient.Timeout (5s) remains the ceiling for the whole request.
                using var stream = response.Content.ReadAsStream();
                using var reader = new StreamReader(stream);
                var responseBody = reader.ReadToEnd();
                return ((int)response.StatusCode, responseBody);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            return ((int)ex.StatusCode, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (-1, string.Empty);
        }
        catch (HttpRequestException)
        {
            return (-1, string.Empty);
        }
        catch (Exception ex)
        {
            WarnRateLimited(ex, method, endpoint);
            return (-1, string.Empty);
        }
    }

    private static void WarnRateLimited(Exception ex, HttpMethod method, string endpoint)
    {
        var type = ex.GetType().Name;
        long now = Environment.TickCount64;
        lock (WarnLock)
        {
            // Per-type dedup — new type → log. Existing type within 60s → skip.
            // Dictionary grows by one entry per unique exception class; bounded by
            // the small set of exception types HttpClient can throw.
            if (_lastWarnTicks.TryGetValue(type, out long last) && now - last < 60_000)
                return;
            _lastWarnTicks[type] = now;
        }
        TrayLog.Warn($"DoRequest {method} {endpoint}: unexpected {type}: {ex.Message}");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _http.Dispose();
        }
    }
}
