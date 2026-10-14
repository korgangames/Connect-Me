using System.Diagnostics;
using System.IO;

namespace ConnectMe.Windows.Win32;

/// <summary>
/// Configures Windows Defender Firewall rules silently on startup
/// to guarantee incoming TCP (42851) and UDP (42849, 42850, 42852) packets
/// from Linux and Android LAN peers are never blocked.
/// </summary>
public static class WindowsFirewallHelper
{
    private static bool _executed;

    public static void EnsureFirewallRules()
    {
        if (_executed) return;
        _executed = true;

        Task.Run(() =>
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    RunNetsh($"advfirewall firewall add rule name=\"Connect Me App\" dir=in action=allow program=\"{exePath}\" enable=yes profile=any");
                }

                RunNetsh("advfirewall firewall add rule name=\"Connect Me TCP Control\" dir=in action=allow protocol=TCP localport=42851 enable=yes profile=any");
                RunNetsh("advfirewall firewall add rule name=\"Connect Me UDP Mesh\" dir=in action=allow protocol=UDP localport=42849,42850,42852 enable=yes profile=any");
            }
            catch
            {
                // Silently ignore if non-admin or restricted
            }
        });
    }

    private static void RunNetsh(string args)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };
            proc.Start();
            proc.WaitForExit(3000);
        }
        catch { }
    }
}
