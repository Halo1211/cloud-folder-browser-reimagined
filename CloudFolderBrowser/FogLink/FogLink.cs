using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser
{
    public class FogLink
    {
        static HttpClient client = CreateClient();

        private static HttpClient CreateClient()
        {
            return AppHttpClientFactory.CreateClient(TimeSpan.FromSeconds(45), routeKey: "FogLink");
        }

        public static void ReloadNetworkSettings()
        {
            Uri? address = client.BaseAddress;
            HttpClient previous = client;
            client = CreateClient();
            client.BaseAddress = address;
            previous.Dispose();
        }

        public static Uri ServerAddress
        {
            get => client.BaseAddress ?? throw new InvalidOperationException("FogLink server address is not configured.");
            set
            {
                if (!value.IsAbsoluteUri || value.Scheme is not ("http" or "https"))
                    throw new ArgumentException("FogLink server address must be an absolute HTTP or HTTPS URL.", nameof(value));

                var builder = new UriBuilder(value);
                if (value.IsDefaultPort)
                    builder.Port = -1;
                if (!builder.Path.EndsWith('/'))
                    builder.Path += "/";

                HttpClient previous = client;
                client = CreateClient();
                client.BaseAddress = builder.Uri;
                previous.Dispose();
            }
        }

        public static async Task<string> GetEncodedAsync(string url)
        {
            try
            {
                HttpResponseMessage response = await client.GetAsync(
                    $"MegaPrivater/encode?url={WebUtility.UrlEncode(url)}");
                //response.EnsureSuccessStatusCode();

                return await response.Content.ReadAsStringAsync();
            }
            catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                var errorMessage = "Failed to encode url";
                if (ex.HResult == -2147467259)
                    errorMessage = errorMessage + ": No connection to the FogLink server";
                return errorMessage;
            }

        }

        public static async Task<List<FogLinkFile>> GetDecodedAsync(string url)
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"MegaPrivater/decode?encriptedLink={WebUtility.UrlEncode(url)}");
            response.EnsureSuccessStatusCode();

            var nodes = JsonConvert.DeserializeObject<FogLinkFile[]>(await response.Content.ReadAsStringAsync());
            return nodes?.ToList()
                ?? throw new InvalidDataException("FogLink returned an empty or invalid response.");
        }
    }
}
