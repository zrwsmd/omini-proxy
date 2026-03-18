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
/// A lightweight MITM HTTP/HTTPS proxy that intercepts traffic, captures plaintext
/// request/response data, and forwards to an upstream proxy or directly to the target.
/// </summary>
public sealed class MitmProxyServer : IAsyncDisposable
{
    private readonly MitmCertificateAuthority _ca;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _port;

    // Upstream proxy (e.g., sing-box mixed-in)
    private string? _upstreamProxyHost;
    private int _upstreamProxyPort;

    // Filter: only intercept these domains (empty = intercept all)
    private readonly ConcurrentDictionary<string, bool> _interceptDomains = new(StringComparer.OrdinalIgnoreCase);

    // Captured messages
    private readonly ConcurrentQueue<CapturedHttpMessage> _captured = new();
    private const int MaxCaptured = 500;

    public event Action<CapturedHttpMessage>? OnMessageCaptured;
    public event Action<string>? OnLog;

    public MitmProxyServer(MitmCertificateAuthority ca)
    {
        _ca = ca;
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

    public void SetInterceptDomains(IEnumerable<string> domains)
    {
        _interceptDomains.Clear();
        foreach (var d in domains)
        {
            if (!string.IsNullOrWhiteSpace(d))
                _interceptDomains[d.Trim()] = true;
        }
    }

    public void ClearCaptured() => _captured.Clear();

    public void Start(int port)
    {
        if (_listener != null) return;

        _port = port;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();

        Log($"MITM proxy started on 127.0.0.1:{port}");
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
        Log("MITM proxy stopped");
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
                // Plain HTTP — read remaining headers, capture and forward
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

        // Check if we should intercept this domain
        bool shouldIntercept = ShouldIntercept(host);

        if (!shouldIntercept)
        {
            // Tunnel directly without interception
            await TunnelDirectAsync(clientStream, host, port, ct);
            return;
        }

        // Send 200 Connection Established
        var response = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
        await clientStream.WriteAsync(response, ct);
        await clientStream.FlushAsync(ct);

        // Get/create domain certificate
        var cert = _ca.GetOrCreateDomainCert(host);

        // TLS handshake with client
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
            Log($"TLS handshake failed for {host}: {ex.Message}");
            sslClientStream.Dispose();
            return;
        }

        // Now read plaintext HTTP from the TLS stream
        await HandleDecryptedStreamAsync(sslClientStream, host, port, ct);
        sslClientStream.Dispose();
    }

    private async Task HandleDecryptedStreamAsync(SslStream clientSsl, string host, int port, CancellationToken ct)
    {
        try
        {
            // Read the HTTP request from the decrypted stream
            var (method, path, httpVersion, headers, body) = await ReadHttpRequestAsync(clientSsl, ct);
            if (method == null) return;

            var url = $"https://{host}{path}";
            var captured = new CapturedHttpMessage
            {
                Method = method,
                Url = url,
                Host = host,
                RequestHeaders = headers,
                RequestBody = body,
                Timestamp = DateTimeOffset.Now,
            };

            var sw = Stopwatch.StartNew();

            // Connect to the actual server (through upstream proxy or direct)
            Stream serverStream;
            TcpClient? serverTcp = null;
            SslStream? serverSsl = null;

            try
            {
                if (!string.IsNullOrEmpty(_upstreamProxyHost))
                {
                    serverTcp = new TcpClient();
                    await serverTcp.ConnectAsync(_upstreamProxyHost, _upstreamProxyPort, ct);
                    serverTcp.NoDelay = true;
                    var ns = serverTcp.GetStream();

                    // Send CONNECT to upstream proxy
                    var connectReq = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n";
                    await ns.WriteAsync(Encoding.ASCII.GetBytes(connectReq), ct);
                    var connectResp = await ReadLineAsync(ns, ct);
                    // Read remaining headers
                    while (true)
                    {
                        var l = await ReadLineAsync(ns, ct);
                        if (string.IsNullOrEmpty(l)) break;
                    }

                    if (connectResp == null || !connectResp.Contains("200"))
                    {
                        captured.Error = $"Upstream proxy rejected CONNECT: {connectResp}";
                        captured.IsCompleted = true;
                        AddCaptured(captured);
                        serverTcp.Dispose();
                        return;
                    }

                    // TLS to actual server over the proxy tunnel
                    serverSsl = new SslStream(ns, leaveInnerStreamOpen: true);
                    await serverSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                    }, ct);
                    serverStream = serverSsl;
                }
                else
                {
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

                // Forward the request to the server
                var reqBytes = BuildHttpRequest(method, path, httpVersion, headers, body);
                await serverStream.WriteAsync(reqBytes, ct);
                await serverStream.FlushAsync(ct);

                // Read the response
                var (statusCode, respHeaders, respBody) = await ReadHttpResponseAsync(serverStream, ct);
                sw.Stop();

                captured.StatusCode = statusCode;
                captured.ResponseHeaders = respHeaders;
                captured.ResponseBody = respBody;
                captured.DurationMs = sw.ElapsedMilliseconds;
                captured.IsCompleted = true;
                AddCaptured(captured);

                // Forward response back to client
                var respBytes = BuildHttpResponse(statusCode, respHeaders, respBody);
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
            Log($"Decrypted stream error for {host}: {ex.Message}");
        }
    }

    private async Task HandlePlainHttpAsync(NetworkStream stream, string method, string url,
        string firstLine, CancellationToken ct)
    {
        // Parse host from URL
        Uri.TryCreate(url, UriKind.Absolute, out var uri);
        var host = uri?.Host ?? "";

        if (!ShouldIntercept(host))
        {
            // Just tunnel
            return;
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

        var body = "";
        if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl) && cl > 0)
        {
            var buf = new byte[cl];
            var read = 0;
            while (read < cl)
            {
                var n = await stream.ReadAsync(buf.AsMemory(read, cl - read), ct);
                if (n == 0) break;
                read += n;
            }
            body = Encoding.UTF8.GetString(buf, 0, read);
        }

        var captured = new CapturedHttpMessage
        {
            Method = method,
            Url = url,
            Host = host,
            RequestHeaders = headers,
            RequestBody = body,
        };
        AddCaptured(captured);
    }

    private async Task TunnelDirectAsync(NetworkStream clientStream, string host, int port, CancellationToken ct)
    {
        // Send 200 to client
        var ok = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
        await clientStream.WriteAsync(ok, ct);

        // Connect upstream
        TcpClient? remote = null;
        try
        {
            if (!string.IsNullOrEmpty(_upstreamProxyHost))
            {
                remote = new TcpClient();
                await remote.ConnectAsync(_upstreamProxyHost, _upstreamProxyPort, ct);
                var ns = remote.GetStream();
                var connectReq = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n";
                await ns.WriteAsync(Encoding.ASCII.GetBytes(connectReq), ct);
                // Read response
                while (true)
                {
                    var l = await ReadLineAsync(ns, ct);
                    if (string.IsNullOrEmpty(l)) break;
                }
                await RelayAsync(clientStream, ns, ct);
            }
            else
            {
                remote = new TcpClient();
                await remote.ConnectAsync(host, port, ct);
                await RelayAsync(clientStream, remote.GetStream(), ct);
            }
        }
        catch { }
        finally
        {
            remote?.Dispose();
        }
    }

    private static async Task RelayAsync(Stream a, Stream b, CancellationToken ct)
    {
        var t1 = CopyAsync(a, b, ct);
        var t2 = CopyAsync(b, a, ct);
        await Task.WhenAny(t1, t2);

        static async Task CopyAsync(Stream from, Stream to, CancellationToken ct2)
        {
            var buf = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                while (!ct2.IsCancellationRequested)
                {
                    var n = await from.ReadAsync(buf, ct2);
                    if (n == 0) break;
                    await to.WriteAsync(buf.AsMemory(0, n), ct2);
                    await to.FlushAsync(ct2);
                }
            }
            catch { }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
    }

    private bool ShouldIntercept(string host)
    {
        if (_interceptDomains.IsEmpty) return true;

        foreach (var domain in _interceptDomains.Keys)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase)) return true;
            if (host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
        Dictionary<string, string> headers, string body)> ReadHttpRequestAsync(Stream stream, CancellationToken ct)
    {
        var firstLine = await ReadLineAsync(stream, ct);
        if (firstLine == null) return (null, null, null, new(), "");

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

        var body = await ReadBodyAsync(stream, headers, ct);
        return (method, path, httpVersion, headers, body);
    }

    private static async Task<(int statusCode, Dictionary<string, string> headers, string body)>
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

        var body = await ReadBodyAsync(stream, headers, ct);
        return (statusCode, headers, body);
    }

    private static async Task<string> ReadBodyAsync(Stream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        byte[] rawBody;

        if (headers.TryGetValue("Transfer-Encoding", out var te) &&
            te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            rawBody = await ReadChunkedBodyAsync(stream, ct);
        }
        else if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl) && cl > 0)
        {
            var buf = new byte[Math.Min(cl, 2 * 1024 * 1024)]; // cap at 2MB
            var read = 0;
            while (read < buf.Length)
            {
                var n = await stream.ReadAsync(buf.AsMemory(read, buf.Length - read), ct);
                if (n == 0) break;
                read += n;
            }
            rawBody = buf[..read];
        }
        else
        {
            return "";
        }

        // Handle gzip/br/deflate
        if (headers.TryGetValue("Content-Encoding", out var ce))
        {
            try
            {
                rawBody = DecompressBody(rawBody, ce);
            }
            catch { /* return raw bytes as string */ }
        }

        return Encoding.UTF8.GetString(rawBody);
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(stream, ct);
            if (sizeLine == null) break;
            var chunkSize = Convert.ToInt32(sizeLine.Trim(), 16);
            if (chunkSize == 0)
            {
                await ReadLineAsync(stream, ct); // trailing CRLF
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
            await ReadLineAsync(stream, ct); // trailing CRLF after chunk
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

    private static byte[] BuildHttpRequest(string method, string path, string httpVersion,
        Dictionary<string, string> headers, string body)
    {
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(path).Append(' ').Append(httpVersion).Append("\r\n");
        foreach (var (k, v) in headers)
            sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("\r\n");

        var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        if (string.IsNullOrEmpty(body)) return headerBytes;

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var result = new byte[headerBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, result, headerBytes.Length, bodyBytes.Length);
        return result;
    }

    private static byte[] BuildHttpResponse(int statusCode, Dictionary<string, string> headers, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(statusCode).Append(" OK\r\n");

        // Rewrite content-length and remove transfer-encoding/content-encoding since we decoded
        var skipHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Transfer-Encoding", "Content-Encoding", "Content-Length" };

        foreach (var (k, v) in headers)
        {
            if (!skipHeaders.Contains(k))
                sb.Append(k).Append(": ").Append(v).Append("\r\n");
        }
        sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        sb.Append("\r\n");

        var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var result = new byte[headerBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, result, headerBytes.Length, bodyBytes.Length);
        return result;
    }

    #endregion
}
