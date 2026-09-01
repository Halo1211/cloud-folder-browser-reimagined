using System;
using Newtonsoft.Json;
using YandexDiskSharp.Models;
using CG.Web.MegaApiClient;
using CloudFolderBrowser.Sync;

namespace CloudFolderBrowser
{
    public class CloudFile
    {
        [JsonConstructor]
        public CloudFile(string Name, DateTime Created, DateTime Modified, long Size)
        {
            this.Name = Name;
            this.Created = Created;
            this.Modified = Modified;
            Path = "";
            this.Size = Size;
        }

        public CloudFile(Resource r)
        {
            Name = r.Name;
            Created = r.Created;
            PublicUrl = r.PublicUrl;
            Modified = r.Modified;
            Path = r.Path;
            Size = r.Size;
        }

        public string Name;
        public DateTime Created;
        public DateTime Modified;
        public long Size;
        public bool HasKnownSize { get; set; } = true;
        public Uri PublicUrl { get; set; }
        public string EncryptedUrl { get; set; }
        public string Path { get; set; }

        /// <summary>
        /// True when PublicUrl is a share page that must be converted to a
        /// temporary direct link by a configured debrid/download resolver.
        /// </summary>
        public bool RequiresLinkResolver { get; set; }

        [JsonIgnore]
        public SyncPlanAction PlannedAction { get; set; } = SyncPlanAction.Download;

        [JsonIgnore]
        public string LocalNameOverride { get; set; } = string.Empty;

        [JsonIgnore]
        public string LocalSavePathOverride { get; set; } = string.Empty;

        [JsonIgnore]
        public Guid? DownloadHistoryId { get; set; }

        [JsonIgnore]
        public int DownloadPriority { get; set; }

        [JsonIgnore]
        public INode MegaNode { get; set; }

    }

}
