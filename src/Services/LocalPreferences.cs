using System.Text.Json;
using Vantage.Common;

namespace Vantage.Services;

public static class LocalPreferences
{
    private static readonly object Gate = new();
    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vantage");
    private static readonly string FilePath = Path.Combine(FolderPath, "settings.json");
    private static Dictionary<string, JsonElement>? _values;

    public static bool GetBool(string key, bool defaultValue)
    {
        lock (Gate)
        {
            var values = Load();
            return values.TryGetValue(key, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : defaultValue;
        }
    }

    public static string GetString(string key, string defaultValue)
    {
        lock (Gate)
        {
            var values = Load();
            return values.TryGetValue(key, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? defaultValue
                    : defaultValue;
        }
    }

    public static void SetBool(string key, bool value) => Set(key, JsonSerializer.SerializeToElement(value, JsonDefaults.Persist));

    public static void SetString(string key, string value) => Set(key, JsonSerializer.SerializeToElement(value, JsonDefaults.Persist));

    private static Dictionary<string, JsonElement> Load()
    {
        if (_values is not null) return _values;

        try
        {
            if (File.Exists(FilePath))
            {
                _values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(FilePath),
                    JsonDefaults.Persist);
            }
        }
        catch
        {
            _values = null;
        }

        return _values ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }

    private static void Set(string key, JsonElement value)
    {
        lock (Gate)
        {
            var values = Load();
            values[key] = value;

            try
            {
                Directory.CreateDirectory(FolderPath);
                var temporaryPath = FilePath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(values, JsonDefaults.Persist));
                File.Move(temporaryPath, FilePath, true);
            }
            catch
            {
                // A preference write must never interrupt the app.
            }
        }
    }
}
