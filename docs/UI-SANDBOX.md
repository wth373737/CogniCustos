# UI 沙箱（CogniCustos.Ui）

独立的 UI 工程：**不引用 Core / Adapters**，不连模型、不连数据库，用假数据把界面和交互先做对。
等界面定稿，再由 `CogniCustos.Desktop` 或新的宿主把真实数据接进来。

```
src/CogniCustos.Ui/            ← WinExe，可单独运行（Avalonia 12.1.6 / net8.0）
  App.axaml / Program.cs
  ShellWindow.axaml            ← 窗口 + Viewbox（等比缩放），Esc 在窗口级处理
  Views/ShellView.axaml        ← 1280×800 画布：顶栏 / 卡片 / 推理记录 / 输入条 / 竖排栏 / 抽屉 / 帮助
  Views/SettingsPage.axaml     ← 设置页（盖住整块画布）
  Resources/Lang.en.axaml      ← 英文文案（可翻译资源）
  Resources/Lang.zh.axaml      ← 中文文案
  Localization/Localizer.cs    ← 语言切换：换资源字典 + 给 VM 提供取词
  Styles/Tokens.axaml          ← 颜色、字体、图标字形、手绘几何
  Styles/Controls.axaml        ← 按钮/卡片/导航等样式
  ViewModels/ShellViewModel.cs ← 窗口状态（抽屉、设置、语言、输入、Toast）
  ViewModels/CardViewModel.cs  ← 单张卡片（标题、条目、折叠、加一条、变体）
  ViewModels/SettingsViewModel.cs, SettingsSectionViewModel.cs ← 设置页选中状态
  Mvvm/                        ← 极小的 ObservableObject / RelayCommand

tools/CogniCustos.Ui.Shots/    ← 无头渲染：把每个状态导成 PNG（只看图）
tools/CogniCustos.Ui.Tests/    ← 无头回归：19 条断言，非 0 退出码即失败
```

## 一、等比缩放（任何分辨率都保持示例图的比例）

`ShellWindow` 里是

```xml
<Viewbox Stretch="Uniform" StretchDirection="Both">
  <views:ShellView Width="1280" Height="800" />
</Viewbox>
```

即：**整块界面按 1280×800 画布排版，然后整体等比缩放到窗口**。

- 1280×800 → 1.00×；1024×640 → 0.80×；1920×1080 → 1.35×（两侧留边）；2560×1440 → 1.80×（两侧留边）。
- 字、间距、卡片、图标全部同比例变化，不会出现"窗口变大只有空白变多"。
- 缩放是**矢量变换**，文字不会糊（Skia 在最终变换下绘制）。
- 命中测试、焦点、输入框都跟随缩放，不需要额外处理。

取舍：

| 需求 | 做法 |
|---|---|
| 不同比例显示器（16:9 / 21:9） | 现在是留边（`Uniform`）。想改成裁切填满 → 换 `Stretch="UniformToFill"` |
| 想连窗口比例一起锁死 | 在 `ShellWindow` 里按 1.6 约束 `Width/Height`（会挡住用户自由拉伸） |
| 想改设计基准尺寸 | 改 `ShellView` 的 `Width/Height` 与像素值即可，画布整体比例随之变化 |

## 二、文本资源与翻译

文案全部在 `Resources/Lang.en.axaml` / `Lang.zh.axaml`，键名分组：`App.*`、`Card.*`、`Tile.*`、`Section.*`、`Tip.*`、`Demo.*`、`Toast.*`、`Help.*`、`Settings.*`、`Drawer.*`、`Reasoning.*`。

两种消费方式，同一份文件：

```xml
<!-- XAML：随语言切换自动刷新 -->
<TextBlock Text="{DynamicResource App.Home}" />
<Button ToolTip.Tip="{DynamicResource Tip.Menu}" />
```

```csharp
// 视图模型：取当前语言的字符串
var label = Localizer.Get("Card.Row.New");
```

切换机制：`Localizer.SetLanguage("zh")` 用 `AvaloniaXamlLoader` 载入 `Lang.zh.axaml`，替换 `Application.Resources` 里第 2 个合并字典；`DynamicResource` 自动重解析，VM 侧由 `ShellViewModel.ApplyLanguage` 重取一次。

**新增一种语言**（例如日语）：
1. 复制 `Lang.en.axaml` → `Lang.ja.axaml`，只改值、不改键；
2. `Localizer.KnownLanguages` 加上 `"ja"`；
3. 若要 UI 里能切到它，把 `NextLanguage()` 改成循环或加一个语言菜单。

翻译质量由测试兜底（见下），键名拼错会返回 `!key!` 并在界面上露出来，不会静默变空。

## 三、运行与调试（VS Code）

| 目的 | 做法 |
|---|---|
| 跑窗口 | `F5` → `UI: run the window (CogniCustos.Ui)`；或 `dotnet run --project src/CogniCustos.Ui` |
| 跑回归 | `F5` → `UI: run regression tests`；或 `dotnet run --project tools/CogniCustos.Ui.Tests` |
| 出图 | `F5` → `UI: render screenshots headlessly`；或 `dotnet run --project tools/CogniCustos.Ui.Shots -- artifacts/ui 1280 800` |
| 只预览 XAML | 打开 `Views/ShellView.axaml`，Avalonia 扩展的预览器会加载它（构造函数里有 `Design.IsDesignMode` 分支给设计态 VM）。**这一条没能实测**，第一次用预览器时留意 |

> 构建注意：这台机器上 `dotnet build` 在受限沙箱里会因 SChannel / 命名管道被拒，**在 VS Code 里跑不受影响**。

## 四、交互（已实现）

- 右侧竖排栏：`≡` 抽屉、`?` 帮助、`⚙` 设置页、`En/中` 切语言（当前项高亮）。
- 抽屉：`‹` 向左展开预留区（宽度 208→560 带过渡、内容淡入），展开后变 `›`；抽屉从顶栏下方滑出。
- 卡片：`∨` 折叠 / `∧` 展开（300 ↔ 78）；`＋` 加一条；空列表显示空态文案。
- 输入条：输入后按 **Enter** 追加到第一张卡片，推理记录显示状态，6 秒后自动消失。
- 关闭：`Esc`（窗口级，启动即生效）、点抽屉/帮助之外、顶栏 `Home`。
- 设置页：左栏 16 个分区，选中项标题进右栏；`↩` 或 `Esc` 返回。
- 语言切换：全局生效；**用户自己加的条目不会被翻译**（用户数据不是界面文案）。

## 五、设计约定

- 颜色/字体/图标全在 `Styles/Tokens.axaml`。图标用 Windows **Segoe MDL2 Assets** 字形；数据库图标该字体没有，手绘 `StreamGeometry`。
- 画布 1280×800：顶栏 76、内容自适应、底部 140、右栏 64；卡片 300×300（折叠 78）。
- 卡片变体：`CardVariant`（Plain/Pink/Olive）→ `Classes.pink/olive` → 样式。加变体 = 枚举 + 两个 bool + 样式类；要**结构不同**时改按 VM 子类型选模板。
- 新文案先加进两个 Lang 文件，再在 XAML 用 `DynamicResource`、在 VM 用 `Localizer.Get`。

## 六、回归测试（tools/CogniCustos.Ui.Tests）

19 条断言，跑在真实可视树上（真按键、真绑定、真缩放）。非 0 退出码即失败。

```
PASS language_files_have_identical_non_empty_keys   en/zh 键集合完全一致且没有空值
PASS settings_sections_all_have_resources           16 个分区键都在资源里
PASS every_key_resolves_in_both_languages           两种语言下每个键都能取值
PASS enter_adds_row_and_clears_input                回车提交
PASS empty_enter_does_nothing                       空输入不提交
PASS card_collapse_and_expand                       折叠 78 / 展开 300
PASS added_rows_use_current_language_and_are_not_retranslated
PASS language_switch_keeps_settings_selection       ← 设置选中不丢
PASS language_switch_keeps_user_rows                ← 用户行不被清
PASS dynamic_resource_text_updates_on_language_switch  ← DynamicResource 真刷新
PASS escape_closes_drawer_without_inner_focus       ← Esc 无焦点也生效
PASS escape_closes_settings_with_inner_focus
PASS drawer_and_settings_are_mutually_exclusive
PASS backdrop_visibility_follows_overlays
PASS drawer_expands_and_animates_width
PASS canvas_scales_uniformly_at_1024x640            ← 等比缩放 0.80
PASS canvas_scales_uniformly_at_1280x800            ← 1.00
PASS canvas_scales_uniformly_at_2560x1440           ← 1.80
PASS canvas_scales_uniformly_at_odd_aspect          ← 16:10 以外的比例不扭曲
```

缩放断言的做法：外壳必须始终以 1280×800 布局（`Bounds`），再由 `TransformToVisual(window)` 读出的矩阵必须是 `s=0.8/1.0/1.8` 且 M11==M22（等比、不拉伸）。

用控制台断言而不是 xunit：`Avalonia.Headless.XUnit 12.x` 依赖 xunit.v3，多一层版本耦合不值得；退出码已经够当门禁。

## 七、出图

9 个状态 → `artifacts/ui/`，文件名带尺寸：

```
home  drawer  expanded  settings  help  collapsed  toast  settings-zh  settings-deep
```

`settings-deep` 用来看"先选中第 6 项再切中文"是否还保持选中（历史 bug A）。
常用尺寸：`1280 800`、`1024 640`、`1920 1080`、`2560 1440`。

## 八、版本与依赖

- Avalonia **12.1.6**、net8.0；包版本集中在根目录 `Directory.Packages.props`。
- 12 的两处迁移：`TextBox.Watermark` → `PlaceholderText`；`Bitmap.Save(path,int)` → `Save(path, PngBitmapEncoderOptions.Default)`。

## 九、已知限制

1. **比例锁在 1.6:1**：非 16:10 的窗口会留边（这是"保持示例图比例"的直接结果）。要填满就换 `UniformToFill`（会裁掉边缘）。
2. **文字随窗口缩**：1024×640 时 11.5px 正文变成约 9px。如果觉得小，提高最小窗口尺寸或把画布基准调到 1120×700。
3. **DPI**：缩放让比例与 DPI 无关，但 150%/200% 下的实际字号观感还没实测。
4. 无边框标题栏、抽屉 7 个图标的目标页、设置分区表单、推理记录展开态都还没做——会改设计，等你拍板。
5. 预览器的实际渲染没验证过。

## 十、变更记录

**第 4 轮（2026-11-21）：等比缩放 + 文案资源化**

- `ShellWindow` 引入 `Viewbox`，画布固定 1280×800，整体等比缩放；最小窗口 1024×640。
- 新增 `Resources/Lang.en.axaml`、`Lang.zh.axaml` 与 `Localizer`；`Strings.cs` 删除，XAML 改 `DynamicResource`，VM 改 `Localizer.Get`。
- 回归测试从 12 条扩到 19 条，加入资源一致性、DynamicResource 刷新、四组缩放断言。

**第 3 轮（2026-11-20）：四个 bug + 回归工程**（详见 commit `3144fd5`）
