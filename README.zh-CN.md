# Samsung Tizen Brightness

<p align="center">
  <strong>简体中文</strong> · <a href="README.md">English</a>
</p>

一个用于兼容 Samsung Tizen 显示设备的 Windows 托盘亮度控制器。电视端配套应用会在调节硬件背光时保持 HDMI 画面显示。

<p align="center">
  <img src="docs/screenshots/brightness-flyout.png" width="478" alt="亮度调节弹窗">
</p>

## 下载

从 [GitHub Releases](https://github.com/dttutty/samsung-tizen-brightness/releases/latest) 下载 Windows x64 版本。

## 使用条件

- Windows 11
- 电脑与兼容的 Samsung Tizen 显示设备位于同一局域网
- 可选：开启开发者模式并安装配套 Tizen 应用，以实现不遮挡 HDMI 的直接控制

已在 Samsung Smart Monitor M7 / M70B（`LS43BM702UNXZA`，2022 Tizen）上验证。其他型号可能兼容，但不作保证。

## 设置

1. 启动 Windows 程序，输入显示设备的局域网 IP。
2. 如需授权，右键托盘图标选择“重新配对电视遥控权限…”，并在显示设备上允许。
3. 左键单击托盘图标即可调节亮度。

安装配套 Tizen 应用后，程序可以直接调节亮度且不遮挡 HDMI。没有开发者模式时会自动退回模拟遥控器方式，调节过程中电视设置菜单会短暂出现。

配对令牌使用 Windows DPAPI 加密并仅保存在本机；仓库不包含用户地址或令牌。

## 构建

```powershell
dotnet publish .\src\SamsungTizenBrightness\SamsungTizenBrightness.csproj -c Release -r win-x64 --self-contained false
```

先在 `tizen/HDMIBrightnessBridge/bridge.js` 中填写电脑的局域网 IPv4 地址，再用 Tizen Studio 和包含目标设备 DUID 的 Partner 证书打包；Public 证书无法获得所需的 AVInfo 权限。

> 本项目是非官方社区项目，与 Samsung 无关。使用的 Samsung 接口未公开保证兼容性。程序不包含恢复出厂或 Service Menu 操作。

## 许可证

[MIT](LICENSE)
