# ScreenOffTray v0.1.4 · 预发布 / Pre-release

这是首个 GitHub 预发布，提供 Windows x64 离线安装包和项目源码。

## 本版变化

- 自动计时改用已有规则识别的实体键鼠活动，Windows 标记的模拟输入不再重置计时。
- 应用要求保持亮屏时暂停自动关屏；保护解除后重新计满关闭时间。
- 托盘显示“应用要求亮屏”或“无法读取亮屏状态”等暂停原因。
- 沿用既有驱动、显示切换、3 秒保护和第一次输入仅唤醒的规则。手动关屏仍可覆盖自动保护。

下载 `ScreenOffTray-0.1.4-Setup-x64.exe`。需要 Windows 10 22H2 / Windows 11 x64、.NET Framework 4.8 和一个实体显示器。升级前正常退出旧版，现有设置保留。Windows 管理员及驱动发布者提示由用户操作；安装器不自动重启。

已完成定向隔离测试、专门代码审查、11 项安装载荷核验，以及单台电脑的普通权限升级启动。没有重复已通过的人工关屏矩阵；这些结果不代表全部硬件或长期运行兼容性。干净系统首装、虚拟屏对独显直连自动切换的影响尚未完整验证。

某些非视频应用也会保留亮屏请求，此时自动关屏仍会暂停。快捷键冲突反馈仍有已知盲区，可更换组合键或使用托盘立即关屏。本版更新源继续留空，不自动安装更新。主程序及安装器未进行应用代码签名，驱动保持上游签名。

安装包：3,068,863 字节。SHA-256：

```
ef46f699f35a7c1bb669b03efd8568c7b4a81d2c3ff84cf875156fb5c4392039
```

## English

The first GitHub pre-release provides a Windows x64 offline installer and source code.

- Automatic idle timing now uses the existing physical-input classification. Windows-marked injected input does not reset it.
- Display-required requests pause automatic off. A full timeout restarts when the protection ends.
- The tray explains display-request and unreadable-state pauses.
- Existing driver, display switching, 3-second guard and wake-only first input are retained. Manual off can override automatic protection.

Download `ScreenOffTray-0.1.4-Setup-x64.exe`. Requires Windows 10 22H2 / Windows 11 x64, .NET Framework 4.8 and one physical display. Exit the previous version normally before upgrading; settings are retained. Complete Windows prompts yourself. Setup never restarts Windows automatically.

Targeted isolated tests, dedicated code review, verification of all 11 application payload files and a standard-user upgrade/startup check on one computer were completed. The previously accepted physical display-off matrix was not repeated. Clean-system installation, full hardware coverage, automatic discrete-GPU display switching and long-term operation remain unverified.

Non-video apps can also retain display requests and pause automatic off. Shortcut-conflict feedback has known blind spots; try another combination or the tray action. The update source remains blank, with no automatic installation. Application and installer binaries have no application code signature; the driver retains its upstream signature.

Installer size and SHA-256 are listed above and in `SHA256SUMS.txt`.
