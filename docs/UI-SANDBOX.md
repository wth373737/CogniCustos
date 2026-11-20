# UI 沙箱（CogniCustos.Ui）

独立的 UI 工程：**不引用 Core / Adapters**，不连模型、不连数据库，用假数据把界面和交互先做对。
等界面定稿，再由 `CogniCustos.Desktop` 或新的宿主把真实数据接进来。

```
src/CogniCustos.Ui/            ← WinExe，可单独运行（Avalonia 12.1.6 / net8.0）
  App.axaml / Program.cs
  ShellWindow.axaml            ← 窗口（1280×800，最小 1040×600），Esc 在窗口级处理
  Views/ShellView.axaml        ← 主界面：顶栏 / 卡片区 / 推理记录 / 输入条 / 右侧竖排栏 / 抽屉 / 帮助
  Views/SettingsPage.axaml     ← 设置页（盖住整个窗口）
  Styles/Tokens.axaml          ← 颜色、字体、图标字形、手绘几何（换皮只改这里）
  Styles/Controls.axaml        ← 按钮/卡片/导航等样式
  Localization/Strings.cs      ← 中英双语（含假数据与 16 个分区标题）
  ViewModels/ShellViewModel.cs ← 窗口状态（抽屉、设置、语言、输入、Toast）
  ViewModels/CardViewModel.cs  ← 单张卡片（标题、条目、折叠、加一条、变体）
  ViewModels/SettingsViewModel.cs, SettingsSectionViewModel.cs ← 设置页选中状态
  Mvvm/                        ← 极小的 ObservableObject / RelayCommand

tools/CogniCustos.Ui.Shots/    ← 无头渲染：把每个状态导成 PNG（只看图）
tools/CogniCustos.Ui.Tests/    ← 无头回归：12 条断言，非 0 退出码即失败
```

## 运行与调试（VS Code）

| 目的 | 做法 |
|---|---|
| 跑窗口 | `F5` → `UI: run the window (CogniCustos.Ui)`；或终端 `dotnet run --project src/CogniCustos.Ui` |
| 跑回归 | `F5` → `UI: run regression tests`；或 `dotnet run --project tools/CogniCustos.Ui.Tests` |
| 出图自检 | `F5` → `UI: render screenshots headlessly`；或 `dotnet run --project tools/CogniCustos.Ui.Shots -- artifacts/ui 1280 800` |
| 只预览 XAML | 打开 `Views/ShellView.axaml`，Avalonia 扩展的预览器会加载它（`ShellView` 构造函数里有 `Design.IsDesignMode` 分支给设计态 VM）。**这一条没能在你机器上实测**，第一次用预览器时留意一下 |
| 断点 | 直接打断点；改 XAML 后重新 F5（没有热重载） |

> 构建注意：这台机器上 `dotnet build` 在受限沙箱里会因 SChannel / 命名管道被拒，**在 VS Code 里跑不受影响**。

## 交互（已实现）

- 右侧竖排栏：`≡` 抽屉、`?` 帮助、`⚙` 设置页、`En/中` 切语言（当前项高亮）。
- 抽屉：`‹` 向左展开预留区（宽度 208→560 带过渡、内容淡入），展开后变 `›`；抽屉从顶栏下方滑出。
- 卡片：`∨` 折叠 / `∧` 展开（300 ↔ 78）；`＋` 加一条；空列表显示空态文案。
- 输入条：输入后按 **Enter** 追加到第一张卡片，推理记录里显示一行状态（6 秒后自动消失）。
- 关闭：`Esc`（窗口级，启动即生效）、点击抽屉/帮助之外、顶栏 `Home`。
- 设置页：左栏 16 个分区（ListBox 自滚动），选中项标题进右栏；`↩` 或 `Esc` 返回。
- 语言切换是全局的：导航、卡片标题、假数据、空态、预留区文字都跟着变。**用户自己加的条目不会被翻译**（那是用户数据，不是界面文案）。

## 设计约定

- 颜色/字体/图标全在 `Styles/Tokens.axaml`。图标用 Windows 自带 **Segoe MDL2 Assets** 字形；
  数据库图标该字体里没有，手绘成 `StreamGeometry`（`GeoDataBase`）。
- 尺寸按 1280×800 设计：顶栏 76、内容自适应、底部 140、右栏 64。
- 卡片：`Border.card` 宽 300 / 高 300，折叠态高度来自 `CardViewModel.CardHeight`。
  三张卡片由 `ShellViewModel.Cards` 驱动，加第四张只改数据。
- **卡片变体**：`CardVariant`（Plain / Pink / Olive）→ 模板里 `Classes.pink` / `Classes.olive` → `Controls.axaml` 里的样式。
  加一种变体 = 加枚举值 + 两个 bool + 一个样式类。若以后需要**结构不同**的卡片，应该换 `ItemsControl` + 按 VM 子类型选模板。
- 文案全部走 `Strings`，不要在 XAML 里写死中文或英文；假数据走 `ShellViewModel.BuildXxx()`。

## 回归测试（tools/CogniCustos.Ui.Tests）

12 条断言，跑在真实可视树上（真的按键、真的绑定）。它们对应四个已修复的 bug 和若干面板规则：

```
PASS sections_have_titles_in_both_languages            16 个分区中英都有文案
PASS enter_adds_row_and_clears_input                   回车提交
PASS empty_enter_does_nothing                          空输入不提交
PASS card_collapse_and_expand                          折叠 78 / 展开 300
PASS added_rows_use_current_language_and_are_not_retranslated   新增行用当前语言，用户行不回译
PASS language_switch_keeps_settings_selection          ← bug A 回归
PASS language_switch_keeps_user_rows                   ← bug C 回归
PASS escape_closes_drawer_without_inner_focus          ← bug B 回归
PASS escape_closes_settings_with_inner_focus
PASS drawer_and_settings_are_mutually_exclusive
PASS backdrop_visibility_follows_overlays
PASS drawer_expands_and_animates_width                 ← bug D 回归（宽度过渡 + 淡入）
```

这里是**控制台断言**而不是 xunit：`Avalonia.Headless.XUnit 12.x` 依赖 xunit.v3，多一层版本耦合不值得。
退出码非 0 就能当门禁；以后要接测试资源管理器，再加 xunit.v3 即可。

## 出图（tools/CogniCustos.Ui.Shots）

9 个状态 → `artifacts/ui/`（文件名带尺寸，换尺寸不覆盖）：

```
home  drawer  expanded  settings  help  collapsed  toast  settings-zh  settings-deep
```

`settings-deep` 是专门给 bug A 看的：先选中第 6 项再切中文，图上应同时看到列表高亮和右栏标题都在。
常用尺寸：`1280 800`（设计基准）、`1040 600`（最小窗口）、`2560 1440`（大屏）。

## 版本与依赖

- 全解决方案：Avalonia **12.1.6**，net8.0；`Selftest 37/37`、真实窗口启动正常。
- 包版本集中在根目录 `Directory.Packages.props`（中央包管理），各 csproj 不再写版本号。
- 12 的两处迁移（已改）：`TextBox.Watermark` → `PlaceholderText`；
  `Bitmap.Save(path)` / `Save(path, int)` → `Save(path, PngBitmapEncoderOptions.Default)` 或 `JpegBitmapEncoderOptions`。

## 已知限制

1. **DPI 未验证**：所有图都是 100% 缩放渲染；高分屏（150%/200%）下的字距与卡片高度没实测。
2. **大屏不自适应**：2560 宽时三张 300 宽的卡片靠左，右边留白很多。要不要给卡片区加最大宽度或居中，等你定。
3. **无边框标题栏**、抽屉 7 个图标的目标页、设置分区表单、推理记录展开态（右侧 `›`）都还没做——这些会改设计，等你拍板。
4. 预览器（VS Code 扩展）的实际渲染我没验证过。
5. 卡片只有视觉变体，没有结构性差异；也没有拖拽排序。

## 修复记录（2026-11-20 第 3 轮，高档审视后）

| 问题 | 现象 | 修法 |
|---|---|---|
| A 设置选中丢失 | 切语言后右栏标题变空 | 分区改成**稳定身份**（`SettingsSectionViewModel`，只换 Title 不换实例），ListBox 绑 `SelectedItem` |
| B Esc 失效 | 启动后未聚焦时按 Esc 无反应 | 键处理从 `ShellView` 提到 `ShellWindow`（窗口级） |
| C 用户行被清 | 切语言清掉刚输入的行 | `CardViewModel.Refresh()` 只替换种子行，用户行保留 |
| D 抽屉跳变 | 展开是瞬间跳变 | `Width` 加 `DoubleTransition`，预留区用 `Opacity` 淡入；内容改为右对齐 560 宽的 Grid + 裁剪 |
| E 无效配置 | `.vscode/settings.json` 里有不存在的键 | 改成扩展真实的 `avalonia.preview.*`；删掉过时的 OmniSharp 键 |
| G 死代码 | `StyleClass`、`CloseOverlaysCommand` | 删除；`CloseOverlays()` 方法留给后备处理器 |
| H 细节 | 加行文案写死英文、Toast 不消失 | 走 `Strings.NewRowLabel`；Toast 6 秒自动清 |
| I 双滚动 | 设置页套了两层 ScrollViewer | 去掉外层，ListBox 自己滚 |
| J 版本重复 | 三个 csproj 各写 12.1.6 | 根目录 `Directory.Packages.props` |
