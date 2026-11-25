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

## 一之二、顶部提醒行与它的抽屉

- 一行"当日 + 明日"的提醒，**连续滚动**播放；文字短于可视宽度时不滚、也不重复。
- 右侧箭头是展开开关：收起朝下，展开朝上；点它开合，点**抽屉以外任何地方**也收起。
  实现上，抽屉和这条提醒条都画在"点击遮罩"之上（`ZIndex` 5 遮罩 / 6 两者），
  所以点提醒条是开合、点别处是关闭，两种点击不会互相抢。
- 抽屉宽度 507、水平居中于提醒条；高度**跟内容行数走**，封顶 274（画布单位，
  随 Viewbox 一起缩放）；超过封顶就滚动。
- 列表是**虚拟化**的（`VirtualizingStackPanel`），只创建可见行，几千条也不卡。
- 背景透明度是一个资源开关：`Tokens.axaml` 里的 `ReminderDrawerBackground`（现在是 `#F2FFFFFF`，
  把 alpha 改大就是更不透明、改小就更透）。你说以后可配置，这里先留了这一个旋钮。
- 行内跳转/点击行为暂未实现（按"后续再补"处理）。

## 一之三、右侧抽屉：一层一层加

抽屉不是"一个可以变宽的抽屉"，而是**可堆叠的层**，每一层都是独立组件：

| 层 | 宽度 | 背景 | 内容 | 打开方式 |
|---|---|---|---|---|
| 第 1 层 | 390 | **与右栏同色**（`RailBg`），不透明 | 图标网格（7 个瓷砖）+ 底部 `‹` 与日历 | 右栏 ≡ |
| 第 2 层 | 290 | 白色（`Surface`） | 预留的"未来功能"区，大字号文案 | 第 1 层底部的 `‹` |

（这里的教训：第 1 层原来用了另一个偏白的 `DrawerBg`，看起来像浮在页面上的半透明板；
稿子里它和右栏是同一块颜色、同样的清晰度，所以现在直接用 `RailBg`。）

- 第 2 层开在第 1 层的**左边**，第 1 层原地不动；`‹` 变 `›`，再点只关第 2 层。
- 右栏 ≡ 是**开/关切换**：点一下打开，再点一下关闭。右栏 `ZIndex=15`（高于抽屉的 9），
  否则抽屉滑入的 240ms 内会从右栏上方经过、把紧接着的第二次点击吃掉——
  这就是"再点一下不关"的真实原因，测试里连点两下即可复现/防回归。
- 抽屉从窗口顶端到底端，**完整盖住下面的所有东西**（包括顶栏）；**抽屉本身不糊**，
  糊的是**抽屉左边的页面**。
- 因为这个"盖住"，抽屉已经从内容列挪到根 Grid（内容列与右栏之间）：
  否则内容列上的模糊效果会作为独立图层盖在抽屉上面（第一版就是因此透出页面内容）。
- 模糊在抽屉左边的页面上：`ShellViewModel.BlurWhenDrawerOpen = 6`（8 太糊、2 太轻）；
  压暗是 `HomeDimOpacity`（5% / 第二层 10%）。两个值将来都进"界面配置"。
- 点抽屉左侧的主页区域 → 逐层关闭（先关第 2 层，再点关第 1 层）；Esc 一次关掉全部。
- 模糊半径与变暗程度是 VM 里的两个值（`HomeBlurRadius` / `HomeDimOpacity`），
  以后进"界面配置"做成可调。
- 以后加第 3 层：VM 里再开一个 `XxxOpen` + 一个 Border，宽度由自己决定；
  不要改已有层的宽度。

## 一之四、界面配置（设置页第 2 项）

设置页新增 `Section.Appearance`。选中它时，右侧内容区不是"尚未实现"，而是一排按钮：
主页 / 提醒抽屉 / 提醒抽屉（长列表）/ 图标抽屉 / 未来区抽屉 / 语言菜单 / 帮助面板 / 设置页。
点一下就**关掉设置页并打开对应界面**，用来随时查看做过的部件。

## 一之五、日历窗口

点第一张卡片右上角的日期（`26/11/22`）会打开一个**应用内浮动窗口**（不是系统窗口）：

- 标题栏：`Calendar` + `Year: 2026` / `Month: 11` 两个上下调节器 + 最小化 + 关闭；
- 表头 7 列（周一开始），下面 6 行日期格；非本月日期淡显，今天有圆点底；
- 表格下方是预留区（浅绿），示例文字后续补；
- 标题栏可拖动，窗口被限制在 1280×720 画布内；
- 最小化：收起到标题栏（46 高），再点还原；关闭：隐藏；Esc 先关日历，再关其它浮层；
- 窗口用 `Canvas` 定位，`ZIndex=30`，在设置页之上。

文案全部资源化（`Calendar.*`、`Weekday.*`），切语言时表头和标题一起变。

**注意**：离屏渲染（`Ui.Shots`）里，左侧栏的文字会错误地画在日历窗口之上（真实窗口没有这个问题）。
这是无头渲染的图层顺序差异，和之前模糊图层那个坑同类；判断这类问题以真窗口为准。

## 一之六、提醒条的双色、卡片的数据格式、三个弹窗

**提醒条**：每条提醒带 `Urgent` 标记。普通项用正文色，**当日紧急**项用暗红 `#8E1B1B`
（实测像素 `(142,27,27)`）。滚动行由多条文字片段组成，不是一整段字符，因此可以逐条上色。

**卡片数据结构**（`CardItem`）：`When`（如 `11/27 3pm – 5pm`）→ `Title` →（仅待处理卡）
`Source` / `SourceFile`。展示顺序固定为时间、事项、来源。

**三个弹窗**（同一个 `DialogViewModel` 表面，一次只开一个，Esc 先关它）：

| 触发 | 弹窗 | 出口 |
|---|---|---|
| 点第二张卡的条目 | 待处理：确认 / 无视 / 更多选项 | 确认 → 进第一张卡 + 提醒条；无视 → 二次确认；更多选项 → 输入框 |
| 上一步选"无视" | 二次确认 | 确认后从卡片移除 |
| 上一步选"更多选项" | 输入框（底部输入框的副本，含麦克风） | 发送后把输入内容作为事项写进第一张卡 + 提醒条 |
| 点第三张卡的笔记 | 编辑 / 让 AI 处理 | 编辑 → 多行编辑框；AI → 输入框（预填原文） |

AI 改写修辞属于项目里的 AI 设计，UI 这轮用"输入什么就写什么"占位。

**卡片体可滚动**：条目多了以后卡片正文自己滚，不会顶出卡片。

## 一之七、两个独立的推理面板

**不是上下两级**，是两个入口、两块面板，互斥（打开一个就关另一个），都不虚化页面：

| 入口 | 面板 | 内容 |
|---|---|---|
| 左栏 `Reasoning Record` 那一行 | 记录列表（617×600，左上 211,104） | 按天分组、可折叠；组内 `08:01:29-08:05:17` + 一句话 |
| 白面板底部 `›`（打开后变 `‹`） | Thought Trajectory（823×468，左上 211,242） | 当前思考流水：等宽字体、`Run command` / `think:` / `Change:` / `read` / `edit` |

- 列表顺序：最旧在上，`YESTERDAY` → `TODAY` 在底部，`TODAY` 默认展开；日期组用 `yy/MM/dd`。
- 关闭：两个面板右上角**细圆圈 ✕**；`Esc` 先关面板；点面板外部也关（一层透明遮罩，不压暗）。
- 数据模型：`ReasoningGroup{Header, IsExpanded, Chevron, Records}` + `ReasoningRecord{Time, Title, Trajectory}`；
  现在由 `ReasoningViewModel` 给演示内容（`Demo.Record1..3`、`Demo.Trajectory`），后续换成真实记录即可。
- 已知与稿子的差异（细节待调）：稿子里组标题的箭头是固定列（面板宽度约 31% 处），现在是右对齐；
  稿子里轨迹有横向滚动条，现在只做纵向滚动（文字自动换行）。

## 一之八、设置页的 Model Configuration（第一个真表单）

设置页原有骨架不动：**⚙ + Settings Page + 语言徽章 / 左列 280 分类 / 薄荷绿内容卡（14 圆角 + 26,22 内边距）/ 底部帮助与返回**。
新内容只出现在内容卡的 `Section.Model` 分支：

- **三个字段行**：`Model name`、`URL(OpenAI format)`、`API`。只读展示 + 右侧铅笔；**双击行进入编辑**
  （铅笔单击也可以），`Enter` 提交、`Esc` 取消、失焦也提交；`API` 永远显示固定长度圆点。
- **说明文字**：字段组下方一行灰字（参考官方文档）。
- **Advanced settings**：标签 + `˅`，右侧长说明；点击弹出占位窗（标题 + "尚未实现"）。
- **Small local model**：模型名 + `Check ˅` + 红色 `Delete`，右侧说明；`Delete` 走二次确认后把名字换成"未安装"。
- **Ollama 折叠项** + 说明；底部通栏 `＋ 添加自定义模型与服务`（点开占位窗）。
- 没有撤销按钮（按定稿：撤销是后续的事）。
- 占位文字只在"既没有表单也没有预览组"的分类出现，避免压到表单上。

控件样式：`Button.fieldPencil` / `Button.chip` / `Button.danger` / `Button.wideAction`；
文字全部走 `Settings.Model.*` 与 `Dialog.Ok` / `Dialog.Delete` 资源键，中英切换即时生效。

数值目前只在内存里（`ModelField.Value`），下一步接 API 时换到 `state/settings.json` 的模型。

## 一之九、View-Model 的职责划分（第 18 轮整理）

| 文件 | 只负责 | 不再是 |
|---|---|---|
| `ShellViewModel` | 窗口状态：抽屉/页面/面板开关、提醒条、看板、对话框编排、语言切换 | 不再有截图用的状态机、示例种子、死成员 |
| `ShellScenarios` | 截图与"界面配置"预览用的命名状态（`expanded`、`reasoning`、`settings-model`、`dialog-incoming`…） | — |
| `IShellDataSource` / `DemoShellDataSource` / 小样的 `RuntimeShellDataSource` | 内容与动作的接缝 | — |
| `DialogViewModel` | 一个对话框表面；关闭时清掉临时态（composer/editor 开关与文本） | — |
| `ModelField` / `ModelConfigurationViewModel` | 设置页 Model Configuration 的字段与动作事件 | — |

同时修掉的具体问题：

1. 对话框来源用 `enum DialogOrigin { None, Pending, Note }`，替掉原来的 `bool _dialogFromPending`；
2. 关闭只有一条路径 `FinishDialog(removeSource)`，不再有 `RemoveSource + CloseDialog` 两次关闭；
3. 删掉无引用的 `LanguageCode`、`CardItem.CanClick`、`CardViewModel.RowsClickable`、`ReasoningViewModel.ShowTrajectoryCommand`，
   `CardViewModel.ItemCommand` / `ModelField.IsSecret` / `ModelConfigurationViewModel.AdvancedOpen` 改为私有；
4. `AskAndShowAsync` 的返回值统一回 UI 线程再改看板（服务在后台线程答复时不再碰 UI 集合）；
5. `DialogViewModel.RefreshText` 也会刷新 composer 的占位文字（切语言不再留旧提示）。

## 一之十、卡片里的两个箭头 = 翻页滚动（不是折叠）
- 卡片高度固定 300（原来折叠到 78 的那套状态整块删掉：`IsCollapsed` / `CardHeight` / `CollapseGlyph` 都不存在了）；
- 卡头右上箭头 = **往上翻一页**，卡底中间箭头 = **往下翻一页**，步长是视口高度的 85%（最少 48px），
  滚到顶/底就停住；
- 箭头是否可用跟着滚动位置走：不可用时 `IsEnabled=false`、透明度 0.25，可用时满亮；
- 判定条件是 `Extent.Height - Viewport.Height`，所以**加了新行之后**箭头也会自己变亮（挂 `LayoutUpdated`，
  并且只在状态真的变化时才写属性，避免每帧重绘）；
- 卡片正文仍是 `ScrollViewer`（滚动条按需出现），箭头只是它的两个"翻页按钮"。

对应测试：`card_chevrons_page_the_body_instead_of_collapsing`——先断言 `CardViewModel` 上已经没有 `IsCollapsed`，
再往第三张卡塞 14 行让它溢出，然后点卡底箭头确认 `Offset.Y > 0`、卡头箭头变可用、点回去能回滚。

## 一之十一、点读页（ReaderPage，2026-11-25）

设置页之后第二个**整块盖住画布**的页面（`ZIndex=21`，绑 `Reader.IsOpen`），入口是右侧抽屉第 4 行
「点读文件」（`ReadFileTile`）。三列布局：

| 列 | 宽 | 内容 |
|---|---|---|
| 左 | 262 | 概要（可滚，`MaxHeight=196`）+ 页码目录（`ReaderSectionRow`，点一行跳该页） |
| 中 | * | 页面 + 下方「询问」条：框选说明行、`询问 AI 教导` 按钮、输入框、`取消框选` |
| 右 | 344 | 讲评记录（每条带 `#页码`）与红色状态行 |

**选区怎么算**（这是最容易做错的一处）：

- 页面用 `Stretch="Uniform"` 画，位图在宿主里是**letterboxed** 的；指针坐标要先减掉左右/上下留白，
  再除以实际绘制尺寸，得到 **0..1 的归一化坐标（左上原点）**——见 `ReaderPage.ToNormalised`；
- 反向换算 `ToViewPoint` 用于"由状态画选区"（fixture、测试、翻页后重画）；
- 归一化坐标一路传到 `IReaderService`，再由 `DocumentBook` 换算成 PDF 坐标（左下原点）去问 PdfPig；
- 选区在 `LayoutUpdated` 里重画，但**只在数值真的变了才写** `Width/Height`，否则每帧都会触发布局。

**接缝**：`IReaderService`（打开/渲染/整页文字/框选文字/通读/提问/关闭）是 UI 认识的全部。
演示实现 `DemoReaderService` 自己画 8 页（不需要 PDF 引擎），小样的 `RuntimeReaderService` 接
`Documents` + 模型。文件选择在视图层（`DocumentPicker`），因为 StorageProvider 要 `TopLevel`。

截图：`docs/ui-reader.png`（框选 + 讲评）、`docs/ui-reader-empty.png`（刚打开）。
对应测试：`reader_opens_and_fills_summary_and_page_index`、`reader_selection_is_ordered_and_cleared_by_a_page_turn`、
`reader_ask_logs_an_exchange_and_clears_the_input`、`reader_close_resets_and_releases_the_document`、
`reader_open_failure_keeps_the_window_closed`、`drawer_has_one_extra_row_for_the_reader`。

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

## 三、多语言：一套窗口，运行时切换

文案在 `Resources/Lang.en.axaml` / `Lang.zh.axaml`；XAML 用 `{DynamicResource Some.Key}`，视图模型用 `Localizer.Get("Some.Key")`。
**每个窗口只有一份 XAML**，没有"中文窗口/英文窗口"之分。

**右上角语言控件**（`Views/LanguagePicker.axaml`，主窗口与设置页共用同一个控件）：

- 按钮显示 **当前语言**（英文界面显示 `EN`，中文界面显示 `中`），不会出现"按钮中文、界面英文"；
- 点开是一个下拉菜单，列出所有语言并勾选当前项；菜单项由 `Localizer.Languages` 生成，**加语言不用改 XAML**；
- 选中即切换，整个界面（含窗口标题）同时更新。

**切换是怎么传播的**：

| 文本位置 | 机制 |
|---|---|
| XAML 里的文案 | `Localizer.SetLanguage` 替换 `Application.Resources` 的语言字典 → `DynamicResource` 自动重解析 |
| 视图模型里的文案（卡片标题、设置分区、日期） | 窗口基类 `LocalizedWindow` 订阅 `Localizer.LanguageChanged`，调用 `ILocalizedViewModel.RefreshText()` |
| 操作系统窗口标题 | 同一个 `LocalizedWindow` 里 `Title = Localizer.WindowTitle` |

`LocalizedWindow` 在 `OnOpened` 订阅、`OnClosed` 退订——静态事件不会留住已关闭的窗口。

**新加一个窗口的标准动作**（只做这三件）：
1. 窗口继承 `LocalizedWindow`（自动获得标题与语言刷新）；
2. ViewModel 实现 `ILocalizedViewModel.RefreshText()`（把持有的文案重新读一遍，或直接调用 `OnAllPropertiesChanged()`）；
3. 文案加进两份 `Lang.*.axaml`，XAML 用 `DynamicResource`。

**新增一种语言**：复制 `Lang.en.axaml` → `Lang.ja.axaml`（只改值）→ `Localizer.KnownLanguages` 加 `"ja"`。
菜单会自动多出一项。

**缺键策略**：当前语言缺这个键 → 回退英文（`!key!` 只在两种语言都没有时出现）。
测试会拦住半成品翻译：两份目录键集合必须一致、每个键在两种语言下都能取到、XAML 里 `{DynamicResource X}` 引用的每个 X 必须存在。

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

32 条断言，跑在真实可视树上（真按键、真绑定、真缩放），非 0 退出即失败。

```
PASS language_files_have_identical_non_empty_keys
PASS language_picker_shows_current_language        按钮显示"当前语言"
PASS language_menu_lists_both_languages_and_switches  菜单生成 + 选中即切换
PASS window_title_follows_language                 窗口标题跟随语言
PASS xaml_binds_only_existing_resource_keys        XAML 引用的键必须存在（且两种语言都有）
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

## 六之二、在真窗口上取证的规矩（DPI）

这台机器主屏 2560×1440，缩放 **125%**。窗口是 `1280×720 DIP` = **1600×900 物理像素**。
应用自己报的数（`COGNI_DIAG=1` 时的输出）：

```
window client  = 1280 x 720 (DIP)
render scaling = 1.25
screen scaling = 1.25
content bounds = 0, 0, 1280, 720      → Viewbox 里画布仍是 1280×720
```

**坑**：在没有 DPI 清单的进程里（比如普通 PowerShell）读 `GetClientRect`，Windows 会
把尺寸按 1.25 虚拟化成 1280×720；照这个尺寸去截图，就只能截到窗口左上 80%，
右边 20%（抽屉、右栏）被裁掉——看起来就像"抽屉盖住了右栏"。

**规矩**：

1. 截真窗口前先声明 DPI 感知（`SetProcessDpiAwareness(2)`），再读 `GetClientRect`；
   得到的是 1600×900 物理像素。
2. 画布坐标 → 物理坐标：`physical = clientOrigin + canvas × RenderScaling`。
3. 离屏截图（`tools/CogniCustos.Ui.Shots`）固定输 1280×720 画布，与 DIP 尺寸一致，
   所以它和真窗口的**布局**一致；只有"真窗口截屏"这条路径需要处理 DPI。

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

**第 19 轮（2026-11-24）**：卡片箭头改成虚拟滚动（翻页）——卡头向上翻页、卡底向下翻页，
到顶到底自动失效；删除整块折叠状态与 `collapsed` 截图状态，新增 `cards-full` 溢出状态。
断言替换 1 条（共 57 条）。

**第 18 轮（2026-11-24）**：代码整理（不属于新功能）——把截图状态机搬出 `ShellViewModel` 到
`ShellScenarios`，对话框来源改成枚举 + 单一关闭路径，清掉死成员，异步答复回 UI 线程。
新增 2 条断言（共 57 条）。

**第 17 轮（2026-11-24）**：设置页 Model Configuration 真表单（三字段双击编辑、API 掩码、
高级设置/Check/添加的占位窗、删除本地模型二次确认、Ollama 段与通栏按钮），骨架与左列未改动。
新增 6 条断言（共 55 条），两张截图（en/zh）。

**第 16 轮（2026-11-24）**：两个推理面板——记录列表（按天分组、折叠、`YESTERDAY`/`TODAY`）与
思考轨迹（等宽、标题带记录名）；左栏行与底部 `›` 是两个独立入口，互斥；`Esc`/✕/点外部关闭。
新增 5 条断言（共 49 条）。

**第 15 轮（2026-11-22）**：按示例文本落地——提醒条双色（紧急暗红 `/已实测`）、卡片条目改成
"时间 → 事项 →（来源/文件）"格式、第一张卡接收确认后的计划、第二张卡点开三选一弹窗、
无视二次确认、第三张卡编辑/让 AI 处理、共用输入弹窗。新增 9 条断言。

**第 14 轮（2026-11-22）**：新增日历窗口——卡片日期打开应用内浮动窗口（可拖动/最小化/关闭），
7×6 日期网格、年月调节、今天标记、预留区；新增 5 条断言（打开/最小化/关闭、年月导航、拖动与边界）。

**第 13 轮（2026-11-21）**：定位"截图与实际不符"——不是界面问题，是取证问题：
窗口在 125% 缩放下是 1600×900 物理像素，而我按 DPI 虚拟化后的 1280×720 截图，
右栏和抽屉右侧被裁掉。改正为 DPI 感知取窗口物理区域后，真窗口与离屏渲染一致。
文档补了取图规矩（见"六之二"）。

**第 12 轮（2026-11-21）**：按稿子修正抽屉的观感——第 1 层改用**右栏同色**且不透明、
完整盖住顶栏与页面（抽屉挪到根 Grid，避免内容列的模糊图层盖在上面）；模糊只作用于**抽屉左边**的页面（半径 6），
压暗 5%；右栏提到 `ZIndex=15` 修掉"连点两下 ≡ 只开不关"（滑入动画期间点击被抽屉吃掉）。
断言仍 32 条（toggle 用例改成连点两次）。按"太模糊"的反馈，抽屉打开时的页面压暗降到 5%（第二层 10%），
模糊默认关闭并留成 `BlurWhenDrawerOpen` 常量；补一条"≡ 点一下开、再点一下关"的真实点击测试。

**第 10 轮（2026-11-21）**：抽屉改成分层模型——第 1 层 390（图标网格）、第 2 层 290（未来区）开在它的左边，
第一层不动；抽屉盖住顶栏、主页模糊变暗（两个可调值）；点主页逐层关闭。
设置页新增"界面配置"，把做过的界面部件做成按钮。断言 30 → 31。
顺带修了出图工具：截图前等动画结束，之前的图都是转场中途的半透明状态。

**第 9 轮（2026-11-21）**：顶部提醒行按新稿实现——箭头展开/收起（展开朝上）、抽屉高度跟随内容并封顶 274、
虚拟化滚动、点抽屉以外收起（遮罩覆盖整个内容区，提醒条画在遮罩之上）、背景透明度抽成
`ReminderDrawerBackground` 资源。顺带修掉音量按钮误绑语言切换的接线。断言 28 → 30。

**第 8 轮（2026-11-21）**：多语言收口——语言按钮改为"显示当前语言 + 下拉菜单"（菜单由 `Localizer.Languages` 生成，勾选当前项）；新增 `LocalizedWindow` + `ILocalizedViewModel`，切语言时所有窗口的 VM 文本与窗口标题同步；缺键回退英文；新增 XAML 键扫描测试。断言 24 → 28。

**第 7 轮（2026-11-21）**：对齐修正——左栏推理面板下沿齐底、输入框下移到底部 19px、右栏图标中心 519/593/667、顶栏 Home 位置；同时把整块排版按草图实测放大（正文 15.5、卡片标题 17、品牌 20、卡片/右栏图标 30、En 50×40）。新增 `docs/sketch-reference.png` 与 `shots-compare`（渲染 + 草图叠图），每轮必跑。

**第 6 轮**：按草稿纠正三处误读——补左栏（logo + 推理记录）、顶部改成"今天"提醒条（跑马灯 + 展开列表）、第一张卡改成压缩日历；卡片几何按实测重排（246/45/起点 214），pending 卡去掉 ＋，输入框收窄，右栏加宽到 88。


**第 5 轮**：画布改 1280×720（16:9）。**第 4 轮**：等比缩放 + 文案资源化。**第 3 轮**：四个 bug + 回归工程。
