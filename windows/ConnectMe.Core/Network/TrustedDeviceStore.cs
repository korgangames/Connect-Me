using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConnectMe.Core.Protocol;

namespace ConnectMe.Core.Network;

/// <summary>
/// Persistent record for a device that has been marked as "Trusted" / "Remembered".
/// When both devices possess matching trust credentials, they automatically reconnect
/// whenever both programs are running on the local network without requiring manual PIN entry.
/// </summary>
public sealed class TrustedDeviceRecord
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("trustToken")]
    public string TrustToken { get; set; } = string.Empty;

    [JsonPropertyName("assignedEdge")]
    public ScreenEdge AssignedEdge { get; set; } = ScreenEdge.Right;

    [JsonPropertyName("edgeOffsetStart")]
    public float EdgeOffsetStart { get; set; } = 0.0f;

    [JsonPropertyName("edgeOffsetEnd")]
    public float EdgeOffsetEnd { get; set; } = 1.0f;

    [JsonPropertyName("attachedLocalMonitorId")]
    public string AttachedLocalMonitorId { get; set; } = string.Empty;

    [JsonPropertyName("canvasX")]
    public double CanvasX { get; set; }

    [JsonPropertyName("canvasY")]
    public double CanvasY { get; set; }

    [JsonPropertyName("hasCustomCanvasPosition")]
    public bool HasCustomCanvasPosition { get; set; }

    [JsonPropertyName("autoConnect")]
    public bool AutoConnect { get; set; } = true;

    [JsonPropertyName("trustedAt")]
    public DateTimeOffset TrustedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("lastConnectedAt")]
    public DateTimeOffset LastConnectedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Thread-safe persistent storage manager for trusted devices.
/// Saves and loads trusted device credentials to a local JSON file.
/// </summary>
public sealed class TrustedDeviceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, TrustedDeviceRecord> _trustedDevices = new(StringComparer.OrdinalIgnoreCase);

    public string StorageFilePath { get; }

    public TrustedDeviceStore(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            StorageFilePath = customPath;
        }
        else
        {
            string baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(baseFolder))
            {
                baseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            string dir = Path.Combine(baseFolder, "ConnectMe");
            StorageFilePath = Path.Combine(dir, "trusted_devices.json");
        }

        Load();
    }

    public static string GenerateTrustToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }

    public static string GetOrCreatePersistentDeviceId()
    {
        try
        {
            string baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(baseFolder))
            {
                baseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            string dir = Path.Combine(baseFolder, "ConnectMe");
            Directory.CreateDirectory(dir);
            string idFile = Path.Combine(dir, "device_id.txt");
            if (File.Exists(idFile))
            {
                string id = File.ReadAllText(idFile).Trim();
                if (!string.IsNullOrWhiteSpace(id) && id.Length >= 6)
                    return id;
            }
            string cleanMachine = Environment.MachineName.ToLowerInvariant();
            cleanMachine = System.Text.RegularExpressions.Regex.Replace(cleanMachine, @"[^a-z0-9]", "");
            if (cleanMachine.Length > 8) cleanMachine = cleanMachine[..8];
            string newId = $"win-{cleanMachine}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
            File.WriteAllText(idFile, newId);
            return newId;
        }
        catch
        {
            return $"win-{Environment.MachineName.ToLowerInvariant()}";
        }
    }

    public void Load()
    {
        lock (_sync)
        {
            _trustedDevices.Clear();
            if (!File.Exists(StorageFilePath))
                return;

            try
            {
                string json = File.ReadAllText(StorageFilePath);
                var list = JsonSerializer.Deserialize<List<TrustedDeviceRecord>>(json, JsonOptions);
                if (list != null)
                {
                    foreach (var rec in list)
                    {
                        if (!string.IsNullOrWhiteSpace(rec.DeviceId))
                        {
                            _trustedDevices[rec.DeviceId] = rec;
                        }
                    }
                }
            }
            catch
            {
                // Fallback on corrupt file
            }
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                string? dir = Path.GetDirectoryName(StorageFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var list = _trustedDevices.Values.ToList();
                string json = JsonSerializer.Serialize(list, JsonOptions);
                File.WriteAllText(StorageFilePath, json);
            }
            catch
            {
                // Ignore transient write error
            }
        }
    }

    public bool IsDeviceTrusted(string deviceId, out TrustedDeviceRecord? record)
    {
        lock (_sync)
        {
            if (_trustedDevices.TryGetValue(deviceId, out var rec) && rec.AutoConnect)
            {
                record = rec;
                return true;
            }

            record = null;
            return false;
        }
    }

    public bool VerifyTrustToken(string deviceId, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        lock (_sync)
        {
            if (_trustedDevices.TryGetValue(deviceId, out var rec))
            {
                return string.Equals(rec.TrustToken, token.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
    }

    public void AddOrUpdateTrustedDevice(TrustedDeviceRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.DeviceId))
            return;

        lock (_sync)
        {
            record.LastConnectedAt = DateTimeOffset.UtcNow;
            _trustedDevices[record.DeviceId] = record;
            Save();
        }
    }

    public bool RevokeTrust(string deviceId)
    {
        lock (_sync)
        {
            bool removed = _trustedDevices.Remove(deviceId);
            if (removed)
            {
                Save();
            }
            return removed;
        }
    }

    public IReadOnlyList<TrustedDeviceRecord> GetAllTrustedDevices()
    {
        lock (_sync)
        {
            return _trustedDevices.Values.ToList();
        }
    }
}
