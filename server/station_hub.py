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
import json
import os
import re
import sqlite3
import threading
import time
from html import escape
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, unquote, urlparse

PORT = int(os.environ.get("M30_HUB_PORT", "80"))
DB_PATH = os.environ.get("M30_HUB_DB", "/opt/m30hub/stations.db")
CONSOLE_USER = os.environ.get("M30_HUB_USER", "")
CONSOLE_PASS = os.environ.get("M30_HUB_PASS", "")
STATION_TOKEN = os.environ.get("M30_HUB_TOKEN", "")
OFFLINE_SECONDS = int(os.environ.get("M30_HUB_OFFLINE", "30"))
KEEP_HEARTBEATS = int(os.environ.get("M30_HUB_KEEP", "5000"))

_lock = threading.Lock()

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
  --bg:#f2f5f9; --card:#ffffff; --line:#dce3ec; --fg:#1b2431; --dim:#64748b;
  --ok:#12874a; --warn:#a9701a; --err:#c0342f; --idle:#7b8798; --accent:#1a72c4;
  --ok-bg:#e6f6ec; --warn-bg:#fdf3e0; --err-bg:#fdeceb; --idle-bg:#eef1f5;
  --head:#ffffff; --soft:#f7f9fc;
}
html[data-theme="dark"]{
  --bg:#1b222b; --card:#232c36; --line:#3d4a58; --fg:#e9eef5; --dim:#b7c3d1;
  --ok:#4ad786; --warn:#f3c24e; --err:#ff7b7f; --idle:#7f8c9b; --accent:#4ba6e8;
  --ok-bg:#1d3328; --warn-bg:#3a3122; --err-bg:#3b2426; --idle-bg:#2a333e;
  --head:#232c36; --soft:#202832;
}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--fg);
     font:14px/1.5 "Microsoft YaHei",system-ui,-apple-system,sans-serif}
a{color:var(--accent);text-decoration:none}
header{display:flex;align-items:center;gap:12px;padding:13px 20px;
       background:var(--head);border-bottom:1px solid var(--line);
       position:sticky;top:0;z-index:5;flex-wrap:wrap}
header h1{font-size:17px;margin:0;font-weight:600}
header .meta{color:var(--dim);font-size:12.5px}
header .sp{flex:1}
.ghost{border:1px solid var(--line);background:transparent;color:var(--fg);
       border-radius:6px;padding:5px 12px;font-size:12.5px;cursor:pointer;font-family:inherit}
.ghost:hover{border-color:var(--accent);color:var(--accent)}
.grid{display:grid;gap:14px;padding:18px 20px;
      grid-template-columns:repeat(auto-fill,minmax(340px,1fr))}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px}
.grid .card{border-left:4px solid var(--idle)}
.grid .card.is-online{border-left-color:var(--ok)}
.grid .card.is-warn{border-left-color:var(--warn)}
.grid .card.is-err{border-left-color:var(--err)}
.grid .card.is-offline{opacity:.6}
.row1{display:flex;align-items:center;gap:8px}
.name{font-size:15.5px;font-weight:600}
.sid{color:var(--dim);font-size:12px}
.pill{margin-left:auto;font-size:12px;padding:2px 9px;border-radius:99px;
      border:1px solid currentColor;white-space:nowrap}
.p-ok{color:var(--ok)} .p-warn{color:var(--warn)} .p-err{color:var(--err)}
.p-idle{color:var(--idle)} .p-off{color:var(--idle)}
.line{margin-top:10px;display:grid;grid-template-columns:72px 1fr;gap:4px 8px;font-size:13px}
.line .k{color:var(--dim)}
.bar{height:7px;border-radius:4px;background:var(--idle-bg);overflow:hidden;margin-top:10px}
.bar > i{display:block;height:100%;background:var(--accent)}
.chips{margin-top:10px;display:flex;gap:8px;font-size:12.5px;flex-wrap:wrap}
.chips span{padding:1px 8px;border-radius:6px;background:var(--idle-bg);color:var(--dim)}
.chips span.b-ok{background:var(--ok-bg);color:var(--ok)}
.chips span.b-warn{background:var(--warn-bg);color:var(--warn)}
.chips span.b-err{background:var(--err-bg);color:var(--err)}
.acts{margin-top:12px;display:flex;gap:8px}
.btn{display:inline-block;border:1px solid var(--line);border-radius:6px;padding:4px 12px;
     font-size:12.5px;color:var(--fg);background:transparent;cursor:pointer;font-family:inherit}
.btn:hover{border-color:var(--accent);color:var(--accent)}
.btn.primary{background:var(--accent);border-color:var(--accent);color:#fff}
.btn.primary:hover{color:#fff;opacity:.9}
.empty{padding:60px 20px;text-align:center;color:var(--dim)}
footer{padding:10px 20px;color:var(--dim);font-size:12px}
.wrap{padding:16px 20px;display:flex;flex-direction:column;gap:14px}
.cols{display:grid;gap:14px;grid-template-columns:repeat(auto-fit,minmax(330px,1fr))}
.card h2{font-size:14px;margin:0 0 10px;font-weight:600;display:flex;align-items:center;gap:10px}
.card h2 .dim{color:var(--dim);font-weight:400;font-size:12.5px}
.card h2 .sp{flex:1}
.kv{display:grid;grid-template-columns:88px 1fr;gap:6px 10px;font-size:13px}
.kv .k{color:var(--dim)}
table{width:100%;border-collapse:collapse;font-size:12.5px}
th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);white-space:nowrap}
th{color:var(--dim);font-weight:600;background:var(--soft);position:sticky;top:0}
tbody tr:hover{background:var(--soft)}
.tag{display:inline-block;padding:1px 8px;border-radius:99px;font-size:12px;
     border:1px solid currentColor}
.t-ok{color:var(--ok)} .t-warn{color:var(--warn)} .t-err{color:var(--err)} .t-idle{color:var(--idle)}
.scroll{max-height:420px;overflow:auto;border:1px solid var(--line);border-radius:8px}
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


# ── 看板 ────────────────────────────────────────────────────────────────

def _card(r):
    state = r["state"] if r["online"] else "offline"
    label, kind = STATE_LABEL.get(state, (state or "未知", "idle"))
    if r["online"] and (r["slot_err"] or r["alarms"]):
        kind = "err"
    cls = {"ok": "is-online", "warn": "is-warn", "err": "is-err"}.get(kind, "is-offline")
    pct = max(0.0, min(1.0, float(r["progress"] or 0))) * 100
    note = r["extra_obj"].get("note") or "-"
    sid = str(r["station_id"])
    q = escape(sid, quote=True)

    return f"""
 <div class="card {cls}">
   <div class="row1">
     <span class="name">{escape(str(r['name'] or sid))}</span>
     <span class="sid">{escape(sid)}</span>
     <span class="pill p-{kind}">{label}</span>
   </div>
   <div class="line">
     <span class="k">测试方案</span><span>{escape(str(r['plan'] or '-'))}</span>
     <span class="k">传感器</span><span>{escape(str(r.get('model') or '-'))}</span>
     <span class="k">当前工步</span><span>{escape(str(r['step'] or '-'))}</span>
     <span class="k">程序版本</span><span>{escape(str(r['version'] or '-'))}</span>
     <span class="k">最后心跳</span><span>{r['ago']} 秒前</span>
     <span class="k">备注</span><span>{escape(str(note))}</span>
   </div>
   <div class="bar"><i style="width:{pct:.1f}%"></i></div>
   <div class="chips">
     <span>工位 {r['slot_total'] or 0}</span>
     <span class="b-ok">合格 {r['slot_ok'] or 0}</span>
     <span class="b-warn">警告 {r['slot_warn'] or 0}</span>
     <span class="b-err">异常 {r['slot_err'] or 0}</span>
     <span>报警 {r['alarms'] or 0}</span>
   </div>
   <div class="acts">
     <a class="btn primary" href="/station?id={q}">查看详情</a>
     <a class="btn" href="/api/station/{q}/slots.csv">工位明细 CSV</a>
     <a class="btn" href="/api/station/{q}/history.csv">心跳历史 CSV</a>
   </div>
 </div>"""


def render_board(rows) -> str:
    online = sum(1 for r in rows if r["online"])
    if not rows:
        body = ('<div class="empty">还没有设备上报。<br>'
                '在上位机「配置 → 系统设置 → 上报设备 ID」里填好站点 ID 与控制台地址后即可出现。</div>')
    else:
        body = '<div class="grid">' + "".join(_card(r) for r in rows) + "</div>"

    header = (
        '<header><h1>M30 总控制台</h1>'
        f'<span class="meta">{len(rows)} 台设备 · 在线 {online} 台</span>'
        '<span class="sp"></span>'
        '<a class="ghost" href="/download">程序下载</a>'
        + theme_button() +
        f'<span class="meta">刷新于 {time.strftime("%Y-%m-%d %H:%M:%S")}</span></header>'
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
        '<header><h1>程序下载</h1>'
        f'<span class="meta">{len(releases)} 个版本</span>'
        + (f'<span class="meta">当前最新 v{escape(latest["version"])}</span>' if latest else "")
        + '<span class="sp"></span>'
        + '<a class="ghost" href="/">返回控制台</a>'
        + theme_button()
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

    header = (
        '<header>'
        '<a class="btn" href="/">← 返回看板</a>'
        f'<h1>{escape(str(r["name"] or sid))}</h1>'
        f'<span class="sid mono">{escape(sid)}</span>'
        f'<span class="pill p-{kind}">{label}</span>'
        '<span class="sp"></span>'
        f'<a class="btn" href="/api/station/{q}/slots.csv">工位明细 CSV</a>'
        f'<a class="btn" href="/api/station/{q}/history.csv">心跳历史 CSV</a>'
        f'<a class="btn" href="/api/station/{q}/snapshot.json">快照 JSON</a>'
        + theme_button() +
        '</header>'
    )

    body = f'<div class="wrap" id="live">{frag}</div>'
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

    def _authed(self) -> bool:
        if not CONSOLE_USER:
            return True
        head = self.headers.get("Authorization", "")
        if not head.startswith("Basic "):
            return False
        try:
            raw = base64.b64decode(head[6:]).decode("utf-8")
        except Exception:
            return False
        return raw == f"{CONSOLE_USER}:{CONSOLE_PASS}"

    def _deny(self):
        self._send(401, "需要登录", "text/plain; charset=utf-8",
                   {"WWW-Authenticate": 'Basic realm="M30 Hub"'})

    # ── 路由 ────────────────────────────────────────────────────────────
    def do_POST(self):
        path = urlparse(self.path).path
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

    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path
        query = parse_qs(parsed.query)

        if path == "/health":
            self._json(200, {"ok": True})
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
