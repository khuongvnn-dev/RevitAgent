using RevitAgent.Core.Policy;

namespace RevitAgent.Core.Notify;

/// <summary>
/// Owner approval over Telegram: an Approve / Deny question to the configured (owner-only) chat, through the
/// same <see cref="IHumanEscalation"/> as human_ask. Unavailable until Telegram is enabled and configured.
/// </summary>
public sealed class TelegramOwnerApprovalChannel : IOwnerApprovalChannel
{
    private const string ApproveOption = "Approve";
    private const string DenyOption = "Deny";

    private readonly IHumanEscalation _escalation;
    private readonly TelegramConfigStore _config;

    public TelegramOwnerApprovalChannel(IHumanEscalation escalation, TelegramConfigStore config)
    {
        _escalation = escalation;
        _config = config;
    }

    public string Name => "telegram";

    public bool IsAvailable => _config.Load() is { Enabled: true, IsConfigured: true };

    public async Task<OwnerDecision> AskAsync(OwnerApprovalRequest request, CancellationToken cancellationToken)
    {
        var question = new HumanQuestion(
            "Owner approval required",
            request.Describe() + ". It changes the shared Revit add-ins folder of this machine. No answer in " + (int)request.Timeout.TotalMinutes + " minutes = denied.",
            null,
            new[] { ApproveOption, DenyOption },
            DenyOption,
            request.Timeout)
        {
            CancelNote = () => "Request closed (answered on another channel or cancelled).",
            BypassQueue = true,
        };

        var answer = await _escalation.AskAsync(question, cancellationToken).ConfigureAwait(false);
        if (!answer.TimedOut && string.Equals(answer.Choice, ApproveOption, StringComparison.Ordinal))
        {
            return OwnerDecision.Approve(Name, "owner (telegram: " + answer.AnsweredBy + ")");
        }

        return answer.TimedOut
            ? OwnerDecision.Deny(Name, answer.AnsweredBy, answer.Reason ?? "no answer in time", isExplicit: false)
            : OwnerDecision.Deny(Name, "owner (telegram: " + answer.AnsweredBy + ")", "denied by the owner");
    }
}
