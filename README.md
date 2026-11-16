# MacPulse

A native macOS menu bar app: system monitor, disk analyzer and cleaner, written in SwiftUI.

原生 macOS 菜单栏工具：系统监控 + 磁盘空间分析 + 安全清理。

![Animated menu bar icons](docs/icons.png)

## 功能 Features

- **菜单栏动画图标**：史莱姆、小幽灵、小鸡、小鲸鱼、火箭、心跳、脉搏线、风扇、均衡器。CPU 越忙动画越快，可选按负载变色。
- **自己勾选显示内容**：CPU、CPU 走势图、内存、磁盘剩余、网速、温度、GPU、电池。
- **三种弹出面板**：紧凑列表 / 大数字卡片 / 大图表。
- **概览**：实时曲线、每个核心的负载（超大核 / 性能核 / 能效核）、24 小时历史、内存构成。
- **磁盘**：用方块图（treemap）逐层查看空间占用，右键在 Finder 中显示或移到废纸篓。
- **项目**：自动识别开发项目，区分源码 / Git 历史 / 依赖和构建产物；`dist`、`build` 只有在 `.gitignore` 里才算可安全删除。
- **进程**：按应用汇总（把同一个应用的多个子进程合并成一行），可以退出或强制退出。
- **清理**：开发缓存（npm / pip / Go / Cargo …）、项目产物、应用缓存、日志、下载里的旧文件、大文件。默认移到废纸篓；只动用户目录，受保护目录和白名单永远跳过。
- **提醒**：磁盘不足、内存压力紧张、CPU 持续高负载、每周可清理空间提示。
- **打字统计**：每天按键数、每小时分布、键盘热力图、最常用的键和应用、每日目标。**只计数，从不记录打了什么**，默认关闭。
- **截图**：自定义快捷键（默认 ⌥⇧A / Alt+Shift+A），自动识别窗口，矩形 / 箭头 / 画笔 / 文字 / 马赛克标注，复制、保存或贴在屏幕上（可缩放、调透明度）。
- **主题色**：跟随系统、黑白、跟随负载，或樱花粉、薄荷绿等。
- **开机自启**，不联网，不收集任何数据。

### Windows 版：Peekaboo

`windows/Peekaboo` 是同一套功能的 Windows 版（C# / WPF，单文件 exe）。托盘里可以放动画图标和多个数值小图标（Windows 托盘不能显示文字），其余功能与 macOS 版一致。构建：

```bash
cd windows/Peekaboo
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out
```

每次推送都会由 GitHub Actions 在 Windows 上编译、跑自检（读数、扫描、一次真实删除、模拟按键统计）、截图每个页面，产物可以在 Actions 里下载。

> 打字统计在 Windows 上使用全局键盘钩子，个别杀毒软件可能会提示风险；它只计数，代码全部开源。

Menu bar icons that animate faster as CPU load rises, user-selectable metrics, three popover layouts, a 24-hour history, a treemap disk explorer, per-project dependency/build-artifact sizes, per-app process grouping, and a cleaner that moves things to the Trash by default and never touches anything outside your home folder. No network access, no telemetry.

## 安装 Install

需要 macOS 14+ 和 Command Line Tools（不需要完整的 Xcode）：

Requires macOS 14+ and the Xcode Command Line Tools (full Xcode is not required):

```bash
git clone https://github.com/zephyrusguooo028/MacPulse.git
cd MacPulse
./scripts/install.sh   # builds, copies to /Applications and launches
```

只想构建不想安装：`./scripts/build.sh`，产物在 `build/MacPulse.app`。

The app is ad-hoc signed. Some folders (Mail, Messages, Trash) need **Full Disk Access** to be measured; Settings has a shortcut to grant it.

## 开发 Development

```bash
swift build
.build/debug/MacPulse --selftest                 # print every reading and scan result, no UI
.build/debug/MacPulse --icon-sheet icons.png     # render all icon frames
.build/debug/MacPulse --snapshot shots/          # open every page and save screenshots
```

Notes:

- On recent SDKs `@State` is a macro whose plugin ships only with full Xcode, so the views use a tiny `@Local` wrapper (`Views/Components.swift`) to build with the Command Line Tools alone.
- The menu bar item is driven through AppKit (`StatusItemController`) with cached bitmap frames; this keeps the animation cheap.
- CPU temperature comes from a private IOHID API resolved at runtime and is best effort.

## 结构 Layout

```
Sources/MacPulse/
  App/        entry point, settings, status item, alerts, diagnostics
  Metrics/    CPU, memory, disk, network, GPU, battery, temperature, processes, history
  Disk/       disk tree scanner, project scanner
  Cleanup/    cleanup candidates and the safe delete/trash executor
  Views/      SwiftUI pages, popovers, menu bar icons
scripts/      build.sh, install.sh, Info.plist, icon generator
```

## License

MIT
