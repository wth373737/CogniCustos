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
