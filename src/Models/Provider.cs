using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Vantage.Models;

public enum ProviderKind
{
    BuiltIn,
    Custom
}

public enum ProviderStatus
{
    Untested,
    Ok,
    Failed
}

/// <summary>
/// User-pinned vision override for a provider. Default Auto lets the
/// heuristic + live probe decide. Pin ForceYes when you know the model
/// accepts images but the heuristic can't tell (common with custom
/// OpenRouter / Together / Fireworks endpoints); pin ForceNo for
/// text-only models.
/// </summary>
public enum VisionOverride
{
    Auto = 0,
    ForceYes = 1,
    ForceNo = 2,
}

public sealed class Provider : INotifyPropertyChanged
{
    private bool _isEnabled = true;
    private string _name = string.Empty;
    private string _baseUrl = string.Empty;
    private string _apiKey = string.Empty;
    private string _defaultModel = string.Empty;
    private ProviderStatus _status = ProviderStatus.Untested;
    private DateTimeOffset? _lastTestedAt;
    private string? _lastTestMessage;
    private VisionOverride _visionOverride = VisionOverride.Auto;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ProviderKind Kind { get; set; } = ProviderKind.Custom;

    public string BuiltInKey { get; set; } = string.Empty;

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set { if (_baseUrl != value) { _baseUrl = value; OnPropertyChanged(); } }
    }

    public string ApiKey
    {
        get => _apiKey;
        set { if (_apiKey != value) { _apiKey = value; OnPropertyChanged(); } }
    }

    public string DefaultModel
    {
        get => _defaultModel;
        set { if (_defaultModel != value) { _defaultModel = value; OnPropertyChanged(); } }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); } }
    }

    public ProviderStatus Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); } }
    }

    public DateTimeOffset? LastTestedAt
    {
        get => _lastTestedAt;
        set { if (_lastTestedAt != value) { _lastTestedAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastTestedText)); } }
    }

    public string? LastTestMessage
    {
        get => _lastTestMessage;
        set { if (_lastTestMessage != value) { _lastTestMessage = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// User-pinned vision-override for this provider. Default Auto lets
    /// the heuristic + live probe decide. Pin ForceYes when you know
    /// the model accepts images but the heuristic can't tell (common
    /// with custom OpenRouter / Together / Fireworks endpoints that
    /// route to a vision model under a non-standard name); pin ForceNo
    /// when configured for a text-only model.
    /// </summary>
    public VisionOverride VisionOverride
    {
        get => _visionOverride;
        set { if (_visionOverride != value) { _visionOverride = value; OnPropertyChanged(); } }
    }

    [JsonIgnore]
    public string StatusText => Status switch
    {
        ProviderStatus.Ok => "Connected",
        ProviderStatus.Failed => "Connection failed",
        _ => "Not tested"
    };

    [JsonIgnore]
    public string LastTestedText => LastTestedAt is null
        ? "Never tested"
        : LastTestedAt.Value.ToLocalTime().ToString("MMM d, h:mm tt");

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
