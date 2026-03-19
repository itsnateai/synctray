using System.Net;
using System.Net.Http;
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

    public (int StatusCode, string Body) Get(string endpoint)
    {
        return DoRequest(HttpMethod.Get, endpoint, null);
    }

    public (int StatusCode, string Body) Post(string endpoint, string? body = null)
    {
        return DoRequest(HttpMethod.Post, endpoint, body);
    }

    public (int StatusCode, string Body) Patch(string endpoint, string body)
    {
        return DoRequest(HttpMethod.Patch, endpoint, body);
    }

    private (int StatusCode, string Body) DoRequest(HttpMethod method, string endpoint, string? body)
    {
        try
        {
            using var request = new HttpRequestMessage(method, _config.WebUI + endpoint);
            request.Headers.Add("X-API-Key", _config.ApiKey);

            if (body is not null)
                request.Content = new StringContent(body, Utf8NoBom, "application/json");

            using var response = _http.Send(request);
            var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ((int)response.StatusCode, responseBody);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            return ((int)ex.StatusCode, string.Empty);
        }
        catch
        {
            return (-1, string.Empty);
        }
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
