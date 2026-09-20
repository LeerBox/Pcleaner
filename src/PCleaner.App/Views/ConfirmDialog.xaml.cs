using System.Windows;

namespace PCleaner.App.Views;

/// <summary>
/// Themed confirmation dialog. The confirm button is deliberately NOT the default button, so a stray Enter key
/// (or an automation tool answering dialogs) cannot start a cleanup.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(Window owner, string title, string message, string? warning, string? detail, string confirmText = "Clean now")
    {
        InitializeComponent();
        Owner = owner;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmText.Text = confirmText;
        System.Windows.Automation.AutomationProperties.SetName(ConfirmButton, confirmText);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            WarningText.Text = warning;
            WarningPanel.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            DetailText.Text = detail;
            DetailPanel.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => CancelButton.Focus();
    }

    private void OnDragMove(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>Shows the dialog modally and returns true when the user confirmed.</summary>
    public static bool Confirm(Window owner, string title, string message, string? warning = null, string? detail = null, string confirmText = "Clean now", Action<string>? diagnostics = null)
    {
        var dialog = new ConfirmDialog(owner, title, message, warning, detail, confirmText);
        var result = dialog.ShowDialog() == true;
        diagnostics?.Invoke(dialog.Diagnostics);
        return result;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Diagnostics = $"confirm source={e.Source?.GetType().Name} originalSource={e.OriginalSource?.GetType().Name} mouse={System.Windows.Input.Mouse.LeftButton} pos={System.Windows.Input.Mouse.GetPosition(this)} focus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name} active={IsActive} elapsed={(DateTime.UtcNow - _shownUtc).TotalMilliseconds:F0}ms";
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Diagnostics = $"cancel source={e.Source?.GetType().Name} elapsed={(DateTime.UtcNow - _shownUtc).TotalMilliseconds:F0}ms";
        DialogResult = false;
        Close();
    }

    private readonly DateTime _shownUtc = DateTime.UtcNow;

    /// <summary>How the dialog was answered (for the activity log).</summary>
    public string Diagnostics { get; private set; } = "closed without answer";
}