using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Calcpad.Core.Canvas;
using Calcpad.Core.Canvas.Engine;
using Calcpad.Core.Plugins;
using Calcpad.Plugin.Sdk.Ui;
using Calcpad.Rendering;

namespace Calcpad.App.Views.Canvas;

/// <summary>
/// The infinite canvas surface. It draws the document through <see cref="CanvasDocumentRenderer"/>,
/// owns camera pan / zoom, pointer and keyboard input, tool behaviour and the live widgets plugins
/// place on the canvas. Everything the product shows on its canvas - calculator keypads, unit
/// converters, graph panes - is a widget rendered by this control.
/// </summary>
/// <remarks>
/// The control works with mouse and keyboard on Windows and with touch on Android: panning is space
/// drag, middle drag, right drag, the pan tool or two fingers; zooming is Ctrl + wheel, the zoom
/// commands or a two finger pinch. Widgets receive pointer events before canvas logic and keyboard
/// events while they own focus.
/// </remarks>
public sealed partial class InfiniteCanvasView : Control, IWidgetHostProvider
{
    private const double FitPadding = 48;
    private const double WheelPanStep = 64;
    private const double KeyPanStep = 64;
    private const double InkSessionSeconds = 3;
    private const double MinZoomStep = 1.2;
    private static readonly TimeSpan PluginBadgeDuration = TimeSpan.FromSeconds(2.5);

    private readonly ICanvasEngine _engine;
    private readonly ICanvasEngineHost? _surface;
    private readonly IUiRegistry _ui;
    private readonly IHostApi _host;
    private readonly CanvasDocumentRenderer _renderer;
    private readonly Dictionary<string, CanvasWidgetHost> _widgetHosts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly List<CPoint> _lassoPoints = [];
    private readonly List<InkPoint> _pendingStroke = [];
    private readonly Dictionary<long, Point> _touchPoints = [];
    private readonly DispatcherTimer _badgeTimer;

    private string _activeToolId = CanvasToolIds.Select;
    private string? _focusedWidgetId;
    private string? _focusedPluginId;
    private string _focusedKey = string.Empty;
    private string? _badgePluginId;
    private DateTimeOffset _badgeUntil;
    private bool _gridVisible = true;
    private bool _detached;

    // gesture state
    private bool _spacePanning;
    private bool _panning;
    private bool _pinching;
    private bool _erasing;
    private bool _marquee;
    private bool _lassoing;
    private bool _moving;
    private bool _drawing;
    private bool _pressMoved;
    private double _pinchDistance;
    private Point _pinchCenter;
    private Point _lastScreen;
    private Point _pressScreen;
    private CPoint _pressWorld;
    private CPoint _moveLastWorld;
    private CRect _marqueeWorld;
    private InkTool _drawingTool = InkTool.Pen;
    private uint _drawingColor;
    private double _drawingWidth;
    private double _drawingOpacity;
    private InkObject? _sessionInk;
    private DateTimeOffset _sessionLast;
    private string? _activeWidgetId;
    private string? _editingTextId;
    private int _caretIndex;
    private IDisposable? _gestureScope;
    private IDisposable? _textEditScope;
    private bool _moveScopeUsed;
    private bool _shaping;
    private ShapeObject? _shapePreview;
    private readonly List<CPoint> _shapePoints = [];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private string[] _moveIds = [];
    private string[] _selectionBeforeGesture = [];
    private string? _pressHitId;
    private string? _pressHitWidgetId;
    private bool _pressAdditive;

    /// <summary>Creates the canvas surface.</summary>
    /// <param name="engine">Canvas engine that owns the document, camera and history.</param>
    /// <param name="ui">UI registry used to resolve widget renderers and the theme.</param>
    /// <param name="host">Shell services used by live widgets.</param>
    public InfiniteCanvasView(ICanvasEngine engine, IUiRegistry ui, IHostApi host)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _surface = engine as ICanvasEngineHost;
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _renderer = new CanvasDocumentRenderer(ui)
        {
            Hosts = this,
            VisibleObjects = QueryVisibleObjects,
            OnError = ReportRenderError
        };

        Focusable = true;
        ClipToBounds = true;
        _drawingColor = ToUInt32(_renderer.Theme.TextPrimary);
        _drawingWidth = 2.5;
        _drawingOpacity = 1;

        _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _badgeTimer.Tick += OnBadgeTimerTick;

        _engine.Changed += OnEngineChanged;
        _engine.HistoryChanged += OnHistoryChanged;
        _ui.Invalidated += OnUiInvalidated;
        SyncSelectionFromDocument();
        ApplyCursor();
    }

    /// <summary>The engine this view renders and drives.</summary>
    public ICanvasEngine Engine => _engine;

    /// <summary>Live camera; the same instance the document owns.</summary>
    public Camera Camera => _engine.Camera;

    /// <summary>Id of the active tool. An empty string means "no tool": pointer input only pans.</summary>
    public string ActiveToolId
    {
        get => _activeToolId;
        set => SetTool(value);
    }

    /// <summary>Raised whenever the active tool changes, with the new tool id.</summary>
    public event EventHandler<string>? ToolChanged;

    /// <summary>Shape produced by the shape tool.</summary>
    public ShapeKind ActiveShapeKind { get; set; } = ShapeKind.Rectangle;

    /// <summary>When true a lasso leaves a persistent <see cref="ShapeObject"/> outline behind.</summary>
    public bool PersistLassoShapes { get; set; }

    /// <summary>
    /// Supplies the settings partition of a plugin so live widgets can persist their state. When not
    /// set, widgets get an in-memory store that lives only as long as the process.
    /// </summary>
    public Func<string, IPluginSettings?>? SettingsProvider { get; set; }

    /// <summary>True when the background grid is drawn.</summary>
    public bool IsGridVisible => _gridVisible;

    /// <summary>Shows or hides the background grid.</summary>
    public void SetGridVisible(bool visible)
    {
        if (_gridVisible == visible)
        {
            return;
        }

        _gridVisible = visible;
        Invalidate();
    }

    /// <summary>Activates a tool. Unknown or empty ids leave the canvas in pan / select only mode.</summary>
    public void SetTool(string? toolId)
    {
        var next = toolId ?? string.Empty;
        if (string.Equals(_activeToolId, next, StringComparison.Ordinal))
        {
            return;
        }

        CancelGesture();
        CommitTextEdit();
        _activeToolId = next;
        _renderer.ResetCaches();
        ApplyCursor();
        ToolChanged?.Invoke(this, next);
        Invalidate();
    }

    /// <summary>Requests a repaint. Safe to call from any thread.</summary>
    public void Invalidate()
    {
        if (_detached)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            InvalidateVisual();
            return;
        }

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
    }

    /// <summary>Scales the camera around the viewport centre.</summary>
    public ValueTask ZoomByAsync(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            return ValueTask.CompletedTask;
        }

        var viewport = CurrentViewport();
        Camera.ZoomAt(new CPoint(viewport.Width / 2, viewport.Height / 2), factor);
        NotifyCameraChanged();
        Invalidate();
        return ValueTask.CompletedTask;
    }

    /// <summary>Fits all content into the viewport, or resets to the world origin when empty.</summary>
    public ValueTask ZoomToFitAsync()
    {
        var viewport = CurrentViewport();
        var content = _engine.Document.ContentBounds;
        if (!content.IsEmpty && !viewport.IsEmpty)
        {
            Camera.FitTo(content, viewport, FitPadding);
        }
        else
        {
            Camera.Zoom = 1;
            Camera.CenterOn(CPoint.Origin, viewport);
        }

        NotifyCameraChanged();
        Invalidate();
        return ValueTask.CompletedTask;
    }

    /// <summary>Moves the camera so that a world region is visible.</summary>
    public ValueTask RevealAsync(CRect region)
    {
        if (!region.IsEmpty)
        {
            Camera.FitTo(region, CurrentViewport(), FitPadding);
            NotifyCameraChanged();
            Invalidate();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Selects every unlocked, visible object.</summary>
    public void SelectAll()
    {
        var ids = new List<string>();
        foreach (var obj in _engine.Document.Objects)
        {
            if (obj.Visible && !obj.Locked)
            {
                ids.Add(obj.Id);
            }
        }

        _engine.SetSelection(ids);
        SyncSelectionFromDocument();
        Invalidate();
    }

    /// <summary>Deletes the current selection.</summary>
    public void DeleteSelection()
    {
        var ids = SelectionIds();
        if (ids.Count == 0)
        {
            return;
        }

        using var scope = _engine.BeginEdit("Delete", null);
        foreach (var id in ids)
        {
            _engine.Remove(id);
        }
    }

    /// <inheritdoc />
    public IWidgetHost? GetHost(WidgetObject widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        var pluginId = string.IsNullOrWhiteSpace(widget.OwnerPluginId) ? "calcpad.core" : widget.OwnerPluginId;
        if (!_widgetHosts.TryGetValue(pluginId, out var host))
        {
            host = new CanvasWidgetHost(this, _engine, _host, pluginId, ResolveSettings(pluginId));
            _widgetHosts[pluginId] = host;
        }

        return host;
    }

    private IPluginSettings ResolveSettings(string pluginId)
    {
        try
        {
            return SettingsProvider?.Invoke(pluginId) ?? new InMemoryPluginSettings();
        }
        catch (Exception ex)
        {
            ReportRenderError($"Resolving settings for plugin '{pluginId}' failed; falling back to memory.", ex);
            return new InMemoryPluginSettings();
        }
    }

    /// <inheritdoc />
    public bool IsWidgetFocused(WidgetObject widget) =>
        widget is not null && string.Equals(_focusedWidgetId, widget.Id, StringComparison.Ordinal);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width) ? 1024 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) || double.IsNaN(availableSize.Height) ? 768 : availableSize.Height;
        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        SyncViewport(size);
        return size;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _detached = false;
        SyncViewport(Bounds.Size);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _detached = true;
        _badgeTimer.Stop();
        CancelGesture();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var size = Bounds.Size;
        var viewport = new CSize(size.Width, size.Height);
        if (viewport.IsEmpty)
        {
            return;
        }

        SyncViewport(size);
        var theme = _renderer.Theme;
        context.DrawRectangle(new SolidColorBrush(theme.CanvasBackground), null, new Rect(size));
        _renderer.Render(context, _engine.Document, Camera, viewport, _gridVisible, drawSelection: true, _selected);
        DrawOverlays(context, viewport);
    }

    private IReadOnlyList<CanvasObject> QueryVisibleObjects(CanvasDocument document, CRect region)
    {
        try
        {
            return _engine.Query(new CanvasQuery { Region = region, IncludeStrokes = true });
        }
        catch (Exception ex)
        {
            ReportRenderError("Canvas region query failed.", ex);
            return document.Objects;
        }
    }

    private void ReportRenderError(string message, Exception? error)
    {
        var log = _surface?.Log;
        if (log is not null)
        {
            log.Warn(message, error);
            return;
        }

        System.Diagnostics.Debug.WriteLine(error is null ? message : $"{message} {error}");
    }

    private void OnEngineChanged(object? sender, CanvasChangedEventArgs e)
    {
        if (e.Kind == CanvasChangeKind.SelectionChanged)
        {
            _selected.Clear();
            foreach (var id in e.ObjectIds)
            {
                _selected.Add(id);
            }
        }
        else if (e.Kind == CanvasChangeKind.Reloaded)
        {
            SyncSelectionFromDocument();
            _renderer.ResetCaches();
            _sessionInk = null;
            _editingTextId = null;
        }

        if (!string.IsNullOrEmpty(e.OriginPluginId) && e.Kind is CanvasChangeKind.Added or CanvasChangeKind.Updated)
        {
            _badgePluginId = e.OriginPluginId;
            _badgeUntil = DateTimeOffset.UtcNow + PluginBadgeDuration;
            RunOnUiThread(() =>
            {
                if (!_badgeTimer.IsEnabled)
                {
                    _badgeTimer.Start();
                }
            });
        }

        Invalidate();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Invalidate();

    private void OnUiInvalidated(object? sender, EventArgs e) => Invalidate();

    private void OnBadgeTimerTick(object? sender, EventArgs e)
    {
        if (DateTimeOffset.UtcNow < _badgeUntil)
        {
            return;
        }

        _badgeTimer.Stop();
        _badgePluginId = null;
        Invalidate();
    }

    private void SyncSelectionFromDocument()
    {
        _selected.Clear();
        foreach (var id in _engine.Document.Selection)
        {
            _selected.Add(id);
        }
    }

    private void SyncViewport(Size size)
    {
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        if (_surface is not null)
        {
            _surface.ViewportSize = new CSize(size.Width, size.Height);
        }
    }

    private CSize CurrentViewport()
    {
        var size = Bounds.Size;
        if (double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width > 0 && size.Height > 0)
        {
            return new CSize(size.Width, size.Height);
        }

        return _surface?.ViewportSize ?? new CSize(1024, 768);
    }

    private void NotifyCameraChanged() => _surface?.NotifyCameraChanged();

    /// <summary>Current selection ids, independent of the concrete engine type.</summary>
    private IReadOnlyList<string> SelectionIds() =>
        _surface is not null ? _surface.SelectionSnapshot() : _engine.Document.Selection.ToArray();

    /// <summary>Live object lookup; null when the id is unknown.</summary>
    private CanvasObject? FindObject(string? objectId) =>
        string.IsNullOrEmpty(objectId) ? null : _engine.Document.Find(objectId);

    private void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    private void ApplyCursor()
    {
        var type = _activeToolId switch
        {
            CanvasToolIds.Pen or CanvasToolIds.Highlighter or CanvasToolIds.Shape => StandardCursorType.Cross,
            CanvasToolIds.Text => StandardCursorType.Ibeam,
            CanvasToolIds.Lasso or CanvasToolIds.RectangleSelect => StandardCursorType.Cross,
            CanvasToolIds.Eraser => StandardCursorType.Hand,
            CanvasToolIds.Pan => StandardCursorType.SizeAll,
            _ => StandardCursorType.Arrow
        };

        try
        {
            Cursor = new Cursor(type);
        }
        catch
        {
            // Cursor creation needs a platform; the canvas works without it.
        }
    }

    /// <summary>Converts an ARGB colour to the packed form canvas objects use.</summary>
    public static uint ToUInt32(Color color) =>
        ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    /// <summary>Formats a world coordinate for the status bar.</summary>
    public static string DescribePoint(CPoint point) =>
        point.ToString();

    /// <summary>Renders overlays the document renderer does not own: live gestures and hints.</summary>
    private void DrawOverlays(DrawingContext context, CSize viewport)
    {
        if (_drawing && _pendingStroke.Count > 0)
        {
            var preview = new InkStroke
            {
                Points = [.. _pendingStroke],
                Width = _drawingWidth,
                Color = _drawingColor,
                Opacity = _drawingOpacity
            };
            _renderer.DrawInkStroke(context, preview, Camera);
        }

        if (_lassoing && _lassoPoints.Count > 1)
        {
            var pen = new Pen(new SolidColorBrush(_renderer.Theme.Selection), 1.5)
            {
                DashStyle = new DashStyle([4, 3], 0)
            };
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                var first = Camera.WorldToScreen(_lassoPoints[0]);
                path.BeginFigure(new Point(first.X, first.Y), false);
                for (var i = 1; i < _lassoPoints.Count; i++)
                {
                    var point = Camera.WorldToScreen(_lassoPoints[i]);
                    path.LineTo(new Point(point.X, point.Y));
                }

                path.EndFigure(true);
            }

            context.DrawGeometry(null, pen, geometry);
        }

        if (_marquee && !_marqueeWorld.IsEmpty)
        {
            var rect = Camera.WorldToScreen(_marqueeWorld);
            var selection = _renderer.Theme.Selection;
            context.DrawRectangle(
                new SolidColorBrush(selection, 0.12),
                new Pen(new SolidColorBrush(selection), 1.2),
                new Rect(rect.X, rect.Y, rect.Width, rect.Height));
        }

        DrawTextEditor(context);
        DrawPluginBadge(context, viewport);
        DrawEmptyHint(context, viewport);
    }

    private void DrawPluginBadge(DrawingContext context, CSize viewport)
    {
        if (string.IsNullOrEmpty(_badgePluginId) || DateTimeOffset.UtcNow > _badgeUntil)
        {
            return;
        }

        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var text = new FormattedText(
            $"modified by {_badgePluginId}",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            12,
            new SolidColorBrush(_renderer.Theme.TextPrimary));
        var padding = 8.0;
        var rect = new Rect(viewport.Width - text.Width - (padding * 3), 12, text.Width + (padding * 2), text.Height + (padding * 0.8));
        context.DrawRectangle(
            new SolidColorBrush(_renderer.Theme.SurfaceRaised, 0.92),
            new Pen(new SolidColorBrush(_renderer.Theme.Accent), 1),
            new RoundedRect(rect, 8));
        context.DrawText(text, new Point(rect.X + padding, rect.Y + (rect.Height - text.Height) / 2));
    }

    private void DrawEmptyHint(DrawingContext context, CSize viewport)
    {
        if (_engine.Document.Objects.Count > 0)
        {
            return;
        }

        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
        var text = new FormattedText(
            "Infinite canvas - pick a tool, or drop a widget from the plugin menu.",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            13,
            new SolidColorBrush(_renderer.Theme.TextSecondary));
        text.MaxTextWidth = Math.Max(80, viewport.Width - 40);
        text.TextAlignment = TextAlignment.Center;
        context.DrawText(text, new Point(20, Math.Max(20, (viewport.Height / 2) - (text.Height / 2))));
    }

    private void DrawTextEditor(DrawingContext context)
    {
        if (_editingTextId is null || FindObject(_editingTextId) is not TextObject text)
        {
            return;
        }

        var rect = Camera.WorldToScreen(text.Bounds);
        var box = new Rect(rect.X - 2, rect.Y - 2, Math.Max(rect.Width + 4, 24), Math.Max(rect.Height + 4, 12));
        context.DrawRectangle(
            new SolidColorBrush(_renderer.Theme.Selection, 0.12),
            new Pen(new SolidColorBrush(_renderer.Theme.Selection), 1),
            new RoundedRect(box, 4));

        var caretScreen = new Point(rect.X + Math.Max(0, rect.Width), rect.Y);
        context.DrawLine(
            new Pen(new SolidColorBrush(_renderer.Theme.Accent), 1.4),
            new Point(caretScreen.X, caretScreen.Y),
            new Point(caretScreen.X, caretScreen.Y + Math.Max(12, rect.Height)));
    }
}
