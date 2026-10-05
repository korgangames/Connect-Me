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

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static WindowsUpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ConnectMe-Windows", "1.7.3"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(GITHUB_LATEST_RELEASE_URL, ct);
            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;
                string hint = code == 404
                    ? "GitHub 404 Not Found döndürdü. Depo (korgangames/Connect-Me) Private (Gizli) ayarlanmış olabilir. Güncellemelerin cihazlar tarafından görülebilmesi için deponun 'Public' (Açık) olması gerekir."
                    : $"GitHub API HTTP {code} ({resp.ReasonPhrase}) hatası döndürdü.";
                return new UpdateCheckResult(false, false, null, hint);
            }

            var jsonStream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: ct);
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string title = root.TryGetProperty("name", out var tProp) ? (tProp.GetString() ?? tag) : tag;
            string body = root.TryGetProperty("body", out var bProp) ? (bProp.GetString() ?? "") : "";

            if (string.IsNullOrWhiteSpace(tag))
                return new UpdateCheckResult(false, false, null, "Sürüm etiketi (tag_name) bulunamadı.");

            if (!IsNewerVersion(tag, currentVersion))
                return new UpdateCheckResult(true, false, null, null);

            string downloadUrl = "";
            string zipName = $"ConnectMe-Windows-x64{tag}.zip";
            long size = 0;

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

        // Güncelleme komut betiği (.cmd)
        string cmdPath = Path.Combine(tempDir, "apply_update.cmd");
        string cmdContent = $@"@echo off
timeout /t 2 /nobreak >nul
copy /y ""{newExe}"" ""{currentExe}"" >nul
start """" ""{currentExe}""
del ""{cmdPath}""
";
        await File.WriteAllTextAsync(cmdPath, cmdContent, ct);

        // Arka planda çalıştır ve uygulamayı kapat
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{cmdPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
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
