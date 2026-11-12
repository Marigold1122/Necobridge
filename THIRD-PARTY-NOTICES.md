# 第三方与素材说明

## 开发参考

本项目的 Windows 输入捕获和源端拖放捕获方案参考 Microsoft PowerToys Mouse Without Borders 的公开实现及 Windows 官方文档，并非将整个 PowerToys 作为运行依赖。

- https://github.com/microsoft/PowerToys/tree/main/src/modules/MouseWithoutBorders
- https://github.com/microsoft/PowerToys/blob/main/LICENSE
- https://learn.microsoft.com/en-us/windows/win32/shell/dragdrop
- https://learn.microsoft.com/en-us/windows/win32/shell/clipboard

v0.2.3 输入移动适配 PowerToys commit `f5f29e08b39cc7139ccbe4a2ec4eb4841779bc45` 的 `InputHook.cs` 与 `Event.cs` 默认绝对归一化路径：系统处理后的钩子位移，分别按源端宽高映射；不是 RawInput 计数的自拟倍率。

剪贴板 UI 线程应用与有限重试流程参考 `Clipboard.cs`（blob `9e5341788666585835a92e0c2c3e04641d3cb5c5`）和 `IpcChannelHelper.cs`（blob `7e6bfd0217532f637053656d93820bd2b5db99e7`）。本工程使用非阻塞 Dispatcher、消息 ID 和剪贴板序列消除回环，并非整套上游实现直接移植。

### Microsoft PowerToys — MIT License

Copyright (c) Microsoft Corporation. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

## 运行依赖

Windows 发布包包含 .NET / Windows Desktop 运行时及其许可证通知。不得移除发布工具生成的第三方通知和运行时文件。

## 持久历史参考及数据库

持久历史参考 EcoPaste 的实际 SQLite schema、图片独立存储与清理设计，未复制 Rust/Tauri 源码、未嵌入其运行时。EcoPaste 项目采用 Apache-2.0：https://github.com/EcoPasteHub/EcoPaste 。本项目独立 WPF 历史模块使用 Microsoft.Data.Sqlite 10.0.15（MIT）及其 SQLitePCLRaw/SQLite 原生依赖，分发包保留其运行依赖，许可可查 https://github.com/dotnet/efcore/blob/main/LICENSE.txt 与 https://www.sqlite.org/copyright.html 。

## 插画

壁纸由用户提供用于本地界面设计和测试。未确认公开分发授权前，不将包含该插画的包公开发布或商用分发。图像不由 NekoBridge 声称原创。

## 设计协作

界面视觉由用户指定的 claude-opus-5-5 经指定服务参与设计，设计结果保存在 design/。模型仅用于开发阶段；应用不包含 API 密钥、模型 SDK 或 AI 调用功能。
