namespace FlowDeck.App;

/// <summary>In-memory feedback for the current workspace, independent of device control.</summary>
internal sealed class DeviceStatusFeedback(Func<DateTimeOffset>? clock = null)
{
    private const int HistoryCapacity = 20;
    private readonly object sync = new();
    private readonly List<Progress> active = [];
    private readonly List<Entry> history = [];
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);
    private string result = "", hint = "";

    internal readonly record struct Snapshot(string Message, string Details);
    private sealed record Entry(DateTimeOffset Time, string Message);
    private sealed class Progress(string message)
    {
        internal string Message { get; } = message;
    }

    internal Snapshot Read()
    {
        lock (sync)
        {
            var message = active.Count > 0 ? active[^1].Message : result.Length > 0 ? result : hint;
            var details = string.Join(Environment.NewLine + Environment.NewLine,
                history.AsEnumerable().Reverse().Select(entry => $"[{entry.Time.LocalDateTime:MM/dd HH:mm:ss}] {entry.Message}"));
            return new(Compact(message), details);
        }
    }

    // Keep the service's original wording in history; shorten only known results
    // in the one-line status bar. Unknown messages and failures remain intact.
    private static string Compact(string message) => string.Join(Environment.NewLine,
        message.Split(Environment.NewLine).Select(line => line switch
        {
            "設定を確認しました" => "設定確認済み",
            "設定を取得しました" => "設定取得済み",
            "通知設定を保存しました" => "通知設定を保存済み",
            "表示設定を保存しました" => "表示設定を保存済み",
            "機器名を保存しました" => "機器名を保存済み",
            "冷却グループを削除しました" => "冷却グループ削除済み",
            "冷却グループを保存しました" => "冷却グループ保存済み",
            "照明のグループ・シーンを保存しました" => "照明グループ・シーン保存済み",
            "照明の名前を保存しました" => "照明名を保存済み",
            "標準の名前に戻しました" => "標準名に復帰済み",
            "お気に入りを保存しました" => "お気に入り保存済み",
            "最近使った色を保存しました" => "使用履歴を保存済み",
            "起動時の冷却設定を保存しました" => "起動時の冷却設定を保存済み",
            "設定の差分と機器の対応を確認しました" => "設定差分・機器の対応を確認済み",
            "切断済み機器の整理内容を確認しました" => "切断済み機器の整理内容を確認済み",
            "現在の接続で使えるメモリ照明を確認しました" => "接続中のメモリ照明を確認済み",
            "OpenRGBの導入状態を確認しました" => "OpenRGB導入状態を確認済み",
            "設定を反映しました。制御は監視状態です" => "設定反映済み · 監視のみ",
            "照明の設定を保存しました。変更対象外の制御は継続しています" => "照明設定を保存済み · 対象外の制御は継続",
            "冷却端子の設定を保存しました。変更対象外の制御は継続しています" => "冷却端子設定を保存済み · 対象外の制御は継続",
            _ => line
        }));

    internal void Report(string message, bool isHint)
    {
        message = Normalize(message);
        lock (sync)
        {
            if (isHint)
            {
                if (hint == message) return;
                hint = message;
            }
            else
            {
                if (result == message) return;
                result = message;
            }
            Remember(message);
        }
    }

    internal void RecordNotice(string message)
    {
        lock (sync) Remember(Normalize(message));
    }

    internal IDisposable Begin(string message, Action changed)
    {
        message = Normalize(message);
        if (message.Length == 0) return EmptyScope.Instance;
        var progress = new Progress(message);
        lock (sync)
        {
            active.Add(progress);
            Remember(message);
        }
        return new Scope(this, progress, changed);
    }

    private bool End(Progress progress)
    {
        lock (sync) return active.Remove(progress);
    }

    // Repeated polls never change timestamps or append the same message. A genuinely
    // changed message can recur later; keep only its latest occurrence in the bounded list.
    private void Remember(string message)
    {
        if (message.Length == 0 || history.Count > 0 && history[^1].Message == message) return;
        var previous = history.FindIndex(entry => entry.Message == message);
        if (previous >= 0) history.RemoveAt(previous);
        history.Add(new(now(), message));
        if (history.Count > HistoryCapacity) history.RemoveAt(0);
    }

    internal static string Normalize(string? message) => message?.Trim() ?? "";

    private sealed class Scope(DeviceStatusFeedback owner, Progress progress, Action changed) : IDisposable
    {
        private DeviceStatusFeedback? current = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref current, null);
            if (value?.End(progress) == true) changed();
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
