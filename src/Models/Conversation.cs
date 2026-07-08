using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

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
        set => SetField(ref _title, string.IsNullOrWhiteSpace(value) ? "New conversation" : value);
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

    public bool Contains(string query)
    {
        return Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || Messages.Any(message =>
                message.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)
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
