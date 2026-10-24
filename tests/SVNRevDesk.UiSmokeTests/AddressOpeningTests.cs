using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class AddressOpeningTests
{
    private const string Url = "http://example.invalid/svn/test";

    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        await VerifyOpenAsync("button", cancel: false, wrongPassword: false, enter: false);
        await VerifyOpenAsync("enter", cancel: false, wrongPassword: false, enter: true);
        await VerifyOpenAsync("cancel", cancel: true, wrongPassword: false, enter: false);
        await VerifyOpenAsync("wrong-password", cancel: false, wrongPassword: true, enter: false);

        using var failed = new MainViewModel(new UserSettings(Path.Combine(output, "network-error")),
            new AuthenticatedRepository(output) { Failure = new IOException("测试网络不可用") });
        await failed.OpenInBackgroundAsync(Url, false);
        Check(!failed.HasSession && failed.EmptyStateTitle == "仓库打开失败"
            && failed.EmptyStateDescription.Contains("测试网络不可用"), "An initial network failure remains visible in the empty view");

        async Task VerifyOpenAsync(string name, bool cancel, bool wrongPassword, bool enter)
        {
            var settings = new UserSettings(Path.Combine(output, name)) { CloseBehavior = WindowCloseBehavior.Exit, AutoRefresh = false };
            var repository = new AuthenticatedRepository(output);
            var window = new MainWindow(settings, repository) { Left = -8000, Top = -8000, ShowActivated = false };
            typeof(MainWindow).GetField("windowLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            var model = (MainViewModel)window.DataContext;
            repository.HasCredentials = () => model.Runner.Username == "test-user" && model.Runner.Password == "test-secret";
            var prompted = false;
            var finished = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<InputDialog>().FirstOrDefault();
                if (dialog == null || prompted) return;
                prompted = true;
                Check(!model.HasSession && !model.IsReading && model.EmptyStateTitle == "仓库需要登录",
                    "First authentication failure shows a login state before prompting");
                render(dialog, Path.Combine(output, name + "-login.png"), 580, 370);
                if (!cancel)
                {
                    Descendants<TextBox>(dialog).Single().Text = "test-user";
                    Descendants<PasswordBox>(dialog).Single().Password = wrongPassword ? "wrong-secret" : "test-secret";
                }
                dialog.DialogResult = !cancel;
            };
            try
            {
                window.Show(); window.UpdateLayout();
                var address = (ComboBox)window.FindName("AddressBox");
                address.ApplyTemplate();
                ((TextBox)address.Template.FindName("PART_EditableTextBox", address)).Text = Url + "/";
                Check(model.Address == Url + "/", "Pasting into the actual editable address control updates the open target");
                timer.Start();
                model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsReading) && !model.IsReading && prompted) finished = true; };
                if (enter)
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Enter)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    address.RaiseEvent(args);
                    Check(args.Handled, "Enter in the address bar is handled");
                }
                else Descendants<Button>(window).Single(button => Equals(button.Content, "打开地址"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => prompted && (cancel || finished));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (cancel || wrongPassword)
                    Check(!model.HasSession && model.EmptyStateTitle == "仓库需要登录" && model.EmptyStateDescription.Contains("账号密码"),
                        "Cancelling or rejecting credentials retains an actionable login message");
                else
                    Check(model.Session?.Url == Url && model.Entries.Count == 4 && repository.OpenCount == 2,
                        "First open automatically retries with session credentials and displays repository entries");
                Check(((ListBox)window.FindName("SessionTabs")).Items.Count == 1, "Retry reuses the initial repository tab");
                render(window, Path.Combine(output, name + "-result.png"), 1360, 780);
            }
            finally { timer.Stop(); window.Close(); }
            var json = File.ReadAllText(Path.Combine(settings.StorageDirectory, "settings.json"));
            Check(!json.Contains("test-secret") && !json.Contains("wrong-secret") && !json.Contains("test-user"),
                "Authentication retry does not persist session credentials");
        }
    }

    private sealed class AuthenticatedRepository(string output) : IRepositoryService
    {
        private readonly BrowsingTests.ControlledRepository inner = new(output);
        public Func<bool> HasCredentials { get; set; } = () => false;
        public Exception? Failure { get; init; }
        public int OpenCount { get; private set; }
        public Task<RepositorySession> OpenUrlAsync(string url, CancellationToken token)
        {
            OpenCount++;
            if (Failure != null) throw Failure;
            if (!HasCredentials()) throw new SvnException("info", 1, "E215004: Authentication failed");
            return inner.OpenUrlAsync(url, token);
        }
        public Task<RepositorySession> OpenWorkingCopyAsync(string path, CancellationToken token) => inner.OpenWorkingCopyAsync(path, token);
        public Task<RepositorySnapshot> RefreshAsync(RepositorySession session, CancellationToken token) => inner.RefreshAsync(session, token);
        public Task<RepositorySnapshot> RefreshDirectoryAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token) => inner.RefreshDirectoryAsync(session, scope, token);
        public Task<IReadOnlyList<BrowserEntry>> ReadDirectoryAsync(RepositorySession session, RepositorySnapshot snapshot, string relative, CancellationToken token) => inner.ReadDirectoryAsync(session, snapshot, relative, token);
    }

    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition()) { if (timeout.Elapsed.TotalSeconds > 15) throw new TimeoutException("Address opening did not settle"); await Task.Delay(20); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
