# NekoBridge

面向局域网的多电脑键鼠共享与跨机文件拖放工具。

## 当前状态

已实现 Windows x64 v0.2.3 本地试用版，界面保留连接、屏幕排列、设置。鼠标移动适配 Mouse Without Borders 固定版本的默认算法，取消 v0.2.2 的 RawInput 计数倍率；顶部停放保留。剪贴板改为在线已配对设备间双向纯文本同步、有限重试及去回环。原生跨屏拖放修正 OLE 辅助窗口的启动与消息唤醒。相关来源与 MIT 许可见 THIRD-PARTY-NOTICES.md。

验证包含编译、鼠标映射纯运算 smoke、双向剪贴板隔离 smoke，以及真实 Windows Shell 文件数据对象网络读取与临时目录落盘。真实双机不同设备/缩放的手感仍需用户验收，不宣称与所有硬件组合完全等效。原生交互检查仅使用独立临时文件和测试窗口，不处理用户文件。

分发目录：artifacts/NekoBridge-0.2.3-win-x64/；运行 NekoBridge.exe。ZIP：artifacts/NekoBridge-0.2.3-win-x64.zip。完整使用说明见 [docs/使用说明.md](docs/使用说明.md)。

## 范围

- 第一版：Windows，目标为 4～5 台已配对电脑。
- 鼠标按屏幕布局跨边缘切换电脑，键盘跟随当前控制目标。
- 文件来源不局限于键鼠所在主机：可以操作 B，将 B 的文件拖到 C。
- 目标体验：桌面及资源管理器之间，文件、多选文件和文件夹跨机拖放，默认复制。
- 后续考虑 macOS，其他系统不在范围内。

## 技术边界

选择 C# + .NET 10 LTS；Windows 设置界面采用 WPF。

- 通用核心负责配对、协议、屏幕布局、控制权状态和传输任务，不依赖 Windows UI 或系统句柄。
- Windows 适配负责原生键鼠、剪贴板、Shell/OLE 拖放及系统权限。
- WPF 只负责 Windows 界面。未来 macOS 的系统适配及界面另行实现，复用已验证的协议和适用核心。
- 不为将来可能的功能提前建立空项目、空接口或伪实现。
- UI 设计调用仅在开发过程中发生，不进入产品运行链路。

## 最先需要验证的风险

成熟键鼠共享项目不等于已经解决任意跨机拖放。先用两台 Windows 验证从资源管理器拾取文件、跨屏交接拖放、落入目标资源管理器目录的完整链路，再验证多选与文件夹。不能将“发送文件到接收窗口”当成跨资源管理器拖放完成。

现有 Mouse Without Borders 的文件传输存在功能限制；不能承诺直接复用就满足全部需求。安装、服务模式、管理员窗口及安全桌面需要分别确定支持范围，不默认承诺覆盖。

## 开发环境

工程本地已安装并通过官方 SHA-512 校验的 .NET 10.0.404 SDK，路径为 .tools/dotnet；未替换全局 SDK。scripts/build.ps1 编译，scripts/publish.ps1 生成包含运行时的 Windows x64 分发目录和 ZIP。tools/CoreSmoke 为小范围回环检查，tools/smoke-windows.ps1 为真实桌面交互 smoke，均不进入产品分发包。

## 参考

- [PowerToys Mouse Without Borders 源码](https://github.com/microsoft/PowerToys/tree/main/src/modules/MouseWithoutBorders)
- [Mouse Without Borders 官方说明与限制](https://learn.microsoft.com/en-us/windows/powertoys/mouse-without-borders)
- [Windows Shell 拖放](https://learn.microsoft.com/en-us/windows/win32/shell/dragdrop)
- [.NET 发布与支持信息](https://github.com/dotnet/core)

开发和外观调用约束见 AGENTS.md。
