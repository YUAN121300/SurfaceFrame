using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace SurfaceFrame;

public partial class App : Application
{
    public static string[] Args { get; private set; } = Array.Empty<string>();

    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Args = e.Args;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var created = true;
        try
        {
            _mutex = new Mutex(true, @"Local\SurfaceFrame.Single", out created);
        }
        catch (Exception ex)
        {
            Log.Info("mutex " + ex.GetType().Name);
            created = true;
        }
        if (!created)
        {
            Shutdown(0);
            return;
        }
        var window = new MainWindow(Locate());
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    static LoadedConfig Locate()
    {
        string? path = null;
        if (Args.Length > 0)
            path = Args[0];
        else
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "frame.json");
            var example = Path.Combine(AppContext.BaseDirectory, "frame.example.json");
            if (File.Exists(beside))
                path = beside;
            else if (File.Exists(example))
                path = example;
        }
        if (path == null)
            return ConfigLoader.DemoDefaults(AppContext.BaseDirectory);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory;
        return ConfigLoader.Load(path, Path.Combine(directory, "frame.private.json"), ReadEnv);
    }

    static string? ReadEnv(string name)
    {
        try { return Environment.GetEnvironmentVariable(name); }
        catch { return null; }
    }
}

static class Log
{
    static readonly object Gate = new();

    public static void Info(string message)
    {
        try
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine;
            lock (Gate)
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "frame.log"), line);
        }
        catch
        {
        }
    }
}
