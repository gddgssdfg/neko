using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopPet.App.Core;

namespace DesktopPet.App.Services;

/// <summary>逐帧眼层锚点草稿；只写回有改动的 offset，保留未知 JSON 字段。</summary>
public sealed class SkinAnchorEditor
{
    private static readonly object SaveGate = new();
    private readonly string _directory;
    private byte[] _baseline;
    private JsonObject _document;
    private readonly Dictionary<string, SkinEyeOffset[]> _values = new(), _saved = new();
    private readonly List<Change> _undo = [], _redo = [];
    private sealed record Change(string State, int Frame, SkinEyeOffset Before, SkinEyeOffset After);
    public SkinReview Review { get; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsDirty => _values.Any(pair => !pair.Value.SequenceEqual(_saved[pair.Key]));

    public SkinAnchorEditor(string directory)
    {
        _directory = Path.GetFullPath(directory);
        _baseline = File.ReadAllBytes(Path.Combine(_directory, "skin.json"));
        Review = SkinReview.Open(_directory);
        if (!File.ReadAllBytes(Path.Combine(_directory, "skin.json")).SequenceEqual(_baseline))
            throw new IOException("读取期间清单已变化，请重新校验。");
        using var stream = new MemoryStream(_baseline);
        _document = JsonNode.Parse(stream)?.AsObject() ?? throw new FormatException("skin.json 必须是对象。");
        foreach (var state in Review.Info.States)
        {
            var offsets = Enumerable.Range(0, Review.Info.Frames)
                .Select(i => i < state.EyeOffsets.Count ? state.EyeOffsets[i] : default).ToArray();
            _values[state.Name] = offsets; _saved[state.Name] = offsets.ToArray();
        }
    }
    public bool CanEdit(string state) => Review.Atlas.States.TryGetValue(state, out var value)
        && value.EyeLayer is { } key && Review.Atlas.GetEye(key) is not null;
    public SkinEyeOffset GetOffset(string state, int frame) => _values[state][frame];
    public void SetOffset(string state, int frame, SkinEyeOffset offset)
    {
        if (!CanEdit(state)) throw new InvalidOperationException("此动作没有可编辑的独立眼层。");
        if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y) || Math.Abs(offset.X) > Review.Info.FrameWidth || Math.Abs(offset.Y) > Review.Info.FrameHeight)
            throw new ArgumentOutOfRangeException(nameof(offset), "锚点必须是画幅范围内的有限数字。");
        var previous = GetOffset(state, frame);
        if (previous == offset) return;
        _values[state][frame] = offset;
        _undo.Add(new(state, frame, previous, offset));
        if (_undo.Count > 512) _undo.RemoveAt(0);
        _redo.Clear();
    }
    public void RestoreFrame(string state, int frame) => SetOffset(state, frame, _saved[state][frame]);
    public (string State, int Frame)? Undo()
    {
        if (!CanUndo) return null;
        var change = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        _values[change.State][change.Frame] = change.Before; _redo.Add(change);
        return (change.State, change.Frame);
    }
    public (string State, int Frame)? Redo()
    {
        if (!CanRedo) return null;
        var change = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        _values[change.State][change.Frame] = change.After; _undo.Add(change);
        return (change.State, change.Frame);
    }
    public string? Save(string backupRoot)
    {
        lock (SaveGate)
        {
            if (!IsDirty) return null;
            var manifest = Path.Combine(_directory, "skin.json");
            void CheckUnchanged()
            {
                if (!File.ReadAllBytes(manifest).SequenceEqual(_baseline))
                    throw new IOException("清单已被外部修改，未覆盖。请备份当前草稿后重新校验。");
            }
            CheckUnchanged();
            var document = (JsonObject)_document.DeepClone();
            foreach (var node in document["states"]!.AsArray().OfType<JsonObject>())
            {
                var name = node["name"]!.GetValue<string>();
                if (_values[name].SequenceEqual(_saved[name])) continue;
                var offsets = node["eyeOffsets"]?.AsArray() ?? new JsonArray();
                if (node["eyeOffsets"] is null) node["eyeOffsets"] = offsets;
                while (offsets.Count < Review.Info.Frames) offsets.Add(new JsonObject { ["x"] = 0, ["y"] = 0 });
                for (int i = 0; i < offsets.Count; i++)
                {
                    if (_values[name][i] == _saved[name][i]) continue;
                    var point = offsets[i]!.AsObject(); point["x"] = _values[name][i].X; point["y"] = _values[name][i].Y;
                }
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            var stage = Path.Combine(Path.GetTempPath(), "neko-anchor-" + Guid.NewGuid().ToString("N"));
            var temporary = manifest + ".neko-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ExtensionInstaller.Install(_directory, stage, false, Review.Info.Id);
                var candidate = Path.Combine(stage, Review.Info.Id);
                File.WriteAllBytes(Path.Combine(candidate, "skin.json"), bytes);
                ExtensionInstaller.InspectSkin(candidate);
                var backup = Path.Combine(Path.GetFullPath(backupRoot), Review.Info.Id, Guid.NewGuid().ToString("N"), "skin.json");
                if (backup.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("备份目录必须位于皮肤目录之外。");
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.WriteAllBytes(backup, _baseline);
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes); output.Flush(flushToDisk: true); }
                CheckUnchanged();
                File.Replace(temporary, manifest, null);
                _baseline = bytes; _document = document;
                foreach (var (name, values) in _values) _saved[name] = values.ToArray();
                _undo.Clear(); _redo.Clear();
                return backup;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                // Only this invocation's fresh private staging tree is removed.
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
            }
        }
    }
}
