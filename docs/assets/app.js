/* ==========================================================================
   Calcpad · 下载落地页脚本
   零依赖 IIFE，不污染全局。功能：
   1. 平台识别 → 主下载按钮
   2. GitHub Releases 版本/体积动态获取（失败静默降级）
   3. 亮色 / 暗色主题
   4. 中英双语切换
   5. 滚动进入动画
   ========================================================================== */
'use strict';

(function () {
  var root = document.documentElement;

  /* ------------------------------------------------------------------ *
   * 静态兜底值（GitHub API 失败时继续使用）
   * ------------------------------------------------------------------ */
  var FALLBACK_VERSION = '0.0.0';
  var FALLBACK_SIZES = {
    'win-x64': '45.9 MB',
    'win-arm64': '44.4 MB',
    'android': '57.9 MB'
  };
  var RELEASE_API = 'https://api.github.com/repos/PassersbyfromChina/Calcpad/releases/latest';
  var RELEASES_PAGE = 'https://github.com/PassersbyfromChina/Calcpad/releases';
  var DOWNLOAD_BASE = RELEASES_PAGE + '/latest/download/';
  var FETCH_TIMEOUT_MS = 5000;
  var THEME_KEY = 'calcpad.theme';
  var LANG_KEY = 'calcpad.lang';
  var ASSET_KEYS = ['win-x64', 'win-arm64', 'android'];

  /* ------------------------------------------------------------------ *
   * 文案字典
   * ------------------------------------------------------------------ */
  var STRINGS = {
    zh: {
      lang: 'zh-CN',
      title: 'Calcpad · 无限画布计算器 · 下载',
      description: 'Calcpad 把 iPad 计算器的完整体验带到 Android 与 Windows 的无限画布上：一切功能都是插件。手写转文字完全离线，云上传默认关闭，无遥测。',
      themeColor: '#faf8f5',
      strings: {
        skip: '跳到主要内容',
        navFeatures: '功能',
        navPrivacy: '隐私',
        navDownload: '下载',
        navGithub: 'GitHub',
        badgeNote: '无限画布 · 插件驱动',
        heroTitleA: '一张无限画布，',
        heroTitleB: '装下整个 iPad 计算器。',
        heroSub: 'Calcpad 把 iPad 计算器的完整体验带到 Android 与 Windows：没有页面边界，可以无限平移缩放；基础计算器、科学计算器、单位换算、数学笔记、手写识别……一切功能都是插件。',
        otherPlatforms: '其他平台',
        primaryPending: '正在识别你的系统…',
        detecting: '正在识别你的系统…',
        winReq: 'Windows 版是框架依赖的单文件程序，需要安装 .NET 9 Desktop Runtime 才能运行。<a href="https://dotnet.microsoft.com/download/dotnet/9.0" rel="noopener">下载官方运行时</a>',
        trustCapability: '能力制授权',
        trustCloud: '云上传默认关闭',
        trustTelemetry: '无遥测',
        trustMIT: 'MIT 开源',

        featuresTitle: '重点功能',
        featuresLead: '外壳不硬编码任何功能界面：键盘、单位换算、图形、历史都由插件贡献。禁用某个插件，它的界面就立刻消失。',
        f1t: '无限画布',
        f1d: '没有页面边界：计算过程、笔迹、文字、图形、AI 回答都可以摆在画布上的任意位置，无限平移缩放，需要多大就有多大。',
        f2t: '插件驱动',
        f2d: '内置 8 个插件，贡献菜单、快捷键、工具与面板。插件可以读画布、访问数据库、修改画布，都要先拿到你的授权。',
        f3t: 'iPad 计算器全套',
        f3d: '基础计算器与科学计算器（2nd 切换、三角/双曲、阶乘、EE、Deg/Rad/Grad）都在，表达式与结果同屏，橙色运算符、大圆角按键。',
        f4t: '单位换算',
        f4d: '14 个类别、376 个单位；支持中文单位名，直接写「3米转厘米」就能算，双向实时换算。',
        f5t: '数学笔记',
        f5d: '逐行实时求值，变量跨行共享；写 y=f(x) 就直接画出函数图，可缩放平移、读取坐标、把结果回填到画布。',
        f6t: '手写转文字',
        f6d: '完全离线的笔迹识别器，本地优先；可选加载你自备的 ONNX 模型。清晰就直接识别，不清晰就标注，概率相近就停下来让你选。',
        f7t: '圈画问 AI',
        f7d: '用套索圈住画布上的任意内容，先预览将要发送的确切内容（文本原文、图片缩略图、字节数、目标服务与模型），确认后才发出去。',
        f8t: '计算历史',
        f8d: '历史持久化到本地数据库，可搜索、可一键回插画布，接着上次的推导继续算。',

        privacyTitle: '隐私与授权',
        privacyLead: '这是本项目的核心设计，不是附加项。插件默认什么都做不了，能做什么由你逐项决定，而且随时可以收回。',
        p1t: '能力制授权，逐次校验',
        p1d: '插件必须在清单里声明所需能力（画布读写、数据库、网络、AI、摄像头等），授权在每一次 API 调用时重新校验，而不是只在加载时检查一次。未授权直接抛 PermissionDeniedException，你可以在插件管理器里随时撤销任何一项。',
        p2t: '云上传默认关闭',
        p2d: '全局开关默认关闭，此时任何插件都无法把内容送出这台设备。开启后依然逐次确认：对话框里列出发起插件、用途、目标服务、模型、字符数与字节数，并逐条展示文本原文。你可以选择「仅发送文字」，图片一律剔除。禁止整画布外传，超过大小上限在询问之前就直接拒绝。',
        p3t: '本地审计，密钥不落明文',
        p3d: '所有上传（无论允许还是拒绝）都写入本地上传审计表，你可以在「插件 → 上传审计」里查看。云供应商的 API key 用 DPAPI（Windows）或 AES-256-GCM 加密，保护失败时拒绝落盘并显式标记。没有遥测：这个页面除了读取版本号，不会向任何地方发送数据。',
        localFirstStrong: '本地优先：',
        localFirstRest: '手写识别与问答都先走本机路径，云端是可选增强而不是默认依赖；本地端点可用时不会触发任何上传确认。',

        downloadTitle: '下载',
        downloadLead: '当前只发布 Windows 与 Android 版本，没有 macOS、iOS 或 Linux 安装包。数字签名与校验信息见版本页。',
        recTag: '推荐',
        platWinX64: 'Windows（x64）',
        platWinX64Note: '适用于绝大多数 Intel / AMD 处理器的 Windows 10 与 Windows 11 电脑。',
        platWinArm: 'Windows on Arm',
        platWinArmNote: '适用于骁龙 X 系列等 Arm64 设备，例如 Surface Pro 与新款轻薄本。',
        platAndroid: 'Android',
        platAndroidNote: 'APK 安装包，支持 Android 7.0 及以上；同时包含 arm64-v8a 与 x86_64。',
        platWinKind: '框架依赖单文件',
        platApkKind: 'APK · minSdk 24',
        winReqShort: '需要 .NET 9 Desktop Runtime',
        winReqLink: '官方下载',
        apkReq: '安装时系统可能提示「未知来源」',
        downloadBtn: '下载',
        noticeStrong: 'Windows 用户请注意：',
        noticeRest: '两个 Windows 安装包都是框架依赖的单文件程序，第一次运行前需要安装 .NET 9 Desktop Runtime，否则会提示缺少运行时。也可以先装运行时再运行程序，两者顺序任意。<a href="https://dotnet.microsoft.com/download/dotnet/9.0" rel="noopener">前往微软官方下载 .NET 9 Desktop Runtime</a>',
        allReleases: '查看全部版本页',
        footerLicense: 'MIT 许可',
        footerReleases: '版本页',

        /* 运行时生成 */
        primaryWindows: '下载 Windows 版（{arch}）· {size}',
        primaryAndroid: '下载 Android 版（APK）· {size}',
        seeAll: '查看全部下载',
        detected: '已识别你的系统：{os} · {arch}',
        detectedUnknown: '已识别你的系统：{os}',
        noPackage: '当前只发布 Windows 与 Android 版本，你的系统暂时没有对应安装包。',
        downloading: '正在下载 {name}（{size}）…',
        releaseFailed: '下载没有开始。请到版本页手动选择安装包。',
        recommended: '推荐下载 · {size}',
        releasedOn: '发布于 {date}'
      }
    },
    en: {
      lang: 'en',
      title: 'Calcpad · Infinite-canvas calculator · Download',
      description: 'Calcpad brings the full iPad Calculator experience to an infinite canvas on Android and Windows, where every feature is a plugin. Handwriting recognition runs fully offline, cloud upload is off by default, and there is no telemetry.',
      themeColor: '#14120f',
      strings: {
        skip: 'Skip to main content',
        navFeatures: 'Features',
        navPrivacy: 'Privacy',
        navDownload: 'Download',
        navGithub: 'GitHub',
        badgeNote: 'Infinite canvas · Plugin driven',
        heroTitleA: 'One infinite canvas,',
        heroTitleB: 'the whole iPad Calculator on it.',
        heroSub: 'Calcpad brings the complete iPad Calculator experience to Android and Windows: no page boundaries, unlimited panning and zooming. Basic calculator, scientific calculator, unit conversion, math notes, handwriting recognition — every feature is a plugin.',
        otherPlatforms: 'Other platforms',
        primaryPending: 'Detecting your system…',
        detecting: 'Detecting your system…',
        winReq: 'The Windows build is a framework-dependent single file and needs the .NET 9 Desktop Runtime to run. <a href="https://dotnet.microsoft.com/download/dotnet/9.0" rel="noopener">Get the official runtime</a>',
        trustCapability: 'Capability-based permissions',
        trustCloud: 'Cloud upload off by default',
        trustTelemetry: 'No telemetry',
        trustMIT: 'MIT licensed',

        featuresTitle: 'Highlights',
        featuresLead: 'The shell hard-codes no feature UI: keypads, unit conversion, plots and history are all contributed by plugins. Disable a plugin and its interface disappears immediately.',
        f1t: 'Infinite canvas',
        f1d: 'No page boundaries: calculations, ink, text, shapes and AI answers can sit anywhere on the canvas, with unlimited panning and zooming — as large as you need.',
        f2t: 'Plugin driven',
        f2d: 'Eight built-in plugins contribute menus, shortcuts, tools and panels. Plugins can read the canvas, reach the database and modify the canvas — but only with the permissions you grant.',
        f3t: 'The full iPad Calculator set',
        f3d: 'Basic and scientific calculators (2nd toggle, trig and hyperbolic, factorial, EE, Deg/Rad/Grad), expressions and results on screen together, orange operators and large rounded keys.',
        f4t: 'Unit conversion',
        f4d: '14 categories and 376 units, with Chinese unit names supported — type “3米转厘米” and it just works, converting both ways in real time.',
        f5t: 'Math notes',
        f5d: 'Line-by-line live evaluation with variables shared across lines; write y=f(x) to plot it, then zoom, pan, read coordinates and push results back to the canvas.',
        f6t: 'Handwriting to text',
        f6d: 'A fully offline ink recogniser, local first, with optional user-supplied ONNX models. Clear ink is accepted outright, uncertain ink is labelled as a guess, and close calls stop and let you choose.',
        f7t: 'Lasso anything, ask AI',
        f7d: 'Lasso any content on the canvas, preview exactly what would be sent (raw text, image thumbnails, byte count, target service and model), and only then confirm.',
        f8t: 'Calculation history',
        f8d: 'History is stored in a local database: searchable, insertable back onto the canvas in one click, so you can continue where you left off.',

        privacyTitle: 'Privacy and permissions',
        privacyLead: 'This is the core of the project, not an add-on. A plugin can do nothing by default; you decide what it may do, item by item, and you can take it back at any time.',
        p1t: 'Capability permissions, re-checked every call',
        p1d: 'A plugin must declare the capabilities it needs (canvas read/write, database, network, AI, camera and more). Permission is re-validated on every API call, not once at load time. An unauthorised call throws PermissionDeniedException, and you can revoke any single capability in the plugin manager.',
        p2t: 'Cloud upload off by default',
        p2d: 'The global switch is off by default, and while it is off no plugin can send anything off this device. Once enabled, every upload is still confirmed one by one: the dialog lists the initiating plugin, purpose, target service, model, character count and byte count, and shows the raw text item by item. You can choose text only, which strips images entirely. Sending the whole canvas is forbidden, and anything over the size limit is rejected before you are even asked.',
        p3t: 'Local audit trail, keys never in plaintext',
        p3d: 'Every upload, allowed or refused, is written to a local upload audit table you can read under Plugins → Upload audit. Cloud API keys are encrypted with DPAPI on Windows or AES-256-GCM elsewhere, and if protection fails the key is refused and explicitly flagged. There is no telemetry: apart from reading the release version, this page sends nothing anywhere.',
        localFirstStrong: 'Local first: ',
        localFirstRest: 'Handwriting recognition and Q&A go through on-device paths first; the cloud is an optional enhancement, not a default dependency. When a local endpoint is available, no upload confirmation is triggered at all.',

        downloadTitle: 'Download',
        downloadLead: 'Only Windows and Android are published right now; there is no macOS, iOS or Linux package. See the releases page for signatures and checksums.',
        recTag: 'Recommended',
        platWinX64: 'Windows (x64)',
        platWinX64Note: 'For the vast majority of Intel and AMD Windows 10 and Windows 11 PCs.',
        platWinArm: 'Windows on Arm',
        platWinArmNote: 'For Arm64 devices such as Snapdragon X series laptops and Surface Pro.',
        platAndroid: 'Android',
        platAndroidNote: 'APK package, Android 7.0 and later, bundling both arm64-v8a and x86_64.',
        platWinKind: 'Framework-dependent single file',
        platApkKind: 'APK · minSdk 24',
        winReqShort: 'Requires the .NET 9 Desktop Runtime',
        winReqLink: 'Official download',
        apkReq: 'Android may warn about an unknown source',
        downloadBtn: 'Download',
        noticeStrong: 'Windows users, please note: ',
        noticeRest: 'Both Windows packages are framework-dependent single files, so the .NET 9 Desktop Runtime must be installed before the first run or the app will report a missing runtime. Either order works: install the runtime first, or run the app first and install it after. <a href="https://dotnet.microsoft.com/download/dotnet/9.0" rel="noopener">Download the .NET 9 Desktop Runtime from Microsoft</a>',
        allReleases: 'See all releases',
        footerLicense: 'MIT License',
        footerReleases: 'Releases',

        /* 运行时生成 */
        primaryWindows: 'Download for Windows ({arch}) · {size}',
        primaryAndroid: 'Download for Android (APK) · {size}',
        seeAll: 'See all downloads',
        detected: 'Detected your system: {os} · {arch}',
        detectedUnknown: 'Detected your system: {os}',
        noPackage: 'Only Windows and Android builds are published, so there is no package for your system yet.',
        downloading: 'Downloading {name} ({size})…',
        releaseFailed: 'The download did not start. Please pick a package on the releases page.',
        recommended: 'Recommended · {size}',
        releasedOn: 'Released {date}'
      }
    }
  };

  /* ------------------------------------------------------------------ *
   * 平台定义
   * ------------------------------------------------------------------ */
  var PLATFORMS = {
    'win-x64': {
      file: 'Calcpad-win-x64.exe',
      os: { zh: 'Windows', en: 'Windows' },
      arch: { zh: 'x64', en: 'x64' }
    },
    'win-arm64': {
      file: 'Calcpad-win-arm64.exe',
      os: { zh: 'Windows', en: 'Windows' },
      arch: { zh: 'Arm64', en: 'Arm64' }
    },
    'android': {
      file: 'Calcpad-android.apk',
      os: { zh: 'Android', en: 'Android' },
      arch: { zh: 'Arm64 / x86_64', en: 'Arm64 / x86_64' }
    }
  };

  /* ------------------------------------------------------------------ *
   * 小工具
   * ------------------------------------------------------------------ */
  function $(sel, scope) { return (scope || document).querySelector(sel); }
  function $$(sel, scope) { return Array.prototype.slice.call((scope || document).querySelectorAll(sel)); }

  function format(tpl, vars) {
    if (!tpl) return '';
    return tpl.replace(/\{(\w+)\}/g, function (m, key) {
      return Object.prototype.hasOwnProperty.call(vars, key) ? String(vars[key]) : m;
    });
  }

  function safeGet(key) {
    try { return window.localStorage.getItem(key); } catch (e) { return null; }
  }

  function safeSet(key, value) {
    try { window.localStorage.setItem(key, value); } catch (e) { /* 私密模式等，忽略 */ }
  }

  function formatBytes(bytes) {
    if (typeof bytes !== 'number' || !isFinite(bytes) || bytes <= 0) return '';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }

  /* ------------------------------------------------------------------ *
   * 状态
   * ------------------------------------------------------------------ */
  var state = {
    lang: 'zh',
    detected: { key: null, os: { zh: '未知系统', en: 'Unknown system' }, arch: null },
    sizes: {
      'win-x64': FALLBACK_SIZES['win-x64'],
      'win-arm64': FALLBACK_SIZES['win-arm64'],
      'android': FALLBACK_SIZES['android']
    },
    version: FALLBACK_VERSION
  };

  function t(key) {
    var table = STRINGS[state.lang].strings;
    if (Object.prototype.hasOwnProperty.call(table, key)) return table[key];
    var fallbackTable = STRINGS.zh.strings;
    return Object.prototype.hasOwnProperty.call(fallbackTable, key) ? fallbackTable[key] : '';
  }

  /* ------------------------------------------------------------------ *
   * 平台识别
   * ------------------------------------------------------------------ */
  function normalizeArch(raw) {
    var value = String(raw || '').toLowerCase();
    if (!value) return null;
    if (value.indexOf('arm') !== -1 || value.indexOf('aarch64') !== -1) return 'arm64';
    if (value.indexOf('x86_64') !== -1 || value.indexOf('x86-64') !== -1 || value.indexOf('amd64') !== -1) return 'x64';
    if (value.indexOf('x64') !== -1 || value.indexOf('x86') !== -1) return 'x64';
    return null;
  }

  function archLabel(arch) {
    if (arch === 'arm64') return { zh: 'Arm64', en: 'Arm64' };
    if (arch === 'x64') return { zh: 'x64', en: 'x64' };
    return null;
  }

  function detectFromUa() {
    var ua = String(navigator.userAgent || '');
    var plat = String(navigator.platform || '') + ' ' + String(navigator.userAgentData && navigator.userAgentData.platform || '');
    var lower = ua.toLowerCase();

    if (/android/i.test(ua)) {
      return { key: 'android', os: { zh: 'Android', en: 'Android' }, arch: null };
    }
    if (/windows|win32|win64/i.test(plat) || /windows nt/i.test(ua)) {
      var arch = normalizeArch(ua);
      if (!arch && /wow64/i.test(ua)) arch = 'x64';
      return { key: arch === 'arm64' ? 'win-arm64' : 'win-x64', os: { zh: 'Windows', en: 'Windows' }, arch: arch || 'x64' };
    }
    if (/iphone|ipad|ipod/i.test(ua) || /mac/i.test(plat) || /mac os x/i.test(lower)) {
      var isIos = /iphone|ipad|ipod/i.test(ua);
      return { key: null, os: { zh: isIos ? 'iOS' : 'macOS', en: isIos ? 'iOS' : 'macOS' }, arch: null };
    }
    if (/linux|x11/i.test(plat) || /linux/i.test(lower)) {
      return { key: null, os: { zh: 'Linux', en: 'Linux' }, arch: null };
    }
    return { key: null, os: { zh: '未知系统', en: 'Unknown system' }, arch: null };
  }

  function finalizeDetection(raw) {
    var base = detectFromUa();
    var os = raw.os || base.os;
    var arch = raw.arch || base.arch;
    var key = raw.key;

    if (!key && os && os.zh === 'Windows') {
      key = arch === 'arm64' ? 'win-arm64' : 'win-x64';
      if (!arch) arch = 'x64';
    }
    if (!key && os && os.zh === 'Android') key = 'android';
    return { key: key || null, os: os, arch: arch || null };
  }

  function detectPlatform() {
    var fallback = detectFromUa();

    if (!navigator.userAgentData || typeof navigator.userAgentData.getHighEntropyValues !== 'function') {
      return Promise.resolve(fallback);
    }

    var pending;
    try {
      pending = navigator.userAgentData
        .getHighEntropyValues(['architecture', 'bitness', 'platform', 'platformVersion']);
    } catch (e) {
      return Promise.resolve(fallback); /* 同步抛出也要能降级 */
    }

    return Promise.resolve(pending)
      .then(function (high) {
        var platform = String(high.platform || navigator.userAgentData.platform || '');
        var os = null;
        var key = null;

        if (/android/i.test(platform) || /android/i.test(String(navigator.userAgent))) {
          os = { zh: 'Android', en: 'Android' };
          key = 'android';
        } else if (/windows/i.test(platform)) {
          os = { zh: 'Windows', en: 'Windows' };
        } else if (/macos|mac os/i.test(platform)) {
          os = { zh: 'macOS', en: 'macOS' };
        } else if (/ios|ipados/i.test(platform)) {
          os = { zh: 'iOS', en: 'iOS' };
        } else if (/chrome os|cros/i.test(platform)) {
          os = { zh: 'ChromeOS', en: 'ChromeOS' };
        } else if (/linux/i.test(platform)) {
          os = { zh: 'Linux', en: 'Linux' };
        }

        var arch = null;
        if (os && os.zh === 'Windows') {
          var a = normalizeArch(high.architecture);
          if (!a && String(high.bitness) === '64') a = 'x64';
          arch = a || 'x64';
          key = arch === 'arm64' ? 'win-arm64' : 'win-x64';
        }
        return finalizeDetection({ key: key, os: os, arch: arch });
      })
      .catch(function () { return fallback; });
  }

  /* ------------------------------------------------------------------ *
   * 主题
   * ------------------------------------------------------------------ */
  var themeMeta = $('meta[name="theme-color"]');
  var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
  var manualTheme = false;

  function syncThemeColor() {
    if (!themeMeta) return;
    var dark = root.getAttribute('data-theme') === 'dark';
    var configured = STRINGS[state.lang].themeColor;
    var value = configured || (dark ? '#14120f' : '#faf8f5');
    themeMeta.setAttribute('content', value);
  }

  function setTheme(theme, fromUser) {
    root.setAttribute('data-theme', theme);
    if (fromUser) {
      manualTheme = true;
      safeSet(THEME_KEY, theme);
    }
    syncThemeColor();
  }

  function initTheme() {
    var saved = safeGet(THEME_KEY);
    manualTheme = saved === 'dark' || saved === 'light';
    if (manualTheme) {
      root.setAttribute('data-theme', saved);
    } else if (media) {
      root.setAttribute('data-theme', media.matches ? 'dark' : 'light');
    }
    syncThemeColor();

    var toggle = $('#themeToggle');
    if (toggle) {
      toggle.addEventListener('click', function () {
        setTheme(root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark', true);
      });
    }

    if (media) {
      var onChange = function (event) {
        if (manualTheme) return; /* 用户手动选过就不再跟随 */
        root.setAttribute('data-theme', event.matches ? 'dark' : 'light');
        syncThemeColor();
      };
      if (typeof media.addEventListener === 'function') media.addEventListener('change', onChange);
      else if (typeof media.addListener === 'function') media.addListener(onChange);
    }
  }

  /* ------------------------------------------------------------------ *
   * 语言
   * ------------------------------------------------------------------ */
  function applyLanguage(persist) {
    var table = STRINGS[state.lang];

    root.setAttribute('lang', table.lang);
    document.title = table.title;

    var desc = $('meta[name="description"]');
    if (desc) desc.setAttribute('content', table.description);

    var og = $('meta[property="og:title"]');
    if (og) og.setAttribute('content', state.lang === 'zh' ? 'Calcpad · 无限画布计算器' : 'Calcpad · Infinite-canvas calculator');

    $$('[data-i18n]').forEach(function (el) {
      var key = el.getAttribute('data-i18n');
      var value = table.strings[key];
      if (typeof value !== 'string' || !value) return;
      if (el.getAttribute('data-i18n-html') === '1') el.innerHTML = value;
      else el.textContent = value;
    });

    var label = $('#langLabel');
    if (label) label.textContent = state.lang === 'zh' ? 'EN' : '中文';

    var toggle = $('#langToggle');
    if (toggle) {
      toggle.setAttribute('aria-label', state.lang === 'zh'
        ? '切换语言：中文 / English'
        : 'Switch language: English / 中文');
    }

    if (persist) safeSet(LANG_KEY, state.lang);
    renderPrimary();
    renderRecommended();
    syncThemeColor();
  }

  function initLanguage() {
    var saved = safeGet(LANG_KEY);
    if (saved === 'en' || saved === 'zh') {
      state.lang = saved;
    } else {
      var nav = String((navigator.languages && navigator.languages[0]) || navigator.language || '');
      state.lang = /^en\b/i.test(nav) ? 'en' : 'zh';
    }

    var toggle = $('#langToggle');
    if (toggle) {
      toggle.addEventListener('click', function () {
        state.lang = state.lang === 'zh' ? 'en' : 'zh';
        applyLanguage(true);
      });
    }
    applyLanguage(false);
  }

  /* ------------------------------------------------------------------ *
   * 渲染：主下载按钮
   * ------------------------------------------------------------------ */
  function renderPrimary() {
    var button = $('#primaryDownload');
    var detectLine = $('[data-role="detect"]');
    var text = $('[data-role="primary-text"]');
    if (!button || !text) return;

    var detected = state.detected;
    var lang = state.lang;
    var osName = detected.os ? detected.os[lang] : '';

    if (detected.key) {
      var platform = PLATFORMS[detected.key];
      var size = state.sizes[detected.key];
      var archName = detected.arch ? archLabel(detected.arch) : platform.arch;
      var archText = archName ? archName[lang] : '';

      button.setAttribute('href', DOWNLOAD_BASE + platform.file);
      button.setAttribute('data-download', 'true');
      button.setAttribute('data-role-key', detected.key);
      button.setAttribute('data-size', size);
      button.setAttribute('rel', 'noopener');
      button.removeAttribute('aria-disabled');

      text.textContent = detected.key === 'android'
        ? format(t('primaryAndroid'), { size: size })
        : format(t('primaryWindows'), { arch: archText || platform.arch[lang], size: size });
    } else {
      button.setAttribute('href', '#download');
      button.removeAttribute('data-download');
      button.removeAttribute('data-role-key');
      button.removeAttribute('data-size');
      button.removeAttribute('rel');
      button.setAttribute('aria-disabled', 'true');
      text.textContent = t('seeAll');
    }

    if (detectLine) {
      if (!detected.key) {
        detectLine.textContent = t('noPackage');
      } else if (detected.arch) {
        detectLine.textContent = format(t('detected'), { os: osName, arch: archLabel(detected.arch)[lang] });
      } else {
        detectLine.textContent = format(t('detectedUnknown'), { os: osName });
      }
    }
  }

  /* ------------------------------------------------------------------ *
   * 渲染：平台卡片
   * ------------------------------------------------------------------ */
  function renderRecommended() {
    var recommended = state.detected.key;
    ASSET_KEYS.forEach(function (key) {
      var card = $('[data-card="' + key + '"]');
      if (!card) return;

      var isRecommended = key === recommended;
      card.classList.toggle('is-recommended', isRecommended);

      var tag = $('[data-role="rec-tag"]', card);
      if (tag) tag.hidden = !isRecommended;

      var pick = $('[data-role="pick"]', card);
      if (pick) {
        pick.textContent = isRecommended
          ? format(t('recommended'), { size: state.sizes[key] })
          : '';
      }
    });
  }

  function applySize(key, size) {
    if (!size) return;
    state.sizes[key] = size;

    var card = $('[data-card="' + key + '"]');
    if (card) {
      $$('[data-role="size"]', card).forEach(function (el) { el.textContent = size; });
    }

    var button = $('#primaryDownload');
    if (button && button.getAttribute('data-role-key') === key) {
      button.setAttribute('data-size', size);
    }
  }

  function applyVersion(version) {
    if (!version) return;
    state.version = version;
    $$('[data-role="version"], [data-role="version-inline"]').forEach(function (el) {
      el.textContent = 'v' + version;
    });
  }

  /* ------------------------------------------------------------------ *
   * GitHub Releases：失败 / 限流 / 超时一律静默降级
   * ------------------------------------------------------------------ */
  function loadRelease() {
    if (typeof window.fetch !== 'function' || typeof window.AbortController !== 'function') return;

    var controller = new AbortController();
    var timer = window.setTimeout(function () { controller.abort(); }, FETCH_TIMEOUT_MS);

    window.fetch(RELEASE_API, {
      signal: controller.signal,
      headers: { Accept: 'application/vnd.github+json' },
      cache: 'no-store',
      referrerPolicy: 'no-referrer'
    })
      .then(function (response) {
        if (!response.ok) return null;
        return response.json();
      })
      .then(function (data) {
        if (!data || typeof data !== 'object') return;
        if (typeof data.tag_name === 'string' && data.tag_name) {
          applyVersion(data.tag_name.replace(/^v/i, ''));
        }
        var assets = Array.isArray(data.assets) ? data.assets : [];
        assets.forEach(function (asset) {
          var name = String(asset && asset.name || '').toLowerCase();
          for (var i = 0; i < ASSET_KEYS.length; i += 1) {
            var key = ASSET_KEYS[i];
            if (name === PLATFORMS[key].file.toLowerCase()) {
              var size = formatBytes(asset.size);
              if (size) applySize(key, size);
              break;
            }
          }
        });
      })
      .catch(function () { /* 静默降级：继续使用静态兜底值 */ })
      .then(function () {
        window.clearTimeout(timer);
        renderPrimary();
        renderRecommended();
      });
  }

  /* ------------------------------------------------------------------ *
   * 下载反馈（直接落盘，不弹窗、不加载新页面）
   * ------------------------------------------------------------------ */
  function initDownloadFeedback() {
    document.addEventListener('click', function (event) {
      var link = event.target && event.target.closest
        ? event.target.closest('[data-download][href]')
        : null;
      if (!link) return;

      var key = link.getAttribute('data-role-key');
      if (!key) {
        var card = link.closest('[data-card]');
        key = card ? card.getAttribute('data-card') : null;
      }
      if (!key || !PLATFORMS[key]) return;

      var status = $('[data-role="pick"]', link.closest('[data-card]') || document) ||
        $('[data-role="detect"]');
      if (!status) return;

      status.textContent = format(t('downloading'), {
        name: PLATFORMS[key].file,
        size: state.sizes[key]
      });

      window.setTimeout(function () {
        if (status.textContent.indexOf(state.sizes[key]) !== -1) {
          status.textContent = t('releaseFailed');
        }
      }, 12000);
    });
  }

  /* ------------------------------------------------------------------ *
   * 滚动进入动画
   * ------------------------------------------------------------------ */
  function initReveal() {
    var targets = $$('.card, .feature, .platform, .notice, .local-first, .section-head');
    if (!targets.length) return;

    var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (reduced || typeof window.IntersectionObserver !== 'function') return;

    targets.forEach(function (el, index) {
      el.classList.add('reveal');
      el.style.setProperty('--reveal-delay', (index % 4) * 70 + 'ms');
    });

    var observer = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        entry.target.classList.add('is-visible');
        observer.unobserve(entry.target);
      });
    }, { rootMargin: '0px 0px -8% 0px', threshold: 0.06 });

    targets.forEach(function (el) { observer.observe(el); });
  }

  /* ------------------------------------------------------------------ *
   * 启动
   * ------------------------------------------------------------------ */
  function boot() {
    root.classList.add('js');

    initTheme();
    initLanguage();
    initDownloadFeedback();
    initReveal();

    detectPlatform().then(function (detected) {
      state.detected = detected;
      renderPrimary();
      renderRecommended();
    });

    loadRelease();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
