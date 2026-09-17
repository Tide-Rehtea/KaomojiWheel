# 颜文字轮盘

适用于 Windows 11 的后台颜文字剪贴板工具。按 `Ctrl + Shift + Z` 唤出轮盘，选择仓库和颜文字后复制到剪贴板，不会自动粘贴或自动发送。

## 功能

- 鼠标附近自适应轮盘布局与动态分页
- 自定义仓库和颜文字、本地 JSON 存储
- 仓库拖动排序、随机复制、托盘常驻
- 可配置快捷键、开机自启和减少动效
- GitHub Releases OTA：自动/手动检查、SHA-256 校验、备份、失败回滚和自动重启

## 构建

需要 .NET 8 SDK 和 Windows 11：

```powershell
dotnet restore source/KaomojiWheel/KaomojiWheel.csproj
dotnet build source/KaomojiWheel/KaomojiWheel.csproj -c Release
dotnet run --project source/KaomojiWheel.SmokeTests/KaomojiWheel.SmokeTests.csproj -c Release
```

生成正式发布包：

```powershell
./scripts/Build-Release.ps1 -Version 1.0.0
```

完整 OTA 架构和发版流程见 [docs/OTA-发布指南.md](docs/OTA-发布指南.md)。

## 数据与隐私

所有仓库、颜文字和设置都保存在程序旁的 `Data` 文件夹。程序不会上传这些内容。OTA 更新包禁止包含或覆盖 `Data`。

