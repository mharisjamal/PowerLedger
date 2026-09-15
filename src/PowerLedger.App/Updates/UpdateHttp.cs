using System.Net.Http;
using System.Net.Http.Headers;

namespace PowerLedger.App;

/// <summary>The one HttpClient updates use for the App's life: the system's proxy, the user agent GitHub's API asks for,
/// and no overall timeout, since the feed and the download each set their own.</summary>
internal static class UpdateHttp
{
    public static HttpClient Create(string version)
    {
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PowerLedger", version));
        return http;
    }
}
