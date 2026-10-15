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
    private const string GITHUB_LATEST_RELEASE_URL =
        "https://api.github.com/repos/korgangames/Connect-Me/releases/latest";

    private const string GITHUB_WEB_LATEST_URL =
        "https://github.com/korgangames/Connect-Me/releases/latest";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static WindowsUpdateService()
    {
        string ver = typeof(WindowsUpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.8.5";
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

            string url = $"{GITHUB_LATEST_RELEASE_URL}?_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            using var resp = await _http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                var jsonStream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: ct);
                var root = doc.RootElement;

                tag = root.GetProperty("tag_name").GetString() ?? "";
                title = root.TryGetProperty("name", out var tProp) ? (tProp.GetString() ?? tag) : tag;
                body = root.TryGetProperty("body", out var bProp) ? (bProp.GetString() ?? "") : "";

                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    string fallbackUrl = "";
                    string fallbackName = "";
                    long fallbackSize = 0;
                    string cleanTag = tag.TrimStart('v', 'V');

                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            if (name.Contains(cleanTag, StringComparison.OrdinalIgnoreCase) || name.Contains(tag, StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                                zipName = name;
                                size = asset.TryGetProperty("size", out var sProp) ? sProp.GetInt64() : 0;
                                break;
                            }
                            else if (string.IsNullOrEmpty(fallbackUrl))
                            {
                                fallbackUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
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
            }
            else
            {
                // Fallback: Check GitHub web redirect (bypasses GitHub API rate limits e.g. 403 Forbidden)
                string? webTag = await GetLatestTagFromWebRedirectAsync(ct);
                if (!string.IsNullOrEmpty(webTag))
                {
                    tag = webTag;
                    title = $"Connect Me {tag}";
                    string cleanTag = tag.TrimStart('v', 'V');
                    zipName = $"ConnectMe-Windows-x64-v{cleanTag}.zip";
                    downloadUrl = $"https://github.com/korgangames/Connect-Me/releases/download/{tag}/ConnectMe-Windows-x64.zip";
                }
                else
                {
                    int code = (int)resp.StatusCode;
                    string hint = code == 404
                        ? "GitHub 404 Not Found döndürdü. Depo (korgangames/Connect-Me) Private (Gizli) ayarlanmış olabilir. Güncellemelerin cihazlar tarafından görülebilmesi için deponun 'Public' (Açık) olması gerekir."
                        : $"GitHub API HTTP {code} ({resp.ReasonPhrase}) hatası döndürdü.";
                    return new UpdateCheckResult(false, false, null, hint);
                }
            }

            if (string.IsNullOrWhiteSpace(tag))
                return new UpdateCheckResult(false, false, null, "Sürüm etiketi (tag_name) bulunamadı.");

            if (!IsNewerVersion(tag, currentVersion))
                return new UpdateCheckResult(true, false, null, null);

            if (string.IsNullOrWhiteSpace(downloadUrl))
                return new UpdateCheckResult(false, false, null, $"Yeni sürüm ({tag}) bulundu ancak Windows uyumlu .zip paketi yer almıyor.");

            var updateInfo = new UpdateReleaseInfo(tag, title, body, downloadUrl, zipName, size);
            return new UpdateCheckResult(true, true, updateInfo, null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, null, $"Güncelleme sunucusuna erişilemedi: {ex.Message}");
        }
    }

    private static async Task<string?> GetLatestTagFromWebRedirectAsync(CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
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
        try
        {
            var rClean = remoteTag.Trim().TrimStart('v', 'V').Split('-')[0];
            var cClean = currentVersion.Trim().TrimStart('v', 'V').Split('-')[0];
            var rParts = rClean.Split('.').Select(p => int.TryParse(p, out var v) ? v : 0).ToList();
            var cParts = cClean.Split('.').Select(p => int.TryParse(p, out var v) ? v : 0).ToList();

            int maxLen = Math.Max(rParts.Count, cParts.Count);
            for (int i = 0; i < maxLen; i++)
            {
                int r = i < rParts.Count ? rParts[i] : 0;
                int c = i < cParts.Count ? cParts[i] : 0;
                if (r > c) return true;
                if (r < c) return false;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
