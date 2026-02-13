using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace WowProxy.App;

internal static class Bootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try
                {
                    if (e.ExceptionObject is Exception ex)
                    {
                        WriteStartupLog("UnhandledException", ex.ToString());
                    }
                    else
                    {
                        WriteStartupLog("UnhandledException", "Unknown exception object.");
                    }
                }
                catch
                {
                }
            };

            WriteStartupLog("Start", "Process loaded.");
        }
        catch
        {
        }
    }

    internal static void WriteStartupLog(string stage, string message)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WowProxy", "logs");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "last-startup.log");
        var text = new StringBuilder()
            .Append("Time: ").AppendLine(DateTimeOffset.Now.ToString("O"))
            .Append("Stage: ").AppendLine(stage)
            .Append("Process: ").AppendLine(Environment.ProcessPath ?? string.Empty)
            .Append("PID: ").AppendLine(Environment.ProcessId.ToString())
            .Append("Message: ").AppendLine(message)
            .AppendLine();
        File.AppendAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

