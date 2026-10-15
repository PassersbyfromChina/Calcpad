using Calcpad.Core.Canvas;

namespace Calcpad.Plugin.AskAi;

/// <summary>Command ids of the ask-AI plugin.</summary>
public static class AskAiCommands
{
    /// <summary>Lasso something and ask about it.</summary>
    public const string AskRegion = "askai.ask.region";

    /// <summary>Ask about the whole canvas: text summary with per object opt in.</summary>
    public const string AskWholeCanvas = "askai.ask.canvas";

    /// <summary>Ask about the current selection.</summary>
    public const string AskSelection = "askai.ask.selection";

    /// <summary>Show the preview panel for the pending draft.</summary>
    public const string OpenPreview = "askai.open.preview";

    /// <summary>Ask again with the same context.</summary>
    public const string Regenerate = "askai.regenerate";

    /// <summary>Insert the last answer as a plain text object.</summary>
    public const string InsertAnswerAsText = "askai.insert.text";

    /// <summary>Remove the last answer card.</summary>
    public const string DeleteCard = "askai.delete.card";

    /// <summary>Show the settings panel.</summary>
    public const string OpenSettings = "askai.open.settings";

    /// <summary>Preview panel id.</summary>
    public const string PreviewPanelId = "askai.panel.preview";

    /// <summary>Settings panel id.</summary>
    public const string SettingsPanelId = "askai.panel.settings";

    /// <summary>Floating action bar overlay id.</summary>
    public const string ActionBarId = "askai.overlay.actionbar";
}

/// <summary>
/// Exactly what the user is about to send. The preview panel edits this object and the send path
/// consumes it verbatim: nothing is added in transit.
/// </summary>
public sealed class AskAiDraft
{
    /// <summary>Objects the context came from, for the audit trail and for the card placement.</summary>
    public IReadOnlyList<string> SourceObjectIds { get; set; } = [];

    /// <summary>World region the lasso covered, when there was one.</summary>
    public CRect? Region { get; set; }

    /// <summary>Question the user typed.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>Text context the user chose to include.</summary>
    public string ContextText { get; set; } = string.Empty;

    /// <summary>PNG crop the user chose to include.</summary>
    public byte[]? ContextPng { get; set; }

    /// <summary>Whether the text context is part of the payload.</summary>
    public bool IncludeText { get; set; } = true;

    /// <summary>Whether the image is part of the payload.</summary>
    public bool IncludeImage { get; set; }

    /// <summary>Prefer an on-device endpoint for this request.</summary>
    public bool PreferLocal { get; set; } = true;

    /// <summary>Bytes the payload will occupy (text as UTF-8 plus the PNG when included).</summary>
    public int TotalBytes =>
        (IncludeText ? System.Text.Encoding.UTF8.GetByteCount(ContextText ?? string.Empty) : 0) +
        System.Text.Encoding.UTF8.GetByteCount(Question ?? string.Empty) +
        (IncludeImage && ContextPng is { Length: > 0 } png ? png.Length : 0);

    /// <summary>Human readable payload summary for the UI.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Question))
        {
            parts.Add($"问题 {System.Text.Encoding.UTF8.GetByteCount(Question)} 字节");
        }

        if (IncludeText && !string.IsNullOrWhiteSpace(ContextText))
        {
            parts.Add($"画布文字 {System.Text.Encoding.UTF8.GetByteCount(ContextText)} 字节 / {ContextText.Length} 字");
        }

        if (IncludeImage && ContextPng is { Length: > 0 } png)
        {
            parts.Add($"圈选图像 {png.Length} 字节");
        }

        return parts.Count == 0 ? "没有内容可发送" : string.Join("，", parts) + $"，合计 {TotalBytes} 字节";
    }

    /// <summary>Builds the request handed to <c>IAiApi.AskAsync</c>.</summary>
    public Calcpad.Core.Ai.AskRequest ToRequest(bool allowCloud) => new()
    {
        Question = Question,
        ContextText = IncludeText ? ContextText : null,
        ContextPng = IncludeImage ? ContextPng : null,
        Region = Region,
        AllowCloud = allowCloud,
        PreferOnDevice = PreferLocal
    };
}
