# 学程

> 🎓 高考倒计时桌面伴侣 — 倒计时 · 课表 · 考试 · 天气 · 提醒 · 远程管理 · 自动化任务
> 基于 Avalonia + FluentAvalonia 构建，WinUI 3 风格界面 ｜ 当前版本：**v2.7.0**
>
> 📦 框架迁移已完成（WPF → Avalonia）；**旧 WPF 版已确认淘汰（2026-09-05），不再维护**，源码归档于 `LegacyWPF/`（本地保留、不入库）
> 🔐 老师账号密码采用 **PBKDF2 哈希存储**（v2.7.0 起），settings.json 不再保存明文密码

<p align="center">
  <img src="icon.ico" width="96" alt="图标"/>
</p>

---

## ✨ 功能

### 核心倒计时
- ⏳ **实时倒计时** — 天/时/分/秒精确刷新，支持设置小数位数
- 🌐 **中英双语** — 中文 + 英文双行倒计时显示，各行独立开关
- 📊 **高中进度条** — 可视化展示高中生活已过百分比，百分比文字独立开关
- 🎬 **数字脉冲动画** — 每秒轻量缩放脉冲，可开关
- ✨ **入场动画** — 启动/恢复时数字滚动 + 进度条平滑过渡
- 💡 **数字发光** — 数字带发光效果，颜色随设置同步

### 考试与日程
- 📝 **考试全屏倒计时** — 科目名称/剩余时间/进度条/下一场信息，最后 5 分钟蜂鸣提醒
- 🏁 **考后自动恢复** — 最后一场考试结束 3 秒后自动退出，恢复课表栏
- 🔄 **双击/F11 切换** — 考试窗口全屏↔窗口一键切换
- 🚀 **开考自动进入** — 当天有考试时自动进入全屏考试模式（可开关）
- 📅 **课表悬浮栏** — 吸附屏幕顶部，上课自动收缩为进度条、下课自动展开
- ⏰ **下课倒计时** — 栏内置 60 秒倒计时，到设定秒数自动展开 + 提示音
- ⏱ **下课延迟展开** — 下课后等 2 分钟再展开栏，给老师留操作窗口
- 🔄 **调休/调课/代课** — 调休（整天复制）、调课（格子交换/移动）、代课（老师替换）
- 🚫 **按科目隐藏** — 指定科目的课自动隐藏所有窗口，下课恢复
- 🔔 **预备铃适配** — 提前 2 分钟自动进入上课模式（窗口隐藏 + 栏收缩）

### 深度自定义
- 🎨 **字体与字号** — 全部自定义，支持系统已安装字体
- 🎨 **配色方案** — 数字/文字/进度条/考试全屏颜色自由调整，内置颜色选择器
- 🔍 **透明度** — 滑杆调节窗口与课表栏透明度
- 📝 **文本定制** — 中英文标签文字可自由修改

### 屏幕定位
- 📐 **6 种预设** — 顶部 / 中上 / 居中 / 中下 / 底部 / 自定义
- 🖱 **拖动定位** — 自定义模式拖动窗口，坐标实时保存
- 📏 **Y 偏移** — 精细微调垂直位置
- 👻 **点击穿透** — 预设模式下鼠标事件穿透，不阻挡操作
- 🖥 **多显示器** — 课表栏自动吸附所在显示器顶部并铺满

### 实用功能
- ☁ **实时天气** — 课表栏与考试模式内置天气显示（描述/风/湿度/温度，定时刷新）
- 💬 **每日一言** — API 励志语录随机展示，可自定义 API 地址与字段
- 📦 **系统托盘** — 任务栏托盘常驻，右键菜单快捷操作

### 🧩 自动化任务（v2.7.0 新增）
- 🧩 **拼图式规则** — 像拼积木一样把「① 触发条件」与「② 要做的动作」两块拼起来，无需写配置
- ⏰ **触发拼块** — 固定时间（每天/指定星期）· 上课前 N 分钟 · 下课时 · 放学时（当天末节下课）· 闲置 N 分钟 · 软件启动后
- ⚙️ **动作拼块** — 打开文件 · 打开科目课件（自动取课件目录最新文件）· 播放音频 · 熄屏 · 关机/重启（倒计时可取消）· 弹出提醒
- 🛡 **防误触** — 考试模式/上课时段不触发闲置熄屏；自习班会课前不自动开课件；每条规则独立启停 + 全局总开关
- 💾 **独立配置** — 规则存 automations.json（随软件文件夹分发，「恢复默认设置」不会误删）

### 🌐 远程管理（v2.5.0 起持续迭代）
- 🖥 **局域网 HTTP 服务** — 启动软件自动开启（可关），老师浏览器访问 `http://本机IP:8080`
- 👥 **多老师账号** — 语数英物化生 6 位老师 + 管理员，登录页下拉选账号
- 📚 **课表远程管理** — 周课表可视化编辑（选科限定，模态框选课），双击改、一键保存
- 📎 **课件投递** — 拖拽/点击上传（500MB，白名单后缀），班级电脑自动打开，可设桌面/课件等预设目录
- 📊 **班级状态** — IP / 磁盘剩余 / 最近上传 / 运行指示灯（15s 自动刷新）
- 📋 **操作日志** — 登录/改课表/上传全记录（按老师显示名），管理员可查
- 🏫 **班级信息** — 班级名 / 老师名网页端与设置页均可改
- 🔐 **安全** — Token 持久化登录、**登录限速**（同 IP 错 5 次锁 2 分钟）、**PBKDF2 密码哈希**（v2.7.0，旧明文登录后自动迁移）、账号名不外泄、IP 白名单可选开关
- ⌨️ **全局快捷键** — Ctrl+Shift+H 显隐主窗 / Ctrl+Shift+B 课表栏 / Ctrl+Shift+E 考试模式
- 🎯 **始终置顶** — 可选始终置顶或正常窗口层级
- 🚀 **自动更新** — GitHub Release 检查，自包含/框架依赖版自动匹配下载
- 🔒 **单实例** — 重复启动时激活已有窗口（按进程名校验，不误激活同名窗口）
- 💾 **配置持久化** — JSON 文件存储（settings.json / schedule.json / automations.json），重启不丢失；支持一键备份/恢复

---

## 🛠 技术栈

| 技术 | 说明 |
|------|------|
| .NET 10 | 目标框架 |
| Avalonia 12 | 跨平台桌面 UI 框架 |
| FluentAvaloniaUI 3 | WinUI 3 风格主题（Mica/圆角/Fluent 控件） |
| C# | 开发语言 |
| AXAML | 界面标记语言 |
| Avalonia.Controls.DataGrid | 课表/考试表格编辑 |
| Avalonia.Controls.ColorPicker | 颜色选择 |
| Avalonia.Fonts.Inter | 默认字体 |
| 高德天气 API | 实时天气数据 |

> 注：点击穿透 / 托盘 / 自启动注册表 / 全局快捷键等使用 Win32 P/Invoke，当前仅 Windows 生效（Avalonia 框架本身跨平台，非 Windows 平台对应功能自动降级为 no-op）。

---

## 🚀 快速开始

### 环境要求

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### 运行

```bash
git clone https://github.com/XEKernel/StudyJourney.git
cd StudyJourney/StudyJourney.Avalonia
dotnet run
```

### 编译发布

自包含（无需安装 .NET，体积较大）：

```bash
dotnet publish StudyJourney.Avalonia/StudyJourney.Avalonia.csproj -c Release -r win-x64 --self-contained true
```

框架依赖（需安装 .NET 10 桌面运行时，体积较小）：

```bash
dotnet publish StudyJourney.Avalonia/StudyJourney.Avalonia.csproj -c Release -r win-x64 --self-contained false
```

发布输出位于 `StudyJourney.Avalonia/bin/Release/net10.0/win-x64/publish/`。更新程序 `Updater/` 单独编译，随发布一起分发。

---

## 📁 项目结构

```
StudyJourney.Avalonia/
├── App.axaml / App.axaml.cs        # 应用入口（托盘/快捷键/自动更新/考试模式统一入口）
├── Program.cs                      # Main 入口（单实例 Mutex）
├── StudyJourney.Avalonia.csproj    # 项目文件
├── Assets/                         # 图标（icon.ico / icon.png）
│
├── Models/                         # 数据模型（纯数据，无 UI 依赖）
│   ├── AppSettings.cs              # 应用设置（JSON 持久化；含 TeacherAccount 与选科）
│   ├── ScheduleEntry.cs            # 课表/考试条目/时段模板/课程表网格行
│   ├── ScheduleManager.cs          # 课表与考试数据管理（加载/保存/查询/导入）
│   └── AutomationRule.cs           # 自动化规则（触发/动作拼块枚举 + automations.json 存储）
│
├── Services/                       # 服务层（业务逻辑）
│   ├── ReminderService.cs          # 提醒调度服务（上课/下课/考试/60 秒倒计时）
│   ├── AutomationService.cs        # 自动化任务调度（触发判定/动作执行/闲置与熄屏 P/Invoke）
│   ├── HttpServerService.cs        # 远程管理 Minimal API（内嵌教师端 Web 控制台）
│   ├── WeatherService.cs           # 天气服务（HTTP + JSON 解析）
│   └── UpdateService.cs            # GitHub Release 更新检查/下载
│
├── Helpers/                        # 公共工具
│   ├── AppLogger.cs                # 轻量日志（Debug + 文件，超限轮转）
│   ├── FileAtomic.cs               # 原子写文件（settings/schedule/tokens/automations）
│   ├── PasswordHasher.cs           # 密码哈希（PBKDF2-SHA256，登录凭据存储）
│   ├── ColorUtils.cs               # 颜色解析/天气表情符号
│   ├── GlobalHotKeyManager.cs      # 全局快捷键（Win32 RegisterHotKey）
│   └── TimeSimulator.cs            # 时间模拟（调试用）
│
├── Views/                          # 窗口与页面（UI 层）
│   ├── MainWindow.axaml(.cs)       # 主窗口倒计时（动画/穿透/隐藏逻辑）
│   ├── ScheduleBarWindow.axaml(.cs)# 课表悬浮栏（紧凑/展开/多显示器/天气）
│   ├── ExamModeWindow.axaml(.cs)   # 考试全屏倒计时
│   ├── SettingsWindow.axaml(.cs)   # 设置窗口（FAAppWindow + NavigationView 8 页）
│   ├── ScheduleEditorWindow.axaml(.cs) # 课表/考试编辑（DataGrid + 周视图调课）
│   ├── ColorPickerDialog.axaml(.cs)# 颜色选择对话框
│   ├── DebugTimeWindow.axaml(.cs)  # 时间模拟调试窗口
│   └── Settings/                   # 8 个设置页
│       ├── CountdownPage.axaml(.cs)  # 倒计时
│       ├── PositionPage.axaml(.cs)   # 位置
│       ├── ApiPage.axaml(.cs)        # API（一言/天气）
│       ├── SchedulePage.axaml(.cs)   # 课表/提醒
│       ├── ExamPage.axaml(.cs)       # 考试
│       ├── ServerPage.axaml(.cs)     # 服务器（远程管理/账号/科目/日志）
│       ├── AutomationPage.axaml(.cs) # 自动化（拼图式规则）
│       ├── AboutPage.axaml(.cs)      # 关于（版本/更新）
│       └── ISettingsPage.cs          # 设置页接口（Load/Apply/IsDirty）
│
└── Updater/                        # 独立更新程序（StudyJourney.Updater）
```

---

## 📝 设置页说明

| 页面 | 功能 |
|-----|------|
| ⏳ 倒计时 | 字体、字号、颜色、透明度、可见内容、时间精度/小数位、动画、中英文文本、目标/起算日期、自定义倒计时 |
| 📐 位置 | 6 种预设 + 自定义坐标 + Y 偏移 + 始终置顶 + 上课隐藏 + 自启动 |
| 🔌 API | 每日一言 API + 实时天气（城市/颜色/刷新间隔） |
| 📋 课表 | 课表栏开关、提醒开关、提示音、自动收缩 |
| 📝 考试 | 考试模式开关、自动进入、字号、颜色、字体 |
| 🌐 服务器 | 远程服务开关/自启、课件存放位置（预设+自定义）、班级信息、老师账号（密码加密存储）、可选科目、操作日志 |
| 🧩 自动化 | 总开关、规则列表（增删/启停）、拼图式编辑器（触发块 + 动作块 + 实时预览） |
| ℹ 关于 | 版本信息、检查更新、GitHub 仓库 |

---

## 📅 课表与考试

1. **课表编辑**：设置 → 📋 课表 →「编辑课表」→ 周视图网格填课程名，或 DataGrid 平铺编辑
2. **时段模板**：定义每天节次时间，一键应用到全部星期
3. **调休**：选来源星期 → 目标星期 → 应用，一键复制课表
4. **调课**：点击课程表格子选源 → 再点击选目标 → 交换/移动/代课
5. **课表栏**：托盘菜单 → 课表栏，悬浮屏幕顶部；上课自动收缩，距下课 1 分钟显示倒计时并展开
6. **考试模式**：设置 → 📝 考试 → 添加考试 + 科目 → 托盘菜单 → 进入考试模式
7. **导入导出**：课表/考试 JSON 一键导入导出，支持数据备份/恢复

---

## 📄 更新日志

详见 GitHub Releases: https://github.com/XEKernel/StudyJourney/releases

---

## 📄 许可证

MIT License

Copyright (c) 2025 XEKernel
