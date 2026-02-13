using System.Net;
using System.Net.Sockets;
using WowProxy.Core.SingBox;
using WowProxy.Domain;

static int GetFreeTcpPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

var singBoxPath =
    args.Length > 0
        ? args[0]
        : Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "tmp",
            "sing-box",
            "sing-box-1.12.21-windows-amd64",
            "sing-box.exe"
        );

singBoxPath = Path.GetFullPath(singBoxPath);

if (!File.Exists(singBoxPath))
{
    Console.Error.WriteLine($"sing-box.exe 不存在：{singBoxPath}");
    return 2;
}

var port = GetFreeTcpPort();
var settings = new AppSettings(
    SingBoxPath: singBoxPath,
    MixedPort: port,
    EnableClashApi: false,
    ClashApiPort: 9090,
    ClashApiSecret: null,
    EnableSystemProxy: false
);

var workDir = Path.Combine(Path.GetTempPath(), "WowProxySelfTest", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workDir);
var configPath = Path.Combine(workDir, "config.json");

var configFactory = new SingBoxConfigFactory();
await configFactory.WriteAsync(settings, configPath);

await using var core = new SingBoxCoreAdapter(singBoxPath);
core.LogReceived += (_, line) => Console.WriteLine($"[{line.Level}] {line.Line}");
core.RuntimeInfoChanged += (_, info) => Console.WriteLine($"[STATE] {info.State} {info.LastError}");

var check = await core.CheckConfigAsync(configPath, workDir);
if (!check.IsOk)
{
    Console.Error.WriteLine(check.Stderr);
    return 3;
}

await core.StartAsync(new WowProxy.Core.Abstractions.CoreStartOptions(workDir, configPath));
await Task.Delay(500);

try
{
    var handler = new HttpClientHandler
    {
        Proxy = new WebProxy($"http://127.0.0.1:{port}"),
        UseProxy = true,
    };

    using var http = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    using var resp = await http.GetAsync("http://example.com/");
    Console.WriteLine($"[SELFTEST] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
    return resp.IsSuccessStatusCode ? 0 : 4;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[SELFTEST] 请求失败：{ex.Message}");
    return 5;
}
finally
{
    await core.StopAsync();
}
