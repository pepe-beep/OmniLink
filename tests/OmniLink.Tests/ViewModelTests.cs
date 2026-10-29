using System.Collections.Generic;
using System.IO;
using OmniLink.Core;
using OmniLink.Core.Clipboard;
using OmniLink.Core.Events;
using OmniLink.ViewModels;
using Xunit;

namespace OmniLink.Tests;

public class ViewModelTests
{
    private class TestViewModel : ViewModelBase
    {
        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
    }

    [Fact]
    public void ViewModelBase_SetProperty_NotifiesPropertyChanged()
    {
        var vm = new TestViewModel();
        var changedProps = new List<string>();
        vm.PropertyChanged += (s, e) => changedProps.Add(e.PropertyName!);

        vm.Name = "OmniLink";

        Assert.Single(changedProps);
        Assert.Equal("Name", changedProps[0]);
        Assert.Equal("OmniLink", vm.Name);

        // Setting same value should not fire event
        vm.Name = "OmniLink";
        Assert.Single(changedProps);
    }

    [Fact]
    public void HistoryViewModel_AddAndClear_UpdatesEntries()
    {
        // C2: store จริง persist ที่ runstate — test ต้อง isolate ด้วย temp dir
        var dir = Path.Combine(Path.GetTempPath(), "omnilink-vmtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var vm = new HistoryViewModel(new ClipboardHistoryStore(dir));
        Assert.Equal(0, vm.TotalCount);

        vm.AddEntry("text/plain", "Sample clip 1", null, "node-1");
        vm.AddEntry("text/plain", "Sample clip 2", null, "node-2");

        Assert.Equal(2, vm.TotalCount);
        Assert.Equal("Sample clip 2", vm.Entries[0].PreviewText);

        vm.Clear();
        Assert.Equal(0, vm.TotalCount);
        Assert.Empty(vm.Entries);
    }
}
