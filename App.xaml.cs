using PetGPT.Shell;

namespace PetGPT;

public partial class App : System.Windows.Application
{
    private AppLifetime? _lifetime;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        _lifetime = new AppLifetime(this);
        await _lifetime.StartAsync();
    }

    protected override void OnSessionEnding(System.Windows.SessionEndingCancelEventArgs e)
    {
        _lifetime?.HandleSessionEnding();
        base.OnSessionEnding(e);
    }
}
