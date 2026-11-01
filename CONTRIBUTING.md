# 参与贡献

欢迎提交可复现的问题和范围明确的 Pull Request。开始前请阅读 [AGENTS.md](AGENTS.md) 和 [README.md](README.md) 中的测试版能力与限制；涉及客户端适配的改动，请注明客户端版本、模式和实际验证范围。

## 开发环境与构建

使用 Windows 10 19041+ / Windows 11 x64、PowerShell、[global.json](global.json) 指定的 .NET SDK，以及 Node.js 24（仅桥接模拟测试需要）。首次构建需要联网还原 NuGet 包。

在仓库根目录运行：

```powershell
.\build.ps1 -Dotnet (Get-Command dotnet).Source
```

脚本生成 `artifacts/VitroHUD/` 下的 Windows x64 自包含程序，检查 XAML 和图标资源，并运行 `VitroHUD.Probe.exe --self-test`。开发与 CI 无需使用 `-Install`；手动启动应用前先完成下面的离线逻辑测试。

## 自动检查

以下检查使用逻辑或内存中的模拟数据，不需要登录或连接原客户端：

```powershell
dotnet run --project tests/GlassInteraction/GlassInteraction.csproj --configuration Release
dotnet run --project tests/ProviderTabs/ProviderTabs.csproj --configuration Release
node --test tests/claude-bridge.test.mjs
```

PowerShell 中每条命令都应以退出码 `0` 结束。ProviderTabs 保留了旧标签交互逻辑的回归检查，不能据此断言当前界面仍使用顶部标签栏。构建自检还使用临时目录和本机回环监听器检查配置迁移、协议与端口归属，不向真实会话发送消息。

[Windows CI](.github/workflows/ci.yml) 在 push 和 Pull Request 时运行这些检查及发布脚本。成功后保留七天构建产物，供检查该次提交；CI 通过不代表所有客户端、账号或后台操作均兼容。

页面解析样例可通过 `node tests/build-format-fixtures.cjs` 生成，在浏览器打开生成的 `tests/format-fixtures.html` 查看汇总。生成 HTML 不提交到仓库。

真实客户端验收应使用专门的测试会话，先验证只读连接。任何原生鼠标、键盘或剪贴板输入测试，都必须事先取得操作者明确确认，不得在 CI 或常规自动检查中触发。本机临时诊断脚本和私人验收记录不属于公开源码。

## 适配与隐私边界

- 保持来源窗口和会话绑定，保护原客户端已有草稿；发送结果不明确时不得自动重试。
- UI Automation 保持独立进程隔离和请求时限；将客户端内容与外部输入视为不可信数据。
- 不用模型 API 或伪造内容替代真实客户端连接。模拟数据仅用于测试，并应清楚标明。
- 不提交真实会话正文、截图中的私人内容、凭据、令牌、本地连接文件、诊断转储或个人机器路径。复现材料应使用脱敏信息和人工构造的数据；生成的构建与验收文件不进入源码提交。
- 兼容性结论只覆盖实际测试过的版本和模式；尚未验证、上游失败或部分可用的能力应如实记录，保留测试版边界。

## 提交 Pull Request

说明触发问题的条件、修改后的行为和验证命令及结果。界面改动可附脱敏截图；适配改动注明是否只读测试、是否真正发送及是否收到原客户端回执。无法验证的步骤直接标为未验证。安全问题请遵循 [SECURITY.md](SECURITY.md)，避免在公开 Issue 中披露敏感细节。
