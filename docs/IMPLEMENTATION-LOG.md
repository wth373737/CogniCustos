# 实施记录 —— 代码怎么改的、怎么验、以后改哪里

> **用途**：`docs/PARTITION-MODEL.md` 定规则，这份文件记**落地过程**。每完成一次可运行的改动就追加一节，
> 每节都回答五个问题：改了什么 / 为什么 / 动了哪些文件 / 怎么验证 / 以后要改哪里。
>
> **约定**：条目按时间倒序追加在文件末尾；每条都标出对应的文档条款编号，方便反查。

---

## 2026-11-24 · P0-1 数据底座：迁移框架、原始区、严格等级、溯源列

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 迁移框架 | 新增 `SqliteSchema`：用 `PRAGMA user_version` 记录版本，`EnsureUpgraded()` 每个版本只跑一次，**已发布的步骤不再修改**。当前 `Version = 3`。 |
| 原始区 | 新增 `IRawStore` / `RawArtifact` / `SqliteRawStore`。内容寻址：同一段文本 = 一个 artifact（sha256 去重）；字节落在 `state/raw/<hash 前两位>/<hash>.txt`；表 `raw_artifacts` 存元数据与 R1 概括。 |
| R1 概括离线降级 | 入库时**不依赖模型**生成概括：标题取首行（≤80 字，否则用来源名），概括取去空白后的前 200 字，`summary_kind = 'fallback'`。以后接模型只改这一个字段，不换表。 |
| 溯源四问 | `tasks` 与 `schedule_blocks` 增加四列：`partition`（默认 `main`）、`author`（默认 `user`）、`source_ref`（可空）、`representation`（默认 `value`）。 |
| 严格等级 | `SqliteTaskStore.NormalizeLevel` 不再静默把未知值变成 `short`：新增 `TryNormalizeLevel`，未知值返回 false；`NormalizeLevel` 抛 `ArgumentException`；`task.add` / `plan.add` 两个工具在**提议时**就返回失败并回话。 |
| 沙箱表 | 新增 `sandbox_entries` 表（`section / kind / text / created_at / source_ref / status`）。本轮只建表，写入路径下一节接。 |
| 运行时暴露 | `CogniCustosRuntime.Raw` 对外可读，应用内代码可取字节；**工具集里没有任何读原始字节的函数**。 |

### 2. 为什么（对应文档条款）

- §6.1「每一行都能回答四件事」→ 溯源四列；默认值让旧数据立即合规。
- §6.6「内容寻址、只增不改」→ hash 去重 + 独立字节文件 + 只提供 `Put`，没有 update/delete。
- §6.3「解析失败不猜」→ 等级严格化（原来 `NormalizeLevel` 的 `_ => "short"` 正是文档点名的通道）。
- §1.2/§3.1「原始区低可信、只能发现不能引用」→ `SearchSummaries` 只搜标题/概括/来源；读字节的 `ReadText` 不在任何工具里。
- §10「知识库/原始区缺失」→ 原始区从"不存在"变成"有表有字节有概括"。

### 3. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/SqliteSchema.cs` | **新增**，唯一的建表/迁移入口 |
| `src/CogniCustos.Core/RawStore.cs` | **新增**，`SqliteRawStore` |
| `src/CogniCustos.Contracts/Contracts.cs` | 新增 `RawArtifact`、`IRawStore` |
| `src/CogniCustos.Core/SqliteTaskStore.cs` | `Init()` 改为调用 `SqliteSchema.EnsureUpgraded`；等级严格化；日程存储同样走框架 |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | 创建并暴露 `Raw`；两个 applier 用严格等级 |
| `src/CogniCustos.Core/BuiltinTools.cs` | `task.add` 用严格等级，非法值在提议时失败 |
| `src/CogniCustos.Selftest/Program.cs` | 新增 6 条断言（37 → 43） |

### 4. 怎么验证

```bash
dotnet build -c Debug
dotnet run --project src/CogniCustos.Selftest -c Debug     # 期望 passed=43 failed=0
dotnet run --project tools/CogniCustos.Ui.Tests -c Debug   # UI 侧不受影响，仍应 ALL PASS
dotnet run --project samples/CogniCustos.Sample -- --check  # 小样链路仍通
```

新增断言覆盖：schema 版本可重入 / 四列齐全 / 同文本去重 / 概括离线且确定性 / 字节落盘且可回读 /
关键词只在概括层命中 / 未知等级被拒且工具回话。

### 5. 以后要改哪里（维护指引）

- **加表/加列**：`SqliteSchema.Version` 加 1，在 `EnsureUpgraded` 里加 `if (version < N) StepN...`，**不要改旧步骤**；
  已装机的库下次启动自动升级，无需手动迁移脚本。
- **换概括策略**（接模型）：只改 `SqliteRawStore.Put` 里 `summary` 与 `summary_kind` 的生成，
  建议新增 `summary_kind = 'model'`，DB 结构不动。
- **原始区加保留策略**：`raw_artifacts.status` 已经预留（`kept` / 以后 `expired`），清理逻辑应新写一个
  `RawRetention` 服务，**不要在 `RawStore` 里加删除**（§2.1 结构性关闭）。
- **加新的等级**：只改 `TryNormalizeLevel` 的两个分支（工具侧与 applier 侧都会跟着生效）。

### 6. 已知未做（下一节）

- 提议（`proposals.json`）还没进数据库，变更集与原子应用未做 → §6.2 仍不满足。
- `sandbox_entries` 只有表，`SandboxStore` 还在写 `SANDBOX.md` 整节覆盖 → §6.7 未满足。
- 详情卡片的人话渲染仍只覆盖 `schedule.add` → §6.4 未满足。

---

## 2026-11-24 · P0-2 沙箱条目化（SANDBOX.md 变成导出视图）

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 条目化存储 | `SandboxStore` 接数据库：每次写入在 `sandbox_entries` 插一行（`section / kind / text / created_at / source_ref / status`），**旧条目永不覆盖**。 |
| 文档降级为视图 | `SANDBOX.md` 仍在写，但内容改为「每个 section 取最新一条」的渲染结果 —— 是**导出/便于人看**，不是数据源。 |
| 思考也条目化 | `AppendThoughtAsync` 除了追加 `THOUGHTS.md`，同时记一条 `kind='thought'` 的条目。 |
| 新增应用内 API | `AppendEntryAsync(section, kind, text, sourceRef)` 与 `Entries(section, limit)`：前者给将来的"记录发现"用，后者给应用读（**都不是工具**，AI 看不到）。 |
| 构造参数 | `SandboxStore(root, dbPath?)`：不传 dbPath 时保持旧的纯文件行为（测试与外部调用不受影响）。 |

### 2. 为什么（对应条款）

- §6.7「沙箱整节覆盖可能丢失早期判断」→ 每次写入留一行 + 时间戳，`SANDBOX.md` 只呈现最新态。
- §1.2「沙箱：AI 自由写；用户可读、可改」→ 旧判断仍可查（`Entries`），用户干预可以另开条目标记。
- §2.1「结构性关闭」→ `SandboxStore` 只提供插入，没有 update/delete。

### 3. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/SandboxStore.cs` | 重写：条目化 + 导出视图 + `AppendEntryAsync` / `Entries` |
| `src/CogniCustos.Contracts/Contracts.cs` | 新增 `SandboxEntry` 记录 |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | 构造 sandbox 时传入同一个数据库路径 |
| `src/CogniCustos.Selftest/Program.cs` | 新增 2 条断言（43 → 45） |

### 4. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug     # 期望 passed=45 failed=0
```

新增断言覆盖：同一 section 写两次 = 两条条目、顺序与新→旧、时间戳递增、文件只含最新版；
思考条目 `kind='thought'`。原有"sandbox 写读一节""拒绝未知 section"两条仍然通过。

### 5. 以后要改哪里

- **加 section**：只改 `SandboxStore.Sections` 数组；`Render`/`Parse` 会自动覆盖。注意 `CogniCustosRuntime` 的自文档里提到 section 名，需同步。
- **给条目加字段**（例如"是否已被用户干预"）：`SqliteSchema.Version` +1，加一列；读取集中在 `Entries` 与 `Record` 两处。
- **换成条目浏览 UI**：`Entries(section)` 已经是现成数据源，直接给 ViewModel 用；不要再解析 Markdown。

### 6. 已知未做

- 用户干预（用户改沙箱）还没有单独标记入口 —— 计划在 P1 里给 `AppendEntryAsync` 传 `kind='intervention'`。
- 提议（`proposals.json`）仍未进数据库：变更集与原子应用是下一步（P0-3）。

---

## 2026-11-24 · P0-3 提议 = 变更集，确认 = 一个事务

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 变更集 | `Proposal` 从"一个 target + 一个 payload"变成 **`Changes: IReadOnlyList<Change>`**；`Target` / `Payload` 保留为"第一条"的便捷属性，旧的单目标调用点不用改。新增 `ProposeSet(title, summary, changes)`。 |
| 提议入库 | `proposals` + `proposal_changes` 两张表（schema v4）。内存里的 List 没了，**重启不丢**；`proposals.json` 改成导出视图，不再被读回。 |
| 原子确认 | `Approve(id)`：开一个事务 → 依次执行每条变更 → 任何一条失败（返回 `Ok=false` 或抛异常）就**整体回滚**、提议保持 Pending、返回"nothing was written" → 全部成功才提交并把状态改为 Approved。 |
| 事务型 applier | 门禁新增 `RegisterTransactionalApplier(target, (conn, tx, payload) => ...)`；`tasks.add` / `tasks.complete` / `plan.add` / `schedule.add` 四条都改成事务型，直接复用 store 新增的 internal 方法（`AddPlanned(conn,tx,...)`、`Complete(conn,tx,id)`、`schedule.Add(conn,tx,...)`）。 |
| 文件型目标的边界 | `kb.index` 仍是文件型（不能回滚），因此**只允许单条提议**；变更集里混入文件型目标会被拒绝并说明原因。 |
| 持久化细节 | 提议记录写库后立即导出 JSON；导出失败不影响决策（`Export()` 吞异常）。 |

### 2. 为什么（对应条款）

- §1.4「一条提议 = 一个变更集」→ `Changes` 列表。
- §6.2「一张卡、一次确认、一个事务（整批批准或整批驳回）」→ 单事务 + 全有或全无。
- §6.3「解析失败不猜」→ 坏 payload / 未知 target 在**确认时**整批回滚并报错，而不是写一半。
- §2.1「结构性关闭」→ 事务型 applier 只注册在门禁上；工具无法直接调用它们。
- §10 里"`Approve` 非原子"这条现在成立了。

### 3. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Contracts/Contracts.cs` | `Change`；`Proposal.Changes` + 便捷属性；`IApprovalGate.ProposeSet` |
| `src/CogniCustos.Core/ApprovalGate.cs` | 重写：SQLite 存储 + 事务应用 + JSON 导出 |
| `src/CogniCustos.Core/SqliteSchema.cs` | v4：`proposals` / `proposal_changes` |
| `src/CogniCustos.Core/SqliteTaskStore.cs` | 事务型 `AddPlanned` / `Complete` / `schedule.Add` |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | 门禁挂库；四条事务型 applier；`kb.index` 保持文件型 |
| `src/CogniCustos.Selftest/Program.cs` | 新增 4 条断言（45 → 49） |

### 4. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug     # 期望 passed=49 failed=0
```

新增断言：两个数据库落点一起成功；第二条失败时第一条也不留、提议仍 Pending；含文件型目标的变更集被拒；
换一个 runtime 打开同一目录仍能看到同一条提议并驳回成功。

### 5. 以后要改哪里

- **新增一种变更类型**：写一个 applier（数据库型走 `RegisterTransactionalApplier`），在 `CogniCustosRuntime.Create` 里注册，
  工具的 `Target*` 常量放在对应 Tools 类里。渲染文案（卡片上那一行）**下一步**才做，见 P1。
- **加一种文件型目标**：只能用 `RegisterApplier`，并且它不能和别的变更同批（门禁会拒）。
- **改事务边界**：`ApprovalGate.Approve` 是唯一入口；不要把存储操作散到 applier 之外。
- **加"部分批准"**：现在刻意不支持；要做的话在 `Approve` 里按 ord 逐条提交，并给提议加 `approved_ord` 记录。

### 6. 已知未做

- **预演（dry-run）还没做**：现在"失败就回滚"，但用户仍要在点确认之后才知道会失败（§6.4 的"不可确认"状态）。
  下一节 P1 先做粗版：确认前跑一次"空事务"预演，任一条失败就把卡片标红。
- 卡片的**人话渲染**仍只有 `schedule.add`；变更集在多落点时卡片还没显示成多行。

---

## 2026-11-24 · P1 变更集的人话渲染、粗预演、卡片按落点分行

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 渲染器 | 新增 `ChangeRenderer.Describe(Change)` → `ChangeNarrative(Target, Label, Line, Problem)`。每种 target 渲染"即将真正写入的字段"：任务名/截止/级别、日程日期与时段、计划层级与父项、完成任务号、知识库来源与字数。payload 缺字段或不是 JSON → `Problem` 非空（`Ok=false`）。 |
| 预演（粗版） | `ApprovalGate.Preview(id)` → `ProposalPreview(Ok, Changes, Problem)`：先渲染全部变更，再把数据库类变更放进一个**必定回滚**的事务里真跑一遍。失败就把原因带回卡片；文件型目标（`kb.index`）标注"无法预演"并放行。 |
| 卡片 | 示例的待处理卡改成**一条提议一行一个落点**：行文本 = 渲染结果，行首显示提议标题，右侧标注变更类型，第一行还带 `✓ 可确认` / `✗ …` 的预演状态。 |
| 失败可见 | `IShellDataSource.IncomingConfirmed/Ignored` 改为返回 `string?`：null = 成功，非 null = 展示给用户的原因。ViewModel 收到失败时**不新增计划行、不删待处理条目**，只把原因写进状态栏。 |
| 常量对齐 | 渲染器用 `BuiltinTools.Target*` / `PlannerTools.Target*` 常量做分支，不再写字面量（测试当场抓到 `plans.add` 被写成 `plan.add` 的 bug）。 |

### 2. 为什么（对应条款）

- §6.4「每个变更类型必须有人话渲染，且渲染的是即将真正写入的字段」→ `ChangeRenderer`。
- §6.3「解析失败不猜」+ 我上一节提的"整批批准要先预演"→ `Preview`（粗版：真跑再回滚，不是静态分析）。
- "看不懂的卡片等于自动批准" → 行文本直接是字段值，不是摘要。
- 失败要可见：确认被拒时卡片内容不变、原因写状态栏，而不是静默吞掉。

### 3. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/ChangeRenderer.cs` | **新增** |
| `src/CogniCustos.Core/ApprovalGate.cs` | `Preview(id)` |
| `src/CogniCustos.Contracts/Contracts.cs` | `ChangeNarrative`、`ProposalPreview`、`IApprovalGate.Preview` |
| `src/CogniCustos.Ui/ViewModels/IShellDataSource.cs` | `IncomingConfirmed/Ignored` 返回 `string?` |
| `src/CogniCustos.Ui/ViewModels/DemoShellDataSource.cs` | 返回 null |
| `src/CogniCustos.Ui/ViewModels/ShellViewModel.cs` | 确认被拒 → 状态栏 + 不动看板 |
| `samples/CogniCustos.Sample/RuntimeShellDataSource.cs` | 按落点分行 + 预演状态 + 真 Approve/Reject |
| `tools/CogniCustos.Ui.Tests/Program.cs` | 新增 1 条断言（57 → 58） |
| `src/CogniCustos.Selftest/Program.cs` | 新增 4 条断言（49 → 53） |

### 4. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug          # 期望 passed=53 failed=0
dotnet run --project tools/CogniCustos.Ui.Tests -c Debug        # 期望 ALL PASS（58）
dotnet run --project samples/CogniCustos.Sample -- --check      # 期望 OK
```

新增断言覆盖：四种变更的渲染文案 / 坏 payload 在预演期就被报出 / 有效变更集"预演不写、随后确认仍能写" /
无效变更集预演失败且不写 / UI 侧"确认被拒时不新增计划行、不删条目、原因进状态栏"。

### 5. 以后要改哪里

- **加一种变更类型**：先在 Tools 里加 `Target*` 常量，再在 `ChangeRenderer` 的 switch 里加一个分支（用常量），
  最后在 `CogniCustosRuntime` 注册 applier。两处漏一个，测试里的渲染断言会失败。
- **预演升级为静态分析**（不真跑）：把 `Preview` 里"apply + rollback"换成"解析 payload + 校验引用存在"；
  接口不变，调用方无感。
- **卡片要多落点换行更细**：行数据就是 `ChangeNarrative`，`CardItem` 只是承载；要加图标/颜色时改 `RuntimeShellDataSource` 的映射即可。

### 6. 已知未做

- 预演还是"跑一遍再回滚"：对真实副作用（比如将来带网络的 applier）不安全，届时必须先做静态校验。
- 卡片上仍不显示"变更集共几条"的汇总行；多落点时第一行的状态挂在第一行上。
- 抓取器仍未接入原始区（`raw_artifacts` 目前只有测试写入）。

---

## 2026-11-24 · P1.5 抓取器接原始区 + 卡片汇总行

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 门户读取落原始区 | `PortalSidecarTool` 每次成功读取都先 `IRawStore.Put("portal", url, text, title)`，返回值末尾附 `[raw #id]`。同一页面读两次 = 一个 artifact（内容寻址）。 |
| 可注入的读取器 | 构造参数新增 `IRawStore? raw` 与 `Func<url, domain, (ok, body)>? fetch`：测试可以注入假读取器，不需要真 sidecar。 |
| 模型的发现面 | 新增两个只读工具 `raw.list`（最近条目：id/标题/来源/摘要类型/字数）与 `raw.search`（按关键词搜**摘要**）。**没有 `raw.read` / `raw.get`**：字节只能由应用读（`IRawStore.ReadText` 不在工具里）。 |
| 卡片汇总行 | 变更集超过一处写入时，第一行标题显示 `… · 共 N 处写入`（不新增控件）。 |
| 示例自检 | `--check` 现在还会造一个两落点变更集，断言待处理卡出现两行、且首行带"共 2 处写入"。 |

### 2. 为什么（对应条款）

- §1.3「外部 → 原始」这条通道原来只有"预留接口"，现在真的写入；§10 里"原始区没有写入者"不再成立（邮件仍未接）。
- §3.1「R0 只能发现」→ 模型只见摘要；`raw.search` 的输出明确标注 "unconfirmed material"。
- §6.6「内容寻址、同一文件自动去重」→ 门户读两次是一个 artifact。

### 3. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/RawTools.cs` | **新增**：`raw.list` / `raw.search` |
| `src/CogniCustos.Adapters/PortalSidecarTool.cs` | 写原始区 + 可注入读取器 |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | 注册 `RawTools` |
| `src/CogniCustos.Desktop/MainWindow.cs` | 传入 `rt.Raw` |
| `samples/CogniCustos.Sample/RuntimeShellDataSource.cs` | 汇总行 |
| `samples/CogniCustos.Sample/SelfCheck.cs` | 两落点卡片断言 |
| `src/CogniCustos.Selftest/Program.cs` | 新增 3 条断言（53 → 56） |

### 4. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug        # passed=56
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --check
# 期望出现：card rows: 2 (multi landing point: True) / OK
```

断言覆盖：`raw.list`/`raw.search` 只给摘要且不存在 `raw.read`；门户读取落库并回传 handle；
同一页面两次读取是同一个 artifact。

### 5. 以后要改哪里

- **接入邮件**：写一个和 `PortalSidecarTool` 同形的工具（`mail.read`），内部同样 `raw.Put("mail", ...)`；
  **不要**直接往 `raw_artifacts` 写 SQL，走 `IRawStore` 才能保证去重与摘要一致。
- **给抓取器加"来源时间/抓取者"**：`IRawStore.Put` 现在只有 kind/source/text/title；要加字段就扩展接口 + `SqliteSchema` v5。
- **摘要接模型**：只改 `SqliteRawStore.Put` 的 `summary` 与 `summary_kind`（`model`），`raw.search` 无需改动。

### 6. 已知未做

- 邮件抓取、定时抓取、抓取失败的记录都还没做（现在只有"读一次写一次"）。
- 原始区的保留/清理策略（§8 待定）未实现，`status` 列已预留。
- 变更集卡片还没有"整批不可确认时置灰"的交互（现在只是把 `✗ 原因` 写在行上，按钮仍可点，点了会被门禁拒绝）。

---

## 2026-11-25 · P2 可见性阶梯与隔离判定

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 阶梯入库 | schema v5：`raw_artifacts.visibility` 默认 `R1`、`sandbox_entries.visibility` 默认 `S`。`RawArtifact` / `SandboxEntry` 带上该字段（有默认值，旧构造点不受影响）。 |
| 单一闸门 | 新增 `Visibility`：`AllowsMainContext`（只有 `K`/`M` 为真）、`CanBeCited`、`Normalize`（未知值一律降到 `R0`，**绝不静默升格**）、`Label`（给界面用的中文标签）。 |
| 知识库只收已确认 | `KeywordKnowledgeBase` 的每条记录带 `visibility=K`；`SearchAsync` 在返回前过滤 `CanBeCited`，所以检索结果天然可引用。索引文件里能看到 `"K"`。 |
| 判定器 | 新增 `IJudge` / `JudgeVerdict` / `KeywordJudge`：输入是问题 + 候选 artifact，**只读标题与 R1 概括**，输出"相关/不相关 + 类别 + 分数 + id"，**不回传正文**。类别桶：日程 / 邮件 / 课程 / 任务 / 其他。运行时暴露 `rt.Judge`。 |
| 原始工具标注 | `raw.list` / `raw.search` 的输出带上 `R1 · 原始概括` 这类标签，模型看得到"这是未确认材料"。 |

### 2. 为什么（对应条款）

- §3.1 五档可见性 → 落到列上，不再靠约定。
- §3.2「未确认内容不进入主对话」→ `AllowsMainContext` 是唯一判断点；判定器只回裁决与句柄。
- §3.3「发现 / 读取 / 确认」三种同意 → 发现走 `raw.search`（R1），确认才进 K/M。
- §2.3「窄工具面」→ 判定不是工具，是应用侧能力（`rt.Judge`），模型无法自我审查式地"绕过判定"。

### 3. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/Visibility.cs` | **新增**：阶梯常量 + 唯一闸门 |
| `src/CogniCustos.Core/Judge.cs` | **新增**：`IJudge` / `JudgeVerdict` / `KeywordJudge` |
| `src/CogniCustos.Core/SqliteSchema.cs` | v5：visibility 两列 |
| `src/CogniCustos.Contracts/Contracts.cs` | 两条记录带 `Visibility`（默认值） |
| `src/CogniCustos.Core/RawStore.cs` / `SandboxStore.cs` | 读写该列 |
| `src/CogniCustos.Core/KeywordKnowledgeBase.cs` | 记录带 K、检索过滤可引用者 |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | 暴露 `Judge` |
| `src/CogniCustos.Core/RawTools.cs` | 输出带可见性标签 |
| `src/CogniCustos.Selftest/Program.cs` | 新增 4 条断言（56 → 60） |

### 4. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug     # passed=60
```

断言覆盖：R1/S 不能进主上下文、K/M 可以、未知值降到 R0；知识库只收 K 且检索结果可引用；
判定器对相关材料给"相关 + 类别 + 标题"且**不含正文**；概括只有前 200 字，**超出部分判定器看不到**
（同一句话放进概括里就能命中，放在 400 字之后就不命中）。

### 5. 以后要改哪里

- **接真模型做判定**：实现另一个 `IJudge`（输入同样只有标题+概括），在 `CogniCustosRuntime` 里替换 `new KeywordJudge()`；
  注意返回类型里不要加"摘要/正文"字段，那会让调用方有机会把正文带进主对话。
- **加一档可见性**：改 `Visibility` 常量与三个函数，并在 schema 加迁移（旧值默认不动）。
- **让判定结果进主对话**：应该在 `ChatAsync` 里把"相关 + id"渲染成一句询问，**不要**把 material 直接塞进 prompt。

### 6. 已知未做

- 判定器还没接进 `ChatAsync`：现在只有应用侧能调（`rt.Judge`），对话里不会主动提"我发现一份相关材料"。
- 用户"确认收录"到知识库的入口还没有（现在只有 `kb.index` 建议 + 确认这条路径）。
- 概要是"前 200 字"的确定性降级，接内置本地模型后会换成真摘要（`summary_kind=model`）。
