# 快速便签 · OneBox 内置小工具

**热键呼出一个置顶便签窗，内容自动保存。** 它同时是编写新小工具的模板——结构最小，却用到了插件平台的全部核心能力。

- 插件 id：`quicknote`
- 数据目录：`data/plugin-state/quicknote/`
- 源码：`src/OneBox.Plugins.QuickNote/`
- 平台与接口说明见[平台 README](../../README.md)

## 功能

- **`Ctrl+Alt+N` 呼出/隐藏**一个置顶便签窗（`Topmost="True"`，380×300，深色底）
- **内容自动保存**：每次文本变化都写入插件私有设置，关闭窗口或插件退出前再存一次
- **清空便签**：托盘子菜单里的「清空便签」一键清掉并同步落盘

## 托盘菜单

本插件在托盘右键菜单里占一个「快速便签」子菜单：

| 菜单项 | 行为 |
|---|---|
| 显示便签 | 显示/隐藏便签窗（等同热键） |
| 清空便签 | 清空内容并保存 |

## 数据文件

| 文件 | 内容 |
|---|---|
| `settings.json` | 便签正文（键 `note`），由宿主读写 |

宿主运行日志在 `data/logs/plugin-quicknote.log`。

## 作为模板：它演示了什么

这个插件刻意保持最小，把平台的每个能力各用一次，照搬即可写新插件：

| 能力 | 代码位置 |
|---|---|
| 连接宿主并运行主体 | `Program.cs`：`OneBoxPlugin.RunAsync(args, (context, ct) => …)` |
| 声明全局热键并检查注册结果 | `QuickNotePlugin.StartAsync` → `DeclareHotkeysAsync` |
| 声明托盘菜单项 | `QuickNotePlugin.StartAsync` → `DeclareTrayMenuAsync` |
| 接收热键/菜单事件（自动回到 UI 线程） | `HotkeyPressed` / `MenuInvoked` |
| 读写插件私有设置 | `GetSettingAsync` / `SetSettingAsync` |
| 上报状态与日志 | `ReadyAsync` / `LogInfoAsync` |
| 停止时收尾 | `DisposeAsync` 中保存并关闭窗口 |

## 实现要点

- **事件回 UI 线程**：SDK 内部一律 `ConfigureAwait(false)`，但 `HotkeyPressed`/`MenuInvoked` 已由 `PluginContext` 派发回捕获的同步上下文，因此回调里可以直接碰 WPF 控件；`QuickNotePlugin` 仍用 `Dispatcher.BeginInvoke` 兜一层
- **避免回填触发保存**：`NoteWindow.NoteText` 的 setter 用 `_loading` 标志抑制 `TextChanged`，否则程序化回填会立刻触发一次保存
- **设置变更节流**：`TextChanged` 即调用 `SetSettingAsync`，由宿主的设置存储落地；`DisposeAsync` 里再保存一次兜底

## 相关源码

```
src/OneBox.Plugins.QuickNote/
  Program.cs           进程入口（STA + OneBoxPlugin.RunAsync）
  QuickNotePlugin.cs   插件主体：热键/菜单声明、设置读写、窗口管理
  NoteWindow.xaml(.cs) 便签窗口
  Assets/app.ico       图标（由 tools/icons/gen_icons.py 生成）
  plugin.json          清单
```

图标有两处用途：项目里的 `ApplicationIcon` 让 exe 带上图标（宿主据此在工具箱列表、设置中心与托盘子菜单里显示），窗口则通过 `Icon="pack://application:,,,/Assets/app.ico"` 引用同一枚资源。
