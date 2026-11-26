using System.Text.Json.Nodes;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui;

namespace RevitAgent.Core.Bridge;

/// <summary>Host side of view_export_image: where the PNG may be written.</summary>
internal static class ViewExportImageSupport
{
    public const int DefaultPixelWidth = 1600;
    public const int MaxPixelWidth = 4096;

    /// <summary>
    /// Resolves <c>save_path</c> inside the captures or runs folder (SANDBOX_VIOLATION otherwise; default: a new file in captures)
    /// and bounds <c>pixel_width</c>. The Bridge receives an absolute path it can write to directly.
    /// </summary>
    public static JsonObject Prepare(ToolCall call, JsonObject args, BridgeToolServices services)
    {
        args["save_path"] = ResolveSavePath(services.Paths, call.GetString("save_path"));
        var width = call.GetInt("pixel_width", DefaultPixelWidth) ?? DefaultPixelWidth;
        args["pixel_width"] = Math.Clamp(width, 64, MaxPixelWidth);
        return args;
    }

    public static string ResolveSavePath(AgentPaths paths, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Path.Combine(paths.CapturesDir, $"view_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        }

        string full;
        try
        {
            full = UiPaths.Evidence(paths, requested, paths.CapturesDir);
        }
        catch (GatewayException ex) when (ex.Code == Contracts.Protocol.GatewayErrorCodes.PermissionDenied)
        {
            throw new GatewayException(Contracts.Protocol.GatewayErrorCodes.SandboxViolation, ex.Message, ex.Details);
        }

        return full.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? full : full + ".png";
    }
}
