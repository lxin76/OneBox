using ClipboardFloat.App.ViewModels;
using ClipboardFloat.Core;

namespace ClipboardFloat.Tests;

public class FloatingViewModelTests
{
    private static FloatingViewModel Vm(int capacity = 10)
        => new(new Settings { MaxHistory = capacity });

    private static ClipItem TextItem(string text)
        => ClipItem.Create(ClipKind.Text, text, null, null, null, null, text);

    [Fact]
    public void AddItem_InsertsAtFront()
    {
        var vm = Vm();
        vm.AddItem(TextItem("a"));
        vm.AddItem(TextItem("b"));
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("b", vm.Items[0].Preview);
        Assert.Equal(2, vm.VisibleCount);
    }

    [Fact]
    public void SearchText_FiltersItems()
    {
        var vm = Vm();
        vm.AddItem(TextItem("hello world"));
        vm.AddItem(TextItem("goodbye moon"));

        vm.SearchText = "hello";
        Assert.Single(vm.Items);
        Assert.Equal("hello world", vm.Items[0].Preview);
        Assert.Equal(1, vm.VisibleCount);

        vm.SearchText = "";
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(2, vm.VisibleCount);
    }

    [Fact]
    public void SearchText_MatchIsCaseInsensitive()
    {
        var vm = Vm();
        vm.AddItem(TextItem("HttpStatusCode 200"));
        vm.SearchText = "httpstatus";
        Assert.Single(vm.Items);
    }

    [Fact]
    public void ClearHistory_EmptiesItems()
    {
        var vm = Vm();
        vm.AddItem(TextItem("a"));
        vm.ClearHistoryCommand.Execute(null);
        Assert.Empty(vm.Items);
        Assert.Equal(0, vm.VisibleCount);
    }

    [Fact]
    public void Delete_RemovesSingleItem()
    {
        var vm = Vm();
        vm.AddItem(TextItem("a"));
        vm.AddItem(TextItem("b"));
        vm.DeleteCommand.Execute(vm.Items[1]);
        Assert.Single(vm.Items);
        Assert.Equal("b", vm.Items[0].Preview);
    }

    [Fact]
    public void AddItem_Duplicate_MovesToFront()
    {
        var vm = Vm();
        vm.AddItem(TextItem("x"));
        vm.AddItem(TextItem("y"));
        vm.AddItem(TextItem("x"));
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("x", vm.Items[0].Preview);
    }

    [Fact]
    public void CapacityTrimming_DeletesFromPersister()
    {
        var persister = new FakePersister();
        var vm = new FloatingViewModel(new Settings { MaxHistory = 2 }, persister);
        var one = TextItem("1");
        vm.AddItem(one);
        vm.AddItem(TextItem("2"));
        vm.AddItem(TextItem("3")); // 触发裁剪，删除最旧的 "1"

        Assert.Equal(3, persister.Saved.Count);          // 3 条都曾保存
        Assert.Equal(new[] { one.Hash }, persister.Deleted); // 裁剪时按哈希删除 "1"
    }

    [Fact]
    public void DeleteAndClear_SyncToPersister()
    {
        var persister = new FakePersister();
        var vm = new FloatingViewModel(new Settings { MaxHistory = 10 }, persister);
        var a = TextItem("a");
        vm.AddItem(a);
        vm.AddItem(TextItem("b"));

        vm.DeleteCommand.Execute(vm.Items[1]); // 删除 a
        Assert.Single(persister.Deleted);
        Assert.Equal(a.Hash, persister.Deleted[0]);

        persister.Deleted.Clear();
        vm.ClearHistoryCommand.Execute(null);
        Assert.True(persister.Cleared);
        Assert.Empty(vm.Items);
    }

    private sealed class FakePersister : IHistoryPersister
    {
        public List<string> Saved { get; } = new();
        public List<string> Deleted { get; } = new();
        public bool Cleared { get; private set; }

        public void SaveAdded(ClipItem item) => Saved.Add(item.Text ?? "");
        public void Delete(string hash) => Deleted.Add(hash);
        public void Clear() => Cleared = true;
    }
}
