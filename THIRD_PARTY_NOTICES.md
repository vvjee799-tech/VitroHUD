# 第三方组件与名称

VitroHUD 自有源码与仓库中的图标素材采用 [MIT](LICENSE) 许可证。第三方依赖继续适用其各自许可证，使用本项目的许可证不会改变它们的条款。

| 组件 | 用途 | 上游与许可信息 |
| --- | --- | --- |
| .NET | 桌面与 Probe 运行时 | [dotnet/runtime](https://github.com/dotnet/runtime)，MIT；运行时分发包也包含各自的第三方声明 |
| Windows App SDK / WinUI 3 | Windows 原生界面 | [microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK)，MIT |
| Win2D | 图形与模糊效果 | [microsoft/Win2D](https://github.com/microsoft/Win2D)，MIT |

依赖版本见项目文件与 `global.json`，构建时通过 NuGet 获取。生成的自包含程序及第三方运行时文件不存入源码仓库；分发构建产物时应保留依赖自带的许可证和声明。

Claude、ChatGPT、Codex、DeepSeek、Windows 等名称属于各自权利人。本项目是独立的社区工具，不代表这些产品的官方项目、背书或合作。官方客户端和其订阅服务不由本仓库分发，使用时仍需遵守相应服务条款。

本机调查产生的客户端解包副本、分析材料和诊断输出不属于公开源码，已排除在提交范围之外。
