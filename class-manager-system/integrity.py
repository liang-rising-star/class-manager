"""配置文件完整性校验 + 审计日志。

目标：当 data 下的配置/设备/批次文件被「应用之外」的改动动过时，能被发现。
做法：用 Windows DPAPI（当前用户作用域）封一个随机密钥，再对文件内容做 HMAC，
      结果写进 data/.integrity.json。应用自己改文件后会重新签名；
      别人改了文件却签不出同样的 HMAC，下次启动就会报出来。
"""

import base64
import ctypes
import hashlib
import hmac
import json
import os
import sys
import time
from ctypes import wintypes

DATA_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data")

KEY_FILE = ".integrity.key"
MANIFEST_FILE = ".integrity.json"
AUDIT_FILE = "audit.log"

WATCHED = ("config.json", "devices.json", "history.json")

__all__ = [
    "verify", "refresh", "audit", "audit_tail", "last_alert", "clear_alert",
    "DATA_DIR", "AUDIT_FILE",
]

# ---------------------------------------------------------------- DPAPI

class _DataBlob(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]


def _blob(data: bytes):
    buf = ctypes.create_string_buffer(data, len(data))
    return _DataBlob(len(data), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char))), buf


def _dpapi(protect: bool, data: bytes) -> bytes:
    if os.name != "nt":
        return data  # 非 Windows 直接明文（本系统只跑 Windows）

    fn = ctypes.windll.crypt32.CryptProtectData if protect else ctypes.windll.crypt32.CryptUnprotectData
    in_blob, _keep = _blob(data)
    out_blob = _DataBlob()

    if protect:
        ok = fn(ctypes.byref(in_blob), None, None, None, None, 0x1, ctypes.byref(out_blob))
    else:
        ok = fn(ctypes.byref(in_blob), None, None, None, None, 0x1, ctypes.byref(out_blob))

    if not ok:
        raise OSError("DPAPI 调用失败")

    try:
        return ctypes.string_at(out_blob.pbData, out_blob.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(out_blob.pbData)


def _load_key() -> bytes:
    path = os.path.join(DATA_DIR, KEY_FILE)
    if os.path.exists(path):
        with open(path, "rb") as f:
            return _dpapi(False, f.read())

    key = os.urandom(32)
    os.makedirs(DATA_DIR, exist_ok=True)
    with open(path, "wb") as f:
        f.write(_dpapi(True, key))

    try:
        os.chmod(path, 0o600)
    except Exception:
        pass
    return key


# ---------------------------------------------------------------- 指纹

def _digest(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def _manifest_path() -> str:
    return os.path.join(DATA_DIR, MANIFEST_FILE)


def _sign(files: dict) -> str:
    key = _load_key()
    body = json.dumps(files, sort_keys=True, separators=(",", ":")).encode()
    return hmac.new(key, body, hashlib.sha256).hexdigest()


def refresh():
    """应用自己正常改完文件后调用，重新签名。"""
    files = {}
    for name in WATCHED:
        path = os.path.join(DATA_DIR, name)
        if os.path.exists(path):
            files[name] = _digest(path)

    payload = {"files": files, "signed_at": time.strftime("%Y-%m-%d %H:%M:%S"),
               "hmac": _sign(files)}

    tmp = _manifest_path() + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
    os.replace(tmp, _manifest_path())


_alert = None


def verify():
    """返回被外部修改的文件列表；同时把告警记进审计日志。"""
    global _alert

    path = _manifest_path()
    if not os.path.exists(path):
        refresh()
        return []

    try:
        with open(path, "r", encoding="utf-8") as f:
            payload = json.load(f)
        files = payload.get("files") or {}
        signed = payload.get("hmac") or ""
    except Exception:
        return []

    changed = []
    for name in WATCHED:
        file_path = os.path.join(DATA_DIR, name)
        exists = os.path.exists(file_path)
        if name in files:
            if not exists or _digest(file_path) != files[name]:
                changed.append(name)
        elif exists:
            changed.append(name)   # 新冒出来的文件

    manifest_tampered = not hmac.compare_digest(signed, _sign(files))
    if manifest_tampered and not changed:
        changed.append(MANIFEST_FILE)

    _alert = changed or None
    if changed:
        audit("integrity", "检测到配置文件被外部修改：" + "、".join(changed))
    return changed


def last_alert():
    return _alert


def clear_alert():
    """用户确认后重新签名，告警消失。"""
    global _alert
    _alert = None
    refresh()


# ---------------------------------------------------------------- 审计日志

def audit(event: str, detail: str = "", ip: str = ""):
    try:
        os.makedirs(DATA_DIR, exist_ok=True)
        line = "{}\t{}\t{}\t{}".format(
            time.strftime("%Y-%m-%d %H:%M:%S"), event, ip or "-", (detail or "").replace("\n", " ")[:500])
        with open(os.path.join(DATA_DIR, AUDIT_FILE), "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass


def audit_tail(limit: int = 50):
    path = os.path.join(DATA_DIR, AUDIT_FILE)
    if not os.path.exists(path):
        return []
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            lines = f.readlines()[-limit:]
    except Exception:
        return []
    return [ln.rstrip("\n") for ln in lines if ln.strip()]
