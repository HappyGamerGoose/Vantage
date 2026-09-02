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
    private CancellationTokenSource? _autoSaveCts;
    private CancellationTokenSource? _providerSaveCts;
    private readonly ObservableCollection<Provider> _providers = new();
    private readonly VisionCapability _visionCapability = new();
    private readonly List<ModelChoice> _modelChoices = new();
    private Conversation? _activeConversation;
    private Conversation? _responseConversation;
    private CancellationTokenSource? _responseCts;
    private CancellationTokenSource? _agentRunCts;
    private long _responseRequestVersion;
    private bool _loaded;
    private const string SelectedModelSettingKey = "SelectedModel";

    public MainWindow()
    {
        App.LogStartup("MainWindow constructor starting");
        InitializeComponent();
        App.LogStartup("MainWindow XAML initialized");

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
            App.LogStartup("Extended Mica titlebar initialized");
        }
        catch (Exception ex)
        {
            App.LogStartup($"Extended titlebar skipped: {ex.Message}");
        }

        AddKeyboardAccelerators();
        App.LogStartup("Keyboard accelerators set");
        ApplyCurrentTheme();
        App.LogStartup("Theme applied");

        try
        {
            SystemBackdrop = new MicaBackdrop
            {
                Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt,
            };
            App.LogStartup("Backdrop: Mica BaseAlt");
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
            var launchMaximized = LocalPreferences.GetBool("LaunchMaximized", true);

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
        // Conversations safety net: any add/remove/move on the
        // collection that doesn't go through a code path that awaits
        // PersistAsync would otherwise live only in memory until the
        // next close. OnConversationsChanged catches that case.
        Conversations.CollectionChanged += OnConversationsChanged;
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
        UpdateAboutVersionText();
        LoadProviders();
        RenderProviderCards();
        UpdateSidebarVisibility();
        UpdateComposerFocus();

        await LoadConversationsAsync();

        // Refresh the sidebar so the past-conversations list is
        // visible on first launch. LoadConversationsAsync adds the
        // history to `Conversations`, but the sidebar binds to
        // `FilteredConversations`, which is normally repopulated
        // by RefreshSearchAndConversations on user actions
        // (create / rename / delete / send). On a cold start
        // there's no user action yet, so the sidebar would render
        // empty until the user creates a new conversation —
        // exactly the "past conversations do not load" bug the
        // user just hit. Calling it here fills the list with the
        // loaded history before we show the chat surface.
        RefreshSearchAndConversations();

        // Always land on the fresh empty state, not the most recent
        // conversation. Resume-by-default is hostile: the user
        // opens the app wanting a fresh start and finds themselves
        // staring at the tail end of a session that ended hours ago.
        // The sidebar is right there — clicking a past conversation
        // is a single tap. ActivateConversation(null) shows the
        // EmptyState ("Type below to start.") and clears the
        // sidebar selection. Matches the README's flow: "The first
        // conversation is created when you send your first prompt."
        ActivateConversation(null);
        ShowPage("chat");

        RestoreComposerFocus();
    }
}
