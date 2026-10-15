using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Calcpad.Core.Plugins;
using Calcpad.App.Views.Dialogs;

namespace Calcpad.App.Views;

public sealed partial class MainView
{
    private async ValueTask ZoomAsync(double factor, bool reset = false)
    {
        var surface = _surface;
        if (surface is null)
        {
            return;
        }

        if (reset)
        {
            var zoom = _runtime.Canvas.Camera.Zoom;
            await surface.ZoomByAsync(1.0 / zoom);
        }
        else
        {
            await surface.ZoomByAsync(factor);
        }

        UpdateStatus();
    }

    private async ValueTask ZoomToFitAsync()
    {
        if (_surface is null)
        {
            return;
        }

        await _surface.ZoomToFitAsync();
        UpdateStatus();
    }

    /// <summary>Central dispatch: shell commands first, then whatever plugin owns the command id.</summary>
    private async ValueTask ExecuteCommandAsync(string commandId, string trigger)
    {
        try
        {
            if (commandId.StartsWith(ShellCommandPrefix, StringComparison.Ordinal))
            {
                await ExecuteShellCommandAsync(commandId[ShellCommandPrefix.Length..]);
                return;
            }

            var context = new CommandContext { CommandId = commandId, Trigger = trigger };
            var handled = await _runtime.Plugins.ExecuteCommandAsync(commandId, context);
            if (!handled)
            {
                await _runtime.Host.ShowToastAsync($"没有插件响应该命令：{commandId}", ToastLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            await _runtime.Host.ShowToastAsync($"命令执行失败：{ex.Message}", ToastLevel.Error);
        }
        finally
        {
            UpdateStatus();
        }
    }

    private async ValueTask ExecuteShellCommandAsync(string name)
    {
        switch (name)
        {
            case "file.new":
                if (await ConfirmDiscardAsync())
                {
                    _runtime.Workspace.NewDocument();
                }

                break;

            case "file.open":
                await OpenAsync();
                break;

            case "file.save":
                await SaveAsync(false);
                break;

            case "file.saveAs":
                await SaveAsync(true);
                break;

            case "file.exportPng":
                await ExportPngAsync();
                break;

            case "file.exit":
                (TopLevel.GetTopLevel(this) as Window)?.Close();
                break;

            case "edit.undo":
                _runtime.Canvas.Undo();
                break;

            case "edit.redo":
                _runtime.Canvas.Redo();
                break;

            case "edit.delete":
                foreach (var id in _runtime.Canvas.Document.Selection.ToList())
                {
                    _runtime.Canvas.Remove(id);
                }

                _runtime.Canvas.SetSelection([]);
                break;

            case "edit.selectAll":
                _runtime.Canvas.SetSelection(_runtime.Canvas.Document.Objects.Select(o => o.Id));
                break;

            case "edit.clear":
                if (await _runtime.Host.ConfirmAsync("清空画布", "将删除画布上的全部内容，此操作可以撤销。"))
                {
                    using (_runtime.Canvas.BeginEdit("清空画布"))
                    {
                        foreach (var id in _runtime.Canvas.Document.Objects.Select(o => o.Id).ToList())
                        {
                            _runtime.Canvas.Remove(id);
                        }
                    }
                }

                break;

            case "view.zoomIn":
                await ZoomAsync(1.25);
                break;

            case "view.zoomOut":
                await ZoomAsync(1 / 1.25);
                break;

            case "view.fit":
                await ZoomToFitAsync();
                break;

            case "view.actual":
                await ZoomAsync(1.0 / _runtime.Canvas.Camera.Zoom);
                break;

            case "view.toggleGrid":
                _runtime.Settings.ShowGrid = !_runtime.Settings.ShowGrid;
                _runtime.Settings.Save();
                _surface?.SetGridVisible(_runtime.Settings.ShowGrid);
                break;

            case "plugins.manage":
                await ShowPluginManagerAsync();
                break;

            case "plugins.audit":
                await ShowAuditAsync();
                break;

            case "plugins.openFolder":
                await _runtime.Host.ShowToastAsync($"插件目录：{_runtime.Paths.PluginsDirectory}");
                break;

            case "help.about":
                await ShowAboutAsync();
                break;

            default:
                await _runtime.Host.ShowToastAsync($"未知命令：{name}", ToastLevel.Warning);
                break;
        }
    }

    private async ValueTask<bool> ConfirmDiscardAsync()
    {
        if (!_runtime.Workspace.IsDirty)
        {
            return true;
        }

        return await _runtime.Host.ConfirmAsync("未保存的更改", "当前画布有未保存的更改，确定放弃吗？");
    }

    private async ValueTask OpenAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开画布",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(WorkspaceService.FileFilterName) { Patterns = ["*.calcpad.json", "*.json"] }
            ]
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        try
        {
            if (await _runtime.Workspace.LoadAsync(path))
            {
                _runtime.Settings.RememberDocument(path);
                _runtime.Settings.Save();
                await _runtime.Host.ShowToastAsync($"已打开 {Path.GetFileName(path)}", ToastLevel.Success);
            }
            else
            {
                await _runtime.Host.ShowToastAsync("文件不存在", ToastLevel.Error);
            }
        }
        catch (Exception ex)
        {
            await _runtime.Host.ShowToastAsync($"打开失败：{ex.Message}", ToastLevel.Error);
        }

        UpdateStatus();
    }

    private async ValueTask SaveAsync(bool forceDialog)
    {
        var top = TopLevel.GetTopLevel(this);
        string? target = forceDialog ? null : _runtime.Workspace.CurrentPath;

        if (target is null)
        {
            if (top is null)
            {
                return;
            }

            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存画布",
                SuggestedFileName = WorkspaceService.SuggestFileName(_runtime.Workspace.DocumentTitle),
                DefaultExtension = "json",
                FileTypeChoices =
                [
                    new FilePickerFileType(WorkspaceService.FileFilterName) { Patterns = ["*.calcpad.json"] }
                ]
            });

            target = file?.TryGetLocalPath();
        }

        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        try
        {
            await _runtime.Workspace.SaveAsync(target);
            _runtime.Settings.RememberDocument(target);
            _runtime.Settings.Save();
            await _runtime.Host.ShowToastAsync("已保存", ToastLevel.Success);
        }
        catch (Exception ex)
        {
            await _runtime.Host.ShowToastAsync($"保存失败：{ex.Message}", ToastLevel.Error);
        }

        UpdateStatus();
    }

    private async ValueTask ExportPngAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var bounds = _runtime.Canvas.Document.ContentBounds;
        if (bounds.IsEmpty)
        {
            await _runtime.Host.ShowToastAsync("画布是空的", ToastLevel.Warning);
            return;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 PNG",
            SuggestedFileName = WorkspaceService.SuggestFileName(_runtime.Workspace.DocumentTitle).Replace(".calcpad.json", ".png", StringComparison.Ordinal),
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }]
        });

        var target = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        var raster = await _runtime.Canvas.RasterizeAsync(bounds.Inflate(40), 2.0);
        if (raster is null)
        {
            await _runtime.Host.ShowToastAsync("导出失败：光栅化不可用", ToastLevel.Error);
            return;
        }

        await File.WriteAllBytesAsync(target, raster.Png);
        await _runtime.Host.ShowToastAsync("已导出 PNG", ToastLevel.Success);
    }

    private async ValueTask ShowPluginManagerAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = new PluginManagerWindow(_runtime);
        if (owner is not null)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }

        RebuildContributions();
    }

    private async ValueTask ShowAuditAsync()
    {
        var entries = await _runtime.Egress.GetAuditAsync(200);
        var text = entries.Count == 0
            ? "还没有任何上传记录。"
            : string.Join("\n", entries.Select(e =>
                $"{e.At:yyyy-MM-dd HH:mm:ss}  {(e.Allowed ? "已允许" : "已拒绝")}  {e.PluginId}  {e.Intent}  {e.Destination}/{e.Model}  {e.Characters} 字  {e.Bytes} 字节"));

        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new PromptDialog("上传审计", "所有云上传（无论允许还是拒绝）都会记录在这里。", text);
        if (owner is not null)
        {
            await dialog.ShowDialog<string?>(owner);
        }
    }

    private async ValueTask ShowAboutAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var lines = new List<string>
        {
            $"Calcpad {_runtime.Host.Info.Version}",
            $"平台：{_runtime.Host.Info.Platform}",
            $"运行时：{_runtime.Host.Info.RuntimeVersion}",
            $"数据目录：{_runtime.Paths.DataDirectory}",
            string.Empty,
            "无限画布 + 插件驱动。所有插件能力都需要授权，云上传默认禁止且逐次确认。"
        };

        var dialog = new ConfirmDialog("关于 Calcpad", string.Join("\n", lines));
        if (owner is not null)
        {
            await dialog.ShowDialog<bool>(owner);
        }
    }
}
