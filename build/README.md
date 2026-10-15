# Calcpad 构建说明

一条命令构建 Calcpad 的全部发布目标。产物统一放在仓库根的 `artifacts/`（已在 `.gitignore` 中忽略）。

## 快速开始

```powershell
# 三平台：Windows x64、Windows on Arm 发布 + Android APK
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\build.ps1
```

成功时退出码为 0，失败为 1；每个步骤的完整日志写入 `artifacts\logs\`，汇总表写入 `artifacts\build-summary.txt`。

## 参数

| 参数 | 说明 |
| --- | --- |
| `-Targets` | `win-x64`、`win-arm64`、`linux-x64`、`osx-arm64`、`android` 的任意组合。默认 `win-x64,win-arm64,android`。 |
| `-Configuration` | 构建配置，默认 `Release`。 |
| `-SelfContained` | 生成自包含发布（默认框架依赖）。 |
| `-OutputDirectory` | 覆盖 `artifacts/` 位置。 |
| `-Clean` | 构建前清空产物目录。 |

示例：

```powershell
# 只出 Windows x64 + Android
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\build.ps1 -Targets win-x64,android

# 自包含的 Linux x64 发布
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\build.ps1 -Targets linux-x64 -SelfContained
```

## 产物

| 目标 | 路径 | 说明 |
| --- | --- | --- |
| Windows x64 | `artifacts\win-x64\Calcpad.exe` | `Calcpad.Desktop` 单文件发布结果（托管与原生库全部内嵌） |
| Windows on Arm | `artifacts\win-arm64\Calcpad.exe` | 同上，arm64 RID |
| Android | `artifacts\android\Calcpad-<version>-<apk>.apk` | `AndroidPackageFormat=apk`，debug keystore 签名 |
| 日志 | `artifacts\logs\*.log` | 每个目标的完整构建输出 |

版本号从 `Directory.Build.props` 的 `<Version>` 读取（当前为 `0.0.0`），脚本不硬编任何版本。

### 为什么桌面端默认单文件发布

Windows 的 Smart App Control（应用程序控制策略）会拦截**从磁盘加载的未签名托管 DLL**（`FileLoadException` / CodeIntegrity 事件 3077）。`src\Calcpad.Desktop\Calcpad.Desktop.csproj` 因此默认启用：

```xml
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>false</SelfContained>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
<EnableCompressionInSingleFile>false</EnableCompressionInSingleFile>
```

`IncludeNativeLibrariesForSelfExtract=true` 让 `e_sqlite3.dll`、`onnxruntime.dll`、`libSkiaSharp.dll` 等原生库也进入 exe，运行时解压到 `%TEMP%\.net\<app>\...`，从而绕开对磁盘原生 DLL 的拦截。`build.ps1` 的桌面目标同样传 `-p:PublishSingleFile=true`，命令行加 `-p:PublishSingleFile=false` 可以关掉。

实测（单文件 exe）：`Calcpad.exe --data-dir <dir>` 能正常启动，`<dir>` 下生成 `calcpad.db`、`calcpad.db-wal`，日志写入 `<dir>\logs\calcpad.log`，5 个内置插件全部加载。

> 框架依赖发布（`--self-contained false`）需要目标机器安装 .NET 9 运行时；需要免安装时用 `-SelfContained`。

## 工具链要求

- **.NET 9 SDK**（`global.json` 已固定 9.0.x）。脚本按以下顺序查找 `dotnet`：`$env:CALCPAD_DOTNET` → `PATH` → 仓库同级 `_toolchain\dotnet\dotnet.exe`。
- **Android**：`net9.0-android` 需要 Android workload、Android SDK（platform-tools / platforms;android-35 / build-tools;35.0.0）和 JDK 17。
  - SDK 查找顺序：`$env:ANDROID_HOME` → `$env:ANDROID_SDK_ROOT` → 仓库同级 `_toolchain\android-sdk`。
  - JDK 查找顺序：`$env:JAVA_HOME` → 仓库同级 `_toolchain\jdk17`。
  - 脚本会把 `AndroidSdkDirectory` / `JavaSdkDirectory` 显式传给 MSBuild。
  - 缺少 SDK 或 JDK 时 Android 目标标记为 `SKIPPED` 并让脚本以非 0 退出，避免"静默成功"。

安装 workload 与依赖（一次即可）：

```powershell
dotnet workload install android
```

## 手工命令（不使用脚本时）

```powershell
dotnet publish src\Calcpad.Desktop\Calcpad.Desktop.csproj -c Release -r win-x64  --self-contained false -p:PublishSingleFile=true
dotnet publish src\Calcpad.Desktop\Calcpad.Desktop.csproj -c Release -r win-arm64 --self-contained false -p:PublishSingleFile=true

$env:JAVA_HOME     = "<jdk17>"
$env:ANDROID_HOME  = "<android-sdk>"
dotnet build src\Calcpad.Android\Calcpad.Android.csproj -c Release -f net9.0-android `
  -p:AndroidSdkDirectory="$env:ANDROID_HOME" -p:JavaSdkDirectory="$env:JAVA_HOME"
```

APK 产出位置：`src\Calcpad.Android\bin\Release\net9.0-android\*.apk`（`-Signed.apk` 为已签名版本）。

## CI

`.github/workflows/build.yml` 在 push / PR 时构建：

- `windows` 矩阵：`win-x64`、`win-arm64` 发布 + Android APK（安装 android workload）。
- `linux`：`linux-x64` 桌面头构建。

CI 只构建、不跑测试：仓库按设计不包含测试工程（`.gitignore` 忽略 `tests/`），可执行验证放在仓库外的独立工程中。
