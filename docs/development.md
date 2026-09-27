# 开发、验证与打包

[← 返回首页](../README.md)

VSAI 是面向 Visual Studio 2022 / 2026 的 64 位 VSIX，使用 C#、WPF、.NET Framework 4.7.2、VS SDK 与 WebView2。模型请求通过本机 `codex app-server` 处理；VSIX 不包含 Codex CLI。

## 工程入口

| 位置 | 用途 |
| --- | --- |
| [CodexVs2026Extension.sln](../CodexVs2026Extension.sln) | 解决方案 |
| [CodexVsix.csproj](../CodexVsix/CodexVsix.csproj) | 扩展工程 |
| [CodexVsix.Tests](../CodexVsix.Tests/) | 回归测试 |
| [CodexProcessService.cs](../CodexVsix/Services/CodexProcessService.cs) | app-server 进程、协议、会话、取消与工具分发 |
| [CodexOfficialWebViewBridge.cs](../CodexVsix/Services/CodexOfficialWebViewBridge.cs) | WebView 消息适配 |
| [SolutionContextService.cs](../CodexVsix/Services/SolutionContextService.cs) | IDE 上下文、文件搜索与源码导航 |
| [VisualStudioDebugService.cs](../CodexVsix/Services/VisualStudioDebugService.cs) | VS 调试现场读取 |
| [VsDebugTools.cs](../CodexVsix/Services/VsDebugTools.cs) | 调试工具定义与参数校验 |
| [VsDebugToolDispatcher.cs](../CodexVsix/Services/VsDebugToolDispatcher.cs) | 工具调用、取消与进程代际隔离 |
| [CodexToolWindowViewModel.cs](../CodexVsix/ViewModels/CodexToolWindowViewModel.cs) | 经典界面与会话状态 |
| [CodexWebview](../CodexVsix/UI/CodexWebview/) | 冻结前端及宿主适配文件 |
| [scripts](../scripts/) | 版本同步、包验证与发布辅助脚本 |

主调用链：

```text
Visual Studio 编辑器 / 调试器
             ↕
       VSAI 宿主与工具
             ↕
      codex app-server
             ↕
         所选模型
```

UI 由 WebView2 承载的 Codex WebView 展示，经典 WPF 界面作为回退。访问 DTE 与 WPF 对象需要遵守 VS UI 线程要求。冻结前端的来源与许可见 [SOURCE.md](../CodexVsix/UI/CodexWebview/SOURCE.md)。

## 版本管理

[Directory.Build.props](../Directory.Build.props) 是版本来源，程序集版本为 `$(Version).0`。使用脚本同步中央版本和两份 VSIX manifest：

```powershell
.\scripts\Set-VsixVersion.ps1 -Version 1.0.9
```

修改源码版本不会自动构建、安装或发布。发布包版本需要另外通过包验证确认。

## 构建与测试

下面是维护者按需执行的命令，不表示当前源码已经通过这些检查。依赖恢复、测试和打包应在已配置 VS 扩展开发环境的 Windows 上运行；测试也会编译工程。

从仓库根目录执行锁定恢复和测试：

```powershell
dotnet restore CodexVs2026Extension.sln --locked-mode
dotnet test CodexVs2026Extension.sln -c Release --no-restore
```

最终 VSIX 使用完整 Visual Studio MSBuild 打包。以下示例将交付包和构建日志放在仓库外，显式关闭自动部署：

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsInstall = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vsInstall) { throw "未找到 Visual Studio MSBuild。" }
$vsMsbuild = Join-Path $vsInstall "MSBuild\Current\Bin\MSBuild.exe"

[xml]$versionProps = Get-Content .\Directory.Build.props -Raw
$releaseVersion = [string]$versionProps.Project.PropertyGroup.Version
$artifactDir = Join-Path $env:TEMP ("VSAI-" + $releaseVersion + "-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
$packagePath = Join-Path $artifactDir ("VSAI-" + $releaseVersion + ".vsix")
$buildLog = Join-Path $artifactDir "build.log"

& $vsMsbuild .\CodexVsix\CodexVsix.csproj `
  /t:Rebuild /m:1 /nr:false `
  /p:Configuration=Release `
  /p:RestoreLockedMode=true `
  /p:BuildVsixPackage=true `
  /p:DeployExtension=false `
  "/p:TargetVsixContainer=$packagePath" `
  "/flp:logfile=$buildLog;verbosity=normal"
if ($LASTEXITCODE -ne 0) { throw "VSIX 构建失败，请检查 $buildLog。" }

.\scripts\Test-VsixPackage.ps1 -VsixPath $packagePath -ExpectedVersion $releaseVersion
```

包验证脚本检查 manifest / 程序集版本、必要资源、运行时依赖以及不应打包的凭据和私钥文件。仓库的 [CI](../.github/workflows/ci.yml) 定义了恢复、测试、打包及包验证流程；工作流文件本身不代表某次提交已经通过。

## 调试工具的验证范围

工具在 `thread/start` 注册，通过 `item/tool/call` 回调到本机宿主；恢复聊天沿用创建时保存的工具定义。升级前的聊天需要新建一次。协议依赖 Codex app-server 的实验性 `dynamicTools` 支持，升级 CLI 后应复核协议契约。

当前测试源码覆盖：

- 工具定义合并、参数范围与工具名称归属。
- 暂停现场标识失效、请求取消与旧进程响应抑制。
- 受控内存输入输出下的 app-server 工具分发。

1.0.9 已通过构建、自动化回归和 VS2026 安装校验。这些检查不能替代 DTE 实机验证，调试能力仍需在 VS 中确认：

1. 暂停在 C++ 断点，核对当前堆栈、源码位置与栈帧参数。
2. 查看其他线程后，确认 VS 当前选中的线程和栈帧未改变。
3. 继续、单步、结束调试后，确认旧现场请求被拒绝，重新获取快照后可继续分析。
4. 覆盖异常、缺少符号、优化变量，以及 WebView 与经典界面两种入口。
5. 在工具读取期间取消聊天或重启 app-server，确认旧请求不会回复到新进程。

读取次数、字符串和响应体都有边界；单次阻塞的 COM 调用无法被协作式超时强制中断。对象成员枚举缺少禁止隐式求值的开关，因此当前不展开成员。

## 安装与发布

安装前关闭目标 VS 实例，使用本次构建并验证的包；确认安装日志与安装后的版本。会话恢复、WebView 显示和源码点击跳转仍需在 VS 中试用。

本项目使用 [自有 Gallery](https://vsix.snowsome.com) 分发。部署采用独立 release 目录、校验和及原子切换，具体流程见 [AGENTS.md](../AGENTS.md)。Gallery 部署、Git 提交 / 推送和 Marketplace 发布是不同操作，各自按授权执行。

修改前请阅读 [项目协作指南](../AGENTS.md)，保留现有编码、换行、上游版权与第三方声明。
