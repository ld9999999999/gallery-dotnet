using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Gallery.Desktop.ViewModels;
using Gallery.Desktop.Views;

namespace Gallery.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsPath = DesktopSettings.DefaultPath;
            var vm = new MainViewModel(DesktopSettings.Load(settingsPath), settingsPath);
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            // Stop the scanner and watchers cleanly before the process exits. Do it off the UI thread so nothing waits on it.
            desktop.Exit += (_, _) =>
            {
                vm.StopPolling();
                Task.Run(() => vm.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(10));
            };
            _ = vm.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
