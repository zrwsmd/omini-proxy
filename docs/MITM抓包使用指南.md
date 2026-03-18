# WowProxy MITM 抓包使用指南

## 功能概述

WowProxy 内置了 MITM (中间人) 抓包功能，可以拦截并解密 HTTPS 流量，让你以**明文**查看请求和响应内容。这对于抓取 AI IDE（Cursor、Windsurf、Copilot 等）发送给大模型的系统提示词和消息内容非常有用。

**核心特点：**
- 默认**捕获所有流量**，无需提前知道目标域名
- 类似 Wireshark 的**显示过滤器**，支持多字段、多条件、正则表达式
- **独立工作**，不依赖 sing-box 代理连接（也可配合使用）
- 自动解压 gzip / brotli / deflate，JSON 自动格式化

---

## 快速开始（3 步）

### 第 1 步：安装 CA 证书（首次使用，仅需一次）

1. 打开 WowProxy，切换到 **「抓包分析」** 标签页
2. 点击 **「安装 CA 证书」** 按钮
3. Windows 会弹出安全警告，点击 **「是」** 确认安装
4. 状态显示 **「✓ CA 已安装」** 即表示成功

> **为什么需要 CA 证书？**
> HTTPS 流量是加密的。MITM 代理需要用自己的 CA 证书为每个域名动态签发证书，
> 才能解密流量。安装 CA 后，你的浏览器/IDE 才会信任这些动态证书。

### 第 2 步：启动抓包

1. 确认端口号（默认 `10811`，可修改）
2. 点击 **「▶ 开始抓包」**
3. 状态栏显示 `抓包运行中 — 代理地址 127.0.0.1:10811`

### 第 3 步：配置 IDE / 浏览器代理

将目标应用的 HTTP 代理指向 MITM 代理地址：

```
HTTP 代理: 127.0.0.1
端口: 10811
```

#### 各 IDE 配置方式

**Cursor:**
- 设置 → Search "proxy" → HTTP Proxy 填入 `http://127.0.0.1:10811`

**Windsurf:**
- 设置 → Search "proxy" → HTTP Proxy 填入 `http://127.0.0.1:10811`

**VS Code / GitHub Copilot:**
- 设置 → Search "proxy" → Http: Proxy 填入 `http://127.0.0.1:10811`

**系统全局代理（抓取所有应用）：**
- Windows 设置 → 网络 → 代理 → 手动设置代理 → 地址 `127.0.0.1`，端口 `10811`

**命令行临时设置：**
```powershell
$env:HTTP_PROXY = "http://127.0.0.1:10811"
$env:HTTPS_PROXY = "http://127.0.0.1:10811"
```

配置完成后，在 IDE 中正常使用 AI 功能（发送消息、补全代码等），
抓包列表会**实时显示**所有经过的 HTTP/HTTPS 请求。

---

## 显示过滤器

抓包默认捕获**所有流量**。使用显示过滤器可以筛选你关心的请求。

### 过滤字段说明

| 字段名 | 说明 | 示例值 |
|--------|------|--------|
| `all` | 搜索所有字段（URL、Host、Body 等） | `openai` |
| `host` / `sni` | 域名（SNI） | `api.openai.com` |
| `url` | 完整 URL | `/v1/chat/completions` |
| `path` | 请求路径 | `/v1/chat` |
| `method` | HTTP 方法 | `POST` |
| `status` | 响应状态码 | `200` |
| `protocol` | 协议 | `HTTPS` |
| `ip` | 目的 IP 地址 | `104.18.` |
| `port` | 目的端口 | `443` |
| `req.content-type` | 请求 Content-Type | `application/json` |
| `resp.content-type` | 响应 Content-Type | `text/event-stream` |
| `req.body` | 请求体内容 | `system` |
| `resp.body` | 响应体内容 | `assistant` |
| `req.header` | 请求头（全部） | `Authorization` |
| `resp.header` | 响应头（全部） | `x-request-id` |
| `error` | 错误信息 | `timeout` |
| `duration` | 响应耗时（毫秒） | `1000` |

### 条件运算符

| 运算符 | 说明 |
|--------|------|
| `contains` | 包含（默认） |
| `not contains` | 不包含 |
| `equals` | 完全匹配 |
| `not equals` | 不等于 |
| `starts with` | 以…开头 |
| `ends with` | 以…结尾 |
| `regex` | 正则表达式匹配 |
| `>` | 大于（数值比较） |
| `<` | 小于（数值比较） |

### 过滤示例

**抓取所有 AI API 请求：**
- 字段: `url`，条件: `contains`，值: `chat/completions`

**只看 POST 请求：**
- 字段: `method`，条件: `equals`，值: `POST`

**查找包含系统提示词的请求：**
- 字段: `req.body`，条件: `contains`，值: `system`

**用正则匹配多个域名：**
- 字段: `host`，条件: `regex`，值: `openai|anthropic|cursor`
- 或勾选 **「正则」** 复选框

**查找慢请求（>2秒）：**
- 字段: `duration`，条件: `>`，值: `2000`

**查找错误请求：**
- 字段: `status`，条件: `>`，值: `399`

---

## 查看请求详情

点击列表中任意一条请求，右侧面板显示：

| 标签页 | 内容 |
|--------|------|
| **Request Headers** | 请求方法、URL、Host、IP、所有请求头 |
| **Request Body** | 请求体明文（JSON 自动格式化缩进） |
| **Response Headers** | 状态码、所有响应头 |
| **Response Body** | 响应体明文（JSON 自动格式化缩进） |

**复制内容：** 在 Request Body / Response Body 页面点击 **「复制」** 按钮即可复制到剪贴板。

---

## 配合 sing-box 代理使用

如果你同时使用 WowProxy 的代理功能（翻墙），抓包代理可以将流量转发给 sing-box：

```
IDE → MITM 代理 (10811) → sing-box 代理 (10808) → 目标服务器
```

操作步骤：
1. 先启动 sing-box 代理（主页面点连接）
2. 再启动抓包（抓包分析标签页）
3. IDE 代理设置为 `127.0.0.1:10811`

如果不需要翻墙，抓包代理会直连目标服务器：
```
IDE → MITM 代理 (10811) → 目标服务器
```

---

## 常见问题

### Q: 启动后列表没有数据？
- **确认 IDE/浏览器的代理已设置为 `127.0.0.1:10811`**
- 确认 CA 证书已安装（状态显示 "✓ CA 已安装"）
- 在 IDE 中发送一条消息触发请求

### Q: IDE 报 SSL 证书错误？
- CA 证书未安装，点击「安装 CA 证书」
- 部分应用需要重启后才能识别新安装的 CA
- 某些应用（如 Node.js）需要设置环境变量信任 CA：
  ```powershell
  $env:NODE_TLS_REJECT_UNAUTHORIZED = "0"
  ```

### Q: 抓到了很多无关请求？
- 使用显示过滤器：字段 `host`，条件 `regex`，值 `openai|anthropic|cursor|copilot`
- 或者字段 `req.body`，条件 `contains`，值 `messages` 只看聊天请求

### Q: 如何找到系统提示词？
1. 过滤: `req.body` contains `system`
2. 点击匹配的请求
3. 切换到 **Request Body** 标签页
4. 在 JSON 中找 `"role": "system"` 对应的 `"content"` 字段

### Q: 端口被占用？
- 修改端口号为其他值（如 `10812`），然后 IDE 代理也改成对应端口

### Q: 如何卸载 CA 证书？
1. 点击「证书目录」打开 CA 文件夹
2. Windows: 运行 `certmgr.msc` → 受信任的根证书颁发机构 → 证书
3. 找到 `WowProxy MITM CA`，右键删除
