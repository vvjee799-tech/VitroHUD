# 标签交互逻辑回归

```powershell
dotnet run --project tests/ProviderTabs/ProviderTabs.csproj -c Release
```

该项目只测试保留的标签交互状态机，包括点击容差、排序、取消、长按和迟到计时器处理，不连接官方客户端，也不发送原生输入。

当前 VitroHUD 使用侧边客户端入口；本项目用于历史逻辑回归，不能据此判断当前界面仍有顶部标签栏。当前侧栏与胶囊几何另由 `tests/GlassInteraction` 覆盖。
