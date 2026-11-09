<div align="center">

# 🎫 班级管理系统

**轻量 · 安全 · 开箱即用** 的积分卡生成与核销平台

[![License: GPL v3](https://img.shields.io/badge/License-GPL%20v3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0.en.html)
[![Python 3.9+](https://img.shields.io/badge/Python-3.9%2B-3776AB?logo=python&logoColor=white)](https://www.python.org/)
[![Docker](https://img.shields.io/badge/Docker-Ready-2496ED?logo=docker&logoColor=white)](https://www.docker.com/)
[![Docker Hub](https://img.shields.io/badge/Docker%20Hub-Repository-2496ED?logo=docker&logoColor=white)](https://hub.docker.com/r/wsxxstar/class-manager)
[![GitHub](https://img.shields.io/badge/GitHub-Repository-181717?logo=github&logoColor=white)](https://github.com/liang-rising-star/class-manager)

</div>

---

> [!IMPORTANT]
> 📦 **本项目即将归档**：后续开发将合并至 [class-manager](https://github.com/liang-rising-star/class-manager) 项目，本仓库将不再单独维护。
> 新的 Issue / PR 请提交到 [class-manager](https://github.com/liang-rising-star/class-manager/issues)。

---

## 📋 项目简介

班级管理系统是一款专为教育场景设计的积分卡管理工具，旨在解决传统积分卡容易伪造、难以管理的问题。

### 核心功能

- 🔐 **双重防伪** — 每张积分卡配备 10 位唯一编号 + SHA-256 哈希校验，伪造几乎不可能
- 📦 **批量生成** — 一键批量生成积分卡，自动导出排版精美的 A4 Word 文档
- 🔍 **快速核销** — 扫码枪扫入或手动输入编号核销，一次提交多个编码
- 🏷️ **Code128 条码** — 兼容所有标准条码扫描枪，即扫即核
- 🌐 **局域网可用** — 纯 HTTP 即可使用，无线网络下的手机、平板都能录入核销
- 🛡️ **盐值轮转** — 恢复出厂设置时自动重新生成加密盐，使所有旧卡失效
- 🚀 **零外部依赖** — 内置 SQLite 数据库，无需额外安装数据库服务
- 🐳 **Docker 一键部署** — 一行命令启动，数据自动持久化
- 🎨 **简洁中文界面** — 全中文响应式 UI，专注教育场景

## 🆕 3.0 有什么新东西

| 能力 | 说明 |
|---|---|
| 🖥️ **桌面托盘常驻** | 窗口关掉只是收进托盘，双击图标回来；托盘菜单可重启系统 / 退出 |
| 🎬 **启动动画** | 登录后可播放过场动画，支持「每次 / 每天一次 / 关闭」三档 |
| 🔔 **核销提示音** | 成功/失败各 12 / 8 种预设音效，可选自定义音频 |
| 🖨️ **静默直打** | 桌面版自己渲染 A4 并送打印机，不弹 WPS/Word |
| 🔄 **检查更新** | 侧边栏一键对比 GitHub Release，正式 / 体验双通道，支持一键下载安装 |
| 📦 **自建安装包** | 单文件 exe，自带 .NET 运行时与后端整套文件，装到普通目录即可用 |
| 🗑️ **独立卸载程序** | 安装目录里的 `uninstall.exe`，注册进系统「应用」列表，可选保留成绩数据 |
| 🧩 **开机自启同步** | 安装器勾选自启动后，设置页状态与注册表保持一致 |

## 🚀 快速开始

系统提供四种使用方式，按需选择：

| 方式 | 适合谁 |
|---|---|
| [安装包](#方式一安装包推荐windows) | Windows 单机使用（推荐） |
| [Docker](#方式二docker-部署) | 服务器 / NAS / 局域网多人使用 |
| [本机 Python](#方式三本地运行web-模式) | 开发调试，或不想装任何打包产物 |
| [源码运行桌面版](#方式四源码方式运行桌面版webview2托盘常驻) | 参与桌面端开发 |

### 方式一：安装包（推荐，Windows）

下载 `class-manager-setup.exe`（单文件，自带 .NET 运行时与后端），双击安装：

- **安装位置**：默认 `D:\class-manager`（建议别装进 `C:\Program Files`，那里没有写入权限）
- **可选组件**：开机自启 / 桌面快捷方式 / 开始菜单

装完目录结构：

```
D:\class-manager
├── class-manager.exe        # 启动器（双击它）
├── update.exe             # 更新程序（由「检查更新」自动拉起）
├── uninstall.exe          # 卸载程序（也注册进系统「应用」列表）
├── desktop\               # 桌面外壳（WebView2）
└── class-manager-system\    # 后端 + 内置 Python + data
```

> 只需要 WebView2 运行时（Win10/11 一般自带；缺失时程序会提示并给出官方下载链接）。
> 想装到别的盘也行，路径别含空格和中文以外的特殊字符即可。

### 方式二：Docker 部署

#### 镜像版本

| 版本标签 | 架构 | 说明 |
|:---:|:---:|:---|
| `latest` | x86 | 最新版本（当前 3.0） |
| `2.0` | x86 | 稳定版本 2.0 |
| `1.0` | x86 | 稳定版本 1.0 |
| - | ARM | 暂未构建（后续有计划） |

> 💡 **ARM 架构说明**：目前暂未构建 ARM 架构的镜像，后续有计划支持。如果需要在 ARM 设备上运行，请自行拉取代码构建。

#### 使用 Docker run

```bash
docker run -d --name class-manager -p 8000:8000 -v ./data:/app/data wsxxstar/class-manager:latest
```

#### 使用 Docker Compose

```yaml
services:
  class-manager:
    image: wsxxstar/class-manager:latest
    ports:
      - "8000:8000"
    volumes:
      - ./data:/app/data
    restart: unless-stopped
```

### 方式三：本地运行（Web 模式）

用本机 Python 直接跑后端，浏览器访问 `http://localhost:8000`：

```bash
cd class-manager-system

# 安装依赖
pip install -r requirements.txt

# 启动服务（默认 8000 端口，可用 --port 换端口）
python run.py
python run.py --port 8080

# 开发时可加热重载
python run.py --dev

# 恢复出厂设置
python run.py --reset
```

> ⚠️ 启动后会占用一个命令行窗口，**请勿关闭**，关闭后服务将停止运行。

Windows 用户也可以直接用打包好的安装程序（见「[方式一](#方式一安装包推荐windows)」），
不需要自己装 Python；旧版的 `启动.bat` / `run.exe` 已在 3.0 移除。

#### 使用软件包内置 Python 运行时

如果不想在本机装 Python，可以用 `class-manager-system/python/` 里自带的运行时：

```bash
cd class-manager-system

# 使用内置 Python 运行时并指定端口（例如 8080）
./python/python.exe run.py --port 8080
```

> ⚠️ **关于内置 Python 运行时**：`class-manager-system/python/`（约 430MB）不纳入版本管理。
> 从仓库克隆后，需要把 [Python 3.14 Windows embeddable 包](https://www.python.org/downloads/windows/)
> 解压到 `class-manager-system/python/`，再用 [get-pip.py](https://bootstrap.pypa.io/get-pip.py) 装好 pip
> 并执行 `python\python.exe -m pip install -r requirements.txt`；或者干脆用本机 Python 运行（见上文）。
> 用 PyInstaller / Inno Setup 打便携包时，这个目录必须存在。

### 方式四：源码方式运行桌面版（WebView2，托盘常驻）

`desktop/` 是一个 C# WinForms 外壳，用 **WebView2** 内嵌网页，并自动拉起内嵌 Python 后端（固定端口 **3445**）。

```powershell
# 需要 .NET SDK 8+ 与 WebView2 运行时（Win10/11 通常已自带）
cd desktop
dotnet build -c Release

# 直接运行（用 --root 指定项目根目录）
.\bin\Release\net8.0-windows\class-manager.exe --root "D:\class-manager"
```

> 💡 建议用 `build\package.ps1` 出完整包再运行 `dist\class-manager\class-manager.exe`：
> 直接在工作区/只读位置运行 exe 时，进程可能继承受限的完整性级别，
> WebView2 会因无法写入下载目录而提示“没有权限，请保存到文档文件夹”。
> 必要时可用管理员命令行修正：`icacls "desktop\bin" /setintegritylevel (OI)(CI)Medium /T`

用法：

- 双击 exe 直接进主界面，后端由它自己启动/停止，不用再开命令行窗口
- **单实例**：程序已在后台运行时再次双击 exe，不会开第二个进程，而是把已有窗口叫到前台
- 支持 `--tray` 参数：启动后只驻留托盘不弹窗（设置里的「开机自启」就是用这个参数登记的）
- 点窗口右上角 **×** 只是收进**系统托盘**，后台继续运行
- 托盘图标：**双击**打开主界面，**右键**菜单 → 打开主界面 / **重启系统** / **退出**
  - 「重启系统」是整体重启：托盘图标和窗口先消失，随后自动启动新进程（后端也一起重启），
    适合改了配置或代码后重新加载；重启后需要重新登录
- 程序会从自身目录向上查找 `class-manager-system\python\python.exe` 来定位项目根目录；
  也可以显式指定：`class-manager.exe --root "D:\class-manager"`，或设环境变量 `CLASS_MANAGER_ROOT`
- 导出文件走系统「另存为」对话框，自己选保存位置
- 运行时日志：`%LOCALAPPDATA%\ClassManager-Desktop\desktop.log`（后端启动失败时会写明原因）

> ⚠️ 桌面端只监听 `127.0.0.1:3445`，不对外网开放；需要局域网访问请用「[方式二](#方式二docker-部署)」的 Web 模式。
> 若双击后提示"后端服务未能启动"，多半是 exe 所在目录不可写（例如被安全软件或沙箱限制了写入），
> 把整个程序目录放到普通路径（如 `D:\class-manager`）或以管理员身份运行即可。

## 🔄 检查更新与自动更新

侧边栏「检查更新」会读取 GitHub 仓库的 Release 并与当前版本比对。

| 通道 | 数据来源 | 说明 |
|---|---|---|
| 正式 | `releases/latest` | 只认非预发布版本 |
| 体验版 | `releases` 列表第一条 | 弹窗里手动打开「加入体验测试版」后生效 |

更新流程：

```
检查到新版本 → 点「立即更新」
   → 从 Release 资产里找文件名含 update 且以 .zip 结尾的包
   → 下载到 class-manager-system\update\update.zip
   → 启动 update.exe，主程序退出
   → update.exe 等进程结束、备份 data、删旧目录、解压新包、还原 data、重启
```

也可以点「选择更新包…」直接导入本地 zip（会先复制到同一位置，再走上面的流程）。

> 发版要求：Release 里上传的 zip 文件名必须包含 `update`（例如 `class-manager-update-3.1.0.zip`），
> 解压后根目录应当是 `class-manager.exe` + `class-manager-system\` + `desktop\`。
> 后台每小时自动检查一次，且只用**正式通道**；体验版只在手动检查时生效。

## 🗑️ 卸载

- 系统「设置 → 应用 → 已安装的应用 → 班级管理系统 → 卸载」
- 或直接运行安装目录下的 `uninstall.exe`
- 或命令行 `uninstall.exe --silent`（无界面）

弹窗里可以勾选「保留成绩数据」：

| 选择 | 结果 |
|---|---|
| 勾选保留 | 只删程序文件与快捷方式，`class-manager-system\data` 保留 |
| 不勾选（默认） | 整个安装目录删除，包括数据库与备份 |

卸载会先结束主程序与后端 python，再清理快捷方式、开机自启、注册表，最后删目录。

## 🏗️ 构建与打包

```powershell
# 1. 应用本体 → dist\class-manager\（自包含 .NET + 内置 Python + 卸载/更新程序）
powershell -ExecutionPolicy Bypass -File build\package.ps1

# 2. 单文件安装包 → dist\class-manager-setup.exe
powershell -ExecutionPolicy Bypass -File build\make-setup.ps1
```

`build\make-setup.ps1` 会依次编译卸载程序（AOT 单文件）、更新程序（AOT 单文件），
把应用目录压成 `payload.zip` 内嵌进安装器，最终产出一个自带 .NET 运行时的单文件 exe。

> 打包脚本会自检桌面程序是否为「自包含」发布（`runtimeconfig` 里要有 `includedFrameworks`），
> 否则装到没装 .NET 的电脑上会提示缺少 .NET。

## ⚠️ 注意事项

### 服务启动
- 启动后会弹出命令行窗口，**请勿关闭**，关闭后服务将停止运行
- 服务默认运行在 `8000` 端口，请确保该端口未被占用（已被占用？请参考前文自定义端口方法）

### 访问方式
- 在本机浏览器中访问：`http://localhost:8000` 或 `http://127.0.0.1:8000`
- 在局域网其他设备上访问：`http://[此电脑的IP地址]:8000`
- 首次访问将引导设置管理员账号

### 数据持久化

所有数据都存储在 `data/` 目录中：

```
data/
├── config.json      # 系统配置
├── history.json     # 待处理批次（未打印/未保存的生成记录）
├── db/
│   └── scores.db    # SQLite 数据库
├── backups/         # 手动/定时备份的 zip
└── output/          # 导出的 Word 文档
```

> 💡 **配置文件丢失**：不用担心，系统会自动重建默认配置并重新引导管理员设置
> 💡 **数据备份**：建议定期备份 `data/` 目录，以防止数据丢失

## 🖼️ 界面预览

| 生成积分卡 | 核销积分卡 |
|:---:|:---:|
| 填写班级、标题、面额、数量 → 一键生成并保存 Word | 输入/扫描条码 → 实时显示核销结果与总额 |
| 3 列 × 7 行 排版，每张卡含条码 + 编号 | ✅ 核销成功 / ❌ 伪造编码 / ❌ 已核销 |

## 📖 使用说明

### 1️⃣ 初始化

首次访问系统时，需设置管理员用户名和密码。

如果是从 2.x 旧版本升级，可以先点「选择旧版安装目录并导入」：选中旧版程序的安装目录后，
系统会自动搜索其中的 `scores.db` 并导入全部积分记录（含已核销状态），导入完成后再创建管理员。
重复导入不会产生重复数据（已存在的编号会自动跳过）。

> 初始化完成之后也能导入：设置页「数据备份」卡片里有「导入 2.x 旧库」按钮（需登录）。
> 初始化阶段不需要登录，已初始化后则要求已登录，避免未授权写入。
>
> 桌面版会调用系统原生文件夹选择框（DPI 清晰）；网页版回退到服务端的 Tk 对话框。
> 取消选择时不会给出任何提示，只有路径真正无效时才会报错。

### 2️⃣ 生成积分

1. 登录后点击「生成积分」
2. 填写班级名称、积分卡标题、单张面额、生成数量
3. 点击生成 → 弹出另存为，选择保存位置
4. 打印文档，裁剪分发

生成后如果没打印也没保存就关掉页面，这一批不会丢：它会留在下方的「待处理批次」里
（数据存在 `data/history.json`），随时可以回来对它执行 **打印 / 保存 / 删除**。
打印或保存过的批次会自动从列表中消失，删除则会连同导出的 docx 一起清掉。

### 3️⃣ 核销积分

1. 点击「核销积分」
2. 输入积分卡编号（一行一个，或空格分隔）
3. 也可使用条码扫描枪直接扫描
4. 查看核销结果与总额统计

核销页有两个模式：

- **单个核销**（默认）——扫码枪扫一下会自动回车提交，核销成功变绿、失败变红并播放提示音，
  结果逐条向下累加，底部实时统计成功张数、失败张数与合计面额
- **批量核销** —— 一次粘贴或扫入多行编码，提交后统一校验

### 4️⃣ 系统设置

- **修改密码** — 在设置页面输入当前密码与新密码
- **数据备份** — 一键把 `config.json` + `scores.db` 打包成 zip 存到 `data/backups`；
  「立即备份」立刻生成一份，「打开备份文件夹」直接跳转到该目录
- **定时备份** — 选择每天的备份时间（如 02:30），到点自动把 `config.json` + `scores.db`
  打包到 `data/backups`；可设置最多保留份数，超出后自动删除最旧的一份；
  关掉「启用定时备份」开关即停止
- **打印** — 「直接打印」在桌面版里由程序自己渲染 A4 卡片页并静默打印（页面上有进度条弹窗
  显示 准备 → 渲染 → 发送 → 完成 四个阶段），不启动 WPS/Word；
  网页版仍回退到系统关联程序打印。下拉选择使用的打印机，默认「系统默认打印机」；改选别的打印机后
  会优先用 printto 直达该设备，失败时临时切换默认打印机再还原
- **桌面应用 → 开机自启** — 独立卡片，开关式滑块；开启后下次启动桌面版会把本程序登记到当前用户的
  启动项（带 `--tray` 参数，开机后只在托盘运行，双击图标才打开主界面）
- **清理** — 「已生成的导出文件」删除 `data/output` 里的 Word；「垃圾文件」删除 `__pycache__`、`*.pyc`、`*.tmp`；
  两类操作都会先弹窗显示数量与体积，确认后才执行
- **恢复出厂** — 弹窗内输入当前密码，清空所有数据并重置加密盐（⚠️ 旧积分卡将全部失效）

## 🛠️ 技术栈

| 层级 | 技术 |
|:---:|:---:|
| 后端 | FastAPI + Uvicorn |
| 数据库 | SQLite 3（内嵌） |
| 模板引擎 | Jinja2 |
| 条码生成 | python-barcode (Code128) + Pillow |
| 文档导出 | python-docx |
| 打印渲染 | pypdfium2（PDF → 位图，静默直打用） |
| 桌面外壳 | C# WinForms + WebView2 |
| 安装/更新/卸载 | .NET AOT 单文件（自建安装器） |
| 容器化 | Docker + Docker Compose |

## 📁 项目结构

```
launcher/                   # 原生 AOT 启动器（dist 根目录的 class-manager.exe）
├── Program.cs
└── class-manager.Launcher.csproj
installer/
├── ClassManager.Setup/       # 自建安装程序（WebView2 向导，输出单文件 exe）
├── ClassManager.Uninstall/   # 独立卸载程序（单文件，支持保留数据）
└── ClassManager.Update/      # 独立更新程序（AOT，校验后替换目录）
build/
├── package.ps1             # 打应用包
└── make-setup.ps1          # 打单文件安装包
class-manager-system/         # Web 后端与前端模板
├── main.py                 # 核心应用（FastAPI 路由 + 业务逻辑）
├── run.py                  # 启动入口（支持 --port / --dev / --reset）
├── reset.py                # 恢复出厂设置脚本
├── generate_manual.py      # 用户手册生成器
├── requirements.txt        # Python 依赖
├── Dockerfile              # Docker 镜像定义
├── docker-compose.yml      # Docker Compose 编排
├── entrypoint.sh           # 容器入口脚本
├── templates/              # Jinja2 HTML 模板
│   ├── _sidebar.html       # 公共左侧菜单（四个内页共用）
│   ├── _history.html       # 待处理批次片段（生成页引用）
│   ├── init_data_source.html # 初始化：全新开始 / 导入 2.x 旧库
│   ├── welcome.html        # 欢迎过场页
│   ├── boot.html           # 启动动画页
│   ├── init_admin.html     # 管理员初始化页
│   ├── login.html          # 登录页
│   ├── index.html          # 控制台（首页）
│   ├── generate.html       # 生成积分页
│   ├── verify.html         # 核销积分页
│   ├── setting.html        # 设置页
│   └── print_sheet.html    # 浏览器打印用的 A4 卡片页
├── static/
│   ├── css/app.css         # 统一设计系统（颜色/圆角/阴影变量 + 组件样式）
│   └── js/sound.js         # 核销提示音（Web Audio 播放）
├── python/                 # 内嵌 Python 运行时（不入库，见「方式三」说明）
└── data/                   # 运行时数据（自动生成）
    ├── config.json
    ├── history.json
    ├── db/scores.db
    ├── backups/            # 定时/手动备份的 zip
    └── output/
desktop/                    # 桌面外壳（C# WinForms + WebView2，托盘常驻）
├── ClassManager.Desktop.csproj
├── Program.cs              # 后端拉起 / WebView2 窗口 / 单实例
├── TrayMenu.cs             # 托盘图标与右键菜单
└── app.manifest
kiosk-agent/                # 一体机端（装在教室一体机上，托盘常驻）
├── ClassManager.KioskAgent.csproj
├── Program.cs              # 托盘启动 + 单实例
├── TrayApp.cs              # 托盘菜单（打开主界面 / 对接地址 / 开机自启）
├── MainForm.cs             # WebView2 主窗口
├── AgentServer.cs          # 本地 Web 服务 + 教师端 API
├── CameraService.cs        # 摄像头采集 → MJPEG
├── MicService.cs           # 麦克风采集 → WAV 流
├── RemoteExecService.cs    # 远程指令（白名单 + 超时）
├── AgentConfig.cs          # 配置 / 访问密码 / 访问密钥
└── wwwroot/                # 设置界面（HTML/CSS/JS）
```

## 🖥️ 班级设备（一体机端）

教室里的「一体机」装一个独立小程序，管理端即可远程查看画面、收听声音、叫号播报、按需执行指令。

**端口约定**：默认 **TCP 3457**（HTTP 接口）+ **UDP 3457**（视频流），两端都可自定义。
**视频走 UDP，其余接口走 TCP。**

### 三步接入

1. 一体机装好 `class-manager-kiosk.exe` → 托盘图标双击打开 → 设置访问密码
2. 主界面「设备信息」里复制 **局域网地址 + 访问密钥**（密钥自动生成，可一键重置）
3. 管理端「设置 → 班级设备」填 **IP、端口（默认 3457）、访问密钥**，先点「测试连接」再保存

### 管理端三个页面

| 页面 | 能力 |
|---|---|
| 班级监控 | 每台一体机一路实时画面（UDP 收帧、MJPEG 输出），在线状态 / 帧率 / 帧数；支持单间教室**喊话**（TTS）和**全屏**观看 |
| 班级叫号 | 输入姓名或学号 + 话术模板，向勾选的教室播报（调用一体机系统语音） |
| 一体机控制 | 开关摄像头 / 麦克风、授权远程执行、执行指令、测试播报 |

接口：`/api/speak`（TTS）、`/api/mic/stream`（WAV）、`/api/exec`（需授权 + 密钥），
视频用 UDP 订阅协议（`KVSUB` / `KVD1` 分片）。

- 启动后**只驻留系统托盘**，双击图标才打开设置界面
- 打包产物：`dist\kiosk-agent\`（自包含 .NET，丢到一体机上即可运行）

详见 [kiosk-agent/README.md](kiosk-agent/README.md)。

> 📌 打包待办：安装程序需要自动完成的配置（防火墙 3457/TCP+UDP、端口预置、开机自启、教师机 vs 服务器模式）
> 记录在 [docs/安装程序待办.md](docs/安装程序待办.md)。

## 🔒 安全设计

| 机制 | 说明 |
|:---|:---|
| 双重编码 | 10 位短编号（条码/人工录入）+ SHA-256 哈希（保留列，供后续签名校验）|
| 安装盐 | 每个实例独立加密盐，重置后旧卡全部失效 |
| 口令存储 | PBKDF2-SHA256（20 万轮加盐），旧版明文口令登录时自动升级 |
| 会话管理 | 64 字符随机 Session ID + HTTP-only Cookie，空闲 8 小时过期 |
| CSRF | 双提交令牌，所有 POST（含 `/update/download`）校验，缺失即 403 |
| 登录节流 | 同一 IP 连续 5 次失败锁定 5 分钟 |
| 核销防双花 | 条件更新（`WHERE status='未使用'`）+ rowcount 判定，同一张卡只能核销一次 |
| 文件保护 | 中间件拦截敏感路径；下载仅限导出目录内的 `.docx`，拒绝路径穿越 |

## 🗃️ 数据库结构

```sql
CREATE TABLE scores (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    class_name  TEXT    NOT NULL,          -- 班级名称
    title       TEXT    NOT NULL,          -- 积分卡标题
    amount      INTEGER NOT NULL,          -- 面额（分）
    code        TEXT    UNIQUE NOT NULL,   -- 10 位防伪编号
    hash        TEXT    UNIQUE NOT NULL,   -- SHA-256 防伪哈希
    issue_date  TEXT    NOT NULL,          -- 发放日期
    status      TEXT    DEFAULT '未使用',  -- 未使用 / 已作废
    verified_at TEXT                       -- 核销时间（成功核销时写入）
);
```

> 老库（无 `verified_at` 列）启动时自动 `ALTER TABLE` 补列，已有数据不受影响。

## ⚙️ 配置文件

`data/config.json`：

```json
{
  "system": {
    "name": "班级管理系统",
    "init_required": true,
    "encryption_salt": "<自动生成的32位hex>"
  },
  "paths": {
    "database": "./data/db/scores.db",
    "output": "./data/output"
  },
  "admin": {
    "username": "",
    "password": "<PBKDF2 哈希；旧版明文会在首次登录后自动升级>"
  }
}
```

> 打印版式（列数/行数/行高）是 `main.py` 顶部的 `CARD_*` 常量，不再放在配置里 ——
> 旧的 `printing` 段已废弃，保留也不会生效。

## 👤 作者

**liang-rising-star** — 项目发起与主要开发者

- 仓库：<https://github.com/liang-rising-star/class-manager>
- 问题反馈：<https://github.com/liang-rising-star/class-manager/issues>

## 📄 开源协议

本项目基于 **GNU General Public License v3.0** 发布，完整协议文本见仓库根目录的 [LICENSE](LICENSE) 文件。

- 你可以自由使用、修改、分发本程序，包括商业用途
- 分发修改版时必须同样以 GPL-3.0 开源，并提供完整源代码
- 本程序不含任何担保

协议原文：<https://www.gnu.org/licenses/gpl-3.0.html>

---

<div align="center">

**Made with ❤️ for Education**

</div>
