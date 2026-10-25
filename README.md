# DPL (Diamond Pickaxe Launcher) 1.0.0

DPL 是一个基于 .NET 8 和 Avalonia UI 开发的轻量级、跨平台 Minecraft 启动器。项目用 MVVM 架构，核心启动逻辑基于CmlLib.Core

## 功能特性

- 本地版本管理：支持自动扫描并启动本地已安装的Minecraft实例
- 原版游戏下载：支持从Mojang官方源及镜像源双源稳定下载游戏
- 模组加载器：支持主流Mod加载器的自动下载与安装
- 资源获取：支持从 Modrinth 和 CurseForge 搜索并下载模组与资源包，内置中文搜索支持
- 多主题支持：内置多种主题。
- 账号管理：支持微软正版登录与离线账号模式。

## 系统要求

- 操作系统：Windows 10/11, macOS 10.15+, 主流 Linux 发行版。
- 运行环境：如果使用从源码编译的版本的话得装一下[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## 使用方法:
### Windows

1. 从 Releases 页面下载最新的 `DPL-win-x64.zip` 压缩包
2. 将压缩包解压到任意非系统保护目录
3. 双击运行 `DPL.exe` 即可启动


### macOS

由于本项目目前没有Apple开发者签名 macOS系统的Gatekeeper会阻止应用直接运行 请按照以下步骤操作：

1. 从 Releases 页面下载 `DPL-osx-arm64.zip` 或 `DPL-osx-x64.zip`
2. 解压下载的 zip 文件
3. 打开 macOS 自带的Terminal应用
4. cd到启动器目录
5. 依次运行以下命令赋予执行权限并解除系统隔离:
   ```bash
   chmod +x DPL
   xattr -cr .
   codesign --force --deep --sign - ./DPL
6. ./DPL启动 即可


### Linux

由于 Linux 发行版众多且桌面环境复杂 一般情况下系统不会允许直接双击运行非包管理器安装的二进制文件 请按照以下步骤操作。

1. 确认已安装必要依赖

由于 DPL 使用 Avalonia 渲染图形界面 在运行前请确保系统已安装必要的图形库依赖:

2.解压并运行程序

1. 从 Releases 页面下载最新的 DPL-linux-x64.zip 压缩包并解压
2. 打开终端 进入解压后的目录
3. 赋予程序执行权限并启动
   ```bash
   chmod +x DPL
   ./DPL
