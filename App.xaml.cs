using System;
using System.Configuration;
using System.Data;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LBAAssembler;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // Belt-and-suspenders alongside DebugLog's other call sites: the crash
    // this whole logging effort was added to chase (see DebugLog.cs's own
    // comment) is a native access violation, which these managed handlers
    // never see -- they can only ever fire for an exception thrown from
    // managed code, a different failure class. Wired anyway since a
    // managed exception is cheap to also catch here, and it means any
    // *future* crash that turns out to be a normal .NET exception shows up
    // in the same log rather than only in a Windows Error Reporting popup.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Applied before the first window is ever constructed (App.xaml has no StartupUri any more, on
        // purpose -- that would construct MainWindow as part of base.OnStartup itself, before this line
        // ever ran, so every {DynamicResource ThemeXxx} in it would resolve to nothing for one frame).
        ThemeManager.Apply(ThemeManager.Parse(EditorSettings.Current.Theme));
        // Point the native engine's own log at the same file (it stays silent without this).
        Environment.SetEnvironmentVariable("LBA2_EDITOR_DEBUG_LOG", DebugLog.LogFile);
        DebugLog.Log($"App: startup (version {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version})");
        DispatcherUnhandledException += (_, args) =>
        {
            DebugLog.Log($"App: DispatcherUnhandledException: {args.Exception}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            DebugLog.Log($"App: AppDomain UnhandledException (terminating={args.IsTerminating}): {args.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DebugLog.Log($"App: UnobservedTaskException: {args.Exception}");
        };
        if (Export.ExportCli.Wants(e.Args)) { Shutdown(Export.ExportCli.Run(e.Args)); return; }      // batch export, no window
        new MainWindow().Show();
    }
}

