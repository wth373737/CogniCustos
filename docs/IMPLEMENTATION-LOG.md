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

---

## 2026-11-25 · P3 判定进对话 + 用户自己收录

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 对话里提一句 | `ChatAsync` 现在先跑一次判定：取「未被收录」的原始材料，命中相关的（最多 2 条）就在回答前加一行——`（未确认材料，未进入上下文）#id「标题」类别 —— 要确认后我再用吗？`。**只有句柄与标题，不带摘要或正文**。 |
| 用户收录通道 | `rt.AcceptRawAsync(id)`（应用侧，不是工具）：应用读字节 → 写知识库（`source = "raw:<id>"`）→ 把该条目状态标成 `accepted` → 记一条沙箱思考。 |
| 收录后不再追问 | 判定只针对 `status != accepted` 的条目；已收录的不会再被当作"未确认材料"提起（断言精确到 **这个 id 不再出现**，其他未确认材料仍可被提）。 |
| 状态可写 | `IRawStore.SetStatus(id, status)`，`raw_artifacts.status` 从 `kept` 走向 `accepted`（保留策略以后用同一列）。 |

### 2. 为什么（对应条款）

- §3.2「判定对话只回裁决与句柄」→ 主对话拿到的是一行提示，不是材料。
- §3.3「三种同意：发现 / 读取 / 确认」→ 发现 = `raw.search`；确认 = `AcceptRawAsync`；中间那次读取由应用直接做，不经过模型。
- §6.6「删除原始条目不影响已确认的事实」→ 收录时把文本复制进知识库，之后原始字节可以清理。

### 3. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | `ChatAsync` 前置提示；`AcceptRawAsync`；`MaterialNotice` |
| `src/CogniCustos.Contracts/Contracts.cs` | `IRawStore.SetStatus` |
| `src/CogniCustos.Core/RawStore.cs` | 实现 `SetStatus` |
| `src/CogniCustos.Selftest/Program.cs` | 新增 4 条断言（60 → 64） |

### 4. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug     # passed=64
```

断言覆盖：相关问题会看到"未确认材料 + #id + 标题"且**不引用正文**；无关问题没有提示；
`AcceptRawAsync` 之后知识库能按 `raw:<id>` 检索到、状态变 `accepted`、该 id 不再被提；未知 id 失败。

### 5. 以后要改哪里

- **判定换成模型**：实现新的 `IJudge`（输入仍只有标题 + 概括），替换 `CogniCustosRuntime` 里 `new KeywordJudge()`；
  **不要**给 `JudgeVerdict` 加"摘要"字段，否则提示里迟早会把正文带出来。
- **提示文案/位置**：改 `MaterialNotice` 一处即可；若以后要在 UI 里做成卡片，把返回的 id 列表暴露给 ViewModel，
  不要在这里拼界面文案。
- **用户收录入口做成界面**：接 `rt.AcceptRawAsync(id)`，卡片上「确认收录」按钮即可；原始字节由应用读，不用过模型。

### 6. 已知未做

- 判定仍是关键词/字符重合（`KeywordJudge`），中文短问题容易误命中——这就是"接内置模型"那一轮要替换的东西。
- 界面上还没有"未确认材料"列表与"确认收录"按钮（数据与 API 已就绪）。
- 概要是前 200 字降级；`accept` 之后知识库里存的仍是原文，将来接模型后可换成结构化摘要。

---

## 2026-11-25 · M 内置本地模型 ⇄ Ollama 可切换

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 状态持久化 | `AppSettings` 新增 `LocalModelSource`（`builtin` / `ollama` / `none`）与 `BuiltInInstalled`；Ollama 地址**写死** `http://127.0.0.1:11434`。 |
| 切换服务 | 新增 `LocalModelService`：`UseOllama(model)` / `UseBuiltIn()` / `DeleteBuiltIn()` / `StatusText()`；`ModelRouter.ReplaceRole` 让路由在切换时替换角色，而不是叠加。 |
| 切到 Ollama 自动清理 | 按设计：`UseOllama` **先删掉内置模型**（`<root>/models/builtin`，返回释放的 MB 数），再写设置、再注册 Ollama provider。失败（模型名为空、删除失败）时不切换。 |
| 删空也安全 | 内置模型本来就不在时，`DeleteBuiltIn` 返回"没有可删除的内置模型"，仍把 `BuiltInInstalled=false` 落盘，不报错。 |
| 设置页 | "I have custom local models, using Ollama" 那一行变成可点按钮 → 打开**小窗**：地址只读（固定）、只填模型名、说明里写明"启用会删除自带模型以释放空间；只想额外加模型请用下面的添加按钮"。 |
| 反馈 | 成功 → 关闭小窗、把 "Small local model" 行的状态改成 `Ollama · <model>` 并提示；失败 → **小窗保持打开**、原因进状态栏。 |
| 解耦 | UI 通过 `IShellDataSource.LocalModelStatus()` / `EnableOllama(model)` 与后端交互，示例里接到 `rt.LocalModels`；UI 不引用 Core。 |

### 2. 为什么（对应条款与你的说明）

- §8.7「内置运行时第一顺位、Ollama 是进阶手动选项」→ 地址固定、只需模型名。
- 你补充的语义："用 Ollama 按钮的目的是清理硬盘，不是并存" → 切换即删除；**并存**要走 "Add custom models and services"。
- §2.3「窄面」→ 删除用的是 `Directory.Delete` 一处，没有给模型层加任何工具。

### 3. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/LocalModelService.cs` | **新增** |
| `src/CogniCustos.Core/SettingsAndReminders.cs` | 两个设置字段 |
| `src/CogniCustos.Core/ModelRouter.cs` | `ReplaceRole` |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | 暴露 `LocalModels` |
| `src/CogniCustos.Ui/ViewModels/IShellDataSource.cs` | 两个新成员 |
| `src/CogniCustos.Ui/ViewModels/ModelConfigurationViewModel.cs` | 小窗状态与事件 |
| `src/CogniCustos.Ui/ViewModels/ShellViewModel.cs` | `EnableOllama` 编排 + 状态种子 |
| `src/CogniCustos.Ui/Views/SettingsPage.axaml` | 可点行 + 小窗 + 遮罩 |
| `samples/CogniCustos.Sample/RuntimeShellDataSource.cs` | 接到 `rt.LocalModels` |
| `tools/CogniCustos.Ui.Tests/Program.cs` | 新增 2 条（58 → 60） |
| `src/CogniCustos.Selftest/Program.cs` | 新增 3 条（64 → 67） |

### 4. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug       # passed=67
dotnet run --project tools/CogniCustos.Ui.Tests -c Debug     # ALL PASS（60）
dotnet run --project tools/CogniCustos.Ui.Shots -c Debug -- artifacts/ui 1280 720
# 新状态：settings-ollama
```

断言覆盖：切到 Ollama 会删除内置模型目录、设置落盘为 `ollama`、路由里出现 `ollama:<model>`；
地址固定且模型名必填；删除不存在的内置模型不报错；界面把小窗里的模型名传到后端，
成功后关窗并更新行状态，失败时小窗不关、原因进状态栏。

### 5. 以后要改哪里

- **接真 Ollama**：`LocalModelService` 构造时可注入 `Func<url, model, IModelProvider>`；桌面端传
  `new OllamaModelProvider(url, model)`，测试传假的。默认实现（`StandbyOllamaProvider`）只报告不可用。
- **内置模型换成真运行时**：`BuiltInPath` 下的 `model.json`（`{"name": "..."}`）与权重文件就是约定好的位置；
  接 LLamaSharp 时只需在同一目录放权件 + 在 `UseBuiltIn()` 里注册 provider。
- **加"并存"路径**：不要改 `UseOllama`；那是 "Add custom models and services" 的职责，应当只追加 provider、
  不删除任何东西。

### 6. 已知未做

- 内置模型的实际下载/校验还没做（`models/builtin` 目前由测试或手工放置）。
- "Add custom models and services" 仍是占位窗。
- 切换到 Ollama 后是否真的可用，取决于 `OllamaModelProvider.Probe()`；示例进程里注入的是占位 provider（诚实报告不可用）。

---

## 2026-11-25 · LM 内置本地模型真的跑起来了（llama.cpp）

### 0. 实测数字（本机 Ryzen 9 7900X，CPU 推理）

| 项 | 数值 |
|---|---|
| 运行时依赖 | `LLamaSharp 0.30.0` + `LLamaSharp.Backend.Cpu 0.30.0`；单个平台的 natives（llama.dll / ggml*.dll / mtmd.dll）约 **4.8 MB**（all-RID 输出目录 108.7 MB，发布时只带 win-x64 约 5 MB） |
| 模型 | `qwen2.5-0.5b-instruct-q4_k_m.gguf`，**468.6 MB**（磁盘）；运行时 CPU compute buffer 约 **300 MiB** |
| 切换耗时 | **0.01 s**（模型是懒加载，切换不加载） |
| 第一次回答 | **2.1 s**（含加载模型；之后同样问题 1 s 上下） |
| 模型摘要（R1） | **0.6 s**，落库 `summary_kind = model` |

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 运行时 | `CogniCustos.Adapters.LlamaModelProvider`：Qwen2.5 模板、`MaxTokens=512`、CPU 推理；懒加载，缺文件时 `IsAvailable=false` 让路由退回 standby。 |
| natives 加载 | `LLamaSharp.Backend.Cpu` 把库放在 `runtimes/<rid>/native`（最佳实现在 `avx2/`），而默认加载器只看 exe 旁边 → 首次使用时把该目录**整套**拷到 exe 同级（llama + ggml-base + ggml-cpu + ggml + mtmd）。 |
| 非 ASCII 路径 | llama.cpp 在 Windows 用窄字符打开 GGUF，中文目录会 "No such file" → 先取 **8.3 短路径**（`GetShortPathNameW`，`I:\AGENT~1\...`），失败再镜像到 LocalApplicationData（沙箱环境不允许写那里，但正常桌面可用）。 |
| 下载与校验 | `BuiltInModelInstaller`：下载到 `.downloading` → 校验大小（sha256 记录进 `model.json`）→ 移动到正式名；`InstallBuiltInAsync` 之后自动切到内置。 |
| 切换 | `rt.SwitchToBuiltIn()`：切模型 + 把该 provider 接到 `Raw.Summarizer`（摘要用模型，失败自动回退）；`rt.SwitchToOllama(model)`：切走并把 summarizer 置空。 |
| 接线 | 桌面与小样在 `Create` 之后设置 `LocalModels.BuiltInFactory = path => new LlamaModelProvider(path)`（Core 不能引用 Adapters，所以用工厂注入）。 |
| 自检工具 | `CogniCustos.Sample --model-check`：缺模型就下载（带 % 进度）、切换、问一句、跑一次模型摘要，打印全部耗时。 |

### 2. 为什么（对应条款）

- §8.7「内置运行时第一顺位 + 开箱即用」→ 装好即用、切换 0.01s、首答 2.1s。
- §6.9「概括必须有离线降级」→ `Raw.Summarizer` 为空或抛异常时仍是 `fallback`，入库不受影响。
- §2.3「窄面」→ 模型只做两件事：对话、摘要；没有任何工具能把原始字节塞进提示词。

### 3. 涉及文件

| 文件 | 变化 |
|---|---|
| `Directory.Packages.props` | LLamaSharp 0.30.0 / LLamaSharp.Backend.Cpu 0.30.0 |
| `src/CogniCustos.Adapters/LlamaModelProvider.cs` | **新增** |
| `src/CogniCustos.Core/BuiltInModelInstaller.cs` | **新增** |
| `src/CogniCustos.Core/LocalModelService.cs` | installer、`BuiltInFactory`、`InstallBuiltInAsync`、`UseBuiltIn` 接真 provider |
| `src/CogniCustos.Core/RawStore.cs` + `Contracts.cs` | `Summarizer` 钩子（接口上） |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | `SwitchToBuiltIn` / `SwitchToOllama` |
| `samples/CogniCustos.Sample/ModelCheck.cs` + `Program.cs` | `--model-check` |
| `src/CogniCustos.Desktop/MainWindow.cs`、`samples/.../SampleWindow.axaml.cs` | 注入内置 provider 工厂 |
| `src/CogniCustos.Selftest/Program.cs` | 新增 3 条（67 → 70） |

### 4. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug          # passed=70
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --model-check
# 期望：installed 468 MB / switch 0.01s / reply time ~2.1s / summary: model ~0.6s
```

### 5. 以后要改哪里

- **换模型**：改 `BuiltInModelInstaller.ModelFileName` / `DownloadUrl`（以及 `ExpectedBytes`），再把 `LlamaModelProvider.Wrap` 的对话模板换成该模型的模板；`model.json` 会记录实际 sha256。
- **换后端（CUDA / Vulkan / Metal）**：把 `LLamaSharp.Backend.Cpu` 换成对应包即可，代码里 `GpuLayerCount=0` 是唯一的 GPU 相关设置。
- **卸载**：`LocalModelService.DeleteBuiltIn` 删的是 `<root>/models/builtin`；8.3 短路径不复制文件，所以不会残留。
- **非 ASCII 路径**：正常桌面会走短路径（不占额外磁盘）；只有 8.3 被关掉且目录非 ASCII 时才走镜像，镜像位置在 `%LOCALAPPDATA%\CogniCustos\models`，排查时可删。

### 6. 已知未做

- 模型下载**没有**在 UI 里做成进度条（只有 `--model-check` 与 `InstallBuiltInAsync` 的 `IProgress`）；"Check" 按钮仍只弹占位窗。
- 摘要走模型后**入库会慢 0.6s**（同步等待），批量抓取时应改成后台队列。
- 判定器（`IJudge`）仍是关键词实现，没有换成模型版。

---

## 2026-11-25 · LM-GPU 用我们的 AMD 显卡跑（Vulkan）

### 0. 实测（同机、同模型、同问题、预热后）

| 后端 | 加载+预热 | 回答 | 速度 |
|---|---|---|---|
| CPU（Ryzen 9 7900X，24 线程） | 1.2 s | 0.5 s | **86 字/秒** |
| **Vulkan（Radeon RX 7900 XTX）** | 0.8 s | **0.2 s** | **374 字/秒** |

约 **4.3 倍**（0.5B 的小模型本来 CPU 就够用；模型越大差距越明显）。

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 后端包 | 只在**示例项目**里加 `LLamaSharp.Backend.Vulkan 0.30.0`（natives：`ggml-vulkan.dll` 48.4 MB + llama/ggml 若干，合计约 52 MB）。产品默认仍是 CPU。 |
| provider | `LlamaModelProvider(path, contextSize, gpuLayers, preferVulkan)`；`gpuLayers>0 && preferVulkan` 时从 `runtimes/<rid>/native/vulkan` 取 natives，并显式指定 `ModelParams.Devices = ["Vulkan0"]`。 |
| 切换后端 | 复制 natives 时**必须覆盖**：上一次 CPU 跑留下的 `llama.dll` 会挡住 Vulkan 版（它的日志会安静地只列出 CPU 设备）。 |
| 测量工具 | `--model-check --gpu`：同一流程换 Vulkan，并先跑一次预热再计时，输出字/秒。 |

### 2. 踩到的两个坑（都已修）

1. **只设 `GpuLayerCount` 不够**：0.30 把设备选择交给 llama.cpp 的 `devices` 参数，不写 `Devices=["Vulkan0"]` 时 Vulkan 后端虽然被加载，但每一层仍然 `assigned to device CPU`（日志能直接看到）。
2. **旧后端的 natives 会赢**：拷贝时 `overwrite:false` 导致 CPU 的 `llama.dll` 留在 exe 旁，Vulkan 版没生效；改成切换后端时覆盖。

### 3. 怎么验证

```bash
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --model-check --gpu
# 日志里应出现：load_tensors: layer N assigned to device Vulkan0
# 以及：reply time 约 0.2s
```

### 4. 以后要改哪里

- **产品里开 GPU**：把 `LLamaSharp.Backend.Vulkan` 从示例挪到 `CogniCustos.Adapters`，并在 `LocalModelService` 里把 `gpuLayers/preferVulkan` 做成设置项（例如"硬件加速：自动/关闭"）。
- **NVIDIA**：换成 `LLamaSharp.Backend.Cuda12`，设备名 `CUDA0`，其余不变。
- **多显卡/核显**：`Devices` 按优先级列出，例如 `["Vulkan1"]` 指定独显；设备名以 llama.cpp 日志里的枚举为准。
- **显存不足**：`gpuLayers` 从 99 往下调，让尾巴留在 CPU。

### 5. 已知未做

- 设置页还没有"硬件加速"开关（现在只有命令行 `--gpu`）；GPU 用户的显存/驱动探测也没做。
- Vulkan 包让示例输出目录多 ~52 MB；发布时应按用户环境选择包，而不是全部带上。

---

## 2026-11-25 · LM-UI 设置页的 Check / Delete 接到真下载与真删除

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 接缝 | `IShellDataSource` 新增 `InstallBuiltIn(IProgress<double>)`、`DeleteBuiltIn(out string message)`、`SwitchToBuiltIn()`；UI 仍不引用 Core。 |
| Check | 真下载（缺模型时）→ 校验 → 切到内置。下载在 `Task.Run` 上跑，进度经 `Progress<double>` + `Dispatcher.UIThread.Post` 回到界面；下载期间 Check/Delete 置灰，行下方显示"下载中 N%"。 |
| Delete | 走 `LocalModels.DeleteBuiltIn`（真删 `<root>/models/builtin`），结果（释放多少 MB / 没有可删）进状态栏；删完刷新行状态。 |
| 语言 | 新增 `Settings.Model.Installing` / `Downloading` 两条资源。 |
| 示例 | `RuntimeShellDataSource` 直接把三个成员接到 `rt.LocalModels` / `rt.SwitchToBuiltIn`。 |

### 2. 踩到并修掉的一个时序 bug

进度回调是 **post** 到 UI 线程的，可能在"安装结束、已经清空提示"之后才到达，于是行下留下 `Downloading 50%`。
修法：`ReportInstallProgress` 在 `!IsBusy` 时直接忽略——晚到的进度不允许把空闲状态重新点亮。

另有一处 XAML 事故：进度文字被插到了 Ollama 那个 `Button` 里面，导致按钮有了两个 Content，Avalonia 直接报
`AVLN3000 ... multiple assignments to the property Content`，整包 XAML 编译失败（表现是测试宿主
"No precompiled XAML found"）。已把文字移到按钮外。

### 3. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Ui/ViewModels/IShellDataSource.cs` | 三个新成员 |
| `src/CogniCustos.Ui/ViewModels/DemoShellDataSource.cs` | 演示实现（下载"瞬间完成"） |
| `src/CogniCustos.Ui/ViewModels/ModelConfigurationViewModel.cs` | `InstallNote` / `IsBusy` / `ReportInstallProgress` / `CompleteOllamaSwitch` |
| `src/CogniCustos.Ui/ViewModels/ShellViewModel.cs` | `CheckLocalModelAsync`、删除改走数据源 |
| `src/CogniCustos.Ui/Views/SettingsPage.axaml` | 进度行、忙碌置灰 |
| `samples/CogniCustos.Sample/RuntimeShellDataSource.cs` | 接真实现 |
| `tools/CogniCustos.Ui.Tests/Program.cs` | 新增 1 条（60 → 61） |

### 4. 怎么验证

```bash
dotnet run --project tools/CogniCustos.Ui.Tests -c Debug    # ALL PASS（61）
dotnet run --project src/CogniCustos.Selftest -c Debug      # passed=70
```

新增断言：点 Check 会让数据源执行安装、期间忙碌、结束后提示消失、行上显示已安装的模型名。

### 5. 以后要改哪里

- **换下载源/加校验**：只改 `BuiltInModelInstaller`（`DownloadUrl`、`ExpectedBytes`、`model.json` 里的 sha256）。
- **给 Check 加"检测硬件推荐模型"**：在 `CheckLocalModelAsync` 里先调探测器，选好 `ModelFileName` 再 download；
  界面已有进度位，不需要新控件。
- **下载可取消**：`InstallBuiltInAsync` 已接受 `CancellationToken`，只需把界面的取消按钮接到它。

### 6. 已知未做（我记录里剩下的两件）

1. **摘要仍同步**：`Raw.Put` 走模型摘要时会阻塞约 0.6 s；批量抓取应改为后台队列（下一步）。
2. **判定器仍是关键词版**：`IJudge` 的模型实现还没做（再下一步）。

---

## 2026-11-25 · LM-J 判定器换成模型版 + 硬件加速开关

### 1. 判定器（`ModelJudge`）

| 项 | 内容 |
|---|---|
| 契约不变 | 仍是 `IJudge.Find(question, candidates)` → `JudgeVerdict`（裁决 + 句柄），模型只看到**标题 + R1 概括**（每条截 80 字，最多 8 条）。 |
| 双层结构 | 关键词判定**总是先跑**（免费、可复现、就是兜底答案）；模型可用时再问一次，用模型的裁决覆盖对应的 id，模型没提的 id 保留关键词结果。 |
| 容错 | 模型输出不是 JSON、或没模型、或模型抛异常 → 直接用关键词结果，不抛错、不空返回。 |
| 接线 | `CogniCustosRuntime.Judge` 跟着当前模型走：`SwitchToBuiltIn` / `SwitchToOllama` 里换成 `ModelJudge`，没有模型时回到 `KeywordJudge`。 |
| 实测 | `--model-check --gpu`：`judge : ModelJudge`，`judge hit : #2 relevant=True category=日程`——由 0.5B 模型自己判的相关性。 |

### 2. 硬件加速开关

| 项 | 内容 |
|---|---|
| 设置 | `AppSettings.UseGpu`；`LocalModelService.UseGpu`（读写即落盘）、`GpuStatusText()`。 |
| 生效方式 | `BuiltInFactory` 签名变成 `(path, gpu)`；桌面/示例传 `gpu ? 99 层偏移 + Vulkan : CPU`。切换开关后**下一次**模型调用生效（provider 在切换模型时重建）。 |
| 界面 | 模型配置页"本地小模型"下面一行：左侧"硬件加速（Vulkan）"+ 说明（"在 CPU 上运行；有支持的显卡时约慢 4 倍"/"在显卡上运行（Vulkan）…"），右侧一个 chip 显示"已开启/已关闭（CPU）"，点击切换。 |
| 语言 | 标签、状态、说明三条资源都随中英切换刷新（`RefreshText` 里补了三个属性）。 |

### 3. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/Judge.cs` | 新增 `ModelJudge` |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | `Judge` 可变 + 两处切换 |
| `src/CogniCustos.Core/SettingsAndReminders.cs` / `LocalModelService.cs` | `UseGpu`、工厂签名 |
| `src/CogniCustos.Ui/...`（IShellDataSource/ModelConfigurationViewModel/ShellViewModel/SettingsPage.axaml/Lang.*） | 开关的接缝、状态、行与资源 |
| `src/CogniCustos.Desktop/MainWindow.cs`、`samples/.../SampleWindow.axaml.cs`、`ModelCheck.cs` | 工厂改成带 GPU 参数；`--model-check` 顺带验证判定器 |
| `src/CogniCustos.Selftest/Program.cs` | 新增 3 条（70 → 73） |
| `tools/CogniCustos.Ui.Tests/Program.cs` | 新增 1 条（61 → 62） |

### 4. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug        # passed=73
dotnet run --project tools/CogniCustos.Ui.Tests -c Debug      # ALL PASS（62）
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --model-check --gpu
# 期望：judge : ModelJudge / judge hit : #N relevant=True category=…
```

判定器断言：模型裁决被采用并分类；模型输出无法解析时与关键词结果完全一致；没有模型时仍是关键词结果。

### 5. 以后要改哪里

- **换判定提示词/阈值**：`ModelJudge.Ask` 一处；`MaxSummaryChars` 控制给模型看多少概括。
- **让判定更省**：现在是每条消息都调一次模型（GPU 上 ~0.2 s）。要省就在 `Find` 里先看关键词是否已有高分命中，命中就跳过模型。
- **硬件加速默认开**：把 `AppSettings.UseGpu` 默认值改成 `true` 即可；注意没有 Vulkan 驱动的机器会退化（`preferVulkan` 找不到 natives 时仍走 CPU 文件，但需要包存在）。
- **NVIDIA**：换 `LLamaSharp.Backend.Cuda12`，`Devices=["CUDA0"]`。

### 6. 已知未做

- 摘要仍是同步调用（记录里的最后一件事）。
- 硬件加速没有做"探测：这台机器有没有可用 Vulkan/CUDA"——现在用户点了就点了，失败会在模型调用时以错误文本呈现。

---

## 2026-11-25 · LM-S 摘要改为后台队列（记录里的最后一件）

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 接缝 | `IRawStore` 新增 `BackgroundSummaries`（开关）与 `SummaryUpdated`（事件）。 |
| 快路径 | `BackgroundSummaries = true` 时，`Put` 只写**降级摘要**并入库，然后把它排进队列；调用方不等模型。 |
| 后台 | 单线程 worker：取队列 → `Task.Run(summarizer)` → 截断到 200 字 → `UPDATE ... SET summary, summary_kind='model'` → 抛 `SummaryUpdated`。队列空则退出，下次入队再起（不常驻线程）。 |
| 容错 | 摘要器抛异常 / 返回空 → 保留降级摘要，行不丢；重复条目按内容哈希去重，不会重复排队。 |
| 接线 | `SwitchToBuiltIn` 打开开关并接摘要器；`SwitchToOllama` 关闭并清空。 |

### 2. 实测（内置模型 + Vulkan）

```
ingest     : 10 ms · fallback · 校历：2026-2027 学年第二学期 ……（降级摘要）
background : model  · 校历：2026-2027 学年第二学期 2 月 23 日开学，4 月 6 日到 4 月 10 日为考试周。
```

入库不再被模型拖慢（原来同步时 ingress 要等 0.6 s；GPU 上也要等一次调用）。

### 3. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Contracts/Contracts.cs` | 两个新成员 |
| `src/CogniCustos.Core/RawStore.cs` | 队列、worker、`UpdateSummary` |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | 两个切换点设置开关 |
| `samples/CogniCustos.Sample/ModelCheck.cs` | 分别打印 ingest 与后台摘要 |
| `src/CogniCustos.Selftest/Program.cs` | 新增 2 条（73 → 75） |

### 4. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug     # passed=75
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --model-check --gpu
# 期望：ingest 约 10 ms（fallback）；随后 background: model
```

断言覆盖：入库 < 500 ms 且摘要种类是 `fallback`；事件到达后库里变成 `model` 且内容来自模型；
摘要器抛异常时库里仍是可用的降级摘要。

### 5. 以后要改哪里

- **想同步回去**：`Raw.BackgroundSummaries = false`（单条时更直观，批量时别这么干）。
- **想批量并发**：`DrainSummaries` 目前串行（模型本身也不是并发安全的），要并发得先给每个 worker 自己的 provider 实例。
- **摘要长度**：截断用 `SummaryChars`（200），模型提示词在 `Runtime.SwitchToBuiltIn` 里（80 字）。

### 6. 已知未做

- 队列没有持久化：进程退出时未完成的摘要会丢（行里留的是降级摘要，可接受）。
- 没有"重新生成摘要"的入口。

---

## 2026-11-25 · API-1 主推理模型真配置 + 每日抓取

### 1. 主推理模型（模型名 / 地址 / Key 真的生效）

| 项 | 内容 |
|---|---|
| 保存 | 三个字段提交（Enter 或失焦）时，`ModelField.Committed` → `CloudSaveRequested` → `CloudModelService.UseCloud`：校验（模型名必填、地址必须是 http(s) 绝对地址）→ 落盘 `CloudModel/CloudBaseUrl/CloudApiKey` → **注册 provider**。 |
| 生效顺序 | 云端 provider 用 `ModelRouter.AddFirst` 排在最前：配了就用云端，没配才轮到内置/Ollama，都没有则 standby。 |
| 切到本地 | 切内置模型或 Ollama 时只 `Unregister`（把云端 provider 摘掉），**不删配置**——字段里的值还在，切回来即用。 |
| 状态行 | 三个字段下面显示"已保存并生效 <模型>"或"未配置：由本地模型回答"。 |
| 不泄漏 | `KeyHint` 只说"已填写"，任何地方都不回显 Key。 |

### 2. 每日抓取（关键词 → 原始区 → 提议 → 确认后进主库）

| 项 | 内容 |
|---|---|
| 来源 | `WebInfoSource`（配置的网址，HTML 去标签取正文与 `<title>`）+ `InboxInfoSource`（一个文件夹，一文件一封"邮件"）。两者都实现 `IInfoSource`，IMAP 以后照这个接口加即可。 |
| 关键词 | `InfoKeywords`：考试/作业/截止/deadline/缴费/注册/选课/成绩/放假/会议/讲座/调课/重要/通知/变更，可替换。命中才算"重要变化"。 |
| 日期 | 支持 `2027-04-06`、`4月6日`、`2027/4/6`；**`4月6日` 写在 11 月会被推到明年**（不然就成了过去的日期）。 |
| 流程 | 每条都 `raw.Put`（原始区，R1 概括）→ 命中关键词的生成变更集：`tasks.add`（标题/截止）＋ 有日期时 `schedule.add`（那天 9:00–10:00），走审批门成为**待确认提议**。 |
| 每日一次 | `LastFetchDate` 落盘；`InfoPoller.IsDue` / `MarkRan`；启动时 `rt.RunDailyFetchAsync()` 只在没跑过的当天跑。 |
| 确认之后 | `Gate.Approve` 落库 → 任务出现在**当日提醒顶栏**（`Today()` 读 `Tasks` 里未完成、按截止排序），日程块出现在**日历**（`CalendarCard` 读 `Schedule`）。 |
| 手动跑 | `CogniCustos.Sample.exe --fetch-now`：打印来源数、抓到几条、生成了哪些待确认提议。 |

### 3. 实测

```
result : 抓到 1 条，其中 1 条像重要变化，已生成 1 个待确认提议
  · 教务通知
  → #p1795581300913-0a47 抓取：教务通知（待确认）
```

断言里确认后：`plan: task=教务通知 due=2027-04-06 blocks=1`——任务进了待办、日程进了 4 月 6 日那一天。

### 4. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/CloudModelService.cs` | **新增**（含 `SetFactory`、`Unregister`、`ClearCloud`） |
| `src/CogniCustos.Core/InfoPoller.cs` | **新增**（来源、关键词、日期、poller） |
| `src/CogniCustos.Core/ModelRouter.cs` | `AddFirst` |
| `src/CogniCustos.Core/SettingsAndReminders.cs` | `WebSources` / `InboxPath` / `LastFetchDate` |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | `CloudModels`、`Poller`、`RunDailyFetchAsync`；切本地时摘掉云端 |
| `src/CogniCustos.Ui/ViewModels/ModelField.cs` | `Committed` 事件、`SetValue` |
| `src/CogniCustos.Ui/ViewModels/ModelConfigurationViewModel.cs` | `CloudStatus`、`SetCloudValues`、`SetCloudStatus` |
| `src/CogniCustos.Ui/ViewModels/{IShellDataSource,DemoShellDataSource,ShellViewModel}.cs` | 保存接缝与状态行 |
| `src/CogniCustos.Ui/Views/SettingsPage.axaml` + Lang.* | 状态行 |
| `samples/CogniCustos.Sample/{SampleWindow,FetchNow,Program}.cs` | 启动时抓取、`--fetch-now` |
| `src/CogniCustos.Desktop/MainWindow.cs` | 注入真实 HTTP provider 工厂 |
| `src/CogniCustos.Selftest/Program.cs` | 新增 3 条（75 → 78） |
| `tools/CogniCustos.Ui.Tests/Program.cs` | 新增 1 条（62 → 63） |

### 5. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug     # passed=78
dotnet run --project tools/CogniCustos.Ui.Tests -c Debug   # ALL PASS（63）
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --fetch-now
```

### 6. 以后要改哪里

- **抓取来源**：改 `state/settings.json` 里的 `WebSources`（网址数组）与 `InboxPath`（文件夹）；界面上的"邮件抓取配置/网页浏览配置"两节仍是占位。
- **关键词**：`InfoKeywords.Defaults`，或构造时传入自己的词表。
- **换成 IMAP**：实现 `IInfoSource`（`Kind = "email"`），在 `InfoSources.From` 里加进去即可，其余流程不动。
- **抓取频率**：`IsDue` 现在按天；要改成每小时就把 `LastFetchDate` 换成时间戳。
- **提议落点**：`InfoPoller.RunAsync` 里构造 `Change` 的两行——现在是"任务 + 日程"，要加"写入知识库"就在那里加一个 `kb.index`。
- **API 配置**：`CloudModelService.UseCloud`；调试时可以把 `CloudBaseUrl` 指向本地 llama.cpp server。

### 7. 已知未做

- 抓取没有"订阅项"的界面（只有 settings.json），也没有增量判断（同一个页面每次都会重新入原始区；靠内容哈希去重）。
- 邮件只支持"一文件一封"的文件夹，没有 IMAP 登录与未读标记。
- 提议的"时间"是固定 9:00–10:00，没有从正文里抽时间段。

---

## 2026-11-25 · CFG 邮件抓取配置 / 网页浏览配置（照两个插件的写法）

照本机两个插件（cookie 白名单、邮件整理）的配置界面来组织：**列表行 + 状态 + 单项操作 + 一个添加表单 + 一行说明**，
不新造交互。

### 1. 邮件抓取配置

| 项 | 内容 |
|---|---|
| 账号行 | 地址 / `host:port` / 抓取范围（最近 N 天 · 只抓未读）/ 状态（未验证 → 测试后变成服务器回应） |
| 单项操作 | `测试`（真的连 IMAP 端口，读服务器问候语：`* OK` 才算通，超时 8 秒）、`删除` |
| 添加表单 | 地址、IMAP 服务器/端口（默认 993）、应用密码（密码框，保存后不回显）、回溯天数、"只抓未读" |
| 导入 | `从 Thunderbird 导入`：读 `%APPDATA%\Thunderbird\Profiles\*\prefs.js` 里的 hostname 与邮箱地址，配成账号；**密码读不出来**（Thunderbird 加密存储），提示逐个补 |
| 凭据 | 应用密码写 `state/secrets.json`（本机文件；**诚实地说明现在还没有凭据库**），账号元数据进 `settings.json` |

### 2. 网页浏览配置

| 项 | 内容 |
|---|---|
| 网址行 | 域名 / 完整 URL / 访问方式（`只读 GET · 表单提交被禁止`）/ `删除` |
| 添加 | 校验必须是 http(s) 绝对地址，重复添加不产生重复项 |
| 说明 | 明确写："需要登录的站点，Cookie 由浏览器插件提供，并按域名逐个向你确认"——和 cookie 插件同一套语义 |

### 3. 层次约定（踩坑记录）

`CogniCustos.Ui` 不引用 `CogniCustos.Core`。第一次实现直接把 `Core.EmailAccount` 放进接缝，编译立刻失败；
改成 UI 侧的 `EmailAccountInfo` 记录，由示例负责在边界上映射。这条规则以后照办。

### 4. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/EmailAccounts.cs` | **新增**：账号、预设服务商、`secrets.json`、Thunderbird 导入、真实端口测试 |
| `src/CogniCustos.Core/InfoPoller.cs` | `WebSourcesService`（校验/去重） |
| `src/CogniCustos.Core/SettingsAndReminders.cs` | `EmailAccounts` 列表 |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | 暴露 `EmailAccounts` / `WebSources` |
| `src/CogniCustos.Ui/ViewModels/RetrievalViewModels.cs` | **新增**：两节的行与表单 |
| `src/CogniCustos.Ui/ViewModels/{IShellDataSource,DemoShellDataSource,ShellViewModel,SettingsViewModel}.cs` | 接缝、事件转发、小节可见性 |
| `src/CogniCustos.Ui/Views/SettingsPage.axaml` + `Lang.*` | 两节内容与文案 |
| `samples/CogniCustos.Sample/RuntimeShellDataSource.cs` | 映射到 Core |
| `src/CogniCustos.Selftest/Program.cs` | 新增 4 条（78 → 82） |
| `tools/CogniCustos.Ui.Tests/Program.cs` | 新增 2 条（63 → 65） |
| `tools/CogniCustos.Ui.Shots/Program.cs`、`ShellScenarios.cs` | 两个新状态 `settings-email` / `settings-web` |

### 5. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug       # passed=82
dotnet run --project tools/CogniCustos.Ui.Tests -c Debug     # ALL PASS（65）
dotnet run --project tools/CogniCustos.Ui.Shots -- artifacts/ui 1280 720   # settings-email / settings-web
```

### 6. 以后要改哪里

- **服务商预设**：`EmailAccountsService.Presets`（Outlook / Gmail / QQ / 163 / 自填）。
- **测试的严格程度**：`TestAsync` 现在只读问候语；接 IMAP 后可以改成登录验证（那时用它读 `SecretFor`）。
- **凭据换成真正的库**：`SaveSecret` / `SecretFor` 一处替换即可，界面不感知。
- **网页抓取的规则**（关键词、只读、Cookie 来源）：`InfoKeywords` 与 `WebInfoSource`；这一节只是列表。

### 7. 已知未做

- **IMAP 读取还没接**：现在能配账号、能测端口、能导入，但每日抓取只读收件夹（`InboxPath`）里的文件。
- 邮件正文解析（MIME/HTML）与已读标记没有做。
- 网页这一节没有"需要登录"的域名授权流程——那是 cookie 插件的职责，我们只读。

---

## 2026-11-25 · TB 改走雷鸟（不配协议），并在真实邮箱上验证

### 1. 为什么改

你说得对：很多邮箱根本不接受第三方单独配 IMAP（Gmail 要应用密码、Outlook 要走 OAuth、163 要授权码），
协议这条路"太卡"。而**雷鸟已经把这些都解决了**：用户登录一次，邮件就在磁盘上，格式稳定。
所以默认路径改成"读雷鸟的本地邮件库"，手动配服务器降级为进阶选项。

### 2. 实现

| 项 | 内容 |
|---|---|
| 发现 | `%APPDATA%\Thunderbird\Profiles\*` → `ImapMail/<account>/<folder>`、`Mail/<account>/<folder>`，跳过 `.msf` 索引与空文件 |
| 读取 | 只读每个 mbox **末尾 4 MB**（Outlook 那个 INBOX 有 943 MB，不能整读），按 `From ` 行切分，取最新 N 条 |
| 解码 | CRLF 归一；RFC 2047 主题（`=?utf-8?B?…?=`、`Q` 编码）；嵌套 multipart 递归找 `text/plain`（否则 `text/html`）；`base64` / `quoted-printable` 正文解码；去标签 |
| 日期 | 邮件日期是 `Mon, 01 Jan 2026 10:00:00 +0800`。**很多发件人星期写错**，.NET 会直接拒解析——现在先剥掉星期再解析；缺 `Date` 时退到 `Received` |
| 接入 | `ThunderbirdMailSource : IInfoSource`，`InfoSources.From` 里排在最前；`UseThunderbird` 默认开 |
| 界面 | 邮件抓取配置顶部一张卡片："从 Thunderbird 读取 / N 个文件夹 · 最大 X MB / 重新扫描 / 使用 Thunderbird 开关"，下面是灰字说明；手动账号列表收进"高级（不推荐）"折叠 |

### 3. 真实数据实测（本机）

```
thunderbird : 7 个邮箱文件夹（最大 943.5 MB）
   · outlook.office365-1.com/INBOX  943.5 MB
   · outlook.office365.com/INBOX    282.3 MB
   · imap.gmail-1.com/INBOX         107.6 MB
   · imap.gmail-2.com/INBOX          76.6 MB
   · imap.163-1.com/INBOX             0.4 MB

--mail-peek（只读）解析示例：
· [2026-11-24] HEBAHAN KEMPEN 16 HARI AKTIVISME …  ← 马来语主题、正文正确
· [2026-11-24] WORKSHOP: SPSS BASIC STATISTICS
· [2026-11-25] What's new in ClickUp: Sheets, schedules …

--fetch-now：抓到 55 封 → 关键词命中 7 封 → 生成 7 个待确认提议
```

**第一版关键词太宽**：7 条命中里全是营销邮件（Position Tracking、Google 账号提示、账单通知）。
加了"噪声词表"（unsubscribe/退订/促销/限时/登录活动…）后：**55 封里只剩 1 条像重要变化**。

### 4. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/ThunderbirdMail.cs` | **新增**：发现、tail 读取、主题/正文解码、`ThunderbirdMailSource` |
| `src/CogniCustos.Core/InfoPoller.cs` | 默认源加入 Thunderbird；关键词噪声表 |
| `src/CogniCustos.Core/SettingsAndReminders.cs` | `UseThunderbird`（默认 true） |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | `MailFolders`、`RescanMail`、`SettingsPath` |
| `src/CogniCustos.Ui/ViewModels/RetrievalViewModels.cs` | Thunderbird 卡片状态、扫描/开关命令、高级折叠 |
| `src/CogniCustos.Ui/ViewModels/{IShellDataSource,ShellViewModel,DemoShellDataSource}.cs`、`Views/SettingsPage.axaml`、`Lang.*` | 接缝、文案、布局 |
| `samples/CogniCustos.Sample/MailPeek.cs` | **新增** `--mail-peek`（只读查看解析结果） |
| `src/CogniCustos.Selftest/Program.cs` | 新增 2 条（82 → 84） |
| `tools/CogniCustos.Ui.Tests/Program.cs` | 新增 1 条（65 → 66） |

### 5. 怎么验证

```bash
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --mail-peek   # 只看，不写
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --fetch-now   # 真跑一遍
dotnet run --project src/CogniCustos.Selftest -c Debug                          # passed=84
```

### 6. 以后要改哪里

- **读哪些文件夹**：`ThunderbirdMailSource` 的 `Folders()`；目前读所有非空 mbox（含 Trash），要排除的话在那里过滤。
- **读多少**：`TailBytes`（4 MB）与 `perFolder`（20 条）。
- **关键词与噪声**：`InfoKeywords.Defaults` / `InfoKeywords.Ignored`。
- **雷鸟路径**：`ThunderbirdMail.ProfilesRoot`；自定义安装位置可传 `DiscoverProfiles(root)`。
- **换回 IMAP**：实现 `IInfoSource` 并放进 `InfoSources.From`；两者可以并存。

### 7. 已知未做

- 只读 INBOX 这类 mbox，不读 `.msf` 索引（所以"已读/未读"状态拿不到）。
- 附件不解析；HTML 邮件只取文本。
- 没有"已抓过就跳过"的增量标记：同一封邮件再次运行仍会入原始区（靠内容哈希去重，不会重复提议）。
- 雷鸟开着时它可能正在写 mbox；已用 `FileShare.ReadWrite` 打开，但极端情况下最后一条可能不完整（会丢该条，不影响其它）。

---

## 2026-11-25 · TB-SEL 选择"读取哪些邮箱"

### 1. 改了什么

| 项 | 内容 |
|---|---|
| 选择 | 设置页 Thunderbird 卡片里新增 `要读取的邮箱` 列表：每个发现的文件夹一行（`账号 / 文件夹` + 大小 + 复选框），默认全开 |
| 持久化 | 关掉的文件夹记进 `settings.json` 的 `MutedMailFolders`（`account/folder`）；保存时立刻重建抓取来源，下一次抓取只读勾上的 |
| 发现 | 点 `重新扫描` 或改开关都会重扫 profile 并把列表刷新到界面 |
| 语义 | 未配置（列表为空）= 全读，所以新装机器不需要先配；关掉某个 = 明确不读它 |

### 2. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/SettingsAndReminders.cs` | `MutedMailFolders` |
| `src/CogniCustos.Core/ThunderbirdMail.cs` | `Selected(settingsPath)` / `UseSelection(...)` / `_enabled` |
| `src/CogniCustos.Core/InfoPoller.cs` | `UseSources(...)`（选择变化后重建来源） |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | `MailFolderViews()`、`SetMailFolderEnabled()`、`BuildSources()`、`MailSource` |
| `src/CogniCustos.Ui/ViewModels/RetrievalViewModels.cs` | `MailFolderRow`（开关）、`SetMailFolders`、`MailFolderToggleRequested` |
| `src/CogniCustos.Ui/ViewModels/{IShellDataSource,ShellViewModel,DemoShellDataSource}.cs`、`Views/SettingsPage.axaml`、`Lang.*` | 接缝、事件、列表 UI 与文案 |
| `samples/CogniCustos.Sample/RuntimeShellDataSource.cs` | 接到 runtime |
| `src/CogniCustos.Selftest/Program.cs` | 新增 1 条（84 → 85） |
| `tools/CogniCustos.Ui.Tests/Program.cs` | 新增 1 条（66 → 67） |

### 3. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest -c Debug        # passed=85
dotnet run --project tools/CogniCustos.Ui.Tests -c Debug      # ALL PASS（67）
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --fetch-now
# 关掉某个邮箱后再跑，抓取数量应减少
```

### 4. 以后要改哪里

- **默认全关**：把 `SetMailFolderEnabled` 的初始写入改成"全部 muted"即可。
- **按文件夹细分（不只 INBOX）**：`ThunderbirdMail.DiscoverFolders` 已经在遍历所有 mbox，所以列表天然包含 Sent/Trash；要过滤就在那里挑。
- **同时支持"选中的账号 + 网页 + 收件夹"**：`BuildSources` 是唯一装配点。

### 5. 已知未做

- 列表只能勾选已发现的文件夹；如果雷鸟后来新加账号，需要点一次 `重新扫描`。
- 没有"只读最近 N 天"的按文件夹设置（全局 `perFolder = 20`）。

---

## 2026-11-25 · TB-ADDR 选到"具体邮箱"而不是服务器目录

### 1. 问题

之前列表显示的是雷鸟的**目录名**（`imap.gmail-1.com`、`outlook.office365-1.com`），用户看到的是服务器，
不是"我的哪个邮箱"。同一个服务器上挂多个邮箱时，光看目录根本分不清。

### 2. 做法：从 `prefs.js` 把目录还原成邮箱

Thunderbird 的 prefs 里有三张表可以串起来：

```
mail.account.accountN.server      → serverK
mail.account.accountN.identities  → idM
mail.identity.idM.useremail       → 真实邮箱地址
mail.server.serverK.hostname      → 服务器主机名
```

目录名的规则是：**同一主机名的第 n 个账号**得到 `host`、`host-1`、`host-2`…（后缀插在最后一个点之前，
例如 `imap.gmail.com` → `imap.gmail-1.com`）。实现按主机名分组计数还原，不是按 server 编号。

补充：账号没有身份时退回 `mail.server.*.userName`（它常常就是邮箱地址），仍拿不到就保留目录名——不编造。

### 3. 真实数据实测（本机）

```
0209560@student.uow.edu.my / INBOX     (outlook.office365-1.com)  943.5 MB
wutianhao@student.usm.my   / INBOX     (outlook.office365.com)    282.3 MB
wutianhao373737@gmail.com  / INBOX     (imap.gmail-1.com)         107.6 MB
imap.gmail-2.com           / INBOX     (imap.gmail-2.com)          76.6 MB   ← 见下
18916954605@163.com        / INBOX     (imap.163-1.com)            0.4 MB
wutianhao@student.usm.my   / Trash     (outlook.office365.com)       0 MB
```

可见的 5 个真实邮箱里 4 个识别成功。`imap.gmail-2.com` 对应的雷鸟账号在 prefs 里已经没有身份、也没有
`userName`，所以显示目录名——**没有凭据就不猜**。界面上邮箱是主行，服务器目录作为灰色小字留作参考。

### 4. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/ThunderbirdMail.cs` | `ParseAccounts(prefs.js)`、按主机名编号的 `DirectoryName`、`ThunderbirdFolder.Label/Address/Hostname` |
| `src/CogniCustos.Core/CogniCustosRuntime.cs` | `MailFolderViews()` 多带 `Mailbox` / `Server` |
| `src/CogniCustos.Ui/ViewModels/IShellDataSource.cs` | `MailFolderInfo` 记录 |
| `src/CogniCustos.Ui/ViewModels/RetrievalViewModels.cs` | 行显示邮箱、灰色服务器行、`DirectoryNote` |
| `src/CogniCustos.Ui/Views/SettingsPage.axaml` | 两行布局 |
| `samples/CogniCustos.Sample/MailPeek.cs` | 打印 `Label / Folder (dir)` |
| `src/CogniCustos.Selftest/Program.cs` | 新增 1 条（85 → 86） |

### 5. 怎么验证

```bash
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --mail-peek
dotnet run --project src/CogniCustos.Selftest -c Debug     # passed=86
```

断言用合成 profile：两个账号同一主机 → 目录 `host` / `host-1`，标签分别是两个邮箱地址。

### 6. 以后要改哪里

- **还认不出的账号**：`ParseAccounts` 里加别的线索（例如 `mail.identity.*.smtpServer` 或 `mail.server.*.realuserName`）。
- **显示层次**：`MailFolderRow.Display` / `DirectoryNote`。
- **Local Folders**：它不是账号（`Mail/Local Folders`），目前直接显示目录名。

### 7. 已知未做

- 一个 Gmail 目录（`imap.gmail-2.com`，76 MB）在 prefs 里查不到归属，仍显示目录名。
- 勾选/取消仍以 `目录/文件夹` 作为键（稳定），改显示名不会破坏已保存的选择。

---

## 2026-11-25 · MAIL-2 增量 + 只读某个邮箱 + 打标签/精细概括/进日历

### 1. 增量：同一封邮件只入一次

| 项 | 内容 |
|---|---|
| 台账 | 数据库 v6 新增 `ingested_messages(key, source, ingested_at)`，主键是消息标识 |
| 标识 | 邮件用 `Message-ID`；没有就退回 `目录/文件夹 + 日期 + 主题`。网页用 `网址 + 长度 + 哈希`，收件夹用 `路径 + 修改时间` |
| 行为 | 抓取时先 `WasIngested`，已读过的直接跳过（计入 `Skipped`），不再入原始区、不再生成提议 |
| 提示 | 结果行会说明："没有新内容：55 条都读过了" / "抓到 3 条（跳过已读 52 条）" |

实测同一份邮箱连跑两次：第一次 `抓到 55 条 … 1 个提议`，第二次 `没有新内容：55 条都读过了`。

### 2. 只读某个邮箱

界面把文件夹按**邮箱**分组（`wutianhao@student.usm.my` 这类地址），每个邮箱一行：总开关 + `只读这个` 按钮 + 服务器名；
下面嵌套它自己的文件夹，可单独勾。`只读这个` 会把其它邮箱的所有文件夹写进 `MutedMailFolders`，下次抓取只读它。

### 3. 打标签 → 精细概括 → 待确认 → 日历

| 步 | 内容 |
|---|---|
| 标签 | 数据库 v7 给 `raw_artifacts` 加 `label`；抓取时先分类：`important` / `routine` / `promo`（促销词表命中即 promo） |
| 精细概括 | 只有 `important` 继续：抽日期（`2027-04-08`）与时间（`09:30`、`9:00-10:30`、`上午 9 点`），生成一行摘要 |
| 待确认 | 提议标题就是这行摘要，例如 `2027-04-08 · 09:30-10:30 · 考试：考试通知`；描述里写"重要邮件 · 考试 / 来源 / 正文前 240 字" |
| 日历 | 确认后落库：任务进待办，日程块用抽到的时间（没有时间才用 09:00–10:00） |

### 4. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/SqliteSchema.cs` | v6 台账表、v7 `label` 列 |
| `src/CogniCustos.Contracts/Contracts.cs` | `WasIngested` / `MarkIngested` / `SetLabel` / `GetLabel` |
| `src/CogniCustos.Core/RawStore.cs` | 四个方法 |
| `src/CogniCustos.Core/InfoPoller.cs` | 去重、`Label()`、`FindTime()`、`Brief()`、跳过的计数与文案 |
| `src/CogniCustos.Core/ThunderbirdMail.cs` | `Message-ID` 进 `FetchedItem.Key` |
| `src/CogniCustos.Ui/ViewModels/RetrievalViewModels.cs` | `MailboxRow`（邮箱级开关 + 只读这个）、分组 |
| `src/CogniCustos.Ui/ViewModels/{IShellDataSource,ShellViewModel,DemoShellDataSource}.cs`、`Views/SettingsPage.axaml`、`Lang.*` | 分组界面与事件 |
| `src/CogniCustos.Core/CogniCustosRuntime.cs`、`samples/.../RuntimeShellDataSource.cs` | `SetOnlyMailbox` |
| `src/CogniCustos.Selftest/Program.cs` | 新增 2 条（86 → 88） |
| `tools/CogniCustos.Ui.Tests/Program.cs` | 新增 1 条（67 → 68） |

### 5. 怎么验证

```bash
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --fetch-now   # 连跑两次看"没有新内容"
dotnet run --project src/CogniCustos.Selftest -c Debug                           # passed=88
dotnet run --project tools/CogniCustos.Ui.Tests -c Debug                         # ALL PASS（68）
```

断言覆盖：标签正确（考试=important、限时优惠=promo）、标题形如 `2027-04-08 · 09:30-10:30 · 考试：考试通知`、
`只读这个` 只留一个邮箱、勾选状态直达后端。

### 6. 以后要改哪里

- **换标识**：`ThunderbirdMailSource` 里 `FetchedItem.Key`。
- **标签规则**：`InfoKeywords.Label` 与 `Ignored` / `Defaults` 词表。
- **时间抽取**：`InfoKeywords.FindTime`（支持 `9:00`、`9:00-10:30`、`上午 9 点`；暂不处理"下周三"）。
- **"没有新内容"的判定**：`PollResult.Summary`。

### 7. 已知未做

- 标签还没有界面（只能从 `raw.list` / 数据库看）；"重要邮件"的模型精修仍待接（现在是确定性抽取）。
- `Message-ID` 缺失时的退化键包含主题，主题改过的同一封邮件会被当成新的。
- 时间只解析到分钟，不处理时区/跨天；"下周三 9 点"这类相对日期还不支持。

---

## 2026-11-25 · LOOP 标签上卡片 + 抽屉"读邮件"按钮（最小闭环打通）

### 1. 标签显示

| 项 | 内容 |
|---|---|
| 数据 | 数据库 v8 给 `proposals` 加 `tag`；`ProposeSet(..., tag)` 存，`Proposal.Tag` 读出 |
| 写入 | 抓取生成提议时打 `重要邮件` |
| 界面 | 待确认卡片每行在标题上方显示一个小胶囊（`重要邮件` / `Important mail`），`CardItem.Tag` + `HasTag` |
| 实测 | 渲染 `home` 状态可见：卡片里 `Important mail` 胶囊贴在标题上 |

### 2. 抽屉里的"读邮件"按钮

| 项 | 内容 |
|---|---|
| 位置 | 打开抽屉后第三排左边的 mail tile（`Tile.CheckMail`），原来没有命令 |
| 行为 | 点击 → 提示"正在读取邮箱…" → 后台读**被允许的邮箱**（`IShellDataSource.FetchNow()` → `rt.Poller.RunAsync`）→ 重建卡片/待办/日历 → 用结果行提示（"抓到 N 条…生成 M 个待确认提议" / "没有新内容：55 条都读过了"） |
| 刷新 | `ShellViewModel.ReloadBoards()`：卡片 `Refresh()` + `Today.ResetSeeds(_source.Today())` |
| 意义 | 不用等"每天自动跑"，用户点一下就能拿到最新待确认 |

### 3. 用户的最小闭环（现在可以真的走一遍）

```
打开软件 → 设置里配置模型（模型名/地址/Key 保存即生效）
        → 设置里选邮箱（只读 wutianhao@student.usm.my）
        → 抽屉点"读邮件" → 后台读雷鸟本地邮件 → 重要邮件打标签 → 生成待确认（带日期/时间摘要）
        → 点待确认里的行 → 确认 → 进入当日提醒 + 日历（时间块用邮件里写的时间）
```

### 4. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/SqliteSchema.cs` | v8 `proposals.tag` |
| `src/CogniCustos.Contracts/Contracts.cs` | `Proposal.Tag`、`ProposeSet(..., tag)` |
| `src/CogniCustos.Core/ApprovalGate.cs` | 存/读 tag |
| `src/CogniCustos.Core/InfoPoller.cs` | 提议带 `重要邮件` 标签 |
| `src/CogniCustos.Ui/ViewModels/CardItem.cs`、`Views/ShellView.axaml` | 胶囊样式行 |
| `src/CogniCustos.Ui/ViewModels/ShellViewModel.cs` | `CheckMailCommand`、`ReloadBoards()` |
| `src/CogniCustos.Ui/ViewModels/{IShellDataSource,DemoShellDataSource}.cs`、`samples/.../RuntimeShellDataSource.cs` | `FetchNow()` |
| `samples/CogniCustos.Sample/FetchNow.cs` | 打印标签 |
| `tools/CogniCustos.Ui.Tests/Program.cs` | 新增 1 条（68 → 69） |

### 5. 怎么验证

```bash
dotnet run --project tools/CogniCustos.Ui.Tests -c Debug     # ALL PASS（69）
dotnet run --project src/CogniCustos.Selftest -c Debug       # passed=88
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --fetch-now
dotnet run --project tools/CogniCustos.Ui.Shots -- artifacts/ui 1280 720   # home 里能看到胶囊
```

### 6. 以后要改哪里

- **按钮读哪些邮箱**：`RuntimeShellDataSource.FetchNow()` 用的是 `rt.Poller`（受设置页勾选控制）。
- **标签文案**：`Lang.*` 的 `Card.Tag.Mail`，以及 `InfoPoller` 里的 `"重要邮件"`。
- **想加"读取中"的转圈**：`Tile.CheckingMail` 文案已存在，接到 `IsBusy` 状态即可。

### 7. 已知未做

- 读邮件时界面没有进度条（只有一句提示），长邮箱会看起来"卡一下"；
- 待确认卡片一次只显示提议的变更行，摘要（日期/时间那行）只在标题里；
- 抽屉 mail tile 与"每日自动抓取"共用同一个 poller，所以点一次会让当天的自动抓取也跳过（`LastFetchDate` 同日）。

---

## 2026-11-25 · READ 点读（打开文件 → 通读概要 → 方框选中 → 学长学姐讲评）

分支 `feat/point-read`。这是第一版"能用的点读"，界面与交互按最小可用做，细节留给下一轮。

### 1. 用户看到的东西

| 位置 | 行为 |
|---|---|
| 右侧抽屉最下面新增一行 | 瓷砖「点读文件」→ 打开文件对话框（pdf / doc / docx / ppt / pptx） |
| 点读整页（ZIndex 21，盖住画布） | 左：概要 + 页码目录（目录行可点，跳到该页）；中：页面 + 页码翻页；右：讲评记录 |
| 页面框选 | 在页面上按住左键拖出方框，松手即取"框里那些字"；再点一次空白处取消 |
| 询问 | 「询问 AI 教导」按钮 + 旁边的输入框（可空着直接点，也可写得更具体）；回答进右侧讲评列 |
| 打开文件 | 头部的「换一个文件」 |
| 关闭 | 头部 ✕ 或 Esc（优先关日历/对话框，然后才是点读） |

### 2. 三个技术决定（都有实测依据）

1. **渲染用 Docnet（PDFium），不用 PDFtoImage。** PDFtoImage 5.4 会拖进 SkiaSharp 4.150，
   Avalonia 12 钉的是 3.119；同一进程里两个大版本会互相拆台。Docnet 只回 BGRA 原始像素，
   由 UI 直接做成 `WriteableBitmap`，全程不碰 SkiaSharp 的版本。
2. **文字与坐标用 PdfPig。** 纯托管，给出词级 bbox（PDF 坐标左下原点），换算成 0..1 的
   "左上原点"后，方框命中就是一次矩形比较。Docnet 自己的 `GetText` 只用于整页文字。
3. **Office 转换走子进程（PowerShell 7），不在应用里做 COM。** 见第 3 节。

### 3. 实测：这台机器上的 Office 自动化（重要，别重复踩）

| 做法 | 结果 |
|---|---|
| .NET 8 进程里 `CreateInstance(Word.Application)`（STA 线程、MTA 线程都试过） | **永远不返回**；一个 45 行的裸 .NET 8 控制台程序也能复现 |
| 同上，但用**命名参数** `Open(path, ReadOnly: true, …)` | 同样挂住（改位置参数不能修 COM 创建这一段） |
| Windows PowerShell 5.1 调同样的脚本 | 直接报参数绑定错误（`PSReference`），不可用 |
| **PowerShell 7**（`C:\Program Files\PowerShell\7\pwsh.exe`）跑同一脚本 | **1.4 MB PDF，6–8 秒完成** |
| 从本应用（WinExe）派生子进程执行同一脚本 | **仍然挂住**，Word 停在启动画面 |

结论与取舍：
- PowerPoint → PDF（`SaveAs 32`）、Word → PDF（`SaveAs2 17`）的代码都对，脚本单独跑是通的；
- 但只要由这个 WinExe 派生，Word 就不干活 —— 这一层是环境问题，不是本功能的逻辑问题；
- 所以实现为"尽力而为 + 快速失败"：第一次尝试最多等 **25 秒**，失败后把结论写进
  `reader/office-bridge.txt`（形如 `FAIL 2026-11-25 …`），**同一天再点就直接返回**（0.3 秒）；
- 提示语明确给出可行路径：用 Office「另存为 → PDF」，再用点读打开那个 PDF。

`COGNI_DOC_DEBUG=1` 时，`--reader-check` 会打印转换桥每一步（`convert: word: opened` 等），
下次排查从这里开始。

### 4. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Documents/`（新工程） | `DocumentBook`（打开/渲染/文字/词框/裁切）、`DocumentOpener`（PDF 直开、Office 转 PDF 后开）、`OfficePdfConverter`（PowerShell 桥 + 当日缓存 + 25 秒上限）、`PngEncoder`（BGRA→PNG，70 行，无依赖） |
| `Directory.Packages.props` | `Docnet.Core 2.6.0`、`PdfPig 0.1.16` |
| `src/CogniCustos.Contracts/Vision.cs` | `IVisionModel`（图片进、文字出） |
| `src/CogniCustos.Core/DocLecture.cs` | 学长学姐人设、通读概要 + 目录（JSON 宽松解析，失败退化为逐页首行）、框选提问、无模型时的诚实回答 |
| `src/CogniCustos.Adapters/OllamaVisionModel.cs` | Ollama `/api/generate` + `images`，只在选区没有文字层时才用 |
| `src/CogniCustos.Ui/ViewModels/ReaderViewModel.cs`、`IReaderService.cs`、`DemoReaderService.cs` | 阅读器状态机；接缝；演示文档（自己画 8 页，不需要 PDF 引擎） |
| `src/CogniCustos.Ui/Views/ReaderPage.axaml(.cs)`、`DocumentPicker.cs` | 整页界面、橡皮筋选区（含 letterbox 换算）、文件选择 |
| `src/CogniCustos.Ui/Views/ShellView.axaml(.cs)`、`ShellViewModel`、`IShellDataSource`、`Lang.*` | 抽屉第 4 行、`Reader` 属性、`CreateReader()`、两条语言各 36 个键 |
| `CogniCustos.Selftest/Program.cs` | 新增 11 条（88 → 99）：PDF 页数/像素/文字层/框选/裁切/PNG/拒收/OOXML 判定/目录解析/无模型 |
| `tools/CogniCustos.Ui.Tests/Program.cs` | 新增 6 条（69 → 75）+ `StubReader` |
| `tools/CogniCustos.Ui.Shots/Program.cs` | 新增两个状态 `reader`、`reader-ask` |
| `samples/CogniCustos.Sample/` | `RuntimeReaderService`（真文档 + 真模型）、`ReaderCheck`（`--reader-check <file>` 端到端自检） |

### 5. 怎么验证

```bash
dotnet build CogniCustos.slnx -c Debug                       # 0 errors
dotnet run --project src/CogniCustos.Selftest -c Debug       # passed=99
dotnet run --project tools/CogniCustos.Ui.Tests -c Debug     # ALL PASS（75）
dotnet run --project tools/CogniCustos.Ui.Shots -- artifacts/ui 1280 720   # reader / reader-ask 两张图

# 真文件 + 真模型（Ollama 在则用 qwen2.5:7b，vision 用 qwen3-vl:4b）
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --reader-check "…/0. Briefing CAT 304_Sem I_26.pdf"
```

实测（58 页的课程 PDF）：打开 114 ms、整页渲染 89 ms（1400×1050，5.7 MB）、
通读概要 + 8 条目录 1.7 s、框选提问 1.3 s；答案里会点明"在第 1 页、页面顶部"。

### 6. 以后要改哪里

- **换渲染引擎**：只改 `DocumentBook.OpenPdf/Render`；上层只看 `RenderedPage(Width,Height,Bgra)`。
- **框选命中规则**：`WordBox.CentreInside`（现在是"词中心落在框内"）。
- **人设与提示词**：`DocLecture.Persona` / `OutlineAsync` / `AskAsync`。
- **目录条数上限**：`DocLecture` 里的 40 条与 `OutlineCharsPerPage`。
- **界面布局**：`ReaderPage.axaml` 的三列（262 / * / 344），抽屉第 4 行在 `ShellView.axaml`。
- **Office 转换**：`OfficePdfConverter.Script`；换 `-File` 以外的调用方式时，先看 `office-bridge.txt` 的当日结论。

### 7. 已知未做

- **pptx/docx 自动转换在这台机器上不可用**（第 3 节的实测），只有 PDF 是可靠入口；
- 选区只取"词中心在框内"的词，遇到多栏排版或图文混排会漏；扫描件靠 vision，质量取决于 4B 模型；
- 没有缩放/平移、没有连续滚动、没有多页并排；
- 通读是把每页前 320 字喂给模型（总额 7k 字符），长文档的目录可能只覆盖前 40 页；
- 讲评记录只在本窗口内，关掉就没了（没有落库，也没有进知识库）；
- 没有"把这段讲评存成笔记"的入口。


### 8. 真机反馈修掉的第一处：框选坐标（2026-11-25 晚）

用户实测：**框稍微拖大一点，整块往左闪一下，靠右的区域框不住。**

原因不是渲染，是坐标空间混了。`ToNormalised` 把 `PageImage.Bounds` 当成"宿主尺寸"，
但指针坐标 `e.GetPosition(PageHost)` 是相对 `PageHost` 的。Avalonia 在 `Stretch=Uniform` 下
**把 Image 控件本身摆成绘制后的矩形**（实测：host 598×535，image 在 110,0 处、376.9×533），
于是丢掉的是图片在宿主里的那 111 px 偏移：

- 归一化被压缩 598 / 376.9 ≈ **1.59 倍**并整体左移；
- 松手后 `DrawCommitted()` 又用这份错误坐标重画框 → 看到"整块往左闪一下"；
- 右边缘早就越过 1、被 clamp 到 1 → 最右边永远框不住。

修法：不再自己算 letterbox，直接问控件要矩形——`TryPageRect()` = `(PageImage 原点, PageImage 尺寸)`，
`ToNormalised` / `ToViewPoint` 都用它。

回归：`reader_selection_uses_the_page_rect_not_the_host`——用真指针（MouseDown/Move/Up）从页面
25%/30% 拖到 95%/80%，断言四个归一化数值，并断言框的落点；再断言页面确实在宿主里居中留边
（否则这条测试抓不到东西）。把 `TryPageRect` 换回旧的宿主写法时，它会报
`left edge mapped to 0.343, expected 0.250`——正是用户看到的左移量。修复后 075 → 076 条断言。

顺带确认：截图 `docs/ui-reader.png` 里那个框，修复前是探出页面左边界的，现在落在页内。

---

## 2026-11-25 · PROMPT-P0 提示词与推理参数的唯一事实源 + 只读高级设置

第一步（P0）只做两件事：把所有写死的提示词**搬到一处**，再把这些值**显示出来**。
编辑、校验、按用途分档的温度留到 P1/P2。

### 1. 为什么要"搬"而不是"复制"

提示词原来散在 8 个地方（运行时、判定器、隐私检查、点读、内置模型适配器）。
如果设置页再维护一份副本，面板迟早会跟真正发出去的那份漂移——**一个会说谎的面板比没有面板更糟**。
所以 P0 让调用点直接读目录：`PromptCatalog.Render(key)`，面板渲染的也是这个目录。

| 键 | 位置 | 谁在发 |
|---|---|---|
| `chat.system` | `Core/Prompts.cs` | `CogniCustosRuntime.ChatAsync`（含 `{{tools}}`） |
| `summary.one-line` | 同上 | 原始区摘要（`RawStore.Summarizer`） |
| `judge.system` / `judge.format` | 同上 | `ModelJudge.Ask` |
| `guard.privacy-check` | 同上 | `SensitiveContentChecker.CheckAsync` |
| `lecture.persona` / `lecture.outline` / `lecture.vision-describe` | 同上 | `DocLecture` |
| `provider.chat-template` | `Contracts/ChatTemplates.cs` | `LlamaModelProvider.Wrap`（**适配器与目录读同一常量**） |

### 2. 分段模型：声音 vs 契约

每条提示词按"能不能改"切成段，每段带 `PromptSegmentKind`：

- **Editable**：人设、语气、规则——P1 起开放编辑；
- **Locked**：JSON 协议、段落形状、隐私闸门——解析器依赖它们，永远不会开放。

界面上 Locked 段带「代码固定」标记，用户能立刻看出"为什么这段改不了"。
隐私检查整条锁死（四个决定里的第 2 条）。

### 3. 占位符与"响亮失败"

`{{tools}}` / `{{system}}` / `{{user}}` 由 `Contracts/Template.Fill` 单次扫描替换。
**缺参数直接抛异常**，不允许把 `{{tools}}` 原样发给模型——那是一个 bug，不是"降级的回答"。

### 4. 参数与常量也进面板（仍然是只读）

- 四个适配器的采样参数从请求体里的字面量提成只读属性：
  `OllamaModelProvider.Temperature/NumPredict`、`OllamaVisionModel.Temperature/NumPredict`、
  `OpenAiCompatibleProvider.Temperature`、`LlamaModelProvider.ContextSize/MaxTokens/ChatTemplate`；
- 影响效果不亚于提示词的常量也暴露出来：`ModelJudge.MaxCandidates/MaxSummaryChars`、
  `DocLecture.OutlineCharsPerPage/OutlineTotalBudget/AskCharsPerPage`、`SqliteRawStore.SummaryChars`、
  `SensitiveContentChecker.MaxCheckedChars`、`CogniCustosRuntime.MaxToolSteps`。

面板里参数一律带来源（"当前实例" / "工厂默认，本会话未加载" / "常量"），
因为"这个 2048 是现在的还是默认的"直接决定你调试时该信哪个数。

### 5. 涉及文件

| 文件 | 变化 |
|---|---|
| `src/CogniCustos.Core/Prompts.cs`（新） | 目录、键、分段、`Render`/`Segment` |
| `src/CogniCustos.Contracts/ChatTemplates.cs`（新） | `Template.Fill` + `ChatTemplates.QwenInstruct`（适配器与目录共用） |
| `CogniCustosRuntime` / `Judge` / `SensitiveContentChecker` / `DocLecture` | 改读目录；`DocLecture.Persona` 变成目录渲染结果 |
| 四个适配器 | 采样参数提成属性，行为不变（默认值即原字面量） |
| `src/CogniCustos.Ui/ViewModels/AdvancedViewModel.cs`（新） | 面板状态；`AdvancedRowViewModel`/`AdvancedItemViewModel` |
| `SettingsPage.axaml` | 高级设置窗口（900×566，ZIndex 41）：左列表 + 右分段只读文本框 |
| `IShellDataSource.AdvancedSnapshot()` | 新接缝；小样从目录 + 真实 provider 组装，演示源给样例 |
| `ShellViewModel` | `Advanced` 属性、`OpenAdvanced`、Esc 关闭、箭头复位 |
| `Lang.en/zh.axaml` | 新增 51 个键（两侧 278，仍一致） |

### 6. 怎么验证

```bash
dotnet build CogniCustos.slnx -c Debug          # 0 errors
dotnet run --project src/CogniCustos.Selftest   # passed=106（+7 条提示词不变量）
dotnet run --project tools/CogniCustos.Ui.Tests # ALL PASS（77）
dotnet run --project tools/CogniCustos.Ui.Shots -- artifacts/ui 1280 720   # advanced 状态
```

自检盯住的不变量（这些是"改提示词不能改坏"的底线）：

- 每条提示词都能用声明的变量渲染出来，且渲染后不含 `{{…}}`；
- 缺变量时抛异常（不会静默发出占位符）；
- `chat.system` 仍然含 `{"action":"tool"}` / `{"action":"answer"}` 与 `task.add`；
- `judge.format` / `lecture.outline` 仍然含解析器要的字段（id/relevant/category、summary/sections/page）；
- 隐私检查整条 Locked、chat 协议 Locked、人设 Editable；
- 适配器的对话模板与目录里那条**逐字节相等**（这条专门防"两份模板"）；
- `DocLecture.Persona` 就是目录渲染结果。

界面侧两条：面板能开、条目/分段/锁定标记都在、显示的是数据源给的真实文本；
关闭后返回箭头复位。

### 7. 下一步（P1，按约定顺序）

可编辑 + 保存到 `settings.json`（默认值留在代码里做回退）+ 恢复默认 + 变量校验；
先只放开 `summary` / `lecture.persona` / `lecture.vision-describe` 这类弱耦合段，
`chat.protocol`、`judge.format`、`guard.*` 保持只读。

---

## 2026-11-25 · PROMPT-P1 可编辑（覆盖存储 + 校验 + 恢复默认 + 试跑）

P0 是"看得见"，P1 是"改得动"，并且**改的地方就是代码真正读的那一处**。

### 1. 三个决定（沿用上一轮拍板）

1. 只有 **Editable 段**能改；`chat.protocol`、`judge.format`、`lecture.outline/format`、`guard.*` 仍是 Locked，界面上没有输入框；
2. 默认文本永远留在 `Core/Prompts.cs`，用户改动只作为**覆盖**存进 `state/prompts.json`；
3. 保存时校验：非空、占位符集合必须与默认一致。

### 2. 覆盖是怎么生效的

```
PromptCatalog.Render(key)            ← 所有调用点唯一的入口
   └─ SegmentText(key, segmentId)    ← 覆盖优先，否则默认
        └─ PromptOverrides.TryGet()  ← state/prompts.json
```

`CogniCustosRuntime.Create` 启动时 `PromptCatalog.Configure(PromptOverrides.Load(...))`。
所以改一句提示词 = 写一个 JSON 键，不需要重启代码路径、也不需要重新接线。

**"恢复默认"就是删除那个键**——这也是为什么默认值必须留在代码里，而不是被覆盖文件接管。

### 3. "试跑"：把"改了有没有用"变成可观察的

每个可试跑的条目下面有一个 `试跑` 按钮：用**当前文本**真发一次请求，显示
"实际发送的内容 / 耗时 / 回复 / 失败原因"。这同时是排查阅读器问题的工具——
`--reader-ask` 用的就是同一条 `DocLecture.PromptTrace`。

### 4. 顺手修掉的阅读器问题（用户反馈"PDF 不能有效回答"）

| 现象 | 原因 | 处理 |
|---|---|---|
| 没框选就提问，回答像在胡说 | 选区是 (0,0,0,0)，代码把页面裁出 **1×1 像素**交给视觉模型 | 选区面积 < 0.25% 视为"没框"，不再调视觉模型；提示词里改成"这一问没有框选，按整页回答" |
| 整页文字被喂成"一词一行" | `DocumentBook.Text` 用 `\n` 拼接每个词 | 按基线聚合成行（同 baseline 用空格，跨行才换行） |
| 没有模型时像坏掉 | 只回一句"没有可用的推理模型" | 明确写成"当前没有可用的推理模型，只能把你选中的原文给你看；配置模型后才能讲评" |

复验（真实 58 页课程 PDF）：有框选 → 6.4 s、回答正确；无框选 → `hadBox=False / regionPngBytes=0`，1.7 s 回答按整页展开。

### 5. 涉及文件

| 文件 | 变化 |
|---|---|
| `Core/PromptOverrides.cs`（新） | `state/prompts.json` 的读写；坏文件退化为空 |
| `Core/Prompts.cs` | `Configure` / `SegmentText` / `IsOverridden` / `Validate` / `CheckPlaceholders` / `SaveOverride` / `ResetOverride` |
| `Core/CogniCustosRuntime.cs` | 启动时装载覆盖 |
| `Core/DocLecture.cs` | `PromptTrace`（试跑与 `--reader-ask` 共用）；无模型文案；无框选分支 |
| `Documents/DocumentBook.cs` | 整页文字按行聚合 |
| `Sample/RuntimeReaderService.cs` | 框选面积门槛、`LastAskTrace`、`TracePrompts` |
| `Sample/ReaderAsk.cs`（新） | `--reader-ask <file> [page] [x0 y0 x1 y1] [question]`，打印真实提示词与回复 |
| `Ui/ViewModels/AdvancedViewModel.cs` | 行内编辑、保存/恢复、覆盖徽标、试跑面板 |
| `Ui/Views/SettingsPage.axaml` | 输入框 + 保存/恢复按钮 + 试跑区 |
| `IShellDataSource` / `ShellViewModel` / `DemoShellDataSource` / 小样 | 三个新接缝：保存、恢复、试跑 |
| `Lang.*` | 新增 11 个键（两侧 289） |

### 6. 怎么验证

```bash
dotnet build CogniCustos.slnx -c Debug          # 0 errors
dotnet run --project src/CogniCustos.Selftest   # passed=111
dotnet run --project tools/CogniCustos.Ui.Tests # ALL PASS（79）
dotnet run --project tools/CogniCustos.Ui.Shots -- artifacts/ui 1280 720
```

自检盯住：改动立即生效、恢复默认回到原文、Locked 段拒绝、占位符增删被拒、空文本被拒、
覆盖文件能往返读写。界面测试盯住：编辑 → 保存 → 徽标出现 → 恢复 → 回到默认，以及 Locked 段没有输入框。

### 7. 还没做（P2/P3）

- 按用途分档的温度与能力声明（`InferenceProfile` + `Capabilities`），以及让 `num_ctx` 与点读预算联动；
- 连接测试（对某个 profile 发 ping，显示首 token/总耗时）；
- 结构化调用日志（现在错误要么进 Toast、要么被 catch 吞掉）。

---

## 2026-11-25 · 云端模型"配置完反而找不到模型" + 设置页连接测试

### 1. 根因：可用性用探测代替了事实

`OpenAiCompatibleProvider.IsAvailable` 以前是 `GET /models` 的结果并缓存：

```csharp
var resp = _http.GetAsync("models", 3s);   // 很多端点没有这个路径
_available = resp.IsSuccessStatusCode;     // 于是"配好的模型"被判成不可用
```

`ModelRouter.Resolve` 只选 `IsAvailable` 的 provider，于是**填对了也被跳过**，回落到 standby，
用户看到的就是"配置完反而说找不到模型"。没配置时反而能用，是因为那时借的是本地 Ollama。

### 2. 修法

1. **可用 = 已配置**：`IsAvailable => true`。对不对由真实请求回答；
   错 key/错地址会以端点自己的 HTTP 错误出现在第一次调用里（比"找不到模型"有用得多）。
2. **`/v1` 回退**：先 `chat/completions`，只在 **404** 时再试一次 `v1/chat/completions`。
   有人说 base URL 带 `/v1`、有人说不带——不再让用户猜。
3. **测试按钮**：设置页「主推理模型」三个字段下方 `测试连接`，
   用**表单里当前的值**（未保存也可以）真发一次请求，结果显示一行：成功/失败 · 耗时 · 端点原话。

### 3. 验证（新增 3 条自检，用本地 TCP 桩，不需要网络）

- 一个**没有 /models** 的端点 → `IsAvailable=true` 且 `TestAsync` 成功（这正是原 bug 的复现）；
- 只有 `/v1/chat/completions` 的端点 → 记录到的路径依次是 `/chat/completions`、`/v1/chat/completions`，拿到回答；
- `401` 响应 → 测试返回失败且消息里带 `401`（端点的原话，不是"找不到模型"）。

界面侧一条：表单值确实传到了接缝、测试中按钮不可点、结果行同时含耗时与端点原话、失败行含 401。

### 4. 涉及文件

| 文件 | 变化 |
|---|---|
| `Adapters/OpenAiCompatibleProvider.cs` | `IsAvailable` 恒真；`TestAsync`；9 行 `/v1` 回退；错误里带 Endpoint |
| `Contracts/CloudTestResult.cs`（新） | 适配器的测试结果类型 |
| `Ui/ViewModels/CloudTestResult.cs`（新） | 面板用的同名结构（UI 不引用 Contracts） |
| `ModelConfigurationViewModel` | `TestCommand`/`IsTesting`/`TestStatus`/`CloudTestRequested` |
| `SettingsPage.axaml` | 三个字段下面的「测试连接」+ 结果行 |
| `IShellDataSource` / `ShellViewModel` / 演示源 / 小样 | `TestCloudAsync(model, url, key)`；小样用表单值建临时 provider |
| `Lang.*` | 5 个键（`Settings.Model.Test*`），两侧 294 |

### 5. 怎么验证

```bash
dotnet run --project src/CogniCustos.Selftest   # passed=114（+3 云端）
dotnet run --project tools/CogniCustos.Ui.Tests # ALL PASS（80）
```

手工：设置页填 `https://api.deepseek.com` + `deepseek-chat` + key → 点「测试连接」→
应显示`已连通 · 1234 ms · 可以。`；换成错 key 应显示 `连接失败 · … 401 …`。

---

## 2026-11-25 · 验证"配置是否真的生效"：统一装配点 + `--model-status` + 说清为什么没模型

用户问题："配置了 URL/模型/API key，测试也成功，可点读仍说没有可用推理模型。"

### 1. 先把链路证明一遍（不靠推理）

自检里新增两条，用本地 TCP 桩跑**应用真实的顺序**：

| 断言 | 覆盖的场景 |
|---|---|
| 保存后 `Resolve(Reasoning)` 是 `cloud:stub-model`，且 `DocLecture.AskAsync` 真的打到桩 | 会话内保存后立刻提问 |
| 只在磁盘留 settings.json、**重启**后再装配，仍解析到云端且提问打到桩 | 配完关掉重开 |
| 端点没有 `/models` 仍然可用 | 原 bug 的复现 |

结论：代码链路上"保存 → 路由 → 点读"是通的。剩下的可能性只有两种——
跑的不是这一版，或者装配点没跑到（于是有了下面第 2 条）。

### 2. 装配点收敛到一处

GUI、`--check`、`--reader-check`、`--reader-ask` 之前各写一份 runtime 装配。
现在统一到 `Sample/AppRuntime.cs`：`SetFactory` + `BuiltInFactory` + 借用 Ollama 只此一处，
任何入口都不会再"少配一步"。

### 3. `--model-status`：把"到底在用哪个模型"变成一行事实

```
CogniCustos.Sample.exe --model-status
  data / settings / prompts 路径
  cloud: configured=True model='deepseek-chat' base='https://api.deepseek.com' key=已填写
  registered providers:
    cloud:deepseek-chat     role=Reasoning  available=True
  resolve(reasoning) = cloud:deepseek-chat  → questions will use this
```

退出码 0 = 有模型能回答，2 = 没有任何 provider 可用（并给出下一步该查什么）。
这是给用户的自检入口，也是我下次定位问题时要的第一条信息。

### 4. 说清"为什么没有模型"

阅读器的 standby 文案以前只有一句"没有可用的推理模型"。现在列出**注册了哪些 provider、
各自可不可用**，并区分"云端还没注册（填完要点保存）"和"注册了但地址/密钥不对"。

### 5. 顺带

- `CloudModelService.StatusText()` / `UseCloud` 的措辞从"已生效"改成"已保存"
  ——保存成功不等于调用成功，不能让界面替系统下这个结论。

### 6. 验证

```bash
Selftest   → passed=116（+2：保存后链路、重启后链路）
Ui.Tests   → ALL PASS（80）
--check / --reader-check / --model-status 均 OK
```

---

## 2026-11-25 · 没有"保存键"这件事：显式保存 + 未保存标记 + 原因就地显示

用户反馈："你让我点保存，可我在界面上看不到保存键；反而看到一句没保存的提示。"这是对的。

### 1. 原设计错在哪

三个字段是"双击编辑、**失焦即提交**"：

```csharp
field.Committed += () => CloudSaveRequested?.Invoke(ModelName.Value, Url.Value, ApiKey.Value);
```

于是：改「模型名称」→ 点到别处 → 立刻用**另外两个还是空的字段**发起保存 → 失败 → 弹一句 toast；
而字段的值已经被 `Commit()` 写进内存，行上显示的是新值。用户看到的正是"配置显示出来了，
却提示没保存，而且找不到保存键"。

### 2. 改成显式保存

- 字段提交只做一件事：把表单标记为**未保存**（界面上出现「未保存」标签）；
- 三格下方新增 **保存** 按钮：三个字段**一起**写入，缺一个就拒绝；
- 拒绝/成功的原因就地显示在按钮下面（不只靠 toast），绿色/红色区分；
- 按钮在无改动时变灰（`CanExecute` 依赖 `HasUnsavedChanges`）。

### 3. 措辞

`已保存并生效` → `已保存`。保存成功不等于调用成功，界面不该替系统下这个结论。

### 4. 验证

- 界面自检：改字段 → 出现「未保存」、存储里**没有**新值、保存键可用 → 点保存 → 存储变成新值、
  「未保存」消失、就地出现"已保存 …"；
- 另一条：保存被源拒绝时，原因出现在按钮下面且用失败色（不只剩 toast）；
- 全部 116 条自检 + 界面自检全绿。

### 5. 教训

凡是"用户以为有、其实没有"的控件，都是我描述界面时的想当然。以后描述交互前先看 XAML。

---

## 2026-11-25 · 文本全部走资源 + 回复语言跟随界面并让位于用户

### 1. 文本审计：XAML 干净，问题在 C#

扫了所有 `.axaml` 的 `Text/Content/Header/Watermark` 字面量：只剩图标字形（`&#xE70F;` 之类）与品牌名 `CogniCustos`，都不该翻译。

C# 里找出 5 处**真·界面文本**（其余是注释或演示数据）：

| 位置 | 原文 | 现在 |
|---|---|---|
| `ShellViewModel` 邮件状态 | `$"已添加 {address}"`、`$"已删除 {address}"` | `Settings.Email.Added` / `.Removed`（`{0}`） |
| `ShellViewModel` 网页状态 | `"已添加"` / `"已删除"` | `Settings.Web.Added` / `.Removed` |
| `EmailAccountRow.Scope` | `$"最近 {n} 天 · {(unread?"只抓未读":"全部")}"` | `Settings.Email.ScopeFormat` + `.UnreadOnly`/`.All` |
| `EmailRetrievalViewModel.EmptyNote` | 硬编码中文 | `Settings.Email.EmptyNote` |
| `WebBrowsingViewModel.EmptyNote` | 硬编码中文 | `Settings.Web.EmptyNote` |

### 2. 切语言后不刷新的两个区块

邮件与网页两个设置区块**从来没有 RefreshText**，所以切了语言标签还是旧语言。现在：

- `EmailRetrievalViewModel.RefreshText()`：自身 7 个标签 + 每个账号行 + 每个邮箱行；
- `WebBrowsingViewModel.RefreshText()`：`EmptyNote` / `Status`；
- `SettingsViewModel.RefreshTitles()` 一并调用；`Advanced` 走 `OnAllPropertiesChanged`。

顺带发现并修掉一个真 bug：`Settings.Email.UnreadOnly` 在我的追加里与既有键重复，Avalonia 启动即抛
`ResourceDictionary.Add` 重复键。资源文件现在 308 键、0 重复（并且加了去重脚本逻辑，后续追加不会再撞）。

### 3. 回复语言：先是界面语言，用户一开口就让位

规则写成两条，缺一不可：

**（a）提示词层面**：`PromptLanguage`（Core）持有 `zh`/`en`，`PromptCatalog.Render` 给
"会产出人话"的 6 条提示词自动追加一段语言指令——`chat.system`、`summary.one-line`、`judge.system`、
`lecture.persona`、`lecture.outline`、`lecture.vision-describe`；隐私检查（JSON 判决）不加。
指令原文：

> 回答语言：简体中文，也就是界面语言。用户没有另行选择时，一律用它。
> 但用户自己的语言优先：如果他这一轮的提问用的是别的语言，或者明确点名了某种语言，就整段改用那种语言回答，不要把问题翻译回中文。
> 一段回答里不要中英混排；专有名词、代码、文件名、引用原文保持原样。

**（b）用户消息层面**：`DocLecture` 的整段脚手架（"材料：… 这一页的文字… 问题：…"）现在跟随
**回答语言**，而回答语言 = 用户这一问的语言（有 CJK 即中文），没提问时才用界面语言；
最后一行再补一句目标语言（"（请用简体中文回答。）" / "(Answer in English.)"）。

为什么两层都要：实测第一版只有 (a) 时，**英文界面 + 中文提问仍然整段英文**
（模型把"English, no Chinese"当绝对禁令）。加上 (b) 之后同一问题立刻变成中文。

### 4. 实测记录（真实 58 页 PDF，本地 qwen2.5:7b）

| 界面语言 | 提问 | 结果 |
|---|---|---|
| 中文 | 中文 | 中文 |
| 英文 | 英文 | 英文 |
| 英文 | 中文 | **英语（错）→ 加上 (b) 后：中文（对）** |

命令行复现：`set COGNI_LANG=en` 后跑 `--reader-ask`。

### 5. 验证

```bash
Selftest   → passed=118（+2：语言指令落在哪些提示词；用户语言优先于界面语言，断言在真实提示词上）
Ui.Tests   → ALL PASS（+1：切语言必须通知宿主，邮件区块重新渲染）
Ui.Shots   → ALL OK
```

### 6. 还没做（如实记）

- Core 里仍有**中文硬编码的诊断文案**（提示词校验、云端校验、模型注册原因等）。它们会出现在
  英文界面里。要彻底解决需要 Core 输出**消息码**、由宿主翻译——这是下一步，不是这一轮。
- 演示数据源（`DemoShellDataSource`）里的示例文本仍是中文：那是"数据"，不是界面文案。
