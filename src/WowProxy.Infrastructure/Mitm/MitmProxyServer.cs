using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace WowProxy.Infrastructure.Mitm;

/// <summary>
/// A MITM HTTP/HTTPS proxy that intercepts ALL traffic, captures plaintext
/// request/response data, and forwards to an upstream proxy or directly to the target.
/// Works standalone — no upstream proxy required.
/// Filtering is done post-capture in the UI (like Wireshark display filters).
/// </summary>
public sealed class MitmProxyServer : IAsyncDisposable
{
    private readonly MitmCertificateAuthority _ca;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _port;

    // Upstream proxy (optional — e.g., sing-box mixed-in)
    private string? _upstreamProxyHost;
    private int _upstreamProxyPort;

    // Captured messages
    private readonly ConcurrentQueue<CapturedHttpMessage> _captured = new();
    private const int MaxCaptured = 2000;

    public event Action<CapturedHttpMessage>? OnMessageCaptured;
    public event Action<string>? OnLog;

    public MitmProxyServer(MitmCertificateAuthority ca)
    {
        _ca = ca;
    }

    static MitmProxyServer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public int Port => _port;
    public bool IsRunning => _listener != null;

    public IReadOnlyCollection<CapturedHttpMessage> CapturedMessages =>
        _captured.ToArray();

    public void SetUpstreamProxy(string? host, int port)
    {
        _upstreamProxyHost = host;
        _upstreamProxyPort = port;
    }

    public void ClearUpstreamProxy()
    {
        _upstreamProxyHost = null;
        _upstreamProxyPort = 0;
    }

    public bool HasUpstreamProxy => !string.IsNullOrEmpty(_upstreamProxyHost) && _upstreamProxyPort > 0;

    public void ClearCaptured() => _captured.Clear();

    public void Start(int port)
    {
        if (_listener != null) return;

        _port = port;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();

        Log($"MITM 抓包已启动 127.0.0.1:{port}（拦截全部 HTTPS 流量）" +
            (HasUpstreamProxy ? $"  上游代理: {_upstreamProxyHost}:{_upstreamProxyPort}" : "  直连模式"));
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _ca.Dispose();
    }

    public async Task StopAsync()
    {
        if (_listener == null) return;

        _cts?.Cancel();
        _listener.Stop();
        _listener = null;

        if (_acceptLoop != null)
        {
            try { await _acceptLoop; } catch { }
            _acceptLoop = null;
        }

        _cts?.Dispose();
        _cts = null;
        Log("MITM 抓包已停止");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log($"Accept error: {ex.Message}");
                client?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();

        try
        {
            var firstLine = await ReadLineAsync(stream, ct);
            if (string.IsNullOrEmpty(firstLine)) return;

            var parts = firstLine.Split(' ', 3);
            if (parts.Length < 2) return;

            var method = parts[0].ToUpperInvariant();
            var target = parts[1];

            if (method == "CONNECT")
            {
                await HandleConnectAsync(stream, target, ct);
            }
            else
            {
                await HandlePlainHttpAsync(stream, method, target, firstLine, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Log($"Client error: {ex.Message}");
        }
    }

    private async Task HandleConnectAsync(NetworkStream clientStream, string target, CancellationToken ct)
    {
        // Read remaining headers from CONNECT request
        while (true)
        {
            var line = await ReadLineAsync(clientStream, ct);
            if (string.IsNullOrEmpty(line)) break;
        }

        // Parse host:port
        var colonIdx = target.LastIndexOf(':');
        var host = colonIdx > 0 ? target[..colonIdx] : target;
        var port = colonIdx > 0 && int.TryParse(target[(colonIdx + 1)..], out var p) ? p : 443;

        // Resolve IP for the RemoteAddress field
        var remoteAddr = host;
        try
        {
            if (!IPAddress.TryParse(host, out _))
            {
                var addrs = await Dns.GetHostAddressesAsync(host, ct);
                if (addrs.Length > 0) remoteAddr = addrs[0].ToString();
            }
        }
        catch { /* keep hostname */ }

        // Always intercept — capture ALL traffic
        // Send 200 Connection Established
        var response = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
        await clientStream.WriteAsync(response, ct);
        await clientStream.FlushAsync(ct);

        // Get/create domain certificate
        var cert = _ca.GetOrCreateDomainCert(host);

        // TLS handshake with client (we act as the server)
        var sslClientStream = new SslStream(clientStream, leaveInnerStreamOpen: true);
        try
        {
            await sslClientStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, ct);
        }
        catch (Exception ex)
        {
            Log($"TLS 握手失败 {host}: {ex.Message}");
            sslClientStream.Dispose();
            return;
        }

        // Now read plaintext HTTP from the decrypted TLS stream
        await HandleDecryptedStreamAsync(sslClientStream, host, port, remoteAddr, ct);
        sslClientStream.Dispose();
    }

    private async Task HandleDecryptedStreamAsync(SslStream clientSsl, string host, int port, string remoteAddr, CancellationToken ct)
    {
        try
        {
            // Read the HTTP request from the decrypted stream
            var (method, path, httpVersion, headers, bodyBytes) = await ReadHttpRequestAsync(clientSsl, ct);
            if (method == null) return;

            var url = $"https://{host}{path}";
            var reqContentType = headers.TryGetValue("Content-Type", out var rct) ? rct : "";
            var bodyStr = DecodeBodyForCapture(bodyBytes, headers, reqContentType);

            var captured = new CapturedHttpMessage
            {
                Method = method,
                Url = url,
                Path = path ?? "/",
                Host = host,
                Protocol = "HTTPS",
                RemoteAddress = remoteAddr,
                RemotePort = port,
                RequestHeaders = headers,
                RawRequestBody = bodyStr,
                RequestBody = bodyStr,
                RequestContentType = reqContentType,
                RequestSize = bodyBytes.Length,
                Timestamp = DateTimeOffset.Now,
            };

            var sw = Stopwatch.StartNew();

            // Connect to the actual server (through upstream proxy or direct)
            TcpClient? serverTcp = null;
            SslStream? serverSsl = null;

            try
            {
                Stream serverStream;

                if (HasUpstreamProxy)
                {
                    serverTcp = new TcpClient();
                    await serverTcp.ConnectAsync(_upstreamProxyHost!, _upstreamProxyPort, ct);
                    serverTcp.NoDelay = true;
                    var ns = serverTcp.GetStream();

                    // Send CONNECT to upstream proxy
                    var connectReq = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n";
                    await ns.WriteAsync(Encoding.ASCII.GetBytes(connectReq), ct);
                    var connectResp = await ReadLineAsync(ns, ct);
                    while (true)
                    {
                        var l = await ReadLineAsync(ns, ct);
                        if (string.IsNullOrEmpty(l)) break;
                    }

                    if (connectResp == null || !connectResp.Contains("200"))
                    {
                        captured.Error = $"上游代理拒绝 CONNECT: {connectResp}";
                        captured.IsCompleted = true;
                        AddCaptured(captured);
                        serverTcp.Dispose();
                        return;
                    }

                    serverSsl = new SslStream(ns, leaveInnerStreamOpen: true);
                    await serverSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                    }, ct);
                    serverStream = serverSsl;
                }
                else
                {
                    // Direct connect
                    serverTcp = new TcpClient();
                    await serverTcp.ConnectAsync(host, port, ct);
                    serverTcp.NoDelay = true;
                    serverSsl = new SslStream(serverTcp.GetStream(), leaveInnerStreamOpen: true);
                    await serverSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                    }, ct);
                    serverStream = serverSsl;
                }

                // Forward the original request bytes to the server
                var reqBytes = BuildHttpRequest(method, path ?? "/", httpVersion ?? "HTTP/1.1", headers, bodyBytes);
                await serverStream.WriteAsync(reqBytes, ct);
                await serverStream.FlushAsync(ct);

                // Read the response
                var (statusCode, respHeaders, respBodyBytes) = await ReadHttpResponseAsync(serverStream, ct);
                sw.Stop();

                var respContentType = respHeaders.TryGetValue("Content-Type", out var respCt) ? respCt : "";

                captured.StatusCode = statusCode;
                captured.ResponseHeaders = respHeaders;
                var responseBodyText = DecodeBodyForCapture(respBodyBytes, respHeaders, respContentType, contentAlreadyDecoded: true);
                captured.RawResponseBody = responseBodyText;
                captured.ResponseBody = responseBodyText;
                captured.ResponseContentType = respContentType;
                captured.ResponseSize = respBodyBytes.Length;
                captured.DurationMs = sw.ElapsedMilliseconds;
                captured.IsCompleted = true;
                AddCaptured(captured);

                // Forward response back to client
                var respBytes = BuildHttpResponse(statusCode, respHeaders, respBodyBytes);
                await clientSsl.WriteAsync(respBytes, ct);
                await clientSsl.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                sw.Stop();
                captured.Error = ex.Message;
                captured.DurationMs = sw.ElapsedMilliseconds;
                captured.IsCompleted = true;
                AddCaptured(captured);
            }
            finally
            {
                serverSsl?.Dispose();
                serverTcp?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log($"解密流错误 {host}: {ex.Message}");
        }
    }

    private async Task HandlePlainHttpAsync(NetworkStream stream, string method, string url,
        string firstLine, CancellationToken ct)
    {
        Uri.TryCreate(url, UriKind.Absolute, out var uri);
        var host = uri?.Host ?? "";
        var path = uri?.PathAndQuery ?? url;
        var port = uri?.Port ?? 80;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(stream, ct);
            if (string.IsNullOrEmpty(line)) break;
            var sep = line.IndexOf(':');
            if (sep > 0)
                headers[line[..sep].Trim()] = line[(sep + 1)..].Trim();
        }

        var bodyBytes = await ReadBodyBytesFromHeaders(stream, headers, ct);
        var reqContentType = headers.TryGetValue("Content-Type", out var rct) ? rct : "";
        var requestBodyText = DecodeBodyForCapture(bodyBytes, headers, reqContentType);

        var captured = new CapturedHttpMessage
        {
            Method = method,
            Url = url,
            Path = path,
            Host = host,
            Protocol = "HTTP",
            RemoteAddress = host,
            RemotePort = port,
            RequestHeaders = headers,
            RawRequestBody = requestBodyText,
            RequestBody = requestBodyText,
            RequestContentType = reqContentType,
            RequestSize = bodyBytes.Length,
        };

        // Forward to upstream or direct
        var sw = Stopwatch.StartNew();
        TcpClient? remote = null;
        try
        {
            if (HasUpstreamProxy)
            {
                remote = new TcpClient();
                await remote.ConnectAsync(_upstreamProxyHost!, _upstreamProxyPort, ct);
            }
            else
            {
                remote = new TcpClient();
                await remote.ConnectAsync(host, port, ct);
            }
            remote.NoDelay = true;
            var ns = remote.GetStream();

            // Rebuild and send request
            var target = HasUpstreamProxy ? url : path;
            var reqLine = $"{method} {target} HTTP/1.1\r\n";
            await ns.WriteAsync(Encoding.ASCII.GetBytes(reqLine), ct);
            foreach (var (k, v) in headers)
                await ns.WriteAsync(Encoding.ASCII.GetBytes($"{k}: {v}\r\n"), ct);
            await ns.WriteAsync("\r\n"u8.ToArray(), ct);
            if (bodyBytes.Length > 0)
                await ns.WriteAsync(bodyBytes, ct);
            await ns.FlushAsync(ct);

            // Read response
            var (statusCode, respHeaders, respBodyBytes) = await ReadHttpResponseAsync(ns, ct);
            sw.Stop();

            var respContentType = respHeaders.TryGetValue("Content-Type", out var respCt) ? respCt : "";
            captured.StatusCode = statusCode;
            captured.ResponseHeaders = respHeaders;
            var responseBodyText = DecodeBodyForCapture(respBodyBytes, respHeaders, respContentType, contentAlreadyDecoded: true);
            captured.RawResponseBody = responseBodyText;
            captured.ResponseBody = responseBodyText;
            captured.ResponseContentType = respContentType;
            captured.ResponseSize = respBodyBytes.Length;
            captured.DurationMs = sw.ElapsedMilliseconds;
            captured.IsCompleted = true;
            AddCaptured(captured);

            // Forward back to client
            var respBytes = BuildHttpResponse(statusCode, respHeaders, respBodyBytes);
            await stream.WriteAsync(respBytes, ct);
            await stream.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            captured.Error = ex.Message;
            captured.DurationMs = sw.ElapsedMilliseconds;
            captured.IsCompleted = true;
            AddCaptured(captured);
        }
        finally
        {
            remote?.Dispose();
        }
    }

    private void AddCaptured(CapturedHttpMessage msg)
    {
        _captured.Enqueue(msg);
        while (_captured.Count > MaxCaptured)
            _captured.TryDequeue(out _);
        OnMessageCaptured?.Invoke(msg);
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    #region HTTP parsing helpers

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(buf.AsMemory(0, 1), ct);
            if (n == 0) return sb.Length > 0 ? sb.ToString() : null;
            var c = (char)buf[0];
            if (c == '\n')
            {
                var result = sb.ToString();
                return result.EndsWith('\r') ? result[..^1] : result;
            }
            sb.Append(c);
        }
    }

    private static async Task<(string? method, string? path, string? httpVersion,
        Dictionary<string, string> headers, byte[] body)> ReadHttpRequestAsync(Stream stream, CancellationToken ct)
    {
        var firstLine = await ReadLineAsync(stream, ct);
        if (firstLine == null) return (null, null, null, new(), Array.Empty<byte>());

        var parts = firstLine.Split(' ', 3);
        var method = parts.Length > 0 ? parts[0] : "";
        var path = parts.Length > 1 ? parts[1] : "/";
        var httpVersion = parts.Length > 2 ? parts[2] : "HTTP/1.1";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(stream, ct);
            if (string.IsNullOrEmpty(line)) break;
            var sep = line.IndexOf(':');
            if (sep > 0)
                headers[line[..sep].Trim()] = line[(sep + 1)..].Trim();
        }

        var body = await ReadBodyBytesFromHeaders(stream, headers, ct);
        return (method, path, httpVersion, headers, body);
    }

    private static async Task<(int statusCode, Dictionary<string, string> headers, byte[] body)>
        ReadHttpResponseAsync(Stream stream, CancellationToken ct)
    {
        var statusLine = await ReadLineAsync(stream, ct);
        var statusCode = 0;
        if (statusLine != null)
        {
            var parts = statusLine.Split(' ', 3);
            if (parts.Length >= 2) int.TryParse(parts[1], out statusCode);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(stream, ct);
            if (string.IsNullOrEmpty(line)) break;
            var sep = line.IndexOf(':');
            if (sep > 0)
                headers[line[..sep].Trim()] = line[(sep + 1)..].Trim();
        }

        var body = await ReadBodyBytesFromHeaders(stream, headers, ct);

        // Decompress if needed
        if (headers.TryGetValue("Content-Encoding", out var ce) && body.Length > 0)
        {
            try { body = DecompressBody(body, ce); }
            catch { }
        }

        return (statusCode, headers, body);
    }

    private static async Task<byte[]> ReadBodyBytesFromHeaders(Stream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        if (headers.TryGetValue("Transfer-Encoding", out var te) &&
            te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadChunkedBodyAsync(stream, ct);
        }

        if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl) && cl > 0)
        {
            var buf = new byte[Math.Min(cl, 4 * 1024 * 1024)]; // cap at 4MB
            var read = 0;
            while (read < buf.Length)
            {
                var n = await stream.ReadAsync(buf.AsMemory(read, buf.Length - read), ct);
                if (n == 0) break;
                read += n;
            }
            return buf[..read];
        }

        return Array.Empty<byte>();
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(stream, ct);
            if (sizeLine == null) break;

            int chunkSize;
            try { chunkSize = Convert.ToInt32(sizeLine.Trim(), 16); }
            catch { break; }

            if (chunkSize == 0)
            {
                await ReadLineAsync(stream, ct);
                break;
            }

            var buf = new byte[chunkSize];
            var read = 0;
            while (read < chunkSize)
            {
                var n = await stream.ReadAsync(buf.AsMemory(read, chunkSize - read), ct);
                if (n == 0) break;
                read += n;
            }
            ms.Write(buf, 0, read);
            await ReadLineAsync(stream, ct);
        }
        return ms.ToArray();
    }

    private static byte[] DecompressBody(byte[] data, string encoding)
    {
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        Stream decompressor;

        if (encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            decompressor = new GZipStream(input, CompressionMode.Decompress);
        else if (encoding.Contains("br", StringComparison.OrdinalIgnoreCase))
            decompressor = new BrotliStream(input, CompressionMode.Decompress);
        else if (encoding.Contains("deflate", StringComparison.OrdinalIgnoreCase))
            decompressor = new DeflateStream(input, CompressionMode.Decompress);
        else
            return data;

        using (decompressor) decompressor.CopyTo(output);
        return output.ToArray();
    }

    private static string DecodeBodyForCapture(
        byte[] bodyBytes,
        Dictionary<string, string> headers,
        string? contentType,
        bool contentAlreadyDecoded = false)
    {
        if (bodyBytes.Length == 0)
            return string.Empty;

        var decodedBytes = bodyBytes;
        if (!contentAlreadyDecoded &&
            headers.TryGetValue("Content-Encoding", out var contentEncoding) &&
            !string.IsNullOrWhiteSpace(contentEncoding))
        {
            try
            {
                decodedBytes = DecompressBody(bodyBytes, contentEncoding);
            }
            catch
            {
                decodedBytes = bodyBytes;
            }
        }

        return DecodeText(decodedBytes, contentType);
    }

    private static string DecodeText(byte[] bodyBytes, string? contentType)
    {
        var explicitEncoding = TryGetEncodingFromContentType(contentType);
        if (explicitEncoding != null)
        {
            try
            {
                return explicitEncoding.GetString(bodyBytes);
            }
            catch
            {
                // Fall through to auto-detection below.
            }
        }

        var bomEncoding = TryGetBomEncoding(bodyBytes);
        if (bomEncoding != null)
        {
            try
            {
                return bomEncoding.GetString(bodyBytes);
            }
            catch
            {
                // Fall through to UTF-8 detection below.
            }
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bodyBytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bodyBytes);
        }
    }

    private static Encoding? TryGetEncodingFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;

        const string charsetToken = "charset=";
        var charsetIndex = contentType.IndexOf(charsetToken, StringComparison.OrdinalIgnoreCase);
        if (charsetIndex < 0)
            return null;

        var charset = contentType[(charsetIndex + charsetToken.Length)..].Trim();
        var separatorIndex = charset.IndexOf(';');
        if (separatorIndex >= 0)
            charset = charset[..separatorIndex];

        charset = charset.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(charset))
            return null;

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch
        {
            return null;
        }
    }

    private static Encoding? TryGetBomEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8;

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode;

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return Encoding.UTF32;

        return null;
    }

    private static byte[] BuildHttpRequest(string method, string path, string httpVersion,
        Dictionary<string, string> headers, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(path).Append(' ').Append(httpVersion).Append("\r\n");
        foreach (var (k, v) in headers)
            sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("\r\n");

        var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        if (body.Length == 0) return headerBytes;

        var result = new byte[headerBytes.Length + body.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(body, 0, result, headerBytes.Length, body.Length);
        return result;
    }

    private static byte[] BuildHttpResponse(int statusCode, Dictionary<string, string> headers, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(statusCode).Append(" OK\r\n");

        var skipHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Transfer-Encoding", "Content-Encoding", "Content-Length" };

        foreach (var (k, v) in headers)
        {
            if (!skipHeaders.Contains(k))
                sb.Append(k).Append(": ").Append(v).Append("\r\n");
        }
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("\r\n");

        var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var result = new byte[headerBytes.Length + body.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(body, 0, result, headerBytes.Length, body.Length);
        return result;
    }

    #endregion
}
