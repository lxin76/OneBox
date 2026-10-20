# 受控滚动集成测试

只使用测试进程创建的 WPF 窗口，不访问浏览器或用户页面。属于截图插件专用验收工具。

## 构建

从 OneBox 目录执行：

```powershell
dotnet build tools/scroll-integration/ScrollIntegration.csproj --nologo
```

## 运行边界

必须先确认没有并行 GUI 操作。默认运行不弹窗、不触发输入；真实测试需要显式参数：

```powershell
dotnet run --no-build --project tools/scroll-integration/ScrollIntegration.csproj -- --run-controlled-input
```

启动仅激活自家测试窗口，并一次定位光标。运行期间不要操作鼠标/键盘；移动光标、切走前台、按修饰键/鼠标或 Esc 都会停止且不重新抢回，不恢复光标。前台检查和 SendInput 无法原子化，因此仍有系统级竞态窗口。不会激活任何其他用户窗口。

## 产物与判定

每次运行写入被忽略的 `artifacts/yyyyMMdd-HHmmss/`：`window-preview.png`、`groundtruth.png`、`frame-NNN.png`、`stitched.png`、`metadata.json`。元数据记录滚轮计数、逐帧 VerticalOffset、DispatcherTimer tick、最大调度间隔、控制器结果、配准进度和退出原因。

Fixture 为 25 个唯一编号段落、2500 DIP 正文，左侧白边、非重复宽度条带、右侧唯一段落色条，最后为 END-024。Groundtruth 由独立同尺寸 Fixture 的 RenderTargetBitmap 产生；与屏幕渲染的字体抗锯齿可能不同，像素 MAE 仅作诊断。自动校验输出物理高度误差不超过 2px、实际滚动到底、编号色条按 0..24 恰好一次且顺序一致，以及捕获期间 timer 持续触发；色条不是 OCR，不伪称已识别文字。两张整页图仍需视觉比较 END 与漏行/重复。

控制器原本保守的 IsComplete 不被测试改写。首版严格配准拒绝会以非零退出和部分图如实保留，不绕过算法。退出码：0 独立 fixture 检查通过，1 结果不完整或用户接管，2 启动/运行异常。
