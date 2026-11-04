# GitHub 同类项目调研

日期：2026-11-04

## 1. 调研问题

在动手前确认三件事：

1. 是否已有开源项目直接满足“只读监听多个本地 Agent 的会话完成情况 + 收件箱式已读/完成管理”；
2. 如果没有，哪些项目的实现方式、数据格式处理或交互设计可以借鉴；
3. 许可证是否允许直接借用代码。

## 2. 候选项目

| 项目 | 形态/技术栈 | 解决的问题 | 与我们的差异 | 许可证 |
|---|---|---|---|---|
| [Claude Squad](https://github.com/smtg-ai/claude-squad) | Go/TUI，tmux + git worktree | 在独立 worktree 中同时跑 Claude Code、Codex、Gemini、Aider，统一管理实例 | 它负责启动和托管 Agent，不是旁路监听；无已读/完成收件箱 | AGPL-3.0 |
| [Clauditor](https://github.com/huylq98/clauditor) | Tauri 2 + Rust + React | 一个窗口内多开 Claude Code，实时状态（运行/等权限/等回复/空闲）、托盘、通知、会话状态机 | 只支持 Claude Code，且自己 spawn PTY；不做跨 Agent 的只读汇总 | MIT |
| [CodeHerd](https://github.com/AndrewMcLachlan/CodeHerd) | Electron + TypeScript + xterm.js | 标签页管理 Claude Code / Codex CLI，会话持久化、会话侧栏、状态栏 | 自己托管终端；不做“完成待看”收件箱 | MIT |
| [Crystal / Nimbalyst](https://github.com/stravu/crystal) | Electron | 多 Claude/Codex 会话 + git worktree 并行、diff 审查 | 项目已改名为 Nimbalyst，Crystal 已停止维护；偏编排而非监控 | 见仓库 |
| [opcode](https://github.com/winfunc/opcode) | Tauri 2 + Rust + React | Claude Code GUI：项目/会话浏览、agent、用量统计、checkpoint | 历史浏览与自建 agent；无跨来源完成提醒 | AGPL-3.0 |
| [Claude Code History Viewer](https://github.com/jhlee0409/claude-code-history-viewer) | Tauri 2 + Rust + React | 31 种 Agent 的本地会话历史统一浏览、搜索、统计，实时文件监听 | 只读“历史浏览器”，没有完成提醒、已读/完成、清理墓碑 | MIT |
| [Vibe Kanban](https://github.com/BloopAI/vibe-kanban) | Rust + React | 看板式规划 + 多 Agent 工作区、diff 审查、任务状态 | 重编排、重工作区；项目已宣布 sunset | 见仓库 |

## 3. 结论

**没有可以直接拿来用的项目。** 现有工具分成两类：

- **托管型**（Claude Squad、Clauditor、CodeHerd、Crystal）：它们自己启动 Agent、接管终端或 worktree。用户已经有一套稳定的工作方式（DSH Web GUI、Codex Desktop、VS Code 里的 Claude Code），不想被换掉。
- **历史浏览型**（CCHV、opcode）：解决“事后回看”，不解决“现在哪条跑完了、我看过没有”。

我们的空白点很明确：**跨 Agent、只读、旁路、以“未读/完成”为中心的桌面收件箱**。

## 4. 可借鉴的具体设计

| 借鉴点 | 来源 | 我们怎么用 |
|---|---|---|
| 会话状态机（running / awaiting_user / idle / exited） | Clauditor | 简化为 进行中 / 待看 / 已完成 / 已中断；以轮次边界而非屏幕内容判定 |
| 实时文件监听 + 防抖刷新 | CCHV | `FileSystemWatcher` + 4 秒兜底轮询；只重扫变化的文件 |
| 多来源适配器与路径表 | CCHV | `IAgentSource` 接口；DSH/Codex/Claude 三个适配器，后续按同一接口扩展 |
| 托盘常驻 + 完成通知 + 点击通知回到记录 | Clauditor | `NotifyIcon` + 气泡通知；点击通知显示窗口并高亮对应记录 |
| 会话标题 + 项目目录 + 相对时间的列表信息密度 | CodeHerd / CCHV | 两行标题 + 目录尾两级 + 相对时间 + 来源徽标 |
| 轻量、本地优先、无遥测 | 全部项目 | 不联网；状态只写 `%APPDATA%` |
| 深链跳转会话 | CCHV | 本期不做（各 Agent 不提供稳定深链）；只做窗口级跳转，已在需求中确认 |

## 5. 许可证与代码复用

- Claude Squad、opcode 为 AGPL-3.0：不复制其代码，避免传染性许可影响分发。
- Clauditor、CodeHerd、CCHV 为 MIT：可以借鉴实现思路；本期不直接复制代码，保持独立实现。
- 所有项目均未提供“跨 DSH / Codex / Claude Code 的只读完成收件箱”，因此自研核心状态机与适配器。

## 6. 数据格式参考

调研中确认的本地数据位置（本项目实际使用）：

- DSH：`~/.dsh/storages/session_projcache/sessions/*.json`，含 `title`、`sessionStats.lastTurn/openStep/pendingCalls`、`sessionListMetadata.lastPromptAt`、`identity.cwd`；
- Codex：`~/.codex/sessions/**/rollout-*.jsonl`，`event_msg` 中的 `task_started` / `task_complete` / `turn_aborted` 是稳定轮次边界；`~/.codex/session_index.jsonl` 提供 `thread_name`；
- Claude Code：`~/.claude/projects/<project>/<sessionId>.jsonl`，`assistant.stop_reason`、`last-prompt`、`cost-state` 可判定轮次结束；首条 `user` 消息可作为标题。

## 7. 引用

- https://github.com/smtg-ai/claude-squad
- https://github.com/huylq98/clauditor
- https://github.com/AndrewMcLachlan/CodeHerd
- https://github.com/stravu/crystal
- https://github.com/winfunc/opcode
- https://github.com/jhlee0409/claude-code-history-viewer
- https://github.com/BloopAI/vibe-kanban
