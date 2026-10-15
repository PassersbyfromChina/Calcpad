namespace Calcpad.Core.Canvas;

/// <summary>
/// The infinite canvas: an unbounded plane holding an ordered set of objects, plus the camera
/// that decides which part of it the user is looking at. There is no fixed page size.
/// </summary>
public sealed class CanvasDocument
{
    public const int CurrentSchemaVersion = 1;

    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Title { get; set; } = "Untitled";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<CanvasObject> Objects { get; set; } = [];

    public Camera Camera { get; set; } = new();

    public Dictionary<string, string> Metadata { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set by the engine so plugins can address the objects the user currently picked.</summary>
    public List<string> Selection { get; set; } = [];

    public CanvasObject? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : Objects.FirstOrDefault(o => o.Id == id);

    public IEnumerable<CanvasObject> Enumerate() => Objects.OrderBy(o => o.ZOrder).ThenBy(o => o.CreatedAt);

    public int NextZOrder() => Objects.Count == 0 ? 0 : Objects.Max(o => o.ZOrder) + 1;

    /// <summary>Bounding box of everything on the canvas; empty when the canvas is empty.</summary>
    public CRect ContentBounds => CRect.Bounding(Objects.Where(o => o.Visible).Select(o => o.Bounds));

    /// <summary>Topmost visible object whose bounds contain the point, honouring z-order.</summary>
    public CanvasObject? HitTest(CPoint world, double tolerance = 0)
    {
        var probe = tolerance > 0 ? new CRect(world.X - tolerance, world.Y - tolerance, tolerance * 2, tolerance * 2) : new CRect(world.X, world.Y, 0, 0);
        return Enumerate()
            .Where(o => o.Visible && !o.Locked)
            .LastOrDefault(o => tolerance > 0 ? o.Bounds.IntersectsWith(probe) : o.Bounds.Contains(world));
    }

    public IEnumerable<CanvasObject> ObjectsIn(CRect region) =>
        Enumerate().Where(o => o.Visible && o.Bounds.IntersectsWith(region));

    public void Add(CanvasObject obj)
    {
        obj.ZOrder = obj.ZOrder == 0 && Objects.Count > 0 ? NextZOrder() : obj.ZOrder;
        Objects.Add(obj);
        ModifiedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Maps <see cref="CanvasObject.Kind"/> to a CLR type so plugins can persist their own object
/// kinds. The host owns one instance and passes it to the serializer.
/// </summary>
public sealed class CanvasObjectRegistry
{
    private readonly Dictionary<string, Type> _byKind = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = [];

    public CanvasObjectRegistry()
    {
        Register<InkObject>(ObjectKinds.Ink);
        Register<TextObject>(ObjectKinds.Text);
        Register<MathObject>(ObjectKinds.Math);
        Register<WidgetObject>(ObjectKinds.Widget);
        Register<AiCardObject>(ObjectKinds.AiCard);
        Register<ImageObject>(ObjectKinds.Image);
        Register<ShapeObject>(ObjectKinds.Shape);
        Register<GroupObject>(ObjectKinds.Group);
    }

    public static CanvasObjectRegistry Default { get; } = new();

    public void Register<T>(string kind)
        where T : CanvasObject
    {
        _byKind[kind] = typeof(T);
        _byType[typeof(T)] = kind;
    }

    public void Register(string kind, Type type)
    {
        if (!typeof(CanvasObject).IsAssignableFrom(type))
        {
            throw new ArgumentException($"{type} is not a CanvasObject", nameof(type));
        }

        _byKind[kind] = type;
        _byType[type] = kind;
    }

    public bool TryResolve(string kind, out Type type) => _byKind.TryGetValue(kind, out type!);

    public string KindOf(Type type) => _byType.TryGetValue(type, out var kind) ? kind : type.Name;

    public IReadOnlyCollection<string> Kinds => _byKind.Keys;
}
