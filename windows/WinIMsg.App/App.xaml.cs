using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppNotifications;
using Microsoft.Win32;
using Windows.Graphics;
using WinIMsg.App.Services;

namespace WinIMsg.App
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? window;
        private DispatcherQueue? dispatcherQueue;
        private MainPage? mainPage;
        private WindowMinimizeHook? minimizeHook;
        private bool isExiting;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                WriteStartupTrace("AppDomain.CurrentDomain.UnhandledException", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                WriteStartupTrace("TaskScheduler.UnobservedTaskException", args.Exception);
                args.SetObserved();
            };
            UnhandledException += (_, args) =>
            {
                WriteStartupTrace("Application.UnhandledException", args.Exception);
                args.Handled = false;
            };
            WriteStartupTrace("App constructor before InitializeComponent");
            this.InitializeComponent();
            WriteStartupTrace("App constructor after InitializeComponent");
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        public IWindowShellService WindowShell { get; } = new WinUiWindowShellService();

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            WriteStartupTrace("OnLaunched entered");
            dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            var launchArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (launchArgs.Any(arg => string.Equals(arg, "--register-app-identity", StringComparison.OrdinalIgnoreCase)))
            {
                AppIdentityService.EnsureConfigured();
                Exit();
                return;
            }

            var allowSecondaryInstance = ShouldAllowSecondaryInstance(launchArgs);

            if (!allowSecondaryInstance && !SingleInstanceManager.TryBecomePrimary())
            {
                SingleInstanceManager.SignalPrimary(launchArgs);
                Exit();
                return;
            }

            if (!allowSecondaryInstance)
            {
                SingleInstanceManager.StartListening(HandleExternalLaunch);
            }

            AppIdentityService.EnsureConfigured();
            RegisterNotifications();

            window ??= new Window { Title = "iMessage for Windows" };
            window.AppWindow.Resize(new SizeInt32(1180, 760));
            SetAppIcon(window);
            minimizeHook ??= new WindowMinimizeHook(window, WindowShell, () => mainPage?.ShouldMinimizeToTray == true);
            window.AppWindow.Closing += OnAppWindowClosing;

            if (window.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                window.Content = rootFrame;
            }

            WriteStartupTrace("Before MainPage navigation");
            _ = rootFrame.Navigate(typeof(MainPage), e.Arguments);
            WriteStartupTrace("After MainPage navigation");
            HandleExternalLaunch(launchArgs);
            RestoreMainWindow();
        }

        private static bool ShouldAllowSecondaryInstance(string[] args)
        {
            return string.Equals(Environment.GetEnvironmentVariable("WINIMSG_ALLOW_SECONDARY_INSTANCE"), "1", StringComparison.Ordinal) ||
                args.Any(arg => string.Equals(arg, "--allow-secondary-instance", StringComparison.OrdinalIgnoreCase));
        }

        public nint MainWindowHandle => window is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(window);

        public void RegisterMainPage(MainPage page)
        {
            mainPage = page;
        }

        public void SetMainWindowIcon(string? iconPath)
        {
            if (dispatcherQueue is not null && !dispatcherQueue.HasThreadAccess)
            {
                _ = dispatcherQueue.TryEnqueue(() => SetMainWindowIcon(iconPath));
                return;
            }

            if (window is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
            {
                window.AppWindow.SetIcon(iconPath);
                return;
            }

            SetAppIcon(window);
        }

        public void RestoreMainWindow()
        {
            if (dispatcherQueue is not null && !dispatcherQueue.HasThreadAccess)
            {
                _ = dispatcherQueue.TryEnqueue(RestoreMainWindow);
                return;
            }

            if (window is null)
            {
                return;
            }

            window.Activate();
            WindowShell.Show(window);
            window.Activate();
        }

        public void HideMainWindow()
        {
            if (dispatcherQueue is not null && !dispatcherQueue.HasThreadAccess)
            {
                _ = dispatcherQueue.TryEnqueue(HideMainWindow);
                return;
            }

            if (window is not null)
            {
                WindowShell.Hide(window);
            }
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void HandleExternalLaunch(string[] args)
        {
            dispatcherQueue?.TryEnqueue(() =>
            {
                RestoreMainWindow();
                if (TryGetWebCompanionLaunchNonce(args) is { } nonce)
                {
                    mainPage?.AuthorizeWebCompanionLaunch(nonce);
                }

                if (args.Length >= 2 && string.Equals(args[0], "--open-chat", StringComparison.OrdinalIgnoreCase))
                {
                    mainPage?.SelectChat(args[1]);
                }
            });
        }

        public static string? TryGetWebCompanionLaunchNonce(IEnumerable<string> args)
        {
            foreach (var arg in args)
            {
                if (!Uri.TryCreate(arg, UriKind.Absolute, out var uri) ||
                    !string.Equals(uri.Scheme, AppIdentityService.ProtocolScheme, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length == 2 && string.Equals(parts[0], "nonce", StringComparison.OrdinalIgnoreCase))
                    {
                        return Uri.UnescapeDataString(parts[1]);
                    }
                }
            }

            return null;
        }

        private void RegisterNotifications()
        {
            try
            {
                AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
                AppNotificationManager.Default.Register();
            }
            catch
            {
                // Notifications can fail for elevated or unpackaged dev runs; the UI remains usable.
            }
        }

        private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
        {
            dispatcherQueue?.TryEnqueue(() =>
            {
                RestoreMainWindow();
                if (args.Arguments.TryGetValue("chat", out var chatId))
                {
                    mainPage?.SelectChat(chatId);
                }
            });
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode != PowerModes.Resume)
            {
                return;
            }

            dispatcherQueue?.TryEnqueue(() => mainPage?.HandleSystemResume());
        }

        private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (!isExiting && mainPage?.ShouldMinimizeToTray == true)
            {
                args.Cancel = true;
                HideMainWindow();
            }
        }

        public void ExitApplication()
        {
            if (dispatcherQueue is not null && !dispatcherQueue.HasThreadAccess)
            {
                _ = dispatcherQueue.TryEnqueue(ExitApplication);
                return;
            }

            isExiting = true;
            minimizeHook?.Dispose();
            minimizeHook = null;
            mainPage?.DisposeTrayIcon();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SingleInstanceManager.Stop();
            if (window is not null)
            {
                window.AppWindow.Closing -= OnAppWindowClosing;
                window.Close();
            }

            Exit();
        }

        private static void SetAppIcon(Window targetWindow)
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Messages.ico");
            if (File.Exists(iconPath))
            {
                targetWindow.AppWindow.SetIcon(iconPath);
            }
        }

        private static void WriteStartupTrace(string message, Exception? exception = null)
        {
            try
            {
                var logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinIMsg",
                    "logs");
                Directory.CreateDirectory(logDirectory);
                var detail = exception is null
                    ? message
                    : $"{message}: {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception}";
                File.AppendAllText(
                    Path.Combine(logDirectory, "startup.log"),
                    $"{DateTimeOffset.Now:O} {detail}{Environment.NewLine}");
            }
            catch
            {
                // Startup diagnostics must never become a launch dependency.
            }
        }
    }
}
