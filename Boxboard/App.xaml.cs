using System.Windows;
using Boxboard.Models;
using Boxboard.Services;

namespace Boxboard;

public partial class App : Application
{
    private SettingsStore? _store;

    internal static BoardSettings InitialSettings(bool demo) => demo
        ? DemoData.Settings()
        : new BoardSettings { StartWithWindows = true };

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            bool demo = e.Args.Contains("--demo");
            string? customPath = null;
            if (e.Args.Length == 2 && e.Args[0] == "--settings" && Path.IsPathFullyQualified(e.Args[1]))
                customPath = e.Args[1];
            else if (!(e.Args.Length == 0 || e.Args.SequenceEqual(["--demo"])))
                throw new ArgumentException("Usage: Boxboard.exe [--demo | --settings <absolute settings.json path>]");
            var path = demo
                ? Path.Combine(Path.GetTempPath(), "Boxboard-demo", Guid.NewGuid().ToString("N"), "settings.json")
                : customPath ?? SettingsStore.DefaultPath;
            _store = new SettingsStore(path);
            if (demo || !File.Exists(path))
                await _store.SaveAsync(InitialSettings(demo));
            var board = new SlotBoard(_store);
            await board.LoadAsync();
            await board.EnsureFourCellsAsync();
            var window = new MainWindow(board, demo, path);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            if (!demo && board.Settings.StartWithWindows)
            {
                try { StartupRegistration.Apply(true, path); }
                catch (Exception ex) { window.ReportStartupError($"Could not enable Start with Windows: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Boxboard could not open its settings. Another Boxboard window may be using the layout, " +
                $"or the settings file may be invalid or inaccessible. No settings were replaced.\n\n{ex.Message}",
                "Boxboard - startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _store?.Dispose();
        base.OnExit(e);
    }
}
