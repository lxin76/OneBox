# 交接：OneBox 截图插件「滚动截长图」失效

> 2026-10-20：功能未修好之前，插件里的入口已统一标注为「暂不可用」——热键不注册、
> 托盘菜单项置灰、设置页与设置中心的滚动参数改为只读。开关只有一处：
> `ScreenshotSettingsSchema.ScrollCaptureAvailable`（目前为 `false`），修好后改成 `true` 即恢复全部入口。

> 复制下面整段（从「你是接手…」到文末）作为新会话的第一条消息即可。

---

你是接手 OneBox 截图插件「滚动截长图」功能排查的工程师。此前已经投入约 20 轮修改仍未修好，**请先读完本文再动手，不要重复已经试错过的路径**。

## 一、项目与环境

- 仓库：`D:\private_proj\deepseek-harness\OneBox`（注意：真实路径是 `D:\private_proj\deepseek-runtime\OneBox`，工作区里的是**目录联接**）
- 解决方案：`OneBox.slnx`，.NET 8 / WPF / Windows
- 截图插件：`src\OneBox.Plugins.Screenshot`（WPF），纯逻辑在 `src\OneBox.Plugins.Screenshot.Core`
- 测试：`tests\OneBox.Tests`（xUnit）
- 发布脚本：`publish-framework-dependent.ps1`，产物在 `publish\framework-dependent`
- 插件的诊断日志（**排查主依据**）：`publish\framework-dependent\data\plugin-state\screenshot\screenshot.log`
- 进程日志：`publish\framework-dependent\data\logs\plugin-screenshot.log`

### 已知环境问题（会影响你）

1. `dotnet build OneBox.slnx` 与 `dotnet build <单个 csproj>` 在本机 **restore 阶段就失败**，且不打印任何错误（`-v d` 下显示 `_FilterRestoreGraphProjectInputItems` 失败）。**不要在这上面浪费时间**，改用下面的本地编译法。
2. 可行的本地编译法（此前一直用）：用 SDK 自带 Roslyn 直接编译
   - `C:\Program Files\dotnet\sdk\10.0.402\Roslyn\bincore\csc.dll`，通过 `dotnet exec csc.dll @响应文件` 调用
   - 引用：`packs\Microsoft.NETCore.App.Ref\8.0.32\ref\net8.0` + `packs\Microsoft.WindowsDesktop.App.Ref\8.0.32\ref\net8.0`
   - 注意：WPF 引用程序集与运行时实现程序集**不能混用**，否则报 `CS7069 DependencyObject`。验证 Core 逻辑时用 NETCore 引用集即可；验证插件服务层需要 WPF 运行时程序集并放在引用列表最前。
3. `Path.GetFullPath` 在 .NET Framework 下可能受环境变量影响，写临时文件**一律用绝对路径**。

## 二、用户诉求（唯一目标）

按热键 → 框选区域 → 程序**自动向下滚动**并把每一屏拼成长图 → 出图。

## 三、当前状态（2026-10-19）

**功能仍然不可用：页面完全不滚动，只截到首屏。**

### 最新一次实测日志（09:11，最有价值）

```
09:11:41.185 滚动截图开始：选区 2560,96 1920×1032 光标=3520,612 选区窗口=0x30D76 进程=42696 目标窗口=0x30D64 初始滚轮=360
09:11:41.414 滚轮落点：光标=3520,612 窗口=0x30D76 根窗口=0x30D64 目标=0x30D64 ✓
09:11:41.444 滚轮注入：SendInput 成功（INPUT 结构 40 字节，每格 360）
09:11:42.066 滚动帧 #1: 画面仍无变化（第 2 次）
09:11:42.716 滚动帧 #1: 画面仍无变化（第 3 次）
09:11:43.384 滚动帧 #1: 画面仍无变化（第 4 次）
09:11:43.635 截图失败: System.InvalidOperationException: 此 Visual 未连接到 PresentationSource。
```

**这组数据说明：**

- 光标落点 ✓（在目标窗口内）
- `SendInput` 返回成功
- **但页面一个像素都没动**

即：输入注入的“形式”全对，页面就是不响应。

### 另一次实测（21:44，腾讯视频首页，Edge）

```
滚轮注入：SendInput 成功（INPUT 结构 40 字节，每格 360）
滚动帧 #2: NoOverlap 匹配=0/0 位置=-1 滚动=0
```

用户已确认：**该页面上用鼠标滚轮可以正常滚动**（对照实验结论为 A）。

## 四、已经试过并失败的方案（不要重复）

| # | 方案 | 结果 | 放弃原因 |
|---|---|---|---|
| 1 | 从目标窗口 DC 取图（GetWindowDC + BitBlt） | 花屏 | 自绘窗口成黑底、透出桌面 |
| 2 | `PostMessage(WM_MOUSEWHEEL)` 定向投递 | 无效 | Chromium 不处理投递的滚轮（有公开 issue 佐证） |
| 3 | `SetForegroundWindow` 抢前台 | 无效且有害 | 系统驳回后台进程抢前台；还误把最小化窗口提起来 |
| 4 | `AttachThreadInput` + `SetForegroundWindow` 合法激活 | 激活**成功**（无警告），页面仍不动 | 前台正确但滚轮不生效 |
| 5 | `mouse_event` 发滚轮 | 无效 | 老接口，受输入桌面限制 |
| 6 | `SendInput` + 修正 `INPUT` 结构为 40 字节显式布局 | **已修好结构 bug**，返回成功，页面仍不动 | 注入被接受，但页面不响应 |
| 7 | 每次轮询挪动进度条窗口再截图 | 造成闪烁、稳定判定失败 | 已改为整段 park/unpark |

### 关于 `SendInput` 的结论

`INPUT` 结构此前写成 `uint type; MOUSEINPUT mi;`（x64 下 sizeof=36），导致 `SendInput` 返回 0。**已修复**为：

```csharp
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct INPUT
{
    [FieldOffset(0)] public uint type;
    [FieldOffset(8)] public MOUSEINPUT mi;
}
```

现在返回成功，**但页面仍不滚动**。这是当前卡住的地方。

## 五、待你验证的关键假设（按优先级）

1. **目标窗口/子窗口层级问题**：`_targetWindow` 是顶层窗口（`GetAncestor(GA_ROOT)`）。Chromium 的内容区通常是**子窗口**，滚轮必须落到子窗口才生效。建议用 `WindowFromPoint` 取 `(x,y)` 处的**直接子窗口**（不要取 root），再对它发送/投递滚轮。
2. **UIPI / 完整性级别**：若 Edge 以更高完整性运行（或存在该目录下的防护软件），注入会被静默丢弃。可查 `GetLastError`、或用 `ChangeWindowMessageFilterEx`。
3. **前台窗口的输入队列归属**：`AttachThreadInput` 只在激活瞬间有效，解挂后发送滚轮时归属可能已失效。可尝试**保持挂接状态**直接发送滚轮，发送完再解挂。
4. **参考更成熟的实现**：ShareX 的 `ScrollingCaptureManager`（GPL v3，**只能参考思路、不要复制代码**）用的是：先 `selectedWindow.Activate()` → 可选 `SendMessage(WM_VSCROLL, SB_TOP)` 回顶 → 循环里 `SendInput` 滚轮 → 抓图 → 行比对拼接。它还支持 `ScrollMethod` 切换（滚轮 / 下方向键 / PageDown / `WM_VSCROLL` 消息）。**建议优先试 `SendMessage(WM_VSCROLL)` 或 `PageDown` 按键**，绕开滚轮注入的坑。

## 六、拼接器（已单独修好，不要再动）

`src\OneBox.Plugins.Screenshot.Core\ScrollStitcher.cs` 已按“逐行后缀匹配”重写（参考 ShareX 思路、自行实现），**12 条 xUnit 用例全绿**，另有 10 组不同帧高/步长/页高的压力用例逐行一致。

算法要点：探针 = 帧顶部一小段（长度从长到短梯度尝试，必须短于重叠区）→ 在结果里定位 → 沿命中处继续向下比对测出**真实重叠长度** → 从重叠之后追加新内容。

**拼接器不是当前故障点**，问题在“页面根本没滚动”。

## 七、代码版图（改过的文件）

| 文件 | 作用 |
|---|---|
| `Services\ScrollCaptureSession.cs` | 滚动会话：激活窗口、发滚轮、抓帧、驱动拼接器。**主要排查对象** |
| `Services\Native.cs` | Win32 P/Invoke（含 `SendInput`/`INPUT`/`AttachThreadInput`） |
| `Core\ScrollStitcher.cs` | 逐行后缀匹配拼接（已完成，12/12 通过） |
| `ScreenshotPlugin.cs` | 流程编排：选区 → 滚动会话 → 贴图 |
| `Windows\ScrollControlWindow.xaml.cs` | 进度条窗口，含 `ParkForCapture`（截图时挪走） |
| `Windows\RegionSelectorWindow.xaml.cs` | 选区遮罩，产出 `ResultWindowHandle` |
| `Services\ScreenCapture.cs` | 屏幕抓图（已回退为原始实现，勿改） |

### 另一个待修的小问题

最新日志末尾：

```
截图失败: System.InvalidOperationException: 此 Visual 未连接到 PresentationSource。
  at OneBox.Plugins.Screenshot.ScreenshotPlugin.ShowPin(...)
  at OneBox.Plugins.Screenshot.ScreenshotPlugin.RunScrollCaptureAsync(...)
```

滚动会话跑在**界面线程**（为了让进度条窗口能挪动），结束后 `ShowPin` 创建贴图窗口时报此错。与主问题独立，但也要修（可考虑用 `Dispatcher.BeginInvoke` 延迟到下一个消息循环再开贴图窗）。

## 八、给接手者的建议

1. **先别改拼接器**，它已经用测试锁死了。
2. **把“输入是否送达”作为唯一突破口**：在 `ScrollCaptureSession.SendWheel` 旁边做对照实验——同一位置分别用 `SendInput` / `PostMessage(WM_MOUSEWHEEL)` / `SendMessage(WM_VSCROLL, SB_LINEDOWN)` / `SendInput` 发 `PageDown` 键，看哪种能让页面滚动。**这一步能直接定位方案**。
3. 每次改动后跑：`dotnet test tests\OneBox.Tests`（若 restore 失败，用第六节所述的 csc 直编法跑 `ScrollStitcherTests`）。
4. **不要在没有本地验证的情况下声称“修好了”**——前几轮反复出现这个问题，浪费了大量用户时间。

## 九、给用户的话（口径）

功能尚未可用。当前卡点已精确定位为「滚轮注入被系统接受但目标页面不响应」，且已排除前台、落点、结构对齐这三个常见原因。下一步将改为测试其他滚动指令（`WM_VSCROLL`/`PageDown`/子窗口定向发送）来绕开该限制。

---

## 附：验证清单（接手后照着跑）

- [ ] 读 `publish\framework-dependent\data\plugin-state\screenshot\screenshot.log` 最新段落
- [ ] 确认拼接器测试仍全绿
- [ ] 做第八节第 2 条的对照实验，记录哪种方式能滚动
- [ ] 修 `ShowPin` 的 PresentationSource 异常
- [ ] 修好后再让用户实测，并附上日志片段

---

# 十、2026-10-19 实测结论与已完成的修改（本轮）

> 本节由接手者在完成对照实验后补写。**第九节与附录的结论已被本节推翻。**

## 10.1 对照实验（真实 Edge / Chromium 页面，测试页为连续彩色条带）

做法：把页面滚到中部，每种方式测试前先用真实 Ctrl+Home 回到顶部并重新抓基线，
每次只发一种指令，比对前后画面差异；另加一组“什么都不做”的对照，确认测量本身没有假阳性。

| 投递方式 | 画面平均差异 | 结论 |
|---|---|---|
| SendInput 滚轮（3 格） | 40 | 有效 |
| SendInput 滚轮（1 格） | 4 | 有效（幅度小） |
| SendInput PageDown | 42 | 有效 |
| PostMessage(WM_MOUSEWHEEL) | 0 | 无效 |
| PostMessage(WM_VSCROLL, SB_PAGEDOWN) | 0 | 无效 |
| 【对照】什么都不做 | 0 | 证明测量有效 |

结论落在顺序上：真实滚轮与 PageDown 有效，两个 PostMessage 路径对 Chromium 页面无效。
顺序定为 WheelInput → PageDown → WheelMessage → VScroll，前两个优先，后两个只作老式窗口兜底。

## 10.2 被推翻的旧结论

- 第九节说「滚轮注入被系统接受但目标页面不响应」—— 不成立。滚轮本身有效，
  之前“画面零变化”的真正原因是测试页面处于不可滚动或未获得焦点的状态，
  以及采样点落在纯色区域导致测量失灵（见 10.4）。
- 第八节第 2 条要求做对照实验 —— 已完成，结果即 10.1 的表。
- 第七节「另一个待修的小问题」ShowPin 的 PresentationSource 异常 —— 已修，见 10.5。

## 10.3 滚动会话的改动

Services\ScrollCaptureSession.cs

1. 投递对象改为光标下最深的可见子窗口（ScrollInjector.HitTestFeedbackWindow），
   不再一律投给 GetAncestor(GA_ROOT) 的顶层窗口。浏览器页面挂在渲染子窗口上，
   顶层窗口收到消息只会交给非客户区。
2. 投递方式失败即换路：画面一整帧没变时，不再只会加大滚轮量（问题不在“量”上），
   而是用 ScrollMethodPlan 换下一种方式并立刻重试。全部试完才按“已到底”处理。
3. 换路前重新补齐前提：重新命中投递窗口、把光标放回选区中心、必要时重新激活前台。
4. 记录起始滚动位置，流程结束（含异常、取消）时还原，避免页面停在最底部。
5. 所有消息类投递一律 PostMessage，绝不用 SendMessage —— 对方卡在嵌套消息循环时会把本进程挂死。

新增文件：

- Services\ScrollInjector.cs：命中测试、四种投递方式的实现、滚动条位置读取。
- Core\ScrollMethodPlan.cs：投递方式的顺序与轮换决策（纯逻辑，可单测）。
- tests\OneBox.Tests\ScrollMethodPlanTests.cs：8 条用例锁死顺序与轮换行为。

## 10.4 排查方法的教训（下次务必避开）

- 采样区必须验证有内容：本轮一度得出“所有方式都无效”的错误结论，
  实际是采样点落在纯白区域（行方差 = 0），任何滚动都测不出来。
  务必同时打印基线亮度与方差，并加一组“什么都不做”的对照。
- PostMessage(WM_KEYDOWN) 对 Chromium 内容区无效：回顶必须用 SendInput 发真实 Ctrl+Home。
- 路径陷阱：D:\private_proj\deepseek-harness 是指向 deepseek-runtime 的链接，
  而 deepseek-runtime 本身不是有效入口。一切操作都用 deepseek-harness 这一侧；
  Edge 打开本地文件也必须用 deepseek-harness 路径，否则会 ERR_FILE_NOT_FOUND。
- 控制台代码页：本机 chcp 为 936 而输出按 UTF-8 编码，经过管道会出现中文乱码与行首标签串位。
  看到“路径不存在”之类矛盾输出时，先用 .NET API（DirectoryInfo.Exists）复核，
  不要相信 PowerShell 的字符串回显。

## 10.5 ShowPin 的 PresentationSource 异常

Windows\PinWindow.xaml.cs：窗口首次 Show 时 WPF 先挂根视觉、再设 DPI，
这一刻 OnDpiChanged 就会触发，而 PresentationSource 尚未接上，
PointToScreen 直接抛 InvalidOperationException，把整个截图流程炸掉。
已在 PhysicalTopLeft() 中判断 PresentationSource.FromVisual 是否可用，
并捕获该异常回退到布局坐标。

## 10.6 验收状态

- dotnet build OneBox.slnx：0 警告 0 错误
- dotnet test OneBox.slnx：487 个测试全部通过（含新增 8 条）
- 插件已发布到 publish\framework-dependent\plugins\screenshot，产物含新代码
- 仍需用户实机确认：按热键框选浏览器页面，确认长图确实接出了多屏内容。
  若仍只有一屏，请把 screenshot.log 里“滚动截图开始”到结束的整段发回 ——
  现在日志会记录每种投递方式的尝试顺序与画面是否变化，能直接定位卡在哪一步。

---

# 十一、2026-10-19 第二轮：拼接重复 / 未滚到底 / 进度条入镜

## 11.1 拼接器：重叠定位彻底重写

用户实测《线边仓WMS通用对接说明》后反馈两点：长图内容重复、变得超长；后来又出现只截一屏就结束。
两者是同一个算法的两次退化。

**(a) 重复与超长的原因**：原实现拿“帧顶部一小段探针”到结果里搜位置。
这份文档恰好包含大量**完全相同的 JSON 代码块与表格**：实测长图里重复间距 618px 出现 419 次、828px 出现 125 次。
探针在这些重复块上会有多个同样好的命中，选错就导致重叠被低估 → 整段重复追加 → 长图成倍变长。
反向的错法（选到靠后的块）则会让重叠被高估 → 内容被吞掉 → 长图反而变短。两种症状都真实出现过。

**(b) 只截一屏的原因**：重写后引入的合格线计算写错了：

    var requiredRows = Math.Max(8, Math.Min(probeSize, frameHeight - bestOverlap));

当重叠只有 114 行、探针上限 200 时，合格线被抬到 200，而最多只能比对 114 行，
于是永远不达标，每一帧都报 NoOverlap、匹配=0/0，功能退化成只截首屏。
改为以**实际参与比对的行数**作为合格线上限。

**(c) 现在的算法**：不再“找位置”，改成唯一可靠的几何约束——
**新帧顶部 k 行必须等于结果末尾 k 行**。对每个候选重叠长度 k 直接验证这条约束并打分，
取最吻合的 k（含精修与唯一性检查）；重叠区内允许跳过零星脏行（代码块边框、光标行），
但连续脏行超过 4 行即判定到达新旧分界。有歧义时选**最小**重叠，宁可少量重复也绝不丢内容。

## 11.2 进度条被截进长图

两个独立原因：

1. **只改 Left/Top 不生效**：整个抓帧循环在界面线程上同步阻塞执行，
   这期间 WPF 消息泵不转，`Left/Top` 只是改属性，真正移动 HWND 要靠消息泵处理 WM_WINDOWPOSCHANGED。
   泵不转，窗口就停在原地，每帧都把它原样截进长图。
   改为 `ShowWindow(SW_HIDE)` / `SW_SHOWNOACTIVATE`，同步生效、不依赖消息泵。
2. **首帧根本没隐藏过**：控制条在会话开始前就 Show()，而首帧直接 `GrabFrame`，
   没走“先隐藏再抓”的路径，于是长图最上面就带上了这块浮窗。改为首帧也走 `CaptureSettledFrame`。

另外把抓图前的等待从固定 Sleep 改成 `DwmFlush()`：
隐藏窗口后必须等 DWM 把底层内容重新合成出来，否则会抓到“窗口刚消失、底色还没补上”的中间帧。

## 11.3 本轮新增的测试

`tests\OneBox.Tests\ScrollStitcherTests.cs` 新增并通过：

- `真实尺寸下_每帧重叠等于帧高减前进量`（1032/918/114，直接取自实机日志）
- `重叠区内零星不匹配行_不应截断重叠扩展`（复现 918→992 的错位）
- `诊断_周期性重复页面连续滚动`（模拟本文档的重复结构）
- `诊断_连续九帧真实参数`（多帧连续滚动）
- `页面含重复结构时_仍按正确位置衔接`
- `结果高与帧高相同时_小幅重叠仍能正确拼接`（复现 11.1(b) 的只截一屏）

全量：`dotnet build OneBox.slnx` 零警告，`dotnet test OneBox.slnx` 493 项全部通过。

## 11.4 排查方法教训（补充）

- **合成数据全部通过，不等于真实数据通过**：上一轮 10 个合成用例全绿，实机仍然全帧 NoOverlap。
  教训是：必须用**实机日志里的确切数字**建用例（帧高、前进量、结果高都要一致），
  而且要覆盖“结果高 == 帧高”“重叠远小于探针上限”这类边界。
- **不要只看日志判断“是否滚动”**：`NoOverlap` 表示拼接失败，不代表页面没滚。
  现在日志会额外输出“画面已变化但找不到重叠区（匹配 x/y，帧高 h，总高 H）”，能区分这两种情况。
- **选区要避开浏览器界面**：用户上一次选区 2560,96 1920×1032 把标签栏、地址栏一起框了进去，
  长图顶部因此出现浏览器 UI。截图时应从网页内容区开始框选。

---

# 十二、2026-10-19 第三轮：拼接“找不到重叠区”、长图只有第一屏

## 12.1 现象

进度浮窗已不出现（第十一节的 ShowWindow 修复生效），页面也确实在滚动，
但长图只有第一屏。日志每一帧都是：

    滚动帧 #2: NoOverlap ... 匹配=0/0 位置=-1
    滚动帧 #2: 画面已变化但找不到重叠区（匹配 0/0，帧高 1032，总高 1032）

## 12.2 根因：判据用“连续脏行”一刀切，而真实页面天然有大量脏行

取了用户上一轮那张 1920×8341 长图，逐行量真实的行剖面差异，得到：

| 场景 | 平均 Distance | 超过容差 12 的行数 | **最长连续超过容差** |
|---|---|---|---|
| 内容**相同**的两段（行 720 与行 1956） | 6.87 | 21 / 120 | **21 行** |
| 内容**不同**的相邻两行（720 与 721） | 4.32 | — | — |

两件事同时成立：

1. 内容完全相同的地方也有**连续 21 行**超出容差（代码块长行、抗锯齿、行内滚动条都会拉大差异）；
2. 内容不同的地方平均差异（4.32）反而**低于**内容相同时（6.87）。

而代码里写的是“连续 4 行对不上就 return 0”。于是正确的衔接点被直接判死，
每一帧都报 NoOverlap，拼接退化成只截首屏。

**结论：逐行绝对阈值 + 连续计数这类判据，在真实渲染结果上不可用。**

## 12.3 修复

1. 去掉“连续脏行就归零”，改为**按吻合比例打分**，由候选之间比高低，而不是用绝对阈值一刀切。
2. 排序指标从**吻合行数**改为**吻合率**。
   用吻合行数会让大重叠天然占优：414 行里的 60%（249 行）会压过 114 行的 100%（114 行），
   结果每帧都误判成大重叠、新内容被大量吞掉（实测周期性页面每帧只追加 618 行）。
3. 吻合率接近（歧义）时，按“**新内容是否真的新**”挑选：
   重叠 k 行之后的那段不应在已拼接结果里出现过；若出现过说明 k 取小了，应换更大的 k。

合格线 = 比对行数 × `OverlapMatchRatio`（0.6），下限 8 行。
0.6 的来历：真实长图里内容相同两段的逐行吻合率约 80%，留足余量。

## 12.4 用真实数据做的验收

把真实长图的行剖面喂进与 C# 同构的打分逻辑（帧起点 918、结果 0..1031、真值 k=114）：

    k= 114  matched=114  ratio=1.000   ← 正确值，完美吻合
    k= 120  matched= 91  ratio=0.758
    k= 414  matched=125  ratio=0.625
    k= 200  matched=  9  ratio=0.045

再叠加模拟重绘噪声（σ = 2 / 5 / 8 / 12），四种强度**全部稳定选中 k=114**。

## 12.5 一个测试用例被证明不可判定

上一轮写的“整页每 300 行逐像素完全重复”用例，在数学上**无法区分相邻周期**
（414 − 114 = 300 恰好一个周期，两个候选的吻合率完全相同）。
这不是算法缺陷，是用例构造不符合真实文档。
已替换为“局部重复 + 其余部分各不相同”的版本，符合本文档的真实形态。

## 12.6 验收状态

- `dotnet build OneBox.slnx`：0 警告 0 错误
- `dotnet test OneBox.slnx`：493 项全部通过
- 插件已发布到 `publish\framework-dependent\plugins\screenshot`
- **仍待实机确认**：需用户重新截图验证长图是否接出完整内容

## 12.7 下一次若仍失败，优先看这三条

1. 日志里“匹配 x/y”：x/y 是否接近 1？接近 0 说明吻合率判据仍不适用，
   应考虑把 `SimilarityTolerance`（当前 12）调大，或改用分块比较而非整行均值。
2. “重叠”与“帧高 - 前进量”是否相等？不等说明选错了候选，检查唯一性/新颖性判据。
3. 总高是否随帧数线性增长且与页面实际高度相符？
   若线性增长但内容重复，说明重叠被低估；若几乎不增长，说明重叠被高估。
