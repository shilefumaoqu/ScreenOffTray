# ScreenOffTray 项目约定

本项目按下表维护三个应用源码目录。

| 职责 | 实际位置 |
| --- | --- |
| `app_main/` 业务与界面 | 三个 .NET 工程各保留自己的业务、界面和专用适配。跨工程的快捷键模型与版本元数据在 `app_main/Shared/`。 |
| `app_request/` 外部与进程入口 | 托盘、设置进程、驱动助手的命令行入口，以及设置进程间的私有管道协议。命令从这里交给已有业务，部署入口仍在 `Installer/ScreenOffTray.iss`。 |
| `app_tools/` 共用技术工具 | `app_tools/Windows/` 提供共用显示适配和程序名称解析。只服务播放模块的 Rust 源码随播放功能维护。 |

三个工程通过显式 `Compile Include` 引用入口和工具，沿用原来生成的三个 EXE。构建、测试和历史说明见[目录结构](docs/目录结构.md)、[构建与验证](docs/构建与验证.md)和[测试入口](tests/README.md)。
