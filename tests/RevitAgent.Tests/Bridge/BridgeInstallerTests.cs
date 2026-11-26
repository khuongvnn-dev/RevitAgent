using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Bridge;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Install;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Bridge;

/// <summary>Installer against temp folders + in-memory trust store: never touches the real %APPDATA% or registry.</summary>
public class BridgeInstallerTests
{
    private sealed class Sandbox
    {
        public Sandbox()
        {
            Root = TestGateway.NewTempDir("revitagent_install_");
            Payload = Path.Combine(Root, "payload");
            Addins = Path.Combine(Root, "Addins");
            Trust = new InMemoryBridgeTrustStore();
            Installer = new BridgeInstaller(Environment());
            WritePayload(2022, "build-22-v1");
            WritePayload(2024, "build-24-v1");
        }

        public string Root { get; }

        public string Payload { get; }

        public string Addins { get; }

        public InMemoryBridgeTrustStore Trust { get; }

        public BridgeInstaller Installer { get; }

        public List<RevitProcessInfo> Running { get; } = new();

        public string InstallRoot => Path.Combine(Root, "bridge");

        public BridgeInstallEnvironment Environment() => new()
        {
            InstallRoot = Path.Combine(Root, "bridge"),
            UserAddinsDir = y => Path.Combine(Addins, y.ToString()),
            Trust = Trust,
            Installations = () => new[] { Revit(2022), Revit(2024) },
            PayloadRoots = () => new[] { Payload },
            RunningRevit = () => Running,
        };

        public void WritePayload(int year, string content)
        {
            var dir = Path.Combine(Payload, BridgeInstaller.YearFolder(year));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, BridgeWire.AssemblyFileName), content);
            File.WriteAllText(Path.Combine(dir, "RevitAgent.Bridge.pdb"), "pdb-" + content);
            File.SetLastWriteTimeUtc(Path.Combine(dir, BridgeWire.AssemblyFileName), DateTime.UtcNow.AddSeconds(year));
        }

        public string Manifest(int year) => Path.Combine(Addins, year.ToString(), BridgeWire.ManifestFileName);

        private RevitInstallation Revit(int year) =>
            new(year, Path.Combine(Root, "Revit " + year), Path.Combine(Root, "Revit " + year, "Revit.exe"), year + ".0", null, Path.Combine(Addins, year.ToString()), Path.Combine(Root, "ProgramData", year.ToString()));
    }

    private static Dictionary<string, object?> R(object o) => Assert.IsType<Dictionary<string, object?>>(o);

    [Fact]
    public void Install_CopiesStampedBuild_WritesManifest_AndTrustsOurAddInId()
    {
        var s = new Sandbox();
        var results = s.Installer.Install(null);
        Assert.Equal(2, results.Count);
        foreach (var year in new[] { 2022, 2024 })
        {
            var r = R(results.Single(x => (int)R(x)["year"]! == year));
            Assert.Equal(true, r["ok"]);
            var asm = (string)r["assembly"]!;
            Assert.StartsWith(Path.Combine(s.InstallRoot, BridgeInstaller.YearFolder(year)), asm);
            Assert.True(File.Exists(asm));
            Assert.True(File.Exists(Path.ChangeExtension(asm, ".pdb")));
            Assert.Equal($"build-{year % 100}-v1", File.ReadAllText(asm));

            var xml = XDocument.Load(s.Manifest(year));
            var addin = xml.Root!.Element("AddIn")!;
            Assert.Equal("Application", addin.Attribute("Type")!.Value);
            Assert.Equal(asm, addin.Element("Assembly")!.Value);
            Assert.Equal(BridgeWire.AddInId, addin.Element("AddInId")!.Value);
            Assert.Equal(BridgeWire.FullClassName, addin.Element("FullClassName")!.Value);
            Assert.Equal("RVAG", addin.Element("VendorId")!.Value);
            Assert.True(s.Trust.IsTrusted(year, BridgeWire.AddInId));
            Assert.True(File.Exists(Path.Combine(s.InstallRoot, BridgeInstaller.YearFolder(year), "install.json")));
        }

        Assert.Contains("2024/" + BridgeWire.AddInId.ToLowerInvariant(), s.Trust.Values);
    }

    [Fact]
    public void Reinstall_SameBuild_ReusesCopy_ChangedBuildGoesSideBySide_AndCleansOld()
    {
        var s = new Sandbox();
        var first = R(s.Installer.Install(new[] { 2024 })[0]);
        var again = R(s.Installer.Install(new[] { 2024 })[0]);
        Assert.Equal(true, again["reused_existing_copy"]);
        Assert.Equal(first["assembly"], again["assembly"]);

        s.WritePayload(2024, "build-24-v2");
        var updated = R(s.Installer.Install(new[] { 2024 })[0]);
        Assert.NotEqual(first["assembly"], updated["assembly"]);
        Assert.Equal("build-24-v2", File.ReadAllText((string)updated["assembly"]!));
        Assert.Contains(Path.GetDirectoryName((string)first["assembly"]!), (List<string>)updated["cleaned_old_copies"]!);
        Assert.Equal((string)updated["assembly"]!, XDocument.Load(s.Manifest(2024)).Root!.Element("AddIn")!.Element("Assembly")!.Value);
    }

    [Fact]
    public void Install_WhileOldCopyIsLocked_InstallsSideBySide_AndRepointsManifest()
    {
        var s = new Sandbox();
        var first = R(s.Installer.Install(new[] { 2024 })[0]);
        var oldDll = (string)first["assembly"]!;
        s.WritePayload(2024, "build-24-v2");
        using (new FileStream(oldDll, FileMode.Open, FileAccess.Read, FileShare.Read)) // like Revit holding the loaded DLL
        {
            var updated = R(s.Installer.Install(new[] { 2024 })[0]);
            Assert.Equal(true, updated["ok"]);
            Assert.Contains(Path.GetDirectoryName(oldDll), (List<string>)updated["locked_old_copies"]!);
            Assert.Equal((string)updated["assembly"]!, XDocument.Load(s.Manifest(2024)).Root!.Element("AddIn")!.Element("Assembly")!.Value);
            Assert.True(File.Exists(oldDll));
        }

        // Once Revit has released it, the next install cleans it up.
        var later = R(s.Installer.Install(new[] { 2024 })[0]);
        Assert.Contains(Path.GetDirectoryName(oldDll), (List<string>)later["cleaned_old_copies"]!);
        Assert.False(File.Exists(oldDll));
    }

    [Fact]
    public void Install_RemovesLegacyV1Manifest_ButNeverForeignOnes()
    {
        var s = new Sandbox();
        var dir = Path.Combine(s.Addins, "2024");
        Directory.CreateDirectory(dir);
        var legacy = Path.Combine(dir, "Old.RevitAgent.addin");
        File.WriteAllText(legacy, $"<?xml version=\"1.0\"?><RevitAddIns><AddIn Type=\"Application\"><Name>RevitAgent.Bridge</Name><Assembly>x.dll</Assembly><AddInId>{BridgeWire.LegacyAddInId}</AddInId><FullClassName>RevitAgent.Bridge.EntryPoint</FullClassName><VendorId>OLDV</VendorId></AddIn></RevitAddIns>");
        var foreign = Path.Combine(dir, "RevitLookup.addin");
        File.WriteAllText(foreign, "<?xml version=\"1.0\"?><RevitAddIns><AddIn Type=\"Application\"><Assembly>RevitLookup.dll</Assembly><AddInId>356CDA5A-E6C5-4c2f-A9EF-B3222116B8C8</AddInId></AddIn></RevitAddIns>");

        var r = R(s.Installer.Install(new[] { 2024 })[0]);
        Assert.Contains(legacy, (List<string>)r["removed_legacy_manifests"]!);
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void Install_RemovesLegacyV1TrustValue_KeepsOtherAddInsTrust()
    {
        var s = new Sandbox();
        s.Trust.Trust(2024, BridgeWire.LegacyAddInId);
        s.Trust.Trust(2024, "F8A5C26B-6E5D-4A9D-8A34-2C9E7A64EED3"); // someone else's add-in
        var r = R(s.Installer.Install(new[] { 2024 })[0]);
        Assert.Single((IReadOnlyList<string>)r["removed_legacy_trust_values"]!);
        Assert.False(s.Trust.IsTrusted(2024, BridgeWire.LegacyAddInId));
        Assert.True(s.Trust.IsTrusted(2024, "f8a5c26b-6e5d-4a9d-8a34-2c9e7a64eed3"));
        Assert.True(s.Trust.IsTrusted(2024, BridgeWire.AddInId));
    }

    [Fact]
    public void Install_DoesNotOverwriteAForeignManifestWithOurFileName()
    {
        var s = new Sandbox();
        Directory.CreateDirectory(Path.GetDirectoryName(s.Manifest(2024))!);
        File.WriteAllText(s.Manifest(2024), "<?xml version=\"1.0\"?><RevitAddIns><AddIn Type=\"Application\"><Assembly>other.dll</Assembly><AddInId>11111111-2222-3333-4444-555555555555</AddInId></AddIn></RevitAddIns>");
        var r = R(s.Installer.Install(new[] { 2024 })[0]);
        Assert.Equal(false, r["ok"]);
        Assert.Contains("other.dll", File.ReadAllText(s.Manifest(2024)));
        Assert.False(s.Trust.IsTrusted(2024, BridgeWire.AddInId));
    }

    [Fact]
    public void Install_ReportsMissingYearOrBuild_PerYear_WithoutStoppingOthers()
    {
        var s = new Sandbox();
        Directory.Delete(Path.Combine(s.Payload, "R22"), recursive: true);
        var results = s.Installer.Install(new[] { 2022, 2023, 2024 });
        var byYear = results.Select(R).ToDictionary(r => (int)r["year"]!);
        Assert.Equal(false, byYear[2022]["ok"]);
        Assert.Contains("build-bridge.ps1", JsonSerializer.Serialize(byYear[2022]["error"]));
        Assert.Equal(false, byYear[2023]["ok"]);
        Assert.Contains(GatewayErrorCodes.NotFound, JsonSerializer.Serialize(byYear[2023]["error"]));
        Assert.Equal(true, byYear[2024]["ok"]);
    }

    [Fact]
    public void Install_ReportsRunningRevitThatNeedsARestart()
    {
        var s = new Sandbox();
        s.Running.Add(new RevitProcessInfo(1234, "Revit", @"C:\Revit 2024\Revit.exe", 2024, DateTime.Now));
        var r = R(s.Installer.Install(new[] { 2024 })[0]);
        Assert.Equal(new[] { 1234 }, (int[])r["restart_required_pids"]!);
        Assert.Contains("restart", (string)r["note"]!);
    }

    [Fact]
    public void Uninstall_RemovesManifestTrustAndCopies_KeepsLockedCopy()
    {
        var s = new Sandbox();
        var installed = R(s.Installer.Install(new[] { 2022, 2024 })[1]);
        var dll24 = (string)installed["assembly"]!;
        using (new FileStream(dll24, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var r = s.Installer.Uninstall(new[] { 2024 }).Select(R).Single();
            Assert.Equal(true, r["ok"]);
            Assert.False(File.Exists(s.Manifest(2024)));
            Assert.False(s.Trust.IsTrusted(2024, BridgeWire.AddInId));
            Assert.Contains((List<string>)r["kept"]!, k => k.StartsWith(Path.GetDirectoryName(dll24)!, StringComparison.OrdinalIgnoreCase));
        }

        Assert.True(File.Exists(s.Manifest(2022)));
        Assert.True(s.Trust.IsTrusted(2022, BridgeWire.AddInId));
        var r22 = s.Installer.Uninstall(new[] { 2022 }).Select(R).Single();
        Assert.False(File.Exists(s.Manifest(2022)));
        Assert.False(Directory.Exists(Path.Combine(s.InstallRoot, "R22")));
        Assert.Empty((List<string>)r22["kept"]!);
    }

    [Fact]
    public void List_ReportsInstalledTrustedAndUpToDate()
    {
        var s = new Sandbox();
        s.Installer.Install(new[] { 2024 });
        var list = s.Installer.List().Select(R).ToDictionary(r => (int)r["year"]!);
        Assert.Equal(true, list[2024]["installed"]);
        Assert.Equal(true, list[2024]["trusted"]);
        Assert.Equal(true, list[2024]["up_to_date"]);
        Assert.Equal("net48", list[2024]["framework"]);
        Assert.Equal(false, list[2022]["installed"]);

        s.WritePayload(2024, "build-24-v3");
        Assert.Equal(false, R(s.Installer.List().First(x => (int)R(x)["year"]! == 2024))["up_to_date"]);
    }

    [Fact]
    public void FindPayload_PrefersTheNewestBuildAcrossRoots()
    {
        var s = new Sandbox();
        var release = Path.Combine(s.Root, "release");
        Directory.CreateDirectory(Path.Combine(release, "R24"));
        var newer = Path.Combine(release, "R24", BridgeWire.AssemblyFileName);
        File.WriteAllText(newer, "newer");
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(1));
        var installer = new BridgeInstaller(new BridgeInstallEnvironment
        {
            InstallRoot = s.InstallRoot,
            UserAddinsDir = y => Path.Combine(s.Addins, y.ToString()),
            Trust = s.Trust,
            Installations = () => Array.Empty<RevitInstallation>(),
            PayloadRoots = () => new[] { s.Payload, release },
        });
        Assert.Equal(Path.GetFullPath(newer), installer.FindPayload(2024)!.DllPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(s.Payload, "R24", BridgeWire.AssemblyFileName)), installer.FindPayload(2024, s.Payload)!.DllPath);
        Assert.Null(installer.FindPayload(2026));
    }

    [Fact]
    public async Task InstallTools_ThroughTheRegistry_UseTheInjectedEnvironment()
    {
        var s = new Sandbox();
        await using var sp = BridgeGatewayToolsTests.BuildGateway(out _, installEnv: s.Environment());
        var registry = sp.GetRequiredService<ToolRegistry>();

        var install = await registry.InvokeAsync("bridge_install", JsonSerializer.SerializeToElement(new { years = new[] { 2024 } }), "t", CancellationToken.None);
        Assert.False(install.IsError, (install.Content[0] as TextContent)?.Text);
        Assert.True(install.Structured!.Value.GetProperty("ok").GetBoolean());
        Assert.True(File.Exists(s.Manifest(2024)));

        var list = await registry.InvokeAsync("bridge_list_installs", null, "t", CancellationToken.None);
        var installs = list.Structured!.Value.GetProperty("installs");
        Assert.Contains(installs.EnumerateArray(), e => e.GetProperty("year").GetInt32() == 2024 && e.GetProperty("installed").GetBoolean());

        var bad = await registry.InvokeAsync("bridge_install", JsonSerializer.SerializeToElement(new { years = new[] { 2019 } }), "t", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, bad.ErrorCode);

        var uninstall = await registry.InvokeAsync("bridge_uninstall", JsonSerializer.SerializeToElement(new { years = new[] { 2024 } }), "t", CancellationToken.None);
        Assert.False(uninstall.IsError);
        Assert.False(File.Exists(s.Manifest(2024)));

        var policy = sp.GetRequiredService<RevitAgent.Core.Policy.GatewayPolicy>();
        policy.MaxPermission = ToolPermission.Write;
        var denied = await registry.InvokeAsync("bridge_install", null, "t", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, denied.ErrorCode);
    }

    [Fact]
    public void RegistryTrustStore_UsesLowercaseAddInIdWithoutBraces()
    {
        Assert.Equal(@"SOFTWARE\Autodesk\Revit\Autodesk Revit 2024\CodeSigning", RegistryBridgeTrustStore.KeyPath(2024));
        Assert.Equal("2b0c5ac5-295c-415b-baad-f731a61c66d8", RegistryBridgeTrustStore.ValueName("{" + BridgeWire.AddInId + "}"));
    }
}
