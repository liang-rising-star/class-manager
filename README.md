<div align="center">

# 🏫 班级管理系统

**积分卡 + 教室设备** 一体化的班级管理平台

[![License: GPL v3](https://img.shields.io/badge/License-GPL%20v3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0.en.html)
[![Python 3.9+](https://img.shields.io/badge/Python-3.9%2B-3776AB?logo=python&logoColor=white)](https://www.python.org/)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![GitHub](https://img.shields.io/badge/GitHub-Repository-181717?logo=github&logoColor=white)](https://github.com/liang-rising-star/class-manager)

</div>

---

## 📋 项目简介

班级管理系统面向中小学班主任和电教老师，解决两件事：**积分卡怎么发、怎么核**，以及**教室里的那台一体机怎么管**。

系统由两套程序组成：

| 程序 | 装在哪 | 干什么 |
|---|---|---|
| **教师端**（管理端） | 老师的电脑 | 生成/核销积分卡、管理班级设备、看监控画面、喊话叫号、远程控制一体机 |
| **一体机端**（班级设备） | 教室的一体机 | 托盘常驻，提供摄像头画面、屏幕直播、麦克风、语音播报、远程指令 |

两端通过**局域网**直连，不需要服务器、不需要公网、不依赖云服务。

---

## ✨ 能力总览

### 积分卡

- 🔐 **双重防伪** — 10 位唯一编号 + SHA-256 哈希，每台设备独立加盐
- 📦 **批量生成** — 一键生成并导出 A4 排版 Word，3 列 × 7 行裁剪即用
- 🏷️ **Code128 条码** — 兼容常见扫码枪，扫一下自动提交核销
- 🔍 **防重复核销** — 条件更新 + rowcount 判定，同一张卡只能核一次
- 🛡️ **盐值轮转** — 恢复出厂时重新生成加密盐，所有旧卡立即失效

### 班级设备

- 🖥️ **设备管理** — 填 IP + 访问密钥，或用「一键配置码」自动填好全部参数
- 📹 **班级监控** — 每台一体机一路实时画面（UDP 传输），支持全屏查看
- 🎙️ **双向对讲** — 麦克风图标开始对讲：老师说话进教室音箱，同时能听到教室声音
- 🔈 **收听开关** — 喇叭图标单独控制是否收听教室麦克风
- 📣 **班级叫号** — 选教室 → 输姓名/学号 → 选话术 → 一体机用系统语音播报
- 🖥️ **一体机控制** — 查看运行状态、屏幕直播、执行远程指令（需本机授权）

---

## 🏗️ 架构

```
┌──────────────────────────────┐        ┌──────────────────────────┐
│  教师端（老师电脑）            │        │  一体机端（教室一体机）     │
│                              │  TCP   │                          │
│  class-manager.exe           │ 3457   │  class-manager-kiosk.exe │
│  ├─ WinForms + WebView2 外壳  │ ─────▶ │  ├─ 托盘常驻，HTTP 服务    │
│  └─ 本地后端 127.0.0.1:3445   │ ◀───── │  ├─ 摄像头 / 屏幕 / 麦克风 │
│                              │  UDP   │  ├─ 语音播报（SAPI）       │
│  页面：积分卡 / 监控 / 叫号   │ 3457   │  └─ 远程指令（白名单）     │
└──────────────────────────────┘        └──────────────────────────┘
                                   视频帧（UDP 分片推流）
```

- **页面永远在本机**：教师端窗口加载的是 `http://127.0.0.1:3445`，浏览器按「安全上下文」处理，对讲的麦克风权限不会被拦
- **视频走 UDP**：摄像头帧分片推送，走 TCP 只会浪费带宽
- **其余接口走 TCP**：状态、设置、播报、指令、音频

---

## 🚀 快速开始

### 一、教师端

```powershell
# 源码方式（需要 .NET 8 SDK + WebView2 运行时）
cd desktop
dotnet build -c Release
.\bin\Release\net8.0-windows\class-manager.exe --root "D:\程序项目\【P】class-manager"
```

程序自带后端，只监听 `127.0.0.1:3445`，不对外暴露。关窗口只是收进托盘。

### 二、一体机端

```powershell
cd kiosk-agent
dotnet publish -c Release -r win-x64 --self-contained true -o ..\dist\kiosk-agent
# 把 dist\kiosk-agent 整个目录拷到一体机上，双击 class-manager-kiosk.exe
```

首次运行：托盘图标双击 → 设置访问密码 → 填教室名称 → 选摄像头。
装到**普通目录**（如 `D:\class-manager-kiosk`），不要装进需要提权的位置。

### 三、把一体机接进教师端

1. 一体机端主界面 →「设备信息」里复制**局域网地址 + 访问密钥**，或点「生成配置码」
2. 教师端 →「设置 → 班级设备 → 添加设备」→ 粘贴配置码，自动填好 IP / 端口 / 密钥
3. 点「测试连接」，显示「连接成功 / 密钥校验通过」即可
4. 打开「班级监控」，卡片变成**在线**、有画面就通了

### 四、端口与防火墙

| 端口 | 协议 | 在哪台机器 | 用途 |
|---|---|---|---|
| 3445 | TCP | 教师端 | 桌面版内置后端（仅本机） |
| 3457 | TCP | 一体机端 | HTTP 接口（状态 / 播报 / 指令 / 音频） |
| 3457 | UDP | 一体机端 | 摄像头视频流 |

一体机必须放行入站 **TCP + UDP 3457**（首次启动弹窗点「允许」，或手动加防火墙规则）。

---

## 🖥️ 使用说明

### 积分卡

1. 「生成积分」→ 填班级、标题、面额、数量 → 导出 Word → 打印裁剪
2. 没打印的批次会留在「待处理批次」里，可随时回来打印/保存/删除
3. 「核销积分」→ 扫码枪扫入或手输编号，支持单个（扫一下自动提交）和批量两种模式

### 班级监控

- 每台设备一张卡片：实时画面、在线状态、帧率、已收帧数
- 🎙️ **麦克风图标**：开始对讲（变红闪烁），老师说话进教室音箱，同时自动开启收听
- 🔈 **喇叭图标**：单独开关「听教室声音」
- ⛶ **全屏图标**：放大画面，全屏下也能对讲

### 班级叫号

顶部下拉选教室 → 输入姓名/学号 → 选话术（请到讲台 / 准备回答 / 交作业 / 家长接送）
→ 调语速 → 「开始叫号」，一体机用系统语音播报。

### 一体机控制

顶部下拉选设备，下面依次是：

- **运行状态**：设备名、摄像头、麦克风、远程执行授权、UDP 视频帧率
- **屏幕直播**：一体机桌面实时画面
- **远程指令**：输入命令执行（需在一体机本机授权远程执行）

> ⚠️ 摄像头开关、麦克风开关、远程执行授权 **只能在一体机本机操作**，
> 管理端不提供远程修改入口（接口层也已拦截）。

---

## 🔒 安全设计

| 机制 | 说明 |
|---|---|
| 管理员口令 | PBKDF2-SHA256（20 万轮加盐），旧版明文登录时自动升级 |
| 会话 | 64 字符随机 Session ID + HttpOnly Cookie，空闲 8 小时过期 |
| CSRF | 双提交令牌，所有 POST 校验（含二进制上传走请求头） |
| 登录节流 | 同一 IP 连续 5 次失败锁定 5 分钟 |
| 一体机访问密钥 | 32 位随机密钥，机器对接用，可一键重置 |
| 一体机访问密码 | 打开设置界面要密码；关窗口即退出登录，重新打开需再登录 |
| 远程执行 | 默认关闭，必须在本机授权；支持命令白名单 + 超时强杀 |
| 核销防双花 | 条件更新（`WHERE status='未使用'`）+ rowcount 判定 |
| 文件保护 | 中间件拦截敏感路径；下载仅限导出目录内的 `.docx` |
| 配置完整性 | 启动时校验 `data` 下配置/设备/批次的 SHA-256（签名密钥由 Windows DPAPI 封装），被外部改动会在设置页提示并记入 `data/audit.log` |
| 目录权限 | 启动时把 `data` 目录 ACL 收紧为「当前用户 / SYSTEM / Administrators」，去掉 `Authenticated Users` 的写权限 |
| 操作日志 | 登录、登录失败、改口令、导入旧数据、检测到配置被改都会写 `data/audit.log`，设置页可查看 |

### ⚠️ 使用提醒：请保护好自己的电脑

本项目是**本地运行的开源软件**，数据就放在本机的 `data` 目录里，安全性最终取决于**这台电脑本身的安全**。

- **务必给 Windows 账号设置开机密码**，不要与他人共用同一个账号
- 离开座位请锁屏（`Win + L`），不要让电脑长时间处于已登录的无人状态
- 管理员口令建议 **10 位以上、大小写+数字+符号混合**；短口令在拿到配置文件后可被离线暴力破解
- 多人共用一台电脑时，建议给每位老师单独一个 Windows 账号（`data` 目录权限会自动收紧到当前账号）
- 别人若以**你的账号**运行程序，仍可能改动数据；系统会在下次启动时提示「配置文件被外部修改」，并可查看操作日志追溯

源码完全公开，任何人都能看到实现细节；但没有这台电脑的登录权限，就无法改动本机数据。**保护好自己的电脑，就是保护数据。**

---

## 📁 目录结构

```
class-manager-system/       # Web 后端与前端页面
├── main.py                 # FastAPI 路由 + 业务逻辑
├── devices.py              # 班级设备对接（UDP 收帧、HTTP 客户端）
├── run.py / reset.py       # 启动 / 恢复出厂
├── Dockerfile / docker-compose.yml
├── templates/              # Jinja2 页面（积分卡 / 监控 / 叫号 / 控制 / 设置）
├── static/                 # 样式与脚本
└── data/                   # 运行时数据（不入库）

desktop/                    # 教师端桌面壳（WinForms + WebView2）
kiosk-agent/                # 一体机端（托盘常驻）
├── AgentServer.cs          # 本地 HTTP 服务 + 接口
├── CameraService.cs        # 摄像头 → JPEG
├── ScreenService.cs        # 屏幕直播（按需采集）
├── MicService.cs           # 麦克风采集
├── AudioPlaybackService.cs # 对讲播放
├── SpeakService.cs         # SAPI 语音播报
├── RemoteExecService.cs    # 远程指令（白名单 + 超时）
├── TrayMenu.cs / PasswordDialog.cs  # 托盘菜单与退出校验
└── wwwroot/                # 设置界面

launcher/                   # 原生启动器
installer/                  # 安装 / 卸载 / 更新程序
build/                      # 打包脚本
docs/安装程序待办.md         # 打包时必须自动配置的事项
```

---

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
    verified_at TEXT                       -- 核销时间
);
```

---

## 📦 构建与打包

```powershell
# 教师端应用包 → dist\class-manager\
powershell -ExecutionPolicy Bypass -File build\package.ps1

# 单文件安装包 → dist\class-manager-setup.exe
powershell -ExecutionPolicy Bypass -File build\make-setup.ps1

# 一体机端自包含包 → dist\kiosk-agent\
cd kiosk-agent
dotnet publish -c Release -r win-x64 --self-contained true -o ..\dist\kiosk-agent
```

> 安装程序需要自动完成的配置（防火墙规则、端口预置、开机自启、安装目录）记录在
> [docs/安装程序待办.md](docs/安装程序待办.md)。

---

## 🐳 可选：Docker 部署（仅 Web 页面）

不装桌面壳、只用浏览器打开网页时：

```bash
docker run -d --name class-manager -p 8000:8000 -v ./data:/app/data wsxxstar/class-manager:latest
```

> 注意：通过 `http://<局域网IP>:8000` 访问时浏览器会禁用麦克风，
> **对讲功能不可用**；需要用对讲请装教师端桌面版。

---

## ❓ 常见问题

**一体机重启后监控没画面？**
已修复：管理端的 UDP 收帧线程不会再因 ICMP 不可达退出，一体机回来后几秒内自动恢复。

**画面卡但语音正常？**
UDP 视频在弱网/无线下容易丢片，优先让一体机走有线；帧率可在 1~30 调整。

**对讲点不动 / 提示麦克风不可用？**
请用教师端桌面版（页面是 127.0.0.1）。用浏览器打开非 https 的局域网地址时，浏览器禁止采集麦克风。

**一体机端关掉窗口后还要重新输密码？**
这是设计如此：关窗口即退出登录。

**一体机端连不上？**
检查三件事：同一局域网（含 AP 隔离）、防火墙放行 TCP+UDP 3457、IP 是否固定。

---

## 📄 开源协议

基于 **GNU General Public License v3.0** 发布，完整文本见 [LICENSE](LICENSE)。

- 可自由使用、修改、分发，包括商业用途
- 分发修改版时须同样以 GPL-3.0 开源并提供完整源码
- 本程序不含任何担保

---

<div align="center">

**Made with ❤️ for Education**

</div>
