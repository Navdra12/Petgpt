using System.Windows;
using PetGPT.Services;
using PetGPT.Windows;

namespace PetGPT;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = new SettingsService();
        var petWindow = new PetWindow(settings);
        MainWindow = petWindow;
        petWindow.Show();
    }
}
