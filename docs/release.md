# 发版与在线更新（OTA）流程

## 一、一条命令打包

```powershell
# 在生产机 / 开发机项目根目录
powershell -ExecutionPolicy Bypass -File build\package.ps1
```

产出两个 zip（版本号自动从 `class-manager-system/main.py` 的 `APP_VERSION` 读取）：

| 文件 | 用途 |
|---|---|
| `dist\class-manager-v1.0-win-x64.zip` | 完整包，给别人手动安装 / 绿色版 |
| `dist\class-manager-update-v1.0-win-x64.zip` | **在线更新包**，发 Release 时上传这个 |

> 更新包和完整包内容一致，只是文件名必须带 `update` 且以 `.zip` 结尾，
> 客户端才会在 Release 资产里把它认出来。

需要安装程序时：

```powershell
powershell -ExecutionPolicy Bypass -File build\make-setup.ps1
# 产物：dist\class-manager-setup.exe（带 update.exe，支持自动更新）
```

## 二、发包前改版本号

```python
# class-manager-system/main.py
APP_VERSION = "1.1"        # ← 改成新版本
```

同时建议同步：

- `desktop/ClassManager.Desktop.csproj` 的 `<Version>`
- `kiosk-agent/ClassManager.KioskAgent.csproj` 的 `<Version>`
- `installer/ClassManager.Update/ClassManager.Update.csproj` 的 `<Version>`

## 三、发 GitHub Release

1. 提交代码并打 tag：

   ```bash
   git add -A
   git commit -m "feat: xxx（v1.1）"
   git tag -a v1.1 -m "班级管理系统 v1.1"
   git push origin main
   git push origin v1.1
   ```

2. 仓库 → **Releases → Draft a new release**
3. 选择 tag `v1.1`，标题填 `v1.1`
4. 上传 `dist\class-manager-update-v1.1-win-x64.zip`
5. 发布

发布后，客户端「检查更新」就能识别并下载。

## 四、客户端更新流程

```
检查更新   /update/check      → GitHub releases（正式版取 latest，体验版取最新一条）
下载       /update/download   → class-manager-system/update/update.zip（带进度）
立即更新   桌面端 ApplyUpdate  → 调 update.exe --dir <安装目录> --zip <包> --restart
更新程序                        → 等主程序退出 → 备份 data → 删旧的
                                 → 解压新包 → 回填 data → 重新启动
```

要点：

- **数据不会丢**：`class-manager-system/data` 先备份到临时目录，铺完新版本再放回去
- **更新程序位置**：安装版在根目录 `update.exe`；代码也兼容
  `class-manager-system\update\update.exe`
- **绿色版（直接解压 zip）没有 update.exe**，点「立即更新」会提示手动覆盖
- 更新包内必须有 `class-manager.exe`（启动器）和 `class-manager-system\`、`desktop\`

## 五、常见问题

| 现象 | 原因 |
|---|---|
| 提示「仓库还没有发布版本」 | GitHub 仓库没有 Release |
| 提示「这个版本没有提供更新包」 | Release 附件名不含 `update` 或不是 `.zip` |
| 提示「缺少 update.exe」 | 用的是绿色版，需改用安装程序或手动覆盖 |
| 检查到新版本但版本号没变 | `APP_VERSION` 没改，或 tag 与 `APP_VERSION` 不一致 |
