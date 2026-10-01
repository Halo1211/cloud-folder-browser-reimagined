using Newtonsoft.Json;
using CG.Web.MegaApiClient;

namespace CloudFolderBrowser.Networking;

internal static class MegaSessionTokenCodec
{
    internal static MegaApiClient.LogonSessionToken? Deserialize(string json)
    {
        MegaApiClient.LogonSessionToken? token =
            JsonConvert.DeserializeObject<MegaApiClient.LogonSessionToken>(json);
        if (token != null && (string.IsNullOrWhiteSpace(token.SessionId) || token.MasterKey?.Length != 16))
            throw new JsonSerializationException("The stored MEGA session token is incomplete.");
        return token;
    }
}
