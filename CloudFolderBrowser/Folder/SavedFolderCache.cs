using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace CloudFolderBrowser;

internal static class SavedFolderCache
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        TypeNameHandling = TypeNameHandling.Auto,
        SerializationBinder = new CloudFolderOnlyBinder()
    };

    internal static string Serialize(CloudFolder folder) => JsonConvert.SerializeObject(folder, Settings);

    internal static CloudFolder? Deserialize(string json)
    {
        CloudFolder? root = JsonConvert.DeserializeObject<CloudFolder>(json, Settings);
        if (root == null)
            return null;

        var pending = new Stack<CloudFolder>();
        var visited = new HashSet<CloudFolder>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0)
        {
            CloudFolder folder = pending.Pop();
            if (!visited.Add(folder) || folder.Files == null || folder.Subfolders == null
                || folder.Files.Any(file => file == null))
                throw new JsonSerializationException("Saved folder data contains an invalid folder tree.");
            foreach (IFolder? child in folder.Subfolders)
            {
                if (child is not CloudFolder cloudChild)
                    throw new JsonSerializationException("Saved folder data contains an invalid folder tree.");
                pending.Push(cloudChild);
            }
        }
        return root;
    }

    private sealed class CloudFolderOnlyBinder : ISerializationBinder
    {
        private static readonly string AssemblyName = typeof(CloudFolder).Assembly.GetName().Name!;
        private static readonly string TypeName = typeof(CloudFolder).FullName!;

        public Type BindToType(string? assemblyName, string typeName)
        {
            if (assemblyName == AssemblyName && typeName == TypeName)
                return typeof(CloudFolder);
            throw new JsonSerializationException("Saved folder data contains an unsupported type.");
        }

        public void BindToName(Type serializedType, out string? assemblyName, out string? typeName)
        {
            if (serializedType != typeof(CloudFolder))
                throw new JsonSerializationException("Saved folder data contains an unsupported type.");
            assemblyName = AssemblyName;
            typeName = TypeName;
        }
    }
}
