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

    private void RunOnUiThread(Action<SyncFilesForm> check)
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
