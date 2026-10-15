using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Calcpad.App.Views.Dialogs;

public sealed class ConfirmDialog : DialogBase
{
    public ConfirmDialog(string title, string message)
        : base(title)
    {
        var ok = Primary("确定");
        var cancel = Secondary("取消");
        ok.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);

        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(Heading(title));
        stack.Children.Add(Body(message));
        stack.Children.Add(Row(cancel, ok));
        SetBody(stack);
    }
}

public sealed class PromptDialog : DialogBase
{
    public PromptDialog(string title, string message, string? initial)
        : base(title)
    {
        var box = new TextBox
        {
            Text = initial ?? string.Empty,
            AcceptsReturn = false,
            Watermark = "输入…",
            FontSize = 14
        };

        var ok = Primary("确定");
        var cancel = Secondary("取消");
        ok.Click += (_, _) => Close(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text);
        cancel.Click += (_, _) => Close(null);

        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(Heading(title));
        stack.Children.Add(Body(message));
        stack.Children.Add(box);
        stack.Children.Add(Row(cancel, ok));
        SetBody(stack);
        Opened += (_, _) => box.Focus();
    }
}

/// <summary>Shown before anything is uploaded. This is the last line of defence for user content.</summary>
public sealed class EgressReviewDialog : DialogBase
{
    public EgressReviewDialog(string pluginName, Calcpad.Core.Security.EgressPreview preview)
        : base("确认上传内容", 560)
    {
        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(Heading("即将把以下内容发送到云端模型"));

        var summary = new StackPanel { Spacing = 4 };
        summary.Children.Add(Body($"发起插件：{pluginName}"));
        summary.Children.Add(Body($"用途：{preview.Intent}"));
        summary.Children.Add(Body($"目标服务：{preview.Destination}"));
        summary.Children.Add(Body($"模型：{preview.Model}"));
        summary.Children.Add(Body($"总字符数：{preview.TotalCharacters}    总字节数：{preview.TotalBytes}"));
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1C1C1E")),
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(14),
            Child = summary
        });

        var hasImage = false;
        foreach (var part in preview.Parts)
        {
            var card = new StackPanel { Spacing = 6 };
            var label = part.Kind switch
            {
                Calcpad.Core.Security.EgressPartKind.Text => "文本",
                Calcpad.Core.Security.EgressPartKind.Image => "图片",
                _ => "笔迹"
            };
            card.Children.Add(new TextBlock
            {
                Text = $"{label} · {part.Label} · {part.ByteCount} 字节",
                Foreground = new SolidColorBrush(Color.Parse("#FFFF9F0A")),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold
            });

            if (part.Kind == Calcpad.Core.Security.EgressPartKind.Image)
            {
                hasImage = true;
                card.Children.Add(Body("图片会以原始像素发送，请确认其中不含你不想外传的内容。"));
            }
            else if (!string.IsNullOrEmpty(part.Text))
            {
                var shown = part.Text.Length > 1200 ? part.Text[..1200] + "\n…（已截断显示，实际发送完整内容）" : part.Text;
                card.Children.Add(new SelectableTextBlock
                {
                    Text = shown,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#FFE6E6EA"))
                });
            }

            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FF202024")),
                CornerRadius = new Avalonia.CornerRadius(10),
                Padding = new Avalonia.Thickness(12),
                Child = card
            });
        }

        var remember = new CheckBox { Content = "本次会话内不再询问同一用途", FontSize = 12 };

        var send = Primary("发送");
        var sendTextOnly = Secondary("仅发送文字");
        var cancel = Secondary("取消");
        send.Click += (_, _) => Close(Calcpad.Core.Security.EgressDecision.Approve(remember.IsChecked == true));
        sendTextOnly.Click += (_, _) => Close(Calcpad.Core.Security.EgressDecision.ApproveTextOnly(remember.IsChecked == true));
        cancel.Click += (_, _) => Close(Calcpad.Core.Security.EgressDecision.Blocked);

        if (!hasImage)
        {
            sendTextOnly.IsVisible = false;
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(sendTextOnly);
        buttons.Children.Add(remember);
        buttons.Children.Add(send);
        stack.Children.Add(buttons);

        SetBody(stack);
    }
}

/// <summary>Generic picker used when a recogniser must not guess for the user.</summary>
public sealed class CandidatePickerDialog : DialogBase
{
    public CandidatePickerDialog(string title, string prompt, IReadOnlyList<(string Text, double Confidence, string? Note)> candidates, bool allowEdit)
        : base(title, 520)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Heading(title));
        stack.Children.Add(Body(prompt));

        var editor = new TextBox
        {
            Text = candidates.Count > 0 ? candidates[0].Text : string.Empty,
            IsVisible = allowEdit,
            FontSize = 18,
            FontFamily = new FontFamily("Consolas, Menlo, monospace")
        };

        foreach (var candidate in candidates)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Avalonia.Thickness(14, 10),
                CornerRadius = new Avalonia.CornerRadius(10),
                Background = new SolidColorBrush(Color.Parse("#FF26262A")),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = candidate.Text,
                            FontSize = 20,
                            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                            Foreground = Brushes.White
                        },
                        new TextBlock
                        {
                            Text = $"{(candidate.Confidence * 100):0}%",
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = new SolidColorBrush(Color.Parse("#FF9A9AA0"))
                        },
                        new TextBlock
                        {
                            Text = candidate.Note ?? string.Empty,
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = new SolidColorBrush(Color.Parse("#FF9A9AA0"))
                        }
                    }
                }
            };
            var text = candidate.Text;
            button.Click += (_, _) => Close(text);
            stack.Children.Add(button);
        }

        stack.Children.Add(editor);

        var useEdited = Primary("使用输入的文本");
        useEdited.Click += (_, _) => Close(string.IsNullOrWhiteSpace(editor.Text) ? null : editor.Text);
        var skip = Secondary("跳过");
        skip.Click += (_, _) => Close(null);
        stack.Children.Add(Row(skip, useEdited));

        SetBody(stack);
    }
}
