using CG.Web.MegaApiClient;
using CG.Web.MegaApiClient.Serialization;
using Newtonsoft.Json;

namespace CloudFolderBrowser.Tests;

public sealed class MegaNodeRegressionTests
{
    [Theory]
    [InlineData("https://mega.nz/folder/sampleShareId#AQIDBAUGBwgJCgsMDQ4PEA", "")]
    [InlineData("https://mega.nz/folder/sampleShareId#AQIDBAUGBwgJCgsMDQ4PEA/folder/child1", "child1")]
    [InlineData("https://mega.nz/folder/sampleShareId#AQIDBAUGBwgJCgsMDQ4PEA/file/file1", "file1")]
    public void FolderLink_ParsesSelectedNodeWithoutReadingFromTheNetwork(string link, string expectedId)
    {
        var client = new MegaApiClient();
        client.GetPartsFromUri(new Uri(link), out string shareId, out _, out _,
            out byte[] key, out string selectedId);

        Assert.Equal("sampleShareId", shareId);
        Assert.Equal(expectedId, selectedId);
        Assert.Equal(16, key.Length);
    }

    [Fact]
    public void FileLink_ParsesFileKeyWithoutReadingFromTheNetwork()
    {
        byte[] fullKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var client = new MegaApiClient();

        client.GetPartsFromUri(new Uri($"https://mega.nz/file/file1#{fullKey.ToBase64()}"),
            out string fileId, out byte[] iv, out byte[] metaMac, out byte[] key);

        Assert.Equal("file1", fileId);
        Assert.Equal(8, iv.Length);
        Assert.Equal(8, metaMac.Length);
        Assert.Equal(16, key.Length);
    }

    [Fact]
    public void WrongAttributeKey_ThrowsInsteadOfCreatingAnErrorNamedFile()
    {
        byte[] actualKey = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        byte[] wrongKey = Enumerable.Range(41, 16).Select(value => (byte)value).ToArray();
        byte[] attributes = Crypto.EncryptAttributes(new Attributes("real-file.txt"), actualKey);

        Assert.Throws<InvalidDataException>(() => Crypto.DecryptAttributes(attributes, wrongKey));
    }

    [Fact]
    public void NodeWithMultipleEncryptedKeys_UsesEntryMatchingPublicShareKey()
    {
        byte[] shareKey = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        byte[] otherKey = Enumerable.Range(41, 16).Select(value => (byte)value).ToArray();
        byte[] nodeKey = Enumerable.Range(81, 16).Select(value => (byte)value).ToArray();
        string attributes = Crypto.EncryptAttributes(new Attributes("real-file.txt"), nodeKey).ToBase64();
        string unrelatedEntry = Crypto.EncryptKey(nodeKey, otherKey).ToBase64();
        string matchingEntry = Crypto.EncryptKey(nodeKey, shareKey).ToBase64();
        string json = JsonConvert.SerializeObject(new[]
        {
            new Dictionary<string, object>
            {
                ["h"] = "node1",
                ["p"] = "root1",
                ["t"] = (int)NodeType.Directory,
                ["ts"] = 1,
                ["a"] = attributes,
                ["k"] = $"unrelated:{unrelatedEntry}/root1:{matchingEntry}"
            }
        });

        List<SharedKey>? sharedKeys = null;
        Node node = Assert.Single(JsonConvert.DeserializeObject<Node[]>(
            json, new NodeConverter(shareKey, ref sharedKeys))!);

        Assert.Equal("real-file.txt", node.Name);
    }

    [Fact]
    public void ShareRootWithMultipleKeys_UsesSelectedKeyHandle()
    {
        byte[] shareKey = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        byte[] otherKey = Enumerable.Range(41, 16).Select(value => (byte)value).ToArray();
        byte[] nodeKey = Enumerable.Range(81, 16).Select(value => (byte)value).ToArray();
        string json = JsonConvert.SerializeObject(new[]
        {
            new Dictionary<string, object>
            {
                ["h"] = "root1",
                ["p"] = "outside",
                ["t"] = (int)NodeType.Directory,
                ["ts"] = 1,
                ["a"] = Crypto.EncryptAttributes(new Attributes("root"), nodeKey).ToBase64(),
                ["k"] = $"unrelated:{Crypto.EncryptKey(nodeKey, otherKey).ToBase64()}/root1:{Crypto.EncryptKey(nodeKey, shareKey).ToBase64()}"
            }
        });

        List<SharedKey>? sharedKeys = null;
        Node node = Assert.Single(JsonConvert.DeserializeObject<Node[]>(
            json, new NodeConverter(shareKey, ref sharedKeys))!);
        var publicNode = new PublicNode(node, "root1");

        Assert.True(node.IsShareRoot);
        Assert.Equal(NodeType.Root, publicNode.Type);
        Assert.Null(publicNode.ParentId);
    }

    [Fact]
    public void SharedRootKey_IsAvailableToFollowingChildrenWhenResponseHasNoKeyList()
    {
        byte[] masterKey = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        byte[] shareKey = Enumerable.Range(41, 16).Select(value => (byte)value).ToArray();
        byte[] rootKey = Enumerable.Range(81, 16).Select(value => (byte)value).ToArray();
        byte[] childKey = Enumerable.Range(121, 16).Select(value => (byte)value).ToArray();
        string json = JsonConvert.SerializeObject(new[]
        {
            new Dictionary<string, object>
            {
                ["h"] = "root1", ["t"] = (int)NodeType.Directory, ["ts"] = 1,
                ["sk"] = Crypto.EncryptKey(shareKey, masterKey).ToBase64(),
                ["k"] = $"root1:{Crypto.EncryptKey(rootKey, shareKey).ToBase64()}",
                ["a"] = Crypto.EncryptAttributes(new Attributes("root"), rootKey).ToBase64()
            },
            new Dictionary<string, object>
            {
                ["h"] = "child1", ["p"] = "root1", ["t"] = (int)NodeType.Directory, ["ts"] = 1,
                ["k"] = $"root1:{Crypto.EncryptKey(childKey, shareKey).ToBase64()}",
                ["a"] = Crypto.EncryptAttributes(new Attributes("child"), childKey).ToBase64()
            }
        });

        List<SharedKey>? sharedKeys = null;
        Node[] nodes = JsonConvert.DeserializeObject<Node[]>(
            json, new NodeConverter(masterKey, ref sharedKeys))!;

        Assert.Equal(new[] { "root", "child" }, nodes.Select(node => node.Name));
    }
}
