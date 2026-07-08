using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Vantage.Models;

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private string? _imagePath;
    private bool _isError;
    private Brush? _overrideBubbleBrush;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Role { get; set; } = "assistant";

    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value))
            {
                OnPropertyChanged(nameof(TextVisibility));
                OnPropertyChanged(nameof(PreviewText));
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(BubbleBrush));
                OnPropertyChanged(nameof(BubbleBorderBrush));
                OnPropertyChanged(nameof(AuthorBrush));
                OnPropertyChanged(nameof(AuthorDisplay));
                OnPropertyChanged(nameof(CleanText));
            }
        }
    }

    public string? ImagePath
    {
        get => _imagePath;
        set
        {
            if (SetField(ref _imagePath, value))
            {
                OnPropertyChanged(nameof(ImageSource));
                OnPropertyChanged(nameof(ImageVisibility));
            }
        }
    }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// True when the message holds an error notice (e.g. "[Agent error] …").
    /// The chat template picks a danger-tinted bubble so the user spots
    /// the failure without parsing text.
    /// </summary>
    [JsonIgnore]
    public bool IsError
    {
        get => _isError || (!string.IsNullOrEmpty(_text) &&
            (_text.StartsWith("[Agent error", StringComparison.OrdinalIgnoreCase)
             || _text.StartsWith("[Provider error", StringComparison.OrdinalIgnoreCase)
             || _text.StartsWith("[Configuration", StringComparison.OrdinalIgnoreCase)));
        set => SetField(ref _isError, value);
    }

    /// <summary>Override the auto-detected bubble background (used by error styling).</summary>
    public Brush? OverrideBubbleBrush
    {
        get => _overrideBubbleBrush;
        set { if (SetField(ref _overrideBubbleBrush, value)) OnPropertyChanged(nameof(BubbleBrush)); }
    }

    [JsonIgnore]
    public string AuthorDisplay => IsError ? "Vantage" :
        (Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "You" : "Vantage");

    [JsonIgnore]
    public string Author => AuthorDisplay;

    [JsonIgnore]
    public string TimeLabel => CreatedAt.ToLocalTime().ToString("h:mm tt");

    [JsonIgnore]
    public HorizontalAlignment BubbleAlignment => Role.Equals("user", StringComparison.OrdinalIgnoreCase)
        ? HorizontalAlignment.Right
        : HorizontalAlignment.Left;

    [JsonIgnore]
    public Visibility ImageVisibility => string.IsNullOrWhiteSpace(ImagePath) ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public Visibility TextVisibility => string.IsNullOrWhiteSpace(Text) ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public ImageSource? ImageSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ImagePath))
            {
                return null;
            }

            try
            {
                var uri = Uri.TryCreate(ImagePath, UriKind.Absolute, out var parsed)
                    ? parsed
                    : new Uri(Path.GetFullPath(ImagePath));

                return new BitmapImage(uri);
            }
            catch
            {
                return null;
            }
        }
    }

    [JsonIgnore]
    public string PreviewText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Text))
            {
                var t = Text;
                if (t.StartsWith("[Agent error", StringComparison.OrdinalIgnoreCase))
                    return "⚠ " + (t.Length <= 92 ? t : t.Substring(0, 89) + "...");
                return t.Length <= 92 ? t : $"{t[..89]}...";
            }

            return string.IsNullOrWhiteSpace(ImagePath) ? "Empty message" : "Image";
        }
    }

    /// <summary>Strip leading error markers for a cleaner rendered body.</summary>
    [JsonIgnore]
    public string CleanText
    {
        get
        {
            if (string.IsNullOrEmpty(_text)) return string.Empty;
            var t = _text;
            // "[Agent error] some message" → "some message"
            var bracket = t.IndexOf("] ", StringComparison.Ordinal);
            if (t.StartsWith("[", StringComparison.Ordinal) && bracket > 0 && bracket < 24)
                t = t.Substring(bracket + 2);
            return t;
        }
    }

    /// <summary>User → neutral surface, assistant → light accent, error → soft danger.
    /// All brushes are theme-aware: lookups go through BrushResolver which reads
    /// from RootGrid.Resources; ThemeManager.Apply mutates the underlying
    /// SolidColorBrush.Color in place when the user switches palette / mode.</summary>
    [JsonIgnore]
    public Brush BubbleBrush
    {
        get
        {
            if (_overrideBubbleBrush is { } b) return b;
            var key = IsError ? "BubbleErrorBrush"
                    : Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                        ? "BubbleUserBrush"
                        : "BubbleAssistantBrush";
            return Vantage.Services.BrushResolver.GetOrDefault(key,
                IsError ? Color.FromArgb(0xFF, 0xFB, 0xEE, 0xE8)
                : Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0xFF, 0xEC, 0xF3, 0xFB));
        }
    }

    [JsonIgnore]
    public Brush BubbleBorderBrush
    {
        get
        {
            var key = IsError ? "BubbleBorderErrorBrush"
                    : Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                        ? "BubbleBorderUserBrush"
                        : "BubbleBorderAssistantBrush";
            return Vantage.Services.BrushResolver.GetOrDefault(key,
                IsError ? Color.FromArgb(0xFF, 0xE9, 0xB9, 0xA7)
                : Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(0x14, 0x07, 0x14, 0x1A)
                    : Color.FromArgb(0x26, 0x00, 0x78, 0xD4));
        }
    }

    [JsonIgnore]
    public Brush AuthorBrush
    {
        get
        {
            var key = IsError ? "AuthorErrorBrush" : "AuthorTextBrush";
            return Vantage.Services.BrushResolver.GetOrDefault(key,
                IsError ? Color.FromArgb(0xFF, 0xA8, 0x32, 0x32)
                : Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
        }
    }

    /// <summary>Approximate single-letter initial shown in the bubble avatar.</summary>
    [JsonIgnore]
    public string AuthorInitial => IsError ? "!" :
        Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "Y" : "V";

    [JsonIgnore]
    public Brush AvatarBrush
    {
        get
        {
            var key = IsError ? "AvatarErrorBrush"
                    : Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                        ? "AvatarUserBrush"
                        : "AvatarAssistantBrush";
            return Vantage.Services.BrushResolver.GetOrDefault(key,
                IsError ? Color.FromArgb(0xFF, 0xFB, 0xEE, 0xE8)
                : Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(0xFF, 0xE2, 0xE9, 0xEE)
                    : Color.FromArgb(0xFF, 0xD6, 0xE7, 0xF8));
        }
    }

    public void AppendText(string text)
    {
        Text += text;
    }

    /// <summary>
    /// Set when this assistant message hosts the live agent-run
    /// visualization. The MessageBubble DataTemplate switches between
    /// the text bubble and the structured view based on this flag —
    /// turning the old "raw markdown" log into a stepper + progress
    /// bar + counter strip.
    /// </summary>
    [JsonIgnore]
    public bool IsAgentRun
    {
        get => _isAgentRun;
        set { if (SetField(ref _isAgentRun, value)) OnPropertyChanged(nameof(AgentRunVisibility)); }
    }
    private bool _isAgentRun;

    /// <summary>The live AgentRun state, if this message is an agent run.</summary>
    [JsonIgnore]
    public AgentRunViewModel? AgentRun
    {
        get => _agentRun;
        set { if (SetField(ref _agentRun, value)) OnPropertyChanged(nameof(AgentRunVisibility)); }
    }
    private AgentRunViewModel? _agentRun;

    [JsonIgnore]
    public Microsoft.UI.Xaml.Visibility AgentRunVisibility =>
        IsAgentRun && _agentRun != null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
