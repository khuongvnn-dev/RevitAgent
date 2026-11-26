using RevitAgent.Contracts.Bridge;

namespace RevitAgent.Tests.Bridge;

/// <summary>command_post must refuse commands that save, sync, relinquish, reload, close or exit (review finding 2026-11-20).</summary>
public class CommandSafetyTests
{
    [Theory]
    [InlineData("Save")]
    [InlineData("save")]
    [InlineData("SaveAsProject")]
    [InlineData("SaveAsFamily")]
    [InlineData("SynchronizeNow")]
    [InlineData("SynchronizeAndModifySettings")]
    [InlineData("RelinquishAllMine")]
    [InlineData("ReloadLatest")]
    [InlineData("ExitRevit")]
    [InlineData("Close")]
    [InlineData("ID_REVIT_FILE_SAVE")]
    [InlineData("ID_FILE_SAVE_TO_CENTRAL")]
    [InlineData("ID_APP_EXIT")]
    public void Dangerous_commands_are_blocked(string command) =>
        Assert.NotNull(CommandSafety.BlockReason(command));

    [Theory]
    [InlineData("ScheduleOrQuantities")]
    [InlineData("Wall")]
    [InlineData("CloseInactiveViews")]
    [InlineData("ID_OBJECTS_WALL")]
    [InlineData("ID_VIEW_NEW_SCHEDULE")]
    [InlineData("CustomCtrl_%CustomCtrl_%Acme%Tools%Save Config")]
    [InlineData("")]
    public void Normal_and_addin_commands_are_allowed(string command) =>
        Assert.Null(CommandSafety.BlockReason(command));
}
