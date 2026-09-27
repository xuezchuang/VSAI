<div align="center">
  <img src="CodexVsix/Resources/MarketplaceIcon.png" alt="VSAI" width="88" height="88">
  <h1>VSAI</h1>
  <p><strong>在 Visual Studio 里读懂代码，也看见调试现场。</strong></p>
  <p>Visual Studio 2022 / 2026 · Windows x64 · 源码版本 1.0.9 · MIT</p>
  <p>
    <a href="https://vsix.snowsome.com">下载安装</a> ·
    <a href="#快速开始">快速开始</a> ·
    <a href="#调试现场分析">调试现场分析</a> ·
    <a href="docs/development.md">开发文档</a>
  </p>
</div>

---

VSAI 将 Codex 嵌入完整的 Visual Studio：结合当前工程、选中代码和编辑器上下文进行分析，点击回答中的源码引用即可回到对应位置。适合日常代码阅读、C++ / SVN 工程维护，以及断点和异常现场排查。

扩展通过本机 Codex CLI 工作，支持 ChatGPT 官方账号与自定义模型服务。主界面使用适配后的 Codex WebView，加载失败时提供经典 WPF 界面。

## 能做什么

| 场景 | VSAI 的支持 |
| --- | --- |
| **读懂工程** | 结合当前文档、选区和打开的文件分析代码，通过 `@file` 查找并补充工程文件。 |
| **定位源码** | 点击回答中的文件与行号引用，在 VS 中打开对应位置。 |
| **分析调试现场** | 1.0.9 新增内置调试工具，按需读取调用堆栈、参数、局部变量和其他线程。 |
| **切换模型** | 统一管理官方模型和第三方服务，按模型选择可用的推理选项。 |
| **继续之前的工作** | 使用独立会话库，按当前工作目录展示历史，保存每个解决方案选择的工作目录。 |
| **在 IDE 内协作** | 流式回答、工具过程、审批、图片附件、代码差异与 Mermaid 图表。 |

## 快速开始

### 使用前准备

| 项目 | 要求 |
| --- | --- |
| Visual Studio | 2022 或 2026，64 位，包含核心编辑器组件 |
| .NET Framework | 4.7.2 或更高版本 |
| Codex CLI | 本机已安装，支持 `codex app-server` |
| 模型服务 | 可用的 ChatGPT 官方登录或第三方服务配置 |

VSIX 不包含 Codex CLI。CLI 的安装方式见 [Codex 官方文档](https://developers.openai.com/codex/cli/)，安装后可在终端确认：

```powershell
codex --version
```

### 安装与第一次对话

1. 从 **[VSAI Gallery](https://vsix.snowsome.com)** 下载 VSIX。关闭目标 VS 实例后，运行安装包并选择对应实例。
2. 打开解决方案，在 **视图 → VSAI** 打开聊天窗口。
3. 打开设置，在 **模型与服务 → ChatGPT 官方订阅 → 登录官方账号** 完成登录；使用第三方服务时，在同一页面添加服务和模型。
4. 确认工作目录与模型，创建聊天。若未找到 CLI，在设置中指定本机 Codex 可执行文件路径。
5. 选中一段代码加入对话，或用 `@file` 指定文件，开始提问。

> 本文对应 **1.0.9 源码**，可下载版本以 Gallery 为准。VS 中的官方登录属于 VSAI 独立账号目录，需要首次登录一次。

**可以这样开始：**

> 分析这个函数的输入、分支和调用链，指出结果最终在哪里使用。先只分析。

> 结合当前选区定位问题，给出最小修改，并说明还需要验证什么。

<details>
<summary>将 VSAI Gallery 添加到 Visual Studio</summary>

在 VS 的 **工具 → 选项 → 环境 → 扩展** 中添加附加扩展库，名称填写 `VSAI`，地址填写：

```text
https://vsix.snowsome.com/atom.xml
```

入口说明见 [Visual Studio 私有扩展库文档](https://learn.microsoft.com/en-us/visualstudio/extensibility/private-galleries)。也可以直接从 Gallery 页面下载安装包。

</details>

## 调试现场分析

**1.0.9 源码新增：把当前 VS 实例的暂停现场交给模型分析，无需单独配置 MCP。**

> **发布与验证状态**：1.0.9 已发布到 Gallery，并通过自动化回归和 VS2026 安装校验；调试现场读取仍待 VS 实机验证。

1. 在 VS 中开始调试，停在断点或异常处。
2. 在安装了 1.0.9 的 VSAI 中**新建聊天**。
3. 输入下面这样的请求，模型即可按需读取现场，并结合源码追踪原因。

> 分析当前断点为什么出错。先看调用堆栈，再检查相关栈帧的参数和局部变量；区分现场证据与推测，先不要改代码。

| 内置工具 | 读取内容 |
| --- | --- |
| `vs_debug_snapshot` | 调试状态、可用停止原因、当前线程的调用堆栈与源码位置 |
| `vs_debug_frame_variables` | 指定栈帧的参数和局部变量 |
| `vs_debug_threads` | 同一次暂停中的其他进程、线程及有数量限制的堆栈 |

工具通过 VSAI 直接注册到 Codex app-server，WebView 与经典界面共用。它们只读取现场，不控制程序运行，不切换当前线程或栈帧，也不执行任意表达式。

**当前边界**

- 继续、单步或停止调试后，旧现场标识失效，后续分析需要重新获取快照。
- 缺少调试符号、编译优化或读取限额会影响可见数据，结果会标识不可用或截断情况。
- 读取参数和局部变量时关闭隐式属性及函数求值；对象成员展开暂不提供。
- 工具依赖 app-server 的实验性 `dynamicTools` 接口。升级前创建的聊天需要重新新建；升级后创建的聊天可继续恢复。
- 现场数据会作为工具结果交给当前所选模型，并随聊天历史保存。堆栈能帮助定位故障，根因仍需结合源码、变量与复现结果判断。

## 常见问题

<details>
<summary><strong>桌面端已经登录，为什么 VSAI 还要登录？</strong></summary>

VSAI 使用独立账号与会话目录，默认位于 `%USERPROFILE%\.codex\vsai`。在 VSAI 设置里登录一次即可，登录和退出不会改变桌面端的账号状态。

</details>

<details>
<summary><strong>切换模型或解决方案后，历史记录在哪里？</strong></summary>

官方模型与第三方模型共用 VSAI 的独立会话库。历史列表按当前工作目录筛选；切回原工作目录可查看对应历史。工作目录会按解决方案保存。

</details>

<details>
<summary><strong>设置、会话和日志保存在哪里？</strong></summary>

扩展设置位于 `%LOCALAPPDATA%\VSAI\settings.json`；启用诊断后，诊断日志位于 `%LOCALAPPDATA%\VSAI\logs`。会话、Codex 配置和运行日志位于独立 Codex home。

目录覆盖规则、旧会话迁移和共享记忆详见 [本地数据与会话](docs/local-data.md)。

</details>

<details>
<summary><strong>主界面加载失败时怎么办？</strong></summary>

扩展会尝试恢复 WebView，失败后提供经典界面。可通过回退界面的重试入口重新加载，也可以启用诊断日志定位问题。报告问题时请注明 VS、VSAI 和 Codex CLI 版本及复现步骤。

</details>

## 开发与项目来源

- [开发、验证与打包](docs/development.md)：工程入口、版本同步、构建命令和验证边界。
- [本地数据与会话](docs/local-data.md)：配置目录、登录隔离、迁移和共享记忆。
- [第三方声明](THIRD-PARTY-NOTICES.md) · [WebView 来源](CodexVsix/UI/CodexWebview/SOURCE.md) · [上游变更历史](CHANGELOG.md)

VSAI 基于 [Visual Codex Studio](https://github.com/rodrigojager/codex-visual-studio-extension) 定制，保留上游版权与 [MIT 许可](LICENSE)。本项目独立维护，与 OpenAI / ChatGPT 无官方隶属或背书关系。
