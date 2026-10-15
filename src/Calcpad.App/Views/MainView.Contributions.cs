using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Calcpad.Core.Plugins;

namespace Calcpad.App.Views;

public sealed partial class MainView
{
    private const string ShellCommandPrefix = "shell.";

    private sealed class MenuNode
    {
        public string Title { get; set; } = string.Empty;

        public int Order { get; set; } = 100;

        public MenuContribution? Item { get; set; }

        public List<MenuNode> Children { get; } = [];
    }

    private void RebuildContributions()
    {
        _menuBar.ItemsSource = BuildMenus();
        BuildToolStrip();
        BuildShortcuts();
        UpdateStatus();
    }

    private List<MenuItem> BuildMenus()
    {
        var roots = new List<MenuNode>();

        MenuNode Root(string title, int order)
        {
            var existing = roots.FirstOrDefault(r => r.Title == title);
            if (existing is not null)
            {
                return existing;
            }

            var node = new MenuNode { Title = title, Order = order };
            roots.Add(node);
            return node;
        }

        void Shell(string root, string title, string commandId, string? shortcut = null, int order = 100)
        {
            Root(root, ShellRootOrder(root)).Children.Add(new MenuNode
            {
                Title = shortcut is null ? title : $"{title}\t{shortcut}",
                Order = order,
                Item = new MenuContribution($"{root}/{title}", title, ShellCommandPrefix + commandId, Shortcut: shortcut, Order: order)
            });
        }

        // Shell owned menus. Plugins add to these or create their own roots.
        Shell("文件", "新建画布", "file.new", "Ctrl+N", 10);
        Shell("文件", "打开…", "file.open", "Ctrl+O", 20);
        Shell("文件", "保存", "file.save", "Ctrl+S", 30);
        Shell("文件", "另存为…", "file.saveAs", "Ctrl+Shift+S", 40);
        Shell("文件", "导出为 PNG…", "file.exportPng", null, 50);
        Shell("文件", "退出", "file.exit", null, 90);

        Shell("编辑", "撤销", "edit.undo", "Ctrl+Z", 10);
        Shell("编辑", "重做", "edit.redo", "Ctrl+Y", 20);
        Shell("编辑", "删除选中", "edit.delete", "Delete", 30);
        Shell("编辑", "全选", "edit.selectAll", "Ctrl+A", 40);
        Shell("编辑", "清空画布", "edit.clear", null, 80);

        Shell("视图", "放大", "view.zoomIn", "Ctrl+=", 10);
        Shell("视图", "缩小", "view.zoomOut", "Ctrl+-", 20);
        Shell("视图", "适应内容", "view.fit", "Ctrl+0", 30);
        Shell("视图", "显示网格", "view.toggleGrid", "Ctrl+G", 50);

        Shell("插件", "插件管理器…", "plugins.manage", "Ctrl+Shift+P", 10);
        Shell("插件", "上传审计…", "plugins.audit", null, 20);
        Shell("插件", "打开插件目录", "plugins.openFolder", null, 30);

        Shell("帮助", "关于 Calcpad", "help.about", null, 10);

        foreach (var contribution in _runtime.Plugins.Menus)
        {
            var segments = contribution.MenuPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            // Plugins declare portable English roots so a plugin works on any locale; the shell
            // folds those into the localized menus instead of showing two of everything.
            var rootTitle = LocalizeRoot(segments[0]);
            var root = Root(rootTitle, ShellRootOrder(rootTitle));
            var current = root;
            for (var i = 1; i < segments.Length - 1; i++)
            {
                var next = current.Children.FirstOrDefault(c => c.Item is null && c.Title == segments[i]);
                if (next is null)
                {
                    next = new MenuNode { Title = segments[i], Order = contribution.Order };
                    current.Children.Add(next);
                }

                current = next;
            }

            current.Children.Add(new MenuNode
            {
                Title = Localized(contribution.Title, contribution.LocalizedTitleZh) ?? contribution.Title,
                Order = contribution.Order,
                Item = contribution
            });
        }

        return [.. roots.OrderBy(r => r.Order).ThenBy(r => r.Title).Select(BuildMenuItem)];
    }

    /// <summary>Maps the portable English menu roots plugins declare onto the shell's menus.</summary>
    private static string LocalizeRoot(string root) => root switch
    {
        "File" => "文件",
        "Edit" => "编辑",
        "View" => "视图",
        "Insert" => "插入",
        "Tools" => "工具",
        "Plugins" => "插件",
        "Help" => "帮助",
        _ => root
    };

    private MenuItem BuildMenuItem(MenuNode node)
    {
        if (node.Item is null)
        {
            var parent = new MenuItem { Header = node.Title };
            parent.ItemsSource = node.Children
                .OrderBy(c => c.Order)
                .ThenBy(c => c.Title)
                .Select(BuildMenuItem)
                .ToList();
            return parent;
        }

        var item = new MenuItem { Header = node.Title };
        var commandId = node.Item.CommandId;
        item.Click += (_, _) => _ = ExecuteCommandAsync(commandId, "menu");
        return item;
    }

    private static int ShellRootOrder(string root) => root switch
    {
        "文件" => 0,
        "编辑" => 10,
        "视图" => 20,
        "插入" => 30,
        "AI" => 40,
        "工具" => 60,
        "插件" => 900,
        "帮助" => 950,
        _ => 500
    };

    private void BuildToolStrip()
    {
        _toolStrip.Children.Clear();

        var tools = _runtime.Plugins.Tools.OrderBy(t => t.Order).ToList();
        foreach (var tool in tools)
        {
            var button = new Button
            {
                Content = Localized(tool.Title, tool.LocalizedTitleZh) ?? tool.Title,
                Classes = { "tool" }
            };
            if (!string.IsNullOrEmpty(tool.Shortcut))
            {
                ToolTip.SetTip(button, tool.Shortcut);
            }
            var toolId = tool.Id;
            var commandId = tool.CommandId;
            button.Click += (_, _) =>
            {
                _runtime.Tools.SetTool(toolId);
                _ = ExecuteCommandAsync(commandId, "tool");
                UpdateToolHighlight();
            };
            button.Tag = toolId;
            _toolStrip.Children.Add(button);
        }

        if (tools.Count > 0)
        {
            _toolStrip.Children.Add(new Border
            {
                Width = 1,
                Margin = new Avalonia.Thickness(6, 4),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF2C2C2E"))
            });
        }

        foreach (var panel in _runtime.Plugins.Panels.OrderBy(p => p.Order))
        {
            var button = new Button
            {
                Content = Localized(panel.Title, panel.LocalizedTitleZh) ?? panel.Title,
                Classes = { "tool" }
            };
            var panelId = panel.Id;
            var pluginId = _runtime.Plugins.OwnerOfCommand(panel.CommandId) ?? string.Empty;
            button.Click += (_, _) => OpenPanel(pluginId, panelId);
            _toolStrip.Children.Add(button);
        }

        _toolStrip.Children.Add(ZoomButton("−", () => ZoomAsync(1 / 1.25)));
        _toolStrip.Children.Add(ZoomButton("100%", () => ZoomAsync(0, reset: true)));
        _toolStrip.Children.Add(ZoomButton("+", () => ZoomAsync(1.25)));
        _toolStrip.Children.Add(ZoomButton("适应", ZoomToFitAsync));

        UpdateToolHighlight();
    }

    private Button ZoomButton(string label, Func<ValueTask> action)
    {
        var button = new Button { Content = label, Classes = { "tool" } };
        button.Click += (_, _) => _ = action();
        return button;
    }

    private void UpdateToolHighlight()
    {
        foreach (var child in _toolStrip.Children.OfType<Button>())
        {
            if (child.Tag is string toolId)
            {
                var active = string.Equals(toolId, _runtime.Tools.ActiveToolId, StringComparison.Ordinal);
                child.Classes.Set("active", active);
            }
        }
    }

    private void BuildShortcuts()
    {
        KeyBindings.Clear();

        foreach (var (gesture, commandId, trigger) in EnumerateShortcuts())
        {
            var parsed = TryParseGesture(gesture);
            if (parsed is null)
            {
                continue;
            }

            KeyBindings.Add(new KeyBinding
            {
                Gesture = parsed,
                Command = new RelayCommand(() => _ = ExecuteCommandAsync(commandId, trigger))
            });
        }
    }

    private IEnumerable<(string Gesture, string CommandId, string Trigger)> EnumerateShortcuts()
    {
        yield return ("Ctrl+N", ShellCommandPrefix + "file.new", "shortcut");
        yield return ("Ctrl+O", ShellCommandPrefix + "file.open", "shortcut");
        yield return ("Ctrl+S", ShellCommandPrefix + "file.save", "shortcut");
        yield return ("Ctrl+Shift+S", ShellCommandPrefix + "file.saveAs", "shortcut");
        yield return ("Ctrl+Z", ShellCommandPrefix + "edit.undo", "shortcut");
        yield return ("Ctrl+Y", ShellCommandPrefix + "edit.redo", "shortcut");
        yield return ("Ctrl+Shift+Z", ShellCommandPrefix + "edit.redo", "shortcut");
        yield return ("Ctrl+A", ShellCommandPrefix + "edit.selectAll", "shortcut");
        yield return ("Delete", ShellCommandPrefix + "edit.delete", "shortcut");
        yield return ("Ctrl+=", ShellCommandPrefix + "view.zoomIn", "shortcut");
        yield return ("Ctrl+OemPlus", ShellCommandPrefix + "view.zoomIn", "shortcut");
        yield return ("Ctrl+-", ShellCommandPrefix + "view.zoomOut", "shortcut");
        yield return ("Ctrl+0", ShellCommandPrefix + "view.fit", "shortcut");
        yield return ("Ctrl+G", ShellCommandPrefix + "view.toggleGrid", "shortcut");
        yield return ("Ctrl+Shift+P", ShellCommandPrefix + "plugins.manage", "shortcut");

        foreach (var shortcut in _runtime.Plugins.Shortcuts)
        {
            yield return (shortcut.Gesture, shortcut.CommandId, "shortcut");
        }
    }

    /// <summary>Meta is mapped to Ctrl so the same plugin gesture works on every platform.</summary>
    private static KeyGesture? TryParseGesture(string gesture)
    {
        var normalized = gesture.Replace("Meta+", "Ctrl+", StringComparison.OrdinalIgnoreCase);
        try
        {
            return KeyGesture.Parse(normalized);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Minimal ICommand so the shell can bind plugin commands to gestures.</summary>
internal sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
