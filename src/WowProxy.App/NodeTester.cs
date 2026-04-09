using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using WowProxy.App.Models;
using WowProxy.Core.SingBox;

namespace WowProxy.App;

internal static class NodeTester
{
    private const int BatchSize = 20;
    private const int BasePort = 20000;
    private const string TestUrl = "http://cp.cloudflare.com/"; // 204 No Content
    private const string SpeedTestUrl = "https://speed.cloudflare.com/__down?bytes=10000000"; // 10MB

    public static async Task TestLatencyAsync(IEnumerable<ProxyNodeModel> nodes, string singBoxPath)
    {
        var allNodes = nodes.ToList();
        var chunks = allNodes.Chunk(BatchSize).ToList();

        foreach (var chunk in chunks)
        {
            await TestBatchAsync(chunk, singBoxPath, isSpeedTest: false);
        }
    }

    public static async Task TestSpeedAsync(IEnumerable<ProxyNodeModel> nodes, string singBoxPath)
    {
        var allNodes = nodes.ToList();
        var chunks = allNodes.Chunk(BatchSize).ToList();

        foreach (var chunk in chunks)
        {
            await TestBatchAsync(chunk, singBoxPath, isSpeedTest: true);
        }
    }

    private static async Task TestBatchAsync(ProxyNodeModel[] nodes, string singBoxPath, bool isSpeedTest)
    {
        var factory = new SingBoxConfigFactory();
        var domainNodes = nodes.Select(n => n.Node).ToList();
        var configJson = factory.BuildBatch(domainNodes, BasePort);
        
        var tempConfigFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempConfigFile, configJson);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = singBoxPath,
            Arguments = $"run -c \"{tempConfigFile}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        try
        {
            process.Start();
            var isReady = await WaitForPortListeningAsync(BasePort, process, TimeSpan.FromSeconds(5));
            if (!isReady)
            {
                await MarkBatchFailedAsync(nodes, isSpeedTest);
                return;
            }

            var tasks = new List<Task>();
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i];
                var port = BasePort + i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (isSpeedTest)
                        {
                            await MeasureSpeed(node, port);
                        }
                        else
                        {
                            await MeasureLatency(node, port);
                        }
                    }
                    catch
                    {
                        if (isSpeedTest)
                        {
                            await SetSpeedAsync(node, -1);
                        }
                        else
                        {
                            await SetLatencyAsync(node, -1);
                        }
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }
        catch
        {
            await MarkBatchFailedAsync(nodes, isSpeedTest);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync();
                }
            }
            catch
            {
            }
            
            try { File.Delete(tempConfigFile); } catch { }
        }
    }

    private static async Task MeasureLatency(ProxyNodeModel node, int port)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var sw = Stopwatch.StartNew();
        using var response = await client.GetAsync(TestUrl, HttpCompletionOption.ResponseHeadersRead);
        sw.Stop();

        if (response.IsSuccessStatusCode)
        {
            await SetLatencyAsync(node, (int)sw.ElapsedMilliseconds);
        }
        else
        {
            await SetLatencyAsync(node, -1);
        }
    }

    private static async Task MeasureSpeed(ProxyNodeModel node, int port)
    {
        // If latency is bad, skip speed test to save time
        if (node.Latency == -1)
        {
            await SetSpeedAsync(node, 0);
            return;
        }

        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        var sw = Stopwatch.StartNew();
        using var response = await client.GetAsync(SpeedTestUrl, HttpCompletionOption.ResponseHeadersRead);
        
        if (!response.IsSuccessStatusCode)
        {
            await SetSpeedAsync(node, 0);
            return;
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[8192];
        long totalBytes = 0;
        var readSw = Stopwatch.StartNew();
        
        // Read for up to 5 seconds
        while (readSw.Elapsed.TotalSeconds < 5)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0) break;
            totalBytes += read;
        }
        readSw.Stop();

        // MB/s
        var seconds = readSw.Elapsed.TotalSeconds;
        if (seconds > 0)
        {
            var mb = totalBytes / 1024.0 / 1024.0;
            await SetSpeedAsync(node, Math.Round(mb / seconds, 2));
        }
    }

    private static async Task<bool> WaitForPortListeningAsync(int port, Process process, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            if (process.HasExited)
            {
                return false;
            }

            try
            {
                using var tcp = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
                return true;
            }
            catch
            {
                await Task.Delay(100);
            }
        }

        return false;
    }

    private static async Task MarkBatchFailedAsync(IEnumerable<ProxyNodeModel> nodes, bool isSpeedTest)
    {
        var tasks = nodes.Select(node => isSpeedTest
            ? SetSpeedAsync(node, -1)
            : SetLatencyAsync(node, -1));
        await Task.WhenAll(tasks);
    }

    private static Task SetLatencyAsync(ProxyNodeModel node, int? value)
        => RunOnUiThreadAsync(() => node.Latency = value);

    private static Task SetSpeedAsync(ProxyNodeModel node, double? value)
        => RunOnUiThreadAsync(() => node.Speed = value);

    private static Task RunOnUiThreadAsync(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is null || app.Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return app.Dispatcher.InvokeAsync(action).Task;
    }
}
