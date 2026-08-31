# Samsung Tizen Brightness

<p align="center">
  <a href="README.zh-CN.md">简体中文</a> · <strong>English</strong>
</p>

A Windows tray controller for the hardware backlight of compatible Samsung Tizen displays. Its companion TV app keeps the HDMI picture visible while brightness is adjusted.

<p align="center">
  <img src="docs/screenshots/brightness-flyout.png" width="478" alt="Brightness flyout">
</p>

## Download

Download the Windows x64 build from [GitHub Releases](https://github.com/dttutty/samsung-tizen-brightness/releases/latest).

## Requirements

- Windows 11
- A compatible Samsung Tizen display on the same local network
- Developer Mode and the companion Tizen app signed with a Samsung Partner certificate

Verified on Samsung Smart Monitor M7 / M70B (`LS43BM702UNXZA`, 2022 Tizen). Other models may work but are not guaranteed.

## Setup

1. Install and launch `tizen/HDMIBrightnessBridge` on the display.
2. Start the Windows app and enter the display's local IP address.
3. If required, right-click the tray icon, select **Pair TV remote permission…**, and approve the request on the display.
4. Left-click the tray icon to adjust brightness.

The pairing token is encrypted with Windows DPAPI and stored locally. No user address or token is included in this repository.

## Build

```powershell
dotnet publish .\src\SamsungTizenBrightness\SamsungTizenBrightness.csproj -c Release -r win-x64 --self-contained false
```

Set the PC's LAN IPv4 address in `tizen/HDMIBrightnessBridge/bridge.js`, then package the TV project in Tizen Studio with a Partner certificate containing the display's DUID. The required AVInfo privilege is unavailable to Public certificates.

> Unofficial community project, not affiliated with Samsung. It uses Samsung interfaces whose compatibility is not publicly guaranteed. Factory reset and service-menu operations are not implemented.

## License

[MIT](LICENSE)
