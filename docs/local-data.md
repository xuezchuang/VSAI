# 本地数据与会话

[← 返回首页](../README.md)

VSAI 为官方模型和第三方模型使用同一套独立会话库。切换模型不会切换会话库；聊天历史按当前工作目录筛选。

## 数据保存在哪里

下表使用未覆盖 home 路径时的常见 Windows 默认值：

| 数据 | 默认位置 |
| --- | --- |
| 扩展设置 | `%LOCALAPPDATA%\VSAI\settings.json` |
| 扩展诊断日志 | `%LOCALAPPDATA%\VSAI\logs\codex-diagnostics.jsonl`，需手动启用 |
| VSAI 私有 Codex home | `%USERPROFILE%\.codex\vsai` |
| VSAI Codex 配置 | 私有 home 下的 `config.toml` |
| 会话与归档 | 私有 home 下的 `sessions`、`archived_sessions` 及运行时索引 |
| app-server 运行日志 | 私有 home 下的 `log` |
| 迁移备份与进度 | 私有 home 下的 `migration-backups`、`session-migration-manifest.json` |
| 共享记忆 | 原 Codex home 下的 `memories` |

这些是当前 Windows 用户的运行数据，不属于仓库或 VSIX。

### 覆盖 home 路径

VSAI 先确定原 Codex home，再使用其 `vsai` 子目录作为私有 home：

1. 优先使用扩展环境变量设置中非空的 `CODEX_HOME`。
2. 否则使用 VS 进程环境中的 `CODEX_HOME`。
3. 都未设置时，使用用户 home 下的 `.codex`；用户 home 优先取 `HOME`，其次 `USERPROFILE`，最后取 Windows 用户目录。

例如，设置 `CODEX_HOME=D:\CodexData` 后，VSAI 的私有目录是 `D:\CodexData\vsai`，共享记忆目录是 `D:\CodexData\memories`。私有 app-server 的 `CODEX_HOME` 与数据库目录会指向同一私有 home。

## 登录与设置

首次初始化时，VSAI 从原 Codex home 复制配置与用户指令作为快照；官方模型缓存会按版本与时间刷新。原账号的 `auth.json` 和会话数据库不会作为初始化快照复制。

后续设置页编辑 VSAI 自己的配置。使用官方账号时，在 **设置 → 模型与服务 → ChatGPT 官方订阅 → 登录官方账号** 登录一次。VSAI 的登录、退出与认证检查均使用私有目录，不改变桌面端登录。

扩展设置中的敏感字段使用当前 Windows 用户的 DPAPI 保护，并通过原子写入与多实例协调保存。这不代表整个 Codex home 或聊天内容都经过 DPAPI 加密；Codex 认证使用私有目录中的文件凭据存储。

## 工作目录与历史

每个解决方案保存各自选择的工作目录；尚未选择时，默认使用解决方案目录。历史列表按当前工作目录展示，同一目录下的官方与第三方模型聊天可以一起查找。

切换解决方案或工作目录会刷新会话入口。若找不到旧聊天，先检查当前目录是否与创建聊天时一致。每个解决方案并没有单独的一份会话数据库。

## 旧会话迁移

首次连接时，扩展扫描原库中的普通和已归档历史，并只读核对本地索引，识别 `codex-vsix` 创建或使用 `vsai_` 服务的记录。

迁移按以下顺序处理：

1. 将原始日志备份到私有 home 的 `migration-backups`。
2. 将分页历史及必要的父记录复制到私有库，由 app-server 重建独立索引。
3. 保存迁移对应关系与进度，验证导入后的历史完整性，并确认源文件未变化。
4. 验证成功后归档原库中尚未归档的对应记录；不删除源日志。

原本已归档的会话保持归档。仅用于补全历史的非 VSAI 父记录在新库中归档，桌面端对应原记录保持不变。只有初始化信息、没有对话及父历史引用的空记录只备份，不生成空聊天。

### 迁移未完成时

运行中的记录、损坏日志、超出大小限制或无法确认导入结果的记录会保留在原库，VSAI 会提示迁移未完成，私有库仍可使用。关闭旧会话后，重启 VS 可重试。

若进度记录出现 `fork-outcome-unknown`，应先核对新库中的导入结果；直接清空迁移清单重试可能造成重复导入。旧版 WebView 缓存保留，新版使用独立缓存，避免恢复已迁移的旧 ID。

## 共享记忆

创建、恢复或分叉聊天时，VSAI 从原 Codex home 读取 `memories\memory_summary.md`，并提供同目录的 `MEMORY.md`、`rollout_summaries` 与 `skills` 路径供进一步查询。

VSAI 关闭私有库的自动记忆生成；桌面端仍负责共享记忆的自动整理，VSAI 私有聊天不会自动进入桌面端的记忆提炼。用户明确要求记住的内容，按共享目录的 `memories\extensions\ad_hoc\notes` 流程追加。

## 诊断与调试数据

扩展诊断日志默认关闭，启用后用于记录进程、协议状态和界面恢复情况。它与私有 home 下的 app-server 运行日志属于不同目录。分享诊断前，应检查其中的本机路径和环境信息。

调试工具返回的堆栈、参数和局部变量会进入当前模型的工具上下文，并随聊天保存；它们不另写入扩展诊断日志。当前对象成员不展开，不可用与截断的数据会在结果中标识。

请勿将认证文件、API Key 或包含私人会话的整个数据目录提交到仓库。
