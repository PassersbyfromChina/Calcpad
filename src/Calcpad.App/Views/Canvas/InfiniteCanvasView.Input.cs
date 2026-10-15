using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Core.Canvas.Engine;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.App.Views.Canvas;

/// <summary>
/// Pointer, gesture and keyboard handling for <see cref="InfiniteCanvasView"/>. Pointer events are
/// offered to a hit widget first; only when the widget does not consume them does the active tool
/// run. Two touch contacts switch the surface into pan / pinch mode.
/// </summary>
public sealed partial class InfiniteCanvasView
{
    private bool _pointerDown;

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetCurrentPoint(this);
        var screen = point.Position;
        var world = WorldFromScreen(screen);
        var isTouch = e.Pointer.Type == PointerType.Touch;

        if (isTouch)
        {
            _touchPoints[e.Pointer.Id] = screen;
            if (_touchPoints.Count >= 2)
            {
                BeginPinch();
                e.Handled = true;
                return;
            }
        }

        CommitTextEdit();
        _pointerDown = true;
        _pressMoved = false;
        _pressScreen = screen;
        _lastScreen = screen;
        _pressWorld = world;
        _selectionBeforeGesture = [.. _selected];

        var widget = FindWidgetAt(world);
        if (widget is not null)
        {
            _pressHitWidgetId = widget.Id;
            _focusedWidgetId = widget.Id;
            _focusedPluginId = string.IsNullOrWhiteSpace(widget.OwnerPluginId) ? "calcpad.core" : widget.OwnerPluginId;
            _focusedKey = string.Empty;
            if (DispatchWidgetPointer(widget, WidgetPointerPhase.Down, world, true, e.ClickCount, 0))
            {
                _activeWidgetId = widget.Id;
                e.Pointer.Capture(this);
                e.Handled = true;
                Invalidate();
                return;
            }
        }

        _pressHitWidgetId = null;
        _focusedWidgetId = null;
        _focusedKey = string.Empty;

        var properties = point.Properties;
        var panRequested = _spacePanning ||
                           properties.IsMiddleButtonPressed ||
                           properties.IsRightButtonPressed ||
                           string.IsNullOrEmpty(_activeToolId) ||
                           string.Equals(_activeToolId, CanvasToolIds.Pan, StringComparison.Ordinal);

        if (panRequested)
        {
            _panning = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var pressure = double.IsFinite(properties.Pressure) && properties.Pressure > 0 ? properties.Pressure : 0.5;
        switch (_activeToolId)
        {
            case CanvasToolIds.Pen:
                BeginStroke(InkTool.Pen, world, pressure);
                break;

            case CanvasToolIds.Highlighter:
                BeginStroke(InkTool.Highlighter, world, pressure);
                break;

            case CanvasToolIds.Eraser:
                BeginErase(world);
                break;

            case CanvasToolIds.Lasso:
                BeginLasso(world);
                break;

            case CanvasToolIds.RectangleSelect:
                BeginMarquee(world);
                break;

            case CanvasToolIds.Text:
                BeginTextTool(world, e.ClickCount);
                break;

            case CanvasToolIds.Shape:
                BeginShape(world);
                break;

            default:
                BeginSelectGesture(world, e.KeyModifiers, e.ClickCount);
                break;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetCurrentPoint(this);
        var screen = point.Position;
        var world = WorldFromScreen(screen);

        if (e.Pointer.Type == PointerType.Touch && _touchPoints.ContainsKey(e.Pointer.Id))
        {
            _touchPoints[e.Pointer.Id] = screen;
            if (_pinching)
            {
                UpdatePinch();
                e.Handled = true;
                return;
            }

            if (_touchPoints.Count >= 2)
            {
                e.Handled = true;
                return;
            }
        }

        if (!_pointerDown)
        {
            return;
        }

        if (Distance(screen, _lastScreen) > 0.5)
        {
            _pressMoved = true;
        }

        if (_panning)
        {
            Camera.PanByScreen(screen.X - _lastScreen.X, screen.Y - _lastScreen.Y);
            NotifyCameraChanged();
        }
        else if (_drawing)
        {
            AppendStrokePoint(world, point.Properties.Pressure);
        }
        else if (_erasing)
        {
            EraseAt(world);
        }
        else if (_lassoing)
        {
            AppendLassoPoint(world);
        }
        else if (_marquee)
        {
            _marqueeWorld = RectBetween(_pressWorld, world);
        }
        else if (_moving)
        {
            MoveSelection(world);
        }
        else if (_shaping)
        {
            UpdateShapePreview(world);
        }
        else if (_activeWidgetId is not null && FindObject(_activeWidgetId) is WidgetObject widget)
        {
            DispatchWidgetPointer(widget, WidgetPointerPhase.Move, world, true, 1, 0);
        }

        _lastScreen = screen;
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetCurrentPoint(this);
        var world = WorldFromScreen(point.Position);

        if (e.Pointer.Type == PointerType.Touch)
        {
            _touchPoints.Remove(e.Pointer.Id);
            if (_touchPoints.Count < 2)
            {
                _pinching = false;
            }
        }

        if (_activeWidgetId is not null && FindObject(_activeWidgetId) is WidgetObject widget)
        {
            DispatchWidgetPointer(widget, WidgetPointerPhase.Up, world, true, 1, 0);
        }

        _activeWidgetId = null;
        EndGesture(cancelled: false);
        e.Pointer.Capture(null);
        e.Handled = true;
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_activeWidgetId is not null && FindObject(_activeWidgetId) is WidgetObject widget)
        {
            DispatchWidgetPointer(widget, WidgetPointerPhase.Cancel, CPoint.Origin, true, 1, 0);
        }

        _activeWidgetId = null;
        EndGesture(cancelled: true);
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetCurrentPoint(this);
        var world = WorldFromScreen(point.Position);

        var widget = FindWidgetAt(world);
        if (widget is not null && DispatchWidgetPointer(widget, WidgetPointerPhase.Wheel, world, true, 1, e.Delta.Y))
        {
            e.Handled = true;
            return;
        }

        var modifiers = e.KeyModifiers;
        if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta))
        {
            var factor = Math.Pow(1.15, e.Delta.Y);
            if (double.IsFinite(factor) && factor > 0)
            {
                Camera.ZoomAt(new CPoint(point.Position.X, point.Position.Y), factor);
                NotifyCameraChanged();
            }
        }
        else if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            Camera.PanByScreen(e.Delta.Y * WheelPanStep, 0);
            NotifyCameraChanged();
        }
        else
        {
            Camera.PanByScreen(0, e.Delta.Y * WheelPanStep);
            NotifyCameraChanged();
        }

        e.Handled = true;
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_editingTextId is not null && HandleTextEditingKey(e))
        {
            e.Handled = true;
            return;
        }

        var focused = FocusedWidget();
        if (focused is not null && DispatchWidgetKey(focused, e))
        {
            e.Handled = true;
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Space:
                _spacePanning = true;
                ApplyCursor();
                e.Handled = true;
                break;

            case Key.Escape:
                CancelGesture();
                CommitTextEdit();
                _engine.SetSelection([]);
                SyncSelectionFromDocument();
                Invalidate();
                e.Handled = true;
                break;

            case Key.Delete:
            case Key.Back:
                DeleteSelection();
                e.Handled = true;
                break;

            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                Redo();
                e.Handled = true;
                break;

            case Key.Z when ctrl:
                Undo();
                e.Handled = true;
                break;

            case Key.A when ctrl:
                SelectAll();
                e.Handled = true;
                break;

            case Key.D0 when ctrl:
            case Key.NumPad0 when ctrl:
                _ = ZoomToFitAsync();
                e.Handled = true;
                break;

            case Key.OemPlus or Key.Add when ctrl:
                _ = ZoomByAsync(MinZoomStep);
                e.Handled = true;
                break;

            case Key.OemMinus or Key.Subtract when ctrl:
                _ = ZoomByAsync(1 / MinZoomStep);
                e.Handled = true;
                break;

            case Key.Left:
                PanByScreen(shift ? -KeyPanStep * 4 : -KeyPanStep, 0);
                e.Handled = true;
                break;

            case Key.Right:
                PanByScreen(shift ? KeyPanStep * 4 : KeyPanStep, 0);
                e.Handled = true;
                break;

            case Key.Up:
                PanByScreen(0, shift ? -KeyPanStep * 4 : -KeyPanStep);
                e.Handled = true;
                break;

            case Key.Down:
                PanByScreen(0, shift ? KeyPanStep * 4 : KeyPanStep);
                e.Handled = true;
                break;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            _spacePanning = false;
            ApplyCursor();
        }
    }

    /// <inheritdoc />
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_editingTextId is null || string.IsNullOrEmpty(e.Text) || FindObject(_editingTextId) is not TextObject text)
        {
            return;
        }

        var value = text.Text ?? string.Empty;
        _caretIndex = Math.Clamp(_caretIndex, 0, value.Length);
        var next = value.Insert(_caretIndex, e.Text);
        _caretIndex += e.Text.Length;
        SetText(text.Id, next);
        e.Handled = true;
    }

    // ----- gestures -----------------------------------------------------------------------------

    private void BeginPan(Point screen)
    {
        _panning = true;
        _lastScreen = screen;
    }

    private void BeginPinch()
    {
        CancelGesture();
        var contacts = new List<Point>(_touchPoints.Values);
        if (contacts.Count < 2)
        {
            return;
        }

        _pinching = true;
        _pinchDistance = Distance(contacts[0], contacts[1]);
        _pinchCenter = new Point((contacts[0].X + contacts[1].X) / 2, (contacts[0].Y + contacts[1].Y) / 2);
    }

    private void UpdatePinch()
    {
        var contacts = new List<Point>(_touchPoints.Values);
        if (contacts.Count < 2)
        {
            _pinching = false;
            return;
        }

        var distance = Distance(contacts[0], contacts[1]);
        var center = new Point((contacts[0].X + contacts[1].X) / 2, (contacts[0].Y + contacts[1].Y) / 2);
        if (_pinchDistance > 1 && distance > 1)
        {
            var factor = distance / _pinchDistance;
            if (double.IsFinite(factor) && factor > 0)
            {
                Camera.PanByScreen(center.X - _pinchCenter.X, center.Y - _pinchCenter.Y);
                Camera.ZoomAt(new CPoint(center.X, center.Y), factor);
                NotifyCameraChanged();
            }
        }

        _pinchDistance = distance;
        _pinchCenter = center;
    }

    private void BeginStroke(InkTool tool, CPoint world, double pressure)
    {
        _drawing = true;
        _drawingTool = tool;
        if (tool == InkTool.Highlighter)
        {
            _drawingWidth = 18;
            _drawingOpacity = 0.35;
            _drawingColor = ToUInt32(_renderer.Theme.Accent);
        }
        else
        {
            _drawingWidth = 2.5;
            _drawingOpacity = 1;
            _drawingColor = ToUInt32(_renderer.Theme.TextPrimary);
        }

        _pendingStroke.Clear();
        _pendingStroke.Add(new InkPoint(world.X, world.Y, pressure, _clock.ElapsedMilliseconds));
        _gestureScope = _engine.BeginEdit(tool == InkTool.Highlighter ? "Highlight" : "Draw", null);
    }

    private void AppendStrokePoint(CPoint world, double pressure)
    {
        var last = _pendingStroke[^1];
        var dx = world.X - last.X;
        var dy = world.Y - last.Y;
        var threshold = 0.7 / Math.Max(Camera.Zoom, 0.01);
        if ((dx * dx) + (dy * dy) < threshold * threshold)
        {
            return;
        }

        _pendingStroke.Add(new InkPoint(world.X, world.Y, double.IsFinite(pressure) && pressure > 0 ? pressure : 0.5, _clock.ElapsedMilliseconds));
    }

    private void FinishStroke()
    {
        _drawing = false;
        var points = _pendingStroke.Count;
        if (points > 0)
        {
            var stroke = new InkStroke
            {
                Points = [.. _pendingStroke],
                Width = _drawingWidth,
                Color = _drawingColor,
                Opacity = _drawingOpacity
            };
            var ink = GetOrCreateSessionInk();
            if (ink is not null)
            {
                var inkId = ink.Id;
                _engine.Update(inkId, target =>
                {
                    var typed = (InkObject)target;
                    if (typed.Groups.Count == 0)
                    {
                        typed.Groups.Add(new InkGroup());
                    }

                    typed.Groups[^1].Strokes.Add(stroke);
                }, null);
            }

            _sessionLast = DateTimeOffset.UtcNow;
        }

        _pendingStroke.Clear();
    }

    private InkObject? GetOrCreateSessionInk()
    {
        var now = DateTimeOffset.UtcNow;
        if (_sessionInk is not null &&
            (now - _sessionLast) < TimeSpan.FromSeconds(InkSessionSeconds) &&
            FindObject(_sessionInk.Id) is InkObject existing &&
            existing.Tool == _drawingTool)
        {
            return existing;
        }

        var ink = new InkObject
        {
            Tool = _drawingTool,
            Bounds = new CRect(_pressWorld.X, _pressWorld.Y, 1, 1)
        };
        _engine.Add(ink);
        _sessionInk = ink;
        return ink;
    }

    private void BeginErase(CPoint world)
    {
        _erasing = true;
        _gestureScope = _engine.BeginEdit("Erase", null);
        EraseAt(world);
    }

    private void EraseAt(CPoint world)
    {
        var radius = 12 / Math.Max(Camera.Zoom, 0.01);
        _surface?.EraseInk(world, radius);
    }

    private void BeginLasso(CPoint world)
    {
        _lassoing = true;
        _lassoPoints.Clear();
        _lassoPoints.Add(world);
    }

    private void AppendLassoPoint(CPoint world)
    {
        var last = _lassoPoints[^1];
        var threshold = 1.5 / Math.Max(Camera.Zoom, 0.01);
        if (last.DistanceTo(world) >= threshold)
        {
            _lassoPoints.Add(world);
        }
    }

    private void BeginMarquee(CPoint world)
    {
        _marquee = true;
        _marqueeWorld = new CRect(world.X, world.Y, 0, 0);
    }

    private void BeginShape(CPoint world)
    {
        _shaping = true;
        _shapePoints.Clear();
        _shapePoints.Add(world);
        UpdateShapePreview(world);
    }

    private void UpdateShapePreview(CPoint world)
    {
        var kind = ActiveShapeKind;
        if (kind == ShapeKind.Freeform)
        {
            var last = _shapePoints.Count > 0 ? _shapePoints[^1] : world;
            if (last.DistanceTo(world) >= 1.2 / Math.Max(Camera.Zoom, 0.01))
            {
                _shapePoints.Add(world);
            }
        }
        else
        {
            _shapePoints.Clear();
            switch (kind)
            {
                case ShapeKind.Rectangle:
                case ShapeKind.Ellipse:
                    _shapePoints.Add(new CPoint(_pressWorld.X, _pressWorld.Y));
                    _shapePoints.Add(new CPoint(world.X, _pressWorld.Y));
                    _shapePoints.Add(new CPoint(world.X, world.Y));
                    _shapePoints.Add(new CPoint(_pressWorld.X, world.Y));
                    break;

                default:
                    _shapePoints.Add(_pressWorld);
                    _shapePoints.Add(world);
                    break;
            }
        }

        _shapePreview = new ShapeObject
        {
            Shape = kind,
            Points = [.. _shapePoints],
            StrokeColor = ToUInt32(_renderer.Theme.Selection),
            FillColor = null,
            StrokeWidth = 2,
            Bounds = CRect.FromPoints(_shapePoints)
        };
    }

    private void FinishShape()
    {
        _shaping = false;
        var preview = _shapePreview;
        _shapePreview = null;
        if (preview is null)
        {
            return;
        }

        var points = new List<CPoint>(_shapePoints);
        if (points.Count < 2)
        {
            return;
        }

        var bounds = CRect.FromPoints(points);
        if (bounds.IsEmpty && preview.Shape is ShapeKind.Line or ShapeKind.Arrow)
        {
            // A zero length line is not worth committing, but a horizontal or vertical one is.
            if (Math.Abs(bounds.Width) < 1 && Math.Abs(bounds.Height) < 1)
            {
                return;
            }
        }

        var shape = new ShapeObject
        {
            Shape = preview.Shape,
            Points = points,
            StrokeColor = preview.StrokeColor,
            StrokeWidth = preview.StrokeWidth
        };
        shape.RecomputeBounds();
        var id = _engine.Add(shape);
        _engine.SetSelection([id]);
        SyncSelectionFromDocument();
    }

    private void BeginSelectGesture(CPoint world, KeyModifiers modifiers, int clickCount)
    {
        var tolerance = 6 / Math.Max(Camera.Zoom, 0.01);
        var hit = _engine.HitTest(world, tolerance);
        _pressHitId = hit?.Id;
        _pressAdditive = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift);

        if (hit is null)
        {
            if (!_pressAdditive)
            {
                _engine.SetSelection([]);
                SyncSelectionFromDocument();
            }

            BeginMarquee(world);
            return;
        }

        if (hit is TextObject && clickCount >= 2)
        {
            _engine.SetSelection([hit.Id]);
            SyncSelectionFromDocument();
            BeginTextEdit(hit.Id);
            return;
        }

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            var next = new List<string>(_selected);
            if (!next.Remove(hit.Id))
            {
                next.Add(hit.Id);
            }

            _engine.SetSelection(next);
            SyncSelectionFromDocument();
        }
        else if (!_selected.Contains(hit.Id))
        {
            _engine.SetSelection([hit.Id]);
            SyncSelectionFromDocument();
        }

        if (!hit.Locked)
        {
            _moving = true;
            _moveScopeUsed = false;
            _pressHitWidgetId = null;
            _moveIds = [.. _selected];
            if (_moveIds.Length == 0)
            {
                _moveIds = [hit.Id];
            }

            _moveLastWorld = world;
        }
    }

    private void MoveSelection(CPoint world)
    {
        var dx = world.X - _moveLastWorld.X;
        var dy = world.Y - _moveLastWorld.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
        {
            return;
        }

        if (!_moveScopeUsed)
        {
            _gestureScope = _engine.BeginEdit("Move", null);
            _moveScopeUsed = true;
        }

        _surface?.Translate(_moveIds, dx, dy);
        _moveLastWorld = world;
    }

    private void BeginTextTool(CPoint world, int clickCount)
    {
        var tolerance = 6 / Math.Max(Camera.Zoom, 0.01);
        if (_engine.HitTest(world, tolerance) is TextObject existing)
        {
            _engine.SetSelection([existing.Id]);
            SyncSelectionFromDocument();
            BeginTextEdit(existing.Id);
            return;
        }

        var created = new TextObject
        {
            Text = string.Empty,
            FontSize = 18,
            Color = ToUInt32(_renderer.Theme.TextPrimary),
            Bounds = new CRect(world.X, world.Y, 1, 1)
        };
        var id = _engine.Add(created);
        _engine.SetSelection([id]);
        SyncSelectionFromDocument();
        BeginTextEdit(id);
    }

    private void BeginTextEdit(string objectId)
    {
        CommitTextEdit();
        _editingTextId = objectId;
        _caretIndex = (FindObject(objectId) as TextObject)?.Text?.Length ?? 0;
        _textEditScope = _engine.BeginEdit("Text", null);
        Focus();
        Invalidate();
    }

    private void SetText(string objectId, string value)
    {
        _engine.Update(objectId, target => ((TextObject)target).Text = value, null);
        Invalidate();
    }

    /// <summary>Ends inline text editing, dropping an empty text object.</summary>
    private void CommitTextEdit()
    {
        var id = _editingTextId;
        _editingTextId = null;
        if (id is not null && FindObject(id) is TextObject text && string.IsNullOrEmpty(text.Text))
        {
            _engine.Remove(id);
            SyncSelectionFromDocument();
        }

        _textEditScope?.Dispose();
        _textEditScope = null;
    }

    private bool HandleTextEditingKey(KeyEventArgs e)
    {
        if (_editingTextId is null || FindObject(_editingTextId) is not TextObject text)
        {
            return false;
        }

        var value = text.Text ?? string.Empty;
        _caretIndex = Math.Clamp(_caretIndex, 0, value.Length);
        switch (e.Key)
        {
            case Key.Escape:
                CommitTextEdit();
                return true;

            case Key.Back when _caretIndex > 0:
                SetText(text.Id, value.Remove(_caretIndex - 1, 1));
                _caretIndex--;
                return true;

            case Key.Delete when _caretIndex < value.Length:
                SetText(text.Id, value.Remove(_caretIndex, 1));
                return true;

            case Key.Left:
                _caretIndex = Math.Max(0, _caretIndex - 1);
                Invalidate();
                return true;

            case Key.Right:
                _caretIndex = Math.Min(value.Length, _caretIndex + 1);
                Invalidate();
                return true;

            case Key.Home:
                _caretIndex = 0;
                Invalidate();
                return true;

            case Key.End:
                _caretIndex = value.Length;
                Invalidate();
                return true;

            case Key.Enter:
                SetText(text.Id, value.Insert(_caretIndex, "\n"));
                _caretIndex++;
                return true;

            default:
                return false;
        }
    }

    private void EndGesture(bool cancelled)
    {
        if (_panning)
        {
            _panning = false;
        }
        else if (_drawing)
        {
            FinishStroke();
        }
        else if (_erasing)
        {
            _erasing = false;
        }
        else if (_lassoing)
        {
            _lassoing = false;
            if (!cancelled)
            {
                FinishLasso();
            }

            _lassoPoints.Clear();
        }
        else if (_marquee)
        {
            _marquee = false;
            if (!cancelled)
            {
                FinishMarquee();
            }

            _marqueeWorld = CRect.Empty;
        }
        else if (_moving)
        {
            _moving = false;
            FinishMove();
        }
        else if (_shaping)
        {
            if (cancelled)
            {
                _shaping = false;
                _shapePreview = null;
                _shapePoints.Clear();
            }
            else
            {
                FinishShape();
            }
        }

        _gestureScope?.Dispose();
        _gestureScope = null;
        _pointerDown = false;
        _pressMoved = false;
        _pressHitId = null;
        _activeWidgetId = null;
    }

    private void FinishMove()
    {
        if (!_moveScopeUsed)
        {
            // A plain click: collapse a multi selection to the clicked object, unless additive.
            if (!_pressMoved && !_pressAdditive && _pressHitId is not null)
            {
                _engine.SetSelection([_pressHitId]);
                SyncSelectionFromDocument();
            }

            return;
        }

        _moveScopeUsed = false;
    }

    private void FinishLasso()
    {
        if (_lassoPoints.Count < 3)
        {
            return;
        }

        var fullyEnclosed = false;
        var hits = _engine.LassoSelect(_lassoPoints, fullyEnclosed);
        MergeSelection(hits);
    }

    private void FinishMarquee()
    {
        if (_marqueeWorld.IsEmpty)
        {
            return;
        }

        var rect = _marqueeWorld;
        var polygon = new List<CPoint>
        {
            new(rect.Left, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Right, rect.Bottom),
            new(rect.Left, rect.Bottom)
        };
        var hits = _engine.LassoSelect(polygon, fullyEnclosed: false);
        MergeSelection(hits);
    }

    private void MergeSelection(IReadOnlyList<CanvasObject> hits)
    {
        if (!_pressAdditive)
        {
            SyncSelectionFromDocument();
            return;
        }

        var merged = new List<string>(_selectionBeforeGesture);
        foreach (var obj in hits)
        {
            if (!merged.Contains(obj.Id))
            {
                merged.Add(obj.Id);
            }
        }

        _engine.SetSelection(merged);
        SyncSelectionFromDocument();
    }

    /// <summary>Aborts the running gesture without committing a selection.</summary>
    private void CancelGesture()
    {
        _drawing = false;
        _pendingStroke.Clear();
        _erasing = false;
        _lassoing = false;
        _lassoPoints.Clear();
        _marquee = false;
        _marqueeWorld = CRect.Empty;
        _moving = false;
        _moveScopeUsed = false;
        _shaping = false;
        _shapePreview = null;
        _shapePoints.Clear();
        _panning = false;
        _pinching = false;
        _pointerDown = false;
        _pressMoved = false;
        _pressHitId = null;
        _activeWidgetId = null;
        _touchPoints.Clear();
        _gestureScope?.Dispose();
        _gestureScope = null;
    }

    // ----- widget routing -----------------------------------------------------------------------

    private WidgetObject? FindWidgetAt(CPoint world)
    {
        try
        {
            return _engine.HitTest(world, 0) as WidgetObject;
        }
        catch
        {
            return null;
        }
    }

    private WidgetObject? FocusedWidget() =>
        _focusedWidgetId is not null ? FindObject(_focusedWidgetId) as WidgetObject : null;

    private bool DispatchWidgetPointer(
        WidgetObject widget,
        WidgetPointerPhase phase,
        CPoint world,
        bool isPrimary,
        int clickCount,
        double wheelDelta)
    {
        var renderer = _ui.FindWidget(widget.WidgetType);
        var host = GetHost(widget);
        if (renderer is null || host is null)
        {
            return false;
        }

        var context = new WidgetPointerContext
        {
            Phase = phase,
            Position = new CPoint(world.X - widget.Bounds.X, world.Y - widget.Bounds.Y),
            LogicalSize = new CSize(widget.Bounds.Width, widget.Bounds.Height),
            Object = widget,
            Host = host,
            IsPrimary = isPrimary,
            ClickCount = clickCount,
            WheelDelta = wheelDelta
        };

        try
        {
            return renderer.OnPointer(context);
        }
        catch (Exception ex)
        {
            ReportRenderError($"Widget '{widget.WidgetType}' failed while handling a pointer event.", ex);
            return false;
        }
    }

    private bool DispatchWidgetKey(WidgetObject widget, KeyEventArgs e)
    {
        var renderer = _ui.FindWidget(widget.WidgetType);
        var host = GetHost(widget);
        if (renderer is null || host is null)
        {
            return false;
        }

        var context = new WidgetKeyContext
        {
            Key = e.Key.ToString(),
            Object = widget,
            Host = host,
            Control = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta),
            Shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            Alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt)
        };

        try
        {
            return renderer.OnKey(context);
        }
        catch (Exception ex)
        {
            ReportRenderError($"Widget '{widget.WidgetType}' failed while handling a key.", ex);
            return false;
        }
    }

    // ----- small helpers ------------------------------------------------------------------------

    private CPoint WorldFromScreen(Point screen) => Camera.ScreenToWorld(new CPoint(screen.X, screen.Y));

    private void PanByScreen(double dx, double dy)
    {
        Camera.PanByScreen(dx, dy);
        NotifyCameraChanged();
        Invalidate();
    }

    private void Undo()
    {
        _engine.Undo();
        SyncSelectionFromDocument();
        Invalidate();
    }

    private void Redo()
    {
        _engine.Redo();
        SyncSelectionFromDocument();
        Invalidate();
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static CRect RectBetween(CPoint a, CPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
}
