# OneBox · 可插拔小工具平台

**Windows 上的小工具平台：宿主只提供插件框架，每个小工具都是独立进程，可随时安装、启用、禁用、卸载。**

```
OneBox.exe（宿主：托盘 + 工具箱 + 热键/托盘/设置/日志/崩溃重启）
  └─ plugins/            ← 把你的小工具丢进来即插即用
       clipboard/  剪贴板   （自带悬浮窗、历史库、JSON 属性面板）
       quicknote/  快速便签 （示例插件，也是写新插件的模板）
       screenshot/ 截图     （区域/全屏/滚动截长图、贴图置顶、文字识别、划选取字）
```

内置的剪贴板小工具延续了原来的悬浮条交互——**常驻置顶、点击条目即自动粘贴、全程不抢焦点**：

```
复制内容 → 悬浮条常驻在目标软件上方 → 单击条目 → 内容自动粘贴进当前软件
```

## 平台特性

- **可插拔**：小工具 = 一个目录 + 一个 `plugin.json` + 一个 exe。放进 `plugins/` 即被发现
- **进程隔离**：每个小工具跑在独立子进程里，崩一个不影响宿主和其他小工具
- **自由装卸**：工具箱里一键启用/禁用；已安装的小工具可连同数据目录一起删除
- **崩溃自愈**：小工具异常退出后按指数退避自动重启（`min(2×次数, 10)` 秒），稳定运行后重置计数；连续崩溃超过阈值则标记为故障并停止重试
- **宿主托管能力**（小工具无需重复实现）：
  - **全局热键**：小工具只声明组合键，宿主统一注册并做冲突仲裁；被占用时明确回报原因，不影响其他热键
  - **分层托盘菜单**：每个小工具在托盘右键菜单里占一个**子菜单**（以其展示名为标题），小工具自己声明的项收在子菜单里，不与其他小工具混在一起；支持勾选项与分隔线
  - **设置中心**：宿主与小工具按插件分层展示——左栏选小工具，右栏只显示它的设置，不再把所有选项混在一页
  - **私有设置**：每个小工具一个独立 `settings.json`，由宿主读写
  - **日志收集**：宿主统一落盘并按小工具分文件，2 MB 轮转；同时捕获小工具进程的 stdout/stderr
  - **气泡通知**、**开机自启**、**状态上报**
- **工具箱窗口**：三个页签——小工具（启用/禁用/重启/安装/卸载）、热键（谁占用了什么）、目录与日志

## 内置小工具：剪贴板

- **常驻置顶 + 不抢焦点**：`WS_EX_TOPMOST | WS_EX_NOACTIVATE`，点击悬浮条不打断当前软件
- **单击即粘贴**：点击历史条目 → 写入剪贴板并 `Ctrl+V` 到前台窗口（可切换为"仅复制"）
- **一键悬浮查看选中内容**：任意编辑器选中文字 → `Ctrl+Alt+C` → 自动复制并打开**自动置顶**的完整内容查看窗口
- **智能同段选中**：查看窗口双击按"字符串值 → `"key": value` 属性对 → 整行 → 整个 `{…}` 区块 → 全文"逐层命中，`Ctrl+双击` 继续向外扩一层
- **JSON 属性面板**：识别 JSON 后列出全部「属性路径 = 值」（嵌套用 `a.b[0].c`），单击复制值、双击复制路径
- **多格式剪贴板**：文本 / HTML / RTF / 图片 / 文件列表，事件驱动监听（零轮询）
- **监听开关**：**默认关闭**，关闭时系统不再投递剪贴板消息，完全不记录任何新的复制内容
- **去重与搜索**、**拖拽到任意目标软件**、**SQLite 持久化**

## 界面截图

![悬浮条](docs/screenshots/screenshot-bar.png)

![界面截图](docs/screenshots/screenshot-window.png)

## 使用

### 运行

下载 [Release](../../releases)，解压后双击 `OneBox.exe`。托盘常驻，右键托盘图标可打开工具箱、设置中心；每个小工具在菜单里是一个**子菜单**，它自己的功能项收在子菜单里。

### 托盘菜单与设置中心

托盘右键菜单的结构：

```
打开工具箱…
设置…
──────────────
剪贴板 ▸  显示/隐藏悬浮窗、监听剪贴板、清空历史、剪贴板设置…
快速便签 ▸ 显示便签、清空便签
截图 ▸    区域截图、全屏截图、滚动截长图、识别剪贴板图片文字、贴图置顶、关闭全部贴图、截图设置…
──────────────
退出 OneBox
```

小工具一多，平铺会把右键菜单撑得很长且难以定位；按小工具分层后每个小工具各占一层，只展开你需要的那一个。停止或禁用的小工具不会出现在菜单里。

**设置中心**（托盘菜单「设置…」）同样按小工具分层：左栏列出「宿主」与各小工具，右栏只显示选中那一项的设置，按分组（保存 / 截图 / 贴图 / 滚动截长图 / 文字识别…）折叠排布，不再把所有选项混在一页。

> 小工具需要自己的设置界面时（例如截图的热键录入需要展示「被占用」提示），仍然可以保留自己的设置窗口，并从子菜单进入；同时它也能把常规设置项声明给宿主，在设置中心里统一调整。两种入口改的是同一份设置，互为同步。

### 剪贴板的全局热键（可在设置页自定义）

| 热键 | 功能 |
|---|---|
| `Ctrl+Alt+V` | 呼出/收起历史面板 |
| `Ctrl+Alt+P` | 把当前剪贴板内容粘贴到前台窗口 |
| `Ctrl+Alt+C` | 复制选中文字并打开置顶的完整内容查看窗口 |
| `Ctrl+Alt+K` | 修复键盘输入（重置卡住的修饰键） |

> 默认热键刻意避开 `Ctrl+Shift`——中文 Windows 上 `Ctrl+Shift` 会被输入法切换器拦截。
> 改键后由宿主重新注册；若组合键已被其他软件占用，会明确提示但保留你的设置。

### 剪贴板交互

| 操作 | 行为 |
|---|---|
| 单击条目（图标区） | 复制 + 自动粘贴（窄条 ⏎ 按钮切换自动粘贴） |
| 点击窄条「监听」开关 | 开启/暂停剪贴板监听 |
| `Ctrl`+单击 | 仅复制 |
| 点击条目右侧 👁 | 打开完整内容查看窗口 |
| 点击条目预览文字 | 选中部分文字，`Ctrl+C` 复制选中部分 |
| 右键条目 | 查看完整内容 / 复制 / 复制为纯文本 / 粘贴为纯文本 / 删除 |
| 拖拽条目 | 拖到任意目标软件 |
| 双击托盘图标 | 唤醒并临时置顶展示 |
| 搜索框 | 输入过滤，`Esc` 关闭并归还焦点 |

## 内置小工具：截图

全屏、区域、滚动截长图，配合**贴图置顶**与**文字识别**：

- **区域截图**：拖动选区，实时显示像素尺寸；鼠标悬停自动吸附到窗口边框（`Space` 切换吸附），单击直接抓整窗，`Ctrl+A` 抓整个虚拟屏幕，`Enter` 确认、`Esc`/右键取消
- **全屏截图**：一次抓下所有显示器组成的虚拟屏幕（多屏负坐标正确处理）
- **滚动截长图**：在区域里滚轮下滚，逐帧用行签名定位重叠区并拼接，到达底部/高度上限自动停止，过程中可随时点「完成并保存」
- **贴图置顶**：截图后立刻以无边框窗口钉在屏幕上，`WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，并**每秒重申一次置顶**（只确认仍在置顶组，不改动组内层叠次序，避免多张贴图互相插队）——切换应用、甚至别的置顶窗口都不会盖住它。可拖动、滚轮缩放、右下角等比缩放、双击还原原始尺寸，支持多张同时贴图
- **保存**：PNG / JPEG（可调质量），文件名支持时间占位符，重名自动加序号；开启「自动保存」后截图直接落盘
- **文字识别**：调用系统自带 `Windows.Media.Ocr` 引擎，**离线可用、无需下载模型**；结果为中文时自动去掉字间空格，识别文本默认复制到剪贴板
- **划选取字**：在贴图上像文档一样拖动鼠标划选文字（**字符级**，可跨行），松开即复制，支持双击选词、`Ctrl+A` 全选、`Ctrl+C` 复制。识别结果按词框缓存在贴图上，划选是纯几何计算，不重复识别
- **不抢焦点**：贴图窗点击不激活，不影响你正在编辑的内容；截图前会自动隐藏自己的贴图与设置窗，避免入镜

### 截图的全局热键（可在设置页自定义）

| 热键 | 功能 |
|---|---|
| `Ctrl+Alt+A` | 区域截图 |
| `Ctrl+Alt+F` | 全屏截图 |
| `Ctrl+Alt+S` | 滚动截长图 |
| `Ctrl+Alt+T` | 识别剪贴板里的图片文字 |

### 贴图窗快捷键

| 操作 | 行为 |
|---|---|
| 拖动图片 | 移动贴图位置 |
| 滚轮 | 以 10% 步进缩放 |
| 双击图片 | 在原始尺寸与屏幕适配之间切换 |
| `Alt+T` | 切换置顶 / 取消置顶 |
| `Alt+C` / `Alt+S` / `Alt+O` | 复制 / 保存 / 识别文字 |
| `Alt+X` | 进入 / 退出「选择文字」模式 |
| `Alt+0` | 恢复原始尺寸 |
| `Esc` | 关闭本贴图（选择文字模式下则先退出该模式） |

### 在贴图上划选取字

点工具条上的 📝 进入「选择文字」模式（或按 `Alt+X`），首次进入会识别一次图中的文字：

| 操作 | 行为 |
|---|---|
| 拖动鼠标 | 划选文字（字符级，可跨行），**松开即复制** |
| 双击 | 选中光标下的整词 |
| `Ctrl+A` / `Ctrl+C` | 全选 / 复制当前选区 |
| `Esc` | 退出选择文字模式 |

> 之所以用工具条按钮而不是直接拖动：贴图窗的左键拖动已经用于**移动窗口**，两种手势若靠"拖多远"隐式区分会难以预期。进入模式后光标变为 I 形，此时拖动才是划选。
> 划选高亮由 OCR 词框直接算出，与鼠标命中测试共用同一套坐标，因此"点哪里、哪里亮"始终一致；OCR 只在首次进入模式时跑一次并缓存，拖动过程不重复识别。

> 文字识别依赖系统已安装的 OCR 语言包。若提示「未安装识别语言包」，请在「设置 → 时间和语言 → 语言和区域」里为目标语言添加「光学字符识别」功能。
> 目标程序以管理员身份运行时，OneBox 也需以管理员身份运行，否则无法对其注入滚动事件。

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
        Key = "autoSave",
        Label = "截图后自动保存",
        Kind = SettingKind.Bool,       // Bool / Text / Multiline / Number / Choice / DirectoryPath / FilePath
        Group = "保存",                 // 同一 Group 归到同一个标题下
        Description = "开启后不询问，直接保存到保存目录。",
        Value = JsonSerializer.SerializeToElement(_settings.AutoSave),
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

**3. 打包**

把编译产物和 `plugin.json` 放进同一个目录，整个目录丢到发布目录的 `plugins/` 下（或用工具箱的「安装…」按钮直接选目录/.zip），然后在工具箱里启用。`OneBox.Plugins.QuickNote` 就是完整可跑的示例，可直接照搬。

> 宿主与插件通过命名管道通信：4 字节小端长度前缀 + UTF-8 JSON 帧，请求-应答按 `requestId` 配对，帧上限 16 MB。协议常量见 `OneBox.Abstractions.Protocol`。

## 项目结构

```
src/OneBox.Abstractions/    宿主与插件共享的契约（net8.0，不依赖 WPF）
  Protocol / PluginManifest / IpcEnvelope / IpcMessages / IpcConnection / Hotkeys
src/OneBox.Plugin.Sdk/      插件侧 SDK：连接宿主、能力 API、事件派发（net8.0）
  OneBoxPlugin（入口）/ PluginContext（会话）
src/OneBox.Host/            宿主（WPF 托盘程序，产物名 OneBox.exe）
  Services/  PluginRegistry / PluginManager / PluginRuntime / HotkeyService
             HostPaths / HostLog / AutoStartService
             TrayMenuBuilder（托盘菜单按插件分层）/ SettingsFormBuilder（设置项分组与取值编解码）
  Windows/   ToolboxWindow（工具箱）/ SettingsCenterWindow（设置中心，按插件分层）/ UninstallDialog
  ViewModels/ PluginItemViewModel / ToolboxViewModel
src/OneBox.Plugins.Clipboard/      剪贴板小工具（独立进程，自带全部 UI）
src/OneBox.Plugins.Clipboard.Core/ 剪贴板的纯逻辑库（可单测）
src/OneBox.Plugins.QuickNote/      示例小工具，兼作模板
src/OneBox.Plugins.Screenshot/      截图小工具（区域/全屏/滚动截长图、贴图置顶、OCR、划选取字）
src/OneBox.Plugins.Screenshot.Core/ 截图的纯逻辑库（文件名、滚动拼接、文本整理、OCR 版面与选区，可单测）
tests/OneBox.Tests/                单元测试
```

宿主运行时目录（`%LOCALAPPDATA%\OneBox\`）：

```
plugins/                  用户安装的小工具
plugin-state/<id>/        每个小工具的私有数据（卸载时可选删除）
logs/host.log             宿主日志
logs/plugin-<id>.log      各小工具日志（含其 stdout/stderr）
plugins.json              启用状态、插件来源、崩溃统计
settings.json             宿主设置
```

## 技术要点

- **插件进程**：宿主以 `--pipe <name> --plugin-id <id>` 启动插件；插件连上命名管道后收到 `hello`（协议版本、数据目录），再收到 `start`
- **STA 安全**：SDK 内部一律 `ConfigureAwait(false)`，但插件的所有回调都通过捕获的同步上下文派发回 UI 线程——否则 WPF 控件会在非 STA 线程上创建
- **热键归属**：只有处于 `Running` 的插件才持有热键，停止/崩溃/禁用会立即让出组合键；注册采用整体重建，避免残留
- **热键应答要等注册结果**：`DeclareHotkeysAsync` 的应答必须携带真实注册结果。宿主若先回 `ok` 再异步注册，插件就永远收到空结果，也就无法提示"这个热键被占用了"——所以宿主先挂起该请求，等注册完成回填结果后再应答
- **内置插件不可卸载**：随宿主分发的插件只能禁用（删了下次启动会复原）；用户安装的同 id 插件优先于内置
- **不抢焦点**：`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`；搜索/文本选择时临时允许激活，失焦自动恢复并把前台归还目标应用
- **一键粘贴引擎**：`keybd_event` 注入 `Ctrl+V`（实测 `SendInput` 扫描码对 WPF 控件不生效），UIAutomation `ValuePattern` 兜底；注入后自检，Ctrl 卡键自动补发释放
- **热键注入防串键**：注入前先等修饰键全部松开（否则 `Ctrl+Alt+C` 里 Alt 残留会变成 `Ctrl+Alt+C` 而非复制）
- **剪贴板争用防护**：写入放后台专用 STA 线程串行队列；UI 线程异常拦截不退出
- **菜单分层不碰 WPF**：`TrayMenuBuilder` 只做数据整理（按插件分组、清洗分隔线），返回纯数据，由 `App.RefreshTrayMenu` 转成 WPF 菜单项——这样"分组/去重分隔线"的逻辑可以完整单测，不必去戳托盘菜单
- **每个插件的菜单项各自清洗**：插件原先是在扁平列表里声明分隔线的，用来和"别人的项"隔开。分层后每个插件独立成子菜单，这些边界分隔线就多余了，因此首尾分隔线一律去掉、连续分隔线折叠成一条
- **设置项声明的边界校验**：宿主传来的 JSON 可能是任何东西（手输、旧版本残留）。`ScreenshotSettingsSchema.Apply` 对每个键都做类型与范围校验，非法值一律忽略并保留原值——被乱改一轮后设置对象仍处于合法值域
- **数字输入失焦/回车才提交**：逐字提交会让插件收到一堆中间态（输入 `10` 时的 `1`），因此只在失焦或回车时下发，并夹取到声明范围
- **设置不双写**：宿主不保存插件设置的副本，只回传改动，由插件自己落地；否则"宿主存的"和"插件存的"迟早会不一致
- **插件停止即清空声明**：连接关闭（正常停止、崩溃、宿主退出）时清掉该插件的菜单与设置声明，避免设置中心残留一个点进去什么也改不了的旧表单

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

### 发布

```powershell
# 发布宿主 + 全部小工具到 ./publish
.\publish.ps1

# 指定输出目录
.\publish.ps1 -OutDir D:\dist\OneBox

# 只发布某个小工具；-FrameworkDependent 改为依赖框架发布（体积更小）
.\publish.ps1 -Plugins quicknote -FrameworkDependent
```

产物布局：`publish/OneBox.exe` 与 `publish/plugins/<id>/`（每个小工具的 exe 与 `plugin.json`）。新增小工具时，在 `publish.ps1` 顶部的 `$pluginProjects` 里加一行即可。

## 已知限制

- 目标软件以管理员运行时，需要 OneBox 也以管理员运行才能注入按键
- 全屏游戏/独占全屏可能拦截注入
- 同一个全局热键只能被一个程序注册；被其他软件占用时该热键不生效（工具箱的热键页会列出）
- 小工具需要与宿主协议版本一致（`protocolVersion`）；不兼容的插件会被标记为损坏而不会被启动

## License

[MIT](LICENSE)
