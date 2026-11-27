# DSH 全域语音输入（Windows 助手）

把 `dsh-voice-memory` 的识别、润色和记忆带到 **Windows 的任意应用**：浏览器、Word、微信、记事本、终端……光标在哪里，听写结果就输入到哪里。

它不是一个独立的大模型应用，而是 DSH 语音插件的**系统级伴侣**：

```
Ctrl+Alt+Space
   ↓
本机麦克风（winmm，16kHz）
   ↓  POST /voice-memory  local-transcribe
DSH 里的 SenseVoice（本机、离线）
   ↓  hot.txt + 学到的纠错
DSH 模型润色（可选）
   ↓
SendInput Unicode → 当前前台窗口的光标处
```

因此它复用了你已经装好的模型、热词、学习记忆和润色设置，不重复下载模型。

## 要求

- Windows 10/11（.NET Framework 4.8，系统自带；无需安装 .NET SDK）。
- **DeepSeek Harness 正在运行**，并且 `dsh-voice-memory` 已启用。助手会自动查找 DSH 的本地端口；也可以手动填写。
- 麦克风可用。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

生成 `bin\DSHVoiceSystem.exe`，双击运行即可；没有控制台窗口。

## 使用

1. 双击 `DSHVoiceSystem.exe`，托盘出现蓝色麦克风图标。
2. 在任意应用里把光标放到要输入的位置。
3. 按 **Ctrl+Alt+Space** 开始录音；再按一次结束。
4. 状态条显示“录音中 → 转写中 → 已输入”；文字会直接打到当前光标处。
5. 右键托盘图标可以：
   - 开始/结束听写
   - 设置 DSH 地址、润色开关、最长录音时长
   - **记住上一条修正…**：把这次识别原文和你实际想要的内容发给 DSH，学成下一次的纠错
   - 开机启动（写入当前用户的 Run 键）
   - 打开日志、退出

## 它与 DSH 内插件的关系

- DSH 内插件负责“在 DSH 输入框里听写 + 从发送前修改中自动学习”。
- 本助手负责“在系统任意窗口里听写”，并复用同一套 ASR/热词/已学纠错/润色。
- 系统全域听写无法自动观察你在其他软件里发送后的修改，所以用托盘里的 **记住上一条修正…** 显式学习。
- 使用 `memory-learn` 路由把修正写回 `$DSH_HOME/voice/memory.json`。

## 已知限制

- DSH 没运行或插件没启用时无法识别；助手会在状态条/托盘提示。
- 使用 Unicode `SendInput` 直接输入，不占用剪贴板；如果目标窗口以管理员权限运行，而助手不是管理员，Windows 的 UIPI 会拒绝输入。此时以管理员身份运行助手即可。
- 极少数游戏/自绘控件不接受 `SendInput`，需要单独的输入法级方案。
- 没有做开机自启安装包；用托盘菜单勾选“开机启动”。

## 文件

- `src/Recorder.cs`：winmm 16kHz 录音、重采样、静音判断。
- `src/DshClient.cs`：自动发现 DSH 端口、调用 `/voice-memory`。
- `src/Injector.cs`：Unicode SendInput 输入。
- `src/TrayContext.cs`、`src/OverlayForm.cs`、`src/HotkeyWindow.cs`、`src/SettingsForm.cs`：托盘、状态条、全局热键、设置。
- `build.ps1`：用系统自带 C# 编译器构建，无外部依赖。
