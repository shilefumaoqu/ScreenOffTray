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
