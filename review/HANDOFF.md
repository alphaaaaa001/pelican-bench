# HANDOFF · svgcheck 桌面版

> 给下一个接手的 AI 或我自己。**先读第 0 节再动任何代码。**
> 更新于 2026-10-12，交接时全套验证为绿（见第 1 节命令）。

---

## 0. 十分钟上手：环境坑清单（每一条都真踩过）

这台机（Windows，`D:\java program\`）上会白烧时间的东西，按杀伤力排序：

| 坑 | 现象 | 对策 |
|---|---|---|
| **PATH 上的 `python` 是 3.4.4** | `svgcheck.py` 直接 `SyntaxError`，且**文件内加 `sys.version_info` 检查救不了**（SyntaxError 在编译期，第一行都执行不到） | 用 `bin\svgcheck.cmd` 包装器；或绝对路径 `C:\Users\A\AppData\Local\Programs\Python\Python312-32\python.exe` |
| **`py` 不是 Python Launcher** | `py -0p` 报 `Unknown option: -0`。这台机的 `py.exe` 是个老解释器本身 | 探测解释器别依赖 `py -0p`。真 Launcher 在 `C:\Users\A\AppData\Local\Programs\Python\Launcher\py.exe` |
| **XML 注释里不能有 `--`** | `.csproj` / `.xaml` 里写 `<!-- --check 同构 -->` → `MSB4025` / `MC3000`，报"XML 无效"，看不出是注释的锅 | 注释里不要出现命令行开关。想提 `--check` 就写成"自检开关" |
| **`.cmd` 必须 CRLF** | cmd.exe 按**字节偏移**解析 `goto` 标签，LF-only 会让它落到一行中间去执行（实测跑到 `heck.cmd` 并死循环）。而 `.gitattributes` 有 `* text=auto eol=lf` | 已加 `*.cmd text eol=crlf`。**别再删这条** |
| **中文 Windows + 管道** | `shell_command` 走 cmd 时 `head`/`tail` 不存在，中文输出乱码（`ϵͳָ···`） | 重定向到文件再用 `read_file` 读；或用 `chcp 65001 >nul` 开头 |
| **`Start-Process -ArgumentList` 不加引号** | 含空格路径 `D:\java program\...` 被切成 `D:\java` → `FileNotFoundException` | 自己拼引号：`-ArgumentList "--render `"$file`" `"$out`""` |
| **`pelican-bench.exe` 是 WinExe** | PowerShell 里 `& $exe` **不等待、不接管 stdout**，看起来像挂住直到超时；还会锁住 exe 让下次 build 失败 | 用 `Start-Process -Wait -RedirectStandardError <file>`；build 前确认没有残留进程 |
| **`dotnet run` 的参数穿透** | `dotnet run -v:q --nologo -- --check` 里 `--nologo` 会被转发给你的程序 | 只用 `-v:q --`，`--nologo` 别放 `--` 前面 |
| **冻结的 Brush 不能改属性** | 渲染带 `opacity` 的产出首次载入即 `InvalidOperationException`（围巾 `opacity=".9"` 一发命中） | `ColorUtil` 一律返回**未冻结**新实例 |
| **`PowerShell` 工具整池失败** | 所有 `powershell` 调用返回 exit 1 / 「拒绝访问」，连 `Get-Location` 都不行 | 换 `shell_command`（cmd 通道）。这不是代码问题，别去 debug 代码 |

---

## 1. 验证：改完必须跑这四条

```
chcp 65001 >nul
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set PYTHONIOENCODING=utf-8
cd /d "D:\java program\pelican-bench"

bin\svgcheck.cmd --check                                  # Python 自检，26 条
dotnet run --project tests\SvgCheck.Tests -v:q -- --check  # C# 自检，27 条
dotnet run --project tests\SvgCheck.Tests -v:q -- --parity # ★ 双尺对拍，硬门槛
dotnet build src\SvgCheck.App -v:q --nologo                # GUI 能编
```

**`--parity` 是这个项目的心脏。** 它让 Python 与 C# 对同一批 fixture 逐规则比 `verdict`/`score`/`summary`，有差异 exit 1。判据漂移 = 两把歪尺子，比没尺子更糟，所以这条不许跳过、不许"先注释掉"。

其它有用入口：

```
dotnet run --project tests\SvgCheck.Tests -v:q -- --score <文件>   # 出文本体检单
dotnet run --project tests\SvgCheck.Tests -v:q -- --dump <文件>    # 只打解析出的几何事实，调试用
dotnet run --project tests\SvgCheck.Tests -v:q -- --json <文件>    # 契约 JSON
src\SvgCheck.App\bin\Debug\net8.0-windows\pelican-bench.exe --render <文件> <out.png> 1000 1000
```

`--render` 是**离屏快照**，存在的理由只有一个：让你能真的看见画出了什么。
「进程没崩」从来不等于「渲染是对的」——第 4 节那三个 bug 全是靠它抓到的。改完渲染一定要看图。

---

## 2. 架构：一句话 + 一张图

**判据是数据，不是代码。** 权重、容差、判据文案、角色依赖、"缺席是否可证"全部在 `rubric.json`，两把尺子共读，**任何一方不得硬编码**。

```
rubric.json  ← 唯一判据源（策略：权重/容差/文案/依赖）
   ├─ svgcheck.py        尺子 A：零依赖文本工具，进 CI
   └─ src/SvgCheck.Core  尺子 B：net8.0 判据内核（无 UI 依赖）
          └─ src/SvgCheck.App   WPF 壳：预览 + 打分表 + 角色指派。里面不许有几何计算
tests/SvgCheck.Tests     控制台跑器：自检 27 条 + 回归 + 对拍。零 NuGet
bin/svgcheck.cmd         解释器探测
```

`Core` 与 `App` 分离是为了让判据能被控制台直接驱动，不牵 WPF。

### rubric.json 的四个关键区块

- `_contract` — 铁律与打分数学
- `_report` — **输出 JSON 的形状与对拍口径**。改输出格式先改这里
- `evidence_policy` — 每条规则的「缺席是否可证」，分 `geometric`（可给 fail）与 `hybrid`（只能 unknown）
- `tolerances` — 含一组**几何比例**（`eye_*` / `pouch_*` / `crank_*`），它们决定 verdict，所以也受铁律 1 管

### 打分数学（别"优化"掉）

```
得分   = Σ(wᵢ·sᵢ) / Σ(wᵢ)      只对 sᵢ ≠ unknown 的规则
覆盖率 = Σ(wᵢ of 已判) / Σ(wᵢ of 全部)
```

`unknown` **从分子分母同时剔除**。这同时落实两条工程笔记：检测不到 ≠ 没有（笔记 2）、验不了 ≠ 通过（笔记 4）。把 unknown 当 0 分算会把一张好图从 98 拉到 62，那是诬告的另一种形态——有专门的自检守着这条。

---

## 3. 已完成 / 未完成

**已完成并验证**：rubric 契约、Core（解析 + 几何 + 判据 + 两种报告）、Python 尺子改造（读 rubric、修两个真 bug、加 `--json`）、`bin/svgcheck.cmd`、27 条 C# 自检、26 条 Python 自检、双尺对拍、WPF 界面（预览/打分表/拖放/导出/右键角色指派）、渐变渲染、离屏快照。

**未完成**：

1. **两个 publish profile**。`SvgCheck.App.csproj` 里还没建 `Properties/PublishProfiles/`。要出两个：
   - self-contained：`PublishSingleFile` + `SelfContained` + `EnableCompressionInSingleFile` + `IncludeNativeLibrariesForSelfExtract`，win-x64，预计 60–90MB
   - framework-dependent：`SelfContained=false` + `PublishReadyToRun`，预计 2–5MB
   
   两者各出一 zip 进 `dist/`（记得加 `.gitignore`）。alpha 已确认**两个都出**。
2. **README 更新**。现在它还说"零依赖 Python 3.8+"、只讲 `svgcheck.py`，得加上桌面版、rubric.json、对拍、`bin/svgcheck.cmd`。工程笔记也该补上新增的几条（见第 4 节）。
3. **角色指派只做过编译验证**，没人工点过。GUI 交互（右键菜单、即时重算、高亮）逻辑看着对，但没人真用过。
4. **`gradient_depth` 的合成值是妥协**。契约规定「有渐变 → 100+渐变数，无渐变 → 纯色层数」，两条 criteria 比同一个数、靠 100 这个量级分开。能用但丑，想清楚可以再设计。

---

## 4. 今天抓到的 bug（都是"看一眼图才发现"型的）

留给后人当反面教材，也解释为什么代码里有那些看起来多余的注释。

| bug | 为什么会溜进去 | 怎么抓到的 |
|---|---|---|
| `PathParser` 的 `A` 命令读 4 个数字 | SVG 弧是「3 数 + 2 标志 + 2 数」，多读的那个把 large-arc 标志当数字吞了，后续全部错位 → 圆弧包围盒 y 跑到 -54 | 新增几何回归 |
| 三次贝塞尔导数系数标度不一致 | `a`、`b` 都除了 3，`c` 忘了。混用两套缩放的方程，根直接算飞 → 包围盒只返回端点 | 新增几何回归 |
| 满画布背景 `<rect>` 冒充地面 | rect 的扁长特判排除了它，但它的四条边仍是 `GeoLine`，被通用收集**又捡了一遍**，贡献 y=0 与 y=420，把推定地面错认成画布底边 | `--dump` 对真值 |
| `Children` 从没被填充 | 树只往扁平 `Elements` 里加，于是 `grad.Children` 恒空 → 取不到 stop → **所有渐变静默回落成灰块**，而报告照样说「3 个渐变 ✅」 | `--render` 看图 |
| 眼睛判据把月亮环形山数成 3 只眼 | 只要求「小圆 + 上半区」，没人管它是不是眼睛 | `--render` 看图 |
| `within_canvas` 恒过 | 满画布背景被当主体 → 留白恒 0 且不为负，**真的被裁切的图也拿满分** | 读数时觉得「0px 太巧」 |
| 一边给分一边画不出来 | 判据说「3 个渐变 ✅」，界面把渐变画成灰块。**这是最糟的组合** | `--render` 看图 |

对拍也立功了：C# 初版在 `crank_pedals`/`pouch_present`/`eye_present` 上比 Python 懒（直接依赖角色指派、没试几何法），覆盖率白低 12%，尺子变钝。7 处漂移 → 收敛到 0。

**结论：几何和渲染的正确性没法靠推理保证，必须看图、必须对拍。**

---

## 5. 三条不许违反的纪律

1. **不许为了过测试改测试**。测试失败说明实现错了，或者判据变了——判据变了要改 `rubric.json` 并说明理由。
2. **不许用 fail 表达「我不知道」**。`hybrid` 规则拿不出正面证据必须 `unknown`/`score=null`。这条来自一次真实诬告：第一版对着明明画了巨大下垂喉囊的产出报「喉囊未检测到」，只因作者没写 class。
3. **不许静默降级**。解析失败、特性不支持、引用断链，一律显式记进 `Degraded` / `Unsupported` 并在 UI 出声。「我没搜到」和「它没有」之间隔着一条河。

另有两条设计立场，别顺手"改进"掉：**只量关系不量美学**（审美没有判据）；**不判断"这是哪个模型画的"**。

---

## 6. 文件地图

```
rubric.json                     判据唯一真源。改行为优先改这里
svgcheck.py                     尺子 A（1600+ 行）。measure() 按 rule.id 分派
bin/svgcheck.cmd                解释器探测，必须 CRLF
src/SvgCheck.Core/
  Parsing/PathParser.cs         d 属性 → 绝对坐标段（全命令集 + 相对转绝对）
  Parsing/SvgDocument.cs        建树、表现属性继承、transform 累积、几何
  Parsing/MiniCss.cs            .class / #id / tag 三类选择器
  Parsing/ColorParse.cs         颜色语法（判据与渲染共用单一真源）
  Geometry/Geom.cs              Pt/Box/Mat + 各基元的精确包围盒
  Rules/Facts.cs                从文档抽出可判定的几何事实
  Rules/Rubric.cs               契约加载、criteria 求值、占位符渲染
  Rules/Roles.cs                角色指派（多值：脚两只、踏板两块）
  Rules/Scorecard.cs            ★ 判据实现，最常改的文件
  Report/Reports.cs             文本 + 契约 JSON
src/SvgCheck.App/
  Rendering/SvgView.cs          预览绘制与拾取
  Rendering/Snapshot.cs         离屏 PNG（验收用）
  MainWindow.xaml(.cs)          打分表、拖放、右键角色指派
tests/SvgCheck.Tests/
  SelfCheck.cs                  27 条断言
  Parity.cs                     ★ 双尺对拍
  Program.cs                    --check/--parity/--score/--json/--dump
fixtures/                       对拍样本
prompts/pelican-prompt.md       题面 v1/v2 与读图三条铁律（权重的出处）
D:\java program\鹈鹕测试\       每日鹈鹕，也是最好的真值样本
```

---

## 7. 建议的下一步顺序

1. 建两个 publish profile，`--render` 之外再验一次 GUI 真开起来
2. 人工过一遍角色指派交互（右键指派 → 覆盖率与分数是否即时变化）
3. 更新 README 与工程笔记
4. 可选：`crank_pedals` 的 `0.35 * rmean` 等臂长容差仍硬编码在 C# 里，该收进 rubric
