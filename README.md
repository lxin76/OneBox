# OneBox · 可插拔小工具平台

**Windows 上的小工具平台：宿主只提供插件框架，每个小工具都是独立进程，可随时安装、启用、禁用、卸载。**

本 README 只讲平台本身和它内置的小工具清单；每个小工具的用法、热键、设置项与技术细节，写在各自的 README 里（见[内置小工具](#内置小工具)）。

```
OneBox.exe（宿主：托盘 + 工具箱 + 热键/托盘/设置/日志/崩溃重启）
  └─ plugins/            ← 把你的小工具丢进来即插即用
       textpin/    置顶文本
       quicknote/  快速便签（示例插件，也是写新插件的模板）
       screenshot/ 截图
```

## 平台特性

- **可插拔**：小工具 = 一个目录 + 一个 `plugin.json` + 一个 exe。放进 `plugins/` 即被发现
- **进程隔离**：每个小工具跑在独立子进程里，崩一个不影响宿主和其他小工具
- **便携**：配置、插件、日志、小工具数据全在安装目录内（`data/`），不写 `%LOCALAPPDATA%`——整个目录拷走即用，删掉目录就是彻底卸载
- **自由装卸**：工具箱里一键启用/禁用；已安装的小工具可连同数据目录一起删除
- **专属图标**：每个小工具一枚按功能设计的图标，exe、窗口、工具箱列表、设置中心与托盘子菜单统一使用；小图标另有简化画法，缩到 16px 仍可辨
- **单实例**：托盘、全局热键、插件进程都只容得下一份，因此以数据目录为作用域开命名互斥体；重复启动时第二个进程把 `--toolbox` / `--settings` 请求转交给正在运行的实例后自行退出，不会出现两份实例互抢热键、同写一份配置的情况
- **崩溃自愈**：小工具异常退出后按指数退避自动重启（`min(2×次数, 10)` 秒），稳定运行后重置计数；连续崩溃超过阈值则标记为故障并停止重试
- **宿主托管能力**（小工具无需重复实现）：
  - **全局热键**：小工具只声明组合键，宿主统一注册并做冲突仲裁；被占用时明确回报原因，不影响其他热键
  - **分层托盘菜单**：每个小工具在托盘右键菜单里占一个**子菜单**（以其展示名为标题），小工具自己声明的项收在子菜单里，不与其他小工具混在一起；支持勾选项与分隔线
  - **设置中心**：宿主与小工具按插件分层展示——左栏选小工具，右栏只显示它的设置，不再把所有选项混在一页
  - **私有设置**：每个小工具一个独立 `settings.json`，由宿主读写
  - **日志收集**：宿主统一落盘并按小工具分文件，2 MB 轮转；同时捕获小工具进程的 stdout/stderr
  - **气泡通知**、**开机自启**、**状态上报**
- **主题风格**：内置**深色 / 浅色 / 石墨 / 暖阳**四套配色，设置中心里点一下立即切换；宿主窗口、系统标题栏与各小工具的窗口一起变色，选择随设置保存、重启沿用
- **右键菜单**（可选）：勾选后桌面与文件夹的右键菜单里多一项「OneBox 工具箱」，右键直接开工具箱，不必先去找托盘图标
- **工具箱窗口**：三个页签（圆角药丸样式，选中态用强调色填充）——小工具（带图标的列表，可启用/禁用/重启/安装/卸载）、热键（**按小工具分组**，谁占用了什么）、目录与日志

## 内置小工具

平台随宿主分发三个小工具（位于 `plugins/`），都可在工具箱里启用/禁用；内置小工具不可卸载，只能禁用。

| 小工具 | 插件 id | 简介 | 详细文档 |
| --- | --- | --- | --- |
| 置顶文本 | `textpin` | 常驻置顶的纯文本框，随手记与暂存纯文本；一键抓取选中文字置顶、复制/粘贴回目标软件，**不监听剪贴板** | [README](src/OneBox.Plugins.TextPin/README.md) |
| 快速便签 | `quicknote` | 热键呼出一个置顶便签窗，内容自动保存；同时是编写新小工具的模板 | [README](src/OneBox.Plugins.QuickNote/README.md) |
| 截图 | `screenshot` | 区域/全屏/滚动截长图，贴图置顶不被遮挡、贴回原位；另存为保存，双引擎离线文字识别与划选取字 | [README](src/OneBox.Plugins.Screenshot/README.md) |

> 想自己写一个小工具，从[快速便签](src/OneBox.Plugins.QuickNote/README.md)照搬即可，接口见下面的[写一个小工具](#写一个小工具)。

## 使用

### 运行

下载 [Release](../../releases)，解压后双击 `OneBox.exe`。托盘常驻，右键托盘图标可打开工具箱、设置中心；每个小工具在菜单里是一个**子菜单**，它自己的功能项收在子菜单里。

**关闭窗口时要不要顺便退出，由你自己定**：默认只关窗口、OneBox 留在托盘；第一次关窗口时会弹一次快捷确认，
问完就把选择记进设置，之后每次关闭都按它执行，不再打扰。想改随时可以去「设置 → 宿主 → 宿主行为」里调。

> 两个窗口都开着时，关掉其中一个不会触发退出，全部关掉才按上面的选择执行——避免"想关个小窗，结果整个程序没了"。

命令行开关（可直接做成桌面快捷方式）：

```
OneBox.exe --toolbox     启动并打开工具箱
OneBox.exe --settings    启动并打开设置中心
```

已经有一个 OneBox 在跑时，再执行上面两条命令不会启动第二份，而是把请求转交给现有实例去开对应窗口。

> 便携模式：所有数据写在 exe 同级的 `data/` 下。想换位置就把整个目录搬走，
> 备份同理——只复制这一个目录即可，不涉及系统任何其他位置。

### 托盘菜单与设置中心

托盘右键菜单的结构：

```
打开工具箱…
设置…
──────────────
置顶文本 ▸   ← 每个小工具一层，标题带它自己的图标，具体项由小工具自己声明
快速便签 ▸
截图 ▸
──────────────
退出 OneBox
```

小工具一多，平铺会把右键菜单撑得很长且难以定位；按小工具分层后每个小工具各占一层，只展开你需要的那一个。停止或禁用的小工具不会出现在菜单里。

**设置中心**（托盘菜单「设置…」）同样按小工具分层：左栏列出「宿主」与各小工具，右栏只显示选中那一项的设置，按分组折叠排布，不再把所有选项混在一页。

「宿主」页的第一组是**外观**：四套主题各给一张带配色预览的卡片，点一下立即生效——宿主窗口、
系统标题栏以及各小工具的窗口（快速便签、置顶文本、截图设置与识别结果）会一起换色，
不需要重启，也不必逐个窗口去调。

> 截图时盖在屏幕上的浮层（区域选择、贴图、滚动控制）固定使用深色：它们要叠加在别人的内容之上，
> 深色半透明是取景需要，不随主题走。

> 小工具需要自己的设置界面时（例如热键录入要展示「被占用」提示），仍然可以保留自己的设置窗口，并从子菜单进入；同时它也能把常规设置项声明给宿主，在设置中心里统一调整。两种入口改的是同一份设置，互为同步。

### 资源管理器右键菜单

设置中心 →「宿主」→「宿主行为」→ 勾选「在桌面与文件夹的右键菜单里加入「OneBox 工具箱」」后，
在**桌面空白处或文件夹空白处**右键就多一项 **OneBox 工具箱**，点一下直接打开工具箱。

右键菜单只留这一个入口，各小工具的入口都在工具箱里——把每个小工具也铺进去会把菜单撑得很长，
一级项还容易被当成"点了没反应"。

- 取消勾选即从右键菜单移除；只写当前用户的 `HKCU`，不需要管理员权限
- 菜单项点击只能执行命令行，因此它是再启动一次 `OneBox.exe --toolbox`，由单实例闸门转交给正在运行的实例：
  正在跑就直接激活工具箱，没在跑就把它启动起来

> Windows 11 把经典右键菜单收进了「显示更多选项」，这一项也在那里。

### 工具箱

页头右侧有一枚齿轮图标 ⚙，点一下直接打开设置中心（与托盘菜单的「设置…」是同一个入口，已打开时只会激活原窗口）。

**小工具页**：每个小工具一张卡片，带图标、版本、来源（内置/已安装）、运行状态与它占用的热键，右侧是启用开关、重启与卸载。

**热键页按小工具分组**，而不是把所有热键平铺成一列：

```
▾ 🖼 截图          4 个热键 · 1 个未生效        有冲突
    ▎Ctrl+Alt+A   截图：区域截图                 已生效
    ▎Ctrl+Alt+F   截图：全屏截图                 已生效
    ▎Ctrl+Alt+S   截图：滚动截长图               已生效
    ▎Ctrl+Alt+T   截图：识别剪贴板图片文字        未生效：已被其他小工具占用
▸ 📌 置顶文本      5 个热键                      正常
```

- 每个小工具一层卡片，**组头直接给出数量与冲突概况**（正常 / 有冲突 / 未运行），不用展开就能看出哪个小工具出了问题
- 组头可点击折叠，另有「全部展开 / 全部折叠」
- 顶部筛选框按**小工具名、组合键或用途**就地过滤：命中插件名则整组保留，否则只留下命中的那几行
- 左侧竖条是该条热键的状态色（绿=已生效，橙=未生效），并写明未生效的具体原因

> 之所以按小工具分组：热键平铺时，小工具一多就退化成一大片看不出归属的行，想找某个小工具的热键只能靠肉眼扫；分组后每层只展开你需要的那一个。

**目录与日志页**：三个目录的路径与「打开」按钮。

## 写一个小工具

最小的小工具只需要三样东西：一个 `plugin.json`、一个入口 exe、以及引用 `OneBox.Plugin.Sdk`。

**1. 清单 `plugin.json`**

```json
{
  "id": "quicknote",
  "name": "快速便签",
  "version": "1.0.0",
  "description": "热键呼出一个置顶便签窗，内容自动保存。",
  "entry": "OneBox.Plugins.QuickNote.exe",
  "protocolVersion": 1
}
```

`id` 必须以小写字母或数字开头，只含小写字母、数字、`.`、`_`、`-`；`entry` 必须位于插件目录内。

**图标**：在项目里设 `<ApplicationIcon>Assets\app.ico</ApplicationIcon>` 即可——宿主会直接读 exe 里嵌入的图标，用在工具箱列表、设置中心和托盘子菜单上。没设图标时显示通用占位图标，不影响加载。
若想在 exe 图标之外另给一个（例如 exe 要留着通用图标，界面里想用另一枚），再在清单里加 `"icon": "app.ico"` 覆盖：它相对插件目录解析，越界或文件缺失都只退回默认图标，不会把插件判为损坏。

**2. 入口**

```csharp
[STAThread]
private static int Main(string[] args)
{
    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    app.Startup += (_, _) => _ = OneBoxPlugin.RunAsync(args, async (context, stoppingToken) =>
    {
        // 在这里创建你的窗口，并使用宿主提供的能力
        await context.DeclareHotkeysAsync([
            new HotkeyDeclaration { ActionId = "toggle", Combination = "Ctrl+Alt+N", Description = "显示/隐藏" },
        ]);
        await context.DeclareTrayMenuAsync([ new TrayMenuItem { Id = "show", Text = "显示便签" } ]);

        context.HotkeyPressed += actionId => { /* 切回 UI 线程操作窗口 */ };
        context.MenuInvoked  += item     => { /* 同上 */ };

        await context.ReadyAsync("快速便签", "已就绪");

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    });
    app.Run();
    return 0;
}
```

`PluginContext` 提供的能力：

| 成员 | 说明 |
|---|---|
| `ReadyAsync` / `SetStatusAsync` | 上报展示名与工具箱里的状态文本 |
| `DeclareHotkeysAsync` | 声明全局热键，由宿主统一注册并做冲突仲裁 |
| `DeclareTrayMenuAsync` | 声明托盘菜单项；这些项会收进**本小工具自己的子菜单**，不与其他小工具混排 |
| `DeclareSettingsAsync` | 声明本小工具在**设置中心**里暴露的设置项（见下） |
| `HotkeyPressed` / `MenuInvoked` / `SettingsChanged` / `ShutdownRequested` / `ConnectionClosed` | 来自宿主的事件（已自动派发回你的 UI 线程） |
| `GetSettingAsync<T>` / `SetSettingAsync<T>` | 读写本小工具的私有设置 |
| `CurrentTheme` / `ThemeChanged` | 宿主当前主题与换主题通知；配合 `OneBox.Plugin.Sdk.Wpf` 的 `PluginTheme` 可让插件窗口自动跟随 |
| `LogInfoAsync` / `LogWarningAsync` / `LogErrorAsync` | 写入宿主统一日志 |
| `NotifyAsync` | 弹出气泡通知 |
| `RequestShutdownAsync` / `RequestRestartAsync` | 请求宿主停止或重启本小工具 |
| `DataDirectory` / `LogDirectory` / `Stopping` | 私有数据目录、日志目录、停止令牌 |

**关于设置中心的分层**

`DeclareSettingsAsync` 让宿主替你渲染设置界面，省掉每个小工具自己写一个窗口。传一组 `SettingDescriptor` 即可：

```csharp
await context.DeclareSettingsAsync(
[
    new SettingDescriptor
    {
        Key = "copyToClipboard",
        Label = "截图后复制到剪贴板",
        Kind = SettingKind.Bool,       // Bool / Text / Multiline / Number / Choice / DirectoryPath / FilePath
        Group = "保存",                 // 同一 Group 归到同一个标题下
        Description = "截完直接写入剪贴板，省一次手动复制。",
        Value = JsonSerializer.SerializeToElement(_settings.CopyToClipboard),
    },
    new SettingDescriptor
    {
        Key = "jpegQuality",
        Label = "JPEG 质量",
        Kind = SettingKind.Number,
        Group = "保存",
        Value = JsonSerializer.SerializeToElement(_settings.JpegQuality),
        Min = 1, Max = 100,            // 宿主会夹取超范围输入
    },
]);

// 用户在设置中心改动后回传给你，由你自己落地持久化
context.SettingsChanged += payload =>
{
    foreach (var change in payload.Changes)
        Apply(change.Key, change.Value);   // 按 Key 分派
    Save();
    _ = context.DeclareSettingsAsync(Describe());  // 重新声明以回显（如夹取后的值）
};
```

设计约定：**小工具是设置值的唯一来源**。宿主只负责渲染与收集改动，改动通过 `SettingsChanged` 回传，由小工具自己落地并保存——宿主不保存一份副本，避免出现两份真相。声明里也可以带 `ReadOnly = true`，用来展示由小工具算出的只读信息。

**让窗口跟随宿主主题**

窗口颜色不要写死，改用宿主下发的主题键（XAML 里写作 `{DynamicResource Theme.Card}`），
并在插件主体开头挂一次：

```csharp
PluginTheme.Attach(context);        // 一次即可：当前主题与之后的变化都会刷进本进程的应用资源

var window = new NoteWindow();
PluginTheme.ApplyToWindow(window);  // 有系统标题栏的窗口加这一行，标题栏的明暗也会跟随
```

`PluginTheme` 来自 `OneBox.Plugin.Sdk.Wpf`。它把整份调色板刷进应用级资源，因此之后新开的窗口
不必再注册也会带上当前主题；可用颜色键见 `OneBox.Abstractions.Theming.ThemeKeys`。
插件没有连宿主时（单独调试）自动退回默认深色主题。

**3. 打包**

把编译产物和 `plugin.json` 放进同一个目录，整个目录丢到发布目录的 `plugins/` 下（或用工具箱的「安装…」按钮直接选目录/.zip），然后在工具箱里启用。[快速便签](src/OneBox.Plugins.QuickNote/README.md)就是完整可跑的示例，可直接照搬。

> 宿主与插件通过命名管道通信：4 字节小端长度前缀 + UTF-8 JSON 帧，请求-应答按 `requestId` 配对，帧上限 16 MB。协议常量见 `OneBox.Abstractions.Protocol`。

## 项目结构

```
src/OneBox.Abstractions/    宿主与插件共享的契约（net8.0，不依赖 WPF）
  Protocol / PluginManifest / IpcEnvelope / IpcMessages / IpcConnection / Hotkeys
  Theming/ ThemeCatalog（内置主题配色）/ ThemePalette / ThemeKeys（颜色键，宿主与插件共用）
src/OneBox.Plugin.Sdk/      插件侧 SDK：连接宿主、能力 API、事件派发（net8.0）
  OneBoxPlugin（入口）/ PluginContext（会话）
src/OneBox.Plugin.Sdk.Wpf/  插件侧 WPF 主题包（net8.0-windows）：PluginTheme 让插件窗口跟随宿主主题
src/OneBox.Host/            宿主（WPF 托盘程序，产物名 OneBox.exe）
  Services/  PluginRegistry / PluginManager / PluginRuntime / HotkeyService
             ThemeManager（主题刷进 WPF 资源、刷新标题栏、广播给插件）
             ContextMenuRegistrar（资源管理器右键菜单：只一项「OneBox 工具箱」）
             HostRequest（命令行开关与单实例转交请求的编解码）
             HostPaths / HostLog / AutoStartService / SingleInstance（单实例闸门）
             TrayMenuBuilder（托盘菜单按插件分层）/ SettingsFormBuilder（设置项分组与取值编解码）
             HotkeyGroupBuilder（工具箱热键页按小工具分组与筛选，纯逻辑）
             PluginIcons（插件图标解析）/ LegacyDataMigrator（旧数据迁移到便携目录）
  Windows/   ToolboxWindow（工具箱，药丸页签 + 分组热键页）/ SettingsCenterWindow（设置中心，按插件分层）/ UninstallDialog
  ViewModels/ PluginItemViewModel / HotkeyGroupViewModel（热键分组与行，含筛选与状态色）
src/OneBox.Plugins.TextPin/      置顶文本小工具（独立进程，自带全部 UI，不监听剪贴板）
src/OneBox.Plugins.TextPin.Core/ 置顶文本的纯逻辑库（文本规范化与截断、统计、落盘、设置声明，可单测）
src/OneBox.Plugins.QuickNote/      示例小工具，兼作模板
src/OneBox.Plugins.Screenshot/      截图小工具（区域/全屏/滚动截长图、贴图置顶、OCR、划选取字）
src/OneBox.Plugins.Screenshot.Core/ 截图的纯逻辑库（文件名、滚动拼接、文本整理、OCR 版面与选区，可单测）
tests/OneBox.Tests/                单元测试
tools/icons/                       图标生成器（造型、尺寸策略、检查图）
```

各小工具的目录结构、数据文件与实现细节，见各自的 README。

宿主运行时目录（**便携模式**：全部在宿主 exe 同级，不写系统用户目录）：

```
plugins/                  随宿主分发的内置小工具
data/plugins/             用户安装的小工具
data/plugin-state/<id>/   每个小工具的私有数据（卸载时可选删除）
data/logs/host.log        宿主日志
data/logs/plugin-<id>.log 各小工具日志（含其 stdout/stderr）
data/plugins.json         启用状态、插件来源、崩溃统计
data/settings.json        宿主设置
```

> 之所以不去 `%LOCALAPPDATA%\OneBox`：那会让"程序在哪"和"数据在哪"分离——卸载会留下残留，
> 装了两份还会互相串数据。现在的规则很简单：**整个安装目录拷走就能用，删掉目录就是彻底卸载**。

**从旧版本升级**：老版本把数据放在 `%LOCALAPPDATA%\OneBox`。首次启动新版本时宿主会把它
一次性搬进安装目录的 `data/`（插件、小工具数据、设置、日志都搬），搬完在 `data/` 下留一个
`.migrated-from-localappdata` 标记，之后不再重复。旧目录只读不删，直到你不再需要旧版本时
手动清理；若新旧位置已有同名文件，以新位置为准。

## 技术要点

- **插件进程**：宿主以 `--pipe <name> --plugin-id <id>` 启动插件；插件连上命名管道后收到 `hello`（协议版本、数据目录），再收到 `start`
- **STA 安全**：SDK 内部一律 `ConfigureAwait(false)`，但插件的所有回调都通过捕获的同步上下文派发回 UI 线程——否则 WPF 控件会在非 STA 线程上创建
- **热键归属**：只有处于 `Running` 的插件才持有热键，停止/崩溃/禁用会立即让出组合键；注册采用整体重建，避免残留
- **热键应答要等注册结果**：`DeclareHotkeysAsync` 的应答必须携带真实注册结果。宿主若先回 `ok` 再异步注册，插件就永远收到空结果，也就无法提示"这个热键被占用了"——所以宿主先挂起该请求，等注册完成回填结果后再应答
- **内置插件不可卸载**：随宿主分发的插件只能禁用（删了下次启动会复原）；用户安装的同 id 插件优先于内置
- **菜单分层不碰 WPF**：`TrayMenuBuilder` 只做数据整理（按插件分组、清洗分隔线），返回纯数据，由 `App.RefreshTrayMenu` 转成 WPF 菜单项——这样"分组/去重分隔线"的逻辑可以完整单测，不必去戳托盘菜单
- **每个插件的菜单项各自清洗**：插件原先是在扁平列表里声明分隔线的，用来和"别人的项"隔开。分层后每个插件独立成子菜单，这些边界分隔线就多余了，因此首尾分隔线一律去掉、连续分隔线折叠成一条
- **设置项声明的边界校验**：宿主传来的 JSON 可能是任何东西（手输、旧版本残留）。小工具在应用改动时对每个键都做类型与范围校验，非法值一律忽略并保留原值——被乱改一轮后设置对象仍处于合法值域
- **数字输入失焦/回车才提交**：逐字提交会让插件收到一堆中间态（输入 `10` 时的 `1`），因此只在失焦或回车时下发，并夹取到声明范围
- **设置不双写**：宿主不保存插件设置的副本，只回传改动，由插件自己落地；否则"宿主存的"和"插件存的"迟早会不一致
- **热键分组是纯逻辑**：`HotkeyGroupBuilder` 只做数据整理（按小工具归组、排序、筛选取子集），不碰 WPF；窗口只负责把整理结果包成视图模型。这样"分组顺序 / 组内排序 / 筛选命中规则"都能完整单测
- **筛选不重建分组对象**：筛选只替换每组要显示的行，不重新构造视图模型——否则搜索框里每敲一个字，用户刚折叠的分组都会被冲掉、列表跳一下
- **组头统计不受筛选影响**：徽标显示的是该小工具的热键总数（`AllItems`），而不是筛选后剩几行，否则筛完会误以为插件只有一两个热键
- **插件停止即清空声明**：连接关闭（正常停止、崩溃、宿主退出）时清掉该插件的菜单与设置声明，避免设置中心残留一个点进去什么也改不了的旧表单
- **数据目录便携化**：`HostPaths.DataDirectory` 只认 `exe 同级/data`，单元测试用 `DataDirectoryOverride` 把根换到临时目录——否则跑一次测试就会真往 `bin/` 旁边写数据
- **图标而非路径**：宿主优先读插件 exe 里嵌入的图标（`ApplicationIcon`），清单的 `icon` 只作可选覆盖。这样图标随 exe 一起走，永远不会和插件实际长相脱节，插件作者也不必额外维护一个图标文件
- **图标读取全程降级**：图标是纯装饰，路径越界、文件缺失、ico 损坏都只退回通用占位图标，绝不把插件判成损坏或抛异常
- **小尺寸另画一套**：16/20/24px 不靠缩放大图，而是按目标像素反推笔画宽度、去掉发光与渐变、并用 alpha 包围盒把造型放大占满画幅——直接缩放的话线宽不足半个像素，必然糊成一团（详见 [tools/icons/README.md](tools/icons/README.md)）
- **单实例以数据目录为作用域**：互斥体与转交管道名都由数据目录路径算出的短标识拼成，所以换一个安装目录就能同时跑两份（这正是便携版该有的行为），同一目录则只允许一份。转交失败只记日志，不让用户干等
- **托盘子菜单带图标**：子菜单标题用插件自己的图标，菜单里一眼能认出是哪一个小工具；取图顺序是「清单声明的 `icon` → exe 内嵌图标 → 通用占位图标」，任一步失败都只降级
- **旧数据一次性迁移**：升级到便携版时把 `%LOCALAPPDATA%\OneBox` 里的插件、插件数据、日志与配置搬进安装目录的 `data/`；只搬「新位置还没有」的条目（不覆盖新版数据），搬完留标记不再重复，失败不阻断启动，旧目录只读不删
- **主题是纯数据，两端共用一组颜色键**：色值只写在 `ThemeCatalog` 里（键名由 `ThemeKeys` 定义），宿主启动时刷进 WPF 应用资源，窗口 XAML 只写 `{DynamicResource Theme.Card}`。换主题 = 替换资源值，已打开的窗口自动重绘，不必逐个窗口改属性
- **主题随 IPC 下发，插件不自己存一份**：握手时带上当前调色板，之后切换再广播 `themeChanged`。插件不内置主题目录，就不会出现「宿主换了配色、插件窗口还是老样子」的错位
- **算出来的画刷要重建，XAML 引用的不用**：热键页的状态色是视图模型算好绑定到属性的，主题一变就得重建（`ToolboxWindow.OnThemeChanged`）；代码构建的控件则用 `SetResourceReference`（代码版的 DynamicResource）才能跟着变
- **文字对比度是可测的**：`ThemeCatalogTests` 按 WCAG 公式算出每组「文字 / 底色」的对比度，断言 ≥ 4.5:1（水印文字放宽到 3:1）。新增主题漏键、色值写错或换了主题看不清字，都会在测试里直接失败，不必靠肉眼看
- **浮层窗口固定深色**：区域选择、贴图、滚动控制这些盖在屏幕上的窗口不跟主题走。它们叠加在任意内容之上，深色半透明是功能需要而非漏适配；跟随主题的是便签、置顶文本、截图设置与识别结果这类常规窗口
- **右键菜单走注册表而不是 shell 扩展**：这里只要一个静态菜单项，注册表就够，不必往资源管理器里塞一个常驻 DLL（那种扩展一出问题会拖累整个资源管理器）。菜单项点击只能执行命令行，于是它再启动一次 `OneBox.exe --toolbox`，由单实例闸门转交给正在运行的实例
- **右键菜单不留空壳容器**：早期版本在里面放了一个只有子菜单、本身没有命令的「OneBox 小工具」——点它不会有任何动作，用户第一下必然点空。现在只留一个能直接点的「OneBox 工具箱」，各小工具的入口都收在工具箱里
- **`--menu-invoke` 通道保留**：`OneBox.exe --menu-invoke <插件> <菜单项>` 可以让人用快捷方式或脚本直接触发某个小工具的菜单项。宿主刚启动、插件还没声明出该项时，请求会先记下来等它声明完再补做，而不是丢了

## 开发

要求：.NET SDK 8.0+

```powershell
# 构建
dotnet build OneBox.slnx

# 测试
dotnet test OneBox.slnx

# 运行宿主（Debug）
dotnet run --project src/OneBox.Host
```

> [截图](src/OneBox.Plugins.Screenshot/README.md)的 PaddleOCR 中文识别模型（约 16 MB）不入库，首次开发前跑一次
> `src/OneBox.Plugins.Screenshot/PrepareModels.ps1` 下载；不下载也能用，插件会自动回退到系统 OCR 引擎。
> 缺失时构建会明确警告（`-p:_RequireOcrModels=true` 可改为直接失败），发布脚本也会校验产物里 4 个
> OCR 文件齐全——缺模型时插件其实会**静默**降级，识别质量明显下降，必须在构建/发布阶段就拦住。

### 发布

两个脚本，按目标机器是否已装运行时选一个：

| 脚本 | 产物 | 适用场景 |
| --- | --- | --- |
| `publish-self-contained.ps1` | `publish/self-contained/` | 自包含，内置 .NET 8 运行时，拷贝即用；体积大 |
| `publish-framework-dependent.ps1` | `publish/framework-dependent/` | 框架依赖，体积小；目标机器须预装 .NET 8 桌面运行时 |

两种模式都把宿主与每个插件各自整合成**单个 exe**，产物布局完全一致：宿主一个 exe，每个插件目录一个 exe 加一个 `plugin.json`。

```powershell
# 自包含发布（推荐分发用）
.\publish-self-contained.ps1

# 框架依赖发布（体积小）
.\publish-framework-dependent.ps1

# 指定输出目录
.\publish-self-contained.ps1 -OutDir D:\dist\OneBox

# 只发布某个小工具
.\publish-framework-dependent.ps1 -Plugins quicknote
```

产物布局：`<输出目录>/OneBox.exe` 与 `<输出目录>/plugins/<id>/`（每个小工具的 exe 与 `plugin.json`）。插件由 `src/OneBox.Plugins.*` 自动发现（`*.Core` 纯逻辑库除外），插件 id 取自各自的 `plugin.json`，新增小工具无需改动脚本。

> 默认会先结束正在运行的 OneBox 及相关插件进程，避免运行中的实例锁定 exe 导致覆盖失败；需要保留进程时加 `-NoStop`。框架依赖脚本在本机缺少对应桌面运行时时会给出警告。

## 已知限制

- 目标软件以管理员运行时，需要 OneBox 也以管理员运行才能注入按键
- 全屏游戏/独占全屏可能拦截注入
- 同一个全局热键只能被一个程序注册；被其他软件占用时该热键不生效（工具箱的热键页会列出）
- 小工具需要与宿主协议版本一致（`protocolVersion`）；不兼容的插件会被标记为损坏而不会被启动

## License

[MIT](LICENSE)