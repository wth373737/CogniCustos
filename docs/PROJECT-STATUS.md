# CogniCustos 当前状态（2026-11-25）

> 这一页是"现在有什么、怎么跑、怎么改"的入口。设计规则看 `PARTITION-MODEL.md`，每一步的改动记录看 `IMPLEMENTATION-LOG.md`。

## 1. 一句话

一个 **.NET 8 / Avalonia 12** 的桌面学习助手：**四个分区的数据模型**（原始区 / 沙箱 / 主数据库 / 知识库）已经从"只有文档"变成**可运行**——
AI 只能提出变更，用户确认后才落库；本地小模型（Qwen2.5-0.5B，llama.cpp）**随软件跑**，可选 Ollama、可选显卡。

## 2. 现在能跑起来的东西

| 产物 | 命令 | 它能做什么 |
|---|---|---|
| 桌面骨架 | `dotnet run --project src/CogniCustos.Desktop` | 打开真实窗口，接真运行时（本地模型、数据库、沙箱、审批门） |
| 示例应用 | `dotnet run --project samples/CogniCustos.Sample` | 同一个外壳 + 真后端；可交互点击 |
| **模型自检** | `CogniCustos.Sample.exe --model-check [--gpu]` | 下载内置模型（带 %）→ 切换 → 提问 → 模型摘要 → 判定器验证，打印全部耗时 |
| **无界面自检** | `…/CogniCustos.Sample.exe --check` | 端到端：写入提议 → 卡片两落点 → 确认 → 审批门落库 |
| 单元自检 | `dotnet run --project src/CogniCustos.Selftest` | **75** 条断言（含判定器、摘要、Ollama 切换、审批门原子性） |
| 界面自检 | `dotnet run --project tools/CogniCustos.Ui.Tests` | **62** 条断言（视图模型 + 交互路径，Headless） |
| 界面截图 | `dotnet run --project tools/CogniCustos.Ui.Shots -- <out> 1280 720` | 所有页面状态渲染成 PNG（`ALL OK`） |

## 3. 四区模型：已实现的部分

```
网页/文件 ──抓取──▶ 原始区 raw_artifacts（R1：标题+概括，应用可读字节）
                        │  判定器只看标题+概括 → 只回裁决与句柄
                        ▼
                 主对话（拿不到正文；只会被问"要确认吗"）
                        │  用户确认
                        ▼
              知识库 knowledge.json（K，可引用）◀── 用户"确认收录"
沙箱 sandbox_entries（S：AI 的思考与草稿，条目化，可导出）
主数据库 tasks / schedule_blocks（M）：变更集 = 一次事务，先预演再确认
```

- **单方向通道**：AI → 只能写沙箱或**提出**变更；应用 → 才写主库/知识库。
- **可见性阶梯** `R0/R1/S/K/M`：只有 K/M 能进主上下文或被引用；未知一律降级为 R0。
- **变更集**：一个提议 = N 个 `Change` = 一个事务；任一失败整组回滚（"什么都没写"）。
- **预演**：`ApprovalGate.Preview` 在事务里真跑再回滚（粗版，见第 6 节）。

## 4. 模型层：已实现

| 能力 | 实现 | 实测 |
|---|---|---|
| 内置模型 | `qwen2.5-0.5b-instruct-q4_k_m.gguf`（468.6 MB）+ LLamaSharp/llama.cpp | 首答 2.1 s；预热后 86 字/秒（CPU） |
| 显卡 | Vulkan 后端（Radeon RX 7900 XTX） | **374 字/秒，约 4.3 倍**；日志确认 `layer N assigned to device Vulkan0` |
| 摘要（R1） | 后台队列：`Put` 立刻返回降级摘要，模型摘要随后原地更新为 `summary_kind=model` | ingest **10 ms**；模型摘要在后台约 0.2–0.6 s |
| 判定器 | `ModelJudge`：先跑关键词（免费、兜底），再问模型（只给标题+80 字概括），按 id 合并 | `judge hit #2 relevant=True category=日程` |
| Ollama | 设置页点开小窗（地址固定 `127.0.0.1:11434`，只填模型名）→ **删除内置模型** → 切到 Ollama | 切换 0.01 s |
| 硬件加速 | 设置页开关（Vulkan / CPU），落盘 `UseGpu` | 下次模型调用生效 |

所有依赖模型的环节都有**无模型降级**：没有模型时对话走 standby、摘要走前 200 字、判定走关键词。

## 5. 数据落点（`COGNICUSTOS_HOME` 或默认数据目录）

```
state/cognicustos.db        v5：tasks / schedule_blocks / proposals / proposal_changes /
                                 sandbox_entries / raw_artifacts（含 visibility 列）
state/knowledge.json        知识库条目（Visibility = K）
state/settings.json         语言、Ollama、LocalModelSource、BuiltInInstalled、UseGpu
state/raw/<xx>/<sha256>.txt 原始字节（应用可读，无工具暴露）
state/proposals.json        审批门的导出视图（只读）
sandbox/SANDBOX.md          最新条目的导出视图（不是真源）
sandbox/THOUGHTS.md         思考记录导出
models/builtin/             内置模型（model.json 记 sha256）
```

## 6. 已知未做（按我建议的优先级）

1. **HTTP 抓取未接**：门户读取已能写原始区，但还没把真实网页抓取接进定时器；邮件抓取也未接。
2. **原始区保留策略**：`status` 列已经能写（`kept` / `accepted`），但"多久后清理未确认字节"没有规则。
3. **预演是粗版**："真跑再回滚"，不是逐条模拟；对文件类目标只能单独提交。
4. **判定器的成本**：每条消息都问一次模型；可以先看关键词是否已高分命中再决定要不要问。
5. **硬件加速没有探测**：不检查机器到底有没有 Vulkan/CUDA 可用；用户点了就在模型调用时报错。
6. **知识库检索**仍是关键词（FTS5/向量检索在计划里）。
7. **界面上还没有"未确认材料列表 + 确认收录按钮"**（数据与 API `rt.AcceptRawAsync` 已就绪）。

## 7. 常见改动去哪儿

| 想改什么 | 改哪里 |
|---|---|
| 换模型 / 换对话模板 | `BuiltInModelInstaller`（文件名、URL、期望大小）+ `LlamaModelProvider.Wrap`（模板） |
| 换 GPU 后端（CUDA/AMD-Metal） | 包换成 `LLamaSharp.Backend.Cuda12`；`ModelParams.Devices` 写 `CUDA0` |
| 判定器提示词/阈值 | `ModelJudge.Ask`；`MaxSummaryChars` 控制给模型看多少概括 |
| 摘要改同步/改后台 | `IRawStore.BackgroundSummaries` |
| Ollama 那套语义 | `LocalModelService.UseOllama`（切换即删内置模型） |
| 四区规则本身 | `docs/PARTITION-MODEL.md`；代码分别是 `Visibility`/`ApprovalGate`/`RawStore`/`SandboxStore` |
| 界面尺寸与状态 | `ShellScenarios`（状态）、`ShellView.axaml`（画布 1280×720 + Viewbox） |

## 8. 验收基线

```
dotnet build CogniCustos.slnx -c Debug   → 0 errors
Selftest                                   → passed=75
Ui.Tests                                   → ALL PASS（62）
Sample --check                             → OK（两落点卡片 + 审批门落库）
Ui.Shots                                   → ALL OK（全部状态渲染）
```
