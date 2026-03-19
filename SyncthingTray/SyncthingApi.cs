using System.Net;
using System.Text;

namespace SyncthingTray;

/// <summary>
/// Synchronous HTTP client for the Syncthing REST API.
/// Uses HttpWebRequest to avoid async patterns.
/// </summary>
internal sealed class SyncthingApi
{
    private readonly AppConfig _config;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public SyncthingApi(AppConfig config)
    {
        _config = config;
    }

    public (int StatusCode, string Body) Get(string endpoint)
    {
        return DoRequest("GET", endpoint, null);
    }

    public (int StatusCode, string Body) Post(string endpoint, string? body = null)
    {
        return DoRequest("POST", endpoint, body);
    }

    public (int StatusCode, string Body) Patch(string endpoint, string body)
    {
        return DoRequest("PATCH", endpoint, body);
    }

    private (int StatusCode, string Body) DoRequest(string method, string endpoint, string? body)
    {
        try
        {
#pragma warning disable SYSLIB0014 // HttpWebRequest is obsolete but we need synchronous HTTP without async
            var request = (HttpWebRequest)WebRequest.Create(_config.WebUI + endpoint);
#pragma warning restore SYSLIB0014
            request.Method = method;
            request.Headers["X-API-Key"] = _config.ApiKey;
            request.Timeout = 5000;

            if (body is not null)
            {
                request.ContentType = "application/json";
                byte[] data = Utf8NoBom.GetBytes(body);
                request.ContentLength = data.Length;
                using var stream = request.GetRequestStream();
                stream.Write(data, 0, data.Length);
            }

            using var response = (HttpWebResponse)request.GetResponse();
            var responseStream = response.GetResponseStream();
            if (responseStream is null)
                return ((int)response.StatusCode, string.Empty);
            using var reader = new System.IO.StreamReader(responseStream, Utf8NoBom);
            return ((int)response.StatusCode, reader.ReadToEnd());
        }
        catch (WebException ex) when (ex.Response is HttpWebResponse errorResponse)
        {
            using (errorResponse)
            {
                var errorStream = errorResponse.GetResponseStream();
                if (errorStream is null)
                    return ((int)errorResponse.StatusCode, string.Empty);
                using var reader = new System.IO.StreamReader(errorStream, Utf8NoBom);
                return ((int)errorResponse.StatusCode, reader.ReadToEnd());
            }
        }
        catch
        {
            return (-1, string.Empty);
        }
    }
}
