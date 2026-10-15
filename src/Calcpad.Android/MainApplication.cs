using Android.App;
using Android.Runtime;
using Calcpad.App;

namespace Calcpad.Android;

/// <summary>
/// Android application object. It publishes the sandboxed paths before Avalonia builds the
/// application, which is what the composition root consumes through
/// <c>CalcpadBootstrapper.PathsOverride</c>.
/// </summary>
[Application(Label = "Calcpad")]
public class MainApplication : Application
{
    /// <summary>Creates the application object.</summary>
    /// <param name="javaReference">Java object reference supplied by the runtime.</param>
    /// <param name="transfer">Ownership of the reference.</param>
    public MainApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    /// <inheritdoc />
    public override void OnCreate()
    {
        CalcpadBootstrapper.PathsOverride = AndroidPaths.Create(this);
        base.OnCreate();
    }
}
