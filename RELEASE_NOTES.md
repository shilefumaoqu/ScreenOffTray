# ScreenOffTray v0.1.9 · 预发布 / Pre-release

相对公开的0.1.4，本版加入音频白名单播放保护、单行名单管理、独立薄荷设置窗口、首次显示准备引导及可互动猫猫。

- 右键保留小菜单；设置按C稿分为基本设置和播放保护，窄工作区改为单列。
- 猫猫会伸懒腰、舔爪、歪头、轻拍，点击避免紧邻重复，空闲时偶尔动作；最小化或隐藏后停播。
- 软件图标读取实际可执行文件。分钟输入、缩放和长内容滚动得到定向修正。
- 名单内软件播放音频时保护，默认连续无实体输入60分钟后不再阻挡关屏；静音视频不在保证内。
- 升级前请从托盘正常退出，安装器保留设置。更新源仍留空，不自动下载安装。
- 驱动及已验收关屏、3秒保护、首次输入消费逻辑未因本轮UI接入修改。

相对0.1.8，安装包5.22 MiB，展开产品文件3.35 MiB；体积增长来自内嵌猫动作素材。

定向原生UI和动画检查、专门代码审查、干净构建及最终解包核验已执行。可见设置关闭曾偶发超时，后续旧二进制及当前源码复验正常，**根因仍未定位，不能宣称已消除**。本版未做干净系统安装或长期运行验证；未重跑已验收关屏矩阵。实体多屏、远程虚拟屏准备及混合显卡自动独显直连仍有限制。详见[验证说明](docs/版本验证.md)。

Compared with public 0.1.4, this pre-release adds audio allowlist playback protection, compact list management, a separate mint Settings window, first-run display preparation and an interactive cat. C-layout columns stack on narrow workspaces. The cat varies click responses and occasional idle actions; hidden/minimized windows stop animation.

Exit the old version normally before upgrading; settings are retained. The update source is still blank. The UI integration does not change the driver or accepted display/input decisions. The installer is 5.22 MiB and its product payload is 3.35 MiB.

Targeted UI/animation checks, dedicated code review, clean build and extracted-package verification were completed. A visible Settings close probe previously timed out; subsequent checks passed, but the cause remains unknown. Clean-system installation, long-term stability, physical DPI switching and all hardware configurations are not verified. This remains a pre-release.
