using System.IO;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core.Revit;
using RevitAgent.Host.Services;
using RevitAgent.Host.ViewModels;
using RevitAgent.Host.Views;

namespace RevitAgent.Tests.Revit;

/// <summary>Dashboard page "Add-in được phép": view model (the only writer of the allowlist) and view loading.</summary>
public class AddinAllowlistViewModelTests
{
    private static AddinAllowlistViewModel Create(out AddinAllowlistStore store, out string home)
    {
        var sp = TestGateway.BuildProvider(out var paths);
        home = paths.Root;
        store = sp.GetRequiredService<AddinAllowlistStore>();
        return new AddinAllowlistViewModel(store, new LocalizationService());
    }

    [Theory]
    [InlineData(@"D:\Work\MyProject\wt-ticket-1\Outcome\MyAddin.dll", 2, @"D:\Work\MyProject\*\Outcome\MyAddin.dll")]
    [InlineData(@"D:\Work\MyProject\wt-ticket-1\Outcome\MyAddin.dll", 0, @"D:\*\MyProject\wt-ticket-1\Outcome\MyAddin.dll")]
    [InlineData(@"D:\Work\MyProject\wt-ticket-1\Outcome\MyAddin.dll", -1, @"D:\Work\MyProject\wt-ticket-1\Outcome\MyAddin.dll")]
    [InlineData(@"D:\Work\MyProject\wt-ticket-1\Outcome\MyAddin.dll", 9, @"D:\Work\MyProject\wt-ticket-1\Outcome\MyAddin.dll")]
    public void BuildPattern_ReplacesOneFolderWithStar(string path, int index, string expected) =>
        Assert.Equal(expected, AddinAllowlistViewModel.BuildPattern(path, index));

    [Fact]
    public void FolderSegments_AreEmpty_ForPatternsAndRelativePaths()
    {
        Assert.Empty(AddinAllowlistViewModel.FolderSegmentsOf(@"D:\Gits\*\x.dll"));
        Assert.Empty(AddinAllowlistViewModel.FolderSegmentsOf("x.dll"));
        Assert.Equal(new[] { "Gits", "X" }, AddinAllowlistViewModel.FolderSegmentsOf(@"D:\Gits\X\x.dll"));
    }

    [Fact]
    public void AddEditToggleRemove_PersistToTheFile()
    {
        var vm = Create(out var store, out _);
        vm.ConfirmRemove = _ => true;
        vm.EditName = "MyAddin.App";
        vm.EditPath = @"D:\Work\MyProject\wt-ticket-1\Outcome\MyAddin.dll";
        vm.EditNote = "dev build";
        vm.UseFolderPattern = true;

        // default: the folder holding the file's own folder (the worktree) becomes *
        Assert.Equal(@"D:\Work\MyProject\*\Outcome\MyAddin.dll", vm.PatternPreview);
        vm.SaveCommand.Execute(null);

        Assert.False(vm.MessageIsError, vm.Message);
        var item = Assert.Single(vm.Items);
        var saved = Assert.Single(store.Load().Entries);
        Assert.Equal(@"D:\Work\MyProject\*\Outcome\MyAddin.dll", saved.PathPattern);
        Assert.Equal("dev build", saved.Note);
        Assert.True(saved.Enabled);
        Assert.Equal(string.Empty, vm.EditName); // form cleared

        item.IsEnabled = false;
        Assert.False(Assert.Single(store.Load().Entries).Enabled);

        vm.EditCommand.Execute(item);
        Assert.True(vm.IsEditing);
        Assert.Equal("MyAddin.App", vm.EditName);
        Assert.Equal(saved.PathPattern, vm.PatternPreview);
        vm.EditName = "Renamed.App";
        vm.SaveCommand.Execute(null);
        var updated = Assert.Single(store.Load().Entries);
        Assert.Equal("Renamed.App", updated.Name);
        Assert.Equal(saved.Id, updated.Id);
        Assert.False(updated.Enabled);

        vm.RemoveCommand.Execute(Assert.Single(vm.Items));
        Assert.Empty(store.Load().Entries);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Save_RejectsEmptyNameAndDangerousPattern_AndWritesNothing()
    {
        var vm = Create(out var store, out _);
        vm.EditPath = @"D:\Gits\X\a.dll";
        vm.SaveCommand.Execute(null);
        Assert.True(vm.MessageIsError);

        vm.EditName = "A";
        vm.EditPath = "**";
        vm.SaveCommand.Execute(null);
        Assert.True(vm.MessageIsError);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void Remove_Cancelled_KeepsTheEntry_AndBrowseFillsThePath()
    {
        var vm = Create(out var store, out _);
        store.Add(new AddinAllowlistEntry { Name = "A", PathPattern = @"D:\Gits\X\a.dll" });
        vm.Reload();
        vm.ConfirmRemove = _ => false;
        vm.RemoveCommand.Execute(Assert.Single(vm.Items));
        Assert.Single(store.Load().Entries);

        vm.BrowseFile = () => @"C:\Tools\B.dll";
        vm.BrowseCommand.Execute(null);
        Assert.Equal(@"C:\Tools\B.dll", vm.EditPath);
    }

    [Fact]
    public void CorruptFile_IsReported_AndNotOverwritten()
    {
        var vm = Create(out var store, out _);
        File.WriteAllText(store.FilePath, "{ nope");
        vm.Reload();
        Assert.NotNull(vm.FileError);
        vm.EditName = "A";
        vm.EditPath = @"D:\Gits\X\a.dll";
        vm.SaveCommand.Execute(null);
        Assert.True(vm.MessageIsError);
        Assert.Equal("{ nope", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void View_LoadsWithoutExceptions()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new AddinAllowlistView();
                Assert.IsAssignableFrom<UserControl>(view);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }
}
