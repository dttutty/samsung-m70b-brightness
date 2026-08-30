# Samsung M70B Brightness

<p align="center">
  <img src="src/M70BBrightness/assets/m70b-tv.png" width="160" alt="Samsung M70B Brightness icon">
</p>

<p align="center">
  <a href="#english">English</a> · <a href="#简体中文">简体中文</a>
</p>

---

<a id="english"></a>

## English

A Windows 11 tray brightness controller for Samsung M70B Smart Monitors. It uses the monitor's local Tizen WebSocket remote-control protocol to navigate to `Picture → Expert Settings → Brightness`, allowing real hardware brightness control from a PC even when DDC/CI is unavailable.

> This is an unofficial community project and is not affiliated with Samsung. It relies on a local remote-control interface whose compatibility is not publicly guaranteed.

### Features

- Windows 11-style popup anchored above the system tray
- High-contrast flat television icon designed for 16×16 tray rendering
- Automatically opens the monitor's Brightness adjustment screen
- Relative slider control using the last known brightness value
- Reset-to-minimum and reset-to-maximum buttons for resynchronization
- Live connection, navigation, adjustment, and exit command status
- Explicit close button that exits the monitor menu without opening Samsung Home
- Tizen pairing token encrypted with Windows DPAPI
- Single-instance protection to prevent duplicate commands

### Download

Download the self-contained Windows x64 build from [GitHub Releases](https://github.com/dttutty/samsung-m70b-brightness/releases/latest). No separate .NET installation is required for the self-contained release.

### Usage

1. Connect the PC and Samsung monitor to the same local network.
2. Start the app and enter the monitor's local IP address on first launch.
3. Approve the pairing request shown on the monitor.
4. Left-click the television icon in the Windows system tray.
5. Wait until the app finishes opening the monitor's Brightness control.
6. Drag the slider to adjust brightness.
7. Click the popup's `×` button to close the monitor menu and dismiss the popup.

Clicking elsewhere does not close the popup. To stop the background app completely, right-click its tray icon and choose **Exit**.

If brightness was changed with SmartThings or the physical remote, use **Reset minimum brightness** or **Reset maximum brightness** once to synchronize the app's remembered value.

### Local data and privacy

The app stores the following files only under `%LOCALAPPDATA%\M70BBrightness`:

- `host.txt` — the monitor's local network address
- `token.dat` — the Tizen pairing token encrypted with Windows DPAPI
- `brightness.txt` — the last remembered brightness value from 0 to 50

No IP address, pairing token, account credential, or brightness history is included in this repository or release package. To pair again, exit the app, delete this directory, and restart it.

### Verified hardware

- Samsung Smart Monitor M7 / M70B
- Model: `LS43BM702UNXZA`
- Tizen platform: 2022 `22_NIKEL_SMT`

Other Samsung displays may work if they support the same Tizen WebSocket remote protocol and menu layout, but they have not been verified.

### Why not use the Windows brightness slider?

The tested M70B connection does not expose a usable DDC/CI brightness VCP, and Windows `SetMonitorBrightness` fails. The native Windows brightness slider requires a brightness interface implemented by the monitor/OEM and graphics driver. A normal desktop app cannot register this local network remote-control protocol as a native Windows brightness provider.

### Build from source

Windows and the .NET 10 SDK are required:

```powershell
dotnet publish .\src\M70BBrightness\M70BBrightness.csproj -c Release -r win-x64 --self-contained false
```

### Security notes

- The WebSocket client connects only to the local host configured by the user.
- Samsung displays use a locally generated/self-signed certificate for this interface, so the app accepts that local TLS certificate.
- User-specific IP addresses and tokens are never compiled into the app.

---

<a id="简体中文"></a>

## 简体中文

一个用于 Windows 11 的 Samsung M70B 智能显示器亮度托盘控制器。它通过显示器的本地 Tizen WebSocket 遥控协议，自动进入 `Picture → Expert Settings → Brightness`，让不支持 DDC/CI 的 M70B 也能从电脑调节真实硬件亮度。

> 这是非官方社区项目，与 Samsung 无关。项目使用的是未公开保证兼容性的本地遥控接口。

### 功能

- 紧贴系统托盘上方的 Windows 11 风格弹窗
- 专为 16×16 托盘尺寸设计的高对比扁平电视图标
- 自动打开显示器的 Brightness 调节界面
- 根据最后记住的亮度进行差量滑块调节
- 最小/最大重置按钮，可重新同步真实亮度
- 实时显示连接、菜单导航、亮度调节和退出命令
- 使用明确的关闭按钮退出设置，不会跳转到 Samsung Home
- 使用 Windows DPAPI 加密保存 Tizen 配对令牌
- 单实例运行，避免多个后台进程重复发送命令

### 下载

前往 [GitHub Releases](https://github.com/dttutty/samsung-m70b-brightness/releases/latest) 下载 Windows x64 自包含版本。自包含发布包不需要额外安装 .NET。

### 使用

1. 确保电脑和 Samsung 显示器处于同一局域网。
2. 启动程序，首次运行时输入显示器的局域网 IP 地址。
3. 在显示器上出现授权提示时选择“允许”。
4. 左键单击 Windows 系统托盘中的电视图标。
5. 等待程序自动进入显示器的 Brightness 调节界面。
6. 拖动滑块调节亮度。
7. 点击弹窗右上角 `×`，程序会退出显示器设置并关闭弹窗。

点击弹窗外不会关闭。若要完全结束后台程序，请右键托盘图标并选择“退出”。

如果通过 SmartThings 或实体遥控器修改过亮度，请使用一次“重置最小亮度”或“重置最大亮度”，让程序重新同步记住的数值。

### 本机数据与隐私

程序只在 `%LOCALAPPDATA%\M70BBrightness` 保存以下文件：

- `host.txt` — 显示器局域网地址
- `token.dat` — 使用 Windows DPAPI 加密的 Tizen 配对令牌
- `brightness.txt` — 程序最后记住的亮度值，范围为 0–50

本仓库和发布包不包含任何用户 IP、配对令牌、账户凭据或亮度历史。若需重新配对，请退出程序、删除该目录，然后重新启动。

### 已验证设备

- Samsung Smart Monitor M7 / M70B
- 型号：`LS43BM702UNXZA`
- Tizen 平台：2022 `22_NIKEL_SMT`

其他支持相同 Tizen WebSocket 遥控协议和菜单布局的 Samsung 显示器可能也能工作，但尚未验证。

### 为什么不用 Windows 原生亮度滑块？

已测试的 M70B 连接没有暴露可用的 DDC/CI 亮度 VCP，Windows 的 `SetMonitorBrightness` 也会失败。Windows 原生亮度滑块需要显示器/OEM 与显卡驱动实现亮度接口；普通桌面应用无法把这套局域网遥控协议注册成 Windows 原生亮度提供程序。

### 从源码构建

需要 Windows 和 .NET 10 SDK：

```powershell
dotnet publish .\src\M70BBrightness\M70BBrightness.csproj -c Release -r win-x64 --self-contained false
```

### 安全说明

- WebSocket 客户端只连接用户配置的局域网主机。
- Samsung 显示器为该本地接口使用自行生成/自签名证书，因此程序会接受该本地 TLS 证书。
- 用户 IP 地址与令牌永远不会编译进程序。

---

## License / 许可证

[MIT](LICENSE)

