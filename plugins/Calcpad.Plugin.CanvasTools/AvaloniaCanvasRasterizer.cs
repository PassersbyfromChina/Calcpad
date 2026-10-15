using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Calcpad.Core.Canvas;
using Calcpad.Plugin.Sdk.Ui;
using Calcpad.Rendering;

namespace Calcpad.Plugin.CanvasTools;

/// <summary>
/// Renders a world region of the canvas into a PNG using Avalonia's <see cref="RenderTargetBitmap"/>.
/// Handwriting recognition and the ask-AI plugin call it through
/// <see cref="ICanvasApi.RasterizeAsync"/>; the crop is exactly the image the egress guard shows the
/// user before anything is uploaded.
/// </summary>
/// <remarks>
/// Grid, selection chrome and widget focus rings are deliberately not drawn: a recognition crop must
/// contain canvas content only. The renderer is the same
/// <see cref="CanvasDocumentRenderer"/> the live canvas uses, linked into this assembly as shared
/// source, so a crop always matches what the user sees.
/// </remarks>
public sealed class AvaloniaCanvasRasterizer : ICanvasRasterizer
{
    /// <summary>Largest bitmap edge, to keep a runaway crop from exhausting memory.</summary>
    public const int MaxPixelsPerSide = 4096;

    /// <summary>Largest total pixel count of one crop.</summary>
    public const long MaxPixels = 16L * 1024 * 1024;

    private readonly ICanvasEngine _engine;
    private readonly IUiRegistry _ui;
    private CanvasDocumentRenderer? _renderer;

    /// <summary>Creates the rasteriser the shell installs on <c>ICanvasEngine.Rasterizer</c>.</summary>
    /// <param name="engine">Engine whose document is rendered.</param>
    /// <param name="ui">UI registry used to resolve widget renderers and the theme.</param>
    public AvaloniaCanvasRasterizer(ICanvasEngine engine, IUiRegistry ui)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    /// <summary>Diagnostic message of the most recent failure, or null.</summary>
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public async ValueTask<CanvasRaster?> RasterizeAsync(CRect region, double scale, CancellationToken ct = default)
    {
        if (!IsUsable(region) || region.IsEmpty)
        {
            return null;
        }

        var request = Plan(region, scale);
        if (request is null)
        {
            return null;
        }

        if (ct.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                return Render(region, request.Value);
            }

            return await Dispatcher.UIThread.InvokeAsync(() => Render(region, request.Value));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private CanvasRaster? Render(CRect region, (double Scale, int Width, int Height) request)
    {
        var renderer = _renderer ??= new CanvasDocumentRenderer(_ui)
        {
            Hosts = new RasterizerWidgetHostProvider(_engine, _ui)
        };

        var camera = new Camera
        {
            OffsetX = region.X,
            OffsetY = region.Y,
            Zoom = request.Scale,
            MinZoom = 0.001,
            MaxZoom = 1000
        };

        var size = new CSize(request.Width, request.Height);
        using var bitmap = new RenderTargetBitmap(new PixelSize(request.Width, request.Height), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawRectangle(new SolidColorBrush(BackgroundColor()), null, new Rect(0, 0, request.Width, request.Height));
            renderer.Render(context, _engine.Document, camera, size, drawGrid: false, drawSelection: false, selected: null);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        LastError = null;
        return new CanvasRaster(stream.ToArray(), request.Width, request.Height, region);
    }

    private static (double Scale, int Width, int Height)? Plan(CRect region, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = 1;
        }

        scale = Math.Clamp(scale, 0.01, 16);
        var width = Math.Max(1, (int)Math.Round(region.Width * scale));
        var height = Math.Max(1, (int)Math.Round(region.Height * scale));

        var shrink = 1.0;
        if (width > MaxPixelsPerSide)
        {
            shrink = Math.Min(shrink, MaxPixelsPerSide / (double)width);
        }

        if (height > MaxPixelsPerSide)
        {
            shrink = Math.Min(shrink, MaxPixelsPerSide / (double)height);
        }

        var total = (long)width * height;
        if (total > MaxPixels)
        {
            shrink = Math.Min(shrink, Math.Sqrt(MaxPixels / (double)total));
        }

        if (shrink < 1)
        {
            scale *= shrink;
            width = Math.Max(1, (int)Math.Round(region.Width * scale));
            height = Math.Max(1, (int)Math.Round(region.Height * scale));
        }

        if (width < 1 || height < 1)
        {
            return null;
        }

        return (scale, width, height);
    }

    private Color BackgroundColor()
    {
        try
        {
            return _ui.Theme?.CanvasBackground ?? PluginTheme.IpadDark.CanvasBackground;
        }
        catch
        {
            return PluginTheme.IpadDark.CanvasBackground;
        }
    }

    private static bool IsUsable(CRect region) =>
        double.IsFinite(region.X) &&
        double.IsFinite(region.Y) &&
        double.IsFinite(region.Width) &&
        double.IsFinite(region.Height) &&
        region.Width > 0 &&
        region.Height > 0;
}
