using System.Windows;
using System.Threading;

namespace CameraDemo
{
    public partial class App : System.Windows.Application
    {
        private const string SingleInstanceMutexName = @"Global\CameraDemo_SingleInstance";
        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Prefer the Sony module by default unless caller already set an explicit hint.
            if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("CAM_DEVICE_HINT")))
            {
                System.Environment.SetEnvironmentVariable("CAM_DEVICE_HINT", "WN Camera|Sony|IMX258");
            }

            bool createdNew;
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
            if (!createdNew)
            {
                System.Windows.MessageBox.Show(
                    "CameraDemo is already running.\nThe new instance will be closed to prevent duplicate execution.",
                    "Single Instance",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
            this.DispatcherUnhandledException += (s, args) =>
            {
                System.IO.File.WriteAllText("crash.log", args.Exception.ToString());
                System.Windows.MessageBox.Show("Crash: " + args.Exception.ToString(), "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            base.OnExit(e);
        }
    }
}
