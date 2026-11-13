<p align="center">
  <img src="docs/assets/logo.png" width="150" alt="NekoBridge">
</p>

<h1 align="center">NekoBridge</h1>

<p align="center">局域网内的键鼠共享，以及跨电脑的文件拖放。</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-x64-1B2130?style=flat-square" alt="Windows x64">
  <img src="https://img.shields.io/badge/version-1.00-58790F?style=flat-square" alt="1.00">
</p>

---

## 关于

一种常见的情况是桌面上摆着好几台电脑，每台各配一套键鼠，用的时候来回换手。NekoBridge 的做法是把键鼠留在一台机器上，其他电脑通过局域网接收输入。鼠标越过屏幕边缘时，控制权就转过去了，键盘也跟着走。

安装后不需要指定哪台是主机。谁插着键鼠谁就在控制，连接是对称的，A 控制 B 和 B 控制 A 都能用。

## 功能

**键鼠共享**：鼠标从屏幕左右边缘移出，指针会出现在相邻电脑上。光标跟着实际排列走，键盘输入随当前鼠标位置切换。任何时候按 `Ctrl+Alt+Home` 回到自己的电脑。

**文件拖放**：在资源管理器里选中文件、多选或者整个文件夹，拖到屏幕边缘继续拖，进入另一台电脑的窗口后松手，内容就复制过去了。复制不会删除源文件。A 控制 B 的时候，也可以直接把 B 上的文件拖到 C。

**剪贴板**：文字、图片和文件引用都能跨机粘贴。文件按需读取，源电脑在线即可，不会预先传一份副本过去。

**历史记录**：`Alt+V` 打开，支持搜索和分类筛选，可以收藏。记录保存在本地数据库，重启之后仍然存在。保留条数、快捷键、是否记录图片和文件都能在设置里调整。

**配对与安全**：首次连接需要对方屏幕上显示的随机密钥，通信全程 TLS 加密。没有配对的设备无法接入。控制连接中断时，注入的按键会被释放，不会出现某个键卡住的情况。

## 安装

从 [Releases](https://github.com/Marigold1122/Necobridge/releases/latest) 下载 `NekoBridge-1.00-win-x64.zip`，解压到任意目录，运行 `NekoBridge.exe`。压缩包内含 .NET 运行时，不需要单独安装。exe 依赖同目录下的其他文件，请不要单独复制出去。

所有参与连接的电脑需要使用相同版本。升级前先从系统托盘退出正在运行的旧版本，配对信息会保留。

第一次启动时如果 Windows 防火墙弹出提示，勾选专用网络即可。

## 连接

以两台电脑为例，左边这台插着键鼠，右边这台要被控制。

在右侧电脑上打开 NekoBridge，选择「被控端」，点击「开启配对」。屏幕上会显示本机的地址和一组密钥，这组信息只在五分钟内有效。

回到左侧电脑，选择「控制端」，填写刚才的地址和密钥，点击连接。连接成功后进入「屏幕排列」，把两张设备卡片拖成它们在桌面上的实际位置。之后把鼠标移到右侧屏幕边缘，指针就过去了。

第三台、第四台的连接方式相同。任意一台都可以发起连接，不必固定某台电脑做中心。

## 系统托盘

关闭窗口后程序继续在托盘运行，右键图标可以暂停共享、打开剪贴板历史或者退出。暂停期间不接收也不转发输入，正在按住的按键会被释放。

## 限制

- 仅提供 Windows x64 版本，macOS 尚未实现。
- 每台电脑的多个显示器被视为一整块虚拟桌面，暂不支持任意二维拼接。
- 无法在 UAC 安全桌面、登录界面或管理员权限窗口中工作，这是系统机制限制。
- 游戏、反作弊程序、独占输入的软件以及远程桌面嵌套场景未做适配。
- 符号链接、回收站、手机设备和压缩包内部对象不属于普通文件，不在拖放支持范围内。
- 跨屏拖放使用 Windows 原生接口，目标程序出错时可能已经写入部分文件，请以资源管理器中的实际结果为准。
- 文件较大时，目标端需要先准备清单，第一次拖放会有等待时间。
- 不同分辨率、缩放比例下的操作手感仍在收集反馈，遇到问题欢迎提 issue，附上操作步骤和两台机器的分辨率会更容易定位。

## 构建

需要 .NET 10 SDK。

```powershell
git clone https://github.com/Marigold1122/Necobridge.git
cd Necobridge

# 编译
.\scripts\build.ps1

# 生成自带运行时的分发目录与压缩包
.\scripts\publish.ps1
```

目录结构：

```
src/NekoBridge.Core      配对、协议、屏幕布局与输入状态，不依赖任何 UI 框架
src/NekoBridge.Windows   Windows 平台适配与 WPF 界面
tools/                   小范围回环检查、发布辅助脚本
```

核心层不引用 WPF 或 Win32 句柄，平台相关的能力全部集中在 `NekoBridge.Windows` 中，后续增加其他平台时可以复用协议和状态管理部分。

## 参考

键鼠共享部分参考了 PowerToys 中 Mouse Without Borders 的公开实现，文件拖放使用 Windows Shell 的原生拖放接口。

- [Mouse Without Borders](https://github.com/microsoft/PowerToys/tree/main/src/modules/MouseWithoutBorders)
- [Windows Shell 拖放](https://learn.microsoft.com/en-us/windows/win32/shell/dragdrop)
- [.NET 发布与支持](https://github.com/dotnet/core)

第三方代码和素材的来源与许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
