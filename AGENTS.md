# Visual Codex Studio 项目协作指南

## 项目与定制方向

- 当前基座是 `rodrigojager/codex-visual-studio-extension`，本地目录 `D:\code\VSAI`。这是完整 Visual Studio 的 VSIX，面向 VS2022 / VS2026，并非 VS Code 扩展。
- 优先服务于 Windows 本机 Codex 分析 C++ / SVN 工程的流程：稳定会话、准确传递上下文、点击回答中的文件引用跳转源码。后续界面和功能需求由用户逐项定制。
- 个人授权、沟通和子代理模型规则沿用用户级指令；当前任务明确指定 Astra 时，子代理也使用 Astra。不要把临时模型选择硬编码进产品。
- 本仓库使用 MIT 许可；保留 `LICENSE`、上游版权和第三方资源声明。普通修复不自动改名、换协议、更新全部依赖或升版本。

## 工程入口

- 解决方案：`CodexVs2026Extension.sln`；主工程：`CodexVsix/CodexVsix.csproj`；测试：`CodexVsix.Tests/CodexVsix.Tests.csproj`。
- 工程使用 C# / WPF、.NET Framework 4.7.2、VS SDK 和 WebView2。版本统一来源为 `Directory.Build.props`，相关脚本位于 `scripts/`。
- 运行时通过本地 `codex app-server` 通信；主界面适配仓库内冻结的 Codex WebView，另有经典 WPF 界面回退。VSIX 不包含 Codex CLI。

| 关注点 | 入口 |
| --- | --- |
| 进程、JSON-RPC、会话与取消 | `CodexVsix/Services/CodexProcessService.cs` |
| WebView 协议桥接与审批转发 | `CodexVsix/Services/CodexOfficialWebViewBridge.cs`、`CodexAppServerRequestRelay.cs` |
| 工程上下文、文件搜索与 VS 导航 | `CodexVsix/Services/SolutionContextService.cs` |
| 经典界面与回答渲染 | `CodexVsix/ViewModels/CodexToolWindowViewModel.cs`、`CodexVsix/UI/MarkdownRenderer.cs` |
| 设置持久化与诊断 | `CodexVsix/Services/ExtensionSettingsStore.cs`、`CodexDiagnosticLogger.cs` |
| 冻结界面资源 | `CodexVsix/UI/CodexWebview/` |
| 包检查与版本同步 | `scripts/Test-VsixPackage.ps1`、`scripts/Set-VsixVersion.ps1` |

## 修改边界

- 修改前检查 Git 状态，保留无关改动及现有编码、BOM、换行。优先修复有调用链证据的问题，不因为代码看起来由 AI 生成而重写模块。
- 保持 .NET Framework 4.7.2 和 VS 宿主兼容；访问 DTE / WPF 必须遵守 UI 线程要求。避免在渲染、CanExecute 或命令状态查询中遍历整个解决方案、启动进程或进行长时间 I/O。
- 进程重启、取消和会话切换必须核查旧请求、旧事件、等待任务与进程资源的归属。审批响应只能回到产生该请求的服务实例；不得靠请求编号碰巧未复用来保证正确性。
- 文件导航按“引用 → 渲染 → 路径和坐标解析 → 工作目录 / solution 定位 → VS 打开”检查。覆盖中文、空格、盘符、相对路径、file URI、字面 `%` / `#`、同名文件和越界坐标；不能打开另一个同名文件并声称成功。
- 改桥接时核对冻结 WebView 实际消息契约，保留经典回退。更新冻结资源及其同步来源属于独立需求，不手工批量修改压缩后的 JavaScript。
- 设置使用当前 Windows 用户的 DPAPI 保护敏感字段，并存在多实例写入协调。读取或解密失败时不得悄悄用默认值覆盖原数据；日志不应包含凭据或完整私密会话。
- 用户分析的 C++ / SVN 工程是扩展使用对象，不是本次修复目标。分析或发送提示不自动授权对那些工程拉取、提交、编译、运行或改动。
- 本机 `%LocalAppData%\CodexVsix\` 与用户 Codex 配置不是仓库配置。不要为测试读取真实凭据、改用户模型或调用付费推理；协议回归优先使用受控内存输入输出。

## 验证与交付

- 默认不编译、不启动 VS、不部署；用户明确要求后按授权范围执行。测试也会编译，不能把它当成无需编译的静态检查。
- 获授权后，锁定依赖恢复使用 `dotnet restore CodexVs2026Extension.sln --locked-mode`；测试使用 `dotnet test CodexVs2026Extension.sln -c Release --no-restore`。仅运行与变更相称的验证，失败修复后再复验。
- VSIX 打包使用本机探测到的完整 VS MSBuild，目标为 `CodexVsix/CodexVsix.csproj`，Release，`BuildVsixPackage=true`、`DeployExtension=false`。不要依赖 Debug 自动部署。
- 打包后运行 `scripts/Test-VsixPackage.ps1`，ExpectedVersion 取当前版本文件。构建日志和交付包放在仓库外，避免加入生成物。
- 安装必须匹配用户指定 VS 实例，并使用本次构建的包。先确认目标 VS 已关闭；不结束用户 VS 进程、不抢焦点。验证安装日志和已安装文件，不仅看退出码。
- 自动化检查不能替代真实 VS 中的会话、WebView 和点击跳转试用。交付时分别说明已执行验证和仍需人工确认的行为；未获授权不提交、推送或发布 Marketplace。

## 待用户定制

- 界面语言、聊天布局、源码引用样式、默认权限、模型选择与 VS2022 兼容范围尚待具体要求；以上不是已批准的功能清单。
- `AGENTS.md` 是开发协作规则，不能代替扩展中的实际权限和只读模式实现。
