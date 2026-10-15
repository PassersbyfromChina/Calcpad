namespace Calcpad.Core.Canvas;

/// <summary>A rasterised crop of the canvas, used for recognition and for AI image context.</summary>
public sealed record CanvasRaster(byte[] Png, int PixelWidth, int PixelHeight, CRect WorldRegion)
{
    public int ByteCount => Png.Length;
}

/// <summary>
/// Host side view of the canvas: everything the shell and the plugin host need. Plugins never
/// see this type; they only get the permission checked <see cref="ICanvasApi"/>.
/// </summary>
public interface ICanvasEngine
{
    CanvasDocument Document { get; }

    Camera Camera { get; }

    CanvasObjectRegistry Registry { get; }

    ICanvasSerializer Serializer { get; }

    event EventHandler<CanvasChangedEventArgs>? Changed;

    event EventHandler? HistoryChanged;

    bool CanUndo { get; }

    bool CanRedo { get; }

    /// <summary>Creates the permission checked facade handed to a plugin.</summary>
    ICanvasApi ApiFor(string pluginId);

    IReadOnlyList<CanvasObject> Query(CanvasQuery? query = null);

    CanvasObject? HitTest(CPoint world, double tolerance = 0);

    /// <summary>Selects every object touched (or enclosed) by a lasso polygon.</summary>
    IReadOnlyList<CanvasObject> LassoSelect(IReadOnlyList<CPoint> polygon, bool fullyEnclosed = false);

    IDisposable BeginEdit(string label, string? originPluginId = null);

    string Add(CanvasObject obj, string? originPluginId = null);

    bool Update(string objectId, Action<CanvasObject> mutate, string? originPluginId = null);

    bool Remove(string objectId, string? originPluginId = null);

    void SetSelection(IEnumerable<string> objectIds);

    void Undo();

    void Redo();

    void ClearHistory();

    /// <summary>Replaces the document contents, e.g. when opening a file. Clears history.</summary>
    void Load(CanvasDocument document);

    /// <summary>Deep, detached copy of the requested objects.</summary>
    IReadOnlyList<CanvasObject> Export(CanvasQuery? query = null);

    ValueTask<CanvasRaster?> RasterizeAsync(CRect region, double scale = 2.0, CancellationToken ct = default);

    /// <summary>Supplied by the shell once a rasteriser exists.</summary>
    ICanvasRasterizer? Rasterizer { get; set; }
}

/// <summary>Implemented by the platform shell to turn world coordinates into pixels.</summary>
public interface ICanvasRasterizer
{
    ValueTask<CanvasRaster?> RasterizeAsync(CRect region, double scale, CancellationToken ct = default);
}
