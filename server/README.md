# M30 总控制台（第一期：只读看板）

把多台测试上位机的运行状态汇总到一个页面看。**只读** —— 这里没有任何下发控制指令的接口，
上位机永远是权威；连不上只是重试，绝不阻塞测试。

## 组成

| 文件 | 作用 |
|---|---|
| `station_hub.py` | 汇总服务：收心跳、存 SQLite、渲染看板页面。**只用 Python 标准库**，服务器上不用装任何包 |
| `m30-hub.service` | systemd 单元模板（`__PASSWORD__` / `__TOKEN__` 由部署脚本替换） |
| `deploy.sh` | 一键部署 / 更新 / 查看状态 |

## 接口

```
POST /api/report     上报状态（上位机调用）
GET  /api/stations   全部站点最新状态（JSON）
GET  /               总控制台页面（服务端渲染，无 JS 也能看）
GET  /health         存活探针
```

上报体（上位机 `StationReporter` 发的就是这个格式）：

```json
{
  "stationId": "M30-01",
  "name": "1号台(自动测试)",
  "version": "1.2.42",
  "state": "running",
  "plan": "m10-10k",
  "step": "T1 25℃ 保温中 12/120min",
  "progress": 0.18,
  "alarms": 0,
  "slots": { "total": 60, "ok": 18, "warn": 1, "err": 0 },
  "extra": { "note": "车间东侧" }
}
```

`state` 取值：`running` / `paused` / `idle` / `alarm` / `error`。
超过 `M30_HUB_OFFLINE` 秒没心跳，看板自动标灰为「离线」。

## 部署

```bash
# 本机（Git Bash）：把服务器密码写进 /tmp/askpass.sh
echo "echo '你的服务器密码'" > /tmp/askpass.sh && chmod +x /tmp/askpass.sh

bash server/deploy.sh            # 部署/更新
bash server/deploy.sh --status   # 看状态
```

脚本会：把 `station_hub.py` 传到 `/opt/m30hub/`、渲染并上传 systemd 单元、
`enable --now` 服务。凭据存在 `artifacts/m30_secrets.txt`（已 gitignore，不入库）。

## ⚠️ AWS 安全组

服务监听 **80** 端口，但 AWS 安全组默认只开 22。**必须在控制台放行 80**，
否则外网打不开：EC2 → 安全组 → 入站规则 → 添加 `HTTP / TCP / 80 / 0.0.0.0/0`。

## 控制台鉴权

浏览器首次访问会弹一次登录框（用户名 `m30`，密码见 `artifacts/m30_secrets.txt`）。
上位机上报走 `X-Station-Token` 头，和 `M30_HUB_TOKEN` 对应。

## 后续（不在第一期）

远程控制必须走「指令下发 → 上位机本地安全校验（仪器就绪 / 无冲突测试 / 气路安全）→ 回执」，
不允许服务器直接让设备动起来。历史数据、报表归档再往后排。
