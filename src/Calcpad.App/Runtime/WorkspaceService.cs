using Calcpad.Core.Canvas;
using Calcpad.Core.Hosting;

namespace Calcpad.App;

/// <summary>
/// Document lifecycle for the infinite canvas. A Calcpad document is a plain JSON file so the
/// user can keep, diff and sync it like any other file; the SQLite database holds plugin data,
/// grants and the upload audit instead.
/// </summary>
public sealed class WorkspaceService
{
    public const string FileExtension = ".calcpad.json";

    public const string FileFilterName = "Calcpad canvas";

    private readonly ICanvasEngine _engine;
    private readonly AppPaths _paths;
    private bool _dirty;

    public WorkspaceService(ICanvasEngine engine, AppPaths paths)
    {
        _engine = engine;
        _paths = paths;
        _engine.Changed += (_, _) => MarkDirty();
    }

    public event EventHandler? StateChanged;

    public string? CurrentPath { get; private set; }

    public bool IsDirty => _dirty;

    public string DocumentTitle => _engine.Document.Title;

    public CanvasDocument NewDocument(string title = "Untitled")
    {
        var doc = new CanvasDocument { Title = title };
        _engine.Load(doc);
        CurrentPath = null;
        _dirty = false;
        RaiseChanged();
        return doc;
    }

    public async ValueTask SaveAsync(string? path = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = path ?? CurrentPath ?? Path.Combine(_paths.DocumentsDirectory, SuggestFileName(_engine.Document.Title));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var json = _engine.Serializer.Serialize(_engine.Document);
        await File.WriteAllTextAsync(target, json, ct).ConfigureAwait(false);
        CurrentPath = target;
        _dirty = false;
        RaiseChanged();
    }

    public async ValueTask<bool> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var doc = _engine.Serializer.Deserialize(json);
        _engine.Load(doc);
        CurrentPath = path;
        _dirty = false;
        RaiseChanged();
        return true;
    }

    public void MarkDirty()
    {
        if (_dirty)
        {
            return;
        }

        _dirty = true;
        RaiseChanged();
    }

    public static string SuggestFileName(string title)
    {
        var safe = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "Untitled";
        }

        return safe + FileExtension;
    }

    private void RaiseChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
