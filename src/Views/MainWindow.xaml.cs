using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Vantage.Models;
using Vantage.Services;
using Vantage.Services.Agent;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinRT.Interop;

namespace Vantage;

public sealed partial class MainWindow : Window
{
    private readonly LocalHistoryStore _historyStore = new();
    private readonly ProviderStore _providerStore = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ObservableCollection<Provider> _providers = new();
    private readonly VisionCapability _visionCapability = new();
    private readonly List<ModelChoice> _modelChoices = new();
    private Conversation? _activeConversation;
    private CancellationTokenSource? _responseCts;
    private CancellationTokenSource? _agentRunCts;
    private bool _loaded;
    private const string SelectedModelSettingKey = "SelectedModel";

    public MainWindow()
    {
        App.LogStartup("MainWindow constructor starting");
        InitializeComponent();
        App.LogStartup("MainWindow XAML initialized");
        AddKeyboardAccelerators();
        App.LogStartup("Keyboard accelerators set");
        ApplyCurrentTheme();
        App.LogStartup("Theme applied");

        try
        {
            SystemBackdrop = null;
            App.LogStartup("Backdrop: solid (Mica disabled for crisp rendering)");
        }
        catch (Exception ex)
        {
            App.LogStartup($"Backdrop setup skipped: {ex.Message}");
        }

        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        }
        catch (Exception ex)
        {
            App.LogStartup($"Window icon skipped: {ex.Message}");
        }

        // Final-flush hook — when the user closes the window (X button,
        // Alt+F4, taskbar close), synchronously persist conversations +
        // providers to disk before the process tears down. Without this
        // anything that's only in memory at shutdown is lost.
        try
        {
            AppWindow.Closing += (sender, args) =>
            {
                CommonUtils.LogDiagnostic("window-closing-flush",
                    "running PersistSync + SaveProviders before process exit");
                FlushStateBeforeExit();
                // Force a final flush of the buffered diagnostic / verbose
                // log writers so the last ~15 entries don't get truncated
                // when the long-lived StreamWriter is closed.
                CommonUtils.FlushLogs();
            };
        }
        catch (Exception ex)
        {
            App.LogStartup($"Window.Closing wire failed: {ex.Message}");
        }

        try
        {
            if (AppWindow.TitleBar is { } tb)
            {
                tb.ExtendsContentIntoTitleBar = true;
                tb.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;

                // Restyle the system title-bar buttons (close / minimize /
                // maximize) so they blend into the flat app palette instead
                // of using Windows' default light chrome that clashes with
                // the dark UI. Resting state is fully transparent, hover is
                // a barely-there tint (16/255 black), pressed is a more
                // visible tint (38/255 black). Foreground picks up the
                // PrimaryTextBrush color so the glyphs read cleanly; the
                // inactive state uses MutedTextBrush so disabled-style
                // dimming doesn't visually compete with the active icons.
                var fgRest     = Windows.UI.Color.FromArgb(0xFF, 0x10, 0x1A, 0x24);
                var fgHover    = Windows.UI.Color.FromArgb(0xFF, 0x10, 0x1A, 0x24);
                var fgPressed  = Windows.UI.Color.FromArgb(0xFF, 0x10, 0x1A, 0x24);
                var fgInactive = Windows.UI.Color.FromArgb(0xFF, 0x94, 0xA0, 0xA6);
                var bgRest     = Microsoft.UI.Colors.Transparent;
                var bgHover    = Windows.UI.Color.FromArgb(0x16, 0x07, 0x14, 0x1A);
                var bgPressed  = Windows.UI.Color.FromArgb(0x26, 0x07, 0x14, 0x1A);
                var bgInactive = Microsoft.UI.Colors.Transparent;

                tb.ButtonBackgroundColor        = bgRest;
                tb.ButtonForegroundColor        = fgRest;
                tb.ButtonHoverBackgroundColor   = bgHover;
                tb.ButtonHoverForegroundColor   = fgHover;
                tb.ButtonPressedBackgroundColor = bgPressed;
                tb.ButtonPressedForegroundColor = fgPressed;
                tb.ButtonInactiveBackgroundColor = bgInactive;
                tb.ButtonInactiveForegroundColor = fgInactive;
            }
        }
        catch (Exception ex)
        {
            App.LogStartup($"Title bar extend skipped: {ex.Message}");
        }

        try
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
            bool launchMaximized = true;
            if (localSettings.Values.TryGetValue("LaunchMaximized", out var val) && val is bool b)
            {
                launchMaximized = b;
            }

            if (launchMaximized && AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlappedPresenter)
            {
                overlappedPresenter.Maximize();
            }
            else
            {
                AppWindow.Resize(new SizeInt32(1180, 760));
            }
        }
        catch (Exception ex)
        {
            App.LogStartup($"Window sizing skipped: {ex.Message}");
        }

        _providers.CollectionChanged += OnProvidersChanged;
    }

    public ObservableCollection<Conversation> Conversations { get; } = new();

    public ObservableCollection<Conversation> FilteredConversations { get; } = new();

    public ObservableCollection<SearchResult> SearchResults { get; } = new();

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        // Wire BrushResolver to RootGrid.Resources so non-UI code
        // (ChatMessage brushes, etc.) can look up theme-aware brushes
        // by key. SolidColorBrush references stay valid across theme
        // switches because ThemeManager.Apply mutates .Color in place.
        Vantage.Services.BrushResolver.Attach(RootGrid.Resources);

        ConversationList.ItemsSource = FilteredConversations;
        SearchResultsList.ItemsSource = SearchResults;
        LoadSettings();
        LoadProviders();
        RenderProviderCards();
        UpdateSidebarVisibility();
        UpdateComposerFocus();

        await LoadConversationsAsync();
        InputBox.Focus(FocusState.Programmatic);

        ShowPage("providers");
    }
}
