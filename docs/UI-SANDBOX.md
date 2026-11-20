# UI 沙箱（CogniCustos.Ui）

独立的 UI 工程：**不引用 Core / Adapters**，不连模型、不连数据库，用假数据把界面和交互先做对。
等界面定稿，再由 `CogniCustos.Desktop` 或新的宿主把真实数据接进来。

```
src/CogniCustos.Ui/            ← WinExe，可单独运行（Avalonia 12.1.6 / net8.0）
  App.axaml / Program.cs
  ShellWindow.axaml            ← 窗口（1280×800，最小 1040×600）
  Views/ShellView.axaml        ← 主界面：顶栏 / 卡片区 / 推理记录 / 输入条 / 右侧竖排栏 / 抽屉 / 帮助
  Views/SettingsPage.axaml     ← 设置页（盖住整个窗口）
  Styles/Tokens.axaml          ← 颜色、字体、图标字形、手绘几何（改这里就能换皮）
  Styles/Controls.axaml        ← 按钮/卡片/导航等样式
  Localization/Strings.cs      ← 中英双语（含假数据），顶栏按钮实时切换
  ViewModels/ShellViewModel.cs ← 窗口状态（抽屉、设置、语言、输入、Toast）
  ViewModels/CardViewModel.cs  ← 单张卡片（标题、条目、折叠、加一条）
  ViewModels/CardItem.cs
  Mvvm/                        ← 极小的 ObservableObject / RelayCommand

tools/CogniCustos.Ui.Shots/    ← 无头渲染：把每个状态导成 PNG（自检 / 视觉回归）
```

## 运行与调试（VS Code）

| 目的 | 做法 |
|---|---|
| 跑窗口 | `F5` → `UI: run the window (CogniCustos.Ui)`；或终端 `dotnet run --project src/CogniCustos.Ui` |
| 出图自检 | `F5` → `UI: render screenshots headlessly`，或 `dotnet run --project tools/CogniCustos.Ui.Shots -- artifacts/ui 1280 800` |
| 出小尺寸图 | 同上，把后两个参数改成 `1000 640`（验证窄窗口） |
| 只预览 XAML | 打开 `Views/ShellView.axaml`，Avalonia 扩展的预览器会实例化它（构造函数已给设计态 VM，带数据渲染） |
| 断点 | 直接打断点；改 XAML 后重新 F5（没有热重载） |

> 构建注意：这台机器上 `dotnet build` 在受限沙箱里会因 SChannel / 命名管道被拒，**在 VS Code 里跑不受影响**；
> 用 DSH 里的 shell 跑构建时需要完全权限。

## 交互（已实现）

- 右侧竖排栏：`≡` 抽屉、`?` 帮助、`⚙` 设置页、`En/中` 切语言（当前项会高亮）。
- 抽屉：`‹` 向左展开预留区，展开后变 `›`；抽屉从顶栏下方滑出，不遮挡顶栏。
- 卡片：标题行 + 条目；`∨` 折叠 / `∧` 展开（高度 300 ↔ 78）；`＋` 加一条假数据；空列表显示空态文案。
- 输入条：输入后按 **Enter** 追加到第一张卡片，并在推理记录里显示一行状态（`Toast`）。
- 关闭：`Esc`、点击抽屉/帮助之外的空白处、顶栏 `Home`，三种都能收起。
- 设置页：左栏 16 个分区（可滚动），选中项显示在右栏标题；`↩` 或 `Esc` 返回。
- 语言切换是全局的：导航、卡片标题、假数据、空态、预留区文字都会跟着变。

## 设计约定（方便你直接改）

- 颜色/字体/图标全在 `Styles/Tokens.axaml`。图标用 Windows 自带 **Segoe MDL2 Assets** 字形；
  数据库图标是该字体里没有的，手绘成 `StreamGeometry`（`GeoDataBase`）。
- 尺寸按 1280×800 设计：顶栏 76、内容自适应、底部 140、右栏 64。
- 卡片：`Border.card` 宽 300 / 高 300；折叠态高度来自 `CardViewModel.CardHeight`。三张卡片由
  `ShellViewModel.Cards` 驱动，加第四张只改数据、不用改 XAML。
- 文案全部走 `Strings`，不要在 XAML 里写死中文或英文；假数据也走 `ShellViewModel.BuildXxx()` 以便随语言切换。

## 回归出图 + 交互自检

`tools/CogniCustos.Ui.Shots` 会依次渲染 8 个状态到 `artifacts/ui/`：

```
home  drawer  expanded  settings  help  collapsed  toast  settings-zh
```

文件名带尺寸（`home-1280x800.png`），换尺寸不会互相覆盖。
渲染完之后还会在真实的可视树上跑 6 条交互断言（真的按键、真的绑定）：

```
PASS enter-submits-input    在输入框里打字并按 Enter → 卡片多一条、输入框清空
PASS submit-shows-status    推理记录显示一行状态
PASS card-collapses         折叠态高度 78、图标翻转
PASS card-expands           展开回来 300
PASS language-switches      中英切换后卡片标题/空态都变了
PASS drawer-opens           抽屉打开、遮罩生效
```

退出码非 0 = 有状态没渲染出来或有断言失败，可以直接拿来当 CI 门禁。

## 升级记录（2026-11-20：Avalonia 11.2.3 → 12.1.6）

全解决方案（含旧的 `CogniCustos.Desktop`）已升级并构建通过，`Selftest 37/37` 仍全过，真实窗口启动正常。
12 里踩到的两处 API 迁移（本仓库已改完）：

| 11 | 12 |
|---|---|
| `TextBox.Watermark` | `TextBox.PlaceholderText` |
| `Bitmap.Save(path)` / `Save(path, int quality)` | `Save(path, PngBitmapEncoderOptions.Default)` 或 `new JpegBitmapEncoderOptions { Quality = ... }` |

另外：`Avalonia.Themes.Fluent`、`FluentTheme`、`UseHeadless` / `CaptureRenderedFrame` 名字都没变；
`net8.0` 仍是受支持的 TFM（12.1.6 同时提供 net10.0 资产）。

## 还没做的事（等你定）

1. 无边框 / 自绘标题栏（草稿里没有系统标题栏，现在是标准窗口）。
2. 抽屉里 7 个图标的行为（现在只有 tooltip 和按压反馈，点下去没有目标页面）。
3. 设置页每个分区的表单；现在只有标题 + 占位说明。
4. 卡片拖拽排序、跨卡片移动、真实日期视图。
5. 推理记录面板的展开态（右侧 `›` 目前是死按钮）。
6. 与 `CogniCustos.Core` 接线：一个 `IShellData` 之类的接口，由真实宿主注入，UI 本身不动。
