using System;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;

namespace LiveWallpaper
{
    internal static class Program
    {
        const string MutexName = @"Local\LiveWallpaper.SingleInstance.5B1E";
        const string ShowEventName = @"Local\LiveWallpaper.ShowSettings.5B1E";

        const string PreviewEventName = @"Local\LiveWallpaper.Preview.5B1E";

        static string PreviewRequestFile { get { return System.IO.Path.Combine(Settings.DataDir, "preview-request.txt"); } }

        [STAThread]
        static void Main(string[] args)
        {
            // A file passed on the command line (e.g. dropped on the exe) is played as a preview.
            string previewArg = null;
            if (args.Length > 0 && System.IO.File.Exists(args[0]) && MediaFactory.KindOf(args[0]) != MediaKind.None)
                previewArg = System.IO.Path.GetFullPath(args[0]);

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // Already running: hand the request to that instance.
                    try
                    {
                        if (previewArg != null)
                        {
                            System.IO.Directory.CreateDirectory(Settings.DataDir);
                            System.IO.File.WriteAllText(PreviewRequestFile, previewArg, System.Text.Encoding.UTF8);
                            EventWaitHandle.OpenExisting(PreviewEventName).Set();
                        }
                        else EventWaitHandle.OpenExisting(ShowEventName).Set();
                    }
                    catch { }
                    return;
                }

                Log.Init();
                Log.Write("Start " + Environment.OSVersion.VersionString);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Write("UI error: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log.Write("Fatal: " + e.ExceptionObject); };

                // WPF (video surfaces) runs on the same thread; the WinForms loop pumps its messages.
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.UnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                {
                    Log.Write("Dispatcher error: " + e.Exception);
                    e.Handled = true;
                };

                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                using (var previewEvent = new EventWaitHandle(false, EventResetMode.AutoReset, PreviewEventName))
                {
                    var app = new TrayApp();
                    var wait = ThreadPool.RegisterWaitForSingleObject(showEvent,
                        delegate { dispatcher.BeginInvoke(new Action(app.ShowSettings)); }, null, -1, false);
                    var waitPreview = ThreadPool.RegisterWaitForSingleObject(previewEvent, delegate
                    {
                        string path = null;
                        try { path = System.IO.File.ReadAllText(PreviewRequestFile).Trim(); System.IO.File.Delete(PreviewRequestFile); } catch { }
                        if (!string.IsNullOrEmpty(path)) dispatcher.BeginInvoke(new Action(delegate { app.Preview(path); }));
                    }, null, -1, false);
                    if (previewArg != null) app.Preview(previewArg);
                    Application.Run(app);
                    wait.Unregister(null);
                    waitPreview.Unregister(null);
                }
                dispatcher.InvokeShutdown();
                Log.Write("Exit");
                GC.KeepAlive(mutex);
            }
        }
    }
}
