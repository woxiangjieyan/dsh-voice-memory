# 语音记忆输入

一个面向 DeepSeek Harness 的本地语音输入插件：说话 → 转写 → 自动润色 → 发送，并且**记住你对识别结果的修改**。

## 它能做什么

- **本地中文识别**：使用 SenseVoice（ONNX，CPU）在本机转写，音频不出本机；支持中文、英文和中英混说，模型与官方语音输入共用 `$DSH_HOME/voice/sensevoice`。
- **自动润色（默认开启）**：复用 DSH 已配置的模型，先理解意图，再清理口头禅、理顺语序、补标点、修正同音错字；失败时保留原文。
- **越用越准的记忆**：
  - 宿主在 `agent/pre-step` 读取你真正发送的消息，与刚才的转写做 token 级对齐；
  - 你对识别结果的每一处修改（例如 `deep sick` → `DeepSeek`、`迪普西克` → `DeepSeek`）会写入 `$DSH_HOME/voice/memory.json`；
  - 下一次转写先应用这些纠错，再交给润色，所以同一个发音习惯第二次就能识别正确；
  - 单字纠错需要重复出现两次才自动应用，避免把一个字改坏；
  - 开启润色时，你的进一步修改学习为“表达偏好”，只用于润色提示词，不会拿去替换原文。
- **可控**：设置 → 语音记忆输入里可以看到学到的纠错、逐条「忘记」或一键清空；所有数据都在本机。
- **两种输入方式**：点输入框右侧麦克风，或按 Alt（可自定义组合键、可按住说话）。

## 安装

**从插件市场（推荐）**：在 DSH 的 **Plugins → Discover** 里添加插件索引，然后搜索「语音记忆输入」或 `dsh-voice-memory`：

- dsh.so 社区市场索引：`https://cdn.dsh.so/market/index.json`
- 本插件在索引中的条目由 `https://github.com/woxiangjieyan/dsh-voice-memory` 提供。

**从 GitHub 直接安装**：Plugins → Add plugin，填入：

```
git+https://github.com/woxiangjieyan/dsh-voice-memory.git
```

安装后刷新页面；首次使用本地模型约 230MB（若已用过官方语音输入，会复用同一份模型）。

## 使用

1. 点输入框右侧麦克风，或按 Alt 说话，再点/再按结束；文字进入输入框。
2. 直接在输入框里改正识别错误，然后正常发送。
3. 下一次说同样的内容时，插件会先应用学到的纠错。

> 本插件与官方 **Voice Input** 可以共存（两个麦克风按钮）。只想保留一个时，在 Plugins 里关闭官方 Voice Input 即可。

## 记忆文件

`%USERPROFILE%\.dsh\voice\memory.json`（即 `$DSH_HOME/voice/memory.json`），纯文本 JSON，可备份、可删除。删除后重新开始学习。

## 已知限制

- 学习发生在“发送”这一刻；如果你改了识别结果但清空输入框没有发送，这次修改不会被学习。
- 草稿里含 `@` 引用芯片时，写入走整段替换，会把芯片展开为纯文本；先发送或清空草稿再听写可避免。
- 本地识别首次使用需要模型（约 230MB）。本机已有官方语音输入的模型时无需重新下载。
- 云端识别与浏览器 Web Speech 仍可在设置里选择，但默认的本地引擎才是隐私与稳定性的推荐路径。

## 来源与许可

本插件基于 MIT 许可的 [dsh-voice-scribe](https://github.com/PensiveFei/dsh-voice-scribe)（作者 PensiveFei）的本地 ASR、录音与设置框架，保留其 MIT 许可与版权声明；新增了记忆层（`lib/memory.js`）、`agent/pre-step` 学习钩子、记忆设置面板与 `/voice-memory` 路由。
