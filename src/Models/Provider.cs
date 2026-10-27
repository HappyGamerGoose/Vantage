using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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

    /// <summary>
    /// Models offered by this endpoint. The first one is the primary model.
    /// <see cref="DefaultModel"/> mirrors it on disk so older builds can
    /// still read the file.
    /// </summary>
    public List<ProviderModel> Models { get; set; } = new();

    [JsonIgnore]
    public ProviderModel? PrimaryModel => Models.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Id));

    /// <summary>
    /// Upgrades a provider saved before multi-model support: its single
    /// <see cref="DefaultModel"/> becomes the first entry in <see cref="Models"/>,
    /// and the provider-wide vision override moves onto that model so models
    /// added later don't silently inherit it. Returns true when anything changed.
    /// </summary>
    public bool NormalizeModels(Func<string, string>? seedDisplayName = null)
    {
        var changed = false;

        var cleaned = Models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(m => m.Id.Trim(), StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        foreach (var m in cleaned) m.Id = m.Id.Trim();
        if (cleaned.Count != Models.Count) { Models = cleaned; changed = true; }

        if (Models.Count == 0 && !string.IsNullOrWhiteSpace(DefaultModel))
        {
            var id = DefaultModel.Trim();
            Models.Add(new ProviderModel
            {
                Id = id,
                DisplayName = seedDisplayName?.Invoke(id) ?? string.Empty,
                VisionOverride = VisionOverride,
            });
            VisionOverride = VisionOverride.Auto;
            changed = true;
        }

        var primary = PrimaryModel?.Id ?? string.Empty;
        if (DefaultModel != primary) { DefaultModel = primary; changed = true; }
        return changed;
    }

    /// <summary>
    /// A transient copy of this provider pinned to one model. The engines,
    /// the vision classifier and its probe cache all read
    /// <see cref="DefaultModel"/>, so handing them this copy makes every run
    /// use exactly the model the user picked. Never persisted.
    /// </summary>
    public Provider ForModel(ProviderModel model) => new()
    {
        Id = Id,
        Kind = Kind,
        BuiltInKey = BuiltInKey,
        Name = Name,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        DefaultModel = model.Id,
        Models = Models,
        IsEnabled = IsEnabled,
        Status = Status,
        LastTestedAt = LastTestedAt,
        LastTestMessage = LastTestMessage,
        VisionOverride = model.VisionOverride != VisionOverride.Auto ? model.VisionOverride : VisionOverride,
    };

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
