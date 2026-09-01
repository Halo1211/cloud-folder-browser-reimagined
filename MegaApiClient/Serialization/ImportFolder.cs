namespace CG.Web.MegaApiClient.Serialization
{
  using System.Collections.Generic;
  using Newtonsoft.Json;

  internal class ImportFolderNodeRequest : RequestBase
  {
    private ImportFolderNodeRequest(string parentNodeId)
      : base("p")
    {
      Nodes = new List<ImportFolderNodeRequestData>();
      ParentId = parentNodeId;
    }

    [JsonProperty("t")]
    public string ParentId { get; private set; }

    [JsonProperty("n")]
    public List<ImportFolderNodeRequestData> Nodes { get; private set; }

    public static ImportFolderNodeRequest ImportFolderRequest(string parentNodeId)
    {
      return new ImportFolderNodeRequest(parentNodeId);
    }

    internal class ImportFolderNodeRequestData
    {
      [JsonProperty("h")]
      public string PublicLinkId { get; set; }

      [JsonProperty("t")]
      public NodeType Type { get; set; }

      [JsonProperty("a")]
      public string Attributes { get; set; }

      [JsonProperty("k")]
      public string Key { get; set; }
    }

    internal class ImportFolderFileNodeRequestData : ImportFolderNodeRequestData
    {
      [JsonProperty("p")]
      public string ParentId { get; set; }
    }
  }
}
