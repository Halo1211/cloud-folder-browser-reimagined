using System.Net;
using CG.Web.MegaApiClient;
using WebDAVClient;
using WebDAVClient.HttpClient;
using YandexDiskSharp;

namespace CloudFolderBrowser.Networking;

public static class NetworkClientAdapters
{
    public static MegaApiClient CreateMegaClient()
    {
        var webClient = new CG.Web.MegaApiClient.WebClient(
            messageHandler: AppHttpClientFactory.CreateHandler(
                DecompressionMethods.All, routeKey: "Mega"));
        return new MegaApiClient(webClient);
    }

    public static Client CreateWebDavClient(NetworkCredential credential, TimeSpan? timeout = null)
    {
        var handler = AppHttpClientFactory.CreateHandler(
            DecompressionMethods.Deflate | DecompressionMethods.GZip,
            serverCredentials: credential,
            routeKey: "WebDav");
        var httpClient = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(10)
        };
        httpClient.DefaultRequestHeaders.ExpectContinue = false;
        return new Client(new HttpClientWrapper(httpClient, httpClient))
        {
            Credentials = credential
        };
    }

    public static RestClient CreateYandexClient(string? accessToken = null)
    {
        RestClient client = string.IsNullOrWhiteSpace(accessToken)
            ? new RestClient()
            : new RestClient(accessToken.Trim());
        NetworkConfiguration configuration = NetworkConfiguration.FromSettings("Yadisk");
        client.Proxy = configuration.ProxyMode switch
        {
            ProxyMode.Direct => new WebProxy(),
            ProxyMode.Custom => configuration.CreateWebProxy(),
            _ => WebRequest.DefaultWebProxy
        };
        return client;
    }
}
