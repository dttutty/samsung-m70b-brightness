# Samsung M70B Brightness

<p align="center">
  <img src="src/M70BBrightness/assets/m70b-tv.png" width="160" alt="Samsung M70B Brightness icon">
</p>

一个用于 Windows 11 的 Samsung M70B 智能显示器亮度托盘控制器。它通过电视的本地 Tizen WebSocket 遥控协议，自动进入 `Picture → Expert Settings → Brightness`，让不支持 DDC/CI 的 M70B 也能从电脑调节真实背光亮度。

> 这是非官方社区项目，与 Samsung 无关。使用的是未公开保证兼容性的本地遥控接口。

## 功能

- Windows 11 风格的右下角托盘弹窗
- 单击托盘电视图标后，自动打开显示器 Brightness 界面
- 滑块按当前已知亮度发送差量按键，响应更快
- “重置最小/最大”可重新同步真实亮度值
- 实时显示连接、菜单导航、调节和退出命令
- 点击右上角 `×` 后逐层退出设置，不会跳转到 Samsung Home
- 配对令牌使用当前 Windows 用户的 DPAPI 加密保存
- 单实例运行，避免两个后台进程重复发送命令

## 下载

可在仓库的 [Releases](../../releases) 页面下载 Windows x64 单文件版本。

## 使用

1. 确保电脑和 Samsung 显示器在同一局域网。
2. 启动程序，首次运行输入显示器的局域网 IP 地址。
3. 显示器出现授权提示时选择“允许”。
4. 单击任务栏右下角的电视图标。
5. 等程序自动进入 Brightness 界面后拖动滑块。
6. 点击弹窗右上角 `×`，程序会同步退出显示器设置界面。

如果显示器亮度被遥控器或其他 App 修改过，请先点一次“重置最小亮度”或“重置最大亮度”，让程序重新同步当前值。

## 本机数据

程序只在 `%LOCALAPPDATA%\M70BBrightness` 保存：

- `host.txt`：显示器局域网地址
- `token.dat`：由 Windows DPAPI 加密的 Tizen 配对令牌
- `brightness.txt`：程序最后记住的亮度（0–50）

这些文件不会上传到 GitHub。如果要重新配对，可退出程序后删除该目录，再次启动。

## 已验证设备

- Samsung Smart Monitor M7 / M70B
- 型号：`LS43BM702UNXZA`
- Tizen 平台：2022 `22_NIKEL_SMT`

其他支持 Samsung Tizen WebSocket 遥控协议、且菜单路径一致的型号可能也能工作，但尚未验证。

## 为什么不用 Windows 原生亮度滑块？

这台 M70B 在已测试的连接上没有暴露可用的 DDC/CI 亮度 VCP，Windows 的 `SetMonitorBrightness` 也失败。Windows 系统亮度滑块需要显示器/显卡驱动提供亮度控制接口，普通桌面应用无法把网络遥控协议直接注册为系统亮度提供程序。

## 从源码构建

需要 Windows 和 .NET 10 SDK：

```powershell
dotnet publish .\src\M70BBrightness\M70BBrightness.csproj -c Release -r win-x64 --self-contained false
```

## 安全说明

- WebSocket 仅连接用户配置的局域网主机。
- Samsung 显示器使用本地自签名证书，因此该连接会接受显示器提供的自签名 TLS 证书。
- 源码和发布包中不包含任何用户 IP、令牌或账户信息。

## License

[MIT](LICENSE)

