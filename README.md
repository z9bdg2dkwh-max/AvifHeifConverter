# AvifHeifConverter

将 AVIF 图片转换为 HEIF/HEIC 格式，保留 HDR Gain Map 与元数据；GUI 版面向带 HDR Gain Map 的 AVIF，转成 iPhone 可正常显示 HDR 的 HEIC。

> 首次公开：本仓库仅含 C# 源码，不含编译产物、运行时 DLL 与转换引擎二进制。

## 组成

| 目录 | 作用 | 目标框架 | 版本 |
| --- | --- | --- | --- |
| `src/AvifHeifConverter/` | 命令行核心：容器解析、Gain Map 提取、HEIC 构建 | `net9.0-windows`（win-x64） | 1.0.0 |
| `src/AvifHeifConverter.Gui/` | WinUI 3 图形界面 | `net9.0-windows10.0.19041.0`，Windows App SDK 2.6.4 | 1.2.0 |

GUI 项目通过 `<Compile Include="..\AvifHeifConverter\Services\**\*.cs">` 直接编译 Core 源码，**两个目录必须保持同级**。Core 项目可用 `-p:CoreMode=true` 切换为不依赖 WinUI 的控制台模式，便于独立验证转换逻辑。

## 构建

- 需要 .NET 9 SDK；GUI 项目另需 csproj 指定的 Windows SDK 包（`10.0.19041.87`）与 Windows App SDK 2.6.4 NuGet 包。
- 示例：`dotnet publish src\AvifHeifConverter.Gui\AvifHeifConverter.Gui.csproj -c Release -r win-x64 --self-contained true`

`heif-enc` 等外部转换引擎**不在本仓库中**，需自行准备并在运行时配置路径。

## 许可

GNU General Public License v2（GPL-2.0），全文见 `LICENSE`。

第三方转换引擎的许可兼容性**尚未核实（待核）**，再分发前请自行确认。
