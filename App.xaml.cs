using System;
using System.Configuration;
using System.Data;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LBA2LevelEditor;

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
        DebugLog.Log("App: startup");
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
    }
}

