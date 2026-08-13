using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests;

public class ExploreViewModelTests
{
    [Fact]
    public void Constructor_PopulatesDefaultSources()
    {
        var vm = new ExploreViewModel();
        Assert.Equal(4, vm.Sources.Count);
        Assert.Contains(vm.Sources, s => s.Name.Contains("BluesYoung"));
        Assert.Contains(vm.Sources, s => s.Name.Contains("Hiddify"));
    }
}
