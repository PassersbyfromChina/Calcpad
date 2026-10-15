# Calcpad

**版本 Version: 0.0.0**

一个「无限画布 + 插件驱动」的计算器，把 iPad 计算器的完整体验带到 **Android、Windows x64、Windows on Arm**。

An infinite-canvas, plugin-driven calculator that brings the iPad Calculator experience to
**Android, Windows x64 and Windows on Arm**.

关键词 / Keywords: `calculator` `infinite canvas` `plugin architecture` `handwriting recognition`
`OCR` `Chinese handwriting` `on-device AI` `ONNX` `Avalonia` `Android` `Windows Arm64` `Math Notes`
`unit converter` `permission model` `privacy` `计算器` `无限画布` `插件` `手写识别` `中英文` `本地 AI` `授权`

---

## 这是什么 / About

Calcpad 不是「一个计算器界面」，而是**一张无限画布**，画布上的一切都由插件提供：

- 计算器键盘、科学键盘、单位换算器、函数图形、计算历史 —— 全部是插件。
- 插件可以**读画布**、**访问数据库**、**修改画布**，并可通过**菜单栏**或**快捷键**启用。
- 所有这些能力都必须**经过用户授权**，而且是逐项、可撤销的。

画布本身是无限的：没有页面边界，可以一直平移、缩放，把计算过程、笔迹、文字、图形、AI 回答
摆在任意位置。这正是 iPad 计算器「数学笔记（Math Notes）」的形态，只是没有纸张边界。

内置 8 个插件，启动日志可见它们贡献的全部界面元素：

```
Plugin host refreshed: 8 active plugin(s), 33 menu item(s), 20 shortcut(s),
                       10 tool(s), 9 panel(s), 5 widget(s).
```

---

## 功能 / Features

### iPad 计算器功能（全部以插件实现）

| 功能 | 插件 | 说明 |
| --- | --- | --- |
| 基础计算器 | `Calcpad.Plugin.BasicCalculator` | 大圆角按键、橙色运算符、表达式与结果同屏、`%` `±` 退格、重复 `=` |
| 科学计算器 | `Calcpad.Plugin.ScientificCalculator` | `2nd` 切换、三角/反三角/双曲、`ln log log2`、`x^y x² x³`、`√ ∛ ⁿ√`、`x!`、`π e`、`EE`、`Deg/Rad/Grad` |
| 单位换算 | `Calcpad.Plugin.UnitConverter` | 14 个类别（长度/质量/温度/面积/体积/速度/时间/能量/功率/压强/数据/角度…）、376 个单位名与符号、中文单位名（`3米转厘米`）、双向实时换算 |
| 数学笔记 | `Calcpad.Plugin.MathNotes` | 多行实时求值、变量跨行共享、`y=f(x)` 图形绘制（可缩放平移、坐标读数、结果回填） |
| 计算历史 | `Calcpad.Plugin.History` | 持久化到本地数据库、可搜索、一键回插画布 |
| 画布工具 | `Calcpad.Plugin.CanvasTools` | 钢笔、荧光笔、橡皮、套索选择、矩形选择、文本、形状（8 个工具） |

数学引擎为手写词法 + 递归下降语法分析，不是 `DataTable.Compute` 之类的取巧实现：
47 个函数、单位代数（`3 m + 20 cm = 3.2 m`、`2 kg * 3 = 6 kg`、`1 m / 1 s = 1 m/s`）、
逐步归约轨迹（Steps）、阶乘/幂次/递归深度上限保护。

### 手写转文字（`Calcpad.Plugin.Handwriting`）

- **中英文混排**，本地优先：
  - 内置**完全离线**的笔迹识别器（位置+方向+曲率的 DTW、栅格 chamfer、墨水投影剖面、27 维特征向量
    四通道融合），全程不经网络。
  - 可选加载用户自备的本地 ONNX 模型（放在数据目录的 `models/` 下），识别在**本机**完成；
    模型缺失或损坏时**优雅降级**并明确说明原因。
- **清晰就直识别**：置信度 ≥ 0.85 直接采用。
- **不清晰就猜，但说清楚是猜的**：置信度 0.60–0.85 采用并标注。
- **概率相近就让用户选**：置信度 < 0.60，或前两名差距 ≤ 0.12，**停下来让用户选候选**，绝不擅自决定。
- **纠错**：语法合理性校验（结合数学引擎）、中英文词典、常见视觉混淆对
  （`0/O`、`1/l/I/|`、`5/S`、`2/Z`、`rn/m`、`vv/w` …），每条修改都给出 `CorrectionNote` 说明原因。
- **范围可选**：整个画布转文字，或**圈选一部分**转文字，界面会明确显示当前范围。

### 圈画任何内容问 AI（`Calcpad.Plugin.AskAi`）

- 用套索圈住画布上的任意内容 → 浮出「圈画问 AI」→ **预览将要发送的确切内容**
  （文本原文 + 图片缩略图 + 字节数 + 目标服务与模型）→ 确认 → 回答以卡片落回画布。
- 支持流式输出、追问、重新生成、插入为新文本、删除。
- 全画布模式默认只列出**纯文本**摘要供你逐条勾选，**完全不调用光栅化**。

---

## 隐私与授权模型 / Privacy & Authorization

这是本项目的核心设计，不是附加项。

**1. 能力制授权（capability based）**

插件默认什么都做不了。它必须在清单里声明所需能力，用户在首次使用时逐项决定：

`CanvasRead` `CanvasWrite` `DatabaseRead` `DatabaseWrite` `ClipboardRead/Write` `FileRead/Write`
`Network` `AiLocal` `AiCloud` `Camera` `Microphone` `Notifications` `ShellIntegration`

授权在**每一次 API 调用时**重新校验，而不是只在加载时检查一次。未授权的调用直接抛
`PermissionDeniedException`（错误信息里带上插件 id、能力与具体 API 名）。
用户可以随时在「插件 → 插件管理器」里撤销任何一项。

**2. 云上传的严格控制**

- 全局开关**默认关闭**：关闭时任何插件都无法把内容送出这台设备。
- 开启后仍然**逐次确认**：每次上传前弹出对话框，列出「发起插件 / 用途 / 目标服务 / 模型 /
  总字符数 / 总字节数」，并**逐条展示**文本原文与图片说明。
- 用户可以选择**「仅发送文字」**，此时图片一律被剔除。
- **禁止整画布外传**：圈选只发圈选内容；全画布模式只发用户逐条勾选的文本摘要，
  每次上传的裁剪区域都**不等于**整画布包围盒。
- 超过大小上限**在询问用户之前**就直接拒绝。
- 所有上传（无论允许还是拒绝）都写入本地上传审计表，用户可在「插件 → 上传审计」查看。

**3. 本地优先**

手写识别与问答都优先走本机路径；`AiCloud` 是可选增强，不是默认依赖。
本地端点可用时**不触发任何上传确认**。

**4. API key 不落明文**

云供应商的 API key 在 Windows 上用 **DPAPI（CurrentUser）** 加密，其它平台用 AES-256-GCM +
机器/用户绑定派生密钥；保护失败时**拒绝落盘**并显式标记 `KeyProtected=false`。

---

## 架构 / Architecture

```
┌───────────────────────────────────────────────────────────────┐
│ Calcpad.Desktop (win-x64 / win-arm64)   Calcpad.Android (apk) │  ← 平台头
├───────────────────────────────────────────────────────────────┤
│ Calcpad.App          Avalonia 外壳：菜单栏/快捷键/面板/对话框  │
│                      + 组合根 CalcpadComposition               │
├───────────────────────────────────────────────────────────────┤
│ Calcpad.Plugin.Sdk   插件 UI 契约：画布 widget、面板、主题      │
├───────────────────────────────────────────────────────────────┤
│ Calcpad.Core                                                   │
│   Canvas      无限画布模型 + 引擎（相机/命中/套索/撤销/序列化）│
│   Numerics    表达式引擎与单位换算                             │
│   Ai          识别流水线与 AI 服务（本地优先 + 受控云）        │
│   Security    权限代理、上传闸门、密钥保护                     │
│   Data        SQLite：按插件分区、授权、设置、审计             │
│   Hosting     插件宿主、上下文工厂、程序集加载                 │
│   Plugins     清单、贡献点（菜单/快捷键/工具/面板/widget）      │
├───────────────────────────────────────────────────────────────┤
│ plugins/       内置插件（每个都是普通的 IPlugin 实现）          │
└───────────────────────────────────────────────────────────────┘
```

插件的一切都是**贡献点（contribution）**：在 `PluginManifest` 里声明菜单项、快捷键、工具、
面板、widget 类型，然后实现命令处理。外壳不硬编码任何功能界面——**禁用插件，它的界面就消失**。

### 目录结构

```
Calcpad/
├─ src/
│  ├─ Calcpad.Core/          画布、数学、AI、安全、数据、插件宿主（无 UI 依赖）
│  ├─ Calcpad.Plugin.Sdk/    写插件用的 SDK（Avalonia 契约）
│  ├─ Calcpad.App/           Avalonia 外壳与组合根
│  ├─ Calcpad.Desktop/       Windows x64 / Windows on Arm 头
│  └─ Calcpad.Android/       Android 头
├─ plugins/                  内置插件源码
├─ build/                    构建脚本
└─ .github/workflows/        CI
```

---

## 构建 / Building

需要 **.NET 9 SDK**。Android 还需要 **Android SDK（platform 35）** 与 **JDK 17**。

```powershell
# 一条命令构建三平台（产物在 artifacts/）
./build/build.ps1

# Windows x64（单文件）
dotnet publish src/Calcpad.Desktop/Calcpad.Desktop.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true

# Windows on Arm（单文件）
dotnet publish src/Calcpad.Desktop/Calcpad.Desktop.csproj -c Release -r win-arm64 `
  --self-contained false -p:PublishSingleFile=true

# Android APK
dotnet build src/Calcpad.Android/Calcpad.Android.csproj -c Release -f net9.0-android `
  -p:AndroidSdkDirectory=<android-sdk> -p:JavaSdkDirectory=<jdk17>

# 整个解决方案
dotnet build Calcpad.sln -c Release
```

桌面端默认 `PublishSingleFile=true` + `IncludeNativeLibrariesForSelfExtract=true`：
托管程序集与原生库都打进单个 exe，既便于分发，也能在有应用程序控制策略
（Smart App Control）的机器上正常加载。

运行时数据默认放在 `%APPDATA%\Calcpad`，可用 `--data-dir <dir>` 覆盖。

---

## 写一个插件 / Writing a plugin

```csharp
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;

public sealed class MyPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "example.hello",
        Name = "Hello",
        Permissions = [PermissionKind.CanvasRead, PermissionKind.CanvasWrite, PermissionKind.ShellIntegration],
        Menus = [new MenuContribution("Insert/Hello", "打个招呼", "hello.greet", Shortcut: "Ctrl+Alt+H")],
        Shortcuts = [new ShortcutContribution("Ctrl+Alt+H", "hello.greet", "打个招呼")]
    };

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        _context = context;
        return ValueTask.CompletedTask;
    }

    public async ValueTask ExecuteCommandAsync(CommandContext context, CancellationToken ct = default)
    {
        if (context.CommandId != "hello.greet") return;

        // 未授权这里会抛 PermissionDeniedException。
        await _context.Canvas.AddAsync(new Calcpad.Core.Canvas.TextObject
        {
            Text = "你好，画布！",
            Bounds = new Calcpad.Core.Canvas.CRect(0, 0, 200, 40)
        }, ct);
    }

    public ValueTask ShutdownAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
}
```

插件被禁用时，它的菜单项、快捷键、面板与画布 widget 会**立即全部消失**。
外部插件也可以从数据目录的 `plugins/` 子目录加载（桌面端，可回收 `AssemblyLoadContext`，
宿主编译的程序集不会被重复加载）。

---

## 验证与已知限制 / Verification & Known limits

开发过程中所有验证都在仓库之外执行，结论来自**真实运行输出**：

| 范围 | 结果 |
| --- | --- |
| 画布引擎（相机/序列化/套索/撤销/权限/空间索引/Headless 视图/光栅化） | 260 项断言全部通过 |
| 数学引擎与 5 个计算器插件 | 328 项断言全部通过 |
| 手写识别与 AI（识别准确率、分级、纠错、egress 拒绝路径、上传裁剪、ONNX 降级） | 211 项断言全部通过 |
| 宿主运行时（授权拦截、分区隔离、审计、插件故障隔离、密钥加密） | 138 项断言通过 / 0 失败 |

**诚实的限制**：

- 手写识别的准确率数字（合成笔迹 top-1 95.5%、top-3 100.0%，n=156）来自**程序合成的笔迹**，
  不等于真人手写基准。残余 top-1 混淆为 `3→9`、`4→+`（top-3 均可兜住，由候选 UI 承接）。
- **中文手写准确率未测**：内置 CJK 通道只有 44 个标签（汉字数字/单位/常用部件），
  中文的实际路线是用户自备 ONNX 模型。
- **Android 未做真机运行验证**：APK 构建通过，`libe_sqlite3.so` 已打包进 APK，
  但 SQLite 与 UI 只做了静态确认。
- Android 的密钥保护走 AES-256-GCM + 机器绑定派生密钥（`ISecretKeyProvider` 已留接口，
  可换成 Android Keystore）。
- 剪贴板尚未暴露宿主 API，AI 回答的「复制」目前实现为「插入为新文本」。

---

## 版本 / Version

`0.0.0` —— 见 `Directory.Build.props`。首次发布前不递增。

## 许可 / License

[MIT](LICENSE) © 2026 PassersbyfromChina
