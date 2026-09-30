using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConnectMe.Core.Protocol;

namespace ConnectMe.Core.Topology;

/// <summary>
/// Cross-platform multi-monitor layout detector & parser for Linux (Nobara KDE Plasma Wayland,
/// wlroots/Sway/Hyprland, X11/XWayland) and generic fallback environments.
/// Windows uses Win32 EnumDisplayMonitors + GetMonitorInfoW + GetDpiForMonitor in Win32MonitorEnumerator,
/// while Linux uses KScreen (kscreen-doctor -j), wlr-randr (--json), or xrandr (--query).
/// </summary>
public static partial class CrossPlatformMonitorDetector
{
    private static readonly Regex XRandrConnectedRegex = new(
        @"^(?<name>[\w\-\.]+)\s+connected\s+(?<primary>primary\s+)?(?<w>\d+)x(?<h>\d+)(?<x>[+\-]\d+)(?<y>[+\-]\d+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Detects all active physical monitors on Linux (KDE Plasma Wayland, wlroots, or X11/XWayland).
    /// </summary>
    public static List<PhysicalMonitorDescriptor> DetectLinuxMonitors()
    {
        // 1. Try KDE Plasma Wayland / X11 native KScreen JSON output (Nobara KDE default)
        string? kscreenJson = TryRunCommand("kscreen-doctor", "-j");
        if (!string.IsNullOrWhiteSpace(kscreenJson))
        {
            var kscreenMonitors = ParseKScreenDoctorJson(kscreenJson);
            if (kscreenMonitors.Count > 0)
                return kscreenMonitors;
        }

        // 2. Try wlroots / Sway / Hyprland JSON output
        string? wlrJson = TryRunCommand("wlr-randr", "--json");
        if (!string.IsNullOrWhiteSpace(wlrJson))
        {
            var wlrMonitors = ParseWlrRandrJson(wlrJson);
            if (wlrMonitors.Count > 0)
                return wlrMonitors;
        }

        // 3. Try XRandR (works on both X11 and XWayland sessions)
        string? xrandrOut = TryRunCommand("xrandr", "--query");
        if (!string.IsNullOrWhiteSpace(xrandrOut))
        {
            var xrandrMonitors = ParseXRandrOutput(xrandrOut);
            if (xrandrMonitors.Count > 0)
                return xrandrMonitors;
        }

        // 4. Fallback default monitor if headless or tools unavailable
        return
        [
            new PhysicalMonitorDescriptor
            {
                MonitorId = "DP-1",
                Name = "Linux Birincil Ekran (DP-1)",
                VirtualX = 0,
                VirtualY = 0,
                Width = 1920,
                Height = 1080,
                ScaleFactor = 1.0,
                IsPrimary = true
            }
        ];
    }

    /// <summary>
    /// Parses the JSON output of KDE Plasma's <c>kscreen-doctor -j</c> command.
    /// Supports both KDE Plasma 5 (<c>primary: bool</c>) and KDE Plasma 6 (<c>priority: 1</c>) schemas.
    /// </summary>
    public static List<PhysicalMonitorDescriptor> ParseKScreenDoctorJson(string json)
    {
        var result = new List<PhysicalMonitorDescriptor>();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            // Strip ANSI escape codes if kscreen-doctor emitted any
            string cleanJson = Regex.Replace(json, @"\x1B\[[0-9;]*[a-zA-Z]", string.Empty);
            int firstBrace = cleanJson.IndexOf('{');
            if (firstBrace > 0)
                cleanJson = cleanJson[firstBrace..];

            using var doc = JsonDocument.Parse(cleanJson);
            if (!doc.RootElement.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array)
                return result;

            int index = 0;
            foreach (var output in outputs.EnumerateArray())
            {
                bool connected = !output.TryGetProperty("connected", out var connProp) || connProp.GetBoolean();
                bool enabled = !output.TryGetProperty("enabled", out var enProp) || enProp.GetBoolean();
                if (!connected || !enabled)
                    continue;

                index++;
                string name = output.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString() ?? $"DP-{index}"
                    : $"DP-{index}";

                int vx = 0, vy = 0;
                if (output.TryGetProperty("pos", out var posProp) && posProp.ValueKind == JsonValueKind.Object)
                {
                    if (posProp.TryGetProperty("x", out var px)) vx = px.GetInt32();
                    if (posProp.TryGetProperty("y", out var py)) vy = py.GetInt32();
                }

                int w = 1920, h = 1080;
                if (output.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == JsonValueKind.Object)
                {
                    if (sizeProp.TryGetProperty("width", out var sw)) w = sw.GetInt32();
                    if (sizeProp.TryGetProperty("height", out var sh)) h = sh.GetInt32();
                }

                double scale = 1.0;
                if (output.TryGetProperty("scale", out var scaleProp) && scaleProp.ValueKind == JsonValueKind.Number)
                {
                    scale = Math.Max(0.5, scaleProp.GetDouble());
                }

                bool isPrimary = false;
                if (output.TryGetProperty("primary", out var primProp) && primProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    isPrimary = primProp.GetBoolean();
                }
                else if (output.TryGetProperty("priority", out var prioProp) && prioProp.ValueKind == JsonValueKind.Number)
                {
                    isPrimary = prioProp.GetInt32() == 1;
                }

                result.Add(new PhysicalMonitorDescriptor
                {
                    MonitorId = name,
                    Name = $"Monitör {index} ({name})",
                    VirtualX = vx,
                    VirtualY = vy,
                    Width = Math.Max(320, w),
                    Height = Math.Max(240, h),
                    ScaleFactor = scale,
                    IsPrimary = isPrimary
                });
            }

            if (result.Count > 0 && !result.Any(m => m.IsPrimary))
            {
                result[0].IsPrimary = true;
            }
        }
        catch
        {
            // Ignore malformed JSON
        }

        return result;
    }

    /// <summary>
    /// Parses the JSON output of <c>wlr-randr --json</c> for wlroots-based Wayland compositors.
    /// </summary>
    public static List<PhysicalMonitorDescriptor> ParseWlrRandrJson(string json)
    {
        var result = new List<PhysicalMonitorDescriptor>();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            int index = 0;
            foreach (var output in doc.RootElement.EnumerateArray())
            {
                bool enabled = !output.TryGetProperty("enabled", out var enProp) || enProp.GetBoolean();
                if (!enabled)
                    continue;

                index++;
                string name = output.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString() ?? $"WL-{index}"
                    : $"WL-{index}";

                int vx = 0, vy = 0;
                if (output.TryGetProperty("position", out var posProp) && posProp.ValueKind == JsonValueKind.Object)
                {
                    if (posProp.TryGetProperty("x", out var px)) vx = px.GetInt32();
                    if (posProp.TryGetProperty("y", out var py)) vy = py.GetInt32();
                }

                int w = 1920, h = 1080;
                if (output.TryGetProperty("modes", out var modesProp) && modesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mode in modesProp.EnumerateArray())
                    {
                        if (mode.TryGetProperty("current", out var cur) && cur.GetBoolean())
                        {
                            if (mode.TryGetProperty("width", out var mw)) w = mw.GetInt32();
                            if (mode.TryGetProperty("height", out var mh)) h = mh.GetInt32();
                            break;
                        }
                    }
                }

                double scale = output.TryGetProperty("scale", out var scProp) && scProp.ValueKind == JsonValueKind.Number
                    ? Math.Max(0.5, scProp.GetDouble())
                    : 1.0;

                result.Add(new PhysicalMonitorDescriptor
                {
                    MonitorId = name,
                    Name = $"Monitör {index} ({name})",
                    VirtualX = vx,
                    VirtualY = vy,
                    Width = w,
                    Height = h,
                    ScaleFactor = scale,
                    IsPrimary = index == 1
                });
            }
        }
        catch
        {
            // Ignore malformed JSON
        }

        return result;
    }

    /// <summary>
    /// Parses <c>xrandr --query</c> text output into <see cref="PhysicalMonitorDescriptor"/> entries.
    /// Supports negative offsets (e.g. <c>DP-2 connected 1080x1920-1080+0</c>) and primary flags.
    /// </summary>
    public static List<PhysicalMonitorDescriptor> ParseXRandrOutput(string xrandrText)
    {
        var result = new List<PhysicalMonitorDescriptor>();
        if (string.IsNullOrWhiteSpace(xrandrText))
            return result;

        var matches = XRandrConnectedRegex.Matches(xrandrText);
        int index = 0;
        foreach (Match match in matches)
        {
            if (!match.Success)
                continue;

            index++;
            string name = match.Groups["name"].Value;
            bool isPrimary = match.Groups["primary"].Success && !string.IsNullOrWhiteSpace(match.Groups["primary"].Value);
            int w = int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture);
            int h = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            int vx = int.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture);
            int vy = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);

            result.Add(new PhysicalMonitorDescriptor
            {
                MonitorId = name,
                Name = $"Monitör {index} ({name})",
                VirtualX = vx,
                VirtualY = vy,
                Width = Math.Max(320, w),
                Height = Math.Max(240, h),
                ScaleFactor = 1.0,
                IsPrimary = isPrimary
            });
        }

        if (result.Count > 0 && !result.Any(m => m.IsPrimary))
        {
            result[0].IsPrimary = true;
        }

        return result;
    }

    private static string? TryRunCommand(string fileName, string arguments)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1500);
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
