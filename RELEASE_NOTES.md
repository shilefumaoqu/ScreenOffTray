# ScreenOffTray 0.2.6

新增应用内更新：在“关于”页下载新版，显示进度并支持取消、重试和手动下载。软件核对官方 Release 的精确安装包名称、HTTPS 地址、大小与 SHA256，校验后由用户点击“保存并重启更新”，保存当前设置草稿、正常退出，再请求 Windows 安装权限。没有常驻更新服务。

更新入口仅支持已登记安装，只读核验并复用原有驱动，不转入首次驱动安装；不会强杀产品或其他软件，也不会自动重启 Windows。安装失败只在旧版本文件完整一致时重开旧版。失效缓存包可重新检查下载，安全退出超时会明确提示恢复方法。

**从 0.2.5 及更早版本升级到 0.2.6，仍需手动下载安装一次；应用内更新用于之后的新版本。** 保留原生玻璃、主题、设置路径、AppId、AssemblyVersion、关屏与恢复规则。旧版本继续独立保留。

已完成隔离下载与控制器用例、中英文 UI 协议检查、实际 GitHub 下载与哈希校验及代码审查。未覆盖安装本机正式程序，真实 UAC、文件替换与安装后完整运行未计作通过；见 [版本验证](docs/版本验证.md)。

Adds in-app updates with download progress, cancellation, retry and manual fallback. Official Release asset names, HTTPS URLs, size and SHA256 are verified. “Save and restart to update” explicitly saves the current settings draft, exits normally and asks Windows for installation approval. No resident update service is added.

The update path requires a registered installation and reuses its driver after read-only verification. It cannot enter first-time driver installation, force-close applications or automatically reboot Windows. Failures restart the old version only when its files still match. Rejected cached packages allow a fresh check; safe-exit timeouts explain manual recovery.

**Users on 0.2.5 or earlier must manually install 0.2.6 once to gain the updater for future versions.** Native glass, themes, settings paths, AppId, AssemblyVersion and display-off/recovery rules are preserved. Previous releases remain available. Isolated tests, actual GitHub download/hash checks and code review were completed; real UAC, file replacement and post-install operation were not tested on the host.

---

# ScreenOffTray 0.2.5

提高文件、界面、安装器和公开 Release 版本到 0.2.5，解决已运行本地 0.2.4 的用户无法通过同版本号发现后续玻璃修复的问题。更新检查仍比较版本，不把同号包误认为更新；现有 0.2.4 可发现独立 0.2.5 并打开安装包下载。

沿用已通过审查与实拍的原生 Desktop Acrylic、主题和界面；玻璃及业务运行逻辑没有新增改动。AppId、AssemblyVersion、设置路径、驱动、显示准备、关屏、输入恢复及播放保护保持不变。保留 0.2.4 及历史包，不自动覆盖安装。

This patch advances the file, UI, installer and public Release versions to 0.2.5. Users already running a local 0.2.4 build can now discover the later glass fix as a strictly newer version and open its Setup download. The update checker continues comparing versions; it does not treat a same-version package as an update.

The reviewed and photographed Desktop Acrylic implementation is unchanged. AppId, AssemblyVersion, settings paths, drivers, display preparation, display-off, input recovery and playback protection remain unchanged. Version 0.2.4 and older packages stay independent. No automatic installation is performed.

---

# ScreenOffTray 0.2.4

设置窗口接入真正的 Windows Desktop Acrylic 背景模糊，替代接近实色的表面。保持同一浅色窗口，在外部蓝、橙背景上完成真实桌面实拍：色调随背景变化，背后的文字和黑白纹理明显柔化，窗口自己的文字保持清楚。保留跟随系统／浅色／深色、Pane 风格控件与高光，不包含参考壁纸。系统不支持原生材质、关闭透明效果或高对比度时明确回退实色，不将其算作玻璃通过。

79 项隔离检查通过，并补齐 DPI 裁切时序与窗口关闭处理。驱动、显示准备、关屏、输入恢复、播放保护、快捷键和设置格式未改。应用更新仍是打开可信 Setup 下载，不自动覆盖安装。本机未安装该候选。实拍和验证边界见 [原生玻璃验证](docs/原生玻璃验证.md)。原生 Acrylic 参数由 Windows 决定，与 Pane 自定义合成并非逐像素一致；深色双背景、跨 DPI 实机与 Windows 10 回退呈现尚未实机复验。历史版本保持独立。

Settings now uses real Windows Desktop Acrylic blur instead of a near-opaque surface. The same Light window was moved across separate blue and orange desktop backgrounds: tint changes, underlying text and black/white edges soften, and foreground text remains clear. System / Light / Dark, Pane-inspired controls and highlights remain; no reference wallpaper is included. Unsupported systems, disabled transparency and high contrast use an explicitly solid fallback, which is not counted as glass acceptance.

Seventy-nine isolated checks passed, including deferred DPI clipping. Drivers, display preparation, display-off, input recovery, playback protection, hotkeys and settings formats are unchanged. Updating opens a trusted Setup download and never installs automatically. This candidate was not installed on the build machine. See [validation scope](docs/原生玻璃验证.md) for actual photographs and limitations. Windows controls the Acrylic parameters; this is not pixel-identical to Pane's custom compositor. Dark dual-background, real cross-DPI and Windows 10 fallback presentation remain unverified. Previous releases stay independent.

---
# ScreenOffTray 0.2.3

本版修复两个设置问题。发现稳定新版本后，“检查更新”按钮会明确变为“下载安装包”，优先打开同一 GitHub Release 中名称和 URL 均已核验的 Setup；仍不会自动下载、安装或退出。外观改为 Pane 的“跟随系统／浅色／深色”，暖白不再作为产品主题；旧 `geometry`、`warm` 自动迁移为浅色，旧 `black` 迁移为深色。驱动、显示准备、关屏、输入恢复、播放保护和快捷键规则未修改。`v0.2.2`、`v0.2.1`、`v0.2.0` 与历史安装包保持独立。

This release fixes two Settings issues. When a stable update is found, Check for updates now becomes Download installer and opens the exact matching GitHub Setup asset after URL validation; the app still does not download, install or exit automatically. Appearance now follows Pane's Use system theme / Light / Dark choices. Warm white is no longer a product theme; legacy geometry and warm values migrate to Light, while legacy black maps to Dark. Drivers, display preparation, display-off, input recovery, playback protection and hotkey rules are unchanged. Releases remain independent.

---

# ScreenOffTray 0.2.2

本版修复从源码重新打包时的兼容问题：Windows PowerShell 5.1 现在显式按 UTF-8 读取工程文件，避免中文资源路径被系统 ANSI 编码误读；解包验证工具固定到已核验的 innounp 2.71.3 GitHub 附件和 SHA256，解决上游同名附件替换造成的冷下载失败。已使用根目录默认入口、全新依赖缓存完成三工程构建和最终 14 个产品文件解包比对。运行逻辑、驱动、显示准备、关屏、输入恢复和播放保护均与 0.2.1 相同。`v0.2.1`、`v0.2.0`、`v0.1.9` 与历史安装包保持独立。

This release fixes source-build compatibility. Windows PowerShell 5.1 now reads project files explicitly as UTF-8, preventing Chinese resource paths from being misread under the system ANSI code page. The extraction verifier is pinned to the verified innounp 2.71.3 GitHub asset and SHA256, resolving a cold-download failure caused when upstream replaced a same-named asset. The root default entry built all three projects with a fresh dependency cache and verified all 14 extracted product files. Runtime logic, drivers, display preparation, display-off, input recovery and playback protection are unchanged from 0.2.1. Releases remain independent.

---

# ScreenOffTray 0.2.1

设置界面保留 Pane 风格的三页布局，移除误照搬的橙蓝绿黄几何壁纸，也删除窗口外围的演示底图、外边距和投影；只保留磨砂玻璃窗口本体与圆角透明边。顶层窗口、侧栏和页脚改用不透明雾面材质，消除 WPF 透明窗口在部分显卡上的内容重影，卡片仍保留窗口内部的轻微层次。外观选项调整为浅色、暖白、黑色，默认浅色。安装向导同步采用 Inno Setup 7.1.0 的现代动态向导、浅/深色雾面背景、玻璃卡片和双层月亮标识，并保留系统原生高 DPI、键盘及升级核验流程。显示准备、关屏、输入恢复、播放保护、白名单、快捷键、语言和开机启动逻辑未改。`v0.2.0`、`v0.1.9` 与历史安装包保持独立。

Settings keeps the three-page Pane-inspired layout while removing the sampled orange, blue, green and yellow geometry wallpaper. It also removes the display-only backdrop, outer margin and shadow, leaving only the rounded frosted-glass window itself. Opaque misted surfaces are used for the top-level window, sidebar and footer to eliminate transparent-window ghosting on affected GPUs while cards keep their internal depth. Its appearances are now Light, Warm white and Black, with Light as the default. Setup uses Inno Setup 7.1.0's modern dynamic wizard with matching light and dark misted backgrounds, glass cards and the layered-moon mark while retaining native high-DPI, keyboard and upgrade-verification behavior. Display preparation, display-off, input recovery, playback protection, allowlist, hotkey, language and startup logic are unchanged. `v0.2.0`, `v0.1.9` and older releases remain independent.

---

# ScreenOffTray 0.2.0

设置界面迁移为独立的 WPF 进程，采用 Pane 风格的三页布局与几何、暖白、黑色三种外观，默认几何。保留原有显示准备、关屏、输入恢复、播放保护、白名单、快捷键、语言和开机启动流程；设置主题随其他设置一起持久化。旧版 `v0.1.9` 与历史安装包保持独立。

Settings now runs in a separate WPF process with three Pane-styled pages and Geometry, Warm white and Black appearances. Existing display preparation, display-off, input recovery, playback protection, allowlist, hotkey, language and startup behavior is retained. The selected appearance is saved with the other settings; older releases remain available.

---

# 0.1.9.13 本地候选 / Local candidate

每次“一键准备”重新识别当前屏幕，仅对已核实的独立 MttVDD 按需匹配缩放；已一致不重复写入。调整完成后再开始原有只读稳定验证，失败沿用原布局恢复责任。设置窗口固定 7:5，常规最小 980×700 逻辑像素，屏幕不足时收紧；保留薄荷色和静态猫。驱动、实体输入/关屏/恢复决策及设置字段不变，无新增运行依赖。验证与交付记录见 `docs/0.1.9.13-接入与交付.md`；本地候选不等于已安装或公开发布。

Every explicit preparation rechecks current displays and matches only the verified independent MttVDD scale when needed. Matching values are not rewritten. Adjustment finishes before the existing read-only settling check; failures retain original-layout recovery ownership. Settings keeps a 7:5 client ratio, normally at least 980×700 logical pixels, bounded by the monitor work area. Mint colors and static cats remain. Driver, input/display/recovery rules and setting fields are unchanged, with no new runtime dependency. This is a local candidate, not an installed or public release.

# 0.1.9.10 本地候选 / Local candidate

音频端点只初始化枚举一次，后续沿用通知更新会话列表；读取周期和播放保护行为不变。升级新增只读驱动核验，保留现有设备与设置。本版已完成两小时隔离观察、专门审查与安装包解包核验；20 MB 目标未达，显示查询未全程通过，实际升级仍未执行；不代表已发布或已安装。

Audio sessions are initialized once and maintained through notifications. Polling and playback decisions are preserved. Application upgrades verify existing drivers without reinstalling them. The isolated resource run, code review and package extraction are complete; the 20 MB target was not met and display queries did not all succeed. This is not a public release or installed acceptance result.

# ScreenOffTray 0.1.9.5 · 本地候选 / Local candidate

设置窗口按确认参考图采用薄荷渐变头部、静态小猫、睡眠小猫与书本台灯场景、等高圆角卡片、分组图标和页脚花纹。插画从参考图裁成透明资源，所有控件仍可操作。保留现有双栏、窄窗堆叠、固定页脚、播放保护、快捷键和全部设置项；状态“检查”在就绪后仍可使用。驱动、播放判断、显示准备和关屏/输入决策均未改变。

Settings now follows the approved reference with a mint gradient header, static cats, a books-and-lamp scene, equal-height rounded cards, section icons and footer botanicals. Reference artwork is embedded as transparent resources while every control stays interactive. Existing two-column, narrow stacking, fixed footer, playback protection, shortcut and all settings remain. Driver, playback, preparation and display/input decisions are unchanged.

历史候选 0.1.9.4：

按用户的内存取舍，将设置页小猫改为已有预览的静态坐姿。保留布局和薄荷色；移除动画计时器、动作图加载、鼠标/键盘摸猫交互及缩放时的动画暂停接线。只内嵌一张静态图。驱动、播放判断、显示准备和关屏/输入决策均未改变。本候选不自动覆盖安装或上传 GitHub。

Settings now uses the existing neutral-pose preview as a static cat. The layout and mint theme remain. Animation timers, sprite-sheet loading, petting input and animation resize hooks are removed. Only one static image is embedded. Driver, playback decisions, preparation and display/input logic are unchanged. No automatic installation or GitHub upload is performed.

历史发行说明：

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
