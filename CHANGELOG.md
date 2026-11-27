# Changelog

## 1.0.2

- 新增 `memory-learn` 路由：系统全域听写无法经过 `agent/pre-step`，托盘助手用显式的“记住上一条修正…”把 raw/final 写入同一份 `memory.json`。
- Windows 全域听写伴侣（`system/`）：全局热键、托盘、状态条、Unicode SendInput、复用 DSH 的 SenseVoice/润色/记忆。

## 1.0.1

- 修复：在已有草稿中间用语音补充内容时，光标被预览/整段替换推到末尾、补充内容只出现在末尾。
  - 录音开始时捕获光标位置，最终文本用 `inputActions.insertText(text, span)` 插入到该位置；
  - 草稿非空时关闭会整段替换草稿的实时预览，避免预览把光标推到末尾；
  - 点击麦克风时阻止默认焦点切换，保留输入框里的选区；
  - 若录音期间草稿被改动（span 失效），退回当前光标插入，不再追加到草稿末尾。

## 1.0.0

- 首个公开版本：本地 SenseVoice 中文/中英混说识别。
- 默认开启的意图理解与智能润色（复用 DSH 已配置模型）。
- 从发送前修改中学习纠错与表达偏好；记忆保存在 `$DSH_HOME/voice/memory.json`。
- 设置页提供引擎、麦克风、热键、润色、热词表与学习记忆管理。
- 基于 MIT 许可的 dsh-voice-scribe 的本地 ASR/录音/设置框架，保留原版权声明。
