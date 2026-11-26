using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Revit;

/// <summary>Add-in allowlist: parser, glob matcher, decisions, store round trip, MCP read-only guarantee (no UI, no Revit).</summary>
public class AddinAllowlistTests
{
    private const string Sample =
        "Name:                MyAddin.App\r\n" +
        "Location:            D:\\Work\\MyProject\\wt-ticket-1\\Outcome\\MyAddin.dll";

    private static AddinAllowlistEntry Entry(string name = "MyAddin.App", string pattern = @"D:\Work\MyProject\*\Outcome\MyAddin.dll", bool enabled = true, string? id = null) =>
        new() { Id = id ?? Guid.NewGuid().ToString("D"), Name = name, PathPattern = pattern, Enabled = enabled };

    private static AddinAllowlistSnapshot List(params AddinAllowlistEntry[] e) => new(e);

    // ---- parser --------------------------------------------------------------------------------------------------

    [Fact]
    public void Parser_ReadsNameAndLocation_FromTheRealDialogText()
    {
        var parsed = AddinAllowlistPolicy.ParseDialogText(new[] { "The add-in is not signed.", Sample, "Do you want to load it?" });
        Assert.NotNull(parsed);
        Assert.Equal("MyAddin.App", parsed!.Value.Name);
        Assert.Equal(@"D:\Work\MyProject\wt-ticket-1\Outcome\MyAddin.dll", parsed.Value.Location);
    }

    [Theory]
    [InlineData("Name: A.B\nLocation: C:\\x\\A.dll")]
    [InlineData("Name:A.B\r\nLocation:   C:\\x\\A.dll   \r\n")]
    [InlineData("  name :\tA.B  \r\n  LOCATION :  C:\\x\\A.dll")]
    [InlineData("intro\r\n\r\nName:        A.B\r\nPublisher:   Unknown\r\nLocation:        C:\\x\\A.dll\r\n")]
    public void Parser_ToleratesWhitespaceCrLfAndCase(string text)
    {
        var parsed = AddinAllowlistPolicy.ParseDialogText(new[] { text });
        Assert.NotNull(parsed);
        Assert.Equal("A.B", parsed!.Value.Name);
        Assert.Equal(@"C:\x\A.dll", parsed.Value.Location);
    }

    [Fact]
    public void Parser_WorksWhenEachLineIsASeparateText()
    {
        var parsed = AddinAllowlistPolicy.ParseDialogText(new[] { "Name:  A.B", "Location:  C:\\x\\A.dll" });
        Assert.Equal(("A.B", @"C:\x\A.dll"), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Name: A.B")]
    [InlineData("Location: C:\\x\\A.dll")]
    [InlineData("Name:\r\nLocation: C:\\x\\A.dll")]
    [InlineData("Name: A.B\r\nName: Other\r\nLocation: C:\\x\\A.dll")] // ambiguous
    [InlineData("Tên: A.B\r\nVị trí: C:\\x\\A.dll")] // not understood -> human
    public void Parser_ReturnsNull_WhenMissingOrAmbiguous(string text)
    {
        Assert.Null(AddinAllowlistPolicy.ParseDialogText(new[] { text }));
        Assert.Null(AddinAllowlistPolicy.ParseDialogText(null));
    }

    [Fact]
    public void Title_IsRecognizedExactly()
    {
        Assert.True(AddinAllowlistPolicy.IsUnsignedAddinTitle("Security - Unsigned Add-In"));
        Assert.True(AddinAllowlistPolicy.IsUnsignedAddinTitle("  security - unsigned add-in "));
        Assert.False(AddinAllowlistPolicy.IsUnsignedAddinTitle("Security - Unsigned Add-In (2)"));
        Assert.False(AddinAllowlistPolicy.IsUnsignedAddinTitle("Security"));
        Assert.False(AddinAllowlistPolicy.IsUnsignedAddinTitle(null));
    }

    // ---- matcher -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"D:\Work\MyProject\*\Outcome\MyAddin.dll", @"D:\Work\MyProject\wt-ticket-1\Outcome\MyAddin.dll", true)]
    [InlineData(@"d:\work\myproject\*\outcome\myaddin.DLL", @"D:\Work\MyProject\X\Outcome\MyAddin.dll", true)]   // case-insensitive
    [InlineData(@"D:\Work\MyProject\*\Outcome\MyAddin.dll", @"D:\Work\MyProject\a\b\Outcome\MyAddin.dll", false)]   // * does not cross \
    [InlineData(@"D:\Work\MyProject\**\MyAddin.dll", @"D:\Work\MyProject\a\b\Outcome\MyAddin.dll", true)]          // ** does
    [InlineData(@"D:\Work\MyProject\**\MyAddin.dll", @"D:\Work\MyProject\MyAddin.dll", false)]                      // needs the \ before the file
    [InlineData(@"D:\Work\Acme?\Outcome\MyAddin.dll", @"D:\Work\Acme1\Outcome\MyAddin.dll", true)]
    [InlineData(@"D:\Work\Acme?\Outcome\MyAddin.dll", @"D:\Work\Acme12\Outcome\MyAddin.dll", false)]
    [InlineData(@"D:\Work\Acme?\Outcome\MyAddin.dll", @"D:\Work\Acme\Outcome\MyAddin.dll", false)]
    [InlineData(@"D:\Gits\X\a.dll", @"D:/Gits/X/a.dll", true)]                                                                       // normalized slashes
    [InlineData(@"D:\Gits\X\a.dll", @"D:\Gits\X\sub\..\a.dll", true)]                                                               // .. resolved
    [InlineData(@"D:\Gits\X\a.dll", @"D:\Gits\X\..\Y\a.dll", false)]
    [InlineData(@"D:\Gits\X\a.dll", @"D:\Gits\X\a.dll.evil", false)]                                                                // anchored
    [InlineData(@"D:\Gits\X\a.dll", @"E:\Gits\X\a.dll", false)]
    [InlineData(@"D:\Gits\X\a+b(1).dll", @"D:\Gits\X\a+b(1).dll", true)]                                                            // regex chars are literal
    [InlineData(@"D:\Gits\X\a.dll", @"relative\a.dll", false)]
    public void Matcher_Globs(string pattern, string location, bool expected) =>
        Assert.Equal(expected, AddinAllowlistPolicy.MatchesPattern(pattern, location));

    [Theory]
    [InlineData("")]
    [InlineData("**")]
    [InlineData("*")]
    [InlineData(@"D:\*")]
    [InlineData(@"D:\Gits\**\*")]
    [InlineData(@"*\MyAddin.dll")]
    [InlineData(@"relative\MyAddin.dll")]
    [InlineData(@"D:\Gits\..\Other\MyAddin.dll")]
    public void Pattern_Validation_RejectsDangerousPatterns(string pattern)
    {
        Assert.NotNull(AddinAllowlistPolicy.ValidatePattern(pattern));
        Assert.False(AddinAllowlistPolicy.MatchesPattern(pattern, @"D:\Gits\Whatever\MyAddin.dll"));
    }

    [Fact]
    public void Pattern_Validation_AcceptsDriveAndUncPatterns()
    {
        Assert.Null(AddinAllowlistPolicy.ValidatePattern(@"D:\Work\MyProject\*\Outcome\MyAddin.dll"));
        Assert.Null(AddinAllowlistPolicy.ValidatePattern(@"\\server\share\addins\**\Tool.dll"));
    }

    // ---- decisions -----------------------------------------------------------------------------------------------

    [Fact]
    public void Decide_Listed_IsLoadOnce_WithMatchedEntry()
    {
        var e = Entry(id: "entry-1");
        var d = AddinAllowlistPolicy.Decide(new[] { Sample }, List(e));
        Assert.Equal(AddinTrustAction.LoadOnce, d.Action);
        Assert.Equal("entry-1", d.MatchedEntryId);
        Assert.Equal("CommandButton_1002", d.ButtonId);
        Assert.Equal("MyAddin.App", d.Name);
    }

    [Fact]
    public void Decide_NameIsCaseInsensitive()
    {
        Assert.Equal(AddinTrustAction.LoadOnce, AddinAllowlistPolicy.Decide(new[] { Sample }, List(Entry(name: "myaddin.APP"))).Action);
    }

    [Fact]
    public void Decide_Unlisted_IsDoNotLoad()
    {
        var d = AddinAllowlistPolicy.Decide(new[] { Sample }, List(Entry(name: "Other.App")));
        Assert.Equal(AddinTrustAction.DoNotLoad, d.Action);
        Assert.Null(d.MatchedEntryId);
        Assert.Equal("CommandButton_1003", d.ButtonId);
        Assert.Equal(AddinTrustAction.DoNotLoad, AddinAllowlistPolicy.Decide(new[] { Sample }, AddinAllowlistSnapshot.Empty).Action);
    }

    [Fact]
    public void Decide_WrongPathOrDisabled_IsDoNotLoad()
    {
        Assert.Equal(AddinTrustAction.DoNotLoad, AddinAllowlistPolicy.Decide(new[] { Sample }, List(Entry(pattern: @"D:\Elsewhere\*\MyAddin.dll"))).Action);
        Assert.Equal(AddinTrustAction.DoNotLoad, AddinAllowlistPolicy.Decide(new[] { Sample }, List(Entry(enabled: false))).Action);
    }

    [Fact]
    public void Decide_InvalidPatternEntry_NeverMatches()
    {
        Assert.Equal(AddinTrustAction.DoNotLoad, AddinAllowlistPolicy.Decide(new[] { Sample }, List(Entry(pattern: "**"))).Action);
    }

    [Theory]
    [InlineData("nothing useful here")]
    [InlineData("Name: A.B")]
    public void Decide_ParseFailure_IsNoDecision(string text)
    {
        var d = AddinAllowlistPolicy.Decide(new[] { text }, List(Entry()));
        Assert.Equal(AddinTrustAction.NoDecision, d.Action);
        Assert.Equal(string.Empty, d.ButtonId);
    }

    [Fact]
    public void Decide_RelativeLocation_IsNoDecision() =>
        Assert.Equal(AddinTrustAction.NoDecision, AddinAllowlistPolicy.Decide(new[] { "Name: A\nLocation: a.dll" }, List(Entry(name: "A"))).Action);

    [Fact]
    public void Decide_UnreadableAllowlist_IsNoDecision()
    {
        var d = AddinAllowlistPolicy.Decide(new[] { Sample }, new AddinAllowlistSnapshot(Array.Empty<AddinAllowlistEntry>(), "invalid JSON"));
        Assert.Equal(AddinTrustAction.NoDecision, d.Action);
    }

    // ---- store ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Store_RoundTrip_UsesTheDocumentedSchema_AndIsolatedHome()
    {
        await using var sp = TestGateway.BuildProvider(out var paths);
        var store = sp.GetRequiredService<AddinAllowlistStore>();
        Assert.Equal(Path.Combine(paths.ConfigDir, "addin-allowlist.json"), store.FilePath);
        Assert.Empty(store.Load().Entries);
        Assert.Null(store.Load().Error);

        var added = store.Add(new AddinAllowlistEntry { Name = "MyAddin.App", PathPattern = @"D:\Work\MyProject\*\Outcome\MyAddin.dll", Note = "dev" });
        store.Add(Entry(name: "Second.App", pattern: @"C:\Tools\Second.dll", enabled: false));

        var snap = store.Load();
        Assert.Equal(2, snap.Entries.Count);
        Assert.Equal(added.Id, snap.Entries[0].Id);
        Assert.Equal("dev", snap.Entries[0].Note);
        Assert.False(snap.Entries[1].Enabled);

        using var json = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        var first = json.RootElement.GetProperty("entries")[0];
        foreach (var key in new[] { "id", "name", "path_pattern", "enabled", "note", "added_at" })
        {
            Assert.True(first.TryGetProperty(key, out _), key);
        }

        // The read-only view (what tools and the gate use) sees the same file.
        Assert.Equal(2, sp.GetRequiredService<AddinAllowlist>().Load().Entries.Count);

        Assert.True(store.SetEnabled(added.Id, false));
        Assert.False(store.Load().Entries[0].Enabled);
        Assert.True(store.Update(store.Load().Entries[0] with { Name = "Renamed.App", Enabled = true }));
        Assert.Equal("Renamed.App", store.Load().Entries[0].Name);
        Assert.True(store.Remove(added.Id));
        Assert.False(store.Remove(added.Id));
        Assert.Single(store.Load().Entries);
        Assert.Empty(Directory.GetFiles(paths.ConfigDir, "*.tmp"));
    }

    [Fact]
    public async Task Store_RejectsInvalidEntries_AndNeverOverwritesACorruptFile()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var store = sp.GetRequiredService<AddinAllowlistStore>();
        Assert.Throws<ArgumentException>(() => store.Add(Entry(pattern: "**")));
        Assert.Throws<ArgumentException>(() => store.Add(Entry(name: " ")));
        Assert.False(File.Exists(store.FilePath));

        File.WriteAllText(store.FilePath, "{ not json");
        Assert.NotNull(store.Load().Error);
        Assert.Throws<InvalidOperationException>(() => store.Add(Entry()));
        Assert.Equal("{ not json", File.ReadAllText(store.FilePath));

        File.WriteAllText(store.FilePath, """{ "version": 2, "entries": [] }""");
        Assert.Contains("version", store.Load().Error);
        var d = sp.GetRequiredService<AddinAllowlist>().Decide(new[] { Sample });
        Assert.Equal(AddinTrustAction.NoDecision, d.Action);
    }

    [Fact]
    public async Task BuiltInBridgeEntry_TrustsOnlyTheInstalledBridgeLocation_AndIsNeverWrittenToTheFile()
    {
        await using var sp = TestGateway.BuildProvider(out var paths);
        var allowlist = sp.GetRequiredService<AddinAllowlist>();
        string Prompt(string name, string location) => $"Name:                {name}\r\nLocation:            {location}";
        var installed = Path.Combine(paths.Root, "bridge", "R24", "2.0.0-79c76535c8", "RevitAgent.Bridge.dll");

        // live 2026-11-25: Revit prompted for the gateway's own Bridge; with an empty list it is still loaded
        var ok = allowlist.Decide(new[] { Prompt("RevitAgent Bridge", installed) });
        Assert.Equal(AddinTrustAction.LoadOnce, ok.Action);
        Assert.Equal(AddinAllowlist.BuiltInBridgeEntryId, ok.MatchedEntryId);

        // same name elsewhere, or another add-in in the Bridge folder → not trusted
        Assert.Equal(AddinTrustAction.DoNotLoad, allowlist.Decide(new[] { Prompt("RevitAgent Bridge", @"D:\Downloads\RevitAgent.Bridge.dll") }).Action);
        Assert.Equal(AddinTrustAction.DoNotLoad, allowlist.Decide(new[] { Prompt("Evil", installed) }).Action);
        Assert.Equal(AddinTrustAction.DoNotLoad, allowlist.Decide(new[] { Prompt("RevitAgent Bridge", Path.Combine(paths.Root, "bridge", "R24", "x", "y", "RevitAgent.Bridge.dll")) }).Action);

        // user entries still work alongside it, and the built-in entry never reaches the file or the read-only listing
        var store = sp.GetRequiredService<AddinAllowlistStore>();
        store.Add(Entry());
        Assert.Equal(AddinTrustAction.LoadOnce, allowlist.Decide(new[] { Sample }).Action);
        Assert.DoesNotContain("builtin", File.ReadAllText(store.FilePath));
        Assert.DoesNotContain(allowlist.Load().Entries, e => e.Id == AddinAllowlist.BuiltInBridgeEntryId);
    }

    // ---- MCP exposure --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadOnlyTool_ListsEntries_AndChangesNothing()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var store = sp.GetRequiredService<AddinAllowlistStore>();
        store.Add(Entry(id: "e1"));
        var before = File.ReadAllText(store.FilePath);

        var registry = sp.GetRequiredService<ToolRegistry>();
        Assert.True(registry.TryGet("addin_allowlist_get", out var tool));
        Assert.Equal(ToolPermission.Read, tool.Permission);
        var result = await registry.InvokeAsync("addin_allowlist_get", null, "test", CancellationToken.None);

        Assert.False(result.IsError);
        var body = result.Structured!.Value;
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        Assert.Equal("e1", body.GetProperty("entries")[0].GetProperty("id").GetString());
        Assert.Equal(before, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public async Task NoMcpTool_CanWriteTheAllowlist()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();

        // 1. no tool is named like a writer of the list
        var names = registry.Tools.Select(t => t.Name).ToList();
        Assert.Contains("addin_allowlist_get", names);
        Assert.DoesNotContain(names, n => n.Contains("allowlist", StringComparison.OrdinalIgnoreCase) && n != "addin_allowlist_get");
        Assert.DoesNotContain(names, n => n.Contains("addin_trust", StringComparison.OrdinalIgnoreCase));

        // 2. no gateway tool type can even reach the write API: it is not a constructor parameter or field of any tool
        var toolTypes = typeof(IGatewayTool).Assembly.GetTypes().Where(t => typeof(IGatewayTool).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.NotEmpty(toolTypes);
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var t in toolTypes)
        {
            Assert.DoesNotContain(t.GetConstructors(all).SelectMany(c => c.GetParameters()), p => typeof(AddinAllowlistStore).IsAssignableFrom(p.ParameterType));
            Assert.DoesNotContain(t.GetFields(all), f => typeof(AddinAllowlistStore).IsAssignableFrom(f.FieldType));
        }

        // 3. nothing a tool receives (revit_handle_dialog, ui_*) is able to touch the file: it never appears from tool calls
        Assert.False(File.Exists(sp.GetRequiredService<AddinAllowlist>().FilePath));
    }
}
