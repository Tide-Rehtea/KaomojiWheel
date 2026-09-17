# 颜文字轮盘 OTA 与正式发布指南

## 1. 整体结构

正式源码保存在 GitHub 仓库，正式二进制文件保存在 GitHub Releases。应用固定读取：

`https://github.com/Tide-Rehtea/KaomojiWheel/releases/latest/download/latest.json`

每个 Release 包含：

- `KaomojiWheel-vX.Y.Z-win-x64.zip`：自包含 Windows x64 程序；
- `latest.json`：版本、下载地址、SHA-256、发布页和更新说明；
- GitHub 自动生成的 Release Notes。

## 2. 客户端检查流程

1. 用户可以在“设置 → 软件更新”中手动检查。
2. 开启自动检查后，程序启动时检查一次，常驻期间每小时判断一次，但实际联网间隔不少于 24 小时。
3. 客户端通过 HTTPS 下载 `latest.json`，验证版本、HTTPS 地址和 SHA-256 格式。
4. 只有远端语义版本高于当前版本时才显示“下载并安装”。
5. 更新永远由用户确认，不强制安装。

## 3. 下载与校验

1. 更新包先写入 `%LocalAppData%/KaomojiWheel/Updates/<version>` 下的临时文件。
2. 下载完成后计算整个 ZIP 的 SHA-256。
3. 计算结果必须与 `latest.json` 完全一致，否则立即停止，不启动安装器。
4. 更新包中的路径必须位于目标目录内，绝对路径和 `..` 越界路径会被拒绝。
5. 更新包或安装清单只要包含 `Data`，安装器就会拒绝执行。

SHA-256 能验证下载完整性，并确保包与 GitHub Release 清单一致。它不等同于 Windows Authenticode 代码签名；如果未来面向大范围公开分发，建议另行购买代码签名证书并在 CI 中签署 EXE。

## 4. 安装、备份和回滚

主程序无法安全覆盖正在运行的自己，因此会复制一份当前单文件 EXE 到临时目录，并以 `--apply-update` 模式启动。临时更新进程会：

1. 等待原进程退出，最长 30 秒；
2. 安全解压 ZIP 到临时 staging 目录；
3. 读取新旧 `installed-files.json`；
4. 将会受影响的旧程序文件备份到 `%LocalAppData%/KaomojiWheel/Backups`；
5. 使用临时文件逐项覆盖，删除新版本不再需要的旧组件；
6. 任意一步失败时删除半成品并恢复备份；
7. 成功或回滚后重新启动 `KaomojiWheel.exe`；
8. 将结果写入 `%LocalAppData%/KaomojiWheel/Updates/updater.log`。

`Data` 永远不在安装文件清单内，因此仓库、颜文字和用户设置会跨版本保留。

## 5. 本地生成正式包

项目版本写在 `source/KaomojiWheel/KaomojiWheel.csproj` 的 `<Version>` 中。先更新版本，再运行：

```powershell
./scripts/Build-Release.ps1 -Version 1.0.1
```

脚本会恢复依赖、Release 构建、运行冒烟测试、发布自包含程序、生成 `installed-files.json`、打 ZIP、计算 SHA-256，并生成 `latest.json`。默认输出目录为 `dist/v1.0.1`。

## 6. GitHub 自动发布

`.github/workflows/release.yml` 监听 `v*.*.*` 标签。推荐流程：

```powershell
git status
git add .
git commit -m "Release v1.0.1"
git push origin main
git tag v1.0.1
git push origin v1.0.1
```

标签推送后，GitHub Actions 会执行与本地相同的发布脚本，并创建 Release。不要重复使用或移动已经发布的版本标签；有修复就递增补丁版本，例如从 `1.0.1` 改为 `1.0.2`。

## 7. 版本策略

使用语义化版本 `主版本.次版本.补丁版本`：

- 修复问题：`1.0.0 → 1.0.1`
- 兼容的新功能：`1.0.1 → 1.1.0`
- 不兼容的数据或交互变化：`1.x → 2.0.0`

Git 标签带 `v`，应用和清单版本不带 `v`。

## 8. 正式目录与旧开发包

`portable-v1`、`portable-v2` 等是开发期快照，不属于正式发布结构。正式状态只保留：

- GitHub 仓库中的源码、工作流和文档；
- GitHub Releases 中按版本保存的 ZIP；
- 本地 `dist/vX.Y.Z` 中最近生成的正式包；
- 一个临时归档目录，用于短期保留旧开发快照，确认正式版稳定后可删除。

