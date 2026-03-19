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

## 搜索过滤（mitmproxy 风格）

抓包默认捕获**所有流量**。搜索栏支持 mitmproxy 风格的前缀命令来筛选。

### 基本用法

**直接输入文字** = 模糊匹配所有字段（URL、Host、Body 等），不区分大小写：
```
openai          → 匹配任何包含 "openai" 的请求
chat/completions → 匹配 URL 中包含该路径的请求
```

### 前缀命令

使用 `~前缀 关键词` 格式搜索指定字段：

| 前缀 | 说明 | 示例 |
|------|------|------|
| `~d` | 域名 / Host / SNI | `~d openai.com` |
| `~u` | URL | `~u /v1/chat/completions` |
| `~path` | 请求路径 | `~path /api/` |
| `~m` | HTTP 方法 | `~m POST` |
| `~c` / `~s` | 状态码 | `~c 200` |
| `~b` | 请求体 + 响应体 | `~b system` |
| `~bq` | 仅请求体（Request Body） | `~bq system_prompt` |
| `~bs` | 仅响应体（Response Body） | `~bs assistant` |
| `~h` | 所有 Header | `~h Authorization` |
| `~hq` | 请求 Header | `~hq Bearer` |
| `~hs` | 响应 Header | `~hs x-request-id` |
| `~dst` | 目的 IP 地址 | `~dst 104.18` |
| `~t` | Content-Type | `~t application/json` |
| `~e` | 错误（无值时显示所有有错误的请求） | `~e` 或 `~e timeout` |
| `~all` | 搜索所有字段 | `~all openai` |

### 正则表达式支持

搜索值中包含正则特殊字符（`|` `(` `)` `[` `]` `.` `+` `?` 等）时**自动识别为正则**：
```
~d openai|anthropic|cursor     → 匹配这三个域名
~bq system.*prompt             → 请求体中正则匹配
~u /v[12]/chat                 → URL 匹配 v1 或 v2
openai|windsurf                → 全局模糊正则匹配
```

普通字符串则按模糊包含匹配：
```
~d openai       → host 包含 "openai"（模糊匹配，不需要写全）
~bq system      → 请求体包含 "system"
```

### 搜索示例

| 目标 | 搜索输入 |
|------|----------|
| 所有 AI API 请求 | `~u chat/completions` |
| 只看 POST 请求 | `~m POST` |
| 查找系统提示词 | `~bq system` |
| 匹配多个 AI 域名 | `~d openai\|anthropic\|cursor` |
| 查看有错误的请求 | `~e` |
| 只看 JSON 响应 | `~t application/json` |
| 某个 IP 段的请求 | `~dst 104.18` |

---

## 查看请求详情

点击列表中任意一条请求，右侧面板显示：

| 标签页 | 内容 |
|--------|------|
| **Request Headers** | 请求方法、URL、Host、IP、所有请求头 |
| **Query Params** | URL 查询参数，解析为 key = value 格式（GET 请求参数一目了然） |
| **Request Body** | 请求体明文（JSON 自动格式化缩进） |
| **Response Headers** | 状态码、所有响应头 |
| **Response Body** | 响应体明文（JSON 自动格式化缩进） |

**复制内容：** 在 Request Body / Response Body 页面点击 **「复制」** 按钮即可复制到剪贴板。

**Size 列说明：** 列表中的 Size 列显示响应包大小，格式为人类可读的 `1.2kb`、`340b` 等。

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
