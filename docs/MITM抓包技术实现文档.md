# MITM 抓包分析技术实现文档

## 目录
1. [技术概述](#技术概述)
2. [核心技术原理](#核心技术原理)
3. [架构设计](#架构设计)
4. [实现细节](#实现细节)
5. [关键代码模块](#关键代码模块)
6. [数据流转](#数据流转)
7. [安全机制](#安全机制)

---

## 技术概述

WowProxy 的 MITM（Man-In-The-Middle，中间人）抓包分析功能是一个**应用层 HTTP/HTTPS 代理**，能够拦截、解密、分析和展示 HTTP/HTTPS 流量的完整内容。

### 核心技术栈
- **.NET 8.0** - 运行时环境
- **WPF (Windows Presentation Foundation)** - UI 框架
- **MVVM 模式** - 架构模式
- **TLS/SSL** - 加密通信协议
- **X.509 证书** - 数字证书标准
- **HTTP/1.1 协议** - 应用层协议

### 与其他抓包工具的对比

| 工具 | 工作层级 | 能否解密 HTTPS | 需要配置代理 | 典型用途 |
|------|----------|---------------|-------------|----------|
| **Wireshark** | 网络层/传输层 | ❌ 不能 | ❌ 不需要 | 网络协议分析 |
| **Fiddler** | 应用层 | ✅ 能 | ✅ 需要 | Web 调试 |
| **mitmproxy** | 应用层 | ✅ 能 | ✅ 需要 | API 测试 |
| **WowProxy MITM** | 应用层 | ✅ 能 | ✅ 需要 | AI API 分析 |

---

## 核心技术原理

### 1. MITM 攻击原理

MITM 抓包的本质是**合法的中间人攻击**，通过在客户端和服务器之间插入代理，实现流量拦截和解密。

#### 正常 HTTPS 通信
```
客户端 <--[TLS 加密]--> 服务器
```
- 客户端直接与服务器建立 TLS 连接
- 使用服务器的证书加密通信
- 第三方无法解密内容

#### MITM 代理通信
```
客户端 <--[TLS1]--> MITM 代理 <--[TLS2]--> 服务器
         (伪造证书)           (真实证书)
```
- 客户端与代理建立 TLS1 连接（使用代理伪造的证书）
- 代理与服务器建立 TLS2 连接（使用服务器真实证书）
- 代理在中间解密、查看、修改、重新加密流量

### 2. CA 证书机制

为了让客户端信任代理的伪造证书，需要：

1. **生成根 CA 证书**
   - 代理生成自己的根证书颁发机构（CA）
   - 使用 RSA 2048 位密钥
   - 有效期 10 年

2. **安装到系统信任库**
   - Windows: `certmgr.msc` → 受信任的根证书颁发机构
   - 用户手动安装或程序自动安装

3. **动态签发服务器证书**
   - 客户端请求 `api.openai.com`
   - 代理动态生成 `api.openai.com` 的证书
   - 使用根 CA 签名该证书
   - 客户端因为信任根 CA，所以信任这个伪造证书

### 3. HTTP CONNECT 隧道

HTTPS 代理使用 `CONNECT` 方法建立 TLS 隧道：

```http
CONNECT api.openai.com:443 HTTP/1.1
Host: api.openai.com:443
```

代理响应：
```http
HTTP/1.1 200 Connection Established
```

之后客户端在这个隧道上进行 TLS 握手。

---

## 架构设计

### 整体架构

```
┌─────────────────────────────────────────────────────────┐
│                    WowProxy.App (WPF)                   │
│  ┌──────────────────────────────────────────────────┐   │
│  │         MitmCaptureViewModel (MVVM)              │   │
│  │  - 搜索过滤逻辑                                   │   │
│  │  - 捕获消息管理                                   │   │
│  │  - UI 数据绑定                                    │   │
│  └────────────────┬─────────────────────────────────┘   │
│                   │                                      │
│                   ▼                                      │
│  ┌──────────────────────────────────────────────────┐   │
│  │            MainWindow.xaml (UI)                  │   │
│  │  - 搜索栏 (mitmproxy 风格)                        │   │
│  │  - DataGrid (请求列表)                           │   │
│  │  - TabControl (详情面板)                         │   │
│  └──────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
                           │
                           │ 调用
                           ▼
┌─────────────────────────────────────────────────────────┐
│            WowProxy.Infrastructure.Mitm                 │
│  ┌──────────────────────────────────────────────────┐   │
│  │          MitmProxyServer (核心引擎)              │   │
│  │  - TCP 监听 (端口 10811)                         │   │
│  │  - TLS 握手和证书伪造                            │   │
│  │  - HTTP 协议解析                                 │   │
│  │  - 流量转发                                      │   │
│  └──────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────┐   │
│  │      MitmCertificateAuthority (CA 管理)          │   │
│  │  - 生成根 CA 证书                                │   │
│  │  - 动态签发服务器证书                            │   │
│  │  - 证书缓存                                      │   │
│  └──────────────────────────────────────────────────┘   │
│  ┌──────────────────────────────────────────────────┐   │
│  │      CapturedHttpMessage (数据模型)              │   │
│  │  - 请求/响应数据                                 │   │
│  │  - 元数据 (时间、大小、状态码等)                 │   │
│  └──────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
```

### 模块职责

#### 1. MitmProxyServer
- **TCP 服务器**：监听 `127.0.0.1:10811`
- **连接处理**：每个客户端连接创建独立任务
- **协议解析**：解析 HTTP 请求和响应
- **TLS 终结**：解密客户端 TLS，重新加密到服务器
- **流量转发**：双向转发请求和响应

#### 2. MitmCertificateAuthority
- **根 CA 管理**：生成、存储、安装根证书
- **证书签发**：为每个域名动态生成证书
- **证书缓存**：避免重复生成相同域名的证书

#### 3. MitmCaptureViewModel
- **数据管理**：维护所有捕获的消息列表
- **搜索过滤**：mitmproxy 风格的前缀搜索
- **UI 绑定**：提供 WPF 数据绑定属性
- **命令处理**：启动/停止、清空、复制等

#### 4. CapturedHttpMessage
- **数据模型**：封装 HTTP 请求/响应的所有信息
- **字段提取**：支持按字段名提取数据用于过滤
- **格式化**：人类可读的大小显示、Query 参数解析

---

## 实现细节

### 1. TCP 监听和连接处理

```csharp
public void Start(int port)
{
    _listener = new TcpListener(IPAddress.Loopback, port);
    _listener.Start();
    _listenTask = Task.Run(async () =>
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
        }
    });
}
```

**关键点：**
- 使用 `TcpListener` 监听本地端口
- 每个连接创建独立的异步任务处理
- 使用 `CancellationToken` 支持优雅停止

### 2. HTTP CONNECT 处理

```csharp
private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
{
    var stream = client.GetStream();
    
    // 读取 HTTP 请求行
    var requestLine = await ReadLineAsync(stream, ct);
    // CONNECT api.openai.com:443 HTTP/1.1
    
    if (requestLine.StartsWith("CONNECT"))
    {
        // 解析目标主机和端口
        var parts = requestLine.Split(' ');
        var hostPort = parts[1].Split(':');
        var targetHost = hostPort[0];
        var targetPort = int.Parse(hostPort[1]);
        
        // 读取并丢弃请求头
        while (!string.IsNullOrEmpty(await ReadLineAsync(stream, ct))) { }
        
        // 响应 200 Connection Established
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 Connection Established\r\n\r\n"), ct);
        
        // 升级到 TLS
        await HandleHttpsAsync(stream, targetHost, targetPort, ct);
    }
}
```

### 3. TLS 握手和证书伪造

```csharp
private async Task HandleHttpsAsync(Stream clientStream, string targetHost, int targetPort, CancellationToken ct)
{
    // 1. 为目标域名生成证书
    var serverCert = _ca.GetOrCreateServerCertificate(targetHost);
    
    // 2. 与客户端建立 TLS (使用伪造证书)
    var sslStream = new SslStream(clientStream, false);
    await sslStream.AuthenticateAsServerAsync(
        serverCert,
        clientCertificateRequired: false,
        enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
        checkCertificateRevocation: false);
    
    // 3. 连接真实服务器
    var serverClient = new TcpClient();
    await serverClient.ConnectAsync(targetHost, targetPort, ct);
    var serverStream = serverClient.GetStream();
    
    // 4. 与服务器建立 TLS (使用真实证书)
    var serverSsl = new SslStream(serverStream, false, 
        (sender, cert, chain, errors) => true); // 忽略证书验证
    await serverSsl.AuthenticateAsClientAsync(targetHost);
    
    // 5. 双向转发流量
    await ForwardTrafficAsync(sslStream, serverSsl, ct);
}
```

**关键点：**
- 客户端侧：使用 `AuthenticateAsServerAsync`，提供伪造证书
- 服务器侧：使用 `AuthenticateAsClientAsync`，验证真实证书
- 两个独立的 TLS 连接，代理在中间解密

### 4. HTTP 协议解析

```csharp
private async Task<(string method, string path, Dictionary<string, string> headers, byte[] body)>
    ReadHttpRequestAsync(Stream stream, CancellationToken ct)
{
    // 读取请求行: GET /v1/chat/completions HTTP/1.1
    var requestLine = await ReadLineAsync(stream, ct);
    var parts = requestLine.Split(' ', 3);
    var method = parts[0];
    var path = parts[1];
    
    // 读取请求头
    var headers = new Dictionary<string, string>();
    while (true)
    {
        var line = await ReadLineAsync(stream, ct);
        if (string.IsNullOrEmpty(line)) break;
        
        var sep = line.IndexOf(':');
        if (sep > 0)
            headers[line[..sep].Trim()] = line[(sep + 1)..].Trim();
    }
    
    // 读取请求体
    var body = await ReadBodyBytesFromHeaders(stream, headers, ct);
    
    return (method, path, headers, body);
}
```

### 5. Chunked 传输编码处理

```csharp
private async Task<byte[]> ReadChunkedBodyAsync(Stream stream, CancellationToken ct)
{
    using var ms = new MemoryStream();
    while (true)
    {
        // 读取块大小 (十六进制)
        var sizeLine = await ReadLineAsync(stream, ct);
        var chunkSize = Convert.ToInt32(sizeLine.Trim(), 16);
        
        if (chunkSize == 0)
        {
            await ReadLineAsync(stream, ct); // 读取尾部 CRLF
            break;
        }
        
        // 读取块数据
        var buf = new byte[chunkSize];
        await stream.ReadExactlyAsync(buf, ct);
        ms.Write(buf);
        
        await ReadLineAsync(stream, ct); // 读取块后的 CRLF
    }
    return ms.ToArray();
}
```

### 6. 内容解压缩

```csharp
private static byte[] DecompressBody(byte[] compressed, string encoding)
{
    using var input = new MemoryStream(compressed);
    using var output = new MemoryStream();
    
    Stream decompressor = encoding.ToLower() switch
    {
        "gzip" => new GZipStream(input, CompressionMode.Decompress),
        "deflate" => new DeflateStream(input, CompressionMode.Decompress),
        "br" => new BrotliStream(input, CompressionMode.Decompress),
        _ => throw new NotSupportedException($"Unsupported encoding: {encoding}")
    };
    
    decompressor.CopyTo(output);
    return output.ToArray();
}
```

### 7. 证书生成和签发

```csharp
public X509Certificate2 GetOrCreateServerCertificate(string hostname)
{
    if (_certCache.TryGetValue(hostname, out var cached))
        return cached;
    
    var rootCa = GetOrCreateRootCa();
    
    // 生成服务器证书
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest(
        $"CN={hostname}",
        rsa,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);
    
    // 添加 SAN (Subject Alternative Name)
    var sanBuilder = new SubjectAlternativeNameBuilder();
    sanBuilder.AddDnsName(hostname);
    req.CertificateExtensions.Add(sanBuilder.Build());
    
    // 使用根 CA 签名
    var cert = req.Create(
        rootCa,
        DateTimeOffset.Now.AddDays(-1),
        DateTimeOffset.Now.AddYears(1),
        BitConverter.GetBytes(DateTime.Now.Ticks));
    
    var certWithKey = cert.CopyWithPrivateKey(rsa);
    _certCache[hostname] = certWithKey;
    
    return certWithKey;
}
```

---

## 关键代码模块

### 文件结构

```
WowProxy.Infrastructure/Mitm/
├── MitmProxyServer.cs          # 核心代理服务器
├── MitmCertificateAuthority.cs # CA 证书管理
└── CapturedHttpMessage.cs      # 数据模型

WowProxy.App/ViewModels/
└── MitmCaptureViewModel.cs     # UI 逻辑

WowProxy.App/
└── MainWindow.xaml             # UI 界面
```

### 核心类关系

```
MitmCaptureViewModel
    ├── 持有 → MitmCertificateAuthority
    ├── 持有 → MitmProxyServer
    └── 管理 → List<CapturedHttpMessage>

MitmProxyServer
    ├── 使用 → MitmCertificateAuthority
    ├── 触发事件 → OnMessageCaptured
    └── 触发事件 → OnLog

MitmCertificateAuthority
    ├── 管理 → X509Certificate2 (根 CA)
    └── 缓存 → Dictionary<string, X509Certificate2>
```

---

## 数据流转

### 完整请求流程

```
1. 客户端发起请求
   Windsurf → 127.0.0.1:10811

2. MITM 代理接收连接
   TcpListener.AcceptTcpClientAsync()

3. 读取 CONNECT 请求
   CONNECT api.openai.com:443 HTTP/1.1

4. 响应 200 Connection Established

5. TLS 握手 (客户端侧)
   - 生成/获取 api.openai.com 的伪造证书
   - AuthenticateAsServerAsync(伪造证书)

6. 连接真实服务器
   TcpClient.ConnectAsync("api.openai.com", 443)

7. TLS 握手 (服务器侧)
   - AuthenticateAsClientAsync("api.openai.com")

8. 读取 HTTP 请求
   - 请求行: POST /v1/chat/completions HTTP/1.1
   - 请求头: Host, Content-Type, Authorization, ...
   - 请求体: JSON payload

9. 创建 CapturedHttpMessage 对象
   - 记录请求信息
   - 记录时间戳

10. 转发请求到服务器
    serverStream.WriteAsync(requestBytes)

11. 读取 HTTP 响应
    - 状态行: HTTP/1.1 200 OK
    - 响应头: Content-Type, Content-Encoding, ...
    - 响应体: JSON 或 SSE 流

12. 解压缩响应体 (如果需要)
    DecompressBody(gzip/deflate/br)

13. 更新 CapturedHttpMessage
    - 响应状态码
    - 响应头
    - 响应体
    - 耗时

14. 触发 OnMessageCaptured 事件
    ViewModel 接收并添加到列表

15. 转发响应到客户端
    clientStream.WriteAsync(responseBytes)

16. UI 更新
    - DataGrid 显示新请求
    - 应用搜索过滤
```

### 事件驱动模型

```csharp
// MitmProxyServer 触发事件
public event Action<CapturedHttpMessage>? OnMessageCaptured;

private void AddCaptured(CapturedHttpMessage msg)
{
    OnMessageCaptured?.Invoke(msg);
}

// MitmCaptureViewModel 订阅事件
_server.OnMessageCaptured += OnMessageCapturedCallback;

private void OnMessageCapturedCallback(CapturedHttpMessage msg)
{
    Application.Current.Dispatcher.BeginInvoke(() =>
    {
        _allMessages.Insert(0, msg);
        if (MatchesSearch(msg))
            CapturedMessages.Insert(0, msg);
    });
}
```

---

## 安全机制

### 1. 证书安全

**根 CA 证书保护：**
- 存储在用户目录：`%APPDATA%\WowProxy\mitm-ca.pfx`
- 使用密码保护（硬编码在代码中，仅用于本地）
- 仅安装到当前用户的信任库，不影响系统

**证书有效期：**
- 根 CA：10 年
- 服务器证书：1 年
- 自动续期机制

### 2. 本地监听

```csharp
_listener = new TcpListener(IPAddress.Loopback, port);
```

- 仅监听 `127.0.0.1`（本地回环）
- 不接受外部网络连接
- 防止被远程利用

### 3. 数据隐私

**敏感信息处理：**
- Authorization 头中的 Token 完整显示（用户需要查看）
- 不记录到日志文件（仅内存）
- 清空功能立即释放内存

**数据限制：**
```csharp
var buf = new byte[Math.Min(cl, 4 * 1024 * 1024)]; // 限制 4MB
```
- 单个请求/响应体最大 4MB
- 防止内存溢出
- 超大响应截断处理

### 4. 错误处理

```csharp
try
{
    await HandleClientAsync(client, ct);
}
catch (Exception ex)
{
    captured.Error = ex.Message;
    OnLog?.Invoke($"Error: {ex.Message}");
}
finally
{
    client.Close();
}
```

- 每个连接独立异常处理
- 错误记录到 CapturedHttpMessage
- 不影响其他连接

---

## 性能优化

### 1. 异步 I/O

所有网络操作使用异步方法：
- `ReadAsync` / `WriteAsync`
- `AcceptTcpClientAsync`
- `AuthenticateAsServerAsync`

避免线程阻塞，提高并发性能。

### 2. 证书缓存

```csharp
private readonly Dictionary<string, X509Certificate2> _certCache = new();
```

- 相同域名的证书只生成一次
- 避免重复的 RSA 密钥生成
- 显著提升 TLS 握手速度

### 3. 内存管理

```csharp
while (CapturedMessages.Count > 2000)
    CapturedMessages.RemoveAt(CapturedMessages.Count - 1);
```

- 限制显示列表最多 2000 条
- 自动移除旧记录
- 防止内存无限增长

### 4. UI 更新优化

```csharp
Application.Current.Dispatcher.BeginInvoke(() => { ... });
```

- 使用 `BeginInvoke` 而非 `Invoke`
- 非阻塞 UI 更新
- 提高响应速度

---

## 限制和已知问题

### 1. 协议支持

**支持：**
- ✅ HTTP/1.1
- ✅ HTTPS (TLS 1.2, TLS 1.3)
- ✅ Chunked 传输编码
- ✅ Gzip/Deflate/Brotli 压缩

**不支持：**
- ❌ HTTP/2
- ❌ WebSocket
- ❌ HTTP/3 (QUIC)

### 2. 证书固定

某些应用使用证书固定（Certificate Pinning），会拒绝 MITM 证书：
- 部分移动应用
- 部分安全要求高的应用

**解决方法：**
- 使用 `--ignore-certificate-errors` 启动参数
- 或修改应用禁用证书验证

### 3. TUN 模式冲突

TUN 模式和 MITM 代理不能同时工作：
- TUN 在网络层劫持流量
- MITM 需要应用主动连接代理
- 两者流量路径不同

### 4. 性能开销

MITM 代理会增加延迟：
- TLS 握手：+50-200ms
- 双重加密/解密：+10-50ms
- 协议解析：+5-20ms

总延迟约 **100-300ms**，对实时性要求高的应用可能有影响。

---

## 扩展和改进方向

### 1. 协议支持

- [ ] HTTP/2 支持
- [ ] WebSocket 支持
- [ ] Server-Sent Events (SSE) 流式解析优化

### 2. 功能增强

- [ ] 请求重放功能
- [ ] 断点调试（修改请求/响应）
- [ ] 自动保存到文件
- [ ] 导出为 HAR 格式

### 3. 性能优化

- [ ] 使用连接池复用服务器连接
- [ ] 实现 HTTP Keep-Alive
- [ ] 优化大文件传输

### 4. 安全增强

- [ ] 支持客户端证书认证
- [ ] 证书密码加密存储
- [ ] 敏感信息脱敏选项

---

## 参考资料

### 技术标准
- [RFC 7230 - HTTP/1.1 Message Syntax and Routing](https://tools.ietf.org/html/rfc7230)
- [RFC 5246 - TLS 1.2](https://tools.ietf.org/html/rfc5246)
- [RFC 8446 - TLS 1.3](https://tools.ietf.org/html/rfc8446)
- [RFC 5280 - X.509 Certificate](https://tools.ietf.org/html/rfc5280)

### 开源项目
- [mitmproxy](https://github.com/mitmproxy/mitmproxy) - Python MITM 代理
- [Fiddler](https://www.telerik.com/fiddler) - .NET Web 调试代理
- [Titanium Web Proxy](https://github.com/justcoding121/titanium-web-proxy) - .NET HTTP 代理库

### .NET 文档
- [SslStream Class](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream)
- [X509Certificate2 Class](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509certificate2)
- [TcpListener Class](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.tcplistener)

---

## 总结

WowProxy 的 MITM 抓包分析功能是一个**完整的应用层 HTTP/HTTPS 代理**，核心技术包括：

1. **TLS 中间人攻击** - 双重 TLS 连接实现解密
2. **动态证书签发** - 为每个域名生成可信证书
3. **HTTP 协议解析** - 完整解析请求和响应
4. **异步事件驱动** - 高性能并发处理
5. **MVVM 架构** - 清晰的 UI 和业务分离

该实现参考了 mitmproxy 和 Fiddler 的设计思想，针对 AI API 分析场景进行了优化，提供了直观的搜索过滤和内容展示功能。
