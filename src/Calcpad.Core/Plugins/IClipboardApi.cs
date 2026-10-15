namespace Calcpad.Core.Plugins;

/// <summary>
/// Per plugin clipboard access. Deliberately NOT part of <see cref="IHostApi"/>: the host API is a
/// singleton that a plugin holds a reference to, so putting the clipboard there would let a plugin
/// read and write the clipboard without the user ever granting it. This facade is created per
/// plugin and checks <c>ClipboardWrite</c> / <c>ClipboardRead</c> on every call.
/// </summary>
public interface IClipboardApi
{
    /// <summary>Copies text. Requires <c>ClipboardWrite</c>; throws when the user refuses.</summary>
    ValueTask SetTextAsync(string text, CancellationToken ct = default);

    /// <summary>Reads clipboard text, or null when it holds none. Requires <c>ClipboardRead</c>.</summary>
    ValueTask<string?> GetTextAsync(CancellationToken ct = default);
}

/// <summary>
/// Fail closed default. Contexts that are not backed by a real clipboard keep this instance, so a
/// plugin gets a clear refusal instead of silently doing nothing.
/// </summary>
public sealed class DeniedClipboardApi : IClipboardApi
{
    public static DeniedClipboardApi Instance { get; } = new();

    public ValueTask SetTextAsync(string text, CancellationToken ct = default) =>
        throw new Security.PermissionDeniedException("(unknown)", Security.PermissionKind.ClipboardWrite, nameof(IClipboardApi.SetTextAsync));

    public ValueTask<string?> GetTextAsync(CancellationToken ct = default) =>
        throw new Security.PermissionDeniedException("(unknown)", Security.PermissionKind.ClipboardRead, nameof(IClipboardApi.GetTextAsync));
}
