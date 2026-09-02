// SPDX-License-Identifier: MIT
// Vantage — MainWindow.Providers.cs
//
// AI Providers page: load from settings, model selector with persistence,
// card rendering via ProviderCardRenderer, test/delete/add dialogs, and
// keyboard accelerator registration.

using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vantage.Models;
using Vantage.Services;
using Vantage.Services.Agent;
using Windows.Foundation;
using Windows.System;

namespace Vantage;

public sealed partial class MainWindow
{
    private void LoadProviders()
    {
        // Detach the auto-save handler for the duration of the load.
        // The handler fires on Add / Remove / Clear, so without this
        // the _providers.Clear() at the top would write an empty list
        // to disk *before* _providerStore.Load() has a chance to run
        // the LocalSettings → file migration, which would then see a
        // zero-byte file and skip the migration entirely. The bug
        // would manifest as "I had providers in v1.0 and now they're
        // gone" on the first launch of v1.5.36+.
        _providers.CollectionChanged -= OnProvidersChanged;
        try
        {
            _providers.Clear();
            foreach (var provider in _providerStore.Load())
            {
                _providers.Add(provider);
            }
        }
        finally
        {
            _providers.CollectionChanged += OnProvidersChanged;
        }
        RefreshModelSelector();
    }

    private void RefreshModelSelector()
    {
        // Capture the user's current selection key so we can restore it
        // after rebuilding the list — survives providers being added /
        // removed / re-enabled without surprising the user.
        var persistedKey = GetSetting(SelectedModelSettingKey, string.Empty);

        _modelChoices.Clear();

        foreach (var provider in _providers.Where(p => p.IsEnabled))
        {
            _modelChoices.Add(new ModelChoice
            {
                Provider = provider,
                ModelId = provider.DefaultModel,
                Display = ModelDisplayNameService.GetFriendlyName(provider.DefaultModel),
                Key = $"{provider.Id}|{provider.DefaultModel}"
            });
        }

        ModelSelector.ItemsSource = null;
        ModelSelector.ItemsSource = _modelChoices;

        if (_modelChoices.Count == 0)
        {
            ModelSelector.PlaceholderText = "Add a provider in AI Providers →";
            ModelSelector.IsEnabled = false;
            UpdateConversationSubtitle(null);
            return;
        }

        ModelSelector.IsEnabled = true;

        // Restore previously-persisted selection if the matching choice
        // survived; otherwise default to index 0.
        var idx = 0;
        if (!string.IsNullOrEmpty(persistedKey))
        {
            for (var i = 0; i < _modelChoices.Count; i++)
            {
                if (_modelChoices[i].Key == persistedKey)
                {
                    idx = i;
                    break;
                }
            }
        }
        ModelSelector.SelectedIndex = idx;
        UpdateConversationSubtitle(_modelChoices.Count > 0 ? _modelChoices[idx] : null);
    }

    /// <summary>
    /// Mirror the active model/provider into the conversation header
    /// subtitle so the user always knows what the agent runs against.
    /// </summary>
    private void UpdateConversationSubtitle(ModelChoice? choice)
    {
        if (ConversationSubtitleBlock is null) return;
        ConversationSubtitleBlock.Text = choice is null
            ? "Add a provider to start · Direct Windows desktop agent"
            : $"{choice.Display} · {choice.Provider.Name}";
    }

    private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelSelector.SelectedItem is not ModelChoice choice) return;
        LocalPreferences.SetString(SelectedModelSettingKey, choice.Key);
        UpdateConversationSubtitle(choice);
    }

    private void OnProvidersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SaveProviders();
        RenderProviderCards();
        RefreshModelSelector();
    }

    private void SaveProviders()
    {
        CancelPendingProviderSave();
        var cts = new CancellationTokenSource();
        _providerSaveCts = cts;
        _ = SaveProvidersAfterDelayAsync(cts);
    }

    private async Task SaveProvidersAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(300, cts.Token);
            _providerStore.Save(_providers);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("provider-save-failed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_providerSaveCts, cts))
            {
                _providerSaveCts = null;
            }
            cts.Dispose();
        }
    }

    private void CancelPendingProviderSave()
    {
        var pending = _providerSaveCts;
        _providerSaveCts = null;
        if (pending is null) return;

        try
        {
            pending.Cancel();
        }
        catch
        {
            // The delayed task may already have completed and disposed
            // its token source. There is nothing left to cancel then.
        }
    }

    private void RenderProviderCards()
    {
        if (ProvidersList is null) return;

        ProvidersList.Children.Clear();
        foreach (var provider in _providers)
        {
            ProvidersList.Children.Add(BuildProviderCard(provider));
        }
    }

    private UIElement BuildProviderCard(Provider provider)
    {
        // Card layout lives in Services/ProviderCardRenderer.cs.
        var ctx = new ProviderCardContext
        {
            Host = RootGrid,
            VisionCapability = _visionCapability,
            SaveProviders = SaveProviders,
            TestProvider = TestProviderAsync,
            DeleteProvider = DeleteProviderAsync,
        };
        return ProviderCardRenderer.Build(provider, ctx);
    }

    private async Task TestProviderAsync(Provider provider)
    {
        provider.Status = ProviderStatus.Untested;
        provider.LastTestedAt = DateTimeOffset.Now;
        provider.LastTestMessage = "Testing…";
        SaveProviders();
        RenderProviderCards();

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            provider.Status = ProviderStatus.Failed;
            provider.LastTestMessage = "Base URL is empty.";
            SaveProviders();
            RenderProviderCards();
            return;
        }

        if (string.IsNullOrWhiteSpace(provider.ApiKey) || string.IsNullOrWhiteSpace(provider.DefaultModel))
        {
            provider.Status = ProviderStatus.Failed;
            provider.LastTestMessage = "API key and model are required.";
            SaveProviders();
            RenderProviderCards();
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var engine = LMMEngine.Create(provider);
            var agent = new LmmAgent(engine, "Reply with OK.");
            agent.AddTextMessage("Connection test", role: "user");
            await agent.GetResponseAsync(temperature: 0, maxTokens: 8, timeout.Token);

            var vision = await _visionCapability.SupportsAsync(provider);
            provider.Status = vision == VisionVerdict.No ? ProviderStatus.Failed : ProviderStatus.Ok;
            provider.LastTestMessage = vision switch
            {
                VisionVerdict.Yes => "Authenticated · vision ready.",
                VisionVerdict.No => "Authenticated, but this model has no vision support.",
                _ => "Authenticated · vision support could not be confirmed.",
            };
        }
        catch (OperationCanceledException)
        {
            provider.Status = ProviderStatus.Failed;
            provider.LastTestMessage = "Connection test timed out.";
        }
        catch (LmmProviderException ex)
        {
            provider.Status = ProviderStatus.Failed;
            provider.LastTestMessage = $"Provider rejected the test (HTTP {ex.HttpStatus}): {ex.Message}";
        }
        catch (Exception ex)
        {
            provider.Status = ProviderStatus.Failed;
            provider.LastTestMessage = ex.Message;
        }
        finally
        {
            provider.LastTestedAt = DateTimeOffset.Now;
            SaveProviders();
            RenderProviderCards();
        }
    }

    private async Task DeleteProviderAsync(Provider provider)
    {
        var dialog = new ContentDialog
        {
            Title = "Remove provider?",
            Content = $"This will remove \"{provider.Name}\" from Vantage. Any history referring to it will stay intact, but new requests will not use it.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _providers.Remove(provider);
        }
    }

    private async void AddProviderButton_Click(object sender, RoutedEventArgs e)
    {
        // Wraps AddProviderFlowAsync in a try/catch so the click handler
        // is crash-safe. The previous version of this method hard-crashed
        // the whole process when one of the resource lookups threw
        // COMException for a missing key. Now any unexpected error
        // surfaces as an `add-provider-failed` diagnostic line.
        try { await AddProviderFlowAsync(); }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("add-provider-failed", ex.Message);
        }
    }

    /// <summary>Real body of the Add Provider flow.</summary>
    private async Task AddProviderFlowAsync()
    {
        // WinUI's ResourceDictionary indexer THROWS when a key is missing
        // (it returns null for ordinary Dictionary<,>). Always use the
        // TryGetValue helper here so a missing resource name can't take
        // down the whole click handler and hard-crash the app.
        var secondaryTextBrush = ResolveBrushOrFallback("SecondaryTextBrush", fallback: null!);
        var errorBrush         = ResolveBrushOrFallback("ErrorTextBrush", fallback: secondaryTextBrush);

        var nameBox = new TextBox
        {
            PlaceholderText = "Mistral, LM Studio, vLLM…",
            Header = "Display name",
        };
        var urlBox = new TextBox
        {
            PlaceholderText = "api.example.com/v1",
            Header = "Base URL",
        };
        var keyBox = new PasswordBox
        {
            PlaceholderText = "Paste your API key (stays on this PC)",
            Header = "API key",
        };
        var modelBox = new TextBox
        {
            PlaceholderText = "model-id",
            Header = "Default model",
        };

        // Inline error block — only visible after a failed Add attempt so
        // the form doesn't open looking angry. Once validation fails, we
        // keep the dialog open and refocus the first empty field until
        // every required field has content.
        var errorBlock = new TextBlock
        {
            FontSize = 12,
            Foreground = errorBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
            Text = "",
        };

        var description = new TextBlock
        {
            FontSize = 13,
            Foreground = secondaryTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Text = "Add your own OpenAI-compatible endpoint. All four fields are required so the provider can be saved and used.",
        };
        var fields = new StackPanel { Spacing = 14, Margin = new Thickness(0, 12, 0, 0) };
        fields.Children.Add(nameBox);
        fields.Children.Add(urlBox);
        fields.Children.Add(keyBox);
        fields.Children.Add(modelBox);
        fields.Children.Add(errorBlock);

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(description);
        stack.Children.Add(fields);

        var dialog = new ContentDialog
        {
            Title = "Add provider",
            Content = stack,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };

        // ── Validation + close-interception ──────────────────────────
        // ContentDialog's default behavior dismisses the dialog when the
        // user clicks the dimmed overlay or hits Escape, even if the form
        // is empty. We intercept that by hooking `Closing` and cancelling
        // when Primary is selected without all required fields filled.
        dialog.Closing += (s, args) =>
        {
            // User picked Cancel / hit Escape / clicked the dimmed
            // overlay → Result is None. Let those close.
            if (args.Result != ContentDialogResult.Primary) return;

            var name  = nameBox.Text?.Trim()  ?? "";
            var url   = urlBox.Text?.Trim()   ?? "";
            var key   = keyBox.Password       ?? "";
            var model = modelBox.Text?.Trim() ?? "";

            var missing = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(name))  missing.Add("Display name");
            if (string.IsNullOrWhiteSpace(url))   missing.Add("Base URL");
            if (string.IsNullOrWhiteSpace(key))   missing.Add("API key");
            if (string.IsNullOrWhiteSpace(model)) missing.Add("Default model");

            if (missing.Count > 0)
            {
                errorBlock.Text = "Please fill in every field. Missing: "
                                  + string.Join(", ", missing)
                                  + ".";
                errorBlock.Visibility = Visibility.Visible;
                args.Cancel = true;  // keep the dialog open

                // Refocus the first missing field so the user can tab
                // through instead of having to click each box.
                FrameworkElement? firstMissing =
                    string.IsNullOrWhiteSpace(name)  ? (FrameworkElement)nameBox  :
                    string.IsNullOrWhiteSpace(url)   ? urlBox  :
                    string.IsNullOrWhiteSpace(key)   ? keyBox  :
                    modelBox;
                try { firstMissing.Focus(FocusState.Programmatic); } catch { }
                return;
            }

            // All filled — fall through and let the dialog close. We've
            // already cached the trimmed values into Text via direct
            // assignment below.
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            // User explicitly cancelled — clean up without saving.
            return;
        }

        // All four fields are guaranteed non-empty by the Closing handler.
        var name = nameBox.Text.Trim();
        var url = urlBox.Text.Trim();
        var key = keyBox.Password;
        var model = modelBox.Text.Trim();

        var newProvider = new Provider
        {
            Kind = ProviderKind.Custom,
            Name = name,
            BaseUrl = url,
            ApiKey = key,
            DefaultModel = model,
            IsEnabled = true,
            Status = ProviderStatus.Untested,
        };
        _providers.Add(newProvider);
        // OnProvidersChanged → SaveProviders fires automatically once the
        // collection update is committed, so the provider is persisted to
        // LocalSettings without a separate manual save call.
        CommonUtils.LogDiagnostic("provider-added",
            $"name=\"{name}\" baseUrl=\"{url}\" model=\"{model}\"");
    }

    private void AddKeyboardAccelerators()
    {
        AddKeyboardAccelerator(VirtualKey.N, VirtualKeyModifiers.Control, NewChatAccelerator_Invoked);
        AddKeyboardAccelerator(VirtualKey.K, VirtualKeyModifiers.Control, SearchAccelerator_Invoked);
        AddKeyboardAccelerator(VirtualKey.L, VirtualKeyModifiers.Control, InputAccelerator_Invoked);
        AddKeyboardAccelerator(VirtualKey.B, VirtualKeyModifiers.Control, SidebarToggleAccelerator_Invoked);
        AddKeyboardAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, EscapeAccelerator_Invoked);
    }

    private void AddKeyboardAccelerator(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers
        };
        accelerator.Invoked += handler;
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void InputAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ShowPage("chat");
        InputBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Safe resource lookup. WinUI's <c>ResourceDictionary</c> indexer
    /// THROWS a COMException when the key isn't present — it does NOT
    /// return null like ordinary dictionary indexers. So
    /// <c>(Brush)Resources["ErrorTextBrush"]</c> can hard-crash the
    /// process on a missing key. This helper uses TryGetValue so a typo
    /// in a resource name surfaces as a logged fallback instead of a
    /// process kill. <paramref name="fallback"/> is returned when the
    /// key is missing OR the resolved value isn't a Brush.
    /// </summary>
    private Brush ResolveBrushOrFallback(string key, Brush fallback)
    {
        try
        {
            if (RootGrid.Resources.TryGetValue(key, out var raw) && raw is Brush b)
                return b;
        }
        catch (Exception ex)
        {
            CommonUtils.LogDiagnostic("resource-lookup-failed",
                $"key={key} {ex.GetType().Name}: {ex.Message}");
        }
        if (fallback is null)
        {
            // Last-ditch: hand back a flat grey so we never return null.
            return new SolidColorBrush(Microsoft.UI.Colors.Gray);
        }
        return fallback;
    }
}
