using System.Collections.Concurrent;
using System.Reflection;

namespace Calcpad.Core.Canvas.Engine;

/// <summary>
/// Copies the public state of one canvas object onto another instance of the same type. Undo and
/// redo need to restore an object in place, and this has to keep working for plugin defined kinds
/// that the engine has never seen, so the property table is built once per type with reflection.
/// </summary>
internal static class ObjectStateCopier
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> StateCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PayloadCache = new();

    /// <summary>Every writable public instance property, including the ones declared by
    /// <see cref="CanvasObject"/> itself.</summary>
    internal static PropertyInfo[] StateProperties(Type type) => StateCache.GetOrAdd(type, static t => Collect(t, includeBase: true));

    /// <summary>Writable public instance properties declared by the concrete type only; the base
    /// properties travel in the serialization envelope instead.</summary>
    internal static PropertyInfo[] PayloadProperties(Type type) => PayloadCache.GetOrAdd(type, static t => Collect(t, includeBase: false));

    /// <summary>Copies state from <paramref name="source"/> into <paramref name="target"/>.</summary>
    /// <exception cref="ArgumentException">The two objects are of different types.</exception>
    internal static void Copy(CanvasObject source, CanvasObject target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (source.GetType() != target.GetType())
        {
            throw new ArgumentException($"Cannot copy state from {source.GetType()} into {target.GetType()}.", nameof(target));
        }

        foreach (var property in StateProperties(source.GetType()))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            try
            {
                property.SetValue(target, property.GetValue(source));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Property {source.GetType().Name}.{property.Name} could not be restored; canvas objects need a public setter.",
                    ex);
            }
        }
    }

    private static PropertyInfo[] Collect(Type type, bool includeBase) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead &&
                        p.GetIndexParameters().Length == 0 &&
                        (includeBase || p.DeclaringType != typeof(CanvasObject)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();
}
