# M30 总控制台

把多台测试上位机的运行状态汇总到一个页面看，并支持**点开看详情**和**导出数据**。

**只读** —— 这里没有任何下发控制指令的接口，上位机永远是权威；连不上只是重试，绝不阻塞测试。
控制台只做两件事：**看**（状态 / 测试明细）和**取**（导出 CSV / JSON）。

## 组成

| 文件 | 作用 |
|---|---|
| `station_hub.py` | 汇总服务：收心跳、存 SQLite、渲染看板与详情页、导出数据。**只用 Python 标准库**，服务器上不用装任何包 |
| `m30-hub.service` | systemd 单元模板（`__PASSWORD__` / `__TOKEN__` 由部署脚本替换） |
| `deploy.sh` | 一键部署 / 更新 / 查看状态 |

## 界面

- **看板 `/`**：每台设备一张卡片 —— 名称 / 站点 ID / 状态、方案、传感器型号、当前工步、版本、
  最后心跳、备注、进度条、工位统计（合格 / 警告 / 异常 / 报警），卡片底部三个按钮：
  **查看详情 / 工位明细 CSV / 心跳历史 CSV**。有 JS 时每 5 秒自动刷新。
- **详情 `/station?id=站点ID`**：基本信息、测试信息（方案 / 型号 / 批次 / 温度点 / 压力点 /
  开始时间 / 已运行 / 进度）、**工位明细表**（工位 / 序列号 / 判定 / 备注）、**指标判定表**、
  **心跳历史表**（时间 / 状态 / 工步 / 进度 / 温度点 / 压力点 / 合格·警告·异常 / 报警）。
  顶部和表格标题旁都有导出按钮。页面每 5 秒**局部刷新**（只换内容区，不整页 reload，滚动位置不丢）。
- **主题**：默认**浅色**，右上角按钮可切深色，选择记在 localStorage。
- **下载 `/download`**：程序安装包下载页。按版本号**倒序**列出所有版本，最新一版带「最新」标记置顶，
  每条显示 **版本号 / 发布日期 / 包大小**，底部一行「下载」按钮。
  支持在清单里给每个版本写 `notes`（更新说明）和 `sha256`（校验值），有就显示。
  控制台看板右上角有「程序下载」入口。

## 接口

```
POST /api/report                     上报状态（上位机调用）
GET  /api/stations                   全部站点最新状态（JSON）
GET  /api/station?id=ID              单台详情（JSON，含工位明细与最近 300 条心跳）
GET  /api/station/ID/slots.csv       导出工位明细 CSV
GET  /api/station/ID/history.csv     导出心跳历史 CSV
GET  /api/station/ID/snapshot.json   导出完整快照 JSON（留档）
GET  /                               总控制台看板
GET  /station?id=ID                  单台详情页（&frag=1 只返回内容片段，供局部刷新）
GET  /download                       程序下载页（版本列表）
GET  /download/<文件名>              下载安装包（支持 Range 断点续传）
GET  /api/releases                   版本清单 JSON（脚本取最新版用）
GET  /health                         存活探针
```

CSV 带 UTF-8 BOM，Excel 双击直接正确显示中文。

## 程序下载页

`/download` 的数据来自服务器上的 **`/opt/m30hub/releases.json`**，安装包放在 **`/opt/m30hub/releases/`**。

```json
[
  {
    "version": "1.2.42",
    "date": "2026-10-15",
    "size": 67188046,
    "file": "M30TestApp.V2-v1.2.42-win-x64-self-contained.zip",
    "sha256": "…",
    "notes": "修复顶部方案页签高亮互斥",
    "latest": true
  }
]
```

- 页面按 `version` **倒序**排，第一条自动标「最新」（`latest` 字段会被服务端重算，不用手填）。
- `file` 为空或文件不在 `releases/` 里时，该条只显示信息、不给下载按钮。
- `notes` 支持换行，会原样显示在版本号下方。`sha256` 留空则不显示校验行。
- **不需要 releases.json 也能用**：服务端启动时会顺带扫一遍 `releases/` 目录，
  从文件名里认出 `-v<版本号>-` 的 zip 自动补进列表（日期取文件修改时间、无说明）。
  想手工维护说明文字就写 json，两者会合并（json 里已有的文件不重复补）。

`deploy.sh` 每次部署会自动干这三件事：

1. 扫本地 `artifacts/release/` 里的所有 zip → 算 SHA256 → 生成 `artifacts/releases.json`
2. 把 `releases.json` 传到服务器
3. 逐个上传 zip 到 `/opt/m30hub/releases/`（服务器上已有同尺寸的会跳过，不重复传 67MB）

**手动加历史版本**：把 zip 丢进 `artifacts/release/` 再跑一次 `bash server/deploy.sh` 即可；
或者直接 scp 到服务器 `/opt/m30hub/releases/`，重启服务后自动被扫出来。

**下载是流式的**，支持 `Range`，浏览器/下载器断点续传没问题；文件名做了目录穿越防护，
只允许访问 `releases/` 目录下的文件。

### 上报体

```json
{
  "stationId": "M30-01",
  "name": "1号台(自动测试)",
  "version": "V1.2.42",
  "state": "running",
  "plan": "M30测试",
  "model": "m10-10k",
  "batch": "260826_01",
  "step": "温度点 T1 25℃ / 压力点 P2 50kPa 稳压中",
  "startedAt": "2026-10-13 12:30:00",
  "elapsed": "00:47:12",
  "tempPoint": "T1 25℃",
  "pressurePoint": "P2 50kPa",
  "progress": 0.42,
  "alarms": 0,
  "slots": { "total": 60, "ok": 52, "warn": 5, "err": 3 },
  "slotDetails": [
    { "slot": "Slot1", "serial": "260826_01#1", "state": "ok", "note": "" }
  ],
  "metrics": [
    { "name": "Offset", "value": "0.012", "limit": "±0.05", "result": "合格" }
  ],
  "extra": { "note": "自动测试", "machine": "PC-M30-01" }
}
```

`state` 取值：`running` / `paused` / `idle` / `alarm` / `error`。
工位判定 `state` 取值：`ok` / `warn` / `err` / `idle`（未测）。
超过 `M30_HUB_OFFLINE` 秒没心跳，看板自动标灰为「离线」。

**带宽**：`slotDetails` / `metrics` 是**变化时才带**的 —— 上位机算一份指纹，没变就不发（发 `null`），
服务端收到 `null` 就沿用库里上一份。256 工位的全量明细不会每 5 秒重发一遍。

**老版本上位机兼容**：老格式（没有 model / slotDetails 等字段）照样能上报，缺的字段留空，
详情页对应位置显示 `-` 或「还没有工位明细上报」。

## 数据库

`stations.db` 两张表：

- `station`：每站一行最新状态（含 `slot_detail` / `metrics` 的 JSON 列）
- `heartbeat`：心跳历史。**不是每 5 秒一条** —— 只在「内容变了」或「距上一条满 60 秒」时写一条，
  每站最多保留 `M30_HUB_KEEP` 条（默认 5000）。

老库升级不用手工迁移：启动时自动 `ALTER TABLE` 补上新列。

## 环境变量

```
M30_HUB_PORT      监听端口（默认 80）
M30_HUB_DB        SQLite 路径（默认 /opt/m30hub/stations.db）
M30_HUB_USER      控制台用户名（留空 = 不校验）
M30_HUB_PASS      控制台密码（留空 = 不校验）
M30_HUB_TOKEN     站点上报令牌（留空 = 不校验）
M30_HUB_OFFLINE   多久没心跳算离线，秒（默认 30）
M30_HUB_KEEP      每站保留多少条心跳历史（默认 5000）
M30_HUB_RELEASES  版本清单 JSON 路径（默认 /opt/m30hub/releases.json）
M30_HUB_RELEASE_DIR  安装包目录（默认 /opt/m30hub/releases）
M30_HUB_VERSION   内置兜底版本号（清单和目录都为空时显示用）
```

## 部署

```bash
# 本机（Git Bash）：把服务器 SSH 密码写进 /tmp/askpass.sh
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

浏览器首次访问会弹一次登录框（用户名 `admin`，密码 `123..00`；部署配置记录在 `artifacts/m30_secrets.txt`）。
上位机上报走 `X-Station-Token` 头，令牌仍保存在本地 `Setting.ini`，和 `M30_HUB_TOKEN` 对应。

## 后续

远程控制必须走「指令下发 → 上位机本地安全校验（仪器就绪 / 无冲突测试 / 气路安全）→ 回执」，
不允许服务器直接让设备动起来。报表归档、历史数据长期留存再往后排。
