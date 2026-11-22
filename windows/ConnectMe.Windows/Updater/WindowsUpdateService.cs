using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ConnectMe.Windows.Updater;

public record UpdateReleaseInfo(
    string VersionTag,
    string ReleaseTitle,
    string ReleaseNotes,
    string DownloadUrl,
    string ZipFileName,
    long FileSizeBytes
);

public record UpdateCheckResult(
    bool IsSuccess,
    bool HasUpdate,
    UpdateReleaseInfo? UpdateInfo,
    string? ErrorMessage
);

public static class WindowsUpdateService
{
    private const string GITHUB_RELEASES_URL =
        "https://api.github.com/repos/korgangames/Connect-Me/releases?per_page=10";

    private const string GITHUB_LATEST_RELEASE_URL =
        "https://api.github.com/repos/korgangames/Connect-Me/releases/latest";

    private const string GITHUB_WEB_LATEST_URL =
        "https://github.com/korgangames/Connect-Me/releases/latest";

    private const string GITHUB_RAW_VERSION_URL =
        "https://raw.githubusercontent.com/korgangames/Connect-Me/main/VERSION";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static WindowsUpdateService()
    {
        string ver = typeof(WindowsUpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.8.36";
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ConnectMe-Windows", ver));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            string tag = "";
            string title = "";
            string body = "";
            string downloadUrl = "";
            string zipName = "";
            long size = 0;
            string? lastErrorHint = null;

            // Katman 1: GitHub Releases API (Tüm son yayınlar - en yüksek sürümü bulur)
            try
            {
                string url = $"{GITHUB_RELEASES_URL}&_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                using var resp = await _http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var jsonStream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: ct);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        JsonElement? bestElement = null;
                        string bestTag = "";

                        foreach (var rel in doc.RootElement.EnumerateArray())
                        {
                            if (rel.TryGetProperty("draft", out var dProp) && dProp.GetBoolean())
                                continue;

                            string relTag = rel.TryGetProperty("tag_name", out var tProp) ? (tProp.GetString() ?? "") : "";
                            if (string.IsNullOrWhiteSpace(relTag)) continue;

                            if (bestElement == null || CompareVersions(relTag, bestTag) > 0)
                            {
                                bestElement = rel;
                                bestTag = relTag;
                            }
                        }

                        if (bestElement.HasValue)
                        {
                            (tag, title, body, downloadUrl, zipName, size) = ParseReleaseElement(bestElement.Value);
                        }
                    }
                }
                else
                {
                    lastErrorHint = $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})";
                }
            }
            catch (Exception ex)
            {
                lastErrorHint = ex.Message;
            }

            // Katman 2: GitHub Releases Latest API (Tekil en son sürüm)
            if (string.IsNullOrWhiteSpace(tag))
            {
                try
                {
                    string url = $"{GITHUB_LATEST_RELEASE_URL}?_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                    using var resp = await _http.GetAsync(url, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var jsonStream = await resp.Content.ReadAsStreamAsync(ct);
                        using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: ct);
                        (tag, title, body, downloadUrl, zipName, size) = ParseReleaseElement(doc.RootElement);
                    }
                    else if (lastErrorHint == null)
                    {
                        lastErrorHint = $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})";
                    }
                }
                catch (Exception ex)
                {
                    lastErrorHint = ex.Message;
                }
            }

            // Katman 3: GitHub Web Yönlendirmesi (API Hız Limitlerini Aşar)
            if (string.IsNullOrWhiteSpace(tag))
            {
                string? webTag = await GetLatestTagFromWebRedirectAsync(ct);
                if (!string.IsNullOrEmpty(webTag))
                {
                    tag = webTag;
                    title = $"Connect Me {tag}";
                    string cleanTag = tag.TrimStart('v', 'V');
                    zipName = $"ConnectMe-Windows-x64-v{cleanTag}.zip";
                    downloadUrl = $"https://github.com/korgangames/Connect-Me/releases/download/{tag}/ConnectMe-Windows-x64.zip";
                }
            }

            // Katman 4: GitHub Raw VERSION Dosyası (Sıfır Limit Kesin Geri Dönüş)
            if (string.IsNullOrWhiteSpace(tag))
            {
                string? rawVer = await GetLatestVersionFromRawAsync(ct);
                if (!string.IsNullOrEmpty(rawVer))
                {
                    string clean = rawVer.Trim().TrimStart('v', 'V');
                    tag = $"v{clean}";
                    title = $"Connect Me {tag}";
                    zipName = $"ConnectMe-Windows-x64-v{clean}.zip";
                    downloadUrl = $"https://github.com/korgangames/Connect-Me/releases/download/{tag}/ConnectMe-Windows-x64.zip";
                }
            }

            if (string.IsNullOrWhiteSpace(tag))
            {
                string msg = lastErrorHint != null
                    ? $"Güncelleme sunucusuna erişilemedi ({lastErrorHint})."
                    : "GitHub üzerinde geçerli bir sürüm etiketi bulunamadı.";
                return new UpdateCheckResult(false, false, null, msg);
            }

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                string cleanTag = tag.TrimStart('v', 'V');
                downloadUrl = $"https://github.com/korgangames/Connect-Me/releases/download/{tag}/ConnectMe-Windows-x64.zip";
                zipName = $"ConnectMe-Windows-x64-v{cleanTag}.zip";
            }

            var updateInfo = new UpdateReleaseInfo(tag, title, body, downloadUrl, zipName, size);
            bool hasUpdate = IsNewerVersion(tag, currentVersion);

            return new UpdateCheckResult(true, hasUpdate, updateInfo, null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, null, $"Güncelleme denetimi sırasında hata oluştu: {ex.Message}");
        }
    }

    private static (string tag, string title, string body, string downloadUrl, string zipName, long size) ParseReleaseElement(JsonElement root)
    {
        string tag = root.TryGetProperty("tag_name", out var tagProp) ? (tagProp.GetString() ?? "") : "";
        string title = root.TryGetProperty("name", out var tProp) ? (tProp.GetString() ?? tag) : tag;
        string body = root.TryGetProperty("body", out var bProp) ? (bProp.GetString() ?? "") : "";
        string downloadUrl = "";
        string zipName = "";
        long size = 0;

        string cleanTag = tag.TrimStart('v', 'V');

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            string fallbackUrl = "";
            string fallbackName = "";
            long fallbackSize = 0;

            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "") : "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    if (name.Contains(cleanTag, StringComparison.OrdinalIgnoreCase) || name.Contains(tag, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var uProp) ? (uProp.GetString() ?? "") : "";
                        zipName = name;
                        size = asset.TryGetProperty("size", out var sProp) ? sProp.GetInt64() : 0;
                        break;
                    }
                    else if (string.IsNullOrEmpty(fallbackUrl))
                    {
                        fallbackUrl = asset.TryGetProperty("browser_download_url", out var uProp) ? (uProp.GetString() ?? "") : "";
                        fallbackName = name;
                        fallbackSize = asset.TryGetProperty("size", out var sProp) ? sProp.GetInt64() : 0;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(fallbackUrl))
            {
                downloadUrl = fallbackUrl;
                zipName = fallbackName;
                size = fallbackSize;
            }
        }

        if (string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrWhiteSpace(tag))
        {
            downloadUrl = $"https://github.com/korgangames/Connect-Me/releases/download/{tag}/ConnectMe-Windows-x64.zip";
            zipName = $"ConnectMe-Windows-x64-v{cleanTag}.zip";
        }

        return (tag, title, body, downloadUrl, zipName, size);
    }

    private static async Task<string?> GetLatestTagFromWebRedirectAsync(CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            using var req = new HttpRequestMessage(HttpMethod.Head, GITHUB_WEB_LATEST_URL);
            using var resp = await client.SendAsync(req, ct);
            if (resp.Headers.Location != null)
            {
                string loc = resp.Headers.Location.ToString();
                int idx = loc.LastIndexOf('/');
                if (idx >= 0 && idx < loc.Length - 1)
                {
                    return loc[(idx + 1)..].Trim();
                }
            }
        }
        catch { }
        return null;
    }

    private static async Task<string?> GetLatestVersionFromRawAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{GITHUB_RAW_VERSION_URL}?_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                string raw = await resp.Content.ReadAsStringAsync(ct);
                return raw.Trim();
            }
        }
        catch { }
        return null;
    }

    public static async Task DownloadAndApplyUpdateAsync(
        UpdateReleaseInfo update,
        IProgress<(int percent, long downloaded, long total)>? progress,
        CancellationToken ct = default)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ConnectMeUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string zipPath = Path.Combine(tempDir, update.ZipFileName);

        // İndirme
        using (var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.FileSizeBytes;
            using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            byte[] buffer = new byte[16384];
            long totalRead = 0;
            int read;
            int lastPercent = -1;

            while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;
                if (total > 0)
                {
                    int pct = (int)((totalRead * 100) / total);
                    if (pct != lastPercent)
                    {
                        lastPercent = pct;
                        progress?.Report((pct, totalRead, total));
                    }
                }
            }
        }

        // Çıkartma
        string extractDir = Path.Combine(tempDir, "extracted");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        string? newExe = Directory.GetFiles(extractDir, "ConnectMe.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (newExe == null)
        {
            newExe = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
        }

        if (newExe == null)
        {
            throw new FileNotFoundException("İndirilen arşiv içinde çalıştırılabilir ConnectMe dosyası bulunamadı.");
        }

        // Mevcut çalışan exe yolu
        string currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(currentExe))
        {
            throw new InvalidOperationException("Mevcut uygulamanın konumu belirlenemedi.");
        }

        string? currentDir = Path.GetDirectoryName(currentExe);
        string newVersionFile = Path.Combine(Path.GetDirectoryName(newExe) ?? extractDir, "VERSION");

        // Güncelleme komut betiği (.cmd) - Ping ile bekleme ve kilit kalkana kadar döngü
        string cmdPath = Path.Combine(tempDir, "apply_update.cmd");
        string cmdContent = $@"@echo off
setlocal
set ""SRC={newExe}""
set ""DST={currentExe}""
set ""SRC_VER={newVersionFile}""
set ""DST_DIR={currentDir}""
set /a ATTEMPTS=0

:retry
set /a ATTEMPTS+=1
if %ATTEMPTS% gtr 40 goto fail

ping 127.0.0.1 -n 2 >nul
copy /y ""%SRC%"" ""%DST%"" >nul 2>&1
if errorlevel 1 goto retry

if exist ""%SRC_VER%"" copy /y ""%SRC_VER%"" ""%DST_DIR%\VERSION"" >nul 2>&1

start """" ""%DST%""
goto cleanup

:fail
start """" ""%DST%""

:cleanup
del /f /q ""%~f0"" >nul 2>&1
";
        await File.WriteAllTextAsync(cmdPath, cmdContent, ct);

        // Arka planda bağımsız kabuk olarak çalıştır ve uygulamayı kapat
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{cmdPath}\"",
            CreateNoWindow = true,
            UseShellExecute = true
        });

        Environment.Exit(0);
    }

    public static bool IsNewerVersion(string remoteTag, string currentVersion)
    {
        return CompareVersions(remoteTag, currentVersion) > 0;
    }

    public static int CompareVersions(string vA, string vB)
    {
        try
        {
            static string Clean(string ver)
            {
                if (string.IsNullOrWhiteSpace(ver)) return "0";
                var s = ver.Trim().TrimStart('v', 'V');
                int spaceIdx = s.IndexOf(' ');
                if (spaceIdx >= 0) s = s[..spaceIdx];
                int dashIdx = s.IndexOf('-');
                if (dashIdx >= 0) s = s[..dashIdx];
                int plusIdx = s.IndexOf('+');
                if (plusIdx >= 0) s = s[..plusIdx];
                return s.Trim();
            }

            var cleanA = Clean(vA);
            var cleanB = Clean(vB);

            var partsA = cleanA.Split('.').Select(p => {
                string digits = new string(p.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, out var v) ? v : 0;
            }).ToList();

            var partsB = cleanB.Split('.').Select(p => {
                string digits = new string(p.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, out var v) ? v : 0;
            }).ToList();

            int maxLen = Math.Max(partsA.Count, partsB.Count);
            for (int i = 0; i < maxLen; i++)
            {
                int a = i < partsA.Count ? partsA[i] : 0;
                int b = i < partsB.Count ? partsB[i] : 0;
                if (a > b) return 1;
                if (a < b) return -1;
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }
}
