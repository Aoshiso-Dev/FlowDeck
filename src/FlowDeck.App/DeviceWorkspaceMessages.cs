using FlowDeck.Core;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private readonly DeviceStatusFeedback feedback = new();
    private string statusMessage = "", statusDetails = "", warningMessage = "", recordedPersistentWarning = "";
    private string connectionNotice = "";

    public string StatusMessage => statusMessage;
    public string StatusDetails => statusDetails;
    public bool HasStatusDetails => statusDetails.Length > 0;
    public string WarningMessage => warningMessage;

    public void ReportStatus(string message, bool hint = false)
    {
        feedback.Report(message, hint);
        PublishFeedback();
    }

    public void ReportConnectionFailure(string message) => SetNoticeCore(message, connectionFailure: true);

    public void ReportConnectionRestored(bool automatic = false)
    {
        var previousFailure = connectionNotice;
        connectionNotice = "";
        if (previousFailure.Length > 0 && notice == previousFailure) SetNotice("");
        ReportStatus("サービス再接続済み", hint: automatic);
    }

    public void ReportResult(string message, Outcome outcome)
        => PublishResult(message, outcome == Outcome.Confirmed);

    public void ReportResult(DeviceOperationResult result)
    {
        var unsaved = result.Targets
            .Select(target => target.Persistence)
            .OfType<DevicePolicySaveResult>()
            .Where(saved => saved.State != DevicePolicySaveState.Saved)
            .ToArray();
        var messages = new[] { result.Message }.Concat(unsaved.Select(saved =>
            string.IsNullOrWhiteSpace(saved.Message)
                ? saved.State == DevicePolicySaveState.SessionOnly
                    ? "接続中のみ・保存なし"
                    : "設定の保存結果は未確認"
                : saved.Message));
        var message = string.Join(Environment.NewLine,
            messages.Select(DeviceStatusFeedback.Normalize).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal));
        // SessionOnly is an intentional successful control mode for the current
        // connection, not a failed save. Keep its explanation without warning color.
        var persistenceProblem = unsaved.Any(saved => saved.State is DevicePolicySaveState.Failed or DevicePolicySaveState.Superseded);
        PublishResult(message, result.Outcome == Outcome.Confirmed && !persistenceProblem);
    }

    private void PublishResult(string message, bool confirmed)
    {
        feedback.Report(message, false);
        // A successful operation acknowledges an earlier transient operation warning.
        // Current-state warnings are computed separately and are never cleared here.
        SetNotice(confirmed ? "" : message);
    }

    public IDisposable BeginStatus(string message)
    {
        var scope = feedback.Begin(message, PublishFeedback);
        PublishFeedback();
        return scope;
    }

    private void SetNotice(string value) => SetNoticeCore(value, connectionFailure: false);

    private void SetNoticeCore(string value, bool connectionFailure)
    {
        value = DeviceStatusFeedback.Normalize(value);
        // An unrelated notice owns its text even when it happens to be identical
        // to the preceding connection error. Reconnection must not acknowledge it.
        connectionNotice = connectionFailure ? value : "";
        if (notice != value)
        {
            notice = value;
            if (value.Length > 0) feedback.RecordNotice(value);
            Notify(nameof(Notice));
        }
        // Even clearing an already empty transient notice must re-evaluate the
        // separately computed PersistentWarning. Unchanged text does not notify UIA.
        PublishFeedback();
    }

    private void PublishFeedback()
    {
        var persistent = DeviceStatusFeedback.Normalize(PersistentWarning);
        if (recordedPersistentWarning != persistent)
        {
            recordedPersistentWarning = persistent;
            if (persistent.Length > 0) feedback.RecordNotice(persistent);
        }
        var snapshot = feedback.Read();
        var nextWarning = string.Join(Environment.NewLine,
            new[] { notice, persistent }
                .SelectMany(value => (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.Ordinal));
        var messageChanged = statusMessage != snapshot.Message;
        var detailsChanged = statusDetails != snapshot.Details;
        var hasDetailsChanged = (statusDetails.Length > 0) != (snapshot.Details.Length > 0);
        var warningChanged = warningMessage != nextWarning;
        statusMessage = snapshot.Message;
        statusDetails = snapshot.Details;
        warningMessage = nextWarning;
        if (messageChanged) Notify(nameof(StatusMessage));
        if (detailsChanged) Notify(nameof(StatusDetails));
        if (hasDetailsChanged) Notify(nameof(HasStatusDetails));
        if (warningChanged) Notify(nameof(WarningMessage));
    }
}
