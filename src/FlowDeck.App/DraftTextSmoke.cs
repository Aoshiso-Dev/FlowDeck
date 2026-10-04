using System.Windows.Controls;

namespace FlowDeck.App;

public static partial class SmokeRunner
{
    private static async Task CheckDraftInputAsync(DraftTextBox box, FlowDeckViewModel vm,
        string name, Func<string> readDraft, Action<bool, string> check)
    {
        box.SelectAll(); box.SelectedText = "";
        foreach (var character in name)
        {
            box.SelectedText = character.ToString();
            box.CaretIndex = box.Text.Length;
            vm.Refresh();
            await Task.Delay(30);
        }
        box.Select(1, 2);
        await Task.Delay(1300); // Includes normal telemetry and dispatcher-timer refreshes.
        check(box.Text == name && readDraft() == name && box.SelectionStart == 1 && box.SelectionLength == 2,
            $"Japanese draft {name} survives telemetry refreshes with its selection intact");
        box.SelectedText = "ABC";
        var replaced = name[..1] + "ABC" + name[3..];
        vm.Refresh(); await Settle();
        check(box.Text == replaced && readDraft() == replaced, $"Selection replacement updates draft {name} without waiting for focus loss");
        box.Undo(); vm.Refresh(); await Settle();
        check(box.Text == name && readDraft() == name, $"Undo restores draft {name} without breaking its binding");
    }
}
