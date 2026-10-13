#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
M30 站点汇总服务（总控制台，第一期：只读看板）

设计原则
--------
1. **只读**：这里没有任何下发控制指令的接口。上位机永远是权威，控制台只负责"看"。
   后续若要加远程控制，必须走"指令下发 + 上位机本地安全校验 + 回执"的路径。
2. **零依赖**：只用 Python 标准库（http.server + sqlite3），服务器上不用装任何包。
3. **离线优先**：上位机连不上这里只是重试，绝不阻塞测试流程；这里收不到心跳就把站点标灰。

接口
----
    POST /api/report      上报状态（上位机调用），JSON，可选 X-Station-Token 校验
    GET  /api/stations    取全部站点最新状态（JSON，供页面自动刷新）
    GET  /                总控制台页面（服务端渲染，无 JS 也能看）

环境变量
--------
    M30_HUB_PORT      监听端口（默认 80）
    M30_HUB_DB        SQLite 路径（默认 /opt/m30hub/stations.db）
    M30_HUB_USER      控制台用户名（留空 = 不校验）
    M30_HUB_PASS      控制台密码（留空 = 不校验）
    M30_HUB_TOKEN     站点上报令牌（留空 = 不校验）
    M30_HUB_OFFLINE   多久没心跳算离线，秒（默认 30）
"""

import base64
import json
import os
import sqlite3
import threading
import time
from html import escape
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

PORT = int(os.environ.get("M30_HUB_PORT", "80"))
DB_PATH = os.environ.get("M30_HUB_DB", "/opt/m30hub/stations.db")
CONSOLE_USER = os.environ.get("M30_HUB_USER", "")
CONSOLE_PASS = os.environ.get("M30_HUB_PASS", "")
STATION_TOKEN = os.environ.get("M30_HUB_TOKEN", "")
OFFLINE_SECONDS = int(os.environ.get("M30_HUB_OFFLINE", "30"))

_lock = threading.Lock()

STATE_LABEL = {
    "running": ("运行中", "ok"),
    "paused": ("已暂停", "warn"),
    "idle": ("空闲", "idle"),
    "alarm": ("报警", "err"),
    "error": ("异常", "err"),
    "offline": ("离线", "off"),
}


def db():
    conn = sqlite3.connect(DB_PATH, timeout=10)
    conn.row_factory = sqlite3.Row
    return conn


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
        conn.commit()


def upsert(payload: dict) -> None:
    sid = str(payload.get("stationId") or "").strip()
    if not sid:
        raise ValueError("stationId 不能为空")
    now = time.time()
    slots = payload.get("slots") or {}
    with _lock, db() as conn:
        row = conn.execute("SELECT first_seen FROM station WHERE station_id=?", (sid,)).fetchone()
        first = row["first_seen"] if row else now
        conn.execute(
            """
            INSERT INTO station (station_id, name, version, state, plan, step, progress, alarms,
                                 slot_total, slot_ok, slot_warn, slot_err, extra, first_seen, last_seen)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(station_id) DO UPDATE SET
                name=excluded.name, version=excluded.version, state=excluded.state,
                plan=excluded.plan, step=excluded.step, progress=excluded.progress,
                alarms=excluded.alarms, slot_total=excluded.slot_total,
                slot_ok=excluded.slot_ok, slot_warn=excluded.slot_warn,
                slot_err=excluded.slot_err, extra=excluded.extra, last_seen=excluded.last_seen
            """,
            (
                sid,
                str(payload.get("name") or sid),
                str(payload.get("version") or ""),
                str(payload.get("state") or "idle"),
                str(payload.get("plan") or ""),
                str(payload.get("step") or ""),
                float(payload.get("progress") or 0.0),
                int(payload.get("alarms") or 0),
                int(slots.get("total") or 0),
                int(slots.get("ok") or 0),
                int(slots.get("warn") or 0),
                int(slots.get("err") or 0),
                json.dumps(payload.get("extra") or {}, ensure_ascii=False),
                first,
                now,
            ),
        )
        conn.commit()


def fetch_all():
    with lock_read() as conn:
        rows = [dict(r) for r in conn.execute("SELECT * FROM station ORDER BY station_id").fetchall()]
    now = time.time()
    for r in rows:
        r["online"] = (now - (r.get("last_seen") or 0)) <= OFFLINE_SECONDS
        r["ago"] = int(now - (r.get("last_seen") or now))
    return rows


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


PAGE = """<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>M30 总控制台</title>
<style>
 :root{{--bg:#1b222b;--card:#232c36;--line:#3d4a58;--fg:#e9eef5;--dim:#b7c3d1;
        --ok:#4ad786;--warn:#f3c24e;--err:#ff7b7f;--idle:#7f8c9b;--accent:#4ba6e8}}
 *{{box-sizing:border-box}}
 body{{margin:0;background:var(--bg);color:var(--fg);
      font:14px/1.5 "Microsoft YaHei",system-ui,sans-serif}}
 header{{display:flex;align-items:center;gap:12px;padding:14px 20px;
         background:#232c36;border-bottom:1px solid var(--line)}}
 header h1{{font-size:17px;margin:0;font-weight:600}}
 header .sp{{flex:1}}
 header .meta{{color:var(--dim);font-size:12.5px}}
 .grid{{display:grid;gap:14px;padding:18px 20px;
        grid-template-columns:repeat(auto-fill,minmax(330px,1fr))}}
 .card{{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px}}
 .card.is-online{{border-left:4px solid var(--ok)}}
 .card.is-offline{{border-left:4px solid var(--idle);opacity:.62}}
 .card.is-warn{{border-left:4px solid var(--warn)}}
 .card.is-err{{border-left:4px solid var(--err)}}
 .row1{{display:flex;align-items:center;gap:8px}}
 .name{{font-size:15.5px;font-weight:600}}
 .sid{{color:var(--dim);font-size:12px}}
 .pill{{margin-left:auto;font-size:12px;padding:2px 9px;border-radius:99px;
        border:1px solid currentColor}}
 .p-ok{{color:var(--ok)}} .p-warn{{color:var(--warn)}} .p-err{{color:var(--err)}}
 .p-idle{{color:var(--idle)}} .p-off{{color:var(--idle)}}
 .line{{margin-top:10px;display:grid;grid-template-columns:70px 1fr;gap:4px 8px;font-size:13px}}
 .line .k{{color:var(--dim)}}
 .bar{{height:7px;border-radius:4px;background:#2f3a46;overflow:hidden;margin-top:10px}}
 .bar > i{{display:block;height:100%;background:var(--accent)}}
 .chips{{margin-top:10px;display:flex;gap:8px;font-size:12.5px}}
 .chips span{{padding:1px 8px;border-radius:6px;background:#2c3744;color:var(--dim)}}
 .empty{{padding:60px 20px;text-align:center;color:var(--dim)}}
 footer{{padding:10px 20px;color:var(--dim);font-size:12px}}
</style></head><body>
<header>
  <h1>M30 总控制台</h1>
  <span class="meta">{count} 台设备 · 在线 {online} 台</span>
  <span class="sp"></span>
  <span class="meta">刷新于 {now}</span>
</header>
{body}
<footer>只读看板（第一期）。上位机为权威，控制台只收心跳与状态，不下发任何控制指令。</footer>
<script>
 // 有 JS 时每 3 秒拉一次；拉不到就保持服务端渲染的这份快照
 setTimeout(function(){{ location.reload(); }}, 3000);
</script>
</body></html>
"""


def render(rows) -> str:
    online = sum(1 for r in rows if r["online"])
    if not rows:
        body = '<div class="empty">还没有设备上报。<br>在上位机「配置 → 站点标识」里填好站点 ID 与控制台地址后即可出现。</div>'
    else:
        cards = []
        for r in rows:
            state = r["state"] if r["online"] else "offline"
            label, kind = STATE_LABEL.get(state, (state or "未知", "idle"))
            if r["online"] and (r["slot_err"] or r["alarms"]):
                kind = "err"
            cls = {"ok": "is-online", "warn": "is-warn", "err": "is-err"}.get(kind, "is-offline")
            pct = max(0.0, min(1.0, float(r["progress"] or 0))) * 100
            extra = {}
            try:
                extra = json.loads(r["extra"] or "{}")
            except Exception:
                pass
            cards.append(f"""
 <div class="card {cls}">
   <div class="row1">
     <span class="name">{escape(str(r['name'] or r['station_id']))}</span>
     <span class="sid">{escape(str(r['station_id']))}</span>
     <span class="pill p-{kind}">{label}</span>
   </div>
   <div class="line">
     <span class="k">测试方案</span><span>{escape(str(r['plan'] or '-'))}</span>
     <span class="k">当前工步</span><span>{escape(str(r['step'] or '-'))}</span>
     <span class="k">程序版本</span><span>{escape(str(r['version'] or '-'))}</span>
     <span class="k">最后心跳</span><span>{r['ago']} 秒前</span>
     <span class="k">备注</span><span>{escape(str(extra.get('note') or '-'))}</span>
   </div>
   <div class="bar"><i style="width:{pct:.1f}%"></i></div>
   <div class="chips">
     <span>工位 {r['slot_total'] or 0}</span>
     <span>合格 {r['slot_ok'] or 0}</span>
     <span>警告 {r['slot_warn'] or 0}</span>
     <span>异常 {r['slot_err'] or 0}</span>
     <span>报警 {r['alarms'] or 0}</span>
   </div>
 </div>""")
        body = '<div class="grid">' + "".join(cards) + "</div>"
    return PAGE.format(count=len(rows), online=online,
                       now=time.strftime("%Y-%m-%d %H:%M:%S"), body=body)


class Handler(BaseHTTPRequestHandler):
    server_version = "M30Hub/1.0"

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
        path = urlparse(self.path).path
        if path == "/api/stations":
            if not self._authed():
                self._deny()
                return
            self._json(200, {"ok": True, "stations": fetch_all(), "now": time.time()})
            return
        if path in ("/", "/index.html"):
            if not self._authed():
                self._deny()
                return
            self._send(200, render(fetch_all()), "text/html; charset=utf-8")
            return
        if path == "/health":
            self._json(200, {"ok": True})
            return
        self._json(404, {"ok": False, "error": "not found"})


def main():
    init_db()
    srv = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    auth = "开启" if CONSOLE_USER else "关闭"
    print(f"[M30Hub] 监听 0.0.0.0:{PORT}  DB={DB_PATH}  控制台鉴权={auth} "
          f"站点令牌={'开启' if STATION_TOKEN else '关闭'}", flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
