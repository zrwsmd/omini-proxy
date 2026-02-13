using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using WowProxy.App.Models;
using WowProxy.Core.SingBox;
using WowProxy.Infrastructure;

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
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            process.Start();
            // Wait a bit for sing-box to start listening
            await Task.Delay(1000);

            if (process.HasExited)
            {
                // Failed to start
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
                        if (isSpeedTest) node.Speed = -1;
                        else node.Latency = -1;
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
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
            node.Latency = (int)sw.ElapsedMilliseconds;
        }
        else
        {
            node.Latency = -1;
        }
    }

    private static async Task MeasureSpeed(ProxyNodeModel node, int port)
    {
        // If latency is bad, skip speed test to save time
        if (node.Latency == -1)
        {
            node.Speed = 0;
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
            node.Speed = 0;
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
            node.Speed = Math.Round(mb / seconds, 2);
        }
    }
}
