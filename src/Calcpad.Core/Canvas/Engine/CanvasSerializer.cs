using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Calcpad.Core.Canvas.Engine;

/// <summary>Raised when a canvas document cannot be read or written.</summary>
public sealed class CanvasSerializationException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public CanvasSerializationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public CanvasSerializationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// JSON serializer for canvas documents. The document envelope is written explicitly; each object's
/// payload is projected from its public properties, which is what lets plugins persist their own
/// object kinds: register the kind on the <see cref="CanvasObjectRegistry"/> and give the type a
/// public parameterless constructor and public settable properties.
/// </summary>
public sealed class CanvasSerializer : ICanvasSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private readonly CanvasObjectRegistry _registry;
    private readonly ICanvasEngineLog _log;

    /// <summary>Creates a serializer bound to a kind registry.</summary>
    public CanvasSerializer(CanvasObjectRegistry registry, ICanvasEngineLog? log = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _log = log ?? NullCanvasEngineLog.Instance;
    }

    /// <summary>True when the registry knows how to build objects of this kind.</summary>
    public bool CanResolve(string? kind) => !string.IsNullOrEmpty(kind) && _registry.TryResolve(kind, out _);

    /// <inheritdoc />
    public string Serialize(CanvasDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            return JsonSerializer.Serialize(ToDto(document), Options);
        }
        catch (CanvasSerializationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CanvasSerializationException("The canvas document could not be serialized.", ex);
        }
    }

    /// <inheritdoc />
    public CanvasDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CanvasDocument();
        }

        DocumentDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<DocumentDto>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new CanvasSerializationException("The canvas document is not valid JSON.", ex);
        }

        return dto is null ? new CanvasDocument() : FromDto(dto);
    }

    /// <inheritdoc />
    public byte[] SerializeBinary(CanvasDocument document)
    {
        var json = Serialize(document);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            writer.Write(json);
        }

        return output.ToArray();
    }

    /// <inheritdoc />
    public CanvasDocument DeserializeBinary(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return new CanvasDocument();
        }

        try
        {
            using var input = new MemoryStream(data.ToArray(), writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            return Deserialize(reader.ReadToEnd());
        }
        catch (CanvasSerializationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new CanvasSerializationException("The binary canvas document could not be read.", ex);
        }
    }

    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private DocumentDto ToDto(CanvasDocument document)
    {
        var objects = new List<ObjectDto>(document.Objects.Count);
        foreach (var obj in document.Objects)
        {
            objects.Add(ToDto(obj));
        }

        return new DocumentDto
        {
            Id = document.Id,
            Title = document.Title,
            SchemaVersion = document.SchemaVersion,
            Camera = new CameraDto
            {
                OffsetX = document.Camera.OffsetX,
                OffsetY = document.Camera.OffsetY,
                Zoom = document.Camera.Zoom,
                MinZoom = document.Camera.MinZoom,
                MaxZoom = document.Camera.MaxZoom
            },
            Metadata = new Dictionary<string, string>(document.Metadata, StringComparer.Ordinal),
            CreatedAt = document.CreatedAt,
            ModifiedAt = document.ModifiedAt,
            Selection = [.. document.Selection],
            Objects = objects
        };
    }

    private ObjectDto ToDto(CanvasObject obj)
    {
        var kind = obj.Kind;
        if (string.IsNullOrEmpty(kind))
        {
            kind = _registry.KindOf(obj.GetType());
        }

        if (!CanResolve(kind))
        {
            _log.Warn($"Canvas object kind '{kind}' ({obj.GetType().FullName}) is not registered; the document will not load without it.");
        }

        return new ObjectDto
        {
            Kind = kind,
            Id = obj.Id,
            Bounds = new RectDto { X = obj.Bounds.X, Y = obj.Bounds.Y, Width = obj.Bounds.Width, Height = obj.Bounds.Height },
            ZOrder = obj.ZOrder,
            Locked = obj.Locked,
            Visible = obj.Visible,
            Opacity = obj.Opacity,
            OwnerPluginId = obj.OwnerPluginId,
            GroupId = obj.GroupId,
            CreatedAt = obj.CreatedAt,
            ModifiedAt = obj.ModifiedAt,
            Tags = new Dictionary<string, string>(obj.Tags, StringComparer.Ordinal),
            Payload = WritePayload(obj)
        };
    }

    private JsonObject WritePayload(CanvasObject obj)
    {
        var payload = new JsonObject();
        foreach (var property in ObjectStateCopier.PayloadProperties(obj.GetType()))
        {
            object? value;
            try
            {
                value = property.GetValue(obj);
            }
            catch (Exception ex)
            {
                throw new CanvasSerializationException($"Reading {obj.GetType().Name}.{property.Name} failed.", ex);
            }

            var name = PayloadName(property.Name);
            if (value is null)
            {
                payload[name] = null;
                continue;
            }

            payload[name] = value is JsonNode node
                ? node.DeepClone()
                : JsonSerializer.SerializeToNode(value, property.PropertyType, Options);
        }

        return payload;
    }

    /// <summary>Payload member names follow the same camelCase convention as the envelope.</summary>
    private static string PayloadName(string propertyName) => JsonNamingPolicy.CamelCase.ConvertName(propertyName);

    /// <summary>
    /// Reads one payload member. camelCase is the written form; the exact CLR name and a case
    /// insensitive match are accepted so files written by earlier builds still load.
    /// </summary>
    private static bool TryReadPayload(JsonObject payload, string propertyName, out JsonNode? node)
    {
        if (payload.TryGetPropertyValue(PayloadName(propertyName), out node))
        {
            return true;
        }

        if (payload.TryGetPropertyValue(propertyName, out node))
        {
            return true;
        }

        foreach (var pair in payload)
        {
            if (string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                node = pair.Value;
                return true;
            }
        }

        node = null;
        return false;
    }

    private CanvasDocument FromDto(DocumentDto dto)
    {
        var schema = dto.SchemaVersion <= 0 ? CanvasDocument.CurrentSchemaVersion : dto.SchemaVersion;
        if (schema > CanvasDocument.CurrentSchemaVersion)
        {
            _log.Warn($"The canvas document uses schema {schema} but this build understands {CanvasDocument.CurrentSchemaVersion}; unknown data is ignored.");
        }

        var document = new CanvasDocument
        {
            Id = string.IsNullOrEmpty(dto.Id) ? Guid.NewGuid().ToString("n") : dto.Id,
            Title = dto.Title ?? "Untitled",
            SchemaVersion = schema,
            Camera = ReadCamera(dto.Camera),
            Metadata = dto.Metadata is null
                ? []
                : new Dictionary<string, string>(dto.Metadata, StringComparer.Ordinal),
            CreatedAt = dto.CreatedAt == default ? DateTimeOffset.UtcNow : dto.CreatedAt,
            ModifiedAt = dto.ModifiedAt == default ? DateTimeOffset.UtcNow : dto.ModifiedAt
        };

        if (dto.Objects is not null)
        {
            foreach (var objectDto in dto.Objects)
            {
                if (objectDto is null || string.IsNullOrEmpty(objectDto.Kind))
                {
                    continue;
                }

                document.Objects.Add(FromDto(objectDto));
            }
        }

        if (dto.Selection is not null)
        {
            foreach (var id in dto.Selection)
            {
                if (!string.IsNullOrEmpty(id) && document.Find(id) is not null && !document.Selection.Contains(id))
                {
                    document.Selection.Add(id);
                }
            }
        }

        return document;
    }

    private CanvasObject FromDto(ObjectDto dto)
    {
        if (!_registry.TryResolve(dto.Kind, out var type))
        {
            throw new CanvasSerializationException(
                $"Unknown canvas object kind '{dto.Kind}'. Register it on the CanvasObjectRegistry before loading the document.");
        }

        CanvasObject instance;
        try
        {
            instance = Activator.CreateInstance(type) as CanvasObject
                ?? throw new CanvasSerializationException($"'{type}' cannot be created as a canvas object.");
        }
        catch (CanvasSerializationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CanvasSerializationException(
                $"Canvas object type '{type}' needs a public parameterless constructor.", ex);
        }

        if (dto.Payload is not null)
        {
            foreach (var property in ObjectStateCopier.PayloadProperties(type))
            {
                if (!property.CanWrite || !TryReadPayload(dto.Payload, property.Name, out var node))
                {
                    continue;
                }

                if (node is null)
                {
                    property.SetValue(instance, null);
                    continue;
                }

                try
                {
                    property.SetValue(instance, node.Deserialize(property.PropertyType, Options));
                }
                catch (Exception ex)
                {
                    throw new CanvasSerializationException(
                        $"Reading {type.Name}.{property.Name} from the document failed.", ex);
                }
            }
        }

        instance.Id = string.IsNullOrEmpty(dto.Id) ? Guid.NewGuid().ToString("n") : dto.Id;
        instance.Bounds = new CRect(dto.Bounds.X, dto.Bounds.Y, dto.Bounds.Width, dto.Bounds.Height);
        instance.ZOrder = dto.ZOrder;
        instance.Locked = dto.Locked;
        instance.Visible = dto.Visible;
        instance.Opacity = SanitizeOpacity(dto.Opacity);
        instance.OwnerPluginId = dto.OwnerPluginId;
        instance.GroupId = dto.GroupId;
        instance.CreatedAt = dto.CreatedAt == default ? DateTimeOffset.UtcNow : dto.CreatedAt;
        instance.ModifiedAt = dto.ModifiedAt == default ? instance.CreatedAt : dto.ModifiedAt;
        instance.Tags = dto.Tags is null
            ? []
            : new Dictionary<string, string>(dto.Tags, StringComparer.Ordinal);
        return instance;
    }

    private static Camera ReadCamera(CameraDto? dto)
    {
        if (dto is null)
        {
            return new Camera();
        }

        var camera = new Camera
        {
            OffsetX = double.IsFinite(dto.OffsetX) ? dto.OffsetX : 0,
            OffsetY = double.IsFinite(dto.OffsetY) ? dto.OffsetY : 0,
            Zoom = SanitizeZoom(dto.Zoom, 1),
            MinZoom = SanitizeZoom(dto.MinZoom, 0.05),
            MaxZoom = SanitizeZoom(dto.MaxZoom, 12)
        };

        if (camera.MinZoom > camera.MaxZoom)
        {
            (camera.MinZoom, camera.MaxZoom) = (camera.MaxZoom, camera.MinZoom);
        }

        camera.Zoom = Math.Clamp(camera.Zoom, camera.MinZoom, camera.MaxZoom);
        return camera;
    }

    private static double SanitizeZoom(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : fallback;

    private static double SanitizeOpacity(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;

    private sealed class DocumentDto
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public int SchemaVersion { get; set; }

        public CameraDto? Camera { get; set; }

        public Dictionary<string, string>? Metadata { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset ModifiedAt { get; set; }

        public List<string>? Selection { get; set; }

        public List<ObjectDto>? Objects { get; set; }
    }

    private sealed class CameraDto
    {
        public double OffsetX { get; set; }

        public double OffsetY { get; set; }

        public double Zoom { get; set; } = 1;

        public double MinZoom { get; set; } = 0.05;

        public double MaxZoom { get; set; } = 12;
    }

    private sealed class ObjectDto
    {
        public string Kind { get; set; } = string.Empty;

        public string? Id { get; set; }

        public RectDto Bounds { get; set; } = new();

        public int ZOrder { get; set; }

        public bool Locked { get; set; }

        public bool Visible { get; set; } = true;

        public double Opacity { get; set; } = 1;

        public string? OwnerPluginId { get; set; }

        public string? GroupId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset ModifiedAt { get; set; }

        public Dictionary<string, string>? Tags { get; set; }

        public JsonObject? Payload { get; set; }
    }

    private sealed class RectDto
    {
        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }
    }
}
