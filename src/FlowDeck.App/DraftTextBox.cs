using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FlowDeck.App;

/// <summary>Keeps an editable draft separate from WPF's IME-owned Text binding.</summary>
public sealed class DraftTextBox : TextBox
{
    public static readonly DependencyProperty DraftProperty = DependencyProperty.Register(
        nameof(Draft), typeof(string), typeof(DraftTextBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            DraftChanged, null, false, UpdateSourceTrigger.PropertyChanged));

    public string Draft { get => (string)GetValue(DraftProperty); set => SetValue(DraftProperty, value); }
    private bool copyingDraft;

    private static void DraftChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var box = (DraftTextBox)source;
        var text = args.NewValue as string ?? "";
        if (box.Text == text) return;
        box.copyingDraft = true;
        try { box.SetCurrentValue(TextProperty, text); }
        finally { box.copyingDraft = false; }
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        // TextBox.Text bindings defer their source update during IME composition.
        // A telemetry-wide PropertyChanged then restores the old source text.
        // Draft has no such deferral; keep it current without rewriting Text or
        // moving focus/selection while the text services lock is held.
        if (!copyingDraft) SetCurrentValue(DraftProperty, Text);
        base.OnTextChanged(e);
    }
}
