# 测试入口

在仓库根目录运行。脚本输出写入被忽略的 `checks/`，不会随安装包发布。

- `python tests/RepositoryLayoutTests.py`：检查三个工程的声明文件、路径边界和共享源码归属，不启动程序。
- `powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File tests/BuildEncodingTests.ps1`：在默认打包入口使用的 Windows PowerShell 5.1 下，检查实际工程读取语句的 UTF-8 解码及图标路径；不下载、构建或安装。
- `powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File tests/BuildDependencyTests.ps1`：使用本地 ZIP 夹具检查实际依赖加载函数的二进制请求头、冷缓存解压、缓存复用及错误哈希阻断；不联网、不执行下载工具。
- `powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File tests/ReleaseVersionConsistencyTests.ps1`：核对程序、设置窗口、帮助、安装器、构建脚本和 README 的公开版本字段一致。
- `tests/SettingsPipeTests.ps1`：隔离命名管道契约。
- `tests/OptimizationChainTests.ps1`（可加 `-View`）：请求协调、设置事务与状态快照，使用假平台。
- `tests/StartupTests.ps1`：初始化确认协议，使用假启动器。
- `tests/WpfSettingsProbe.ps1 -LayoutTests -Runs 1`：正式 WPF 控件、私有非输入桌面、假后台。
- `tests/WpfSettingsProbe.ps1 -CompileOnly`：仅编译隔离 WPF 测试程序。
- `tests/InstallerReleaseVersionTests.ps1`：安装器版本门禁的纯文本/数值检查。

其他驱动准备、资源观察与原型脚本按需使用，执行前阅读脚本及文档中的验证边界。它们不是默认回归入口，不能批量运行所有脚本。

旧 WinForms 设置页、静态猫及专用测试已退出当前源码树。历史源码及当时适用的测试入口保留在 `v0.1.9`、`v0.2.0` 等历史 Tag 中；需要复现时检出对应版本，不能把旧界面结果算作当前 WPF 验证。
