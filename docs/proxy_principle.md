# WowProxy 代理工作原理详解

本文档基于您提供的 VMess 节点链接，详细解析 WowProxy 在 **系统代理 (System Proxy)** 和 **TUN 模式** 下如何实现外网访问。

## 1. 节点配置解析

首先，我们解析一下您提供的 VMess 链接（Base64 解码后的关键参数）：

*   **协议**: VMess (一种加密传输协议)
*   **服务器 IP (`add`)**: `104.16.163.89` (这是 Cloudflare 的 CDN 节点 IP)
*   **端口 (`port`)**: `443` (HTTPS 标准端口)
*   **用户 ID (`id`)**: `e0e7c68f-...` (相当于连接密码)
*   **传输层**: WebSocket (`net`: "ws") + TLS (`tls`: "tls")
*   **伪装域名 (`host`/`sni`)**: `lock-shareholders-did-fruit.trycloudflare.com`

**这意味着**：WowProxy 会伪装成一个正常的浏览器，向 `104.16.163.89` 发起一个安全的 HTTPS (WebSocket) 连接。在这个加密通道内部，传输您实际访问 Google/YouTube 的数据。

---

## 2. 模式一：系统代理 (System Proxy)

这是最基础的模式，依赖于操作系统和软件的主动配合。

### 工作流程：

1.  **初始化监听**：
    *   WowProxy 启动核心 `sing-box.exe`。
    *   `sing-box` 在本地开启一个 HTTP/SOCKS 端口，例如 `127.0.0.1:10808`。
    *   WowProxy 修改 Windows 系统设置 -> “网络和 Internet” -> “代理”，填入 `127.0.0.1:10808`。

2.  **用户发起请求**：
    *   您在 Chrome 浏览器中输入 `https://www.youtube.com`。

3.  **浏览器处理**：
    *   Chrome 检查系统代理设置，发现需要走代理。
    *   Chrome **不进行 DNS 解析**（或者进行远程 DNS 解析），直接将请求打包发送给 `127.0.0.1:10808`。
    *   **数据包内容**：`CONNECT www.youtube.com:443 HTTP/1.1`。

4.  **代理核心处理 (Sing-box)**：
    *   Sing-box 收到请求，识别目标是 `www.youtube.com`。
    *   **路由判断**：根据规则（Geosite），判定 YouTube 需要走代理节点。
    *   **数据封装**：
        1.  将原始请求加密为 VMess 数据包。
        2.  将 VMess 数据包封装进 WebSocket 帧。
        3.  将 WebSocket 帧封装进 TLS (SSL) 加密层。
    *   **建立连接**：Sing-box 通过物理网卡，向节点 IP `104.16.163.89` 的 443 端口发起 TCP 连接。
    *   **握手伪装**：在 TLS 握手阶段，发送 SNI `lock-shareholders...`，让防火墙以为你在访问这个普通网站。

5.  **数据传输**：
    *   加密数据到达代理服务器 -> 服务器解密 -> 代替您访问 YouTube -> 结果原路加密返回。

**局限性**：如果不听话的软件（如命令行、部分游戏）不读取系统代理设置，它们会直接尝试直连 YouTube，导致无法访问。

---

## 3. 模式二：TUN 模式 (虚拟网卡)

这是进阶模式，在网络层（Layer 3）强行接管流量，无需软件配合。

### 工作流程：

1.  **虚拟网卡创建**：
    *   WowProxy 启动 `sing-box`，加载 `wintun.dll` 驱动。
    *   在系统中创建一张名为 `wowproxy-tun-xxxxxx` 的虚拟网卡。
    *   **修改路由表**：执行 `route add 0.0.0.0 mask 0.0.0.0 ...`，将所有网络流量的“下一跳”指向这张虚拟网卡。

2.  **DNS 劫持 (关键步骤)**：
    *   您在 Chrome 输入 `www.youtube.com`。
    *   即使 Chrome 想直连，它首先得知道 YouTube 的 IP。
    *   系统发起 DNS 查询（UDP 53端口）：`Who is www.youtube.com?`
    *   由于路由表指向了 TUN 网卡，这个 UDP 包掉进了 Sing-box 的“陷阱”。
    *   **Sing-box 拦截**：触发 `hijack-dns` 规则，Sing-box 没收了这个包，使用内置的加密 DNS (如 1.1.1.1) 查询到真实 IP (例如 `172.217.x.x`)。
    *   Sing-box 伪造一个 DNS 响应告诉系统：“YouTube 的 IP 是 `172.217.x.x`”。

3.  **流量强行接管**：
    *   系统拿到了 IP，试图向 `172.217.x.x` 发起 TCP 连接。
    *   数据包再次根据路由表，流向 TUN 网卡。
    *   Sing-box 再次捕获这个 IP 数据包。

4.  **防回环与转发**：
    *   Sing-box 提取目标 IP `172.217.x.x`，判断需要走代理。
    *   **封装数据**：同样进行 VMess + WebSocket + TLS 封装。
    *   **关键一步**：Sing-box 将封装好的新数据包（目标是节点 IP `104.16.163.89`）发送出去。
    *   **路由排除**：为了防止这个新数据包又掉进 TUN 网卡造成死循环，WowProxy 预先配置了“路由排除规则” (Route Exclude Address)。这个发往节点 IP 的包会**绕过** TUN 网卡，直接走物理网卡（Wi-Fi/以太网）出站。

5.  **结果**：
    *   对于 Chrome 来说，它以为自己是在直连。
    *   对于操作系统来说，流量只是从一个网卡流过了。
    *   实际上，流量已经被 Sing-box 透明地加密并转发了。

### 总结对比

| 特性 | 系统代理 (System Proxy) | TUN 模式 |
| :--- | :--- | :--- |
| **接管层级** | 应用层 (HTTP/SOCKS) | 网络层 (IP) |
| **软件兼容性** | 差 (需软件支持代理设置) | **完美** (接管所有软件，包括游戏/CMD) |
| **DNS 解析** | 依赖本地或浏览器 | **强制接管** (防污染能力最强) |
| **原理核心** | "请帮我转交" | "此路是我开，留下买路财" |
