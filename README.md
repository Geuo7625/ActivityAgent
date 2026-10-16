# Activity Agent

![托盘图标预览](app-icon-preview.png)

Windows 托盘小工具：实时推送当前前台应用到本机 WebSocket（`ws://127.0.0.1:12000/ws/`）。只监听 `127.0.0.1`，外网无法访问；只传输应用名等元数据，不发送窗口标题、路径和图标。

## 快速开始

---

## 0. 环境要求

- Windows 10 / 11
- .NET 8 SDK（编译用）或已编译好的 `ActivityAgent.exe`
- 首次运行需要**管理员权限**（因为 `HttpListener` 走 HTTP.sys）

---

## 1. 一次性配置 URL ACL

**以管理员身份**打开 PowerShell，执行一次：

```powershell
netsh http add urlacl url=http://127.0.0.1:12000/ user=Everyone
```

成功返回：`URL reservation successfully added`

> 之后可以**不用管理员**运行 exe。想撤销：
> ```powershell
> netsh http delete urlacl url=http://127.0.0.1:12000/
> ```

---

## 2. 编译并启动

直接运行build-installer.bat
运行编译后的exe

程序**没有主窗口**，启动后只出现在**任务栏右下角托盘**。右键托盘图标可：

- 开机自启动开关
- 编辑应用名称映射 / 排除列表
- 更换图标
- 查看日志
- 退出

---

## 3. 验证服务在跑

```powershell
netstat -ano | findstr :12000
```

**看到下面这行就对了**：

```
TCP    127.0.0.1:12000    0.0.0.0:0    LISTENING    <pid>
```

或者浏览器访问：

```
http://127.0.0.1:12000/ws/
```

返回 `400 Bad Request` 就说明服务活着（400 是正常的，因为这不是 WebSocket 升级请求）。

---

## 4. 浏览器验证（最快）

1. 地址栏输入 `about:blank` 回车
2. 按 `F12` 打开开发者工具 → **Console**
3. 粘贴并回车：

```js
const ws = new WebSocket("ws://127.0.0.1:12000/ws/");
ws.onopen    = () => { console.log("connected"); ws.send(JSON.stringify({type:"get_current"})); };
ws.onmessage = e  => console.log(JSON.parse(e.data));
ws.onclose   = () => console.log("closed");
```

控制台依次输出：

```
connected
{type: "started", protocolVersion: 1, server: "ActivityAgent", current: null}
{type: "current", current: {appId: "code", name: "Visual Studio Code", changedAt: "..."}}
```

之后**每次切前台应用**都会自动多打一条：

```
{type: "activity", current: {appId: "msedge", name: "Microsoft Edge", changedAt: "..."}}
```

主动断开：控制台输入 `ws.close()`。


## 本机 WebSocket 接口说明

### 概览

| 项目 | 值 |
| --- | --- |
| 传输协议 | WebSocket（RFC 6455） |
| 监听地址 | `127.0.0.1`（仅本机回环，外网无法访问） |
| 端口 | `12000` |
| 路径 | `/ws/` |
| 完整 URL | `ws://127.0.0.1:12000/ws/` |
| 鉴权 | 无（不需要 `Authorization` 头，也不需要 Token） |
| 编码 | UTF-8 JSON 文本帧 |

> 如果你之前用 `Authorization: Bearer <token>` 的客户端，现在可以直接去掉该请求头。

### 首次启动：URL ACL 权限

`HttpListener` 走 Windows HTTP.sys，即使绑定 `127.0.0.1`，非管理员运行也可能报 `Access is denied`。以**管理员**身份执行一次：

```cmd
netsh http add urlacl url=http://127.0.0.1:12000/ user=Everyone
```

或者直接以管理员身份运行 `ActivityAgent.exe`。删除规则：

```cmd
netsh http delete urlacl url=http://127.0.0.1:12000/
```

---

### 消息协议

所有消息都是 **单个 JSON 文本帧**（`WebSocketMessageType.Text`，`EndOfMessage = true`），字段名一律小驼峰。

### 客户端 → 服务端

| type | 说明 | 示例 |
| --- | --- | --- |
| `get_current` | 主动查询当前前台应用 | `{"type":"get_current"}` |
| `ping` | 心跳 | `{"type":"ping"}` |

其他 type 会收到 `error`；非 JSON 或超过 64 KB 会被拒绝或断开。

### 服务端 → 客户端

#### 1. `started` —— 连接建立后立即下发一次

```json
{
  "type": "started",
  "protocolVersion": 1,
  "server": "ActivityAgent",
  "current": {
    "appId": "code",
    "name": "Visual Studio Code",
    "changedAt": "2026-10-16T14:23:45.123+08:00"
  }
}
```

`current` 可能为 `null`（例如当前是排除项或桌面）。

#### 2. `activity` —— 前台应用发生变化时广播

```json
{
  "type": "activity",
  "current": {
    "appId": "minecraft",
    "name": "Minecraft",
    "changedAt": "2026-10-16T14:25:01.004+08:00"
  }
}
```

`appId` 与 `name` 任一变化即触发（进程名/映射规则决定），轮询周期 500 ms。

#### 3. `current` —— 对 `get_current` 的响应

```json
{ "type": "current", "current": { "appId": "msedge", "name": "Microsoft Edge", "changedAt": "..." } }
```

#### 4. `pong` —— 对 `ping` 的响应

```json
{ "type": "pong", "timestamp": "2026-10-16T14:26:10.500+08:00" }
```

#### 5. `error`

```json
{ "type": "error", "message": "Unknown message type. Supported: get_current, ping." }
```

### 字段说明

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `appId` | string | 小写稳定标识，来自名称映射；未命中时退化为进程名小写 |
| `name` | string | 展示用名称，来自映射表 / 文件描述 / 进程名 |
| `changedAt` | string | ISO 8601 带时区偏移 |

> 窗口标题、可执行文件路径、图标**不会**通过网络发送。

---

### 调用示例

### Python（`websockets`）

```bash
pip install websockets
```

```python
import asyncio
import json
import websockets

URI = "ws://127.0.0.1:12000/ws/"

async def main():
    async with websockets.connect(URI) as ws:
        # 主动查询一次
        await ws.send(json.dumps({"type": "get_current"}))

        async for raw in ws:
            msg = json.loads(raw)
            t = msg.get("type")

            if t in ("started", "activity", "current"):
                cur = msg.get("current")
                if cur:
                    print(f"[{cur['changedAt']}] {cur['name']}  ({cur['appId']})")
                else:
                    print("(无前台应用)")

            elif t == "pong":
                print("pong:", msg["timestamp"])

            elif t == "error":
                print("error:", msg["message"])

asyncio.run(main())
```

### JavaScript（浏览器控制台 / 网页）

```html
<script>
  const ws = new WebSocket("ws://127.0.0.1:12000/ws/");

  ws.onopen = () => {
    console.log("connected");
    ws.send(JSON.stringify({ type: "get_current" }));
  };

  ws.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    if ((msg.type === "activity" || msg.type === "current") && msg.current) {
      console.log("前台应用:", msg.current.name, msg.current.appId);
      document.title = `正在使用：${msg.current.name}`;
    }
  };

  ws.onclose = () => console.log("closed");
  ws.onerror = (e) => console.error("ws error", e);
</script>
```

> 注意：从 `file://` 打开的页面连 `ws://127.0.0.1` 一般没问题；从 `https://` 页面连 `ws://` 会被浏览器按混合内容拦截，需要改为本机可信来源或使用 `wss://`（本程序不支持）。

### Node.js（`ws`）

```bash
npm install ws
```

```js
import WebSocket from "ws";

const ws = new WebSocket("ws://127.0.0.1:12000/ws/");

ws.on("open", () => {
  console.log("connected");
  ws.send(JSON.stringify({ type: "ping" }));
  setInterval(() => ws.send(JSON.stringify({ type: "ping" })), 30000);
});

ws.on("message", (data) => {
  const msg = JSON.parse(data.toString());
  if (msg.current) {
    console.log(`[${msg.current.changedAt}] ${msg.current.name} (${msg.current.appId})`);
  } else {
    console.log(msg);
  }
});

ws.on("close", () => console.log("closed"));
```

### C#（`ClientWebSocket`）

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri("ws://127.0.0.1:12000/ws/"), CancellationToken.None);

// 发送一次查询
var query = Encoding.UTF8.GetBytes("""{"type":"get_current"}""");
await ws.SendAsync(query, WebSocketMessageType.Text, true, CancellationToken.None);

var buffer = new byte[8192];
while (ws.State == WebSocketState.Open)
{
    using var ms = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close)
            return;
        ms.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);

    using var doc = JsonDocument.Parse(ms.ToArray());
    var root = doc.RootElement;
    if (root.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.Object)
        Console.WriteLine($"{cur.GetProperty("name").GetString()} ({cur.GetProperty("appId").GetString()})");
}
```

### 命令行：`wscat`

```bash
npx wscat -c ws://127.0.0.1:12000/ws/
# 连上后直接输入：
{"type":"get_current"}
{"type":"ping"}
```

---

### 排错清单

| 现象 | 原因 / 处理 |
| --- | --- |
| 启动即弹「无法启动监听」 | 端口被占用，或缺少 URL ACL → 执行上面的 `netsh http add urlacl` 或改用管理员运行 |
| 客户端连不上 `ws://127.0.0.1:12000/ws/` | 路径必须精确为 `/ws/`（带结尾斜杠），少斜杠会返回 400 |
| 收到 `400 Bad Request` | 用了普通 HTTP 请求（不是 WS 升级请求），或路径不对 |
| 收到 `error: Unknown message type` | `type` 拼写错误，注意是下划线的 `get_current` |
| 连接一会儿自动断开 | 客户端发了二进制帧或超大消息（>64 KB），服务端会主动 close |