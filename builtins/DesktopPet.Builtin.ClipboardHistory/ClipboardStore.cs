using DesktopPet.Core;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DesktopPet.Builtin.ClipboardHistory;

public sealed record ClipboardEntry(string Text, DateTimeOffset CreatedAt);

/// <summary>Local history with explicit consent and a bounded retention period.</summary>
public sealed class ClipboardStore
{
    private const string EntriesKey = "entries";
    private readonly object _gate = new();
    private readonly IPluginStorage _storage;
    private readonly Func<DateTimeOffset> _now;
    public bool Recording { get; private set; }
    public int RetentionDays { get; private set; }

    public ClipboardStore(IPluginStorage storage, Func<DateTimeOffset>? now = null)
    {
        _storage = storage; _now = now ?? (() => DateTimeOffset.UtcNow);
        Recording = storage.Get<bool>("recordingEnabled");
        RetentionDays = storage.Contains("retentionDays") ? storage.Get<int>("retentionDays") : 7;
        if (RetentionDays is not (0 or 1 or 7 or 30)) RetentionDays = 7;
        if (!storage.Contains(EntriesKey) && storage.Contains(ClipboardHistoryPlugin.HistoryKey))
        {
            var old = storage.Get<List<string>>(ClipboardHistoryPlugin.HistoryKey) ?? new();
            Save(old.Take(20).Select(text => new ClipboardEntry(text, _now())).ToList());
            storage.Remove(ClipboardHistoryPlugin.HistoryKey);
        }
        Read();
    }

    public void SetRecording(bool value)
    { lock (_gate) { if (!value) Recording = false; _storage.Set("recordingEnabled", value); Recording = value; } }

    public void SetRetention(int days)
    {
        lock (_gate)
        {
            if (days is not (0 or 1 or 7 or 30)) throw new ArgumentOutOfRangeException(nameof(days));
            _storage.Set("retentionDays", days); RetentionDays = days; ReadCore();
        }
    }

    public List<ClipboardEntry> Read() { lock (_gate) return ReadCore(); }

    private List<ClipboardEntry> ReadCore()
    {
        var entries = new List<ClipboardEntry>();
        var json = _storage.Get<string>(EntriesKey);
        try
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var item in document.RootElement.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
                            item.TryGetProperty("createdAt", out var date) && date.ValueKind == JsonValueKind.String && date.TryGetDateTimeOffset(out var timestamp))
                            entries.Add(new(text.GetString()!, timestamp));
            }
        }
        catch (JsonException) { return entries; }
        var cutoff = _now().AddDays(-RetentionDays);
        var valid = entries.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Text) &&
            (RetentionDays == 0 || e.CreatedAt > cutoff)).Take(20).ToList();
        if (valid.Count != entries.Count) Save(valid);
        return valid;
    }

    public void Record(string? text)
    {
        lock (_gate)
        {
        if (!Recording || string.IsNullOrWhiteSpace(text) || text.Length > 32768) return;
        var entries = ReadCore();
        if (entries.FirstOrDefault()?.Text == text) return;
        entries.RemoveAll(e => e.Text == text);
        entries.Insert(0, new(text, _now()));
        Save(entries.Take(20).ToList());
        }
    }

    public void Clear()
    { lock (_gate) { _storage.Remove(EntriesKey); _storage.Remove(ClipboardHistoryPlugin.HistoryKey); } }

    // Serialize only a host-owned string: plugin model metadata must not pin its collectible ALC.
    private void Save(List<ClipboardEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                writer.WriteStartObject(); writer.WriteString("text", entry.Text);
                writer.WriteString("createdAt", entry.CreatedAt); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        _storage.Set(EntriesKey, Encoding.UTF8.GetString(stream.ToArray()));
    }
}
