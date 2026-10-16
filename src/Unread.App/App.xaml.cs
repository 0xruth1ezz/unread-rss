using Microsoft.UI.Xaml;

namespace Unread.App;

public partial class App : Application
{
    private Window? _window;
    public App()
    {
        UnhandledException += (_, args) => WriteDiagnostic(args.Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex) { WriteDiagnostic(ex); throw; }
    }

    private static void WriteDiagnostic(Exception error)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnreadReader");
            Directory.CreateDirectory(directory);
            var detail = error + "\nHRESULT: " + error.HResult.ToString("X8");
            foreach (System.Collections.DictionaryEntry entry in error.Data) detail += "\n" + entry.Key + ": " + entry.Value;
            File.WriteAllText(Path.Combine(directory, "diagnostics.log"), detail);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
