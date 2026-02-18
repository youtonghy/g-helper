# 编译指南（Windows）
# 编译指南（Windows）

本项目是 Windows WinForms 应用，仅支持在 Windows 上编译和运行。

## 环境准备

- Windows 10/11
- .NET SDK 8.0（与项目 `net8.0-windows` 目标框架一致）
- （可选）Visual Studio 2022，安装“使用 .NET 的桌面开发”工作负载

## 命令行编译

在仓库根目录执行（以下指令均为 Bash）：

```Bash
dotnet restore app/GHelper.sln
dotnet build app/GHelper.sln -c Release -p:Platform=x64
```

输出位置：`app/bin/Release/net8.0-windows/`。

## 发布（单文件 exe）

需要生成可分发的单文件 exe 时：

```Bash
dotnet publish app/GHelper.csproj -c Release -p:Platform=x64 -p:PublishSingleFile=true -p:SelfContained=false
```

发布产物目录：`app/bin/Release/net8.0-windows/publish/`。

## 常见问题

- 在非 Windows 平台上编译会失败，因为 WinForms 仅支持 Windows。
- 如果运行时提示缺少权限，请以管理员身份运行生成的 `GHelper.exe`。


dotnet clean app/GHelper.csproj -c Release                       
dotnet publish app/GHelper.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./artifacts/publish/win-x64 
