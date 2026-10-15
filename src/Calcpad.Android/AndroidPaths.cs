using Android.Content;
using Calcpad.Core.Hosting;

namespace Calcpad.Android;

/// <summary>
/// Builds the Android <see cref="AppPaths"/>. Everything lives inside the application sandbox:
/// the database and plugin directory under <c>Context.FilesDir</c>, and user visible exports under
/// the app specific external directory so they can be shared without asking for storage access.
/// </summary>
public static class AndroidPaths
{
    /// <summary>Creates the paths for the current application context.</summary>
    /// <param name="context">Android context, normally the application instance.</param>
    public static AppPaths Create(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var files = context.FilesDir?.AbsolutePath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Calcpad");

        var external = context.GetExternalFilesDir(null)?.AbsolutePath;
        var documents = string.IsNullOrEmpty(external)
            ? Path.Combine(files, "documents")
            : external;

        return new AppPaths(
            files,
            documents,
            Path.Combine(files, "plugins"),
            Path.Combine(files, "models"),
            Path.Combine(files, "cache"),
            Path.Combine(files, "calcpad.db"));
    }
}
