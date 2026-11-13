<p align="center">
  <img src="docs/assets/logo.png" width="150" alt="NekoBridge">
</p>

<h1 align="center">NekoBridge</h1>

<p align="center">在 LAN 内共享键盘和鼠标，让文件可以就这样拖动。</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-x64-1B2130?style=flat-square" alt="Windows x64">
  <img src="https://img.shields.io/badge/version-1.00-58790F?style=flat-square" alt="1.00">
</p>

---

## 关于 NekoBridge

桌上并排着好几台 PC，每一台都连着键盘和鼠标，这种情况经常有。NekoBridge 在把键盘和鼠标留在其中一台上的状态下，让其他的 PC 可以通过 LAN 被操作。鼠标从屏幕的边缘通过时，操作对象会切换，键盘的输入也会被送到那一边。

事先没有必要决定主机。连着键盘和鼠标的 PC 就那样成为操作侧，连接是双向的。从 A 操作 B 也可以，从 B 操作 A 也可以。

## 能做的事

### 键盘和鼠标的共享

把鼠标从屏幕左右的端向外移动，指针会移到旁边的 PC。排列顺序可以在设置里决定，所以只要配合实际的配置，就不会搞错方向。无论什么时候想回到自己的 PC，按 `Ctrl+Alt+Home`。

### 文件的拖放

在资源管理器里选中文件或文件夹，拖到屏幕的端，再运到另一方的 PC 的窗口里放下，那个内容会被复制。原来的文件不会消失。用 A 操作 B 的时候，也可以把 B 的文件直接拖到 C。

### 剪贴板的共享

文本、图片、文件可以就那样复制粘贴。文件在变得需要的时点从复制源的 PC 读出，所以事先没有必要转送。需要复制源的 PC 处于启动状态。

### 历史

用 `Alt+V` 可以打开历史。可以做搜索和按种类的筛选，经常使用的东西可以添加到收藏。历史被保存在本地的数据库里，所以即使重启也会留下。保存件数、快捷键、是否记录图片和文件，可以从设置变更。

### 连接与认证

初次连接时，输入对方画面上显示的一次性密钥。通信由 TLS 加密。不会发生从未配对的 PC 被操作的情况。连接断开时，因为把一直按着的键自动松开，所以输入不会一直僵住。

## 安装

从 [Releases](https://github.com/Marigold1122/Necobridge/releases/latest) 下载 `NekoBridge-1.00-win-x64.zip`，解压到任意的文件夹，执行 `NekoBridge.exe`。因为 .NET 运行时被同捆，所以没有必要另外安装。因为只有执行文件单体不会动作，请整个文件夹一起放置。

请让连接的 PC 全部使用相同的版本。更新的情况下，从任务托盘结束旧版本之后，启动新的。配对的信息就那样留下。

初次启动时 Windows 防火墙的确认出现的话，请允许专用网络。

## 连接步骤

以有 2 台 PC 的情况为例。左边 PC 上连着键盘和鼠标，想要操作右边的 PC。

在右边的 PC 打开 NekoBridge，选择「被控端」，按「开启配对」。地址和密钥会显示出来。这个信息只在 5 分钟之间有效。

在左边的 PC 选择「控制端」，输入显示出来的地址和密钥并连接。连接上了的话打开「屏幕排列」，把 2 台的卡片配合实际的位置重新排列。之后只要把鼠标移到右侧画面的端，就可以就那样操作。

第 3 台以后也是同样的步骤。从哪台 PC 开始连接都没有关系。

## 常驻与退出

即使关闭窗口也会留在任务托盘。右键图标的话，可以做共享的暂停、剪贴板历史的显示、结束。暂停中不进行输入的发送接收，被按着的键会被解放。

## 限制

- 只有 Windows x64。不对应 macOS。
- 1 台 PC 有多个显示器的情况下，它们被作为 1 个虚拟桌面处理。不对应任意的 2 维配置。
- 在 UAC 的安全桌面、登录画面、以管理员权限动作的窗口不能输入。这是由 OS 的机制造成的限制。
- 游戏、反作弊、独占输入的软件、远程桌面的嵌套等是动作保证的对象外。
- 符号链接、回收站、智能手机、压缩文件的内部等不是普通的文件，所以不会成为拖动的对象。
- 拖放使用 Windows 标准的机制。复制目标的软件在中途失败的情况下，有只写入了一部分文件的情况。请确认资源管理器上的结果。
- 在大的文件上，到复制目标做接收的准备为止要花时间。最初的 1 次会发生等待。
- 关于分辨率和显示比例不同的环境下的操作感，还在收集信息。有问题的话请在 issue 告知。如果能附上操作的步骤和 2 台的分辨率会很有帮助。

## 构建

需要 .NET 10 SDK。

```powershell
git clone https://github.com/Marigold1122/Necobridge.git
cd Necobridge

# 编译
.\scripts\build.ps1

# 生成同捆运行时的分发文件夹和压缩文件
.\scripts\publish.ps1
```

目录构成如下。

```
src/NekoBridge.Core      配对、协议、屏幕布局、输入状态。不依赖 UI 框架
src/NekoBridge.Windows   Windows 固有的实现和 WPF 的画面
tools/                   小规模的环回确认和发布用的脚本
```

Core 没有参照 WPF 或 Win32 的句柄。依赖 OS 的处理全部集中在 `NekoBridge.Windows` 里，所以在追加其他平台时，协议和状态管理的部分可以就那样使用。

## 参考

键盘和鼠标的共享参考了 PowerToys 的 Mouse Without Borders 的公开实现。文件的拖放使用了 Windows Shell 的标准功能。

- [Mouse Without Borders](https://github.com/microsoft/PowerToys/tree/main/src/modules/MouseWithoutBorders)
- [Windows Shell 拖放](https://learn.microsoft.com/en-us/windows/win32/shell/dragdrop)
- [.NET 的发布和支持](https://github.com/dotnet/core)

第三方代码和素材的出处、许可证请参照 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
