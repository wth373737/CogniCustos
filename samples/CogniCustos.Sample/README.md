# CogniCustos — 可运行小样（UI + 真实运行时）

这不是产品，是**把已经做完的界面和已经做完的后端接起来的一个可运行小样**，用来获得"即视感"、
发现设计缺口。它只做一条闭环：

```
提醒条 / 三张卡  ←  运行时真实数据（任务、日程、待确认提议、沙箱笔记）
确认 / 无视        →  审批处理器（真写 SQLite）
编辑笔记           →  沙箱 notes 分区
底部输入           →  runtime.ChatAsync（Ollama 在则真跑，不在则降级）
```

## 运行（开发）

```bash
dotnet run --project samples/CogniCustos.Sample
```

数据目录默认是 **exe 同级的 `data/`**，可以用环境变量覆盖：

```powershell
$env:COGNICUSTOS_HOME = "D:\cogni-data"; dotnet run --project samples/CogniCustos.Sample
```

首次启动会自动种一批真实数据（两条任务、一个日程块、一条待确认提议、一段沙箱笔记），
所以打开就能看到内容、也有东西可以点。

## 自检（不开窗口）

```bash
samples/CogniCustos.Sample/bin/Debug/net8.0/CogniCustos.Sample.exe --check
```

它会在 `artifacts/sample-check/` 建一个临时运行时，读一遍界面要显示的内容，然后**走确认路径**，
最后核对 SQLite 里的日程块数量 +1、待确认清零。输出形如：

```
line     : 2 entries
plan     : 1 blocks
incoming : 1 proposals
notes    : 1 paragraphs
approve  : blocks 1 -> 2, pending left 0
OK: confirm wrote through the approval gate
```

## 打包（免安装）

```powershell
dotnet publish samples/CogniCustos.Sample -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -o artifacts/sample/win-x64
```

产物是自包含文件夹（不需要装 .NET），双击 `run.cmd` 或 `CogniCustos.Sample.exe` 即可。

## 已知边界（这一版故意不做的）

- 没有登录、抓取、知识库/数据库界面；这些是后续设计要补的；
- AI 改写修辞没有接：确认时直接用原文；
- 提示条与卡片不会自动刷新：确认后的条目由界面自己插入，重启后从数据库重新读。
