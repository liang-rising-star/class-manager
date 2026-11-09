# 班级管理系统 —— 班级设备（一体机端）对接
# Copyright (C) 2026 liang-rising-star
#
# 负责：
#   1. 设备清单的增删改查（data/devices.json）
#   2. UDP 视频订阅与帧重组（视频走 UDP，其余接口走 TCP/HTTP）
#   3. 调用一体机端的 HTTP 接口（状态 / 摄像头 / 麦克风 / 播报 / 远程指令）

import json
import os
import socket
import struct
import threading
import time
import urllib.error
import urllib.request

# ---------- 常量 ----------

DEVICES_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "devices.json")
DEFAULT_AGENT_PORT = 3457
VIDEO_MAGIC = b"KVD1"
VIDEO_HEADER = struct.Struct("<4sIHHI")   # magic, frameId, fragIndex, fragCount, frameLen
MAX_PAYLOAD = 1300
SUBSCRIBE_INTERVAL = 2.0                  # 每 2 秒续订一次
ONLINE_WINDOW = 6.0                       # 6 秒内收到帧视为在线
STALE_WINDOW = 30.0                       # 30 秒前的帧不再复用

_lock = threading.RLock()
_devices = []
_frames = {}          # device_id -> {"jpeg": bytes, "ts": float, "frame_id": int}
_assembling = {}      # device_id -> {frame_id: {"parts": {}, "count": n, "len": n}}
_stats = {}           # device_id -> {"frames": int, "last_frame": float, "fps": float, "window_start": float, "window_count": int}
_started = False
_sock = None
_running = False


# ==================== 设备清单 ====================

def _ensure_dir():
    os.makedirs(os.path.dirname(DEVICES_PATH), exist_ok=True)


def load_devices():
    """从磁盘读取设备清单（只在首次需要时读一次）。"""
    global _devices
    with _lock:
        if _devices:
            return list(_devices)
        _ensure_dir()
        if os.path.exists(DEVICES_PATH):
            try:
                with open(DEVICES_PATH, "r", encoding="utf-8") as f:
                    data = json.load(f)
                if isinstance(data, list):
                    _devices = [_normalize(d) for d in data if isinstance(d, dict)]
            except Exception:
                _devices = []
        return list(_devices)


def save_devices():
    with _lock:
        _ensure_dir()
        tmp = DEVICES_PATH + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(_devices, f, ensure_ascii=False, indent=2)
        os.replace(tmp, DEVICES_PATH)

        # 设备清单是程序自己改的，改完立刻重新签名，
        # 否则下次启动会把这次正常修改误报成「被外部修改」。
        try:
            import integrity
            integrity.refresh()
        except Exception:
            pass


def _normalize(d):
    try:
        port = int(d.get("port") or DEFAULT_AGENT_PORT)
    except (TypeError, ValueError):
        port = DEFAULT_AGENT_PORT
    try:
        video_port = int(d.get("video_port") or 0)
    except (TypeError, ValueError):
        video_port = 0
    return {
        "id": str(d.get("id") or ""),
        "name": str(d.get("name") or "").strip(),
        "room": str(d.get("room") or "").strip(),
        "host": str(d.get("host") or "").strip(),
        "port": port if 1 <= port <= 65535 else DEFAULT_AGENT_PORT,
        "video_port": video_port if 0 <= video_port <= 65535 else 0,
        "key": str(d.get("key") or "").strip(),
        "enabled": bool(d.get("enabled", True)),
    }


def list_devices():
    return load_devices()


def get_device(device_id):
    for d in load_devices():
        if d["id"] == device_id:
            return d
    return None


def add_device(name, room, host, port, key, video_port=0):
    import uuid
    with _lock:
        load_devices()
        dev = _normalize({
            "id": "dev_" + uuid.uuid4().hex[:10],
            "name": name, "room": room, "host": host,
            "port": port, "video_port": video_port, "key": key, "enabled": True,
        })
        _devices.append(dev)
        save_devices()
        return dev


def update_device(device_id, **kwargs):
    with _lock:
        load_devices()
        for i, d in enumerate(_devices):
            if d["id"] == device_id:
                merged = dict(d)
                merged.update({k: v for k, v in kwargs.items() if v is not None})
                _devices[i] = _normalize(merged)
                save_devices()
                return _devices[i]
    return None


def delete_device(device_id):
    global _devices
    with _lock:
        load_devices()
        before = len(_devices)
        _devices = [d for d in _devices if d["id"] != device_id]
        if len(_devices) != before:
            save_devices()
            _frames.pop(device_id, None)
            _stats.pop(device_id, None)
            return True
    return False


def video_port_of(dev):
    return dev.get("video_port") or dev.get("port") or DEFAULT_AGENT_PORT


# ==================== UDP 视频接收 ====================

def start():
    """启动后台 UDP 接收 + 续订线程（重复调用安全）。"""
    global _started, _sock
    with _lock:
        if _started:
            return
        _started = True
        load_devices()

    _sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        _sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 << 20)
    except OSError:
        pass
    _sock.bind(("", 0))
    _sock.settimeout(1.0)

    # Windows 上：往“已关闭的端口”发 UDP 后，下一次 recvfrom 会抛 10054（ICMP 不可达），
    # 关掉这个行为；否则一体机一重启，收帧线程就会被打断。
    try:
        _sock.ioctl(socket.SIO_UDP_CONNRESET, False)
    except (AttributeError, OSError):
        pass

    global _running
    _running = True

    threading.Thread(target=_recv_supervisor, name="device-video-recv", daemon=True).start()
    threading.Thread(target=_subscribe_loop, name="device-video-sub", daemon=True).start()


def _device_by_source(ip, sport):
    """按来源 IP:端口匹配设备（视频端口优先）。"""
    with _lock:
        candidates = [d for d in _devices if d["host"] == ip]
    if not candidates:
        return None
    for d in candidates:
        if video_port_of(d) == sport:
            return d
    return candidates[0] if len(candidates) == 1 else candidates[0]


def _recv_supervisor():
    """收帧线程的守护：即使底层循环意外退出，也会自动重开。"""
    while _running:
        try:
            _recv_loop()
        except Exception as e:
            print("视频接收线程异常，2 秒后重启:", e)
        time.sleep(2)


def _recv_loop():
    while _running:
        try:
            data, addr = _sock.recvfrom(65535)
        except socket.timeout:
            continue
        except OSError:
            # 对端重启 / 端口暂时关闭时 Windows 会抛 10054，继续等，不退出
            continue
        except Exception:
            continue
        if len(data) < VIDEO_HEADER.size or not data.startswith(VIDEO_MAGIC):
            continue

        try:
            _, frame_id, frag_index, frag_count, frame_len = VIDEO_HEADER.unpack_from(data, 0)
        except struct.error:
            continue

        dev = _device_by_source(addr[0], addr[1])
        if not dev:
            continue
        did = dev["id"]
        payload = data[VIDEO_HEADER.size:]

        with _lock:
            bucket = _assembling.setdefault(did, {})
            entry = bucket.get(frame_id)
            if entry is None:
                entry = {"parts": {}, "count": frag_count, "len": frame_len}
                bucket[frame_id] = entry
                # 只保留最近 8 个待拼帧，避免内存膨胀
                if len(bucket) > 8:
                    for old in sorted(bucket)[:-8]:
                        bucket.pop(old, None)
            entry["parts"][frag_index] = payload

            if len(entry["parts"]) == entry["count"]:
                try:
                    jpeg = b"".join(entry["parts"][i] for i in range(entry["count"]))
                except KeyError:
                    jpeg = None
                bucket.pop(frame_id, None)
                if jpeg:
                    _frames[did] = {"jpeg": jpeg, "ts": time.time(), "frame_id": frame_id}
                    st = _stats.setdefault(did, {"frames": 0, "last_frame": 0.0, "fps": 0.0,
                                                 "window_start": time.time(), "window_count": 0})
                    st["frames"] += 1
                    st["last_frame"] = time.time()
                    st["window_count"] += 1
                    span = time.time() - st["window_start"]
                    if span >= 1.0:
                        st["fps"] = round(st["window_count"] / span, 1)
                        st["window_start"] = time.time()
                        st["window_count"] = 0


def _subscribe_loop():
    while _running:
        try:
            now = time.time()
            with _lock:
                targets = [(d["host"], video_port_of(d), d.get("key", "")) for d in _devices
                           if d.get("enabled", True) and d.get("host")]
            for host, port, key in targets:
                try:
                    _sock.sendto(f"KVSUB {key}".encode("ascii", "ignore"), (host, port))
                except Exception:
                    pass
            del now
        except Exception:
            pass
        time.sleep(SUBSCRIBE_INTERVAL)


# ==================== 帧读取 ====================

def latest_frame(device_id):
    with _lock:
        f = _frames.get(device_id)
        if not f:
            return None
        if time.time() - f["ts"] > STALE_WINDOW:
            return None
        return f["jpeg"]


def frame_timestamp(device_id):
    with _lock:
        f = _frames.get(device_id)
        return f["ts"] if f else 0


def video_stats(device_id):
    with _lock:
        st = _stats.get(device_id)
        f = _frames.get(device_id)
        if not st or not f:
            return {"online": False, "fps": 0, "frames": 0, "age": None}
        age = round(time.time() - st["last_frame"], 1)
        return {
            "online": age <= ONLINE_WINDOW,
            "fps": st["fps"],
            "frames": st["frames"],
            "age": age,
        }


# ==================== HTTP 客户端 ====================

class AgentError(Exception):
    pass


def agent_request(dev, method, path, payload=None, timeout=4.0):
    """向一体机端发请求，自动带上 X-Access-Key。"""
    host = dev.get("host") or ""
    if not host:
        raise AgentError("设备未填写 IP 地址")
    port = dev.get("port") or DEFAULT_AGENT_PORT
    url = f"http://{host}:{port}{path}"

    data = None
    headers = {"X-Access-Key": dev.get("key", "")}
    if payload is not None:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"

    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            body = resp.read()
            ctype = resp.headers.get("Content-Type", "")
            if "application/json" in ctype:
                return json.loads(body.decode("utf-8"))
            return body
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        try:
            return json.loads(body)
        except Exception:
            raise AgentError(f"HTTP {e.code}: {body[:120]}")
    except urllib.error.URLError as e:
        raise AgentError(f"连接失败：{getattr(e, 'reason', e)}")
    except socket.timeout:
        raise AgentError("连接超时")
    except Exception as e:
        raise AgentError(str(e))


def device_state(dev):
    """聚合：一体机端上报状态 + 本地视频统计。"""
    out = {
        "id": dev["id"], "name": dev["name"], "room": dev["room"],
        "host": dev["host"], "port": dev["port"], "video_port": video_port_of(dev),
        "reachable": False, "error": "", "agent": None, "video": video_stats(dev["id"]),
    }
    try:
        info = agent_request(dev, "GET", "/api/info", timeout=3)
        out["agent"] = info
        out["reachable"] = bool(info.get("ok"))
    except AgentError as e:
        out["error"] = str(e)
        return out
    return out
