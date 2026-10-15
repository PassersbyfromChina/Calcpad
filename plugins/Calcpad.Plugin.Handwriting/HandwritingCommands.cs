namespace Calcpad.Plugin.Handwriting;

/// <summary>Command ids declared in the plugin manifest. The shell dispatches these back to the plugin.</summary>
public static class HandwritingCommands
{
    /// <summary>Recognise every ink object on the canvas.</summary>
    public const string RecognizeWholeCanvas = "handwriting.recognize.canvas";

    /// <summary>Lasso a region and recognise only that region.</summary>
    public const string RecognizeRegion = "handwriting.recognize.region";

    /// <summary>Recognise the ink objects of the current selection.</summary>
    public const string RecognizeSelection = "handwriting.recognize.selection";

    /// <summary>Recognise the object the command was invoked on again.</summary>
    public const string ReRecognizeObject = "handwriting.recognize.object";

    /// <summary>Accept the pending recognition result and write it back.</summary>
    public const string AcceptResult = "handwriting.accept";

    /// <summary>Undo the last write back.</summary>
    public const string UndoResult = "handwriting.undo";

    /// <summary>Show the recognition result panel.</summary>
    public const string OpenResults = "handwriting.open.results";

    /// <summary>Show the settings panel.</summary>
    public const string OpenSettings = "handwriting.open.settings";

    /// <summary>Id of the results panel.</summary>
    public const string ResultsPanelId = "handwriting.results";

    /// <summary>Id of the settings panel.</summary>
    public const string SettingsPanelId = "handwriting.settings";

    /// <summary>Overlay used for the "which scope?" prompt.</summary>
    public const string ScopeOverlayId = "handwriting.scope";
}
