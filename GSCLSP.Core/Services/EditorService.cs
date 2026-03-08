using System.Diagnostics;
using GSCLSP.Core.Models;

namespace GSCLSP.Core.Services;

public static class EditorService
{
    public static void OpenAtLocation(GscSymbol symbol)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "code",
            Arguments = $"-g \"{symbol.FilePath}:{symbol.LineNumber}\"",
            UseShellExecute = true,
            CreateNoWindow = true
        };

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Could not open VS Code: {ex.Message}");
            Console.WriteLine("Make sure 'code' is in your Windows PATH.");
        }
    }
}