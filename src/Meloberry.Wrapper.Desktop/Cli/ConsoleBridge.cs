using System.Runtime.InteropServices;
using System.Text;

namespace Meloberry.Wrapper.Desktop.Cli;

/// <summary>Attaches a WinExe GUI process to its parent console when invoked via `mby-pc.bat --cli`.</summary>
internal static class ConsoleBridge
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    public static void AttachParentIfInteractive()
    {
        try
        {
            if (!Console.IsOutputRedirected && !Console.IsErrorRedirected && !Console.IsInputRedirected)
            {
                AttachConsole(AttachParentProcess);
            }

            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Continue gracefully when no parent console exists or streams are redirected.
        }
    }
}
