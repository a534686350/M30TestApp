#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M30 站点汇总服务（总控制台）

设计原则
--------
1. **只读**：这里没有任何下发控制指令的接口。上位机永远是权威，控制台只负责"看"和"取"。
   后续若要加远程控制，必须走"指令下发 + 上位机本地安全校验 + 回执"的路径。
2. **零依赖**：只用 Python 标准库（http.server + sqlite3），服务器上不用装任何包。
3. **离线优先**：上位机连不上这里只是重试，绝不阻塞测试流程；这里收不到心跳就把站点标灰。
4. **浅色为主**：页面默认浅色，右上角可切深色（localStorage 记住选择）。

接口
----
    POST /api/report                     上报状态（上位机调用），JSON，可选 X-Station-Token 校验
    GET  /api/stations                   全部站点最新状态（JSON）
    GET  /api/station?id=ID              单台详情（JSON，含工位明细与心跳历史）
    GET  /api/station/ID/slots.csv       导出工位明细 CSV
    GET  /api/station/ID/history.csv     导出心跳历史 CSV
    GET  /api/station/ID/snapshot.json   导出完整快照 JSON（留档用）
    GET  /                               总控制台看板（服务端渲染）
    GET  /station?id=ID                  单台详情页（加 &frag=1 只返回局部片段，供页面局部刷新）
    GET  /health                         存活探针

环境变量
--------
    M30_HUB_PORT      监听端口（默认 80）
    M30_HUB_DB        SQLite 路径（默认 /opt/m30hub/stations.db）
    M30_HUB_USER      控制台用户名（留空 = 不校验）
    M30_HUB_PASS      控制台密码（留空 = 不校验）
    M30_HUB_TOKEN     站点上报令牌（留空 = 不校验）
    M30_HUB_OFFLINE   多久没心跳算离线，秒（默认 30）
    M30_HUB_KEEP      每站保留多少条心跳历史（默认 5000）
"""

import base64
import hashlib
import hmac
import json
import os
import re
import sqlite3
import threading
import time
from html import escape
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, quote, unquote, urlparse

PORT = int(os.environ.get("M30_HUB_PORT", "80"))
DB_PATH = os.environ.get("M30_HUB_DB", "/opt/m30hub/stations.db")
CONSOLE_USER = os.environ.get("M30_HUB_USER", "")
CONSOLE_PASS = os.environ.get("M30_HUB_PASS", "")
STATION_TOKEN = os.environ.get("M30_HUB_TOKEN", "")
OFFLINE_SECONDS = int(os.environ.get("M30_HUB_OFFLINE", "30"))
KEEP_HEARTBEATS = int(os.environ.get("M30_HUB_KEEP", "5000"))

_lock = threading.Lock()

# ── 浏览器会话 ──────────────────────────────────────────────────────────
# 原来用 HTTP Basic 认证，浏览器会弹一个不可定制的原生对话框（样式丑、
# HTTP 站点还会附带「连接不是私密连接」的警告）。改成自带登录页 + 签名 Cookie。
# Basic 头仍然接受，方便 curl / 脚本（curl -u admin:pass）。
SESSION_COOKIE = "m30hub_sess"
SESSION_TTL = int(os.environ.get("M30_HUB_SESSION_TTL", str(12 * 3600)))  # 默认 12 小时
SESSION_SECRET = (os.environ.get("M30_HUB_SECRET", "")
                  or CONSOLE_PASS or "m30hub-insecure-fallback")


def _sign(payload: str) -> str:
    return hmac.new(SESSION_SECRET.encode("utf-8"), payload.encode("utf-8"),
                    hashlib.sha256).hexdigest()


def make_session(user: str) -> str:
    """签发会话令牌：user|过期时间戳|HMAC 签名。"""
    exp = int(time.time()) + SESSION_TTL
    payload = f"{user}|{exp}"
    return f"{payload}|{_sign(payload)}"


def session_ok(token: str) -> bool:
    if not token:
        return False
    parts = token.split("|")
    if len(parts) != 3:
        return False
    user, exp, sig = parts
    if not hmac.compare_digest(_sign(f"{user}|{exp}"), sig):
        return False
    try:
        return int(exp) > time.time()
    except ValueError:
        return False

STATE_LABEL = {
    "running": ("运行中", "ok"),
    "paused": ("已暂停", "warn"),
    "idle": ("空闲", "idle"),
    "alarm": ("报警", "err"),
    "error": ("异常", "err"),
    "offline": ("离线", "off"),
}

SLOT_LABEL = {"ok": "合格", "warn": "警告", "err": "异常", "idle": "未测"}


# ── 数据库 ──────────────────────────────────────────────────────────────

def db():
    conn = sqlite3.connect(DB_PATH, timeout=10)
    conn.row_factory = sqlite3.Row
    return conn


# 第二期新增列：老库直接 ALTER 补上，不用手工迁移
_EXTRA_COLUMNS = [
    ("model", "TEXT"),
    ("batch", "TEXT"),
    ("started_at", "TEXT"),
    ("elapsed", "TEXT"),
    ("temp_point", "TEXT"),
    ("pressure_point", "TEXT"),
    ("slot_detail", "TEXT"),
    ("slot_detail_at", "REAL"),
    ("metrics", "TEXT"),
]


def init_db():
    os.makedirs(os.path.dirname(DB_PATH) or ".", exist_ok=True)
    with db() as conn:
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS station (
                station_id  TEXT PRIMARY KEY,
                name        TEXT,
                version     TEXT,
                state       TEXT,
                plan        TEXT,
                step        TEXT,
                progress    REAL,
                alarms      INTEGER,
                slot_total  INTEGER,
                slot_ok     INTEGER,
                slot_warn   INTEGER,
                slot_err    INTEGER,
                extra       TEXT,
                first_seen  REAL,
                last_seen   REAL
            )
            """
        )
        existing = {r["name"] for r in conn.execute("PRAGMA table_info(station)")}
        for col, ctype in _EXTRA_COLUMNS:
            if col not in existing:
                conn.execute(f"ALTER TABLE station ADD COLUMN {col} {ctype}")

        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS heartbeat (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                station_id  TEXT NOT NULL,
                ts          REAL NOT NULL,
                state       TEXT,
                step        TEXT,
                progress    REAL,
                alarms      INTEGER,
                slot_total  INTEGER,
                slot_ok     INTEGER,
                slot_warn   INTEGER,
                slot_err    INTEGER,
                temp_point  TEXT,
                pressure_point TEXT
            )
            """
        )
        conn.execute("CREATE INDEX IF NOT EXISTS idx_hb ON heartbeat(station_id, ts DESC)")
        conn.commit()


def _as_list(value):
    return value if isinstance(value, list) else []


def upsert(payload: dict) -> None:
    sid = str(payload.get("stationId") or "").strip()
    if not sid:
        raise ValueError("stationId 不能为空")

    now = time.time()
    slots = payload.get("slots") or {}
    slot_total = int(slots.get("total") or 0)
    slot_ok = int(slots.get("ok") or 0)
    slot_warn = int(slots.get("warn") or 0)
    slot_err = int(slots.get("err") or 0)
    state = str(payload.get("state") or "idle")
    step = str(payload.get("step") or "")
    progress = float(payload.get("progress") or 0.0)
    alarms = int(payload.get("alarms") or 0)

    # 明细只在变化时由上位机下发；没带（None）就保留库里上一份
    slot_detail = payload.get("slotDetails")
    slot_detail_json = None
    if isinstance(slot_detail, list):
        slot_detail_json = json.dumps(slot_detail, ensure_ascii=False)

    metrics = payload.get("metrics")
    metrics_json = None
    if isinstance(metrics, list):
        metrics_json = json.dumps(metrics, ensure_ascii=False)

    with _lock, db() as conn:
        row = conn.execute("SELECT first_seen FROM station WHERE station_id=?", (sid,)).fetchone()
        first = row["first_seen"] if row else now

        conn.execute(
            """
            INSERT INTO station (station_id, name, version, state, plan, step, progress, alarms,
                                 slot_total, slot_ok, slot_warn, slot_err, extra, first_seen, last_seen,
                                 model, batch, started_at, elapsed, temp_point, pressure_point,
                                 slot_detail, slot_detail_at, metrics)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(station_id) DO UPDATE SET
                name=excluded.name, version=excluded.version, state=excluded.state,
                plan=excluded.plan, step=excluded.step, progress=excluded.progress,
                alarms=excluded.alarms, slot_total=excluded.slot_total,
                slot_ok=excluded.slot_ok, slot_warn=excluded.slot_warn,
                slot_err=excluded.slot_err, extra=excluded.extra, last_seen=excluded.last_seen,
                model=excluded.model, batch=excluded.batch, started_at=excluded.started_at,
                elapsed=excluded.elapsed, temp_point=excluded.temp_point,
                pressure_point=excluded.pressure_point,
                slot_detail=COALESCE(excluded.slot_detail, station.slot_detail),
                slot_detail_at=COALESCE(excluded.slot_detail_at, station.slot_detail_at),
                metrics=COALESCE(excluded.metrics, station.metrics)
            """,
            (
                sid,
                str(payload.get("name") or sid),
                str(payload.get("version") or ""),
                state,
                str(payload.get("plan") or ""),
                step,
                progress,
                alarms,
                slot_total,
                slot_ok,
                slot_warn,
                slot_err,
                json.dumps(payload.get("extra") or {}, ensure_ascii=False),
                first,
                now,
                str(payload.get("model") or ""),
                str(payload.get("batch") or ""),
                str(payload.get("startedAt") or ""),
                str(payload.get("elapsed") or ""),
                str(payload.get("tempPoint") or ""),
                str(payload.get("pressurePoint") or ""),
                slot_detail_json,
                now if slot_detail_json is not None else None,
                metrics_json,
            ),
        )

        _append_heartbeat(conn, sid, now, state, step, progress, alarms,
                          slot_total, slot_ok, slot_warn, slot_err,
                          str(payload.get("tempPoint") or ""),
                          str(payload.get("pressurePoint") or ""))
        conn.commit()


def _append_heartbeat(conn, sid, now, state, step, progress, alarms,
                      total, ok, warn, err, temp_point, pressure_point):
    """
    写一条心跳历史。

    每 5 秒一条的话一天就是 1.7 万条，没必要 —— 只在"内容变了"或"距上一条满 60 秒"
    的时候写，既能画出变化曲线，也不会把库撑爆。
    """
    last = conn.execute(
        "SELECT * FROM heartbeat WHERE station_id=? ORDER BY ts DESC LIMIT 1", (sid,)
    ).fetchone()

    if last is not None:
        same = (
            last["state"] == state
            and last["step"] == step
            and abs((last["progress"] or 0) - progress) < 0.001
            and (last["alarms"] or 0) == alarms
            and (last["slot_ok"] or 0) == ok
            and (last["slot_warn"] or 0) == warn
            and (last["slot_err"] or 0) == err
            and (last["slot_total"] or 0) == total
        )
        if same and (now - (last["ts"] or 0)) < 60:
            return

    conn.execute(
        """
        INSERT INTO heartbeat (station_id, ts, state, step, progress, alarms,
                               slot_total, slot_ok, slot_warn, slot_err,
                               temp_point, pressure_point)
        VALUES (?,?,?,?,?,?,?,?,?,?,?,?)
        """,
        (sid, now, state, step, progress, alarms, total, ok, warn, err,
         temp_point, pressure_point),
    )

    # 只留最近 N 条
    conn.execute(
        """
        DELETE FROM heartbeat
        WHERE station_id = ?
          AND id NOT IN (SELECT id FROM heartbeat WHERE station_id = ?
                         ORDER BY ts DESC LIMIT ?)
        """,
        (sid, sid, KEEP_HEARTBEATS),
    )


def _decorate(r):
    r = dict(r)
    r["online"] = (time.time() - (r.get("last_seen") or 0)) <= OFFLINE_SECONDS
    r["ago"] = int(time.time() - (r.get("last_seen") or time.time()))
    try:
        r["extra_obj"] = json.loads(r.get("extra") or "{}")
    except Exception:
        r["extra_obj"] = {}
    return r


def fetch_all():
    with lock_read() as conn:
        rows = [dict(r) for r in conn.execute("SELECT * FROM station ORDER BY station_id").fetchall()]
    return [_decorate(r) for r in rows]


def fetch_one(sid):
    with lock_read() as conn:
        row = conn.execute("SELECT * FROM station WHERE station_id=?", (sid,)).fetchone()
        if row is None:
            return None
        history = [dict(h) for h in conn.execute(
            "SELECT * FROM heartbeat WHERE station_id=? ORDER BY ts DESC LIMIT 300", (sid,)
        ).fetchall()]
    out = _decorate(row)
    out["history"] = history
    try:
        out["slot_detail_obj"] = json.loads(out.get("slot_detail") or "[]")
    except Exception:
        out["slot_detail_obj"] = []
    try:
        out["metrics_obj"] = json.loads(out.get("metrics") or "[]")
    except Exception:
        out["metrics_obj"] = []
    return out


class lock_read:
    """只读取一份快照，不长期占用锁。"""

    def __enter__(self):
        _lock.acquire()
        self.conn = db()
        return self.conn

    def __exit__(self, *exc):
        self.conn.close()
        _lock.release()
        return False


# ── 版本清单（下载页数据源）────────────────────────────────────────────
#
# 来源优先级：
#   1. M30_HUB_RELEASES 指定的 JSON 文件（离线 / 内网部署时手工维护）
#   2. /opt/m30hub/releases.json（部署脚本会写入）
#   3. 内置兜底列表（只有当前版本一条）
#
# JSON 结构：  [{"version":"1.2.42","date":"2026-10-15","size":67188046,
#                "file":"M30TestApp.V2-v1.2.42-win-x64-self-contained.zip",
#                "notes":"...","latest":true}, ...]
# 列表按 version 倒序展示；最新一版高亮并置顶。

RELEASES_FILE = os.environ.get("M30_HUB_RELEASES", "/opt/m30hub/releases.json")
RELEASE_DIR = os.environ.get("M30_HUB_RELEASE_DIR", "/opt/m30hub/releases")
LATEST_VERSION = os.environ.get("M30_HUB_VERSION", "")


def _parse_ver(v):
    """把 "1.2.42" 解析成可比较的元组；非数字段退回字符串比较。"""
    parts = re.split(r"[.\-+]", str(v or "").lstrip("vV"))
    out = []
    for p in parts:
        try:
            out.append((0, int(p)))
        except ValueError:
            out.append((1, p))
    return out


def load_releases():
    """读版本清单，失败时回退到内置单条。"""
    data = None
    for path in (RELEASES_FILE, "/opt/m30hub/releases.json"):
        try:
            with open(path, "r", encoding="utf-8") as fh:
                data = json.load(fh)
            break
        except Exception:
            continue

    if not isinstance(data, list):
        data = []

    items = []
    for it in data:
        if not isinstance(it, dict):
            continue
        ver = str(it.get("version") or "").lstrip("vV")
        if not ver:
            continue
        fname = str(it.get("file") or "").strip()
        items.append({
            "version": ver,
            "date": str(it.get("date") or ""),
            "size": int(it.get("size") or 0),
            "file": fname,
            "notes": str(it.get("notes") or ""),
            "sha256": str(it.get("sha256") or ""),
        })

    # 文件不存在时，扫描 RELEASE_DIR 兜底补一条（只补 zip）
    if os.path.isdir(RELEASE_DIR):
        known = {i["file"] for i in items if i["file"]}
        for fn in os.listdir(RELEASE_DIR):
            if not fn.lower().endswith(".zip") or fn in known:
                continue
            m = re.search(r"-v(\d+(?:\.\d+)+)", fn)
            if not m:
                continue
            fp = os.path.join(RELEASE_DIR, fn)
            items.append({
                "version": m.group(1),
                "date": time.strftime("%Y-%m-%d", time.localtime(os.path.getmtime(fp))),
                "size": os.path.getsize(fp),
                "file": fn,
                "notes": "",
                "sha256": "",
            })

    if not items:
        items = [{
            "version": LATEST_VERSION or "unknown",
            "date": "",
            "size": 0,
            "file": "",
            "notes": "尚未发布安装包",
            "sha256": "",
        }]

    items.sort(key=lambda i: _parse_ver(i["version"]), reverse=True)
    for i, it in enumerate(items):
        it["latest"] = i == 0
    return items


def human_size(n):
    if not n:
        return "-"
    x = float(n)
    for unit in ("B", "KB", "MB", "GB"):
        if x < 1024 or unit == "GB":
            return (f"{x:.0f} {unit}" if unit == "B" else f"{x:.1f} {unit}")
        x /= 1024
    return "-"


def find_release_file(fname: str):
    """在发布目录里找文件，返回 (路径, 大小)；找不到返回 (None, 0)。

    只认文件名本身，禁止带路径分隔符或 .. —— 防目录穿越。
    """
    if not fname or "/" in fname or "\\" in fname or ".." in fname:
        return None, 0
    path = os.path.join(RELEASE_DIR, fname)
    real_dir = os.path.realpath(RELEASE_DIR)
    real_path = os.path.realpath(path)
    if not real_path.startswith(real_dir + os.sep):
        return None, 0
    if not os.path.isfile(real_path):
        return None, 0
    return real_path, os.path.getsize(real_path)


# ── 页面样式（默认浅色，data-theme="dark" 切深色）────────────────────────

CSS = """
:root{
  --bg:#f2f5f9; --card:#ffffff; --line:#e3e9f2; --fg:#0f172a; --dim:#64748b;
  --ok:#0e9f6e; --warn:#c27803; --err:#dc2626; --idle:#94a3b8; --accent:#1a73e8;
  --ok-bg:#e7f7f0; --warn-bg:#fdf5e4; --err-bg:#fdecec; --idle-bg:#eff3f8;
  --head:#ffffff; --soft:#f8fafc;
  --bd:rgba(15,23,42,.06);
  --sh:0 1px 2px rgba(15,23,42,.04), 0 4px 14px rgba(15,23,42,.06);
  --sh-lg:0 6px 12px rgba(15,23,42,.08), 0 18px 36px rgba(15,23,42,.10);
  --r:14px;
}
html[data-theme="dark"]{
  --bg:#0e141c; --card:#18202c; --line:#2a3644; --fg:#e8eef7; --dim:#93a3b8;
  --ok:#34d399; --warn:#fbbf24; --err:#f87171; --idle:#64748b; --accent:#60a5fa;
  --ok-bg:#10291f; --warn-bg:#322512; --err-bg:#3a1d1d; --idle-bg:#222c3a;
  --head:#151d28; --soft:#1d2634;
  --bd:rgba(255,255,255,.07);
  --sh:0 1px 2px rgba(0,0,0,.35), 0 4px 14px rgba(0,0,0,.28);
  --sh-lg:0 6px 12px rgba(0,0,0,.42), 0 18px 36px rgba(0,0,0,.38);
}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--fg);
     font:14px/1.55 "Microsoft YaHei",system-ui,-apple-system,"Segoe UI",sans-serif;
     -webkit-font-smoothing:antialiased}
a{color:var(--accent);text-decoration:none}
header{display:flex;align-items:center;gap:12px;padding:11px 22px;
       background:var(--head);border-bottom:1px solid var(--line);
       position:sticky;top:0;z-index:5;flex-wrap:wrap;
       box-shadow:0 1px 3px var(--bd)}
header h1{font-size:16.5px;margin:0;font-weight:700;letter-spacing:.2px}
header .meta{color:var(--dim);font-size:12.5px}
header .sp{flex:1}
.brand{display:flex;align-items:center;gap:9px;font-weight:700;font-size:16px;
       letter-spacing:.2px;margin-right:6px}
.brand .mark{width:30px;height:30px;border-radius:9px;
  background:linear-gradient(135deg,var(--accent),#7c5cff);
  display:grid;place-items:center;color:#fff;font-size:11.5px;font-weight:800;
  letter-spacing:0;box-shadow:0 3px 9px rgba(26,115,232,.32)}
.nav{display:flex;gap:3px;background:var(--soft);border:1px solid var(--line);
     border-radius:11px;padding:3px}
.nav a{padding:5px 14px;border-radius:8px;font-size:13px;color:var(--dim);
       transition:.15s;white-space:nowrap}
.nav a:hover{color:var(--fg)}
.nav a.on{background:var(--card);color:var(--accent);font-weight:600;
          box-shadow:0 1px 3px var(--bd)}
.ghost{border:1px solid var(--line);background:var(--card);color:var(--fg);
       border-radius:9px;padding:6px 13px;font-size:12.5px;cursor:pointer;
       font-family:inherit;transition:.15s}
.ghost:hover{border-color:var(--accent);color:var(--accent)}

/* ── 概览统计条 ── */
.stats{display:grid;gap:13px;padding:20px 22px 0;
       grid-template-columns:repeat(auto-fit,minmax(156px,1fr))}
.stat{position:relative;background:var(--card);border:1px solid var(--line);
      border-radius:var(--r);padding:15px 17px;box-shadow:var(--sh);
      overflow:hidden;transition:box-shadow .18s,transform .18s}
.stat:hover{box-shadow:var(--sh-lg);transform:translateY(-1px)}
.stat::before{content:'';position:absolute;left:0;top:0;bottom:0;width:3px;
              background:var(--accent)}
.stat.s-ok::before{background:var(--ok)}
.stat.s-warn::before{background:var(--warn)}
.stat.s-err::before{background:var(--err)}
.stat .n{font-size:27px;font-weight:800;line-height:1.05;letter-spacing:-.5px;
         font-variant-numeric:tabular-nums}
.stat .n.ok{color:var(--ok)} .stat .n.warn{color:var(--warn)} .stat .n.err{color:var(--err)}
.stat .l{font-size:12.5px;color:var(--dim);margin-top:3px}
.grid{display:grid;gap:16px;padding:20px 22px;
      grid-template-columns:repeat(auto-fill,minmax(352px,1fr))}
.card{background:var(--card);border:1px solid var(--line);border-radius:var(--r);
      padding:15px;box-shadow:var(--sh)}
/* 设备卡片：纵向 flex + 顶部状态色条，按钮贴底 */
.card.dev{padding:0;overflow:hidden;display:flex;flex-direction:column;
          transition:box-shadow .18s ease,transform .18s ease}
.card.dev:hover{box-shadow:var(--sh-lg);transform:translateY(-2px)}
.card.dev .cap{height:3px;background:var(--idle);flex:none}
.card.dev.is-online .cap{background:linear-gradient(90deg,var(--ok),#34d399)}
.card.dev.is-warn .cap{background:linear-gradient(90deg,var(--warn),#f59e0b)}
.card.dev.is-err .cap{background:linear-gradient(90deg,var(--err),#f87171)}
.card.dev.is-offline{opacity:.62;filter:saturate(.7)}
.dev-top{display:flex;align-items:center;gap:9px;padding:14px 16px 11px}
.dev-top .nm{font-size:15.5px;font-weight:700;letter-spacing:.1px;
             overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:200px}
.dev-top .sp{flex:1}
.dev-body{padding:0 16px}
.dev-body .sid{display:inline-block;max-width:100%}
.sid-gap{margin-top:10px}
/* 在线呼吸灯 */
.dot{width:8px;height:8px;border-radius:50%;background:var(--idle);flex:none;
     position:relative}
.dot.ok{background:var(--ok)} .dot.warn{background:var(--warn)} .dot.err{background:var(--err)}
.dot.live::after{content:'';position:absolute;inset:0;border-radius:50%;
  background:inherit;animation:ping 1.9s cubic-bezier(0,0,.2,1) infinite}
@keyframes ping{0%{transform:scale(1);opacity:.6}80%,100%{transform:scale(2.5);opacity:0}}
.sid{color:var(--dim);font-size:11.5px;font-family:Consolas,"Courier New",monospace;
     background:var(--idle-bg);padding:2px 7px;border-radius:5px;
     overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:160px}
.pill{font-size:11.5px;padding:3px 10px;border-radius:99px;white-space:nowrap;
      border:1px solid currentColor;font-weight:600;flex:none}
.p-ok{color:var(--ok);background:var(--ok-bg)}
.p-warn{color:var(--warn);background:var(--warn-bg)}
.p-err{color:var(--err);background:var(--err-bg)}
.p-idle,.p-off{color:var(--idle);background:var(--idle-bg)}
.line{display:grid;grid-template-columns:66px 1fr;gap:5px 10px;font-size:12.8px}
.line .k{color:var(--dim)}
.line .v{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.bar{height:6px;border-radius:99px;background:var(--idle-bg);overflow:hidden;margin:12px 0 0}
.bar > i{display:block;height:100%;border-radius:99px;
         background:linear-gradient(90deg,var(--accent),#5b9bf8);
         transition:width .4s ease}
.bar.warn > i{background:linear-gradient(90deg,var(--warn),#f5b544)}
.bar.err > i{background:linear-gradient(90deg,var(--err),#f36b6b)}
.chips{margin-top:12px;display:grid;grid-template-columns:repeat(5,1fr);gap:6px}
.chips .ch{text-align:center;padding:7px 3px;border-radius:9px;background:var(--idle-bg);
           font-size:11.5px;color:var(--dim);line-height:1.35}
.chips .ch b{display:block;font-size:15px;font-weight:700;color:var(--fg);
             font-variant-numeric:tabular-nums;letter-spacing:-.3px}
.chips .ch.b-ok{background:var(--ok-bg)} .chips .ch.b-ok b{color:var(--ok)}
.chips .ch.b-warn{background:var(--warn-bg)} .chips .ch.b-warn b{color:var(--warn)}
.chips .ch.b-err{background:var(--err-bg)} .chips .ch.b-err b{color:var(--err)}
.acts{padding:13px 16px 15px;border-top:1px solid var(--line);
      display:flex;gap:8px;flex-wrap:wrap;margin-top:auto}
.btn{display:inline-block;border:1px solid var(--line);border-radius:9px;padding:6px 13px;
     font-size:12.5px;color:var(--fg);background:var(--card);cursor:pointer;
     font-family:inherit;transition:.15s;text-align:center}
.btn:hover{border-color:var(--accent);color:var(--accent);background:var(--soft)}
.btn.primary{background:var(--accent);border-color:var(--accent);color:#fff;font-weight:600}
.btn.primary:hover{background:#1668d4;border-color:#1668d4;color:#fff}
.empty{margin:46px auto;max-width:470px;padding:46px 30px;text-align:center;
       color:var(--dim);background:var(--card);border:1px dashed var(--line);
       border-radius:var(--r);font-size:13.5px;line-height:2}
.empty .ico{font-size:34px;margin-bottom:8px;opacity:.45;line-height:1}
.empty b{color:var(--fg);font-size:15px}
footer{padding:16px 22px 26px;color:var(--dim);font-size:12px;text-align:center}
.wrap{padding:18px 22px;display:flex;flex-direction:column;gap:15px}
.dev-head{display:flex;align-items:center;gap:10px;flex-wrap:wrap;padding:16px 22px 0}
.dev-head h1{font-size:19px;margin:0;font-weight:700;letter-spacing:.2px}
.dev-head .sp{flex:1}
.dev-head + .wrap{padding-top:14px}
.cols{display:grid;gap:15px;grid-template-columns:repeat(auto-fit,minmax(330px,1fr))}
.card h2{font-size:13.5px;margin:0 0 11px;font-weight:700;display:flex;align-items:center;gap:9px}
.card h2 .dim{color:var(--dim);font-weight:400;font-size:12.5px}
.card h2 .sp{flex:1}
.kv{display:grid;grid-template-columns:88px 1fr;gap:7px 10px;font-size:13px}
.kv .k{color:var(--dim)}
table{width:100%;border-collapse:collapse;font-size:12.5px}
th,td{text-align:left;padding:7px 9px;border-bottom:1px solid var(--line);white-space:nowrap}
th{color:var(--dim);font-weight:600;background:var(--soft);position:sticky;top:0}
tbody tr:hover{background:var(--soft)}
.tag{display:inline-block;padding:2px 9px;border-radius:99px;font-size:12px;
     border:1px solid currentColor}
.t-ok{color:var(--ok)} .t-warn{color:var(--warn)} .t-err{color:var(--err)} .t-idle{color:var(--idle)}
.scroll{max-height:420px;overflow:auto;border:1px solid var(--line);border-radius:10px}
.mono{font-family:Consolas,"Courier New",monospace}

/* ── 下载页 ── */
.rel{margin-top:14px;border:1px solid var(--line);border-radius:10px;
     background:var(--card);overflow:hidden}
.rel:first-of-type{margin-top:0}
.rel .top{display:flex;align-items:center;gap:10px;padding:12px 16px;flex-wrap:wrap}
.rel .ver{font-size:16.5px;font-weight:600;font-family:Consolas,monospace}
.rel .badge{font-size:11.5px;padding:2px 9px;border-radius:99px;
            background:var(--ok-bg);color:var(--ok);border:1px solid currentColor;font-weight:600}
.rel .dt{color:var(--dim);font-size:12.5px}
.rel .sp{flex:1}
.rel .body{padding:0 16px 14px}
.rel .notes{margin:0 0 12px;color:var(--dim);font-size:13px;white-space:pre-wrap}
.rel .meta{display:flex;gap:16px;color:var(--dim);font-size:12.5px;margin-bottom:12px;flex-wrap:wrap}
.rel .meta b{color:var(--fg);font-weight:600}
.rel .dl{border:1px solid var(--line);border-radius:8px;padding:10px 12px;
         display:flex;align-items:center;gap:10px;background:var(--soft);flex-wrap:wrap}
.rel .dl .fn{font-size:12.5px;color:var(--dim);word-break:break-all;flex:1;min-width:200px}
.rel .dl a{white-space:nowrap}
.dl-hero{padding:16px 20px 4px}
.dl-hero h2{font-size:16px;margin:0 0 4px}
.dl-hero p{margin:0;color:var(--dim);font-size:13px}
.copybox-wrap{display:flex;gap:8px;align-items:center;margin-top:8px;flex-wrap:wrap}
.copybox{font-family:Consolas,monospace;font-size:12.5px;padding:6px 10px;
         border:1px solid var(--line);border-radius:6px;background:var(--soft);
         color:var(--fg);flex:1;min-width:240px}

/* ── 登录页 ── */
body.login-page{display:grid;place-items:center;min-height:100vh;padding:24px;
  background:
    radial-gradient(1000px 520px at 12% -10%, rgba(26,115,232,.17), transparent 60%),
    radial-gradient(820px 460px at 100% 110%, rgba(124,92,255,.15), transparent 55%),
    var(--bg)}
html[data-theme="dark"] body.login-page{background:
    radial-gradient(1000px 520px at 12% -10%, rgba(96,165,250,.15), transparent 60%),
    radial-gradient(820px 460px at 100% 110%, rgba(124,92,255,.14), transparent 55%),
    var(--bg)}
.login-card{width:100%;max-width:374px;background:var(--card);border:1px solid var(--line);
  border-radius:20px;padding:34px 30px 24px;box-shadow:var(--sh-lg)}
.login-card .mark{width:46px;height:46px;border-radius:14px;
  background:linear-gradient(135deg,var(--accent),#7c5cff);
  display:grid;place-items:center;color:#fff;font-size:15px;font-weight:800;
  letter-spacing:.5px;box-shadow:0 8px 20px rgba(26,115,232,.32);margin:0 auto 16px}
.login-card h1{font-size:21px;margin:0;text-align:center;font-weight:700;letter-spacing:.2px}
.login-card .sub{margin:6px 0 24px;text-align:center;color:var(--dim);font-size:12.8px}
.fld{display:block;margin-bottom:14px}
.fld > span{display:block;font-size:12.5px;color:var(--dim);margin-bottom:6px}
.fld input{width:100%;padding:11px 13px;font-size:14px;font-family:inherit;
  color:var(--fg);background:var(--soft);border:1px solid var(--line);
  border-radius:11px;outline:none;transition:border-color .15s,box-shadow .15s,background .15s}
.fld input:focus{border-color:var(--accent);background:var(--card);
  box-shadow:0 0 0 3.5px rgba(26,115,232,.16)}
.login-card .err{background:var(--err-bg);color:var(--err);border-radius:10px;
  padding:9px 12px;font-size:12.8px;margin-bottom:14px}
.login-card .submit{width:100%;padding:12px;border:0;border-radius:11px;cursor:pointer;
  font-family:inherit;font-size:14.5px;font-weight:700;color:#fff;letter-spacing:.3px;
  background:linear-gradient(135deg,var(--accent),#5b8def);
  box-shadow:0 6px 16px rgba(26,115,232,.30);transition:transform .15s,box-shadow .15s,filter .15s}
.login-card .submit:hover{transform:translateY(-1px);filter:brightness(1.06);
  box-shadow:0 9px 22px rgba(26,115,232,.36)}
.login-card .submit:active{transform:translateY(0)}
.login-foot{margin-top:20px;padding-top:16px;border-top:1px solid var(--line);
  display:flex;align-items:center;justify-content:space-between;font-size:12.3px}
.login-foot .hint{color:var(--dim)}
"""

THEME_JS = """
function m30ToggleTheme(){
  var cur = document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
  var next = cur === 'dark' ? 'light' : 'dark';
  document.documentElement.setAttribute('data-theme', next);
  try{ localStorage.setItem('m30theme', next); }catch(e){}
  var b = document.getElementById('themeBtn');
  if(b) b.textContent = next === 'dark' ? '浅色' : '深色';
}
(function(){
  var b = document.getElementById('themeBtn');
  if(b) b.textContent = document.documentElement.getAttribute('data-theme') === 'dark' ? '浅色' : '深色';
})();
"""

HTML_HEAD = """<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>M30 总控制台</title>
<script>
(function(){try{var t=localStorage.getItem('m30theme');if(t)document.documentElement.setAttribute('data-theme',t);}catch(e){}})();
</script>
<style>""" + CSS + """</style>
</head>
<body>
"""


def page(header_html, body_html, script=""):
    return (
        HTML_HEAD
        + header_html
        + body_html
        + '<footer>只读看板。上位机为权威，控制台只收心跳与状态，不下发任何控制指令。</footer>'
        + "<script>" + THEME_JS + script + "</script></body></html>"
    )


def theme_button():
    return '<button class="ghost" id="themeBtn" onclick="m30ToggleTheme()">深色</button>'


def brand(title: str = "总控制台") -> str:
    """左上角品牌标识：渐变方块 + 标题。"""
    return ('<div class="brand"><span class="mark">M30</span>'
            f'<span>{escape(title)}</span></div>')


def nav_tabs(active: str) -> str:
    """顶部主导航。active 取 board / download，决定哪个高亮。"""
    items = [("/", "设备看板", "board"), ("/download", "程序下载", "download")]
    links = []
    for href, label, key in items:
        on = ' class="on"' if key == active else ""
        links.append(f'<a href="{href}"{on}>{label}</a>')
    return '<nav class="nav">' + "".join(links) + "</nav>"


def logout_button() -> str:
    return '<a class="ghost" href="/logout" title="清除本机会话">退出</a>'


def header_tools() -> str:
    """顶栏右侧工具区：退出（仅启用鉴权时显示）+ 深浅色切换。"""
    return (logout_button() if CONSOLE_USER else "") + theme_button()


# ── 登录页 ──────────────────────────────────────────────────────────────

def render_login(error: str = "", next_url: str = "/") -> str:
    """自带登录页，替代浏览器原生的 Basic Auth 对话框。"""
    err_html = f'<div class="err">⚠️ {escape(error)}</div>' if error else ""
    body = f"""
<div class="login-card">
  <div class="mark">M30</div>
  <h1>总控制台</h1>
  <p class="sub">M30 测试上位机 · 设备状态汇总</p>
  <form method="post" action="/login">
    <input type="hidden" name="next" value="{escape(next_url, quote=True)}">
    {err_html}
    <label class="fld"><span>用户名</span>
      <input name="user" autocomplete="username" autofocus required></label>
    <label class="fld"><span>密码</span>
      <input name="pass" type="password" autocomplete="current-password" required></label>
    <button class="submit" type="submit">登 录</button>
  </form>
  <div class="login-foot">
    <span class="hint">M30 Test Console</span>
    <a href="/download">程序下载 →</a>
  </div>
</div>"""
    return (
        '<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">'
        '<meta name="viewport" content="width=device-width,initial-scale=1">'
        '<title>登录 · M30 总控制台</title>'
        '<script>(function(){try{var t=localStorage.getItem("m30theme");'
        'if(t)document.documentElement.setAttribute("data-theme",t);}catch(e){}})();</script>'
        '<style>' + CSS + '</style></head><body class="login-page">' + body
        + '</body></html>'
    )


# ── 看板 ────────────────────────────────────────────────────────────────

def _stats(rows) -> str:
    """顶部概览：设备总数 / 在线 / 运行中 / 报警 / 异常工位。"""
    total = len(rows)
    online = sum(1 for r in rows if r["online"])
    running = sum(1 for r in rows if r["online"] and str(r["state"]) == "running")
    alarms = sum(int(r["alarms"] or 0) for r in rows if r["online"])
    errs = sum(int(r["slot_err"] or 0) for r in rows if r["online"])

    cards = [
        (total, "设备总数", "", ""),
        (online, "在线", "s-ok" if online else "", "ok" if online else ""),
        (running, "运行中", "s-ok" if running else "", "ok" if running else ""),
        (alarms, "报警", "s-err" if alarms else "", "err" if alarms else ""),
        (errs, "异常工位", "s-warn" if errs else "", "warn" if errs else ""),
    ]
    out = []
    for num, label, scls, ncls in cards:
        out.append(f'<div class="stat {scls}"><div class="n {ncls}">{num}</div>'
                   f'<div class="l">{label}</div></div>')
    return '<div class="stats">' + "".join(out) + "</div>"


def _card(r):
    state = r["state"] if r["online"] else "offline"
    label, kind = STATE_LABEL.get(state, (state or "未知", "idle"))
    if r["online"] and (r["slot_err"] or r["alarms"]):
        kind = "err"
    cls = {"ok": "is-online", "warn": "is-warn", "err": "is-err"}.get(kind, "is-offline")
    dot = {"ok": "ok", "warn": "warn", "err": "err"}.get(kind, "idle")
    # 只有「在线且正常」才跑呼吸动画；报警 / 离线常亮，免得满屏闪
    live = " live" if r["online"] and kind == "ok" else ""
    barcls = {"warn": " warn", "err": " err"}.get(kind, "")
    pct = max(0.0, min(1.0, float(r["progress"] or 0))) * 100
    note = r["extra_obj"].get("note") or "-"
    sid = str(r["station_id"])
    q = escape(sid, quote=True)

    return f"""
 <div class="card dev {cls}">
   <div class="cap"></div>
   <div class="dev-top">
     <span class="dot {dot}{live}"></span>
     <span class="nm">{escape(str(r['name'] or sid))}</span>
     <span class="sp"></span>
     <span class="pill p-{kind}">{label}</span>
   </div>
   <div class="dev-body">
     <span class="sid">{escape(sid)}</span>
     <div class="line sid-gap">
       <span class="k">测试方案</span><span class="v">{escape(str(r['plan'] or '-'))}</span>
       <span class="k">传感器</span><span class="v">{escape(str(r.get('model') or '-'))}</span>
       <span class="k">当前工步</span><span class="v">{escape(str(r['step'] or '-'))}</span>
       <span class="k">程序版本</span><span class="v">{escape(str(r['version'] or '-'))}</span>
       <span class="k">最后心跳</span><span class="v">{r['ago']} 秒前</span>
       <span class="k">备注</span><span class="v">{escape(str(note))}</span>
     </div>
     <div class="bar{barcls}"><i style="width:{pct:.1f}%"></i></div>
     <div class="chips">
       <span class="ch"><b>{r['slot_total'] or 0}</b>工位</span>
       <span class="ch b-ok"><b>{r['slot_ok'] or 0}</b>合格</span>
       <span class="ch b-warn"><b>{r['slot_warn'] or 0}</b>警告</span>
       <span class="ch b-err"><b>{r['slot_err'] or 0}</b>异常</span>
       <span class="ch"><b>{r['alarms'] or 0}</b>报警</span>
     </div>
   </div>
   <div class="acts">
     <a class="btn primary" href="/station?id={q}">查看详情</a>
     <a class="btn" href="/api/station/{q}/slots.csv">工位 CSV</a>
     <a class="btn" href="/api/station/{q}/history.csv">心跳 CSV</a>
   </div>
 </div>"""


def render_board(rows) -> str:
    if not rows:
        body = ('<div class="empty"><div class="ico">▤</div>'
                '<b>还没有设备上报</b><br>'
                '在上位机「配置 → 系统设置 → 上报设备 ID」里填好站点 ID 与控制台地址后，'
                '设备会自动出现在这里。</div>')
    else:
        body = (_stats(rows)
                + '<div class="grid">' + "".join(_card(r) for r in rows) + "</div>")

    header = (
        '<header>'
        + brand("总控制台")
        + nav_tabs("board")
        + '<span class="sp"></span>'
        + f'<span class="meta">刷新于 {time.strftime("%H:%M:%S")}</span>'
        + header_tools()
        + '</header>'
    )
    return page(header, body, "setTimeout(function(){location.reload();},5000);")


# ── 下载页 ──────────────────────────────────────────────────────────────

DOWNLOAD_JS = """
function m30Copy(el){
  var t = el.getAttribute('data-copy') || el.textContent;
  if(navigator.clipboard){ navigator.clipboard.writeText(t).then(function(){
    el.textContent='已复制'; setTimeout(function(){ el.textContent=t; },1200);
  }); } else { el.select && el.select(); }
}
"""


def _rel_card(it) -> str:
    ver = escape(it["version"])
    badge = '<span class="badge">最新</span>' if it["latest"] else ""
    size = human_size(it["size"])
    sha = escape(it["sha256"]) if it["sha256"] else ""

    notes = escape(it["notes"]) if it["notes"] else ""

    if it["file"]:
        href = "/download/" + escape(it["file"], quote=True)
        row = (
            '<div class="dl">'
            f'<span class="fn mono">{escape(it["file"])}</span>'
            f'<a class="btn primary" href="{href}">下载</a>'
            '</div>'
        )
    else:
        row = ('<div class="dl"><span class="fn">安装包尚未上传到控制台，'
               '请到 Gitee / GitHub Release 页面下载。</span></div>')

    sha_html = f'<span>SHA256 <b class="mono">{sha}</b></span>' if sha else ""

    return f"""
 <div class="rel">
   <div class="top">
     <span class="ver">v{ver}</span>{badge}
     <span class="dt">{escape(it['date'] or '')}</span>
     <span class="sp"></span>
     <span class="dt">{size}</span>
   </div>
   <div class="body">
     {f'<div class="notes">{notes}</div>' if notes else ''}
     <div class="meta">{sha_html}</div>
     {row}
   </div>
 </div>"""


def render_download(releases) -> str:
    latest = releases[0] if releases else None

    hero = (
        '<div class="dl-hero">'
        '<h2>M30 测试上位机 · 程序下载</h2>'
        '<p>绿色免安装（自包含 win-x64），下载解压后直接运行 <b class="mono">M30TestApp.V2.exe</b>。'
        '按版本倒序排列，最新版在顶部。</p>'
        '</div>'
    )

    body = '<div class="wrap">' + hero
    if not releases:
        body += '<div class="empty">暂无可用版本。</div>'
    else:
        body += "".join(_rel_card(it) for it in releases)
    body += "</div>"

    header = (
        '<header>'
        + brand("程序下载")
        + nav_tabs("download")
        + '<span class="sp"></span>'
        + (f'<span class="meta">最新 v{escape(latest["version"])}</span>' if latest else "")
        + f'<span class="meta">{len(releases)} 个版本</span>'
        + header_tools()
        + '</header>'
    )
    return page(header, body, DOWNLOAD_JS)


# ── 详情 ────────────────────────────────────────────────────────────────

def _detail_fragment(r) -> str:
    sid = str(r["station_id"])
    q = escape(sid, quote=True)
    state = r["state"] if r["online"] else "offline"
    label, kind = STATE_LABEL.get(state, (state or "未知", "idle"))
    if r["online"] and (r["slot_err"] or r["alarms"]):
        kind = "err"
    pct = max(0.0, min(1.0, float(r["progress"] or 0))) * 100

    def v(key, default="-"):
        val = r.get(key)
        return escape(str(val)) if val not in (None, "") else default

    # 工位明细
    details = r.get("slot_detail_obj") or []
    slot_rows = []
    for item in details:
        st = str(item.get("state") or "idle")
        note = str(item.get("note") or "")
        slot_rows.append(
            "<tr>"
            f"<td class='mono'>{escape(str(item.get('slot') or ''))}</td>"
            f"<td class='mono'>{escape(str(item.get('serial') or '')) or '-'}</td>"
            f"<td><span class='tag t-{escape(st)}'>{SLOT_LABEL.get(st, st)}</span></td>"
            f"<td>{escape(note)}</td>"
            "</tr>"
        )
    slot_table = (
        "<div class='scroll'><table><thead><tr>"
        "<th>工位</th><th>序列号</th><th>判定</th><th>备注</th>"
        "</tr></thead><tbody>" + "".join(slot_rows) + "</tbody></table></div>"
        if slot_rows else "<div class='empty' style='padding:24px'>还没有工位明细上报。</div>"
    )

    # 指标
    metrics = r.get("metrics_obj") or []
    metric_html = ""
    if metrics:
        mrows = "".join(
            "<tr>"
            f"<td>{escape(str(m.get('name') or ''))}</td>"
            f"<td class='mono'>{escape(str(m.get('value') or ''))}</td>"
            f"<td class='mono'>{escape(str(m.get('limit') or ''))}</td>"
            f"<td>{escape(str(m.get('result') or ''))}</td>"
            "</tr>"
            for m in metrics
        )
        metric_html = (
            "<section class='card'><h2>指标判定</h2>"
            "<div class='scroll'><table><thead><tr>"
            "<th>指标</th><th>实测</th><th>限值</th><th>结论</th>"
            "</tr></thead><tbody>" + mrows + "</tbody></table></div></section>"
        )

    # 心跳历史
    hist = r.get("history") or []
    hrows = []
    for h in hist:
        hs = str(h.get("state") or "")
        hlabel, hkind = STATE_LABEL.get(hs, (hs or "-", "idle"))
        hrows.append(
            "<tr>"
            f"<td class='mono'>{time.strftime('%m-%d %H:%M:%S', time.localtime(h.get('ts') or 0))}</td>"
            f"<td><span class='tag t-{hkind}'>{hlabel}</span></td>"
            f"<td>{escape(str(h.get('step') or '-'))}</td>"
            f"<td>{float(h.get('progress') or 0) * 100:.1f}%</td>"
            f"<td>{escape(str(h.get('temp_point') or '-'))}</td>"
            f"<td>{escape(str(h.get('pressure_point') or '-'))}</td>"
            f"<td>{h.get('slot_ok') or 0} / {h.get('slot_warn') or 0} / {h.get('slot_err') or 0}</td>"
            f"<td>{h.get('alarms') or 0}</td>"
            "</tr>"
        )
    hist_table = (
        "<div class='scroll'><table><thead><tr>"
        "<th>时间</th><th>状态</th><th>工步</th><th>进度</th><th>温度点</th><th>压力点</th>"
        "<th>合格/警告/异常</th><th>报警</th>"
        "</tr></thead><tbody>" + "".join(hrows) + "</tbody></table></div>"
        if hrows else "<div class='empty' style='padding:24px'>还没有心跳历史。</div>"
    )

    note = r["extra_obj"].get("note") or "-"
    machine = r["extra_obj"].get("machine") or "-"
    detail_at = r.get("slot_detail_at")

    return f"""
<div class="cols">
  <section class="card">
    <h2>基本信息</h2>
    <div class="kv">
      <span class="k">站点 ID</span><span class="mono">{escape(sid)}</span>
      <span class="k">显示名称</span><span>{escape(str(r['name'] or '-'))}</span>
      <span class="k">状态</span><span><span class="tag t-{kind}">{label}</span></span>
      <span class="k">最后心跳</span><span>{r['ago']} 秒前</span>
      <span class="k">首次出现</span><span>{time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(r['first_seen'] or 0))}</span>
      <span class="k">程序版本</span><span>{v('version')}</span>
      <span class="k">运行模式</span><span>{escape(str(note))}</span>
      <span class="k">计算机名</span><span>{escape(str(machine))}</span>
    </div>
  </section>

  <section class="card">
    <h2>测试信息</h2>
    <div class="kv">
      <span class="k">测试方案</span><span>{v('plan')}</span>
      <span class="k">传感器型号</span><span>{v('model')}</span>
      <span class="k">批次号</span><span class="mono">{v('batch')}</span>
      <span class="k">当前工步</span><span>{v('step')}</span>
      <span class="k">温度点</span><span>{v('temp_point')}</span>
      <span class="k">压力点</span><span>{v('pressure_point')}</span>
      <span class="k">开始时间</span><span>{v('started_at')}</span>
      <span class="k">已运行</span><span class="mono">{v('elapsed')}</span>
      <span class="k">进度</span><span>{pct:.1f}%</span>
    </div>
    <div class="bar"><i style="width:{pct:.1f}%"></i></div>
    <div class="chips">
      <span>工位 {r['slot_total'] or 0}</span>
      <span class="b-ok">合格 {r['slot_ok'] or 0}</span>
      <span class="b-warn">警告 {r['slot_warn'] or 0}</span>
      <span class="b-err">异常 {r['slot_err'] or 0}</span>
      <span>报警 {r['alarms'] or 0}</span>
    </div>
  </section>
</div>

<section class="card">
  <h2>工位明细 <span class="dim">{len(slot_rows)} 行{'' if not detail_at else ' · 更新于 ' + time.strftime('%H:%M:%S', time.localtime(detail_at))}</span>
    <span class="sp"></span>
    <a class="btn" href="/api/station/{q}/slots.csv">导出 CSV</a>
  </h2>
  {slot_table}
</section>

{metric_html}

<section class="card">
  <h2>心跳历史 <span class="dim">最近 {len(hist)} 条</span>
    <span class="sp"></span>
    <a class="btn" href="/api/station/{q}/history.csv">导出 CSV</a>
  </h2>
  {hist_table}
</section>
"""


def render_detail(r, fragment=False) -> str:
    frag = _detail_fragment(r)
    if fragment:
        return frag

    sid = str(r["station_id"])
    q = escape(sid, quote=True)
    state = r["state"] if r["online"] else "offline"
    label, kind = STATE_LABEL.get(state, (state or "未知", "idle"))
    if r["online"] and (r["slot_err"] or r["alarms"]):
        kind = "err"

    dot = {"ok": "ok", "warn": "warn", "err": "err"}.get(kind, "idle")
    live = " live" if r["online"] and kind == "ok" else ""

    header = (
        '<header>'
        + brand("设备详情")
        + nav_tabs("board")
        + '<span class="sp"></span>'
        + '<a class="btn" href="/">← 返回看板</a>'
        + header_tools()
        + '</header>'
    )

    # 设备标题区放在 #live 外面 —— 局部刷新只换 #live 里的内容，标题不会被冲掉
    head_bar = (
        '<div class="dev-head">'
        f'<span class="dot {dot}{live}"></span>'
        f'<h1>{escape(str(r["name"] or sid))}</h1>'
        f'<span class="sid">{escape(sid)}</span>'
        f'<span class="pill p-{kind}">{label}</span>'
        '<span class="sp"></span>'
        f'<a class="btn" href="/api/station/{q}/slots.csv">工位 CSV</a>'
        f'<a class="btn" href="/api/station/{q}/history.csv">心跳 CSV</a>'
        f'<a class="btn" href="/api/station/{q}/snapshot.json">快照 JSON</a>'
        '</div>'
    )

    body = head_bar + f'<div class="wrap" id="live">{frag}</div>'
    script = """
// 局部刷新：只换 #live 里的内容，不整页 reload（否则滚动位置和展开状态都会丢）
setInterval(function(){
  var u = location.pathname + (location.search ? location.search + '&' : '?') + 'frag=1';
  fetch(u, {cache:'no-store'}).then(function(r){return r.text();}).then(function(t){
    var el = document.getElementById('live');
    if(el) el.innerHTML = t;
  }).catch(function(){});
}, 5000);
"""
    return page(header, body, script)


# ── 导出 ────────────────────────────────────────────────────────────────

def _safe_name(sid: str) -> str:
    return re.sub(r"[^0-9A-Za-z_\-\.]+", "_", sid) or "station"


def slots_csv(r) -> bytes:
    sid = str(r["station_id"])
    details = r.get("slot_detail_obj") or []
    lines = ["工位,序列号,判定,备注"]
    for item in details:
        st = str(item.get("state") or "idle")
        row = [
            str(item.get("slot") or ""),
            str(item.get("serial") or ""),
            SLOT_LABEL.get(st, st),
            str(item.get("note") or ""),
        ]
        lines.append(",".join(_csv_cell(c) for c in row))
    return ("\ufeff" + "\r\n".join(lines) + "\r\n").encode("utf-8")


def history_csv(r) -> bytes:
    history = r.get("history") or []
    lines = ["时间,状态,工步,进度,温度点,压力点,工位总数,合格,警告,异常,报警"]
    for h in reversed(history):     # 导出按时间正序，跟看表习惯一致
        hs = str(h.get("state") or "")
        label = STATE_LABEL.get(hs, (hs or "", ""))[0]
        row = [
            time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(h.get("ts") or 0)),
            label,
            str(h.get("step") or ""),
            f"{float(h.get('progress') or 0) * 100:.1f}%",
            str(h.get("temp_point") or ""),
            str(h.get("pressure_point") or ""),
            str(h.get("slot_total") or 0),
            str(h.get("slot_ok") or 0),
            str(h.get("slot_warn") or 0),
            str(h.get("slot_err") or 0),
            str(h.get("alarms") or 0),
        ]
        lines.append(",".join(_csv_cell(c) for c in row))
    return ("\ufeff" + "\r\n".join(lines) + "\r\n").encode("utf-8")


def _csv_cell(value: str) -> str:
    value = value or ""
    if any(ch in value for ch in ',"\r\n'):
        return '"' + value.replace('"', '""') + '"'
    return value


def snapshot_json(r) -> bytes:
    payload = {k: v for k, v in r.items()
               if k not in ("extra_obj", "slot_detail_obj", "metrics_obj", "history")}
    payload["slots"] = r.get("slot_detail_obj") or []
    payload["metrics"] = r.get("metrics_obj") or []
    payload["history"] = r.get("history") or []
    payload["exportedAt"] = time.strftime("%Y-%m-%d %H:%M:%S")
    return json.dumps(payload, ensure_ascii=False, indent=2).encode("utf-8")


# ── HTTP ────────────────────────────────────────────────────────────────

class Handler(BaseHTTPRequestHandler):
    server_version = "M30Hub/2.0"

    def log_message(self, fmt, *args):  # 静音：心跳每几秒一次，别把 journal 刷爆
        pass

    # ── 工具 ────────────────────────────────────────────────────────────
    def _send(self, code, body, ctype="application/json; charset=utf-8", extra=None):
        data = body if isinstance(body, bytes) else body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(data)

    def _json(self, code, obj):
        self._send(code, json.dumps(obj, ensure_ascii=False))

    def _download(self, data: bytes, filename: str):
        self._send(200, data, "text/csv; charset=utf-8", {
            "Content-Disposition": f'attachment; filename="{filename}"',
        })

    def _cookie(self, name: str) -> str:
        for part in (self.headers.get("Cookie") or "").split(";"):
            k, _, v = part.strip().partition("=")
            if k == name:
                return v
        return ""

    def _authed(self) -> bool:
        """未配置用户名时全放开；否则接受会话 Cookie 或 Basic 头（脚本用）。"""
        if not CONSOLE_USER:
            return True
        # 1) 浏览器会话 Cookie（登录页签发的）
        if session_ok(self._cookie(SESSION_COOKIE)):
            return True
        # 2) Basic 头：curl -u / 旧脚本仍可用
        head = self.headers.get("Authorization", "")
        if head.startswith("Basic "):
            try:
                raw = base64.b64decode(head[6:]).decode("utf-8")
            except Exception:
                return False
            return hmac.compare_digest(raw, f"{CONSOLE_USER}:{CONSOLE_PASS}")
        return False

    def _wants_html(self) -> bool:
        """浏览器访问（Accept 带 text/html）走跳转，脚本走 401。"""
        return "text/html" in (self.headers.get("Accept") or "")

    def _redirect(self, location: str, extra=None):
        headers = {"Location": location}
        headers.update(extra or {})
        self._send(302, "", "text/plain; charset=utf-8", headers)

    def _deny(self):
        """未登录：页面跳自带登录页，接口返回 401。"""
        if self._wants_html():
            nxt = self.path if self.path.startswith("/") else "/"
            self._redirect("/login?next=" + quote(nxt, safe=""))
        else:
            self._json(401, {"ok": False, "error": "unauthorized"})

    # ── 路由 ────────────────────────────────────────────────────────────
    def do_POST(self):
        path = urlparse(self.path).path

        if path == "/login":
            self._handle_login()
            return

        if path != "/api/report":
            self._json(404, {"ok": False, "error": "not found"})
            return
        if STATION_TOKEN and self.headers.get("X-Station-Token", "") != STATION_TOKEN:
            self._json(403, {"ok": False, "error": "bad token"})
            return
        try:
            n = int(self.headers.get("Content-Length") or 0)
            payload = json.loads(self.rfile.read(n).decode("utf-8") or "{}")
            upsert(payload)
            self._json(200, {"ok": True})
        except Exception as exc:  # 上报方的问题不该影响服务
            self._json(400, {"ok": False, "error": str(exc)})

    def _handle_login(self):
        n = int(self.headers.get("Content-Length") or 0)
        form = parse_qs(self.rfile.read(n).decode("utf-8", "replace"))
        user = (form.get("user") or [""])[0]
        pwd = (form.get("pass") or [""])[0]
        nxt = (form.get("next") or ["/"])[0]
        # 只允许站内相对路径，挡掉 //evil.com 这类开放重定向
        if not nxt.startswith("/") or nxt.startswith("//"):
            nxt = "/"

        if not CONSOLE_USER:      # 未启用鉴权，直接放行
            self._redirect(nxt)
            return

        ok = (hmac.compare_digest(user, CONSOLE_USER)
              and hmac.compare_digest(pwd, CONSOLE_PASS))
        if ok:
            self._redirect(nxt, {
                "Set-Cookie": (f"{SESSION_COOKIE}={make_session(user)}; Path=/; "
                               f"Max-Age={SESSION_TTL}; HttpOnly; SameSite=Lax"),
            })
        else:
            self._send(200, render_login("用户名或密码不正确", nxt),
                       "text/html; charset=utf-8")

    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path
        query = parse_qs(parsed.query)

        if path == "/health":
            self._json(200, {"ok": True})
            return

        # ── 登录 / 退出 ─────────────────────────────────────────────────
        if path == "/login":
            if self._authed():
                self._redirect("/")
                return
            nxt = (query.get("next") or ["/"])[0]
            if not nxt.startswith("/") or nxt.startswith("//"):
                nxt = "/"
            self._send(200, render_login("", nxt), "text/html; charset=utf-8")
            return

        if path == "/logout":
            self._redirect("/login", {
                "Set-Cookie": f"{SESSION_COOKIE}=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax",
            })
            return

        # /api/station/<id>/<file>
        m = re.match(r"^/api/station/(.+?)/(slots\.csv|history\.csv|snapshot\.json)$", path)
        if m:
            if not self._authed():
                self._deny()
                return
            sid = unquote(m.group(1))
            row = fetch_one(sid)
            if row is None:
                self._json(404, {"ok": False, "error": "station not found"})
                return
            stamp = time.strftime("%Y%m%d_%H%M%S")
            base = _safe_name(sid)
            kind = m.group(2)
            if kind == "slots.csv":
                self._download(slots_csv(row), f"{base}_slots_{stamp}.csv")
            elif kind == "history.csv":
                self._download(history_csv(row), f"{base}_history_{stamp}.csv")
            else:
                self._send(200, snapshot_json(row), "application/json; charset=utf-8", {
                    "Content-Disposition": f'attachment; filename="{base}_snapshot_{stamp}.json"',
                })
            return

        if path == "/api/stations":
            if not self._authed():
                self._deny()
                return
            self._json(200, {"ok": True, "stations": fetch_all(), "now": time.time()})
            return

        if path == "/api/station":
            if not self._authed():
                self._deny()
                return
            sid = (query.get("id") or [""])[0]
            row = fetch_one(sid) if sid else None
            if row is None:
                self._json(404, {"ok": False, "error": "station not found"})
                return
            self._json(200, {"ok": True, "station": row})
            return

        if path == "/station":
            if not self._authed():
                self._deny()
                return
            sid = (query.get("id") or [""])[0]
            row = fetch_one(sid) if sid else None
            if row is None:
                self._send(404, "站点不存在", "text/plain; charset=utf-8")
                return
            frag = (query.get("frag") or ["0"])[0] == "1"
            if frag:
                self._send(200, render_detail(row, fragment=True), "text/html; charset=utf-8")
            else:
                self._send(200, render_detail(row), "text/html; charset=utf-8")
            return

        if path in ("/", "/index.html"):
            if not self._authed():
                self._deny()
                return
            self._send(200, render_board(fetch_all()), "text/html; charset=utf-8")
            return

        # ── 程序下载 ────────────────────────────────────────────────────
        # /download          版本列表页（与看板同一套登录）
        # /download/<file>   实际文件流，支持 Range 续传
        # /api/releases      版本清单 JSON，供外部脚本取最新版
        if path in ("/download", "/download/", "/downloads"):
            if not self._authed():
                self._deny()
                return
            self._send(200, render_download(load_releases()), "text/html; charset=utf-8")
            return

        if path == "/api/releases":
            self._json(200, {"ok": True, "releases": load_releases()})
            return

        if path.startswith("/download/"):
            if not self._authed():
                self._deny()
                return
            fname = unquote(path[len("/download/"):])
            real, size = find_release_file(fname)
            if real is None:
                self._send(404, "安装包不存在", "text/plain; charset=utf-8")
                return
            self._send_file(real, size, fname)
            return

        self._json(404, {"ok": False, "error": "not found"})

    def _send_file(self, path: str, size: int, fname: str):
        """流式发文件；支持 Range（下载器 / 浏览器断点续传）。"""
        start, end = 0, size - 1
        code = 200
        rng = self.headers.get("Range", "")
        m = re.match(r"bytes=(\d*)-(\d*)$", rng.strip()) if rng else None
        if m:
            if m.group(1):
                start = int(m.group(1))
            if m.group(2):
                end = min(int(m.group(2)), size - 1)
            if start > end or start >= size:
                self.send_response(416)
                self.send_header("Content-Range", f"bytes */{size}")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            code = 206
        length = end - start + 1

        self.send_response(code)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(length))
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Disposition",
                         f'attachment; filename="{fname}"; filename*=UTF-8\'\'{fname}')
        if code == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()

        with open(path, "rb") as fh:
            fh.seek(start)
            left = length
            while left > 0:
                chunk = fh.read(min(262144, left))
                if not chunk:
                    break
                try:
                    self.wfile.write(chunk)
                except (BrokenPipeError, ConnectionResetError):
                    return
                left -= len(chunk)


def main():
    init_db()
    srv = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    auth = "开启" if CONSOLE_USER else "关闭"
    print(f"[M30Hub] 监听 0.0.0.0:{PORT}  DB={DB_PATH}  控制台鉴权={auth} "
          f"站点令牌={'开启' if STATION_TOKEN else '关闭'}  每站心跳保留={KEEP_HEARTBEATS}",
          flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
