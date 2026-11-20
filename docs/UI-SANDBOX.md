# UI 沙箱（CogniCustos.Ui）

独立的 UI 工程：**不引用 Core / Adapters**，不连模型、不连数据库，用假数据把界面和交互先做对。
等界面定稿，再由 `CogniCustos.Desktop` 或新的宿主把真实数据接进来。

```
src/CogniCustos.Ui/            ← WinExe，可单独运行（Avalonia 12.1.6 / net8.0）
  App.axaml / Program.cs
  ShellWindow.axaml            ← 窗口 + Viewbox（等比缩放），Esc 在窗口级处理
  Views/ShellView.axaml        ← 1280×720 画布：左栏 / 顶栏 / 三张卡 / 输入 / 右栏 / 抽屉 / 浮层
  Views/SettingsPage.axaml     ← 设置页（盖住整块画布）
  Views/ShellView.axaml.cs     ← 提醒条跑马灯（DispatcherTimer）
  Resources/Lang.en.axaml      ← 英文文案（可翻译资源）
  Resources/Lang.zh.axaml      ← 中文文案
  Localization/Localizer.cs    ← 语言切换：换资源字典 + 给 VM 提供取词
  Styles/Tokens.axaml          ← 颜色、字体、图标字形、手绘几何
  Styles/Controls.axaml        ← 按钮/卡片/导航等样式
  ViewModels/ShellViewModel.cs ← 窗口状态（抽屉、设置、语言、输入、Toast）
  ViewModels/TodayViewModel.cs ← 顶部「今天」提醒条（跑马灯 + 展开列表）
  ViewModels/CardViewModel.cs  ← 单张卡片（种类、标题、条目、折叠）
  ViewModels/SettingsViewModel.cs, SettingsSectionViewModel.cs ← 设置页选中状态
  Mvvm/                        ← 极小的 ObservableObject / RelayCommand

tools/CogniCustos.Ui.Shots/    ← 无头渲染：把每个状态导成 PNG（只看图）
tools/CogniCustos.Ui.Tests/    ← 无头回归：24 条断言，非 0 退出码即失败
```

## 一、布局（按草稿实测比例）

草稿窗口内宽 801px，换算到 1280 宽画布（×1.598）：

| 区域 | 草稿 | 1280 画布 | 说明 |
|---|---|---|---|
| 左栏 | 0–117（14.6%） | **187** | 顶部大 logo；底部「推理记录」白面板，**下沿齐窗口底** |
| 内容区 | 118–745 | 1005 | 顶栏 76 高；卡片从 y=85 开始 |
| 右栏 | 746–801（6.9%） | **88** | 顶部 `En`，图标中心在 y=519 / 593 / 667 |
| 卡片 | 154 宽 / 28 间距 | **246 / 45** | 三张左对齐，起点 x=214 |
| 输入框 | 134×30 | **220×48** | 居中于「左栏右侧的全部区域」，距底 19 |

字号与图标（同一套换算，草图值是实测像素）：

| 元素 | 草稿 | 画布 |
|---|---|---|
| 品牌 chip 文字 | cap≈10 | **20**，chip 181×42 |
| 顶栏 Home | cap≈10 | **17** |
| 卡片标题 | ≈13 | **17**，标题框 104 宽、右对齐 |
| 正文/列表行 | ≈12 | **15.5** |
| 左栏面板文字 | ≈11 | **15** |
| 卡片图标 / 右栏图标 | 20–26 | **30** |
| 抽屉图标 / 标签 | 20 / 8 | 22 / 10 |
| `En` 胶囊 | 33×29 | **50×40** |

**语义（这里曾经读错，已纠正）**：

- **顶部那一行是"今天"**：品牌 chip 之后是一条带闹钟图标的提醒条，文字**循环滚动**（跑马灯），
  点它会向下展开**今天的列表**。输入框回车提交的内容也进这里。
- **第一张卡是压缩日历**，不是今天的内容：日历图标 + 折叠箭头 + 右对齐日期；点开日历模式尚未实现（＋按钮暂时禁用）。
- 第二张卡 `Pending Items`（粉色）在草稿里**没有 ＋**；第三张 `note`（淡黄）有 ＋。
- 卡片标题**右对齐**，折叠箭头 ∧ 在标题左侧；卡片底部左侧的 ∨ 是"打开列表"入口。

## 二、等比缩放（任何分辨率都保持示例图比例）

`ShellWindow` 里是

```xml
<Viewbox Stretch="Uniform" StretchDirection="Both">
  <views:ShellView Width="1280" Height="720" />
</Viewbox>
```

- 1024×576 → 0.80×，1280×720 → 1.00×，1920×1080 → 1.50×，2560×1440 → 2.00×。
- 16:9 显示器铺满；非 16:9（4:3、21:9）**留边**，不裁切不变形（已定方案）。
- 缩放是矢量变换（字不糊），命中测试与焦点跟随缩放。

## 三、文本资源与翻译

文案在 `Resources/Lang.en.axaml` / `Lang.zh.axaml`；XAML 用 `{DynamicResource Some.Key}`，视图模型用 `Localizer.Get("Some.Key")`。

切换：`Localizer.SetLanguage("zh")` 载入对应 axaml，替换 `Application.Resources` 里的语言字典；`DynamicResource` 自动重解析，VM 侧由 `ShellViewModel.ApplyLanguage` 重取。

**新增一种语言**：复制 `Lang.en.axaml` → `Lang.ja.axaml`（只改值）→ `Localizer.KnownLanguages` 加 `"ja"` → 若要 UI 可切，改 `NextLanguage()`。
键集合不一致、空值、缺键都会被测试抓住；运行时缺键显示 `!Some.Key!`。

## 四、运行与调试（VS Code）

| 目的 | 做法 |
|---|---|
| 跑窗口 | `F5` → `UI: run the window (CogniCustos.Ui)` |
| 跑回归 | `F5` → `UI: run regression tests` |
| 出图 | `F5` → `UI: render screenshots headlessly`，或 `dotnet run --project tools/CogniCustos.Ui.Shots -- artifacts/ui 1280 720` |
| 预览 XAML | 打开 `Views/ShellView.axaml`，预览器会实例化它（`Design.IsDesignMode` 分支给设计态 VM）。**没实测过** |

> 构建注意：这台机器上 `dotnet build` 在受限沙箱里会因 SChannel / 命名管道被拒，在 VS Code 里跑不受影响。

## 五、交互（已实现）

- 顶部提醒条：循环滚动；点击展开/收起今天的列表（带 `!` 折叠箭头方向变化）。
- 输入框：Enter 追加到今天的提醒里，并在左栏推理面板显示状态（6 秒后消失）。
- 卡片：∧ 折叠 / ∨ 展开（300 ↔ 78）；`note` 卡的 ＋ 加一条；日历卡的 ＋ 与折叠在（+ 禁用）。
- 抽屉：右栏 ≡；`‹` 向左展开预留区（208→560，带过渡与淡入），`›` 收起。
- 设置页：右栏 ⚙；左栏 16 个分区，右栏显示选中标题；`↩` 或 `Esc` 返回。
- 关闭：`Esc`（窗口级）、点抽屉/浮层以外、顶栏 `Home`、标签 `En/中` 切语言。

## 六、回归测试（tools/CogniCustos.Ui.Tests）

24 条断言，跑在真实可视树上（真按键、真绑定、真缩放），非 0 退出即失败。

```
PASS language_files_have_identical_non_empty_keys
PASS settings_sections_all_have_resources
PASS every_key_resolves_in_both_languages
PASS enter_adds_to_today_and_clears_input      回车进「今天」，不进日历卡
PASS empty_enter_does_nothing
PASS calendar_card_is_not_today                第一张卡是日历、没有条目
PASS pending_card_has_no_add_button            草稿里 pending 卡没有 ＋
PASS today_list_toggles                        提醒条列表开合 + 箭头
PASS card_collapse_and_expand
PASS added_rows_use_current_language_and_are_not_retranslated
PASS language_switch_keeps_settings_selection
PASS language_switch_keeps_user_rows
PASS dynamic_resource_text_updates_on_language_switch
PASS escape_closes_drawer_without_inner_focus
PASS escape_closes_settings_with_inner_focus
PASS drawer_and_settings_are_mutually_exclusive
PASS backdrop_visibility_follows_overlays
PASS drawer_expands_and_animates_width
PASS canvas_scales_uniformly_at_1024x576 / 1280x720 / 1600x900 / 1920x1080 / 2560x1440
PASS canvas_letterboxes_at_4x3
```

## 七、出图与「和草图对齐」检查

10 个状态 → `artifacts/ui/`：`home drawer expanded settings help collapsed toast settings-zh settings-deep today-list`。
`toast` 之后的状态曾被语言污染（跑马灯/中文混排），现在每个状态都先复位到英文再演。

**每轮改完必做**：把 `docs/sketch-reference.png`（你的原始草图，已入库）和渲染结果叠成一张对比图。

```bash
dotnet run --project tools/CogniCustos.Ui.Shots -- artifacts/ui 1280 720 docs/sketch-reference.png 0,0,802,452
# 产物：artifacts/ui/compare.png（上=草图裁切缩放到 1280×720，下=当前渲染）
```

VS Code 里对应 `shots-compare` 任务。第 4、5 个参数是草图的裁切矩形（`x,y,w,h`），
正对该草图的第一个面板；换一张设计稿时改这两个数即可。

## 八、版本与依赖

- Avalonia **12.1.6**、net8.0；版本集中在根目录 `Directory.Packages.props`。
- 12 的两处迁移：`TextBox.Watermark` → `PlaceholderText`；`Bitmap.Save(path,int)` → `Save(path, PngBitmapEncoderOptions.Default)`。

## 九、已知限制

1. **日历模式没做**：日历卡目前只有外观，点开是后续工作；＋ 因此暂时禁用。
2. 卡片底部 ∨（打开列表）与抽屉里 7 个图标**还没有目标页面**。
3. **文字随窗口缩**：1024×576 时 15.5px 正文约 12px。
4. **DPI**：比例与 DPI 无关，但 150%/200% 的实际观感未实测。
5. 预览器渲染未实测。
6. 译文过长会顶出固定宽度（卡片标题 104、抽屉标签 52）。
7. **图标是 MDL2 字形，不是手绘**：草图里的图标更粗、形状也不完全一样（草图的 ≡ 带喇叭，这里用汉堡）。

## 十、变更记录

**第 7 轮（2026-11-21）**：对齐修正——左栏推理面板下沿齐底、输入框下移到底部 19px、右栏图标中心 519/593/667、顶栏 Home 位置；同时把整块排版按草图实测放大（正文 15.5、卡片标题 17、品牌 20、卡片/右栏图标 30、En 50×40）。新增 `docs/sketch-reference.png` 与 `shots-compare`（渲染 + 草图叠图），每轮必跑。

**第 6 轮**：按草稿纠正三处误读——补左栏（logo + 推理记录）、顶部改成"今天"提醒条（跑马灯 + 展开列表）、第一张卡改成压缩日历；卡片几何按实测重排（246/45/起点 214），pending 卡去掉 ＋，输入框收窄，右栏加宽到 88。


**第 5 轮**：画布改 1280×720（16:9）。**第 4 轮**：等比缩放 + 文案资源化。**第 3 轮**：四个 bug + 回归工程。
