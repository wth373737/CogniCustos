# CogniCustos — 可运行骨架（架构验证版）

这不是产品，也不是本学期的完成版本。它是**一个能跑的“系统与操作层面”的主体**，用来验证：

1. 分层是否成立（UI / 编排 / 模型 / 工具 / 数据 各自可换）；
2. “AI 只能提议、人确认后才落库”这条规则能否在代码层面强制；
3. 既有 JS 仓库的能力能不能通过**接口**接进来，而不是被重写或丢弃。

UI 是占位的，随时会被替换；不要在这个版本上设计视觉。

---

## 一、它现在能做什么（实测）

| 能力 | 状态 |
|---|---|
| 工具注册表（MCP 形状：name + description + 参数 + 单一 invoke） | ✅ 11 个工具 |
| AI 私有沙箱（文本分区，`brief/notes/proposals/output`，append-only 思考日志） | ✅ |
| 确认处理器（提议 → 批准/驳回 → 应用；重启不丢） | ✅ 持久化 JSON |
| 主数据库（SQLite：任务 / 截止） | ✅ |
| **规划层次（长期 / 中期 / 短期 + 父子归属）** | ✅ 原地迁移旧库 |
| **日程（日 / 周视图，时段块）** | ✅ 与任务同库，独立表 |
| 提醒（轮询已到期任务，25 小时内触达一次） | ✅ |
| 本地模型对话（Ollama，`/api/chat`、`/api/embeddings`） | ✅ 已连 qwen2.5:7b |
| 模型按角色路由 + 无模型时的备用回答（不编造内容） | ✅ |
| 知识库检索（关键词占位实现，接口与 LanceDB 一致） | ✅ 占位 |
| **敏感内容本地检查（规则层，9 类）** | ✅ 不联网，只报告不静默改写 |
| **云端模型 + 出站闸门**（OpenAI 兼容端点） | ✅ 检查→拦截/放行→审计 |
| **出站审计**（含被拒记录） | ✅ JSONL，`/audit` 可查 |
| **只读文件访问**（限制在应用目录内，≤512 KB） | ✅ 无任何写工具 |
| **自文档检索**（README + 源码，AI 能解释系统怎么用） | ✅ 启动时自动索引 |
| 大学门户只读读取（经本地 HTTP 接口复用 cookie-whitelist） | ✅ 接口就位，sidecar 未配置时如实报错 |
| 图像识别 | ❌ 仅预留 `ModelRole.Vision`，未接 |
| MCP 协议的真正实现（JSON-RPC over stdio/HTTP） | ❌ 工具已是 MCP 形状，但还没有协议层 |

自检：**37 条离线不变式** + 3 条真模型往返（工具调用、计划+日程、确认落库），全过。

```bash
dotnet build -c Debug
dotnet run --project src/CogniCustos.Selftest -c Debug          # 37/37
$env:COGNICUSTOS_LIVE_MODEL="1"; dotnet run --project src/CogniCustos.Selftest -c Debug   # + 3 条真模型
dotnet run --project src/CogniCustos.Desktop                    # 打开窗口
```

> 本机注意：这个沙盒下 `dotnet restore/build` 会因 SChannel 凭据不可访问而失败，必须在完全权限下执行。

---

## 二、分层与“两个接口”

```
Avalonia UI  (CogniCustos.Desktop)
      │  只依赖接口，不认识模型也不认识 SQL
CogniCustos.Core
   ├─ CogniCustosRuntime     组装 + 对话循环
   ├─ ToolRegistry           ← 接口一：工具（MCP 形状）
   ├─ ModelRouter            ← 接口二：模型（按角色选）
   ├─ ApprovalGate           提议 / 批准 / 驳回 / 应用
   ├─ SandboxStore           AI 的文本沙箱
   ├─ SqliteTaskStore        主库（AI 写不进来）
   ├─ KeywordKnowledgeBase   知识库（LanceDB 的目标位）
   └─ ReminderService        本地提醒
CogniCustos.Adapters
   ├─ OllamaModelProvider    直接说 Ollama REST
   └─ PortalSidecarTool      GET-only，指向既有 JS 实现
CogniCustos.Contracts       全部接口与 DTO，无依赖
```

**AI 能写什么**：只有 `SandboxStore`。对主库和知识库的每次修改都是一条 `Proposal`，存在沙箱里，等人在 UI 上点“批准”，再由 `ApprovalGate` 的应用器写入。

---

## 三、和既有仓库的关系（接口耦合，不复制粘贴）

| 既有仓库 | 在本骨架里的位置 | 处理方式 |
|---|---|---|
| `dsh-plugin-sandbox-mode` | `SandboxStore` | **借设计**：单文档为唯一事实源、按节替换、独立思考日志；不搬代码 |
| `dsh-plugin-ollama-bridge` | `OllamaModelProvider` | **借接口**：直接说同一套 Ollama REST，因此两个实现可共存、可替换 |
| `dsh-plugin-cookie-whitelist` | `PortalSidecarTool` | **留接口**：只发 GET，通过本地 HTTP 合同调用既有实现；未配置时如实失败 |
| `dsh-video-localmodel` | `ModelRole.Vision` | **留位**：多模态角色已在路由里，实现待接 |
| `dsh-plugin-mail-triage` | — | 未接。邮件在内部文档里算 MVP，本轮未纳入 |
| `dsh-resource-guard` | — | 不接。它是工具链闸门，不属于产品 |

`PortalSidecarTool` 期望的合同（任何实现都能满足）：

```
GET {base}/v1/status                    -> { ok, vaultDomains: [] }
GET {base}/v1/read?url=...&domain=...   -> { ok, title, text, links: [] }
```

---

## 四、和两份计划文档的关系

- 架构与术语遵循 `CogniCustos_Extended_Proposal_clean.docx`：七模块、两个接口、只读边界、沙箱暂存、确认后应用。
- 优先级参考 `内部文档.docx`：先“模块化骨架 + 工具层 + GUI + 多模态接口”，其余往后。
- **冲突未解决的那部分不在本骨架里表态**（邮件是否入 MVP、AI 能否直写知识库、NAS、识图范围）。骨架只提供机制，不替计划做决定。

---

## 五、已知限制（都是有意留下的）

- 对话循环最多 3 步工具调用；没有规划器、没有反思、没有记忆压缩。
- 协议是“宽松解析 + 严格执行”：模型把工具名写进 `action` 也能认，但认不出就原样显示，绝不猜测。
- 知识库是关键词打分，不是向量检索；接口稳定，实现可换。
- 没有多用户、没有登录、没有加密存储（本地单机假设）。
- UI 只有最小交互，没有设计；截图仅供确认“确实跑起来了”。

---

## 六、下一步（按依赖排序，不是按优先级）

1. 把 `ToolRegistry` 包一层真正的 MCP server（stdio），让外部 agent 也能接；
2. 接 `ModelRole.Vision`（本机有 qwen3-vl），把图像 → 文本的链路补上；
3. `KeywordKnowledgeBase` 换成 LanceDB（接口不变）；
4. 门户 sidecar：给 cookie-whitelist 补一个 `/v1/read` 端点，或写一个独立 Node 小进程；
5. 邮件分诊接口化接入；
6. 再谈 UI 设计。

---

## 七、状态

2026-11-17 第二轮：补上提案目标 2（只读文件工具）、目标 3（本地敏感检查 + 云端通路）、目标 7（自文档检索），
并加了出站审计（§5.2.6 要求“包括被拒的请求”）。`passed=29 failed=0`。截图：`docs/screenshot-main.png`。

第一轮（2026-11-16）：分层骨架、确认处理器、沙箱、SQLite、提醒、Ollama 对话、关键词检索、门户接口位。

**出站闸门的行为**（这是本轮最需要验收的部分）：

```
cloud.ask → 本地规则检查（9 类：邮箱/电话/学号/密钥/密码/银行卡/身份证…）
          ├─ 干净      → 发送 → 审计 "sent"
          ├─ 命中      → 拒发 → 审计 "refused"，返回理由 + 脱敏预览
          └─ 命中+人工放行 → 发送 → 审计 "sent-overridden"
```

---

## 八、UI 沙箱（2026-11-20）

界面从这一版开始独立开发：新增 `src/CogniCustos.Ui`，**不引用** Core / Adapters，用假数据把
主窗口（顶栏 / 三张卡片 / 推理记录 / 输入条 / 右侧竖排栏）和三个抽屉（菜单抽屉、预留功能区、设置页）做出来。
界面按 **1280×720（16:9）** 画布设计并整体等比缩放到窗口（16:9 铺满，其他比例留边、不变形）；结构是"左栏 logo+推理记录 / 顶部今天提醒条 / 三张卡（首张是压缩日历）/ 输入框 / 右栏"。文案在 `Resources/Lang.*.axaml`（运行时切换，按钮显示当前语言；新增语言=复制一份文件、只改值并登记到 `Localizer.Languages`）。配套 `tools/CogniCustos.Ui.Shots` 可无头渲染 10 个状态，`tools/CogniCustos.Ui.Tests` 有 43 条无头断言（非 0 退出即失败）。

- 框架：Avalonia **12.1.6**（全解决方案已从 11.2.3 升级，`Selftest 37/37` 仍全过）；包版本集中在 `Directory.Packages.props`
- 结构、交互、设计约定、回归测试与"还没做的事"：见 [docs/UI-SANDBOX.md](docs/UI-SANDBOX.md)
- VS Code：`.vscode/launch.json` 已配好「跑窗口 / 跑回归 / 无头出图 / 跑旧骨架」四个 F5 配置


