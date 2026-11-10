# 班级管理系统 —— 班级积分卡生成与核销
# Copyright (C) 2026 liang-rising-star
#
# 本程序为自由软件：你可以依据 GNU 通用公共许可证（GPL）第 3 版
# 或（由你选择）任何更新版本发布，重新分发和/或修改本程序。
# 本程序不含任何担保，详见仓库根目录的 LICENSE 文件。

from fastapi import FastAPI, Request, Form, HTTPException, Response
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse, FileResponse, StreamingResponse
from fastapi.staticfiles import StaticFiles
from jinja2 import Environment, FileSystemLoader
import sqlite3
import json
import base64
import hashlib
import datetime
import os
import io
import csv
import re
import time
import threading
import zipfile
import random
import secrets
import urllib.parse
import uuid
import devices
import integrity
import string
import barcode
from docx import Document
from docx.shared import Cm, Pt
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.oxml.ns import qn
from lxml import etree

app = FastAPI()

# 获取当前文件所在目录
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
APP_VERSION = "1.0"

# 配置静态文件
app.mount("/static", StaticFiles(directory=os.path.join(BASE_DIR, "static")), name="static")

# 提供logo.ico文件访问
UPDATE_MARK_PATH = os.path.join(BASE_DIR, "update", "last-update.json")


@app.middleware("http")
async def inject_update_done_toast(request, call_next):
    """刚更新完启动时，在页面上弹一次「安装完毕」提示。"""
    response = await call_next(request)

    ctype = response.headers.get("content-type", "")
    if "text/html" not in ctype:
        return response

    info = None
    try:
        if os.path.exists(UPDATE_MARK_PATH):
            if time.time() - os.path.getmtime(UPDATE_MARK_PATH) < 900:      # 15 分钟内有效
                with open(UPDATE_MARK_PATH, "r", encoding="utf-8") as f:
                    info = json.load(f)
            os.remove(UPDATE_MARK_PATH)                                     # 只提示一次
    except Exception:
        info = None

    if not info:
        return response

    version = (info.get("version") or "").strip()
    label = "已更新到 v" + version if version else "更新完成"

    body = b""
    async for chunk in response.body_iterator:
        body += chunk

    text = body.decode("utf-8", "ignore")
    if "</body>" not in text:
        return response

    script = """
<script>
(function () {
  function show() {
    var box = document.createElement('div');
    box.textContent = %s;
    box.style.cssText = 'position:fixed;left:50%%;top:18px;transform:translateX(-50%%);' +
      'z-index:9999;padding:10px 18px;border-radius:999px;background:#2563eb;color:#fff;' +
      'font-size:13.5px;font-weight:600;box-shadow:0 8px 24px rgba(37,99,235,.35);' +
      'opacity:0;transition:opacity .25s,transform .25s;font-family:inherit';
    document.body.appendChild(box);
    requestAnimationFrame(function () { box.style.opacity = '1'; box.style.transform = 'translateX(-50%%) translateY(4px)'; });
    setTimeout(function () { box.style.opacity = '0'; setTimeout(function () { box.remove(); }, 300); }, 5000);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', show);
  else show();
})();
</script>
""" % json.dumps(label, ensure_ascii=False)

    text = text.replace("</body>", script + "</body>", 1)

    from starlette.responses import HTMLResponse as _HTML
    headers = {k: v for k, v in response.headers.items()
               if k.lower() not in ("content-length", "content-type")}
    return _HTML(content=text, status_code=response.status_code, headers=headers)


@app.get("/logo.ico")
async def get_logo():
    logo_path = os.path.join(BASE_DIR, "logo.ico")
    if os.path.exists(logo_path):
        return FileResponse(logo_path, media_type="image/x-icon")
    raise HTTPException(status_code=404, detail="Logo not found")

# 配置Jinja2模板（直接使用，绕过starlette缓存的bug）
template_env = Environment(loader=FileSystemLoader(os.path.join(BASE_DIR, "templates")), autoescape=True)

def asset_version():
    """静态资源版本号：CSS / JS 改动后 URL 变化，浏览器不会继续用旧缓存。"""
    latest = 0
    for rel in (("static", "css", "app.css"),
                ("static", "js", "slider.js"),
                ("static", "js", "sound.js")):
        try:
            latest = max(latest, int(os.path.getmtime(os.path.join(BASE_DIR, *rel))))
        except OSError:
            continue
    return latest


def render(request: Request, template_name: str, context: dict = None) -> HTMLResponse:
    """渲染模板，自动注入 CSRF token 并禁用缓存（避免登录态页面被浏览器缓存）。"""
    template = template_env.get_template(template_name)
    ctx = dict(context or {})
    ctx.setdefault("csrf_token", getattr(request.state, "csrf_token", ""))
    ctx.setdefault("css_version", asset_version())
    ctx.setdefault("app_version", APP_VERSION)
    html = template.render(**ctx)
    return HTMLResponse(html, headers={
        "Cache-Control": "no-store, no-cache, must-revalidate",
        "X-Content-Type-Options": "nosniff",
    })

# ==================== 安全基础：会话 / CSRF / 口令哈希 ====================

SESSION_COOKIE = "session_id"
CSRF_COOKIE = "csrf_token"
SESSION_TTL = 8 * 3600            # 会话空闲 8 小时过期
MAX_QUANTITY = 500                # 单次批量生成上限
PBKDF2_ROUNDS = 200_000

# 打印排版常量：每页 3 列 × 7 行 = 21 张卡片。
# （config.json 里旧的 printing 段从未被读取，已从默认配置移除，改版式请改这里。）
CARD_COLUMNS_PER_PAGE = 3
CARD_ROWS_PER_PAGE = 7
CARD_COLUMN_WIDTH_CM = 6.0
CARD_ROW_HEIGHT_TWIP = '2131'     # 约 3.76cm，exact 固定行高（备用）
PAGE_HEIGHT_CM = 29.7
PAGE_MARGIN_V_CM = 1.0            # 上下边距合计


def card_row_height_twip():
    """把手写卡行高设为「A4 可用高度 / 每页行数」，页面底部不再留大片空白。"""
    # 预留 0.3cm，避免表格因边框/单元格间距多出一行而翻页
    usable = PAGE_HEIGHT_CM - PAGE_MARGIN_V_CM - 0.9
    return str(int(round(usable / CARD_ROWS_PER_PAGE * 567)))

# 会话存储（简单内存存储，生产环境应使用Redis等）
# sid -> {"user": str, "expires": float, "gen": dict, "verify": dict, "setting": str}
sessions = {}

# 登录失败节流：ip -> {"count", "first_ts", "locked_until"}
_login_failures = {}
LOGIN_MAX_FAILURES = 5
LOGIN_LOCK_SECONDS = 300


def hash_password(password: str) -> str:
    """PBKDF2-SHA256 加盐哈希，形如 pbkdf2_sha256$轮数$盐$摘要。"""
    salt = secrets.token_hex(16)
    dk = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt.encode("utf-8"), PBKDF2_ROUNDS)
    return f"pbkdf2_sha256${PBKDF2_ROUNDS}${salt}${dk.hex()}"


def verify_password(password: str, stored: str):
    """返回 (是否匹配, 是否需要升级存储格式)。兼容历史明文口令，登录成功后自动升级。"""
    if not stored:
        return False, False
    if stored.startswith("pbkdf2_sha256$"):
        try:
            _, rounds, salt, digest = stored.split("$", 3)
            dk = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt.encode("utf-8"), int(rounds))
        except (ValueError, TypeError):
            return False, False
        return secrets.compare_digest(dk.hex(), digest), False
    # 旧版明文存储：比对成功即提示调用方升级为哈希
    return secrets.compare_digest(password.encode("utf-8"), stored.encode("utf-8")), True


def _prune_sessions():
    now = time.time()
    for sid in [s for s, v in sessions.items() if v.get("expires", 0) <= now]:
        sessions.pop(sid, None)


def ajax_login_required():
    """ajax 自动保存时：会话过期返回 401 JSON，避免前端拿到 HTML 后误判。"""
    return JSONResponse({"ok": False, "expired": True, "text": "登录已过期，请重新登录"},
                        status_code=401)


def get_session(request: Request):
    """返回 (sid, session)；未登录或已过期返回 (None, None)。滑动续期。"""
    sid = request.cookies.get(SESSION_COOKIE)
    if not sid:
        return None, None
    sess = sessions.get(sid)
    if not sess:
        return None, None
    if sess.get("expires", 0) <= time.time():
        sessions.pop(sid, None)
        return None, None
    sess["expires"] = time.time() + SESSION_TTL
    return sid, sess


def check_csrf(request: Request, token: str):
    expected = getattr(request.state, "csrf_token", "")
    provided = token or request.headers.get("x-csrf-token", "")
    if not expected or not provided or not secrets.compare_digest(expected.encode("utf-8"), provided.encode("utf-8")):
        raise HTTPException(status_code=403, detail="CSRF 校验失败，请刷新页面后重试")


def _client_ip(request: Request) -> str:
    return request.client.host if request.client else "unknown"


def _login_wait_seconds(ip: str) -> int:
    rec = _login_failures.get(ip)
    if not rec:
        return 0
    left = rec.get("locked_until", 0) - time.time()
    return int(left) + 1 if left > 0 else 0


def _note_login_failure(ip: str) -> None:
    now = time.time()
    rec = _login_failures.get(ip)
    if not rec or now - rec.get("first_ts", 0) > LOGIN_LOCK_SECONDS:
        rec = {"count": 0, "first_ts": now, "locked_until": 0}
    rec["count"] += 1
    if rec["count"] >= LOGIN_MAX_FAILURES:
        rec["locked_until"] = now + LOGIN_LOCK_SECONDS
        rec["count"] = 0
        rec["first_ts"] = now
    _login_failures[ip] = rec
    # 防止字典无界增长
    if len(_login_failures) > 5000:
        for k in [k for k, v in _login_failures.items() if v.get("locked_until", 0) <= now][:2500]:
            _login_failures.pop(k, None)


def _clear_login_failures(ip: str) -> None:
    _login_failures.pop(ip, None)


def _is_https(request: Request) -> bool:
    proto = request.headers.get("x-forwarded-proto", "")
    return request.url.scheme == "https" or proto.split(",")[0].strip() == "https"

# 默认配置
def get_default_config():
    """返回默认配置，init_required=True，确保首次启动走管理员设置流程"""
    return {
        "system": {
            "name": "班级管理系统",
            "init_required": True,
            "encryption_salt": secrets.token_hex(16)
        },
        "paths": {
            "database": "./data/db/scores.db",
            "output": "./data/output"
        },
        "admin": {
            "username": "",
            "password": ""
        },
        "backup": {
            "enabled": True,
            "time": "02:00",
            "keep": 10
        },
        "desktop": {
            "autostart": False
        },
        "print": {
            "printer": ""
        }
    }

# 数据目录
DATA_DIR = os.path.join(BASE_DIR, "data")

# 配置文件路径
CONFIG_PATH = os.path.join(DATA_DIR, "config.json")

# 确保数据目录存在
def ensure_data_dir():
    os.makedirs(DATA_DIR, exist_ok=True)

# 读取配置文件（不存在则自动创建默认配置）
def read_config():
    ensure_data_dir()
    if not os.path.exists(CONFIG_PATH):
        # 配置文件不存在，创建默认配置并要求初始化管理员
        default_config = get_default_config()
        write_config(default_config)
        return default_config
    # 兼容历史遗留：带 BOM 或被按本地代码页改过的 config 也要能读出来
    with open(CONFIG_PATH, "rb") as f:
        raw = f.read()

    config = None
    for enc in ("utf-8-sig", "utf-8", "gbk"):
        try:
            config = json.loads(raw.decode(enc))
            break
        except Exception:
            continue

    if config is None:
        raise RuntimeError("config.json 无法解析，请检查文件编码")

    # 系统名称如果被写坏成乱码（私用区字符 / 替换符），恢复成默认值
    try:
        name = config.get("system", {}).get("name") or ""
        if any("" <= ch <= "" or ch == "�" for ch in name):
            config.setdefault("system", {})["name"] = get_default_config()["system"]["name"]
            write_config(config)
    except Exception:
        pass

    # 深合并且只补缺失键：老版本/手改过的 config 缺少 section 时不再 KeyError，
    # 也不会覆盖用户已设置的值（尤其 init_required=False）。
    default = get_default_config()
    changed = False
    for key, val in default.items():
        if key not in config:
            config[key] = val
            changed = True
        elif isinstance(val, dict) and isinstance(config[key], dict):
            for k2, v2 in val.items():
                if k2 not in config[key]:
                    config[key][k2] = v2
                    changed = True
    if not config.get("system", {}).get("encryption_salt"):
        config["system"]["encryption_salt"] = secrets.token_hex(16)
        changed = True
    if changed:
        write_config(config)
    return config

# 写入配置文件
def write_config(config):
    ensure_data_dir()
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(config, f, ensure_ascii=False, indent=2)

    try:
        integrity.refresh()
    except Exception:
        pass


# 获取数据库绝对路径
def get_db_path():
    config = read_config()
    db_path = config["paths"]["database"]
    if not os.path.isabs(db_path):
        db_path = os.path.join(BASE_DIR, db_path)
    return db_path

# ==================== 备份 / 清理 ====================

BACKUP_PREFIX = "classmanager-backup-"
MAX_BACKUP_KEEP = 200


def get_backup_dir():
    path = os.path.join(DATA_DIR, "backups")
    os.makedirs(path, exist_ok=True)
    return path


def list_backups():
    """按时间升序返回备份文件名。"""
    directory = get_backup_dir()
    files = [f for f in os.listdir(directory)
             if f.startswith(BACKUP_PREFIX) and f.endswith(".zip")]
    return sorted(files)


def last_backup_time():
    files = list_backups()
    if not files:
        return None
    path = os.path.join(get_backup_dir(), files[-1])
    return datetime.datetime.fromtimestamp(os.path.getmtime(path)).strftime("%Y-%m-%d %H:%M:%S")


def create_backup():
    """把配置和数据库打包成一个 zip，并清理超出保留份数的旧备份。"""
    directory = get_backup_dir()
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    target = os.path.join(directory, f"{BACKUP_PREFIX}{stamp}.zip")

    db_path = get_db_path()
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as zf:
        if os.path.exists(CONFIG_PATH):
            zf.write(CONFIG_PATH, "config.json")
        if os.path.exists(db_path):
            zf.write(db_path, "db/scores.db")

    prune_backups()
    return os.path.basename(target)


def prune_backups():
    try:
        keep = int(read_config().get("backup", {}).get("keep", 10) or 0)
    except (TypeError, ValueError):
        keep = 10
    if keep <= 0:
        return
    files = list_backups()
    for name in files[:-keep]:
        try:
            os.remove(os.path.join(get_backup_dir(), name))
        except OSError:
            pass


def parse_backup_time(text, default=(2, 0)):
    """把 "HH:MM" 解析成 (时, 分)，非法时返回默认值。"""
    try:
        hour, minute = (text or "").strip().split(":")
        hour, minute = int(hour), int(minute)
        if 0 <= hour <= 23 and 0 <= minute <= 59:
            return hour, minute
    except (ValueError, AttributeError):
        pass
    return default


def last_backup_date():
    files = list_backups()
    if not files:
        return None
    latest = os.path.join(get_backup_dir(), files[-1])
    return datetime.datetime.fromtimestamp(os.path.getmtime(latest)).date()


def next_backup_time():
    """返回下一次自动备份的时间字符串；未开启时返回 None。"""
    config = read_config()
    section = config.get("backup", {}) or {}
    if not section.get("enabled", True):
        return None

    hour, minute = parse_backup_time(section.get("time", "02:00"))
    now = datetime.datetime.now()
    target = now.replace(hour=hour, minute=minute, second=0, microsecond=0)
    if now >= target and last_backup_date() == now.date():
        target += datetime.timedelta(days=1)
    elif now >= target:
        return target.strftime("%Y-%m-%d %H:%M")
    return target.strftime("%Y-%m-%d %H:%M")


def _backup_loop():
    """后台线程：每天到点自动备份一次（跨天只在当天没备份时触发）。"""
    while True:
        try:
            section = read_config().get("backup", {}) or {}
            if section.get("enabled", True):
                hour, minute = parse_backup_time(section.get("time", "02:00"))
                now = datetime.datetime.now()
                target = now.replace(hour=hour, minute=minute, second=0, microsecond=0)
                if now >= target and last_backup_date() != now.date():
                    create_backup()
        except Exception:
            pass
        time.sleep(30)


def start_backup_scheduler():
    thread = threading.Thread(target=_backup_loop, daemon=True, name="backup-scheduler")
    thread.start()


def output_stats():
    """导出目录里的 Word 文件数量与总大小。"""
    out = get_output_dir()
    if not os.path.isdir(out):
        return 0, 0
    files = [f for f in os.listdir(out) if f.lower().endswith(".docx")]
    size = 0
    for f in files:
        try:
            size += os.path.getsize(os.path.join(out, f))
        except OSError:
            pass
    return len(files), size


def clean_output():
    out = get_output_dir()
    removed, freed = 0, 0
    if not os.path.isdir(out):
        return removed, freed
    for name in os.listdir(out):
        path = os.path.join(out, name)
        if not os.path.isfile(path) or not name.lower().endswith(".docx"):
            continue
        try:
            freed += os.path.getsize(path)
            os.remove(path)
            removed += 1
        except OSError:
            pass
    return removed, freed


JUNK_SKIP_DIRS = {"python", "data", ".git", "node_modules"}


def junk_targets():
    """返回可清理的垃圾路径：__pycache__ 目录、*.pyc、*.tmp。"""
    targets = []
    for root, dirs, files in os.walk(BASE_DIR):
        dirs[:] = [d for d in dirs if d not in JUNK_SKIP_DIRS]
        if os.path.basename(root) == "__pycache__":
            targets.append(root)
            dirs[:] = []
            continue
        for name in files:
            if name.endswith((".pyc", ".pyo", ".tmp")):
                targets.append(os.path.join(root, name))
    return targets


def junk_stats():
    targets = junk_targets()
    size = 0
    for path in targets:
        if os.path.isfile(path):
            try:
                size += os.path.getsize(path)
            except OSError:
                pass
    return len(targets), size


def clean_junk():
    removed, freed = 0, 0
    for path in junk_targets():
        try:
            if os.path.isdir(path):
                import shutil
                shutil.rmtree(path, ignore_errors=True)
            else:
                freed += os.path.getsize(path)
                os.remove(path)
            removed += 1
        except OSError:
            pass
    return removed, freed


_dpi_ready = False


def _enable_dpi_awareness():
    """让 Tk 对话框按真实 DPI 渲染，否则 150% 缩放下会被系统位图放大而发糊。"""
    global _dpi_ready
    if _dpi_ready or os.name != "nt":
        return
    _dpi_ready = True
    try:
        import ctypes
        try:
            ctypes.windll.user32.SetProcessDpiAwarenessContext(-4)   # Per-Monitor V2
            return
        except Exception:
            pass
        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(2)           # Per-Monitor
            return
        except Exception:
            pass
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass


HISTORY_NAME = "history.json"
HISTORY_LIMIT = 30


def harden_data_dir_acl():
    """把 data 目录的写权限收紧到「当前用户 / SYSTEM / Administrators」。

    默认继承来的 ACL 常常是「Authenticated Users: 修改」，也就是同一台机器上
    任何登录用户都能改 config.json / devices.json；这里显式断开继承并重设。
    失败不影响运行，只记日志。
    """
    if os.name != "nt":
        return

    try:
        import subprocess

        sid = None
        try:
            out = subprocess.run(
                ["whoami", "/user", "/fo", "csv", "/nh"],
                capture_output=True, text=True, timeout=5, check=False).stdout or ""
            m = re.search(r"(S-1-5-[0-9-]+)", out)
            if m:
                sid = m.group(1)
        except Exception:
            sid = None

        if not sid:
            return

        target = DATA_DIR
        if not os.path.isdir(target):
            return

        subprocess.run(
            ["icacls", target, "/inheritance:r",
             "/grant:r", f"*{sid}:(OI)(CI)F",
             "/grant:r", "*S-1-5-18:(OI)(CI)F",      # SYSTEM
             "/grant:r", "*S-1-5-32-544:(OI)(CI)F"], # Administrators
            capture_output=True, text=True, timeout=10, check=False)
    except Exception:
        pass



harden_data_dir_acl()

try:
    integrity.verify()
except Exception:
    pass

def history_path():
    return os.path.join(DATA_DIR, HISTORY_NAME)


def load_history():
    path = history_path()
    if not os.path.exists(path):
        return []
    try:
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
        return data if isinstance(data, list) else []
    except (OSError, ValueError):
        return []


def save_history(records):
    path = history_path()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(records[:HISTORY_LIMIT], fh, ensure_ascii=False, indent=2)
    os.replace(tmp, path)


def history_add(record):
    records = load_history()
    records.insert(0, record)
    save_history(records)
    # 超出上限的旧记录连同导出文件一起清掉
    for extra in records[HISTORY_LIMIT:]:
        _drop_batch_file(extra)
    return record


def history_find(batch_id):
    for item in load_history():
        if item.get("id") == batch_id:
            return item
    return None


def history_patch(batch_id, **fields):
    records = load_history()
    for item in records:
        if item.get("id") == batch_id:
            item.update(fields)
    save_history(records)


def history_mark(filename, field):
    if not filename:
        return
    records = load_history()
    changed = False
    for item in records:
        if item.get("filename") == filename:
            item[field] = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
            changed = True
    if changed:
        save_history(records)


def _drop_batch_file(item):
    path = os.path.join(get_output_dir(), os.path.basename(item.get("filename", "")))
    try:
        if os.path.isfile(path):
            os.remove(path)
    except OSError:
        pass


def history_remove(batch_id):
    records = load_history()
    kept = []
    for item in records:
        if item.get("id") == batch_id:
            _drop_batch_file(item)
        else:
            kept.append(item)
    save_history(kept)


def history_pending():
    """还没打印也没保存的批次（新的在前）。"""
    return [item for item in load_history()
            if not item.get("printed") and not item.get("saved")]


def list_printers():
    """返回 (打印机名列表, 系统默认打印机)。非 Windows 返回空。"""
    if os.name != "nt":
        return [], ""
    try:
        import ctypes
        from ctypes import wintypes

        winspool = ctypes.WinDLL("winspool.drv")

        default = ""
        size = wintypes.DWORD(0)
        winspool.GetDefaultPrinterW(None, ctypes.byref(size))
        if size.value:
            buf = ctypes.create_unicode_buffer(size.value)
            if winspool.GetDefaultPrinterW(buf, ctypes.byref(size)):
                default = buf.value or ""

        class PRINTER_INFO_4(ctypes.Structure):
            _fields_ = [
                ("pPrinterName", wintypes.LPWSTR),
                ("pServerName", wintypes.LPWSTR),
                ("Attributes", wintypes.DWORD),
            ]

        needed, returned = wintypes.DWORD(0), wintypes.DWORD(0)
        winspool.EnumPrintersW(6, None, 4, None, 0,
                               ctypes.byref(needed), ctypes.byref(returned))
        names = []
        if needed.value:
            buf = ctypes.create_string_buffer(needed.value)
            ok = winspool.EnumPrintersW(6, None, 4, buf, needed.value,
                                        ctypes.byref(needed), ctypes.byref(returned))
            if ok:
                arr = ctypes.cast(buf, ctypes.POINTER(PRINTER_INFO_4 * returned.value)).contents
                names = [arr[i].pPrinterName for i in range(returned.value)]
        return [n for n in names if n], default
    except Exception:
        return [], ""


def _set_default_printer(name):
    try:
        import ctypes
        return bool(ctypes.WinDLL("winspool.drv").SetDefaultPrinterW(name))
    except Exception:
        return False


def docx_to_pdf(docx_path, pdf_path=None, timeout=90):
    """用本机 Office/WPS 把 docx 转成 PDF（无窗口），版面与 Word 完全一致。"""
    if os.name != "nt" or not os.path.isfile(docx_path):
        return None

    pdf_path = pdf_path or (os.path.splitext(docx_path)[0] + ".pdf")
    if os.path.exists(pdf_path) and os.path.getmtime(pdf_path) >= os.path.getmtime(docx_path):
        return pdf_path

    script = f'''
$ErrorActionPreference = "Continue"
try {{
    $w = New-Object -ComObject Word.Application
    $w.Visible = $false
    $w.DisplayAlerts = 0
    try {{
        $d = $w.Documents.Open("{docx_path}", $false, $true)
        $d.SaveAs2("{pdf_path}", 17)
        $d.Close($false)
    }} finally {{
        try {{ $w.Quit() }} catch {{}}
    }}
}} catch {{ }}
'''
    try:
        import subprocess
        subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True, timeout=timeout,
            creationflags=0x08000000 if os.name == "nt" else 0,
        )
    except Exception:
        return None

    if os.path.isfile(pdf_path) and os.path.getsize(pdf_path) > 0:
        return pdf_path
    return None


def print_pdf_direct(pdf_path, printer="", copies=1):
    """把 PDF 逐页渲染成位图，直接送打印机队列（不启动任何外部程序）。"""
    if os.name != "nt":
        raise OSError("仅支持 Windows 打印")

    import ctypes
    from ctypes import wintypes
    import pypdfium2 as pdfium
    from PIL import Image

    _, default_printer = list_printers()
    device = printer or default_printer
    if not device:
        raise OSError("没有可用的打印机")

    gdi32 = ctypes.WinDLL("gdi32")

    HORZRES, VERTRES = 8, 10
    LOGPIXELSX, LOGPIXELSY = 88, 90
    SRCCOPY = 0x00CC0020
    DIB_RGB_COLORS = 0

    class DOCINFOW(ctypes.Structure):
        _fields_ = [("cbSize", ctypes.c_int),
                    ("lpszDocName", wintypes.LPCWSTR),
                    ("lpszOutput", wintypes.LPCWSTR),
                    ("lpszDatatype", wintypes.LPCWSTR),
                    ("fwType", wintypes.DWORD)]

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wintypes.DWORD),
                    ("biWidth", ctypes.c_long),
                    ("biHeight", ctypes.c_long),
                    ("biPlanes", wintypes.WORD),
                    ("biBitCount", wintypes.WORD),
                    ("biCompression", wintypes.DWORD),
                    ("biSizeImage", wintypes.DWORD),
                    ("biXPelsPerMeter", ctypes.c_long),
                    ("biYPelsPerMeter", ctypes.c_long),
                    ("biClrUsed", wintypes.DWORD),
                    ("biClrImportant", wintypes.DWORD)]

    hdc = gdi32.CreateDCW("WINSPOOL", device, None, None)
    if not hdc:
        raise OSError(f"无法连接打印机：{device}")

    try:
        width_px = gdi32.GetDeviceCaps(hdc, HORZRES)
        height_px = gdi32.GetDeviceCaps(hdc, VERTRES)
        dpi = gdi32.GetDeviceCaps(hdc, LOGPIXELSX) or 300

        doc_info = DOCINFOW()
        doc_info.cbSize = ctypes.sizeof(DOCINFOW)
        doc_info.lpszDocName = os.path.basename(pdf_path)
        doc_info.lpszOutput = None
        doc_info.lpszDatatype = None
        doc_info.fwType = 0

        if gdi32.StartDocW(hdc, ctypes.byref(doc_info)) <= 0:
            raise OSError("无法创建打印任务")

        document = pdfium.PdfDocument(pdf_path)
        try:
            for _copy in range(max(1, copies)):
                for index in range(len(document)):
                    page = document[index]
                    scale = dpi / 72.0
                    image = page.render(scale=scale).to_pil().convert("RGB")

                    # 等比缩放到可打印区域（像素尺寸必须等于设备尺寸，否则会按 1:1 像素印成半张）
                    ratio = min(width_px / image.width, height_px / image.height)
                    if abs(ratio - 1) > 0.001:
                        image = image.resize(
                            (max(1, int(image.width * ratio)), max(1, int(image.height * ratio))),
                            Image.LANCZOS)

                    w, h = image.size
                    raw = image.transpose(Image.FLIP_TOP_BOTTOM).tobytes("raw", "BGR")
                    stride = w * 3
                    if stride % 4:
                        pad = 4 - stride % 4
                        raw = b"".join(
                            raw[i * stride:(i + 1) * stride] + bytes(pad)
                            for i in range(h))
                        stride += pad

                    header = BITMAPINFOHEADER()
                    header.biSize = ctypes.sizeof(BITMAPINFOHEADER)
                    header.biWidth = w
                    header.biHeight = h
                    header.biPlanes = 1
                    header.biBitCount = 24
                    header.biCompression = 0
                    header.biSizeImage = stride * h

                    buffer = ctypes.create_string_buffer(raw, len(raw))

                    if gdi32.StartPage(hdc) <= 0:
                        raise OSError("无法开始打印页")
                    gdi32.StretchDIBits(
                        hdc, 0, 0, w, h, 0, 0, w, h,
                        buffer, ctypes.byref(header), DIB_RGB_COLORS, SRCCOPY)
                    gdi32.EndPage(hdc)
        finally:
            document.close()
            gdi32.EndDoc(hdc)
    finally:
        gdi32.DeleteDC(hdc)

    return device


OFFICE_MISSING_MESSAGE = "未检测到 WPS 或 Office，请安装 WPS 或 Office 软件之后再尝试打印。"

_office_state = None


def apply_autostart(enabled):
    """立即把开机自启写进/移出当前用户的启动项。返回 True 表示已生效。"""
    if os.name != "nt":
        return False

    exe = os.environ.get("CLASSMANAGER_DESKTOP_EXE")
    root = os.environ.get("CLASSMANAGER_ROOT")
    if not exe or not os.path.exists(exe):
        return False        # 网页版启动时拿不到桌面端路径，只能等下次启动同步

    try:
        import winreg
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                             r"Software\Microsoft\Windows\CurrentVersion\Run",
                             0, winreg.KEY_SET_VALUE | winreg.KEY_QUERY_VALUE)
        try:
            if enabled:
                command = f'"{exe}"'
                if root:
                    command += f' --root "{root}"'
                command += " --tray"
                winreg.SetValueEx(key, "class-manager", 0, winreg.REG_SZ, command)
            else:
                try:
                    winreg.DeleteValue(key, "class-manager")
                except FileNotFoundError:
                    pass
        finally:
            winreg.CloseKey(key)
        return True
    except Exception:
        return False


def office_available():
    """检查本机是否装了 Word/WPS（查 COM 注册，不启动进程）。"""
    global _office_state
    if _office_state is not None:
        return _office_state

    if os.name != "nt":
        _office_state = False
        return _office_state

    try:
        import winreg
        for prog_id in ("Word.Application", "KWPS.Application"):
            try:
                with winreg.OpenKey(winreg.HKEY_CLASSES_ROOT, prog_id):
                    _office_state = True
                    return _office_state
            except OSError:
                continue
    except Exception:
        pass

    _office_state = False
    return _office_state


def ensure_batch_pdf(batch):
    """按批次数据生成 PDF（纯 Python），返回路径或 None。"""
    if not batch or not batch.get("codes"):
        return None
    filename = os.path.basename((batch.get("filename") or "").replace("\\", "/"))
    if not filename:
        return None
    docx_path = os.path.join(get_output_dir(), filename)

    if not office_available():
        return None

    return docx_to_pdf(docx_path)


# 打印用 PDF：按需把导出目录里的 docx 转成 PDF（同目录同名）


def pick_folder(title="选择文件夹"):
    """弹出系统文件夹选择框。

    返回：
      - 选中路径（str）
      - ""   用户主动取消
      - None 对话框打不开（环境问题）
    """
    _enable_dpi_awareness()
    try:
        import tkinter as tk
        from tkinter import filedialog

        root = tk.Tk()
        root.withdraw()
        try:
            icon = os.path.join(BASE_DIR, "logo.ico")
            if os.path.exists(icon):
                root.iconbitmap(icon)
        except Exception:
            pass
        root.attributes("-topmost", True)
        root.update()
        path = filedialog.askdirectory(title=title, mustexist=True, parent=root)
        root.destroy()
    except Exception:
        return None
    return path or ""


def find_legacy_databases(folder, max_depth=4):
    """在旧版安装目录里递归找 scores.db，返回 [(path, rows, mtime)]，按记录数降序。"""
    results = []
    base = os.path.abspath(folder).rstrip(os.sep)
    for root, dirs, files in os.walk(base):
        depth = root[len(base):].count(os.sep)
        if depth > max_depth:
            dirs[:] = []
            continue
        dirs[:] = [d for d in dirs if d not in {"__pycache__", ".git", "node_modules"}]
        for name in files:
            if name.lower() != "scores.db":
                continue
            path = os.path.join(root, name)
            rows = 0
            try:
                probe = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
                rows = probe.execute("SELECT COUNT(*) FROM scores").fetchone()[0]
                probe.close()
            except Exception:
                continue
            results.append((path, rows, os.path.getmtime(path)))
    results.sort(key=lambda item: (item[1], item[2]), reverse=True)
    return results


LEGACY_JSON_NAMES = ("config.json", "devices.json", "history.json")


def find_legacy_payloads(folder, max_depth=4):
    """扫描旧版目录：scores.db 以及 config.json / devices.json / history.json。

    旧版（class-score）没有 json 配置，扫不到就返回 None，不影响导入数据库；
    同版本互导时会带上这些文件，能一并恢复。
    """
    result = {
        "databases": find_legacy_databases(folder, max_depth=max_depth),
        "config": None,
        "devices": None,
        "history": None,
    }

    base = os.path.abspath(folder).rstrip(os.sep)
    found = {}

    for root, dirs, files in os.walk(base):
        depth = root[len(base):].count(os.sep)
        if depth > max_depth:
            dirs[:] = []
            continue
        dirs[:] = [d for d in dirs if d not in {"__pycache__", ".git", "node_modules"}]

        for name in files:
            low = name.lower()
            if low not in LEGACY_JSON_NAMES:
                continue
            path = os.path.join(root, name)
            # 优先取 data 目录里的那份（最像真正的运行时数据）
            score = 2 if os.path.basename(root).lower() == "data" else 1
            prev = found.get(low)
            if prev is None or score > prev[0]:
                found[low] = (score, path)

    if "config.json" in found:
        result["config"] = found["config.json"][1]
    if "devices.json" in found:
        result["devices"] = found["devices.json"][1]
    if "history.json" in found:
        result["history"] = found["history.json"][1]

    return result


def import_legacy_devices(src_path):
    """把旧版的班级设备清单合并进来（按 host+port 去重），返回 (总数, 新增数)。"""
    with open(src_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)

    if not isinstance(data, list):
        raise ValueError("devices.json 格式不对（应为列表）")

    existing = {(d.get("host"), d.get("port")) for d in devices.list_devices()}
    total, added = 0, 0

    for item in data:
        if not isinstance(item, dict):
            continue
        host = (item.get("host") or "").strip()
        if not host:
            continue
        total += 1
        key = (host, int(item.get("port") or 3457))
        if key in existing:
            continue
        try:
            devices.add_device(
                item.get("name") or host,
                item.get("room") or "",
                host,
                key[1],
                item.get("key") or "",
                int(item.get("video_port") or 0),
            )
            existing.add(key)
            added += 1
        except Exception:
            continue

    return total, added


def import_legacy_history(src_path):
    """把旧版的印卡批次合并进来（按 id 去重），返回 (总数, 新增数)。"""
    with open(src_path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)

    if not isinstance(data, list):
        raise ValueError("history.json 格式不对（应为列表）")

    current = load_history()
    ids = {r.get("id") for r in current if isinstance(r, dict)}
    total, added = 0, 0

    for item in data:
        if not isinstance(item, dict):
            continue
        total += 1
        bid = item.get("id")
        if bid and bid in ids:
            continue
        current.append(item)
        if bid:
            ids.add(bid)
        added += 1

    if added:
        save_history(current)

    return total, added


def import_legacy_database(src_path):
    """把旧库的积分记录合并进当前库，返回 (总数, 实际导入数)。"""
    src = sqlite3.connect(f"file:{src_path}?mode=ro", uri=True)
    src.row_factory = sqlite3.Row
    try:
        columns = {row["name"] for row in src.execute("PRAGMA table_info(scores)")}
        required = {"class_name", "title", "amount", "code", "hash", "issue_date"}
        if not required.issubset(columns):
            raise ValueError("该数据库不是本系统的数据表（缺少必要字段）")

        has_status = "status" in columns
        has_time = "verified_at" in columns
        rows = src.execute("SELECT * FROM scores").fetchall()
    finally:
        src.close()

    init_db()
    db = connect_db()
    try:
        with db:
            before = db.total_changes
            for row in rows:
                db.execute(
                    "INSERT OR IGNORE INTO scores"
                    " (class_name, title, amount, code, hash, issue_date, status, verified_at)"
                    " VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                    (
                        row["class_name"], row["title"], row["amount"], row["code"], row["hash"],
                        row["issue_date"],
                        row["status"] if has_status else "未使用",
                        row["verified_at"] if has_time else None,
                    ),
                )
            inserted = db.total_changes - before
    finally:
        db.close()
    return len(rows), inserted


def human_size(size):
    size = float(size)
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024 or unit == "GB":
            return f"{size:.0f} {unit}" if unit == "B" else f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} GB"


# 打开数据库连接（统一 row_factory，避免硬编码下标）
def connect_db():
    conn = sqlite3.connect(get_db_path(), timeout=10)
    conn.row_factory = sqlite3.Row
    return conn

# 初始化数据库
def init_db():
    db_path = get_db_path()
    # 确保数据库目录存在
    db_dir = os.path.dirname(db_path)
    if db_dir:
        os.makedirs(db_dir, exist_ok=True)
    # 连接数据库
    conn = sqlite3.connect(db_path)
    cursor = conn.cursor()
    # 创建表
    cursor.execute('''
    CREATE TABLE IF NOT EXISTS scores (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        class_name TEXT NOT NULL,
        title TEXT NOT NULL,
        amount INTEGER NOT NULL,
        code TEXT UNIQUE NOT NULL,
        hash TEXT UNIQUE NOT NULL,
        issue_date TEXT NOT NULL,
        status TEXT DEFAULT '未使用',
        verified_at TEXT
    )''')
    # 兼容 v2/v3.0 老库：补核销时间列（可空、幂等，不动已有数据）
    columns = {row[1] for row in cursor.execute("PRAGMA table_info(scores)")}
    if "verified_at" not in columns:
        cursor.execute("ALTER TABLE scores ADD COLUMN verified_at TEXT")

    # 学生名单（班级叫号用）
    cursor.execute("""
    CREATE TABLE IF NOT EXISTS students (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        name TEXT NOT NULL,
        gender TEXT DEFAULT '',
        number TEXT DEFAULT '',
        class_name TEXT DEFAULT '',
        created_at TEXT DEFAULT '',
        sort_order INTEGER DEFAULT 0
    )""")

    stu_cols = {row[1] for row in cursor.execute("PRAGMA table_info(students)")}
    if "sort_order" not in stu_cols:
        cursor.execute("ALTER TABLE students ADD COLUMN sort_order INTEGER DEFAULT 0")
        cursor.execute("UPDATE students SET sort_order = id WHERE sort_order IS NULL OR sort_order = 0")
    if "group_id" not in stu_cols:
        cursor.execute("ALTER TABLE students ADD COLUMN group_id INTEGER DEFAULT 0")

    # 小组（课堂分组用）
    cursor.execute("""
    CREATE TABLE IF NOT EXISTS student_groups (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        name TEXT NOT NULL,
        sort_order INTEGER DEFAULT 0,
        created_at TEXT DEFAULT ''
    )""")

    conn.commit()
    conn.close()

# 生成积分码（8位短编号 + SHA-256哈希）
CODE_CHARS = string.ascii_uppercase + string.digits  # A-Z + 0-9

def generate_score_code(class_name, title, amount, salt=None):
    """返回 (短编号, 哈希值)。短编号10位用于条码和核销，哈希列保留以备后续签名校验。

    salt 由调用方一次读取后传入，避免批量生成时每张卡都读一次配置文件。
    """
    if salt is None:
        salt = read_config()["system"]["encryption_salt"]
    short_code = ''.join(secrets.choice(CODE_CHARS) for _ in range(10))
    # 用短编号 + 业务数据生成哈希做防伪
    timestamp = datetime.datetime.now().isoformat()
    data = f"{short_code}{class_name}{title}{amount}{timestamp}"
    hash_obj = hashlib.sha256((data + salt).encode())
    return short_code, hash_obj.hexdigest()

# 生成Code128条码图片到内存
def generate_barcode_image(barcode_data):
    """生成Code128条码PNG图片，返回BytesIO对象。barcode_data为条码内容（含面额的编码）"""
    Code128 = barcode.get_barcode_class('code128')
    code = Code128(barcode_data, writer=barcode.writer.ImageWriter())
    buf = io.BytesIO()
    code.write(buf, options={
        'module_width': 0.25,
        'module_height': 7.0,
        'write_text': False,
        'quiet_zone': 0.5,
        'format': 'PNG',
    })
    buf.seek(0)
    return buf

# 辅助：给段落添加 tab stop
def add_tab_stop(paragraph, tab_val, tab_pos):
    pPr = paragraph._element.get_or_add_pPr()
    tabs = pPr.makeelement(qn('w:tabs'), {})
    tab = tabs.makeelement(qn('w:tab'), {
        qn('w:val'): tab_val,
        qn('w:pos'): tab_pos,
    })
    tabs.append(tab)
    pPr.append(tabs)

# 辅助：生成面额文本框（锚定在段落右侧，三号字大字效果）
_shape_id_counter = [100]

def make_amount_textbox(amount_text):
    """生成面额文本框的 mc:AlternateContent XML 元素"""
    _shape_id_counter[0] += 1
    doc_pr_id = _shape_id_counter[0]
    font_sz = 32  # 三号 = 16pt = 半磅单位32
    cx = 706120   # 文本框宽
    cy = 325120   # 文本框高（约两行）
    pos_h = 1386840  # 水平偏移
    pos_v = 19050    # 垂直偏移

    xml_template = f'''<mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
  xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
  xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
  xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
  xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"
  xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <mc:Choice Requires="wps">
    <w:drawing>
      <wp:anchor distT="0" distB="0" distL="114300" distR="114300" simplePos="0"
        relativeHeight="251659264" behindDoc="0" locked="0" layoutInCell="1" allowOverlap="1">
        <wp:simplePos x="0" y="0"/>
        <wp:positionH relativeFrom="column">
          <wp:posOffset>{pos_h}</wp:posOffset>
        </wp:positionH>
        <wp:positionV relativeFrom="paragraph">
          <wp:posOffset>{pos_v}</wp:posOffset>
        </wp:positionV>
        <wp:extent cx="{cx}" cy="{cy}"/>
        <wp:effectExtent l="0" t="0" r="10160" b="10160"/>
        <wp:wrapNone/>
        <wp:docPr id="{doc_pr_id}" name="文本框 {doc_pr_id}"/>
        <wp:cNvGraphicFramePr/>
        <a:graphic>
          <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
            <wps:wsp>
              <wps:cNvSpPr txBox="1"/>
              <wps:spPr>
                <a:xfrm>
                  <a:off x="0" y="0"/>
                  <a:ext cx="{cx}" cy="{cy}"/>
                </a:xfrm>
                <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                <a:solidFill><a:schemeClr val="lt1"/></a:solidFill>
                <a:ln w="6350"><a:noFill/></a:ln>
              </wps:spPr>
              <wps:txbx>
                <w:txbxContent>
                  <w:p>
                    <w:pPr>
                      <w:rPr>
                        <w:sz w:val="{font_sz}"/>
                        <w:szCs w:val="{font_sz}"/>
                      </w:rPr>
                    </w:pPr>
                    <w:r>
                      <w:rPr>
                        <w:b/>
                        <w:sz w:val="{font_sz}"/>
                        <w:szCs w:val="{font_sz}"/>
                      </w:rPr>
                      <w:t>{amount_text}</w:t>
                    </w:r>
                  </w:p>
                </w:txbxContent>
              </wps:txbx>
              <wps:bodyPr rot="0" spcFirstLastPara="0" vertOverflow="overflow"
                horzOverflow="overflow" vert="horz" wrap="square"
                lIns="91440" tIns="45720" rIns="91440" bIns="45720"
                numCol="1" spcCol="0" rtlCol="0" fromWordArt="0"
                anchor="t" anchorCtr="0" forceAA="0" compatLnSpc="1">
                <a:noAutofit/>
              </wps:bodyPr>
            </wps:wsp>
          </a:graphicData>
        </a:graphic>
      </wp:anchor>
    </w:drawing>
  </mc:Choice>
</mc:AlternateContent>'''
    return etree.fromstring(xml_template.encode('utf-8'))

# 把用户输入变成安全的文件名片段（防路径穿越：去掉分隔符与保留字符）
def safe_path_part(text, max_len=60):
    cleaned = re.sub(r'[\\/:*?"<>|\x00-\x1f]', "_", str(text))
    cleaned = cleaned.strip().strip(".").replace("..", "_")
    return cleaned[:max_len] or "未命名"

# 导出Word文档
def export_to_word(class_name, title, amount, codes, issue_date):
    config = read_config()
    output_dir = config["paths"]["output"]
    if not os.path.isabs(output_dir):
        output_dir = os.path.join(BASE_DIR, output_dir)
    os.makedirs(output_dir, exist_ok=True)

    doc = Document()

    # 显式指定 A4（python-docx 默认是 Letter 8.5×11in，打印到 A4 会被缩放并出现大片白边）
    for section in doc.sections:
        section.page_width = Cm(21.0)
        section.page_height = Cm(29.7)
        section.left_margin = Cm(1.0)
        section.right_margin = Cm(1.0)
        section.top_margin = Cm(0.5)
        section.bottom_margin = Cm(0.5)

    # 每页 CARD_ROWS_PER_PAGE 行 × CARD_COLUMNS_PER_PAGE 列
    rows_per_page = CARD_ROWS_PER_PAGE
    columns = CARD_COLUMNS_PER_PAGE
    column_width = CARD_COLUMN_WIDTH_CM  # cm
    row_height_twip = card_row_height_twip()  # A4 可用高度均分，整页填满
    items_per_page = rows_per_page * columns
    tab_right_pos = str(int(column_width * 567))

    for page_idx, page_codes in enumerate([codes[i:i+items_per_page] for i in range(0, len(codes), items_per_page)]):
        if page_idx > 0:
            doc.add_page_break()

        # 始终使用完整的8行表格，避免浪费纸张
        table = doc.add_table(rows=rows_per_page, cols=columns)
        table.autofit = False
        table.alignment = WD_TABLE_ALIGNMENT.CENTER

        # 设置列宽 + 固定行高 + 垂直居中
        for row in table.rows:
            tr = row._tr
            trPr = tr.get_or_add_trPr()
            trHeight = trPr.makeelement(qn('w:trHeight'), {
                qn('w:val'): row_height_twip,
                qn('w:hRule'): 'exact',
            })
            trPr.append(trHeight)

            for cell in row.cells:
                cell.width = Cm(column_width)
                tc = cell._tc
                tcPr = tc.get_or_add_tcPr()
                vAlign = tcPr.makeelement(qn('w:vAlign'), {qn('w:val'): 'center'})
                tcPr.append(vAlign)
                
                # 设置单元格边框为黑色
                tcBorders = tcPr.makeelement(qn('w:tcBorders'))
                
                # 上边框
                top = tcBorders.makeelement(qn('w:top'), {
                    qn('w:val'): 'single',
                    qn('w:sz'): '4',
                    qn('w:space'): '0',
                    qn('w:color'): '000000'
                })
                tcBorders.append(top)
                
                # 下边框
                bottom = tcBorders.makeelement(qn('w:bottom'), {
                    qn('w:val'): 'single',
                    qn('w:sz'): '4',
                    qn('w:space'): '0',
                    qn('w:color'): '000000'
                })
                tcBorders.append(bottom)
                
                # 左边框
                left = tcBorders.makeelement(qn('w:left'), {
                    qn('w:val'): 'single',
                    qn('w:sz'): '4',
                    qn('w:space'): '0',
                    qn('w:color'): '000000'
                })
                tcBorders.append(left)
                
                # 右边框
                right = tcBorders.makeelement(qn('w:right'), {
                    qn('w:val'): 'single',
                    qn('w:sz'): '4',
                    qn('w:space'): '0',
                    qn('w:color'): '000000'
                })
                tcBorders.append(right)
                
                tcPr.append(tcBorders)

        # 填充数据
        for i, code in enumerate(page_codes):
            row_idx = i // columns
            col_idx = i % columns
            cell = table.cell(row_idx, col_idx)

            # 清空默认段落
            for p in cell.paragraphs:
                p._element.getparent().remove(p._element)

            # 段落0：班级积分卡 + 右面板额文本框
            p = cell.add_paragraph()
            p.paragraph_format.space_before = Pt(2)
            p.paragraph_format.space_after = Pt(1)
            p.paragraph_format.first_line_indent = Pt(0)

            run_box = p.add_run()
            run_box._element.append(make_amount_textbox(f"{amount}分"))

            run = p.add_run("班级积分卡")
            run.bold = True
            run.font.size = Pt(10)

            # 段落1：班级（tab stop左靠）
            p = cell.add_paragraph()
            p.paragraph_format.space_before = Pt(0)
            p.paragraph_format.space_after = Pt(0)
            add_tab_stop(p, 'right', tab_right_pos)
            run = p.add_run(f"班级：{class_name}")
            run.font.size = Pt(9)
            p.add_run('\t')

            # 段落2：标题 + 日期（tab stop左右锚定）
            p = cell.add_paragraph()
            p.paragraph_format.space_before = Pt(0)
            p.paragraph_format.space_after = Pt(1)
            add_tab_stop(p, 'right', tab_right_pos)
            run = p.add_run(f"标题：{title}")
            run.font.size = Pt(9)
            p.add_run('\t')
            run = p.add_run(f"日期：{issue_date}")
            run.font.size = Pt(9)

            # 段落3：条码图片
            barcode_buf = generate_barcode_image(code)
            p = cell.add_paragraph()
            p.alignment = WD_ALIGN_PARAGRAPH.CENTER
            p.paragraph_format.space_before = Pt(2)
            p.paragraph_format.space_after = Pt(0)
            run = p.add_run()
            run.add_picture(barcode_buf, width=Cm(5))

            # 段落4：编号
            p = cell.add_paragraph()
            p.alignment = WD_ALIGN_PARAGRAPH.CENTER
            p.paragraph_format.space_before = Pt(1)
            p.paragraph_format.space_after = Pt(1)
            run = p.add_run(code)
            run.font.size = Pt(8)
            run.font.name = 'Consolas'
        
        # 对于最后一页，填充剩余的空白单元格
        total_cells = rows_per_page * columns
        for i in range(len(page_codes), total_cells):
            row_idx = i // columns
            col_idx = i % columns
            cell = table.cell(row_idx, col_idx)
            # 清空默认段落
            for p in cell.paragraphs:
                p._element.getparent().remove(p._element)

    filename = (f"{safe_path_part(class_name)}_{safe_path_part(title)}_"
                f"{safe_path_part(amount, 12)}分_{datetime.datetime.now().strftime('%Y%m%d%H%M%S')}.docx")
    filepath = os.path.join(output_dir, filename)
    doc.save(filepath)
    return filepath

# 检查是否已登录
def get_current_user(request: Request):
    _, sess = get_session(request)
    return sess["user"] if sess else None

# 安全防护中间件
@app.middleware("http")
async def security_middleware(request: Request, call_next):
    # 防止访问敏感文件，静默跳转登录页（前缀/精确匹配，避免子串误伤正常路由）
    path = request.url.path
    if path.startswith(("/data/", "/db/")) or path in (
        "/config.json", "/requirements.txt"
    ):
        return RedirectResponse(url="/login")

    # CSRF 双提交令牌：token 同时放在 cookie 与表单隐藏域
    token = request.cookies.get(CSRF_COOKIE) or secrets.token_urlsafe(32)
    request.state.csrf_token = token
    _prune_sessions()

    response = await call_next(request)
    if request.cookies.get(CSRF_COOKIE) != token:
        response.set_cookie(CSRF_COOKIE, token, samesite="lax", httponly=False)
    response.headers.setdefault("X-Content-Type-Options", "nosniff")
    response.headers.setdefault("X-Frame-Options", "SAMEORIGIN")
    return response

# 初始化页的一次性提示（导入旧库结果等）
_init_notice = None
_admin_kept = False          # 恢复出厂时勾选了「保留管理员」，初始化可跳过第一步


BOOT_MODES = {"every": "每次登录一次", "daily": "每天一次", "off": "不开启"}


def boot_should_show():
    """按设置判断这次是否要播放启动动画。"""
    boot = read_config().get("boot", {}) or {}

    # 恢复出厂时保留了设置、且动画是关闭的：初始化后强制播一次
    if boot.get("force_once"):
        return True

    mode = boot.get("mode", "every")
    if mode == "off":
        return False
    if mode == "daily":
        today = datetime.date.today().isoformat()
        return boot.get("last_shown", "") != today
    return True


def boot_target():
    """登录/初始化后该跳去哪儿。"""
    return "/boot?to=/index" if boot_should_show() else "/index"


def mark_boot_shown():
    config = read_config()
    config.setdefault("boot", {})
    config["boot"]["last_shown"] = datetime.date.today().isoformat()
    config["boot"]["force_once"] = False       # 只强制一次
    write_config(config)


# ---------------- 检查更新 ----------------

# 默认更新仓库；可在 config.json 的 update.repo 改成别的仓库，不用改代码
DEFAULT_UPDATE_REPO = "liang-rising-star/class-manager"


def update_repo():
    """当前使用的更新仓库（owner/name）。"""
    try:
        cfg = read_config()
        repo = str(((cfg.get("update") or {}).get("repo")) or "").strip()
    except Exception:
        repo = ""
    return repo or DEFAULT_UPDATE_REPO


def update_urls():
    repo = update_repo()
    return {
        "repo": repo,
        "latest": f"https://api.github.com/repos/{repo}/releases/latest",
        "list": f"https://api.github.com/repos/{repo}/releases?per_page=10",
        "page": f"https://github.com/{repo}/releases",
    }

# 下载任务的实时状态（单任务，够用）
_update_job = {
    "state": "idle",        # idle / downloading / ready / error
    "percent": 0,
    "received": 0,
    "total": 0,
    "path": "",
    "version": "",
    "error": "",
    "updated_at": 0.0,       # 最后一次进度更新的时间，用来判断任务是否已经僵死
}
_update_job_lock = threading.Lock()


def _version_tuple(text):
    """把 "v3.1.0" / "3.1" 转成等长元组，3.0 与 3.0.0 视为同一版本。"""
    numbers = [int(n) for n in re.findall(r"\d+", str(text or ""))][:4]
    return tuple(numbers + [0] * (4 - len(numbers))) if numbers else (0, 0, 0, 0)


def _github_get(url):
    import urllib.request as _url
    req = _url.Request(url, headers={
        "User-Agent": f"class-manager/{APP_VERSION}",
        "Accept": "application/vnd.github+json",
    })
    with _url.urlopen(req, timeout=8) as resp:
        return json.loads(resp.read().decode("utf-8"))


def _pick_release(beta):
    """正式通道取 latest；体验通道取列表里最新的（含 prerelease）。"""
    urls = update_urls()

    if not beta:
        return _github_get(urls["latest"])

    items = _github_get(urls["list"])
    if not isinstance(items, list) or not items:
        raise ValueError("该仓库还没有发布任何版本")
    # 列表本身就是按发布时间倒序，取第一个非草稿
    for item in items:
        if not item.get("draft"):
            return item
    raise ValueError("no releases")


def update_fetch(beta=None):
    """请求 GitHub，返回 {current, latest, has_update, ...}；不写配置。"""
    config = read_config()
    if beta is None:
        beta = bool((config.get("update", {}) or {}).get("beta", False))

    urls = update_urls()

    result = {
        "current": APP_VERSION,
        "repo": urls["repo"],
        "beta": bool(beta),
        "channel": "beta" if beta else "stable",
        "latest": None,
        "has_update": False,
        "prerelease": False,
        "url": urls["page"],
        "name": None,
        "published": None,
        "notes": None,
        "error": None,
    }

    try:
        data = _pick_release(beta)
        latest = (data.get("tag_name") or data.get("name") or "").strip().lstrip("vV")
        result.update({
            "latest": latest,
            "url": data.get("html_url") or urls["page"],
            "name": data.get("name") or latest,
            "prerelease": bool(data.get("prerelease")),
            "published": (data.get("published_at") or "")[:10],
            "notes": (data.get("body") or "").strip()[:1200],
        })
        result["has_update"] = _version_tuple(latest) > _version_tuple(APP_VERSION)
    except Exception as exc:
        result["error"] = _update_error_text(exc, urls["repo"])

    return result


def _update_error_text(exc, repo):
    """把 GitHub 的各种错误翻译成人能看懂的话。"""
    code = getattr(exc, "code", None)
    if code == 404:
        return f"仓库 {repo} 还没有发布版本（或者仓库地址不对）"
    if code == 403:
        return "GitHub 访问被限制（接口调用过于频繁），稍后再试"
    if code in (500, 502, 503):
        return "GitHub 服务器暂时不可用，稍后再试"

    # 我们自己抛的中文提示（例如"该仓库还没有发布任何版本"）直接透出
    if isinstance(exc, ValueError) and str(exc):
        return str(exc)

    text = str(exc)
    if "timed out" in text or "timeout" in text.lower():
        return "连接 GitHub 超时，检查网络后重试"
    if "Name or service not known" in text or "getaddrinfo" in text:
        return "无法解析 GitHub 域名，检查网络后重试"

    return f"检查更新失败（{exc.__class__.__name__}）"


def update_cache(payload):
    """把检查结果写进 config.json，供前台轮询。"""
    config = read_config()
    config.setdefault("update", {})
    config["update"].update({
        "latest": payload.get("latest") or "",
        "has_update": bool(payload.get("has_update")),
        "prerelease": bool(payload.get("prerelease")),
        "name": payload.get("name") or "",
        "url": payload.get("url") or update_urls()["page"],
        "repo": payload.get("repo") or update_repo(),
        "published": payload.get("published") or "",
        "notes": payload.get("notes") or "",
        "checked_at": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
    })
    write_config(config)


@app.get("/update/check")
def update_check(request: Request):
    """手动检查：真的去请求 GitHub，并刷新缓存。"""
    sid, sess = get_session(request)
    if not sess:
        raise HTTPException(status_code=403, detail="请先登录")

    payload = update_fetch()
    if not payload.get("error"):
        update_cache(payload)
    return payload


@app.post("/update/download")
async def update_download(request: Request, csrf_token: str = Form(None)):
    """启动后台下载（带进度），前端轮询 /update/progress。"""
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        raise HTTPException(status_code=403, detail="请先登录")

    job = _update_job_get()
    if job["state"] == "downloading":
        idle = time.time() - (job.get("updated_at") or 0)
        if idle < 45:
            return {"ok": True, "state": "downloading", "resumed": True}
        # 卡住的旧任务（下载线程已死）直接丢掉，重新开始
        _update_job_set(state="idle", percent=0, received=0, total=0, error="")

    config = read_config()
    beta = bool((config.get("update", {}) or {}).get("beta", False))

    _update_job_set(state="downloading", percent=0, received=0, total=0,
                    path="", version="", error="")
    threading.Thread(target=_update_download_worker, args=(beta,), daemon=True,
                     name="update-download").start()
    return {"ok": True, "state": "downloading"}


@app.get("/update/progress")
def update_progress(request: Request):
    """下载进度：state = downloading / ready / error。"""
    sid, sess = get_session(request)
    if not sess:
        raise HTTPException(status_code=403, detail="请先登录")
    return _update_job_get()


def _update_job_get():
    with _update_job_lock:
        return dict(_update_job)


def _update_job_set(**kwargs):
    with _update_job_lock:
        _update_job.update(kwargs)
        _update_job["updated_at"] = time.time()


def _update_download_worker(beta: bool):
    """后台线程：挑资产 → 下载到 class-manager-system/update/update.zip → 上报进度。"""
    _update_job_set(state="downloading", percent=0, received=0, total=0,
                    path="", version="", error="")
    try:
        data = _pick_release(beta)

        # 只要文件名里带 update、并且是 zip，就算更新包（允许带版本号）
        asset = None
        for item in (data.get("assets") or []):
            name = (item.get("name") or "").lower()
            if name.endswith(".zip") and "update" in name:
                asset = item
                break

        if asset is None:
            _update_job_set(state="error",
                            error="这个版本没有提供更新包（文件名需包含 update 且以 .zip 结尾）")
            return

        url = asset.get("browser_download_url")
        if not url:
            _update_job_set(state="error", error="更新包地址无效")
            return

        target_dir = os.path.join(BASE_DIR, "update")
        os.makedirs(target_dir, exist_ok=True)
        target = os.path.join(target_dir, "update.zip")
        temp = target + ".part"

        import urllib.request as _url
        req = _url.Request(url, headers={"User-Agent": f"class-manager/{APP_VERSION}"})

        with _url.urlopen(req, timeout=60) as resp:
            total = int(resp.headers.get("content-length") or 0)
            _update_job_set(total=total, received=0, percent=0)

            received = 0
            with open(temp, "wb") as f:
                while True:
                    chunk = resp.read(256 * 1024)
                    if not chunk:
                        break
                    f.write(chunk)
                    received += len(chunk)
                    percent = int(received * 100 / total) if total else 0
                    _update_job_set(received=received, percent=min(percent, 99))

        os.replace(temp, target)
        _update_job_set(state="ready", percent=100, path=target,
                        version=(data.get("tag_name") or "").lstrip("vV"))
    except Exception as exc:
        _update_job_set(state="error", error=f"下载失败（{exc.__class__.__name__}）")


@app.get("/update/status")
def update_status(request: Request):
    """只读缓存，前台用来显示「有新版本」徽标，不发网络请求。"""
    sid, sess = get_session(request)
    if not sess:
        raise HTTPException(status_code=403, detail="请先登录")

    config = read_config()
    upd = config.get("update", {}) or {}
    return {
        "current": APP_VERSION,
        "repo": update_repo(),
        "beta": bool(upd.get("beta", False)),
        "latest": upd.get("latest") or "",
        "has_update": bool(upd.get("has_update")),
        "prerelease": bool(upd.get("prerelease")),
        "name": upd.get("name") or "",
        "url": upd.get("url") or update_urls()["page"],
        "published": upd.get("published") or "",
        "notes": upd.get("notes") or "",
        "checked_at": upd.get("checked_at") or "",
    }


@app.post("/update/settings")
async def update_settings(request: Request, beta: str = Form(None), csrf_token: str = Form(None)):
    """切换正式 / 体验通道，切换后立刻查一次。"""
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login", status_code=303)

    enabled = bool(beta)
    config = read_config()
    config.setdefault("update", {})
    config["update"]["beta"] = enabled
    write_config(config)

    payload = update_fetch(beta=enabled)
    if not payload.get("error"):
        update_cache(payload)

    return {"ok": True, "beta": enabled, "channel": payload.get("channel"),
            "result": payload}


def _update_loop():
    """后台线程：每小时检查一次更新，结果写缓存。"""
    # 启动后先等 3 分钟再查，避免跟首次启动抢资源
    time.sleep(180)
    while True:
        try:
            # 自动检查只用正式通道；体验版由用户在弹窗里手动开启后生效
            payload = update_fetch(beta=False)
            if not payload.get("error"):
                update_cache(payload)
        except Exception:
            pass
        time.sleep(3600)


def start_update_scheduler():
    thread = threading.Thread(target=_update_loop, daemon=True, name="update-scheduler")
    thread.start()


# 启动动画：初始化完成后进入控制台前的过场# 启动动画：初始化完成后进入控制台前的过场
@app.get("/boot", response_class=HTMLResponse)
async def boot_page(request: Request, to: str = "/index"):
    sid, sess = get_session(request)
    if not sess or not sess.get("user"):
        return RedirectResponse(url="/login")

    # 只允许站内路径，避免被当成开放重定向
    next_url = to if to.startswith("/") and not to.startswith("//") else "/index"
    mark_boot_shown()
    return render(request, "boot.html", {
        "username": sess.get("user", ""),
        "next_url": next_url,
    })


# 初始化第一步：设置管理密码
@app.get("/", response_class=HTMLResponse)
async def init_admin(request: Request):
    global _init_notice
    config = read_config()
    if not config["system"]["init_required"]:
        return RedirectResponse(url="/login")

    sid, sess = get_session(request)
    if sess and sess.get("pending_admin"):
        return render(request, "init_data_source.html", {
            "system_name": config["system"]["name"],
            "username": sess["pending_admin"]["username"],
            "notice": _init_notice,
            "error": None,
        })

    # 恢复出厂时勾选了「保留管理员」：管理员还在，直接进第二步选数据来源
    if _admin_kept and config.get("admin", {}).get("password"):
        return render(request, "init_data_source.html", {
            "system_name": config["system"]["name"],
            "username": config["admin"].get("username", "admin"),
            "notice": _init_notice,
            "error": None,
        })

    # 首次启动先打招呼，点「开始体验」再进入两步设置
    if not (sess and sess.get("welcomed")):
        return render(request, "welcome.html", {
            "system_name": config["system"]["name"],
        })

    notice = _init_notice
    _init_notice = None
    return render(request, "init_admin.html", {
        "system_name": config["system"]["name"],
        "notice": notice,
        "error": None,
    })


# 欢迎页「开始体验」：记下已看过欢迎页，进入第一步设置密码
@app.post("/welcome/done")
async def welcome_done(request: Request, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)

    sid, sess = get_session(request)
    if sess:
        sess["welcomed"] = True
        return RedirectResponse(url="/", status_code=303)

    sid = secrets.token_hex(32)
    sessions[sid] = {"user": None, "expires": time.time() + SESSION_TTL, "welcomed": True}
    response = RedirectResponse(url="/", status_code=303)
    response.set_cookie(SESSION_COOKIE, sid, httponly=True, samesite="lax",
                        secure=_is_https(request), max_age=SESSION_TTL)
    return response


# 初始化第一步提交：先记住管理员账号，进入第二步选择数据来源
@app.post("/init_admin/credentials")
async def init_credentials(request: Request, username: str = Form(""),
                           password: str = Form(""), password2: str = Form(""),
                           csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    config = read_config()
    if not config["system"].get("init_required"):
        raise HTTPException(status_code=403, detail="系统已初始化，请直接登录")

    system_name = config["system"]["name"]
    username = (username or "").strip()

    def back(error):
        return render(request, "init_admin.html", {
            "system_name": system_name, "error": error, "notice": None,
            "username": username,
        })

    if not 1 <= len(username) <= 32:
        return back("用户名长度需为 1-32 个字符")
    if len(password or "") < 4:
        return back("密码至少 4 位")
    if password2 and password != password2:
        return back("两次输入的密码不一致")

    sid = secrets.token_hex(32)
    sessions[sid] = {
        "user": None,
        "expires": time.time() + SESSION_TTL,
        "welcomed": True,
        "pending_admin": {"username": username, "password": hash_password(password)},
    }

    response = RedirectResponse(url="/", status_code=303)
    response.set_cookie(SESSION_COOKIE, sid, httponly=True, samesite="lax",
                        secure=_is_https(request), max_age=SESSION_TTL)
    return response


# 初始化第二步：用「新建数据库」完成初始化
@app.post("/init_admin/complete")
async def init_complete(request: Request, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    config = read_config()
    if not config["system"].get("init_required"):
        raise HTTPException(status_code=403, detail="系统已初始化，请直接登录")

    sid, sess = get_session(request)
    if sess and sess.get("pending_admin"):
        return _apply_pending_admin(request, sess)

    # 保留了管理员的情况：没有 pending_admin，直接把初始化标记清掉
    global _admin_kept
    if _admin_kept:
        config["system"]["init_required"] = False
        write_config(config)
        _admin_kept = False

        name = config.get("admin", {}).get("username", "admin")
        session_id = secrets.token_hex(32)
        sessions[session_id] = {"user": name, "expires": time.time() + SESSION_TTL}
        response = RedirectResponse(url=boot_target(), status_code=303)
        response.set_cookie(SESSION_COOKIE, session_id, httponly=True, samesite="lax",
                            secure=_is_https(request), max_age=SESSION_TTL)
        return response

    return RedirectResponse(url="/", status_code=303)


def _apply_pending_admin(request: Request, sess):
    """把第一步暂存的管理员写入配置，直接登录进入控制台。"""
    pending = sess.get("pending_admin") or {}

    config = read_config()
    config["admin"]["username"] = pending.get("username", "admin")
    config["admin"]["password"] = pending.get("password", "")
    config["system"]["init_required"] = False
    write_config(config)

    global _admin_kept
    _admin_kept = False

    sid = secrets.token_hex(32)
    sessions[sid] = {"user": pending.get("username", "admin"),
                     "expires": time.time() + SESSION_TTL}

    response = RedirectResponse(url=boot_target(), status_code=303)
    response.set_cookie(SESSION_COOKIE, sid, httponly=True, samesite="lax",
                        secure=_is_https(request), max_age=SESSION_TTL)
    return response

# 从 2.x 旧版本导入数据库（仅首次初始化时可用）
SOUND_OK_KEYS = {"bell", "blip", "chime", "coin", "bubble", "xylophone", "drop",
                 "tick", "chord", "sweepup", "typewriter", "doublebell", "custom"}
SOUND_FAIL_KEYS = {"soft", "buzz", "double", "descend", "triple", "woodblock",
                   "thud", "alarm", "custom"}


@app.post("/boot/settings")
async def boot_settings(request: Request, boot_mode: str = Form("every"),
                        ajax: str = Form(None), csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        if ajax:
            return ajax_login_required()
        return RedirectResponse(url="/login", status_code=303)

    config = read_config()
    config.setdefault("boot", {})
    config["boot"]["mode"] = boot_mode if boot_mode in BOOT_MODES else "every"
    if config["boot"]["mode"] == "every":
        config["boot"]["last_shown"] = ""      # 每次登录都播，不需要记录日期
    write_config(config)

    text = f"启动动画已设为「{BOOT_MODES[config['boot']['mode']]}」"
    if ajax:
        return {"ok": True, "text": text}

    sess["notice"] = {"kind": "success", "text": text}
    return RedirectResponse(url="/setting#desktop-card", status_code=303)


@app.post("/sound/settings")
async def sound_settings(request: Request, ok_sound: str = Form("chime"),
                         fail_sound: str = Form("triple"), ajax: str = Form(None),
                         csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        if ajax:
            return ajax_login_required()
        return RedirectResponse(url="/login", status_code=303)

    config = read_config()
    config.setdefault("sound", {})
    config["sound"]["ok"] = ok_sound if ok_sound in SOUND_OK_KEYS else "chime"
    config["sound"]["fail"] = fail_sound if fail_sound in SOUND_FAIL_KEYS else "triple"
    write_config(config)

    text = "核销提示音已保存，回到核销页扫一张就能听到"
    if ajax:
        return {"ok": True, "text": text}

    sess["notice"] = {"kind": "success", "text": text}
    return RedirectResponse(url="/setting#sound-card", status_code=303)


@app.post("/import_legacy")
def import_legacy(request: Request, legacy_path: str = Form(None),
                  csrf_token: str = Form(None)):
    global _init_notice
    check_csrf(request, csrf_token)

    config = read_config()
    init_mode = bool(config["system"].get("init_required"))
    sid, sess = get_session(request)

    # 初始化阶段无需登录；已初始化的系统必须登录后才能导入
    if not init_mode and not sess:
        raise HTTPException(status_code=403, detail="请先登录再导入旧数据")

    target = "/" if init_mode else "/setting"

    pending = bool(sess and sess.get("pending_admin"))

    def finish(kind, text):
        if init_mode:
            # 第二步已设置好管理员：导入成功后直接完成初始化并进入控制台
            if pending and kind == "success":
                return _apply_pending_admin(request, sess)
            return _finish_init_notice(kind, text)
        sess["notice"] = {"kind": kind, "text": text}
        return RedirectResponse(url=target, status_code=303)

    folder = (legacy_path or "").strip().strip('"')
    if not folder:
        picked = pick_folder("选择旧版程序安装目录")
        if picked is None:
            return finish("error", "打不开文件夹选择框，请在桌面版中使用该功能")
        if picked == "":
            # 用户点了取消：按原路返回，不做任何提示
            return RedirectResponse(url=target, status_code=303)
        folder = picked

    if not os.path.isdir(folder):
        return finish("error", f"路径不存在：{folder}")

    payload = find_legacy_payloads(folder)
    candidates = payload["databases"]

    # 老版本只有 scores.db；同版本互导时还会带 json，两者都没有才算失败
    if not candidates and not payload["devices"] and not payload["history"]:
        return finish("error", f"没有在该目录下找到 scores.db 或配置文件：{folder}")

    parts = []
    failed = None

    if candidates:
        src_path, _, _ = candidates[0]
        try:
            total, inserted = import_legacy_database(src_path)
            skipped = total - inserted
            piece = f"积分记录 {inserted} 条"
            if skipped > 0:
                piece += f"（跳过重复 {skipped} 条）"
            parts.append(piece)
        except Exception as exc:
            failed = f"积分数据库导入失败：{exc}"

    if payload["devices"]:
        try:
            total, added = import_legacy_devices(payload["devices"])
            parts.append(f"班级设备 {added} 台" + (f"（跳过重复 {total - added} 台）" if total > added else ""))
        except Exception as exc:
            parts.append(f"班级设备未导入（{exc}）")

    if payload["history"]:
        try:
            total, added = import_legacy_history(payload["history"])
            parts.append(f"印卡批次 {added} 个" + (f"（跳过重复 {total - added} 个）" if total > added else ""))
        except Exception as exc:
            parts.append(f"印卡批次未导入（{exc}）")

    if not parts:
        return finish("error", failed or "没有可导入的内容")

    integrity.audit("import_legacy", "导入完成：" + "，".join(parts), _client_ip(request))
    text = "导入完成：" + "，".join(parts)
    if failed:
        text += f"；{failed}"

    return finish("success", text)


def _finish_init_notice(kind, text):
    global _init_notice
    _init_notice = {"kind": kind, "text": text}
    return RedirectResponse(url="/", status_code=303)


# 提交管理员初始化
@app.post("/init_admin")
async def submit_init_admin(request: Request, username: str = Form(...), password: str = Form(...),
                            csrf_token: str = Form(None)):
    return await init_credentials(request, username, password, "", csrf_token)
    # 旧表单兼容：等价于第一步提交（下方逻辑保留，不再执行）
    check_csrf(request, csrf_token)
    config = read_config()
    # 只在尚未初始化时允许设置管理员，防止匿名用户覆盖已有账号
    if not config["system"].get("init_required"):
        raise HTTPException(status_code=403, detail="系统已初始化，请直接登录")

    username = (username or "").strip()
    system_name = config["system"]["name"]
    if not 1 <= len(username) <= 32:
        return render(request, "init_admin.html", {"system_name": system_name, "error": "用户名长度需为 1-32 个字符"})
    if len(password or "") < 4:
        return render(request, "init_admin.html", {"system_name": system_name, "error": "密码至少 4 位"})

    config["admin"]["username"] = username
    config["admin"]["password"] = hash_password(password)
    config["system"]["init_required"] = False
    write_config(config)

    # 初始化完成直接建立会话进入控制台，不再要求重新登录一次
    session_id = secrets.token_hex(32)
    sessions[session_id] = {"user": username, "expires": time.time() + SESSION_TTL}
    response = RedirectResponse(url=boot_target(), status_code=303)
    response.set_cookie(SESSION_COOKIE, session_id, httponly=True, samesite="lax",
                        secure=_is_https(request), max_age=SESSION_TTL)
    return response

# 登录页面
@app.get("/login", response_class=HTMLResponse)
async def login(request: Request):
    config = read_config()
    if config["system"]["init_required"]:
        return RedirectResponse(url="/")
    return render(request, "login.html", {"system_name": config["system"]["name"], "error": False})

# 提交登录
@app.post("/login")
async def submit_login(request: Request, username: str = Form(...), password: str = Form(...),
                       csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    config = read_config()
    system_name = config["system"]["name"]
    ip = _client_ip(request)

    wait = _login_wait_seconds(ip)
    if wait:
        return render(request, "login.html", {
            "system_name": system_name, "error": True,
            "message": f"登录失败次数过多，请 {wait} 秒后再试",
        })

    ok, needs_upgrade = verify_password(password or "", config["admin"]["password"])
    if config["admin"]["username"] != username or not ok:
        _note_login_failure(ip)
        integrity.audit("login_failed", f"用户名 {username}", ip)
        return render(request, "login.html", {"system_name": system_name, "error": True})

    _clear_login_failures(ip)
    integrity.audit("login", f"用户 {username}", ip)
    # 每次登录成功都先播放启动动画，再进入控制台
    # 历史明文口令登录成功后自动升级为 PBKDF2 哈希
    if needs_upgrade:
        config["admin"]["password"] = hash_password(password)
        write_config(config)

    session_id = secrets.token_hex(32)
    sessions[session_id] = {"user": username, "expires": time.time() + SESSION_TTL}
    response = RedirectResponse(url=boot_target(), status_code=303)
    response.set_cookie(SESSION_COOKIE, session_id, httponly=True, samesite="lax",
                        secure=_is_https(request), max_age=SESSION_TTL)
    return response

# 登出
@app.get("/logout")
async def logout(request: Request):
    session_id = request.cookies.get(SESSION_COOKIE)
    if session_id:
        sessions.pop(session_id, None)
    response = RedirectResponse(url="/login")
    response.delete_cookie(SESSION_COOKIE)
    return response

AUDIT_LABELS = {
    "login": "登录",
    "login_failed": "登录失败",
    "logout": "退出",
    "groups_save": "保存分组",
    "groups_auto": "自动分组",
    "students_bulk_delete": "批量删除学生",
    "bulk_delete_failed": "删除被拒",
    "import_legacy": "导入旧数据",
    "integrity_accept": "重新签名",
    "verify": "核销积分",
    "verify_failed": "核销失败",
    "generate": "生成积分",
    "settings_save": "保存设置",
    "device_add": "添加设备",
    "device_delete": "删除设备",
    "display": "投屏",
    "integrity": "完整性告警",
}


def _dashboard_stats():
    """控制台要用的统计数字。"""
    today = datetime.datetime.now().strftime("%Y-%m-%d")

    stats = {
        "students": 0,
        "boys": 0,
        "girls": 0,
        "groups": 0,
        "ungrouped": 0,
        "cards_total": 0,
        "cards_used": 0,
        "cards_unused": 0,
        "cards_today": 0,
    }

    db = connect_db()
    try:
        rows = db.execute("SELECT gender, IFNULL(group_id, 0) AS gid FROM students").fetchall()
        stats["students"] = len(rows)
        for r in rows:
            gender = (r["gender"] or "").strip()
            if gender == "男":
                stats["boys"] += 1
            elif gender == "女":
                stats["girls"] += 1
            if not r["gid"]:
                stats["ungrouped"] += 1

        stats["groups"] = db.execute("SELECT COUNT(*) FROM student_groups").fetchone()[0] or 0

        rows = db.execute("SELECT status, verified_at FROM scores").fetchall()
        stats["cards_total"] = len(rows)
        for r in rows:
            status = (r["status"] or "").strip()
            used = status in ("已作废", "已核销") or bool(r["verified_at"])
            if used:
                stats["cards_used"] += 1
                if (r["verified_at"] or "").startswith(today):
                    stats["cards_today"] += 1
        stats["cards_unused"] = stats["cards_total"] - stats["cards_used"]
    finally:
        db.close()

    return stats


@app.get("/api/dashboard/devices")
def dashboard_devices(request: Request):
    """控制台设备面板：在线状态、摄像头/麦克风、视频帧率。"""
    _require_login(request)

    out = []
    for dev in devices.list_devices():
        state = devices.device_state(dev)
        agent = state.get("agent") or {}
        video = state.get("video") or {}

        out.append({
            "id": state["id"],
            "name": state["name"],
            "room": state["room"],
            "host": state["host"],
            "port": state["port"],
            "reachable": state["reachable"],
            "error": state["error"],
            "camera": agent.get("cameraEnabled"),
            "mic": agent.get("micEnabled"),
            "needSetup": agent.get("needSetup"),
            "version": agent.get("version"),
            "fps": video.get("fps", 0),
            "frames": video.get("frames", 0),
        })

    online = len([d for d in out if d["reachable"]])
    return JSONResponse({"ok": True, "online": online, "total": len(out), "devices": out})


# 首页
@app.get("/index", response_class=HTMLResponse)
async def index(request: Request):
    user = get_current_user(request)
    if not user:
        return RedirectResponse(url="/login")
    config = read_config()

    stats = _dashboard_stats()
    stats["device_total"] = len(devices.list_devices())

    audit_rows = []
    try:
        for line in reversed(integrity.audit_tail(30)):
            parts = line.split("\t")
            if len(parts) < 2:
                continue
            audit_rows.append({
                "time": parts[0],
                "event": parts[1],
                "label": AUDIT_LABELS.get(parts[1], parts[1]),
                "ip": parts[2] if len(parts) > 2 else "-",
                "detail": parts[3] if len(parts) > 3 else "",
            })
            if len(audit_rows) >= 8:
                break
    except Exception:
        audit_rows = []

    return render(request, "index.html", {
        "system_name": config["system"]["name"],
        "stats": stats,
        "audit_rows": audit_rows,
    })

# 班级监控页面
@app.get("/monitor", response_class=HTMLResponse)
async def monitor(request: Request):
    user = get_current_user(request)
    if not user:
        return RedirectResponse(url="/login")
    config = read_config()
    system_name = config["system"]["name"]
    return render(request, "monitor.html", {
        "system_name": system_name,
        "devices": _public_devices(),
    })

# ==================== 班级设备（一体机端）对接 ====================

@app.on_event("startup")
def _start_device_video_receiver():
    """启动 UDP 视频订阅（视频走 UDP，其余接口走 TCP）。"""
    try:
        devices.start()
    except Exception as e:
        print("视频订阅启动失败:", e)


def _public_devices():
    """给前端用的设备清单（不含访问密钥）。"""
    return [{
        "id": d["id"], "name": d["name"], "room": d["room"],
        "host": d["host"], "port": d["port"],
        "video_port": devices.video_port_of(d),
        "enabled": d.get("enabled", True),
    } for d in devices.list_devices()]


def _require_login(request: Request):
    user = get_current_user(request)
    if not user:
        raise HTTPException(status_code=401, detail="未登录")
    return user


@app.get("/devices/api/list")
async def devices_api_list(request: Request):
    """设备清单（本地数据，秒回；状态由前端并发拉取）。"""
    _require_login(request)
    return JSONResponse([{
        "id": d["id"], "name": d["name"], "room": d["room"],
        "host": d["host"], "port": d["port"],
        "video_port": devices.video_port_of(d),
        "enabled": d.get("enabled", True),
    } for d in devices.list_devices()])


@app.get("/device/{device_id}/screen")
async def device_screen_stream(device_id: str, request: Request):
    """一体机的屏幕直播（MJPEG 代理）。"""
    _require_login(request)
    dev = devices.get_device(device_id)
    if not dev:
        raise HTTPException(status_code=404, detail="设备不存在")

    host = dev.get("host") or ""
    port = dev.get("port") or 3457
    url = f"http://{host}:{port}/api/screen/stream"
    key = dev.get("key") or ""

    def gen():
        req = urllib.request.Request(url, headers={"X-Access-Key": key})
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                while True:
                    chunk = resp.read(8192)
                    if not chunk:
                        break
                    yield chunk
        except Exception:
            return

    return StreamingResponse(gen(), media_type="multipart/x-mixed-replace; boundary=frame",
                             headers={"Cache-Control": "no-store"})


@app.get("/device/{device_id}/mic")
async def device_mic_stream(device_id: str, request: Request):
    """把一体机的麦克风 WAV 流转给浏览器（教师端「听教室」用）。"""
    _require_login(request)
    dev = devices.get_device(device_id)
    if not dev:
        raise HTTPException(status_code=404, detail="设备不存在")

    host = dev.get("host") or ""
    port = dev.get("port") or 3457
    url = f"http://{host}:{port}/api/mic/stream"
    key = dev.get("key") or ""

    def gen():
        req = urllib.request.Request(url, headers={"X-Access-Key": key})
        try:
            with urllib.request.urlopen(req, timeout=10) as resp:
                while True:
                    chunk = resp.read(4096)
                    if not chunk:
                        break
                    yield chunk
        except Exception:
            return

    return StreamingResponse(gen(), media_type="audio/wav",
                             headers={"Cache-Control": "no-store"})


@app.post("/device/{device_id}/audio")
async def device_audio_in(device_id: str, request: Request):
    """教师端实时对讲：把浏览器采集的 PCM 转发给一体机播放。"""
    check_csrf(request, None)          # 令牌走请求头（body 是二进制）
    _require_login(request)

    dev = devices.get_device(device_id)
    if not dev:
        raise HTTPException(status_code=404, detail="设备不存在")

    pcm = await request.body()
    if not pcm:
        return JSONResponse({"ok": False, "error": "空音频"})

    host = dev.get("host") or ""
    port = dev.get("port") or 3457
    url = f"http://{host}:{port}/api/audio/in"
    req = urllib.request.Request(
        url, data=pcm, method="POST",
        headers={"X-Access-Key": dev.get("key") or "",
                 "Content-Type": "application/octet-stream"},
    )
    try:
        with urllib.request.urlopen(req, timeout=5) as resp:
            return JSONResponse(json.loads(resp.read().decode("utf-8")))
    except Exception as e:
        return JSONResponse({"ok": False, "error": str(e)})


@app.post("/device/{device_id}/display")
async def device_display(device_id: str, request: Request, csrf_token: str = Form(None)):
    """把「小组换座」画面推给一体机全屏展示，或让它停止展示。"""
    check_csrf(request, csrf_token)
    _require_login(request)

    dev = devices.get_device(device_id)
    if not dev:
        raise HTTPException(status_code=404, detail="设备不存在")

    form = await request.form()
    action = (form.get("action") or "show").strip()

    if action == "hide":
        try:
            devices.agent_request(dev, "POST", "/api/display/hide", {})
        except devices.AgentError as exc:
            return JSONResponse({"ok": False, "error": str(exc)})
        return JSONResponse({"ok": True, "showing": False})

    raw = form.get("payload") or "{}"
    try:
        payload = json.loads(raw)
    except Exception:
        return JSONResponse({"ok": False, "error": "投屏数据格式不对"})

    try:
        devices.agent_request(dev, "POST", "/api/display/show", payload)
    except devices.AgentError as exc:
        return JSONResponse({"ok": False, "error": str(exc)})

    return JSONResponse({"ok": True, "showing": True})


@app.post("/device/{device_id}/settings")
async def device_remote_settings(device_id: str, request: Request,
                                 room_name: str = Form(""), device_name: str = Form(""),
                                 width: str = Form(""), height: str = Form(""),
                                 fps: str = Form(""), quality: str = Form(""),
                                 chime_before: str = Form(""), chime_after: str = Form(""),
                                 chime_id: str = Form(""),
                                 speak_volume: str = Form(""),
                                 csrf_token: str = Form(None)):
    """远程下发基础设置：教室名、设备名、摄像头分辨率/帧率/画质。"""
    check_csrf(request, csrf_token)
    _require_login(request)

    dev = devices.get_device(device_id)
    if not dev:
        raise HTTPException(status_code=404, detail="设备不存在")

    payload = {}
    if room_name.strip() or device_name.strip():
        payload["roomName"] = room_name.strip()
        payload["deviceName"] = device_name.strip()

    cam = {}
    try:
        if width:
            cam["width"] = int(width)
        if height:
            cam["height"] = int(height)
        if fps:
            cam["fps"] = int(fps)
        if quality:
            cam["quality"] = int(quality)
    except ValueError:
        return JSONResponse({"ok": False, "error": "分辨率/帧率/画质必须是数字"})

    if cam:
        cam["enabled"] = True
        payload["camera"] = cam

    def _flag(v: str):
        return str(v).strip().lower() in ("1", "true", "on", "yes")

    speak = {}
    if chime_before != "":
        speak["chimeBefore"] = _flag(chime_before)
    if chime_after != "":
        speak["chimeAfter"] = _flag(chime_after)
    if chime_id.strip():
        speak["chimeId"] = chime_id.strip()
    if speak_volume.strip():
        try:
            speak["volume"] = max(-1, min(100, int(speak_volume)))
        except ValueError:
            return JSONResponse({"ok": False, "error": "播报音量需要是 0-100 的整数"})
    if speak:
        payload["speak"] = speak

    if not payload:
        return JSONResponse({"ok": False, "error": "没有要修改的内容"})

    try:
        res = devices.agent_request(dev, "POST", "/api/settings", payload, timeout=5)
    except devices.AgentError as e:
        return JSONResponse({"ok": False, "error": str(e)})

    return JSONResponse({"ok": True, "result": res})


@app.post("/device/{device_id}/audio-stop")
async def device_audio_stop(device_id: str, request: Request):
    check_csrf(request, None)
    _require_login(request)
    dev = devices.get_device(device_id)
    if not dev:
        raise HTTPException(status_code=404, detail="设备不存在")

    host = dev.get("host") or ""
    port = dev.get("port") or 3457
    try:
        devices.agent_request(dev, "POST", "/api/audio/stop", {})
    except devices.AgentError as e:
        return JSONResponse({"ok": False, "error": str(e)})
    return JSONResponse({"ok": True})


@app.get("/device/{device_id}/frame")
async def device_frame(device_id: str, request: Request):
    _require_login(request)
    jpeg = devices.latest_frame(device_id)
    if not jpeg:
        raise HTTPException(status_code=503, detail="暂无画面")
    return Response(content=jpeg, media_type="image/jpeg",
                    headers={"Cache-Control": "no-store", "X-Frame-Source": "udp"})


@app.get("/device/{device_id}/stream")
async def device_stream(device_id: str, request: Request):
    """MJPEG 输出：数据来自 UDP 收到的帧，浏览器可直接用 <img> 播放。"""
    _require_login(request)

    def gen():
        last_ts = 0
        idle = 0
        while True:
            jpeg = devices.latest_frame(device_id)
            ts = devices.frame_timestamp(device_id)
            if jpeg and ts != last_ts:
                last_ts = ts
                idle = 0
                head = (b"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: "
                        + str(len(jpeg)).encode() + b"\r\n\r\n")
                yield head + jpeg + b"\r\n"
            else:
                idle += 1
                if idle > 300:      # 约 18 秒无新帧，结束本次输出
                    return
            time.sleep(0.06)

    return StreamingResponse(gen(), media_type="multipart/x-mixed-replace; boundary=frame",
                             headers={"Cache-Control": "no-store"})


@app.get("/device/{device_id}/status")
async def device_status(device_id: str, request: Request):
    _require_login(request)
    dev = devices.get_device(device_id)
    if not dev:
        raise HTTPException(status_code=404, detail="设备不存在")

    st = devices.device_state(dev)
    detail = None
    if st["reachable"]:
        try:
            detail = devices.agent_request(dev, "GET", "/api/state", timeout=3)
        except devices.AgentError as e:
            st["error"] = str(e)
    st["detail"] = detail
    return JSONResponse(st)


@app.post("/device/{device_id}/action")
async def device_action(device_id: str, request: Request, rate: int = Form(0),
                        action: str = Form(...), text: str = Form(""),
                        command: str = Form(""), enabled: str = Form("1"),
                        csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    _require_login(request)
    dev = devices.get_device(device_id)
    if not dev:
        raise HTTPException(status_code=404, detail="设备不存在")

    # 摄像头 / 麦克风 / 远程执行授权只能在一体机本机改，管理端不提供远程开关
    local_only = {"camera_on", "camera_off", "mic_on", "mic_off", "remote_on", "remote_off"}
    if action in local_only:
        return JSONResponse({"ok": False,
                             "error": "该设置只能在一体机本机操作，不能远程修改"},
                            status_code=403)

    try:
        if action == "speak":
            res = devices.agent_request(dev, "POST", "/api/announce",
                                        {"text": text, "rate": max(-10, min(10, rate))}, timeout=8)
        elif action == "speak_stop":
            res = devices.agent_request(dev, "POST", "/api/speak/stop", {})
        elif action == "exec":
            res = devices.agent_request(dev, "POST", "/api/exec", {"command": command}, timeout=20)
        elif action == "autostart":
            want = (enabled or "1").lower() not in ("0", "false", "off", "no")
            res = devices.agent_request(dev, "POST", "/api/autostart", {"enabled": want})
        elif action == "shutdown":
            try:
                res = devices.agent_request(dev, "POST", "/api/power/off", {}, timeout=3)
            except devices.AgentError:
                res = {"ok": True, "action": "shutdown"}
        elif action == "open_url":
            target = (text or "").strip()
            if not target.lower().startswith(("http://", "https://")):
                return JSONResponse({"ok": False,
                                     "error": "网址必须以 http:// 或 https:// 开头"},
                                    status_code=400)
            res = devices.agent_request(dev, "POST", "/api/browser/open", {"url": target})
        elif action == "reboot":
            try:
                res = devices.agent_request(dev, "POST", "/api/power/reboot", {}, timeout=3)
            except devices.AgentError:
                res = {"ok": True, "action": "reboot"}
        elif action == "restart":
            try:
                res = devices.agent_request(dev, "POST", "/api/restart", {}, timeout=3)
            except devices.AgentError:
                # 重启会断开当前连接，属于正常现象
                res = {"ok": True, "restarting": True}
        elif action == "identify":
            res = devices.agent_request(dev, "POST", "/api/speak", {"text": "这是一体机端测试播报"})
        else:
            return JSONResponse({"ok": False, "error": "未知操作"}, status_code=400)
    except devices.AgentError as e:
        return JSONResponse({"ok": False, "error": str(e)})

    return JSONResponse({"ok": True, "result": res})


def _parse_config_code(raw: str):
    """解析一体机端生成的一键配置码，尽量容错。

    返回 (data, error)。data 是 dict 或 None；error 是给用户看的说明。
    """
    if raw is None:
        return None, "没有收到内容"
    text = raw.strip()
    if not text:
        return None, "内容为空"

    # 1) 直接是 JSON
    if text.startswith("{"):
        try:
            obj = json.loads(text)
            if isinstance(obj, dict):
                return obj, None
        except Exception:
            pass

    # 2) 去掉所有空白与非 base64 字符（前缀、换行、引号等）
    alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_+/="
    cleaned = "".join(ch for ch in text if ch in alphabet)
    if not cleaned:
        return None, "里面没有可识别的内容"

    # 3) base64url → base64，补齐 padding
    b64 = cleaned.replace("-", "+").replace("_", "/")
    pad = (-len(b64)) % 4
    b64_padded = b64 + ("=" * pad)

    last_err = None
    for encoding in ("utf-8", "gbk"):
        try:
            data = base64.b64decode(b64_padded)
            decoded = data.decode(encoding)
            obj = json.loads(decoded)
            if isinstance(obj, dict):
                return obj, None
            last_err = "解出来的不是配置数据"
        except Exception as e:
            last_err = str(e)

    # 4) 人类可读格式（「复制对接信息」那种）
    human = _parse_human_code(text)
    if human:
        return human, None

    preview = text[:40].replace("\n", " ")
    return None, f"无法识别（长度 {len(text)}，开头「{preview}」）：{last_err}"


def _parse_human_code(text):
    """解析「教室：xx / 设备：xx / 地址：http://ip:port/ / 密钥：xx」这种文本。

    单行输入框会把换行吃掉，所以不能按行切；用标签正则提取，换行有没有都能认。
    """
    label = (r"(?:教室\s*/\s*班级|教室|班级|设备名称|设备名|设备|"
             r"局域网地址|本机地址|地址|访问密钥|密钥)")
    pairs = re.findall(r"(" + label + r")\s*[：:]\s*(.*?)(?=" + label + r"\s*[：:]|$)",
                       text, re.S)

    data = {}
    for key, value in pairs:
        key = key.strip()
        value = value.strip()
        if value in ("未设置", "-", "—"):
            value = ""

        if key in ("教室", "班级", "教室 / 班级", "教室/班级"):
            data["r"] = value
        elif key in ("设备", "设备名", "设备名称"):
            data["n"] = value
        elif key in ("密钥", "访问密钥"):
            data["k"] = value
        else:
            m = re.match(r"https?://([^/:\s]+)(?::(\d+))?", value)
            if not m:
                m = re.match(r"([^/:\s]+)(?::(\d+))?", value)
            if m:
                data["h"] = m.group(1)
                if m.group(2):
                    data["p"] = int(m.group(2))

    if data.get("k") or data.get("h"):
        data.setdefault("v", 1)
        return data
    return None


@app.post("/devices/parse-code")
async def devices_parse_code(request: Request, code: str = Form(""), csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    _require_login(request)

    data, error = _parse_config_code(code)
    if error:
        return JSONResponse({"ok": False, "error": error})

    return JSONResponse({
        "ok": True,
        "device": {
            "name": str(data.get("n") or ""),
            "room": str(data.get("r") or ""),
            "host": str(data.get("h") or ""),
            "port": int(data.get("p") or 3457),
            "video_port": int(data.get("vp") or 0),
            "key": str(data.get("k") or ""),
        },
    })


@app.post("/devices/add")
async def devices_add(request: Request, name: str = Form(...), room: str = Form(""),
                      host: str = Form(...), port: str = Form("3457"),
                      key: str = Form(""), video_port: str = Form("0"),
                      csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    _require_login(request)
    host = (host or "").strip()
    if not host:
        return JSONResponse({"ok": False, "error": "请填写 IP 地址"}, status_code=400)
    dev = devices.add_device(name.strip() or host, room.strip(), host,
                             int(port or 3457), key.strip(), int(video_port or 0))
    return JSONResponse({"ok": True, "device": dev})


@app.post("/devices/update")
async def devices_update(request: Request, device_id: str = Form(...),
                         name: str = Form(""), room: str = Form(""),
                         host: str = Form(""), port: str = Form(""),
                         key: str = Form(""), video_port: str = Form(""),
                         csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    _require_login(request)
    dev = devices.update_device(
        device_id,
        name=name.strip() or None,
        room=room.strip(),
        host=host.strip() or None,
        port=int(port) if port else None,
        key=key.strip() if key.strip() else None,
        video_port=int(video_port) if video_port != "" else None,
    )
    if not dev:
        return JSONResponse({"ok": False, "error": "设备不存在"}, status_code=404)
    return JSONResponse({"ok": True, "device": dev})


@app.post("/devices/delete")
async def devices_delete(request: Request, device_id: str = Form(...), csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    _require_login(request)
    devices.delete_device(device_id)
    return JSONResponse({"ok": True})


@app.post("/devices/probe")
async def devices_probe(request: Request, host: str = Form(...), port: str = Form("3457"),
                        key: str = Form(""), csrf_token: str = Form(None)):
    """添加设备前先试连一次，避免填错 IP/密钥。"""
    check_csrf(request, csrf_token)
    _require_login(request)
    tmp = {"host": host.strip(), "port": int(port or 3457), "key": key.strip()}
    try:
        info = devices.agent_request(tmp, "GET", "/api/info", timeout=3)
    except devices.AgentError as e:
        return JSONResponse({"ok": False, "error": str(e)})

    state = None
    if key.strip():
        try:
            state = devices.agent_request(tmp, "GET", "/api/state", timeout=3)
        except devices.AgentError as e:
            return JSONResponse({"ok": True, "info": info, "auth": False,
                                 "error": "密钥校验失败：" + str(e)})

    return JSONResponse({"ok": True, "info": info, "auth": state is not None, "state": state})


# 班级叫号页面
@app.get("/call", response_class=HTMLResponse)
async def call_number(request: Request):
    user = get_current_user(request)
    if not user:
        return RedirectResponse(url="/login")
    config = read_config()
    system_name = config["system"]["name"]
    return render(request, "call.html", {
        "system_name": system_name,
        "devices": _public_devices(),
    })

# 一体机控制页面
@app.get("/kiosk", response_class=HTMLResponse)
async def kiosk(request: Request):
    user = get_current_user(request)
    if not user:
        return RedirectResponse(url="/login")
    config = read_config()
    system_name = config["system"]["name"]
    return render(request, "kiosk.html", {
        "system_name": system_name,
        "devices": _public_devices(),
    })

# 生成积分页面
@app.get("/generate", response_class=HTMLResponse)
async def generate(request: Request):
    user = get_current_user(request)
    if not user:
        return RedirectResponse(url="/login")
    config = read_config()
    system_name = config["system"]["name"]
    return render(request, "generate.html", {
        "system_name": system_name,
        "success": False,
        "history": history_pending(),
        "printer_value": (config.get("print", {}) or {}).get("printer", "") or "",
        # 每页排版参数：页面用来提示「生成多少张正好整页，不浪费纸」
        "rows_per_page": CARD_ROWS_PER_PAGE,
        "cols_per_page": CARD_COLUMNS_PER_PAGE,
        "cards_per_page": CARD_ROWS_PER_PAGE * CARD_COLUMNS_PER_PAGE,
    })

# 提交生成积分（结果存会话，避免多人共用时互相串号）
@app.post("/generate")
async def submit_generate(request: Request, class_name: str = Form(...), title: str = Form(...),
                          amount: int = Form(...), quantity: int = Form(...),
                          csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login", status_code=303)

    config = read_config()
    system_name = config["system"]["name"]
    salt = config["system"]["encryption_salt"]

    class_name = (class_name or "").strip()
    title = (title or "").strip()
    if not class_name or not title:
        return render(request, "generate.html", {
        "rows_per_page": CARD_ROWS_PER_PAGE,
        "cols_per_page": CARD_COLUMNS_PER_PAGE,
        "cards_per_page": CARD_ROWS_PER_PAGE * CARD_COLUMNS_PER_PAGE,
            "system_name": system_name, "success": False, "error": "班级名称和积分卡标题都不能为空"})
    if amount <= 0:
        return render(request, "generate.html", {
            "system_name": system_name, "success": False, "error": "单张面额必须大于 0"})
    if not 1 <= quantity <= MAX_QUANTITY:
        return render(request, "generate.html", {
            "system_name": system_name, "success": False,
            "error": f"单次生成数量需在 1-{MAX_QUANTITY} 之间"})

    init_db()
    issue_date = datetime.datetime.now().strftime("%Y-%m-%d")
    codes = []

    conn = connect_db()
    try:
        with conn:
            for _ in range(quantity):
                # 短码撞车概率极低，但一旦撞车就重生成，保证 Word 上每张卡都能核销
                while True:
                    short_code, hash_val = generate_score_code(class_name, title, amount, salt)
                    cur = conn.execute(
                        "INSERT OR IGNORE INTO scores (class_name, title, amount, code, hash, issue_date, status)"
                        " VALUES (?, ?, ?, ?, ?, ?, ?)",
                        (class_name, title, amount, short_code, hash_val, issue_date, "未使用")
                    )
                    if cur.rowcount == 1:
                        codes.append(short_code)
                        break
    finally:
        conn.close()

    # 导出Word
    filepath = export_to_word(class_name, title, amount, codes, issue_date)

    batch = {
        "id": uuid.uuid4().hex[:12],
        "filename": os.path.basename(filepath),
        "class_name": class_name,
        "title": title,
        "amount": amount,
        "quantity": quantity,
        "issue_date": issue_date,
        "created_at": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "codes": codes,
        "printed": False,
        "saved": False,
    }
    history_add(batch)

    sess["gen"] = {
        "system_name": system_name,
        "batch_id": batch["id"],
        "filepath": filepath,
        "filename": os.path.basename(filepath),
        "quantity": quantity,
        "class_name": class_name,
        "title": title,
        "amount": amount,
        "issue_date": issue_date,
        "codes": codes,
    }
    return RedirectResponse(url="/generate_result", status_code=303)

# 生成结果页（防止刷新重复提交）
@app.get("/generate_result", response_class=HTMLResponse)
async def generate_result(request: Request):
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login")
    result = sess.get("gen")
    if not result:
        return RedirectResponse(url="/generate")
    notice = sess.pop("gen_notice", None)
    return render(request, "generate.html", {
        "system_name": result["system_name"],
        "success": True,
        "filepath": result["filepath"],
        "filename": result["filename"],
        "quantity": result["quantity"],
        "notice": notice,
        "printer_value": (read_config().get("print", {}) or {}).get("printer", "") or "",
        "history": [h for h in history_pending() if h.get("id") != result.get("batch_id")],
        "batch_id": result.get("batch_id", ""),
    })

# 核销页面
@app.get("/verify", response_class=HTMLResponse)
async def verify(request: Request):
    user = get_current_user(request)
    if not user:
        return RedirectResponse(url="/login")
    config = read_config()
    system_name = config["system"]["name"]
    sound = config.get("sound", {}) or {}
    return render(request, "verify.html", {
        "system_name": system_name, "results": None, "total_amount": 0, "error": None,
        "sound_ok": sound.get("ok", "chime"), "sound_fail": sound.get("fail", "triple"),
    })

# 提交核销
@app.post("/verify")
async def submit_verify(request: Request, codes: str = Form(None), csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login", status_code=303)
    config = read_config()
    system_name = config["system"]["name"]

    # 空编码检查
    if not codes or not codes.strip():
        return render(request, "verify.html", {
            "system_name": system_name,
            "results": None,
            "total_amount": 0,
            "error": "请输入或扫描积分编号后再核销",
        })

    init_db()
    code_list = [code.strip().upper() for code in codes.split() if code.strip()]
    # 单次核销条数上限，避免一次提交把数据库/线程占满
    if len(code_list) > MAX_QUANTITY:
        return render(request, "verify.html", {
            "system_name": system_name, "results": None, "total_amount": 0,
            "error": f"单次核销数量不能超过 {MAX_QUANTITY} 条",
        })

    results = []
    total_amount = 0
    verified_now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")

    conn = connect_db()
    try:
        for i, code in enumerate(code_list, 1):
            score = conn.execute(
                "SELECT * FROM scores WHERE code = ? OR hash = ?", (code, code)
            ).fetchone()

            if not score:
                results.append({"index": i, "code": code, "amount": 0,
                                "status": "错误", "message": "伪造编码",
                                "verified_at": None, "time_text": ""})
                continue

            # 条件更新 + rowcount 判定：并发/重复提交时只有一次能成功，杜绝一卡多花
            cur = conn.execute(
                "UPDATE scores SET status = '已作废', verified_at = ?"
                " WHERE code = ? AND status = '未使用'",
                (verified_now, score["code"])
            )
            if cur.rowcount == 1:
                results.append({"index": i, "code": code, "amount": score["amount"],
                                "status": "成功", "message": "核销成功",
                                "verified_at": verified_now, "time_text": verified_now})
                total_amount += score["amount"]
            else:
                # 已核销的卡：回显当初的核销时间（早于该字段的老数据标记为「未记录」）
                redeemed_at = score["verified_at"]
                results.append({"index": i, "code": code, "amount": score["amount"],
                                "status": "错误", "message": "已核销编码",
                                "verified_at": redeemed_at,
                                "time_text": redeemed_at or "未记录"})
        conn.commit()
    finally:
        conn.close()

    sess["verify"] = {
        "system_name": system_name,
        "results": results,
        "total_amount": total_amount,
    }
    return RedirectResponse(url="/verify_result", status_code=303)

# 单个核销：扫码枪回车即提交，返回 JSON 供页面逐条追加
@app.post("/verify/one")
def verify_one(request: Request, code: str = Form(""), csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        raise HTTPException(status_code=403, detail="请先登录")

    raw = (code or "").strip().upper()
    if not raw:
        return {"ok": False, "code": "", "message": "没有读到编号"}

    init_db()
    conn = connect_db()
    try:
        score = conn.execute(
            "SELECT * FROM scores WHERE code = ? OR hash = ?", (raw, raw)
        ).fetchone()
        if not score:
            return {"ok": False, "code": raw, "amount": 0,
                    "message": "伪造编码", "time": ""}

        now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        cur = conn.execute(
            "UPDATE scores SET status = '已作废', verified_at = ?"
            " WHERE code = ? AND status = '未使用'",
            (now, score["code"])
        )
        conn.commit()

        if cur.rowcount == 1:
            return {"ok": True, "code": score["code"], "amount": score["amount"],
                    "class_name": score["class_name"], "title": score["title"],
                    "message": "核销成功", "time": now}

        redeemed = score["verified_at"]
        return {"ok": False, "code": score["code"], "amount": score["amount"],
                "class_name": score["class_name"], "title": score["title"],
                "message": "已核销编码", "time": redeemed or "未记录"}
    finally:
        conn.close()


# 核销结果页（防止刷新重复提交）
@app.get("/verify_result", response_class=HTMLResponse)
async def verify_result(request: Request):
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login")
    result = sess.pop("verify", None)
    if not result:
        return RedirectResponse(url="/verify")
    results = result["results"]
    ok_count = sum(1 for r in results if r.get("status") == "成功")
    sound = (read_config().get("sound", {}) or {})
    return render(request, "verify.html", {
        "system_name": result["system_name"],
        "sound_ok": sound.get("ok", "chime"),
        "sound_fail": sound.get("fail", "triple"),
        "results": results,
        "ok_count": ok_count,
        "fail_count": len(results) - ok_count,
        "total_amount": result["total_amount"],
        "error": None,
    })

# 设置页面（GET，读取本会话上次结果）
@app.get("/setting", response_class=HTMLResponse)
async def setting(request: Request):
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login")
    config = read_config()
    system_name = config["system"]["name"]
    result = sess.pop("setting", None)
    notice = sess.pop("notice", None)

    backup_cfg = config.get("backup", {}) or {}
    backup_time = backup_cfg.get("time", "02:00") or "02:00"
    if not re.fullmatch(r"\d{1,2}:\d{2}", backup_time):
        backup_time = "02:00"
    printers, default_printer = list_printers()
    print_cfg = config.get("print", {}) or {}
    out_count, out_size = output_stats()
    junk_count, junk_size = junk_stats()

    return render(request, "setting.html", {
        "system_name": system_name,
        "integrity_alert": integrity.last_alert(),
        "audit_lines": integrity.audit_tail(12),
        "success": result == "success",
        "wrong_password": result == "wrong_password",
        "reset_wrong_password": result == "reset_wrong_password",
        "message": "新密码至少需要 4 位" if result == "weak" else None,
        "notice": notice,
        "backup_count": len(list_backups()),
        "last_backup": last_backup_time(),
        "backup_dir": get_backup_dir(),
        "boot_mode": (config.get("boot", {}) or {}).get("mode", "every"),
        "boot_modes": BOOT_MODES,
        "sound_ok": (config.get("sound", {}) or {}).get("ok", "chime"),
        "sound_fail": (config.get("sound", {}) or {}).get("fail", "triple"),
        "backup_time": backup_time,
        "backup_enabled": bool(backup_cfg.get("enabled", True)),
        "next_backup": next_backup_time(),
        "backup_keep": backup_cfg.get("keep", 10),
        "autostart": bool((config.get("desktop") or {}).get("autostart", False)),
        "printers": printers,
        "default_printer": default_printer,
        "printer_value": print_cfg.get("printer", "") or "",
        "devices": devices.list_devices(),
        "device_default_port": 3457,
        "cache_count": out_count,
        "cache_size": human_size(out_size),
        "junk_count": junk_count,
        "junk_size": human_size(junk_size),
    })

# 提交修改密码
# ==================== 学生名单 ====================

STUDENT_HEADER_HINTS = {
    "name": ("姓名", "名字", "学生", "学生姓名", "name"),
    "gender": ("性别", "男女", "sex", "gender"),
    "number": ("学号", "编号", "序号", "座号", "number", "no"),
    "class": ("班级", "班", "class"),
}


def _norm_gender(value):
    v = str(value or "").strip()
    if v in ("男", "男生", "M", "m", "male", "Male", "1"):
        return "男"
    if v in ("女", "女生", "F", "f", "female", "Female", "0"):
        return "女"
    return ""


def _student_conn():
    init_db()
    return connect_db()


def list_students(keyword=""):
    kw = (keyword or "").strip()
    with _student_conn() as db:
        if kw:
            rows = db.execute(
                "SELECT id, name, gender, number, class_name FROM students"
                " WHERE name LIKE ? OR number LIKE ?"
                " ORDER BY sort_order, id",
                (f"%{kw}%", f"%{kw}%")).fetchall()
        else:
            rows = db.execute(
                "SELECT id, name, gender, number, class_name FROM students"
                " ORDER BY sort_order, id").fetchall()
    return [dict(r) for r in rows]


def add_students(items):
    """items: [(name, gender, number, class_name)]；按 姓名+班级 去重，返回 (总数, 新增)。"""
    init_db()
    db = connect_db()
    total, added = 0, 0
    try:
        with db:
            for name, gender, number, cls in items:
                name = (name or "").strip()
                if not name:
                    continue
                total += 1
                exists = db.execute(
                    "SELECT 1 FROM students WHERE name=?", (name,)).fetchone()
                if exists:
                    continue
                db.execute(
                    "INSERT INTO students (name, gender, number, class_name, created_at)"
                    " VALUES (?, ?, ?, ?, ?)",
                    (name, gender or "", str(number or "").strip(), (cls or "").strip(),
                     datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")))
                added += 1
    finally:
        db.close()
    return total, added


def parse_students_file(filename, content):
    """解析上传的表格：支持 .xlsx/.xlsm 与 .csv，返回 [(name, gender, number, class)]。"""
    ext = (os.path.splitext(filename or "")[1] or "").lower()
    rows = []

    if ext in (".csv", ".txt"):
        text = None
        for enc in ("utf-8-sig", "utf-8", "gbk", "gb18030"):
            try:
                text = content.decode(enc)
                break
            except Exception:
                continue
        if text is None:
            raise ValueError("CSV 编码无法识别（请另存为 UTF-8 或 GBK）")
        rows = [[c for c in r] for r in csv.reader(io.StringIO(text))]
    elif ext in (".xlsx", ".xlsm"):
        try:
            import openpyxl
        except ImportError:
            raise ValueError("缺少 openpyxl，无法读取 Excel；可改用 CSV 格式导入")
        wb = openpyxl.load_workbook(io.BytesIO(content), read_only=True, data_only=True)
        ws = wb.active
        for row in ws.iter_rows(values_only=True):
            rows.append(list(row))
        wb.close()
    else:
        raise ValueError("只支持 .xlsx / .xlsm / .csv 文件")

    rows = [r for r in rows if any(str(c or "").strip() for c in r)]
    if not rows:
        raise ValueError("表格是空的")

    # 表头识别：找不到就用「第 1 列姓名、第 2 列性别」的默认顺序
    header = [str(c or "").strip().lower() for c in rows[0]]
    col = {}
    for key, hints in STUDENT_HEADER_HINTS.items():
        for idx, cell in enumerate(header):
            if any(h.lower() in cell for h in hints):
                col[key] = idx
                break

    has_header = "name" in col
    body = rows[1:] if has_header else rows

    if not has_header:
        col = {"name": 0, "gender": 1, "number": 2, "class": 3}

    items = []
    for r in body:
        def cell(key):
            idx = col.get(key)
            if idx is None or idx >= len(r):
                return ""
            return str(r[idx] or "").strip()

        name = cell("name")
        if not name:
            continue
        items.append((name, _norm_gender(cell("gender")), cell("number"), cell("class")))

    if not items:
        raise ValueError("没有解析到学生姓名，请检查表头是否有「姓名」列")
    return items


@app.get("/students", response_class=HTMLResponse)
def students_page(request: Request):
    _require_login(request)
    config = read_config()
    keyword = request.query_params.get("q", "")
    rows = list_students(keyword)
    _sid, sess = get_session(request)
    notice = sess.pop("students_notice", None) if sess else None

    return render(request, "students.html", {
        "system_name": config["system"]["name"],
        "students": rows,
        "keyword": keyword,
        "notice": notice,
        "total": len(rows),
        "boys": sum(1 for r in rows if r["gender"] == "男"),
        "girls": sum(1 for r in rows if r["gender"] == "女"),
    })


@app.post("/students/import")
async def students_import(request: Request, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    _require_login(request)

    form = await request.form()
    upload = form.get("file")
    if upload is None or not getattr(upload, "filename", ""):
        return RedirectResponse(url="/students", status_code=303)

    content = await upload.read()
    try:
        items = parse_students_file(upload.filename, content)
        total, added = add_students(items)
        msg = {"kind": "success", "text": f"导入完成：解析 {total} 名学生，新增 {added} 名，跳过重复 {total - added} 名"}
    except Exception as exc:
        msg = {"kind": "error", "text": f"导入失败：{exc}"}

    sid, sess = get_session(request)
    if sess:
        sess["students_notice"] = msg
    return RedirectResponse(url="/students", status_code=303)


@app.post("/students/create")
async def students_create(request: Request, csrf_token: str = Form(None)):
    """手动添加一名学生（列表内联编辑用）。"""
    check_csrf(request, csrf_token)
    _require_login(request)

    form = await request.form()
    name = (form.get("name") or "").strip()
    if not name:
        return JSONResponse({"ok": False, "error": "姓名不能为空"})

    db = connect_db()
    try:
        with db:
            row = db.execute("SELECT IFNULL(MAX(sort_order), 0) FROM students").fetchone()
            nxt = (row[0] or 0) + 1
            cur = db.execute(
                "INSERT INTO students (name, gender, number, class_name, created_at, sort_order)"
                " VALUES (?, ?, ?, ?, ?, ?)",
                (name, (form.get("gender") or "").strip(), (form.get("number") or "").strip(),
                 (form.get("class_name") or "").strip(),
                 datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"), nxt))
            new_id = cur.lastrowid
    finally:
        db.close()

    return JSONResponse({"ok": True, "id": new_id})


@app.post("/students/update")
async def students_update(request: Request, csrf_token: str = Form(None)):
    """内联编辑保存。"""
    check_csrf(request, csrf_token)
    _require_login(request)

    form = await request.form()
    try:
        sid = int(form.get("id") or 0)
    except ValueError:
        return JSONResponse({"ok": False, "error": "参数错误"})

    name = (form.get("name") or "").strip()
    if not sid or not name:
        return JSONResponse({"ok": False, "error": "姓名不能为空"})

    db = connect_db()
    try:
        with db:
            db.execute(
                "UPDATE students SET name=?, gender=?, number=? WHERE id=?",
                (name, (form.get("gender") or "").strip(), (form.get("number") or "").strip(), sid))
    finally:
        db.close()

    return JSONResponse({"ok": True})


@app.post("/students/bulk_delete")
async def students_bulk_delete(request: Request, csrf_token: str = Form(None)):
    """批量删除：删除数量超过总人数的 1/3 时，必须校验管理员密码。"""
    check_csrf(request, csrf_token)
    _require_login(request)

    form = await request.form()
    ids = [x for x in (form.get("ids") or "").split(",") if x.strip().isdigit()]
    if not ids:
        return JSONResponse({"ok": False, "error": "没有选中学生"})

    db = connect_db()
    try:
        total = db.execute("SELECT COUNT(*) FROM students").fetchone()[0] or 0

        # 超过三分之一 → 校验管理员口令
        need_pwd = total > 0 and len(ids) > total / 3
        if need_pwd:
            password = (form.get("password") or "").strip()
            if not password:
                return JSONResponse({
                    "ok": False, "need_password": True,
                    "error": f"本次要删除 {len(ids)} / {total} 名学生，超过三分之一，需要管理员密码"
                }, status_code=403)

            config = read_config()
            ok, _ = verify_password(password, config["admin"]["password"])
            if not ok:
                integrity.audit("bulk_delete_failed", f"批量删除密码错误，尝试删除 {len(ids)} 名学生",
                                _client_ip(request))
                return JSONResponse({"ok": False, "need_password": True,
                                     "error": "管理员密码不正确"}, status_code=403)

        with db:
            db.executemany("DELETE FROM students WHERE id=?", [(int(i),) for i in ids])
    finally:
        db.close()

    integrity.audit("students_bulk_delete", f"批量删除 {len(ids)} 名学生", _client_ip(request))
    return JSONResponse({"ok": True, "deleted": len(ids)})


@app.post("/students/reorder")
async def students_reorder(request: Request, csrf_token: str = Form(None)):
    """拖动排序后保存顺序。"""
    check_csrf(request, csrf_token)
    _require_login(request)

    form = await request.form()
    ids = [x for x in (form.get("ids") or "").split(",") if x.strip().isdigit()]
    if not ids:
        return JSONResponse({"ok": False, "error": "参数错误"})

    db = connect_db()
    try:
        with db:
            for idx, sid in enumerate(ids, start=1):
                db.execute("UPDATE students SET sort_order=? WHERE id=?", (idx, int(sid)))
    finally:
        db.close()

    return JSONResponse({"ok": True})


@app.post("/students/delete")
def students_delete(request: Request, student_id: int = Form(...), csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    _require_login(request)
    db = connect_db()
    try:
        with db:
            db.execute("DELETE FROM students WHERE id=?", (student_id,))
    finally:
        db.close()
    return RedirectResponse(url="/students", status_code=303)


@app.post("/students/clear")
def students_clear(request: Request, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    _require_login(request)
    db = connect_db()
    try:
        with db:
            db.execute("DELETE FROM students")
    finally:
        db.close()
    sid, sess = get_session(request)
    if sess:
        sess["students_notice"] = {"kind": "success", "text": "学生名单已清空"}
    return RedirectResponse(url="/students", status_code=303)


# ==================== 小组 ====================

def list_groups():
    init_db()
    db = connect_db()
    try:
        gs = [dict(r) for r in db.execute(
            "SELECT id, name, sort_order FROM student_groups ORDER BY sort_order, id").fetchall()]
        rows = db.execute(
            "SELECT id, name, gender, number, group_id FROM students ORDER BY sort_order, id").fetchall()
    finally:
        db.close()

    by_group = {}
    ungrouped = []
    for r in rows:
        item = dict(r)
        gid = item.get("group_id") or 0
        if gid and any(g["id"] == gid for g in gs):
            by_group.setdefault(gid, []).append(item)
        else:
            ungrouped.append(item)

    for g in gs:
        g["members"] = by_group.get(g["id"], [])

    return gs, ungrouped


def create_groups(count):
    """把学生平均分成 count 组（性别尽量均衡），返回组数。"""
    init_db()
    db = connect_db()
    try:
        with db:
            db.execute("DELETE FROM student_groups")
            db.execute("UPDATE students SET group_id=0")
            for i in range(1, count + 1):
                db.execute(
                    "INSERT INTO student_groups (name, sort_order, created_at) VALUES (?, ?, ?)",
                    (f"{i} 组", i, datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")))

            groups = [r[0] for r in db.execute(
                "SELECT id FROM student_groups ORDER BY sort_order").fetchall()]

            students = [dict(r) for r in db.execute(
                "SELECT id, gender FROM students ORDER BY sort_order, id").fetchall()]

        # 男女生分别轮转分配，保证每组性别尽量均衡
        boys = [x["id"] for x in students if x.get("gender") == "男"]
        girls = [x["id"] for x in students if x.get("gender") == "女"]
        others = [x["id"] for x in students if x.get("gender") not in ("男", "女")]

        assignment = {}
        for pool in (boys, girls, others):
            for idx, sid in enumerate(pool):
                assignment[sid] = groups[idx % len(groups)]

        with db:
            for sid, gid in assignment.items():
                db.execute("UPDATE students SET group_id=? WHERE id=?", (gid, sid))
    finally:
        db.close()

    return len(groups)


def random_partition(count):
    """随机分组（不落库），返回 [[学生...], ...]。"""
    db = connect_db()
    try:
        students = [dict(r) for r in db.execute(
            "SELECT id, name, gender, number FROM students ORDER BY sort_order, id").fetchall()]
    finally:
        db.close()

    random.shuffle(students)
    buckets = [[] for _ in range(max(1, count))]
    for i, st in enumerate(students):
        buckets[i % count].append(st)
    return buckets


@app.get("/groups", response_class=HTMLResponse)
def groups_page(request: Request):
    _require_login(request)
    config = read_config()
    gs, ungrouped = list_groups()
    _sid, sess = get_session(request)
    notice = sess.pop("groups_notice", None) if sess else None

    return render(request, "groups.html", {
        "system_name": config["system"]["name"],
        "groups": gs,
        "ungrouped": ungrouped,
        "total": len(ungrouped) + sum(len(g["members"]) for g in gs),
        "notice": notice,
    })


@app.post("/groups/auto")
def groups_auto(request: Request, count: int = Form(2), csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    _require_login(request)
    n = max(2, min(12, count))
    try:
        made = create_groups(n)
        msg = {"kind": "success", "text": f"已把学生平均分成 {made} 组"}
    except Exception as exc:
        msg = {"kind": "error", "text": f"分组失败：{exc}"}

    _sid, sess = get_session(request)
    if sess:
        sess["groups_notice"] = msg
    return RedirectResponse(url="/groups", status_code=303)


@app.post("/groups/assign")
async def groups_assign(request: Request, csrf_token: str = Form(None)):
    """把某个学生移到某个小组（group_id=0 表示移出小组）。"""
    check_csrf(request, csrf_token)
    _require_login(request)

    form = await request.form()
    try:
        sid = int(form.get("student_id") or 0)
        gid = int(form.get("group_id") or 0)
    except ValueError:
        return JSONResponse({"ok": False, "error": "参数错误"})

    db = connect_db()
    try:
        with db:
            db.execute("UPDATE students SET group_id=? WHERE id=?", (gid, sid))
    finally:
        db.close()

    return JSONResponse({"ok": True})


@app.post("/groups/save")
async def groups_save(request: Request, csrf_token: str = Form(None)):
    """整页保存：按前端提交的结构重建小组与成员归属。

    payload = {
      "groups": [{"id": 0 或已有id, "name": "1 组", "members": [学生id...]}, ...],
      "ungrouped": [学生id...]
    }
    """
    check_csrf(request, csrf_token)
    _require_login(request)

    form = await request.form()
    raw = form.get("payload") or ""
    try:
        data = json.loads(raw)
    except Exception:
        return JSONResponse({"ok": False, "error": "提交的数据格式不对"})

    groups = data.get("groups") or []
    ungrouped = data.get("ungrouped") or []

    if not isinstance(groups, list):
        return JSONResponse({"ok": False, "error": "提交的数据格式不对"})

    db = connect_db()
    try:
        with db:
            keep_ids = []

            for idx, g in enumerate(groups, start=1):
                name = (g.get("name") or "").strip() or f"{idx} 组"
                gid = int(g.get("id") or 0)
                if gid:
                    db.execute("UPDATE student_groups SET name=?, sort_order=? WHERE id=?",
                               (name, idx, gid))
                    keep_ids.append(gid)
                else:
                    cur = db.execute(
                        "INSERT INTO student_groups (name, sort_order, created_at) VALUES (?, ?, ?)",
                        (name, idx, datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")))
                    keep_ids.append(cur.lastrowid)

            # 删掉本次没出现的组
            if keep_ids:
                placeholders = ",".join("?" * len(keep_ids))
                db.execute(f"DELETE FROM student_groups WHERE id NOT IN ({placeholders})", keep_ids)
            else:
                db.execute("DELETE FROM student_groups")

            # 先把所有人置为未分组，再按提交的归属重新分配（提交里的 id 是新组 id 时用索引对齐）
            db.execute("UPDATE students SET group_id=0")

            for idx, g in enumerate(groups):
                gid = keep_ids[idx]
                for sid in (g.get("members") or []):
                    try:
                        db.execute("UPDATE students SET group_id=? WHERE id=?", (gid, int(sid)))
                    except (TypeError, ValueError):
                        continue

            for sid in ungrouped:
                try:
                    db.execute("UPDATE students SET group_id=0 WHERE id=?", (int(sid),))
                except (TypeError, ValueError):
                    continue
    finally:
        db.close()

    integrity.audit("groups_save", f"保存分组：{len(groups)} 个小组", _client_ip(request))
    return JSONResponse({"ok": True})


@app.post("/groups/rename")
async def groups_rename(request: Request, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    _require_login(request)

    form = await request.form()
    try:
        gid = int(form.get("group_id") or 0)
    except ValueError:
        return JSONResponse({"ok": False, "error": "参数错误"})

    name = (form.get("name") or "").strip()
    if not gid or not name:
        return JSONResponse({"ok": False, "error": "组名不能为空"})

    db = connect_db()
    try:
        with db:
            db.execute("UPDATE student_groups SET name=? WHERE id=?", (name, gid))
    finally:
        db.close()

    return JSONResponse({"ok": True})


@app.post("/groups/clear")
def groups_clear(request: Request, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    _require_login(request)

    db = connect_db()
    try:
        with db:
            db.execute("DELETE FROM student_groups")
            db.execute("UPDATE students SET group_id=0")
    finally:
        db.close()

    _sid, sess = get_session(request)
    if sess:
        sess["groups_notice"] = {"kind": "success", "text": "已清空分组"}
    return RedirectResponse(url="/groups", status_code=303)


@app.get("/groups/list.json")
def groups_list_json(request: Request):
    _require_login(request)
    gs, ungrouped = list_groups()
    return JSONResponse({
        "ok": True,
        "groups": [
            {
                "id": g["id"],
                "name": g["name"],
                "members": [{"name": m["name"], "gender": m.get("gender", "")} for m in g["members"]],
            }
            for g in gs
        ],
        "ungrouped": [{"name": m["name"], "gender": m.get("gender", "")} for m in ungrouped],
    })


@app.get("/groups/live", response_class=HTMLResponse)
def groups_live_page(request: Request):
    _require_login(request)
    config = read_config()
    db = connect_db()
    try:
        total = db.execute("SELECT COUNT(*) FROM students").fetchone()[0] or 0
    finally:
        db.close()

    devs = [d for d in devices.list_devices() if d.get("enabled", True)]

    return render(request, "groups_live.html", {
        "system_name": config["system"]["name"],
        "total": total,
        "devices": devs,
    })


@app.post("/groups/live/shuffle")
async def groups_live_shuffle(request: Request, csrf_token: str = Form(None)):
    """现场随机分组（结果不落库，只返回给页面展示）。"""
    check_csrf(request, csrf_token)
    _require_login(request)

    form = await request.form()
    try:
        count = int(form.get("count") or 2)
    except ValueError:
        count = 2
    count = max(2, min(12, count))

    buckets = random_partition(count)
    return JSONResponse({
        "ok": True,
        "groups": [{"index": i + 1, "members": b} for i, b in enumerate(buckets)]
    })


@app.get("/students/list.json")
def students_list_json(request: Request, q: str = ""):
    """给班级叫号的下拉框用。"""
    _require_login(request)
    return JSONResponse({"ok": True, "students": list_students(q)[:200]})


@app.get("/audit", response_class=HTMLResponse)
def audit_page(request: Request):
    """操作日志：谁在什么时候做了敏感操作。"""
    _require_login(request)
    config = read_config()

    warn_events = {"integrity", "bulk_delete_failed", "students_bulk_delete"}
    bad_events = {"login_failed", "verify_failed"}

    rows = []
    for ln in reversed(integrity.audit_tail(300)):
        parts = ln.split("\t")
        while len(parts) < 4:
            parts.append("")

        event = parts[1]
        tone = ""
        if event in bad_events:
            tone = "is-bad"
        elif event in warn_events:
            tone = "is-warn"

        rows.append({
            "time": parts[0],
            "event": event,
            "label": AUDIT_LABELS.get(event, event),
            "ip": parts[2] or "-",
            "detail": parts[3],
            "tone": tone,
        })

    return render(request, "audit.html", {
        "system_name": config["system"]["name"],
        "rows": rows,
    })


@app.post("/integrity_reaccept")
def integrity_reaccept(request: Request, csrf_token: str = Form(None)):
    """用户确认「这个改动是我做的」后，重新签名当前状态。"""
    check_csrf(request, csrf_token)
    _require_login(request)
    integrity.clear_alert()
    integrity.audit("integrity_accept", "用户确认配置改动并重新签名", _client_ip(request))
    return RedirectResponse(url="/setting", status_code=303)


@app.post("/setting")
async def submit_setting(request: Request, current_password: str = Form(...), password: str = Form(...),
                         csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login", status_code=303)
    config = read_config()
    ok, _ = verify_password(current_password or "", config["admin"]["password"])
    if not ok:
        sess["setting"] = "wrong_password"
        return RedirectResponse(url="/setting", status_code=303)
    if len(password or "") < 4:
        sess["setting"] = "weak"
        return RedirectResponse(url="/setting", status_code=303)
    config["admin"]["password"] = hash_password(password)
    write_config(config)
    sess["setting"] = "success"
    return RedirectResponse(url="/setting", status_code=303)

# 恢复出厂设置
def do_reset(keep_settings=False, keep_admin=False):
    """删除数据库与导出文件；可选择保留现有设置和管理员账号。"""
    # 删除数据库
    db_path = get_db_path()
    if os.path.exists(db_path):
        os.remove(db_path)
    # 删除output目录中的文件
    config = read_config()
    output_dir = config["paths"]["output"]
    if not os.path.isabs(output_dir):
        output_dir = os.path.join(BASE_DIR, output_dir)
    if os.path.isdir(output_dir):
        for f in os.listdir(output_dir):
            fp = os.path.join(output_dir, f)
            if os.path.isfile(fp):
                try:
                    os.remove(fp)
                except PermissionError:
                    pass  # 文件被占用，跳过
    # 重置配置：默认回到出厂；勾选保留时沿用原来那部分
    if keep_settings or keep_admin:
        fresh = get_default_config()
        fresh["system"]["init_required"] = True

        if keep_settings:
            for section in ("system", "print", "sound", "boot", "backup", "desktop", "paths"):
                if section in config:
                    fresh[section] = config[section]
            fresh["system"]["init_required"] = True

        if keep_admin and config.get("admin"):
            fresh["admin"] = config["admin"]

        # 保留了设置且启动动画是「不开启」：初始化完成后强制播一次
        if keep_settings and (fresh.get("boot", {}) or {}).get("mode") == "off":
            fresh.setdefault("boot", {})
            fresh["boot"]["force_once"] = True

        write_config(fresh)
    else:
        write_config(get_default_config())

    # 清空会话与登录节流记录
    sessions.clear()
    _login_failures.clear()
    # 保留管理员时，初始化第一步可以跳过
    if keep_admin:
        global _admin_kept
        _admin_kept = True

# ---------------- 备份 ----------------

@app.post("/backup/now")
async def backup_now(request: Request, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login", status_code=303)
    try:
        name = create_backup()
        sess["notice"] = {"kind": "success", "text": f"已创建备份：{name}"}
    except Exception as exc:
        sess["notice"] = {"kind": "error", "text": f"备份失败：{exc}"}
    return RedirectResponse(url="/setting", status_code=303)


@app.post("/backup/open")
async def backup_open(request: Request, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login", status_code=303)

    directory = get_backup_dir()
    opened = False
    if os.name == "nt":
        try:
            os.startfile(directory)      # 本机运行时直接在资源管理器打开
            opened = True
        except Exception:
            opened = False

    sess["notice"] = {
        "kind": "success" if opened else "info",
        "text": f"备份目录：{directory}" if opened else f"请在文件管理器中打开：{directory}",
    }
    return RedirectResponse(url="/setting", status_code=303)


@app.post("/backup/settings")
async def backup_settings(request: Request, backup_time: str = Form("02:00"),
                          keep: int = Form(10), enabled: str = Form(None),
                          ajax: str = Form(None), csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        if ajax:
            return ajax_login_required()
        return RedirectResponse(url="/login", status_code=303)

    if not re.fullmatch(r"\d{1,2}:\d{2}", (backup_time or "").strip()):
        if ajax:
            return {"ok": False, "text": "备份时间格式应为 HH:MM，例如 02:30"}
        sess["notice"] = {"kind": "error", "text": "备份时间格式应为 HH:MM，例如 02:30"}
        return RedirectResponse(url="/setting", status_code=303)

    hour, minute = parse_backup_time(backup_time)
    if not (0 <= hour <= 23 and 0 <= minute <= 59):
        sess["notice"] = {"kind": "error", "text": "备份时间需在 00:00 - 23:59 之间"}
        return RedirectResponse(url="/setting", status_code=303)
    if not 1 <= keep <= MAX_BACKUP_KEEP:
        sess["notice"] = {"kind": "error", "text": f"保留份数需在 1 - {MAX_BACKUP_KEEP} 之间"}
        return RedirectResponse(url="/setting", status_code=303)

    config = read_config()
    config.setdefault("backup", {})
    config["backup"]["time"] = f"{hour:02d}:{minute:02d}"
    config["backup"]["keep"] = keep
    config["backup"]["enabled"] = bool(enabled)
    write_config(config)

    if enabled:
        text = f"已开启每日 {hour:02d}:{minute:02d} 自动备份，最多保留 {keep} 份"
    else:
        text = "已关闭定时备份"

    if ajax:
        return {"ok": True, "text": text, "next_backup": next_backup_time()}

    sess["notice"] = {"kind": "success", "text": text}
    return RedirectResponse(url="/setting", status_code=303)


@app.post("/print/settings")
async def print_settings(request: Request, printer: str = Form(""), ajax: str = Form(None),
                         csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        if ajax:
            return ajax_login_required()
        return RedirectResponse(url="/login", status_code=303)

    printer = (printer or "").strip()
    if printer:
        names, _ = list_printers()
        if printer not in names:
            if ajax:
                return {"ok": False, "text": f"找不到该打印机：{printer}"}
            sess["notice"] = {"kind": "error", "text": f"找不到该打印机：{printer}"}
            return RedirectResponse(url="/setting", status_code=303)

    config = read_config()
    config.setdefault("print", {})
    config["print"]["printer"] = printer
    write_config(config)

    text = f"打印将使用：{printer}" if printer else "打印将使用系统默认打印机"
    if ajax:
        return {"ok": True, "text": text}

    sess["notice"] = {"kind": "success", "text": text}
    return RedirectResponse(url="/setting", status_code=303)


@app.post("/desktop/autostart")
async def desktop_autostart(request: Request, autostart: str = Form(None),
                            ajax: str = Form(None), csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        if ajax:
            return ajax_login_required()
        return RedirectResponse(url="/login", status_code=303)

    enabled = bool(autostart)
    config = read_config()
    config.setdefault("desktop", {})
    config["desktop"]["autostart"] = enabled
    write_config(config)

    applied = apply_autostart(enabled)
    if enabled:
        text = "已开启开机自启" if applied else "已开启开机自启，下次启动桌面版时生效"
    else:
        text = "已关闭开机自启" if applied else "已关闭开机自启，下次启动桌面版时生效"

    if ajax:
        return {"ok": True, "text": text}

    sess["notice"] = {"kind": "success", "text": text}
    return RedirectResponse(url="/setting", status_code=303)


# ---------------- 清理 ----------------

@app.post("/cleanup/cache")
async def cleanup_cache(request: Request, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login", status_code=303)

    removed, freed = clean_output()
    sess["notice"] = {"kind": "success", "text": f"已清理 {removed} 个导出文件，释放 {human_size(freed)}"}
    return RedirectResponse(url="/setting", status_code=303)


@app.post("/cleanup/junk")
async def cleanup_junk(request: Request, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login", status_code=303)

    removed, freed = clean_junk()
    sess["notice"] = {"kind": "success", "text": f"已清理 {removed} 项垃圾文件，释放 {human_size(freed)}"}
    return RedirectResponse(url="/setting", status_code=303)


# 提交恢复出厂设置
@app.post("/reset")
async def reset_system(request: Request, reset_password: str = Form(...),
                        keep_settings: str = Form(None), keep_admin: str = Form(None),
                        csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login", status_code=303)
    config = read_config()
    ok, _ = verify_password(reset_password or "", config["admin"]["password"])
    if not ok:
        sess["setting"] = "reset_wrong_password"
        return RedirectResponse(url="/setting", status_code=303)
    do_reset(keep_settings=bool(keep_settings), keep_admin=bool(keep_admin))
    return RedirectResponse(url="/", status_code=303)


# ---------------- 待处理批次 ----------------

@app.post("/history/{batch_id}/printed")
def history_mark_printed(request: Request, batch_id: str, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        return Response(status_code=403)

    if history_find(batch_id):
        history_patch(batch_id, printed=datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"))
    return Response(status_code=204)


@app.post("/history/{batch_id}/saved")
def history_mark_saved(request: Request, batch_id: str, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        return Response(status_code=403)

    if history_find(batch_id):
        history_patch(batch_id, saved=datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"))
    return Response(status_code=204)


@app.post("/history/{batch_id}/delete")
async def history_delete(request: Request, batch_id: str, csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        return RedirectResponse(url="/login", status_code=303)

    history_remove(batch_id)
    sess["gen_notice"] = {"kind": "success", "text": "已删除该批次及其导出文件"}
    target = "/generate_result" if sess.get("gen") else "/generate"
    return RedirectResponse(url=target, status_code=303)


# 直接打印：用 Office/WPS 100% 打印 docx，同时把 PDF 归档到 output
@app.post("/print-now")
def print_now(request: Request, filename: str = Form(None), batch_id: str = Form(None),
              csrf_token: str = Form(None)):
    check_csrf(request, csrf_token)
    sid, sess = get_session(request)
    if not sess:
        raise HTTPException(status_code=403, detail="请先登录")

    batch = history_find(batch_id) if batch_id else (sess.get("gen") or {})
    if not batch:
        batch = sess.get("gen") or {}

    name = os.path.basename((batch.get("filename") or "").replace("\\", "/"))
    if not name:
        return {"ok": False, "message": "没有可打印的批次"}

    printer = (read_config().get("print", {}) or {}).get("printer", "") or ""

    if not office_available():
        return {"ok": False, "message": OFFICE_MISSING_MESSAGE}

    pdf_path = ensure_batch_pdf(batch)
    if not pdf_path:
        return {"ok": False, "message": "打印页面生成失败，请确认 WPS/Office 能正常打开导出的 Word 文档"}

    try:
        used = print_pdf_direct(pdf_path, printer)
    except Exception as exc:
        return {"ok": False, "message": f"打印失败：{exc}"}

    history_mark(name, "printed")
    return {"ok": True, "message": f"已发送到 {used}"}

def get_output_dir():
    output_dir = read_config()["paths"]["output"]
    if not os.path.isabs(output_dir):
        output_dir = os.path.join(BASE_DIR, output_dir)
    return os.path.realpath(output_dir)


@app.get("/download/{filename}")
async def download_file(request: Request, filename: str):
    if not get_current_user(request):
        return RedirectResponse(url="/login")

    # 只允许下载导出目录内的 .docx，挡掉 ..\ / ../ 之类穿越（Windows 反斜杠同样算分隔符）
    name = os.path.basename(filename.replace("\\", "/"))
    if not name or name != filename or not name.lower().endswith(".docx"):
        raise HTTPException(status_code=404, detail="文件不存在")

    output_dir = get_output_dir()
    filepath = os.path.realpath(os.path.join(output_dir, name))
    try:
        inside = os.path.commonpath([filepath, output_dir]) == output_dir
    except ValueError:          # 跨盘符
        inside = False
    if not inside or not os.path.isfile(filepath):
        raise HTTPException(status_code=404, detail="文件不存在")

    # 是否真的保存成功由客户端确认（用户可能在“另存为”里取消），
    # 桌面端写盘完成后会调用 /history/{id}/saved

    return FileResponse(
        filepath,
        filename=name,
        media_type="application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    )


# 初始化数据库
init_db()

# 启动定时备份（按 data/config.json 里的 backup.interval_hours）
start_backup_scheduler()

# 启动更新检查（每小时一次，结果写进 data/config.json 缓存）
start_update_scheduler()
