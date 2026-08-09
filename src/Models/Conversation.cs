using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Vantage.Models;

public sealed class Conversation : INotifyPropertyChanged
{
    private string _title = "New conversation";
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title
    {
        get => _title;
        set
        {
            if (SetField(ref _title, string.IsNullOrWhiteSpace(value) ? "New conversation" : value))
            {
                OnPropertyChanged(nameof(IconGlyph));
                OnPropertyChanged(nameof(IconBrush));
                OnPropertyChanged(nameof(IconBackgroundBrush));
            }
        }
    }

    public ObservableCollection<ChatMessage> Messages { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set
        {
            if (SetField(ref _updatedAt, value))
            {
                OnPropertyChanged(nameof(UpdatedLabel));
                OnPropertyChanged(nameof(LastMessagePreview));
            }
        }
    }

    [JsonIgnore]
    public string UpdatedLabel
    {
        get
        {
            var local = UpdatedAt.ToLocalTime();
            var now = DateTimeOffset.Now;

            if (local.Date == now.Date)
            {
                return local.ToString("h:mm tt");
            }

            if (local.Date == now.Date.AddDays(-1))
            {
                return "Yesterday";
            }

            return local.ToString("MMM d");
        }
    }

    [JsonIgnore]
    public string LastMessagePreview => Messages.LastOrDefault()?.PreviewText ?? string.Empty;

    [JsonIgnore]
    public string IconGlyph => IconVariant switch
    {
        1 => "\uE787",
        2 => "\uE9D2",
        3 => "\uE70F",
        4 => "\uE943",
        _ => "\uE8BD",
    };

    [JsonIgnore]
    public Brush IconBrush => new SolidColorBrush(IconVariant switch
    {
        1 => Color.FromArgb(255, 93, 66, 245),
        2 => Color.FromArgb(255, 20, 166, 106),
        3 => Color.FromArgb(255, 84, 66, 245),
        4 => Color.FromArgb(255, 232, 93, 4),
        _ => Color.FromArgb(255, 84, 66, 245),
    });

    [JsonIgnore]
    public Brush IconBackgroundBrush => new SolidColorBrush(IconVariant switch
    {
        1 => Color.FromArgb(255, 242, 238, 255),
        2 => Color.FromArgb(255, 232, 248, 240),
        3 => Color.FromArgb(255, 242, 238, 255),
        4 => Color.FromArgb(255, 255, 240, 230),
        _ => Color.FromArgb(255, 242, 238, 255),
    });

    private int IconVariant => (StringComparer.Ordinal.GetHashCode(Id) & 0x7FFFFFFF) % 5;

    public bool Contains(string query)
    {
        return Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || Messages.Any(message =>
                message.SearchableText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || (!string.IsNullOrWhiteSpace(message.ImagePath)
                    && message.ImagePath.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
    }

    public void Touch()
    {
        UpdatedAt = DateTimeOffset.Now;
        OnPropertyChanged(nameof(LastMessagePreview));
    }

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
