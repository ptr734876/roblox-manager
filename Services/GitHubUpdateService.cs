using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RobloxAccountManager.Services
{
    public sealed class GitHubReleaseInfo
    {
        public string TagName { get; init; } = string.Empty;
        public Version Version { get; init; } = new(1, 0, 0);
        public string DownloadUrl { get; init; } = string.Empty;
        public string HtmlUrl { get; init; } = string.Empty;
        public string ReleaseNotes { get; init; } = string.Empty;
        public bool IsUpdateAvailable { get; init; }
    }

    public sealed class GitHubUpdateService
    {
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/ptr734876/roblox-manager/releases/latest";
        private const string FallbackReleaseUrl = "https://github.com/ptr734876/roblox-manager/releases/latest";

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(25)
        };

        public static Version GetCurrentVersion()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return new Version(1, 0, 0);
            return new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));
        }

        public async Task<(GitHubReleaseInfo? release, string? error)> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
                req.Headers.Add("User-Agent", "RobloxAccountManager-Updater");
                req.Headers.Add("Accept", "application/vnd.github+json");

                using var resp = await Http.SendAsync(req, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    return (null, $"GitHub API вернул HTTP {(int)resp.StatusCode}");
                }

                string json = await resp.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tagName = root.TryGetProperty("tag_name", out var tagProp) ? (tagProp.GetString() ?? "v1.0.0") : "v1.0.0";
                string htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? (urlProp.GetString() ?? FallbackReleaseUrl) : FallbackReleaseUrl;
                string body = root.TryGetProperty("body", out var bodyProp) ? (bodyProp.GetString() ?? string.Empty) : string.Empty;

                string cleanVer = tagName.Trim().TrimStart('v', 'V');
                int dashIdx = cleanVer.IndexOf('-');
                if (dashIdx > 0) cleanVer = cleanVer[..dashIdx];
                if (!Version.TryParse(cleanVer, out Version? remoteVersion))
                {
                    remoteVersion = new Version(1, 0, 0);
                }

                string downloadUrl = string.Empty;
                if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        string name = asset.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                        string bUrl = asset.TryGetProperty("browser_download_url", out var u) ? (u.GetString() ?? "") : "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(bUrl))
                        {
                            downloadUrl = bUrl;
                            break;
                        }
                    }
                }

                Version currentVersion = GetCurrentVersion();
                bool isNewer = remoteVersion > currentVersion;

                var info = new GitHubReleaseInfo
                {
                    TagName = tagName,
                    Version = remoteVersion,
                    DownloadUrl = downloadUrl,
                    HtmlUrl = htmlUrl,
                    ReleaseNotes = body,
                    IsUpdateAvailable = isNewer
                };

                return (info, null);
            }
            catch (Exception ex)
            {
                DiagnosticsService.LogException("Updater.Check", ex, flushToDisk: false);
                return (null, ex.Message);
            }
        }

        public async Task<(bool success, string message)> DownloadAndApplyUpdateAsync(
            GitHubReleaseInfo release,
            Action<string>? onProgress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // 1. Если приложение запущено прямо из локального git-репозитория (режим разработчика),
                // сначала пробуем сделать быстрый git pull --ff-only
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string? gitRoot = FindGitRepoRoot(appDir);
                if (!string.IsNullOrEmpty(gitRoot) && string.IsNullOrEmpty(release.DownloadUrl))
                {
                    onProgress?.Invoke("Обновление через git pull...");
                    bool gitPulled = await TryRunGitPullAsync(gitRoot);
                    if (gitPulled)
                    {
                        return (true, "Репозиторий успешно обновлён через git pull.");
                    }
                }

                if (string.IsNullOrEmpty(release.DownloadUrl))
                {
                    Process.Start(new ProcessStartInfo(release.HtmlUrl) { UseShellExecute = true });
                    return (true, "Открыта страница загрузки релиза на GitHub.");
                }

                string currentExePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? Path.Combine(appDir, "RobloxAccountManager.exe");

                string updateTempPath = currentExePath + ".update.exe";
                onProgress?.Invoke($"Скачивание {release.TagName}...");

                using (var req = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl))
                {
                    req.Headers.Add("User-Agent", "RobloxAccountManager-Updater");
                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    resp.EnsureSuccessStatusCode();

                    long? totalBytes = resp.Content.Headers.ContentLength;
                    await using var netStream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                    await using var fileStream = new FileStream(updateTempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    var sw = Stopwatch.StartNew();

                    while ((read = await netStream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        totalRead += read;

                        if (sw.ElapsedMilliseconds >= 200 && totalBytes is > 0)
                        {
                            int pct = (int)(totalRead * 100 / totalBytes.Value);
                            onProgress?.Invoke($"Скачивание {release.TagName}: {pct}% ({totalRead / (1024 * 1024)} МБ)");
                            sw.Restart();
                        }
                    }
                }

                FileInfo downloadedFile = new(updateTempPath);
                if (!downloadedFile.Exists || downloadedFile.Length < 100 * 1024)
                {
                    try { File.Delete(updateTempPath); } catch { }
                    return (false, "Скачанный файл повреждён или слишком мал.");
                }

                // Создаём компактный cmd-скрипт, который ждёт завершения текущего процесса, заменяет .exe и перезапускает его
                string batchPath = Path.Combine(Path.GetTempPath(), $"ram_update_{Environment.ProcessId}.cmd");
                int currentPid = Environment.ProcessId;
                string script =
                    "@echo off\r\n" +
                    "chcp 65001 >nul\r\n" +
                    "ping 127.0.0.1 -n 2 >nul\r\n" +
                    ":waitloop\r\n" +
                    $"tasklist /FI \"PID eq {currentPid}\" 2>NUL | find /I \"{currentPid}\" >NUL\r\n" +
                    "if \"%ERRORLEVEL%\"==\"0\" (\r\n" +
                    "    ping 127.0.0.1 -n 2 >nul\r\n" +
                    "    goto waitloop\r\n" +
                    ")\r\n" +
                    $"move /Y \"{updateTempPath}\" \"{currentExePath}\" >nul\r\n" +
                    $"start \"\" \"{currentExePath}\"\r\n" +
                    "del \"%~f0\"\r\n";

                await File.WriteAllTextAsync(batchPath, script, new UTF8Encoding(false), cancellationToken);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C \"\"{batchPath}\"\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                DiagnosticsService.LogInfo("Updater", $"Установлено обновление {release.TagName}, перезапуск приложения...");
                return (true, "RESTART_REQUIRED");
            }
            catch (Exception ex)
            {
                DiagnosticsService.LogError("Updater.Apply", "Ошибка при скачивании/установке обновления", ex);
                return (false, $"Ошибка обновления: {ex.Message}");
            }
        }

        private static string? FindGitRepoRoot(string startDir)
        {
            try
            {
                DirectoryInfo? dir = new(startDir);
                for (int i = 0; i < 5 && dir != null; i++)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        private static async Task<bool> TryRunGitPullAsync(string repoRoot)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "pull --ff-only",
                    WorkingDirectory = repoRoot,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                if (proc == null) return false;
                await proc.WaitForExitAsync();
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
