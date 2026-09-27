using System.Windows;
using System.Windows.Input;
using PetGPT.Shell;

namespace PetGPT.Windows;

public partial class CommandWindow : Window
{
    private readonly LocalCommandRouter _router;
    private bool _closing;

    public CommandWindow(LocalCommandRouter router)
    {
        InitializeComponent();
        _router = router ?? throw new ArgumentNullException(nameof(router));
        Loaded += (_, _) =>
        {
            CommandTextBox.Focus();
            Keyboard.Focus(CommandTextBox);
        };
    }

    private async void OnRun(object sender, RoutedEventArgs e) => await ExecuteAsync();

    private async void OnCommandKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await ExecuteAsync();
    }

    private async Task ExecuteAsync()
    {
        if (_closing)
            return;
        var input = CommandTextBox.Text;
        CommandTextBox.IsEnabled = false;
        try
        {
            var result = await _router.ExecuteAsync(input);
            ResultTextBox.Text = result.Message;
            if (result.Succeeded)
                CommandTextBox.Clear();
        }
        catch
        {
            ResultTextBox.Text = "The local command could not be completed.";
        }
        finally
        {
            CommandTextBox.IsEnabled = true;
            CommandTextBox.Focus();
        }
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        base.OnClosed(e);
    }
}
