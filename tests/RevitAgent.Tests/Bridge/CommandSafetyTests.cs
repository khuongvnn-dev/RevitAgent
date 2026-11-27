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
    [InlineData(" ID_FILE_SAVE")]
    public void Dangerous_commands_are_blocked(string command) =>
        Assert.NotNull(CommandSafety.BlockReason(command));

    /// <summary>
    /// Enum.TryParse (what older Bridges use) reads "Wall,Door" as Wall|Door and "+12" as the value 12, so a harmless-looking
    /// text can resolve to a blocked PostableCommand (review finding 2026-11-27).
    /// </summary>
    [Theory]
    [InlineData("Wall,Door")]
    [InlineData("Wall, Save")]
    [InlineData("Wall Door")]
    [InlineData("Wall\tDoor")]
    [InlineData("+12")]
    [InlineData("-12")]
    [InlineData("12")]
    public void Comma_lists_inner_whitespace_and_numbers_are_blocked(string command)
    {
        var reason = CommandSafety.BlockReason(command);
        Assert.NotNull(reason);
        Assert.Contains("exactly one", reason);
    }

    [Theory]
    [InlineData("ScheduleOrQuantities")]
    [InlineData("Wall")]
    [InlineData("  Wall  ")]
    [InlineData("CloseInactiveViews")]
    [InlineData("ID_OBJECTS_WALL")]
    [InlineData("ID_VIEW_NEW_SCHEDULE")]
    [InlineData("CustomCtrl_%CustomCtrl_%Acme%Tools%Save Config")]
    [InlineData("CustomCtrl_%x%y")]
    [InlineData("CustomCtrl_%Acme, Inc%Tools%Run 1")]
    [InlineData("")]
    public void Normal_and_addin_commands_are_allowed(string command) =>
        Assert.Null(CommandSafety.BlockReason(command));

    /// <summary>The id Revit resolved is checked by its fragments whatever its prefix (the Bridge's second check).</summary>
    [Theory]
    [InlineData("ID_FILE_SAVE")]
    [InlineData("ID_REVIT_FILE_SAVE_AS")]
    [InlineData("ID_FILE_SAVE_TO_CENTRAL")]
    [InlineData("ID_APP_EXIT")]
    [InlineData("ID_REVIT_FILE_CLOSE")]
    [InlineData("SAVE_TO_CENTRAL")] // no ID_ prefix: the fragments still apply
    [InlineData(" id_file_save ")]
    public void Resolved_dangerous_ids_are_blocked(string commandId) =>
        Assert.NotNull(CommandSafety.ResolvedIdBlockReason(commandId));

    [Theory]
    [InlineData("ID_OBJECTS_WALL")]
    [InlineData("ID_VIEW_NEW_SCHEDULE")]
    [InlineData("CustomCtrl_%CustomCtrl_%Acme%Tools%Save Config")]
    [InlineData("")]
    [InlineData(null)]
    public void Resolved_normal_and_addin_ids_are_allowed(string? commandId) =>
        Assert.Null(CommandSafety.ResolvedIdBlockReason(commandId));
}
