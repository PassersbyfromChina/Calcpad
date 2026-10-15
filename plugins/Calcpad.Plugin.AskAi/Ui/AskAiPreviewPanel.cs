using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Calcpad.Core.Ai;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.AskAi.Ui;

/// <summary>
/// The payload preview and chat panel. Everything the user is about to send is visible here: the
/// exact text (editable), the image thumbnail with its byte size, the destination endpoint and model,
/// and the upload switch state. Follow-up, regenerate, copy and delete live here as well.
/// </summary>
public sealed class AskAiPreviewPanel
{
    private readonly AskAiPlugin _plugin;
    private readonly PluginTheme _theme;
    private readonly StackPanel _root = AskAiUi.VStack(10);
    private TextBox? _question;
    private TextBox? _context;
    private TextBox? _followUp;
    private TextBlock? _summary;

    /// <summary>Creates the panel.</summary>
    public AskAiPreviewPanel(AskAiPlugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _theme = plugin.Theme;
        _plugin.StateChanged += (_, _) => Render();
    }

    /// <summary>Builds the control the shell hosts.</summary>
    public Control Build()
    {
        var scroll = new ScrollViewer
        {
            Content = _root,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        Render();
        return scroll;
    }

    /// <summary>Rebuilds the panel.</summary>
    public void Render()
    {
        _root.Children.Clear();
        _root.Children.Add(AskAiUi.Title("问 AI", _theme));

        var draft = _plugin.Draft;
        var capabilities = _plugin.Capabilities;
        var local = _plugin.IsLocalOnly;

        var badges = AskAiUi.HStack(6);
        badges.Children.Add(AskAiUi.Badge(local ? "本地处理，未联网" : "云端（发送前会确认）", local ? _theme.Success : _theme.Accent, _theme));
        if (capabilities is not null)
        {
            badges.Children.Add(AskAiUi.Badge(
                capabilities.UploadEnabled ? "云上传：开" : "云上传：关",
                capabilities.UploadEnabled ? _theme.TextSecondary : _theme.Danger,
                _theme));
        }

        _root.Children.Add(badges);

        if (draft is null)
        {
            _root.Children.Add(AskAiUi.Body("先用套索圈出内容，或使用「整画布提问」。", _theme, secondary: true));
            _root.Children.Add(AskAiUi.Secondary("整画布提问（只发勾选文字）", _theme, () => _ = _plugin.AskWholeCanvasAsync()));
            return;
        }

        _root.Children.Add(SourcesCard(draft));
        _root.Children.Add(QuestionEditor(draft));
        _root.Children.Add(ContextEditor(draft));
        _root.Children.Add(PayloadCard(draft));
        _root.Children.Add(Actions(draft));

        if (_plugin.IsStreaming)
        {
            _root.Children.Add(AskAiUi.Banner("正在接收回答…", _theme.Accent, _theme));
        }

        if (!string.IsNullOrEmpty(_plugin.Status))
        {
            _root.Children.Add(AskAiUi.Banner(_plugin.Status!, _plugin.Status!.StartsWith("已拒绝", StringComparison.Ordinal) ? _theme.Danger : _theme.SurfaceAlt, _theme));
        }

        if (!string.IsNullOrEmpty(_plugin.LastAnswer))
        {
            _root.Children.Add(AnswerCard());
            _root.Children.Add(FollowUpRow());
        }
    }

    private Control SourcesCard(AskAiDraft draft)
    {
        var stack = AskAiUi.VStack(4);
        stack.Children.Add(AskAiUi.Body("来源", _theme));
        stack.Children.Add(AskAiUi.Body(
            draft.SourceObjectIds.Count == 0
                ? "整画布文本（未生成截图）"
                : $"{draft.SourceObjectIds.Count} 个对象" + (draft.Region is { } region ? $"，区域 {region.Width:0}×{region.Height:0} 世界单位" : string.Empty),
            _theme,
            secondary: true));
        stack.Children.Add(AskAiUi.Body("整画布模式只发送你勾选的文字，绝不生成整画布截图。", _theme, secondary: true));
        return AskAiUi.Card(stack, _theme);
    }

    private Control QuestionEditor(AskAiDraft draft)
    {
        var stack = AskAiUi.VStack(4);
        stack.Children.Add(AskAiUi.Body("问题", _theme));
        _question = new TextBox
        {
            Text = draft.Question,
            Watermark = "例如：解释这个推导，或者检查单位是否一致",
            AcceptsReturn = true,
            Height = 64,
            FontSize = 13
        };
        _question.TextChanged += (_, _) => draft.Question = _question.Text ?? string.Empty;
        stack.Children.Add(_question);
        return stack;
    }

    private Control ContextEditor(AskAiDraft draft)
    {
        var stack = AskAiUi.VStack(4);
        var header = AskAiUi.HStack(8);
        header.Children.Add(AskAiUi.Body("将要发送的文字（可编辑）", _theme));
        header.Children.Add(AskAiUi.Toggle("包含文字", draft.IncludeText, _theme, value => draft.IncludeText = value));
        stack.Children.Add(header);

        _context = new TextBox
        {
            Text = draft.ContextText,
            AcceptsReturn = true,
            Height = 110,
            FontSize = 13,
            IsReadOnly = false,
            TextWrapping = TextWrapping.Wrap
        };
        _context.TextChanged += (_, _) =>
        {
            draft.ContextText = _context.Text ?? string.Empty;
            UpdatePayloadSummary(draft);
        };
        stack.Children.Add(_context);
        return AskAiUi.Card(stack, _theme);
    }

    private Control PayloadCard(AskAiDraft draft)
    {
        var stack = AskAiUi.VStack(6);
        stack.Children.Add(AskAiUi.Body("发送预览", _theme));

        var hasImage = draft.ContextPng is { Length: > 0 };
        if (hasImage)
        {
            stack.Children.Add(AskAiUi.Toggle(
                "只发文字，不发图片",
                !draft.IncludeImage,
                _theme,
                onlyText => draft.IncludeImage = !onlyText));

            var thumbnail = BuildThumbnail(draft.ContextPng!);
            if (thumbnail is not null)
            {
                stack.Children.Add(thumbnail);
            }

            stack.Children.Add(AskAiUi.Body($"图片 {draft.ContextPng!.Length} 字节（PNG 裁剪）", _theme, secondary: true));
        }
        else
        {
            stack.Children.Add(AskAiUi.Body("本次没有图片内容。", _theme, secondary: true));
        }

        var destination = DescribeDestination();
        stack.Children.Add(AskAiUi.Body($"目标服务：{destination}", _theme, secondary: true));
        _summary = new TextBlock
        {
            Text = draft.Describe(),
            FontSize = 12.5,
            Foreground = _theme.Brush(_theme.TextPrimary),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(_summary);
        stack.Children.Add(AskAiUi.Body("本地模型处理时不会上传，也不会弹出上传确认。", _theme, secondary: true));
        return AskAiUi.Card(stack, _theme);
    }

    private Control Actions(AskAiDraft draft)
    {
        var row = AskAiUi.HStack(8);
        var capabilities = _plugin.Capabilities;
        var canSend = draft.TotalBytes > 0 && (capabilities is null || capabilities.OnDeviceChat || capabilities.UploadEnabled);
        var send = AskAiUi.Primary(_plugin.IsStreaming ? "发送中…" : "发送", _theme, () =>
        {
            if (_context is not null)
            {
                _context.Text = draft.ContextText;
            }

            _ = _plugin.SendAsync(_question?.Text ?? draft.Question);
        });
        send.IsEnabled = canSend && !_plugin.IsStreaming;

        row.Children.Add(send);
        row.Children.Add(AskAiUi.Secondary("重新生成", _theme, () => _ = _plugin.RegenerateAsync()));
        row.Children.Add(AskAiUi.Secondary("插入为新文本", _theme, () => _ = _plugin.InsertAnswerAsTextAsync()));
        row.Children.Add(AskAiUi.Secondary("删除卡片", _theme, () => _ = _plugin.DeleteCardAsync()));
        row.Children.Add(AskAiUi.Secondary("设置", _theme, () =>
        {
            if (_plugin.Context is { } context)
            {
                _ = context.Host.OpenPanelAsync(context.PluginId, AskAiCommands.SettingsPanelId);
            }
        }));
        return row;
    }

    private Control AnswerCard()
    {
        var stack = AskAiUi.VStack(6);
        stack.Children.Add(AskAiUi.Body("回答（同时显示在画布卡片上）", _theme));
        var text = new TextBlock
        {
            Text = _plugin.LastAnswer,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _theme.Brush(_theme.TextPrimary)
        };
        stack.Children.Add(text);
        return AskAiUi.Card(stack, _theme, raised: true);
    }

    private Control FollowUpRow()
    {
        var stack = AskAiUi.VStack(4);
        _followUp = new TextBox
        {
            Watermark = "继续追问…",
            FontSize = 13
        };
        stack.Children.Add(_followUp);
        stack.Children.Add(AskAiUi.Secondary("追问", _theme, () =>
        {
            var question = _followUp?.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(question))
            {
                _followUp!.Text = string.Empty;
                _ = _plugin.FollowUpAsync(question);
            }
        }));
        return stack;
    }

    private void UpdatePayloadSummary(AskAiDraft draft)
    {
        // Only the byte counter is refreshed while typing: rebuilding the panel here would steal the
        // caret from the text box the user is editing.
        if (_summary is not null)
        {
            _summary.Text = draft.Describe();
        }
    }

    private Control? BuildThumbnail(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            var bitmap = new Bitmap(stream);
            return new Image
            {
                Source = bitmap,
                Width = 200,
                Height = 130,
                Stretch = Stretch.Uniform
            };
        }
        catch (Exception)
        {
            // A crop that cannot be decoded is still shown as its byte size; never crash the panel.
            return null;
        }
    }

    private string DescribeDestination()
    {
        var providers = _plugin.Providers;
        if (_plugin.IsLocalOnly)
        {
            var local = providers.FirstOrDefault(p => p.IsLocal);
            return local is null ? "本地端点" : $"{local.DisplayName}（本地，未联网）";
        }

        var cloud = providers.FirstOrDefault(p => !p.IsLocal && p.Model is { Length: > 0 }) ?? providers.FirstOrDefault(p => !p.IsLocal);
        return cloud is null ? "未配置云端服务" : $"{cloud.DisplayName} / {cloud.Model ?? "默认模型"}";
    }
}
