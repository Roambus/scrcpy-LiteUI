# 第三方组件声明

本仓库 `scrcpy/` 目录下包含以下第三方组件，其版权归各自所有者所有。分发本软件
（包括打包后的单文件 EXE）时，请保留本声明及相应许可证文本。

打包成单文件 EXE 时，这些组件会被**内嵌进 exe**，并在首次启动时解到
`%LOCALAPPDATA%\Kuaitou\runtime\<一串哈希>\` 下。分发形态变了，但分发性质没变：
请同样保留本声明与相应许可证文本。

| 组件 | 相关文件 | 许可证 | 上游项目 |
|---|---|---|---|
| scrcpy | `scrcpy.exe`、`scrcpy-server`、`scrcpy.png`、`disconnected.png` | Apache-2.0 | [Genymobile/scrcpy](https://github.com/Genymobile/scrcpy) |
| Android SDK Platform-Tools（adb） | `adb.exe`、`AdbWinApi.dll`、`AdbWinUsbApi.dll` | Apache-2.0 | [platform-tools](https://developer.android.com/tools/releases/platform-tools) |
| FFmpeg | `avcodec-62.dll`、`avformat-62.dll`、`avutil-60.dll`、`swresample-6.dll` | LGPL-2.1-or-later | [ffmpeg.org](https://ffmpeg.org/) |
| SDL | `SDL3.dll` | Zlib | [libsdl-org/SDL](https://github.com/libsdl-org/SDL) |
| libusb | `libusb-1.0.dll` | LGPL-2.1-or-later | [libusb.info](https://libusb.info/) |

scrcpy 的完整许可证文本随附于 [`scrcpy/LICENSE.txt`](scrcpy/LICENSE.txt)。

## 关于 LGPL 组件（FFmpeg / libusb）

FFmpeg 与 libusb 以 **LGPL-2.1-or-later** 分发，本项目以**动态链接**（DLL）方式使用，
符合 LGPL 的要求。再分发时请注意：

- 保留其许可证文本与版权声明；
- 不得移除用户替换这些 DLL 的能力（即保持动态链接，不静态合并且不可替换）。

如需再分发，建议一并向接收方提供上述组件的许可证全文，可从表中上游地址获取。

## 应用图标（重要）

本项目内置了一批以包名命名的应用图标（`icons/` 目录，约 2300 个 `.webp`）。
这些图标是各 Android 应用自身的图标，其**商标权与版权归各自应用的所有者**，
不属于本项目的授权范围。

该目录**未入库**，也不在 Apache-2.0 许可证的覆盖范围内，请勿将其视为本项目的
一部分进行再分发。若打包机上存在 `icons/`，构建时会把它内嵌进 exe——这只是
方便显示，不改变图标的权属。程序运行时也会在本机按需在线抓图或从手机取图，
**缺失该素材库不影响任何功能**，仅影响列表中的显示效果。