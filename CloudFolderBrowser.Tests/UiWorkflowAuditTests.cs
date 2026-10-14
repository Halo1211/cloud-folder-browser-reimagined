using System.Reflection;
using System.Runtime.ExceptionServices;
using Aga.Controls.Tree;
using CloudFolderBrowser.Accounts;

namespace CloudFolderBrowser.Tests;

[Collection("Real download integration")]
public sealed class UiWorkflowAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-ui-audit-" + Guid.NewGuid().ToString("N"));
    public UiWorkflowAuditTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SyncResults_ToggleFlatViewPreservesRootFileSelection()
    {
        RunOnUiThread(form =>
        {
            ColumnNode root = (ColumnNode)Field<TreeModel>(form, "newFiles_model").Nodes[0];
            ColumnNode file = (ColumnNode)root.Nodes[0];
            Assert.True(file.IsChecked);
            Field<CheckBox>(form, "flatList2_checkBox").Checked = true;
            ColumnNode flatRoot = (ColumnNode)Field<TreeModel>(form, "newFilesFlat_model").Nodes[0];
            Assert.True(((ColumnNode)flatRoot.Nodes[0]).IsChecked);
            ((ColumnNode)flatRoot.Nodes[0]).IsChecked = false;
            Field<CheckBox>(form, "flatList2_checkBox").Checked = false;
            Assert.False(file.IsChecked);
        });
    }

    [Fact]
    public void SyncResults_RootSizeCountsEverySelectedFileExactlyOnce()
    {
        RunOnUiThread(form =>
        {
            ColumnNode root = (ColumnNode)Field<TreeModel>(form, "newFiles_model").Nodes[0];
            ColumnNode flat = (ColumnNode)Field<TreeModel>(form, "newFilesFlat_model").Nodes[0];
            Assert.Equal(12, root.Size);
            Assert.Equal(12, flat.Size);
            Assert.Equal(7, ((ColumnNode)root.Nodes[1]).Size);
        });
    }

    [Fact]
    public void SyncResults_PartialSelectionSupportsFoldersWithoutProviderTreeMetadata()
    {
        RunOnUiThread(form =>
        {
            ColumnNode root = (ColumnNode)Field<TreeModel>(form, "newFiles_model").Nodes[0];
            root.CheckState = CheckState.Indeterminate;
            ((ColumnNode)root.Nodes[0]).IsChecked = false;
            typeof(SyncFilesForm).GetField("checkedFiles", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(form, new List<CloudFile>());
            typeof(SyncFilesForm).GetMethod("GetCheckedFiles", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, new object[] { root });
            Assert.Equal("child.bin", Assert.Single(Field<List<CloudFile>>(form, "checkedFiles")).Name);
        });
    }

    [Fact]
    public void OctoberAudit_SyncResultsDoNotReuseShareCredentialsForGenericDownloads()
    {
        RunOnUiThread(form => Assert.Null(typeof(SyncFilesForm)
            .GetField("NetworkCredential", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)),
            model => model.WebdavCredential = new System.Net.NetworkCredential("old-share", "fixture-password"));
    }

    [Theory]
    [InlineData("a+b.bin")]
    [InlineData("report%20final.bin")]
    public void OctoberAudit_MainTreeDisplaysLiteralCloudNames(string name)
    {
        RunOnUiThread(form =>
        {
            var folder = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0);
            folder.AddFile(new CloudFile(name, DateTime.MinValue, DateTime.MinValue, 1) { Path = "/" + Uri.EscapeDataString(name) });
            var node = new ColumnNode("root", DateTime.MinValue, DateTime.MinValue, 1) { Tag = folder };
            form.HostMainForm.cloudFlatFolder_model = new TreeModel();
            form.HostMainForm.BuildSubfolderNodes(node);
            Assert.Equal(name, Assert.Single(node.Nodes).Text);
        });
    }

    [Fact]
    public void OctoberAudit_MainTreeDisplaysLiteralLocalNames()
    {
        string path = Path.Combine(_root, "local+report%20.bin");
        File.WriteAllText(path, "fixture");
        RunOnUiThread(form =>
        {
            var folder = new LocalFolder { Name = "root", Path = _root };
            folder.TryAddScannedFile(new FileInfo(path));
            var node = new ColumnNode("root", DateTime.MinValue, DateTime.MinValue, 1) { Tag = folder };
            form.HostMainForm.BuildSubfolderNodes(node);
            Assert.Equal(Path.GetFileName(path), Assert.Single(node.Nodes).Text);
        });
    }

    private void RunOnUiThread(Action<SyncFilesForm> check, Action<MainFormModel>? configure = null)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            string? originalSnapshot = Environment.GetEnvironmentVariable("CFB_UI_SNAPSHOT");
            try
            {
                Environment.SetEnvironmentVariable("CFB_UI_SNAPSHOT", "1");
                var root = new CloudFolder("fixture", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
                root.AddFile(new CloudFile("root.bin", DateTime.MinValue, DateTime.MinValue, 5) { Path = "/root.bin" });
                root.AddFile(new CloudFile("child.bin", DateTime.MinValue, DateTime.MinValue, 7) { Path = "/child/child.bin" });
                root.SizeTopDirectoryOnly = 12;
                root.CalculateFolderSize();
                var model = new MainFormModel { CloudServiceType = CloudServiceType.Other, CloudPublicFolder = root,
                    AllFolders = new() { root } };
                configure?.Invoke(model);
                using var parent = new MainForm();
                using var form = new SyncFilesForm(parent, root, model, _root,
                    new CloudAccountStore(Path.Combine(_root, "accounts.json")));
                try { check(form); }
                finally { form.CloseForm(); }
            }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            finally { Environment.SetEnvironmentVariable("CFB_UI_SNAPSHOT", originalSnapshot); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "UI workflow did not complete.");
        failure?.Throw();
    }

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    public void Dispose() => Directory.Delete(_root, true);
}
