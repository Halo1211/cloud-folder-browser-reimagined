namespace CG.Web.MegaApiClient
{
    using CG.Web.MegaApiClient.Serialization;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.Serialization;
    using System.Security.Cryptography;

    [DebuggerDisplay("NodeInfo - Type: {Type} - Name: {Name} - Id: {Id}")]
  internal class NodeInfo : INodeInfo
  {
    protected NodeInfo()
    {
    }

    internal NodeInfo(string id, DownloadUrlResponse downloadResponse, byte[] key)
    {
      this.Id = id;
      this.Attributes = Crypto.DecryptAttributes(downloadResponse.SerializedAttributes.FromBase64(), key);
      this.Size = downloadResponse.Size;
      this.Type = NodeType.File;
    }

    [JsonIgnore]
    public string Name
    {
      get { return this.Attributes?.Name; }
    }

    [JsonProperty("s")]
    public long Size { get; protected set; }

    [JsonProperty("t")]
    public NodeType Type { get; protected set; }

    [JsonProperty("h")]
    public string Id { get; private set; }

    [JsonIgnore]
    public DateTime? ModificationDate
    {
      get { return this.Attributes?.ModificationDate; }
    }

    [JsonIgnore]
    public Attributes Attributes { get; protected set; }

    #region Equality

    public bool Equals(INodeInfo other)
    {
      return other != null && this.Id == other.Id;
    }

    public override int GetHashCode()
    {
      return this.Id.GetHashCode();
    }

    public override bool Equals(object obj)
    {
      return this.Equals(obj as INodeInfo);
    }

    #endregion
  }

  [DebuggerDisplay("Node - Type: {Type} - Name: {Name} - Id: {Id}")]
  internal class Node : NodeInfo, INode, INodeCrypto
  {
    private byte[] masterKey;
    private List<SharedKey> sharedKeys;
    private string selectedKeyHandle;

    public Node(byte[] masterKey, ref List<SharedKey> sharedKeys)
    {
      this.masterKey = masterKey;
      this.sharedKeys = sharedKeys;
    }

    #region Public properties

    [JsonProperty("p")]
    public string ParentId { get; private set; }

    [JsonProperty("u")]
    public string Owner { get; private set; }

    [JsonProperty("su")]
    public string SharingId { get; set; }

    [JsonProperty("sk")]
    public string SharingKey { get; set; }

    [JsonIgnore]
    public DateTime CreationDate { get; private set; }

    [JsonIgnore]
    public byte[] Key { get; private set; }

    [JsonIgnore]
    public byte[] FullKey { get; private set; }

    [JsonIgnore]
    public byte[] SharedKey { get; private set; }

    [JsonIgnore]
    public byte[] Iv { get; private set; }

    [JsonIgnore]
    public byte[] MetaMac { get; private set; }

    #endregion

    #region Deserialization

    [JsonProperty("ts")]
    private long SerializedCreationDate { get; set; }

    [JsonProperty("a")]
    private string SerializedAttributes { get; set; }

    [JsonProperty("k")]
    private string SerializedKey { get; set; }

    [OnDeserialized]
    public void OnDeserialized(StreamingContext ctx)
    {
      // Add key from incoming sharing.
      if (this.SharingKey != null && this.sharedKeys?.Any(x => x.Id == this.Id) != true)
      {
        this.sharedKeys ??= new List<SharedKey>();
        this.sharedKeys.Add(new SharedKey(this.Id, this.SharingKey));
      }

      this.CreationDate = this.SerializedCreationDate.ToDateTime();

      if (this.Type == NodeType.File || this.Type == NodeType.Directory)
      {
        if (string.IsNullOrWhiteSpace(this.SerializedKey))
        {
          throw new InvalidDataException($"MEGA node '{this.Id}' has no encrypted key.");
        }

        // Shared nodes can have encrypted keys for multiple owners.
        foreach (string serializedKey in this.SerializedKey.Split('/'))
        {
          try
          {
            int splitPosition = serializedKey.IndexOf(':');
            if (splitPosition < 0)
            {
              continue;
            }

            string handle = serializedKey.Substring(0, splitPosition);
            byte[] encryptedKey = serializedKey.Substring(splitPosition + 1).FromBase64();
            int expectedLength = this.Type == NodeType.File ? 32 : 16;
            if (encryptedKey.Length != expectedLength)
            {
              continue;
            }

            SharedKey sharedKey = this.sharedKeys?.FirstOrDefault(x => x.Id == handle);
            byte[] keyForEntry = sharedKey == null
              ? this.masterKey
              : Crypto.DecryptKey(sharedKey.Key.FromBase64(), this.masterKey);
            byte[] fullKey = Crypto.DecryptKey(encryptedKey, keyForEntry);
            byte[] nodeKey;
            byte[] iv = null;
            byte[] metaMac = null;
            if (this.Type == NodeType.File)
            {
              Crypto.GetPartsFromDecryptedKey(fullKey, out iv, out metaMac, out nodeKey);
            }
            else
            {
              nodeKey = fullKey;
            }

            Attributes attributes = Crypto.DecryptAttributes(this.SerializedAttributes.FromBase64(), nodeKey);
            this.FullKey = fullKey;
            this.Key = nodeKey;
            this.Iv = iv;
            this.MetaMac = metaMac;
            this.Attributes = attributes;
            this.selectedKeyHandle = handle;
            if (sharedKey != null)
            {
              this.SharedKey = this.Type == NodeType.Directory ? keyForEntry : fullKey;
            }
            return;
          }
          catch (Exception ex) when (ex is FormatException || ex is ArgumentException
            || ex is CryptographicException || ex is JsonException || ex is InvalidDataException)
          {
            // Try the next key supplied for this node.
          }
        }

        throw new InvalidDataException(
          $"Unable to decrypt attributes for MEGA node '{this.Id}' with any available key.");
      }
    }

    #endregion

    public bool IsShareRoot
    {
      get
      {
        return this.selectedKeyHandle == this.Id;
      }
    }
  }

  [DebuggerDisplay("PublicNode - Type: {Type} - Name: {Name} - Id: {Id}")]
  public class PublicNode : INode, INodeCrypto
  {
    private readonly Node node;

    public PublicNode(INode node, string shareId)
    {
      this.node = node as Node
        ?? throw new ArgumentException("PublicNode can only wrap a MEGA node returned by this client.", nameof(node));
      this.ShareId = shareId;
    }

    public string ShareId { get; }

    public bool Equals(INodeInfo other)
    {
      return this.node.Equals(other) && this.ShareId == (other as PublicNode)?.ShareId;
    }

    #region Forward

    public long Size { get { return this.node.Size; } }
    public string Name { get { return this.node.Name; } }
    public DateTime? ModificationDate { get { return this.node.ModificationDate; } }
    public string Id { get { return this.node.Id; } }
    public string ParentId { get { return this.node.IsShareRoot ? null : this.node.ParentId; } }
    public string Owner { get { return this.node.Owner; } }
    public NodeType Type { get { return this.node.IsShareRoot && this.node.Type == NodeType.Directory ? NodeType.Root : this.node.Type; } }
    public DateTime CreationDate { get { return this.node.CreationDate; } }

    public byte[] Key { get { return this.node.Key; } }
    public byte[] SharedKey { get { return this.node.SharedKey; } }
    public byte[] Iv { get { return this.node.Iv; } }
    public byte[] MetaMac { get { return this.node.MetaMac; } }
    public byte[] FullKey { get { return this.node.FullKey; } }




    #endregion
  }
}
