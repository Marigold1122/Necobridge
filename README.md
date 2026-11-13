<p align="center">
  <img src="docs/assets/logo.png" width="150" alt="NekoBridge">
</p>

<h1 align="center">NekoBridge</h1>

<p align="center">在局域网内共用键盘和鼠标，文件可以直接拖过去。</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-x64-1B2130?style=flat-square" alt="Windows x64">
  <img src="https://img.shields.io/badge/version-1.00-58790F?style=flat-square" alt="1.00">
</p>

---

## 关于 NekoBridge

桌上并排摆着好几台电脑，每台各接一套键盘鼠标，这种情况并不少见。NekoBridge 的做法是让键鼠留在一台电脑上，其余电脑通过局域网接受操作。鼠标越过屏幕边缘时，操作对象随之切换，键盘输入也一并送过去。

事先不需要指定哪台是主机。插着键鼠的那台就是操作方，连接本身是双向的。从 A 操作 B 可以，从 B 操作 A 也可以。

## 功能

### 键鼠共用

把鼠标移出屏幕左右边缘，指针就会出现在相邻的电脑上。排列顺序可以在设置里指定，按照实际摆放调整好之后，方向就不会弄错。想回到自己电脑的时候，按 `Ctrl+Alt+Home`。

### 拖放文件

在资源管理器里选中文件或文件夹，拖到屏幕边缘，再拖进另一台电脑的窗口松手，内容就会复制过去。原来的文件不会消失。在用 A 操作 B 的时候，也可以把 B 上的文件直接拖到 C。

### 剪贴板共享

文字、图片、文件都可以直接复制粘贴。文件是在需要的时候才从来源电脑读取的，不必事先传一份过去。前提是来源电脑开着。

### 历史

按 `Alt+V` 打开历史记录，可以搜索，也可以按类型筛选，常用的还能加入收藏。记录保存在本地数据库里，重启之后依然在。保存条数、快捷键，以及是否记录图片和文件，都可以在设置里修改。

### 配对与认证

第一次连接时，输入对方屏幕上显示的一次性密钥。通信经过 TLS 加密。没有配对的电脑无法操作本机。连接中断时，按住的按键会被自动松开，不会卡在按下状态。

## 安装

从 [Releases](https://github.com/Marigold1122/Necobridge/releases/latest) 下载 `NekoBridge-1.00-win-x64.zip`，解压到任意文件夹，运行 `NekoBridge.exe`。.NET 运行时已经包含在压缩包里，不需要另外安装。单独一个可执行文件无法运行，请整个文件夹一起放。

参与连接的电脑请使用相同的版本。更新时先从系统托盘退出旧版本，再启动新版本，配对信息会保留下来。

第一次启动时如果 Windows 防火墙弹出提示，请允许专用网络。

## 连接步骤

以两台电脑为例。左边这台接着键盘鼠标，要操作右边那台。

在右边打开 NekoBridge，选择「被控端」，点击「开启配对」。屏幕上会显示地址和密钥，这组信息在五分钟内有效。

在左边选择「控制端」，填入刚才的地址和密钥，然后连接。连上之后打开「屏幕排列」，把两张卡片按实际位置排好。之后只要把鼠标移到右边屏幕的边缘，就可以直接操作了。

第三台以后也是同样的步骤。从哪台电脑发起连接都可以。

## 托盘与退出

关闭窗口后程序会留在系统托盘里。右键图标可以暂停共享、查看剪贴板历史，或者退出。暂停期间不接收也不发送输入，按住的按键会被松开。

## 限制

- 只有 Windows x64 版本，暂不支持 macOS。
- 一台电脑接多个显示器时，这些显示器会被当作一个虚拟桌面，不支持任意二维拼接。
- 无法在 UAC 安全桌面、登录界面以及以管理员权限运行的窗口中输入。这是系统机制的限制。
- 游戏、反作弊程序、独占输入的软件以及远程桌面嵌套等情况不在保证范围内。
- 符号链接、回收站、手机设备、压缩文件内部等不属于普通文件，不能作为拖放对象。
- 拖放使用 Windows 自带的功能。如果接收端的程序中途出错，可能已经写入了部分文件，请以资源管理器里的结果为准。
- 文件较大时，接收端需要先做准备，第一次会有等待。
- 分辨率和显示缩放不同的环境下，操作手感方面还在收集反馈。遇到问题欢迎提 issue，附上操作步骤和两台电脑的分辨率会比较容易定位。

## 构建

需要 .NET 10 SDK。

```powershell
git clone https://github.com/Marigold1122/Necobridge.git
cd Necobridge

# 编译
.\scripts\build.ps1

# 生成自带运行时的分发目录和压缩包
.\scripts\publish.ps1
```

目录结构如下。

```
src/NekoBridge.Core      配对、协议、屏幕布局、输入状态，不依赖 UI 框架
src/NekoBridge.Windows   Windows 平台的实现和 WPF 界面
tools/                   小范围回环检查与发布脚本
```

Core 不引用 WPF 或 Win32 句柄，与操作系统相关的处理都集中在 `NekoBridge.Windows` 里，将来增加其他平台时，协议和状态管理这部分可以照原样使用。

## 参考

键鼠共用部分参考了 PowerToys 中 Mouse Without Borders 的公开实现。文件拖放使用的是 Windows Shell 自带的功能。

- [Mouse Without Borders](https://github.com/microsoft/PowerToys/tree/main/src/modules/MouseWithoutBorders)
- [Windows Shell 拖放](https://learn.microsoft.com/en-us/windows/win32/shell/dragdrop)
- [.NET 的发布与支持](https://github.com/dotnet/core)

第三方代码和素材的来源、许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
