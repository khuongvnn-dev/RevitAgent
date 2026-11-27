using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Notify.Tools;

/// <summary>
/// MCP tool allowing an AI agent to ask the human owner a decision question with button options via Telegram.
/// </summary>
public sealed class HumanAskTool : GatewayTool
{
    private readonly IHumanEscalation _escalation;
    private readonly AgentPaths _paths;

    public HumanAskTool(IHumanEscalation escalation, AgentPaths paths)
    {
        _escalation = escalation ?? throw new ArgumentNullException(nameof(escalation));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public override string Name => "human_ask";

    public override string Description =>
        "Ask the owner on Telegram with 2-6 buttons and wait. ONLY when a human must decide (forbidden dialog, risky step, ambiguity). " +
        "Returns the choice, or safe_option on timeout or without Telegram.";

    public override ToolPermission Permission => ToolPermission.Interact;

    // Default tool timeout: allow sufficient time for human response (up to 4 hours)
    public override TimeSpan Timeout => TimeSpan.FromHours(4);

    protected override Schema BuildSchema() => Schema.Object()
        .String("question", "Question.", required: true)
        .StringArray("options", "2-6 choices.", required: true)
        .String("safe_option", "Used on timeout; one of options.", required: true)
        .Integer("timeout_minutes", "Minutes before safe_option.", required: false, minimum: 1, maximum: 240)
        .String("screenshot_path", "PNG under REVITAGENT_HOME to attach.", required: false);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var question = call.RequireString("question");
        var options = call.GetStringList("options");

        if (options.Count < 2 || options.Count > 6)
        {
            throw GatewayException.Validation($"Argument 'options' must contain between 2 and 6 choices (got {options.Count}).");
        }

        var safeOption = call.RequireString("safe_option");
        if (!options.Contains(safeOption))
        {
            throw GatewayException.Validation($"Argument 'safe_option' ('{safeOption}') is not one of the provided options: [{string.Join(", ", options)}].");
        }

        var timeoutMinutes = call.GetInt("timeout_minutes");
        if (timeoutMinutes.HasValue && (timeoutMinutes.Value <= 0 || timeoutMinutes.Value > 240))
        {
            throw GatewayException.Validation($"Argument 'timeout_minutes' must be between 1 and 240 (got {timeoutMinutes.Value}).");
        }

        string? screenshotPath = call.GetString("screenshot_path");
        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            var fullScreenshot = Path.GetFullPath(screenshotPath);
            if (!fullScreenshot.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                throw GatewayException.Validation($"Argument 'screenshot_path' must be a .png file ('{screenshotPath}').");
            }

            if (!File.Exists(fullScreenshot))
            {
                throw GatewayException.Validation($"Screenshot file '{screenshotPath}' does not exist.");
            }

            var root = Path.GetFullPath(_paths.Root);
            if (!fullScreenshot.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw GatewayException.Validation($"Argument 'screenshot_path' must be under REVITAGENT_HOME ('{root}').");
            }

            screenshotPath = fullScreenshot;
        }
        else
        {
            screenshotPath = null;
        }

        var humanQuestion = new HumanQuestion(
            Title: "Decision requested by the agent",
            Text: question,
            ScreenshotPath: screenshotPath,
            Options: options,
            SafeOption: safeOption,
            Timeout: timeoutMinutes.HasValue ? TimeSpan.FromMinutes(timeoutMinutes.Value) : null);

        var answer = await _escalation.AskAsync(humanQuestion, cancellationToken);
        return ToolResult.Json(answer);
    }
}
