# 一体机端（Kiosk Agent）

装在每个教室「一体机」上的独立小软件。教师端（班级管理系统）通过局域网连接它，
获取摄像头画面、收听麦克风、并在授权后远程执行指令。

- 启动后**只在系统托盘**运行，不弹窗
- 默认 **TCP 3457**（HTTP 接口）+ **UDP 3457**（视频流）；端口可在主界面里改
- 视频走 **UDP**，其余接口走 **TCP**
- 双击托盘图标 → 打开主界面（WebView2 里跑本地 Web 页面）
- 主界面里可以：设访问密码、选摄像头、开关/选择麦克风、授权远程执行、查看/重置访问密钥
- 关闭窗口只是收进托盘，后台继续服务

## 目录

```
kiosk-agent/
├── ClassManager.KioskAgent.csproj   # 主工程（WinForms + Kestrel + WebView2）
├── Program.cs                       # 入口：单实例 + 托盘启动
├── TrayApp.cs                       # 托盘图标与右键菜单
├── MainForm.cs                      # WebView2 主窗口
├── AgentServer.cs                   # 本地 Web 服务 + 教师端 API
├── CameraService.cs                 # DirectShow 采集 → JPEG（MJPEG）
├── MicService.cs                    # NAudio 采集 → 16k/16bit 单声道 PCM
├── RemoteExecService.cs             # 远程指令执行（白名单 + 超时）
├── AgentConfig.cs                   # 配置持久化 + 口令/密钥
└── wwwroot/                         # 设置的 Web 界面（原生 HTML/CSS/JS）
```

## 运行与部署

```powershell
cd kiosk-agent
dotnet build -c Release
# 直接把 bin\Release\net8.0-windows 整个目录拷到一体机上运行：
class-manager-kiosk.exe
```

依赖：.NET 8 Desktop Runtime、WebView2 Runtime（Win10/11 一般自带）。

> ⚠️ **不要从受限目录运行**：从沙箱/受限完整性级别目录（部分安全软件保护目录）启动时，
> Windows 会拒绝摄像头访问，表现是「摄像头启动失败 / 无画面」。
> 装到 `D:\class-manager-kiosk` 这类普通目录即可。

配置与日志位置：`%LOCALAPPDATA%\ClassManager-KioskAgent\`
- `config.json`：全部设置（含访问密钥、口令哈希）
- `agent.log`：运行日志

## 首次使用

1. 双击 `class-manager-kiosk.exe`，右下角托盘出现图标（会有气泡提示访问地址）
2. 双击托盘 → 主界面 → 设置**访问密码**（至少 4 位）
3. 填「教室 / 班级名称」，方便教师端区分
4. 选摄像头 → 应用，下方能看到实时画面
5. 需要让教师端控制这台机器时，再打开「授权远程执行」

## 教师端对接

| 用途 | 接口 | 鉴权 |
|---|---|---|
| 设备发现 | `GET /api/info` | 免鉴权 |
| 状态 | `GET /api/state` | 会话或密钥 |
| 摄像头列表 | `GET /api/camera/devices` | 会话或密钥 |
| 单帧截图 | `GET /api/camera/snapshot` | 会话或密钥 |
| 实时画面 | `GET /api/camera/stream`（MJPEG） | 会话或密钥 |
| 麦克风电平 | `GET /api/mic/level` | 会话或密钥 |
| 实时音频 | `GET /api/mic/stream`（WAV 流） | 会话或密钥 |
| 视频（UDP） | UDP 端口发 `KVSUB <密钥>` | 密钥 |
| 语音播报 | `POST /api/speak` `{text, rate}` | 会话或密钥 |
| 停止播报 | `POST /api/speak/stop` | 会话或密钥 |
| 远程执行 | `POST /api/exec` | **仅密钥** + 开关打开 |

鉴权方式二选一：
- 机器对接：请求头 `X-Access-Key: <访问密钥>`
- 浏览器：`POST /api/login` 拿会话 Cookie

示例：

```bash
# 发现设备
curl http://192.168.1.50:3457/api/info

# 取一帧
curl -H "X-Access-Key: 0d006dcc-..." http://192.168.1.50:3457/api/camera/snapshot -o snap.jpg

# 拉实时画面（MJPEG，浏览器/播放器可直接打开）
curl -H "X-Access-Key: 0d006dcc-..." http://192.168.1.50:3457/api/camera/stream

# 远程执行（需先在主界面打开「授权远程执行」）
curl -X POST -H "X-Access-Key: 0d006dcc-..." -H "Content-Type: application/json" \
     -d '{"command":"ipconfig"}' http://192.168.1.50:3457/api/exec
```

## 视频 UDP 协议

摄像头帧不走 HTTP，而是 UDP 推给订阅方（管理端），带宽更省、延迟更低：

```
管理端 → 一体机端（UDP 3457）
    KVSUB <访问密钥>     订阅（相当于心跳，建议每 2 秒发一次）
    KVPING <访问密钥>    续订
    KVBYE <访问密钥>     退订

一体机端 → 管理端（UDP，每片 16 字节头 + 载荷 ≤1300 字节）
    magic "KVD1" | frameId(u32) | fragIndex(u16) | fragCount(u16) | frameLen(u32) | payload
```

按 `frameId` 收齐 `fragCount` 片后拼接即为完整 JPEG。

## 安全设计

| 机制 | 说明 |
|---|---|
| 访问密码 | PBKDF2-SHA256（10 万轮加盐），保护主界面 |
| 访问密钥 | 32 位随机密钥，机器对接用，可在界面一键重置 |
| 远程执行 | 默认关闭；开启后仍需密钥；支持命令前缀白名单 + 超时强杀 |
| 会话 | 64 位随机 Session ID，HttpOnly Cookie，8 小时过期 |
| 音频/视频 | 未启用时接口直接 403，不泄漏采集状态 |

## 托盘菜单

- 打开主界面 / 在浏览器中打开
- 复制教师端对接地址（含密钥）
- 开机自启（托盘）：写入 `HKCU\...\Run`，带 `--tray` 参数
- 重启服务 / 退出

## 已知限制

- 只做了 Windows（DirectShow 摄像头 + WASAPI/WinMM 麦克风）
- 摄像头画面为 MJPEG，局域网用足够；跨公网请自行加隧道
- 远程执行用 `cmd.exe /c`，输出超过 10 万字符会截断
