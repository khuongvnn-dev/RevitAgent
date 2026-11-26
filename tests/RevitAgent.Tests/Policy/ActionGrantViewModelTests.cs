using RevitAgent.Core.Policy;
using RevitAgent.Host.Models;
using RevitAgent.Host.Services;
using RevitAgent.Host.ViewModels;

namespace RevitAgent.Tests.Policy;

public sealed class ActionGrantViewModelTests
{
    private static RevitInstanceItem Item(int pid, bool launched) => new() { Pid = pid, YearText = "2024", Title = "P", LaunchedByGateway = launched };

    [Fact]
    public void Rows_ListOnlyForeignRevit_GrantAndRevokeWork_AndGoneRevitLosesItsGrant()
    {
        var store = new ActionGrantStore();
        var vm = new ActionGrantViewModel(store, new LocalizationService());
        vm.Update(new[] { Item(10, false), Item(11, true) });
        Assert.Equal(new int?[] { null, 10 }, vm.Rows.Select(r => r.Pid).ToArray());

        vm.Rows[1].GrantCommand.Execute(null);
        Assert.True(vm.Rows[1].IsGranted);
        Assert.NotNull(store.FindActive(10));
        Assert.NotEmpty(vm.Rows[1].Countdown);

        vm.Rows[1].RevokeCommand.Execute(null);
        Assert.Null(store.FindActive(10));

        store.Grant(GrantScope.Pid, 10, null);
        vm.Update(Array.Empty<RevitInstanceItem>());
        Assert.Null(store.FindActive(10));
    }
}
