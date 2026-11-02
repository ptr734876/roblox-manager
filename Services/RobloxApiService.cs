using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services
{
    public class RobloxUserInfo
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
    }

    public class RobloxApiService
    {
        // Кэш разрезолвленных ссылок share?code=..., чтобы не делать повторные запросы при запуске пачки аккаунтов
        private static readonly ConcurrentDictionary<string, (long placeId, string? linkCode)> ShareLinkCache = new(StringComparer.OrdinalIgnoreCase);

        // Кэш ников -> UserId, чтобы не опрашивать users.roblox.com повторно и не ловить 429 / таймауты
        private static readonly ConcurrentDictionary<string, (long userId, string exactName)> UsernameCache = new(StringComparer.OrdinalIgnoreCase);

        // Кэшированный CSRF-токен для быстрых запросов тикетов без лишнего 403 раунд-трипа
        private static string? _cachedCsrfToken;

        // Прямые балансировщики Roblox на случай, если DNS отдает заблокированные провайдером IP Cloudflare (104.18.x.x)
        private static readonly IPAddress[] DirectRobloxFallbackIps =
        {
            IPAddress.Parse("128.116.44.3"),
            IPAddress.Parse("128.116.5.3"),
            IPAddress.Parse("128.116.115.3"),
        };

        private static readonly SocketsHttpHandler SharedHandler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
            ConnectCallback = ConnectRobloxWithFallbackAsync
        };

        private static readonly SocketsHttpHandler DirectFallbackHandler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
            ConnectCallback = ConnectRobloxDirectIpsOnlyAsync
        };

        private static async ValueTask<Stream> ConnectRobloxWithFallbackAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            // 1. Сначала всегда пробуем стандартное подключение через системный DNS (включая Cloudflare IP).
            // Если у пользователя включен Zapret (winws) или VPN, стандартный маршрут работает мгновенно и штатно.
            var defaultSocket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var sysCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sysCts.CancelAfter(TimeSpan.FromSeconds(4.0));
                await defaultSocket.ConnectAsync(context.DnsEndPoint, sysCts.Token);
                return new NetworkStream(defaultSocket, ownsSocket: true);
            }
            catch
            {
                defaultSocket.Dispose();
            }

            // 2. Если стандартный коннект не прошел по таймауту, переходим на прямые IP Roblox (обход блокировки Cloudflare TCP)
            return await ConnectRobloxDirectIpsOnlyAsync(context, cancellationToken);
        }

        private static async ValueTask<Stream> ConnectRobloxDirectIpsOnlyAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            string host = context.DnsEndPoint.Host;
            int port = context.DnsEndPoint.Port;

            var candidateIps = new List<IPAddress>();

            try
            {
                IPAddress[] resolved = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken);
                foreach (IPAddress ip in resolved)
                {
                    byte[] bytes = ip.GetAddressBytes();
                    bool isCloudflareAnycast = (bytes[0] == 104 && bytes[1] >= 16 && bytes[1] <= 31) ||
                                               (bytes[0] == 172 && bytes[1] >= 64 && bytes[1] <= 71);
                    if (!isCloudflareAnycast && !candidateIps.Contains(ip))
                    {
                        candidateIps.Add(ip);
                    }
                }
            }
            catch { }

            if (candidateIps.Count == 0 && host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string siblingHost in new[] { "users.roblox.com", "apis.roblox.com", "www.roblox.com" })
                {
                    try
                    {
                        IPAddress[] siblingIps = await Dns.GetHostAddressesAsync(siblingHost, AddressFamily.InterNetwork, cancellationToken);
                        foreach (IPAddress ip in siblingIps)
                        {
                            byte[] bytes = ip.GetAddressBytes();
                            bool isCloudflare = (bytes[0] == 104 && bytes[1] >= 16 && bytes[1] <= 31);
                            if (!isCloudflare && !candidateIps.Contains(ip))
                                candidateIps.Add(ip);
                        }
                        if (candidateIps.Count > 0) break;
                    }
                    catch { }
                }
            }

            if (host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase))
            {
                foreach (IPAddress fallbackIp in DirectRobloxFallbackIps)
                {
                    if (!candidateIps.Contains(fallbackIp))
                        candidateIps.Add(fallbackIp);
                }
            }

            Exception? lastError = null;
            foreach (IPAddress ip in candidateIps)
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    using var perIpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    perIpCts.CancelAfter(TimeSpan.FromSeconds(3.5));
                    await socket.ConnectAsync(new IPEndPoint(ip, port), perIpCts.Token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    lastError = ex;
                }
            }

            if (lastError != null) throw lastError;
            throw new SocketException((int)SocketError.HostUnreachable);
        }

        private static readonly HttpClient SharedClient = new(SharedHandler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };

        private static readonly HttpClient DirectFallbackClient = new(DirectFallbackHandler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };

        private static async Task<HttpResponseMessage> SendWithFallbackAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken = default)
        {
            try
            {
                using var req = requestFactory();
                return await SharedClient.SendAsync(req, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                using var fallbackReq = requestFactory();
                return await DirectFallbackClient.SendAsync(fallbackReq, cancellationToken);
            }
        }

        public static string CleanCookie(string rawCookie)
        {
            if (string.IsNullOrWhiteSpace(rawCookie))
                return string.Empty;

            string cleaned = rawCookie.Trim().Trim('"', '\'');
            if (cleaned.StartsWith(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(".ROBLOSECURITY=".Length).Trim();
            }
            int semiIndex = cleaned.IndexOf(';');
            if (semiIndex > 0)
            {
                cleaned = cleaned.Substring(0, semiIndex).Trim();
            }
            return cleaned;
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string? cookie = null, string? csrfToken = null)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                req.Headers.Add("Cookie", $".ROBLOSECURITY={CleanCookie(cookie)}");
            }
            if (!string.IsNullOrWhiteSpace(csrfToken))
            {
                req.Headers.Add("x-csrf-token", csrfToken);
            }
            return req;
        }

        public async Task<RobloxUserInfo?> ValidateAndGetUserAsync(string cookie)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var response = await SendWithFallbackAsync(
                    () => CreateRequest(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie),
                    cts.Token);
                if (!response.IsSuccessStatusCode)
                    return null;

                string json = await response.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                long userId = root.GetProperty("id").GetInt64();
                string name = root.GetProperty("name").GetString() ?? "";
                string displayName = root.GetProperty("displayName").GetString() ?? name;

                string avatarUrl = await GetAvatarUrlAsync(userId);

                return new RobloxUserInfo
                {
                    Id = userId,
                    Name = name,
                    DisplayName = displayName,
                    AvatarUrl = avatarUrl
                };
            }
            catch
            {
                return null;
            }
        }

        public async Task<string> GetAvatarUrlAsync(long userId)
        {
            try
            {
                string url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=false";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var response = await SendWithFallbackAsync(
                    () => CreateRequest(HttpMethod.Get, url),
                    cts.Token);
                if (!response.IsSuccessStatusCode)
                    return string.Empty;

                string json = await response.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(json);
                var dataArr = doc.RootElement.GetProperty("data");
                if (dataArr.GetArrayLength() > 0)
                {
                    return dataArr[0].GetProperty("imageUrl").GetString() ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>
        /// Получает одноразовый Authentication Ticket для приватного сервера.
        /// Если Roblox возвращает 429 или таймаут, не блокирует работу: клиент может войти через DPAPI-куку.
        /// </summary>
        public async Task<(string? ticket, string? error)> GetAuthTicketAsync(string cookie)
        {
            if (string.IsNullOrWhiteSpace(cookie))
                return (null, "Токен сессии пуст");

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    string? csrf = _cachedCsrfToken;

                    using var resp1 = await SendWithFallbackAsync(() =>
                    {
                        var r = CreateRequest(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket", cookie, csrf);
                        r.Headers.Add("Referer", "https://www.roblox.com/");
                        r.Headers.Add("Origin", "https://www.roblox.com");
                        r.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
                        return r;
                    }, cts.Token);

                    if (resp1.IsSuccessStatusCode &&
                        resp1.Headers.TryGetValues("rbx-authentication-ticket", out var directTicketValues))
                    {
                        string? directTicket = directTicketValues.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(directTicket))
                            return (directTicket, null);
                    }

                    if ((int)resp1.StatusCode == 429)
                    {
                        if (attempt == 1)
                        {
                            await Task.Delay(1500);
                            continue;
                        }
                        return (null, "429");
                    }

                    if (resp1.Headers.TryGetValues("x-csrf-token", out var csrfValues))
                    {
                        string newCsrf = csrfValues.FirstOrDefault() ?? "";
                        if (!string.IsNullOrEmpty(newCsrf))
                        {
                            _cachedCsrfToken = newCsrf;
                            csrf = newCsrf;

                            using var resp2 = await SendWithFallbackAsync(() =>
                            {
                                var r = CreateRequest(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket", cookie, csrf);
                                r.Headers.Add("Referer", "https://www.roblox.com/");
                                r.Headers.Add("Origin", "https://www.roblox.com");
                                r.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
                                return r;
                            }, cts.Token);

                            if (resp2.IsSuccessStatusCode &&
                                resp2.Headers.TryGetValues("rbx-authentication-ticket", out var ticketValues))
                            {
                                string? ticket = ticketValues.FirstOrDefault();
                                if (!string.IsNullOrEmpty(ticket))
                                    return (ticket, null);
                            }

                            if ((int)resp2.StatusCode == 429)
                            {
                                if (attempt == 1)
                                {
                                    await Task.Delay(1500);
                                    continue;
                                }
                                return (null, "429");
                            }

                            return (null, $"HTTP {(int)resp2.StatusCode}");
                        }
                    }

                    return (null, $"HTTP {(int)resp1.StatusCode}");
                }
                catch (OperationCanceledException)
                {
                    return (null, "Таймаут соединения с auth.roblox.com");
                }
                catch (Exception ex)
                {
                    return (null, ex.Message);
                }
            }

            return (null, "429");
        }

        public static bool WriteRobloxCookiesDat(string cookie, long userId, long browserTrackerId)
        {
            try
            {
                string localStorageDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Roblox",
                    "LocalStorage");

                if (!Directory.Exists(localStorageDir))
                {
                    Directory.CreateDirectory(localStorageDir);
                }

                string cleanCookie = CleanCookie(cookie);

                string rawCookieLines =
                    $"#HttpOnly_.roblox.com\tTRUE\t/\tTRUE\t0\t.ROBLOSECURITY\t{cleanCookie}; " +
                    $".roblox.com\tTRUE\t/\tFALSE\t1827735540\tRBXEventTrackerV2\tCreateDate=10/28/2026 03:18:37&rbxid={userId}&rbxuid={userId}&browserid={browserTrackerId}";

                byte[] plainBytes = Encoding.UTF8.GetBytes(rawCookieLines);
                byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                string base64Data = Convert.ToBase64String(encryptedBytes);

                string jsonContent = $"{{\"CookiesVersion\":\"1\",\"CookiesData\":\"{base64Data}\"}}";
                string cookieFilePath = Path.Combine(localStorageDir, "RobloxCookies.dat");

                using var fs = new FileStream(cookieFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonContent);
                fs.Write(jsonBytes, 0, jsonBytes.Length);
                fs.Flush();

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string? FindRobloxPlayerBetaExe()
        {
            try
            {
                string versionsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Roblox",
                    "Versions");

                if (!Directory.Exists(versionsDir))
                    return null;

                // РЎРЅР°С‡Р°Р»Р° РїСЂРѕРІРµСЂСЏРµРј Р·Р°СЂРµРіРёСЃС‚СЂРёСЂРѕРІР°РЅРЅСѓСЋ РІ СЃРёСЃС‚РµРјРµ РІРµСЂСЃРёСЋ roblox-player РІ СЂРµРµСЃС‚СЂРµ Windows.
                // РџСЂРё Р·Р°РїСѓСЃРєРµ РѕС„РёС†РёР°Р»СЊРЅРѕРіРѕ РёРЅСЃС‚Р°Р»Р»РµСЂР° Windows РІСЃРµРіРґР° РѕР±РЅРѕРІР»СЏРµС‚ HKCU\Software\Classes\roblox-player\shell\open\command
                try
                {
                    using var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\roblox-player\shell\open\command");
                    if (regKey != null && regKey.GetValue(null) is string regCmd && !string.IsNullOrWhiteSpace(regCmd))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(regCmd, "\"([^\"]+RobloxPlayerBeta\\.exe)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (match.Success && File.Exists(match.Groups[1].Value))
                        {
                            return match.Groups[1].Value;
                        }
                    }
                }
                catch { }

                // Р•СЃР»Рё СЂРµРµСЃС‚СЂ РЅРµРґРѕСЃС‚СѓРїРµРЅ, РІС‹Р±РёСЂР°РµРј РїР°РїРєСѓ, СЃРѕРІРїР°РґР°СЋС‰СѓСЋ СЃ Р°РєС‚РёРІРЅРѕР№ РІРµСЂСЃРёРµР№ Р»РёР±Рѕ РїРѕСЃР»РµРґРЅРµР№ РїРѕ РґР°С‚Рµ
                var candidate = new DirectoryInfo(versionsDir)
                    .GetDirectories()
                    .Select(d => new FileInfo(Path.Combine(d.FullName, "RobloxPlayerBeta.exe")))
                    .Where(f => f.Exists)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                return candidate?.FullName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Разрешает share-ссылку Roblox через sharelinks API
        /// через официальное API sharelinks/v1/resolve-link (с кэшированием)
        /// </summary>
        public async Task<(long? placeId, string? linkCode, string? error)> ResolveShareLinkAsync(string cookie, string shareCode, string linkType = "Server")
        {
            if (!string.Equals(linkType, "Server", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(linkType, "Experience", StringComparison.OrdinalIgnoreCase))
            {
                return (null, null, "Неподдерживаемый тип ссылки Roblox.");
            }

            linkType = string.Equals(linkType, "Server", StringComparison.OrdinalIgnoreCase) ? "Server" : "Experience";
            string cacheKey = $"{linkType}:{shareCode}";
            if (ShareLinkCache.TryGetValue(cacheKey, out var cached))
                return (cached.placeId, cached.linkCode, null);

            try
            {
                string payload = JsonSerializer.Serialize(new
                {
                    linkId = shareCode,
                    linkType
                });

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var resp1 = await SendWithFallbackAsync(() =>
                {
                    var r = CreateRequest(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link", cookie);
                    r.Headers.Add("Referer", "https://www.roblox.com/");
                    r.Headers.Add("Origin", "https://www.roblox.com");
                    r.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    return r;
                }, cts.Token);
                HttpResponseMessage finalResp = resp1;
                HttpResponseMessage? retryResponse = null;

                if (!resp1.IsSuccessStatusCode && resp1.Headers.TryGetValues("x-csrf-token", out var csrfVals))
                {
                    string csrf = csrfVals.FirstOrDefault() ?? "";
                    retryResponse = await SendWithFallbackAsync(() =>
                    {
                        var r = CreateRequest(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link", cookie, csrf);
                        r.Headers.Add("Referer", "https://www.roblox.com/");
                        r.Headers.Add("Origin", "https://www.roblox.com");
                        r.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                        return r;
                    }, cts.Token);
                    finalResp = retryResponse;
                }

                if (!finalResp.IsSuccessStatusCode)
                {
                    int statusCode = (int)finalResp.StatusCode;
                    retryResponse?.Dispose();
                    return (null, null, statusCode == 429
                        ? "Roblox временно ограничил запрос ссылки (HTTP 429). Подождите и попробуйте снова."
                        : $"Не удалось разрешить ссылку Roblox (HTTP {statusCode}). Проверьте ссылку и доступ аккаунта.");
                }

                string json = await finalResp.Content.ReadAsStringAsync();
                retryResponse?.Dispose();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("privateServerInviteData", out var privData) &&
                    privData.ValueKind == JsonValueKind.Object)
                {
                    if (privData.TryGetProperty("status", out var statusProp) &&
                        statusProp.ValueKind == JsonValueKind.String &&
                        !string.Equals(statusProp.GetString(), "Valid", StringComparison.OrdinalIgnoreCase))
                    {
                        return (null, null, $"Приватная ссылка недействительна или недоступна (статус Roblox: {statusProp.GetString()}).");
                    }

                    long? resolvedPlaceId = null;
                    string? resolvedLinkCode = null;

                    if (privData.TryGetProperty("placeId", out var pProp) && pProp.TryGetInt64(out long pid))
                        resolvedPlaceId = pid;

                    if (privData.TryGetProperty("linkCode", out var lProp))
                        resolvedLinkCode = lProp.GetString();

                    if (resolvedPlaceId is > 0 && !string.IsNullOrWhiteSpace(resolvedLinkCode))
                    {
                        ShareLinkCache[cacheKey] = (resolvedPlaceId.Value, resolvedLinkCode);
                        return (resolvedPlaceId, resolvedLinkCode, null);
                    }

                    return (null, null, "Roblox не вернул данные приватного сервера для этой ссылки.");
                }

                if (root.TryGetProperty("experienceInviteData", out var expData) &&
                    expData.ValueKind == JsonValueKind.Object)
                {
                    if (expData.TryGetProperty("placeId", out var pProp) && pProp.TryGetInt64(out long pid))
                    {
                        ShareLinkCache[cacheKey] = (pid, null);
                        return (pid, null, null);
                    }
                }

                return (null, null, "Roblox вернул неожиданный ответ при обработке ссылки.");
            }
            catch (TaskCanceledException)
            {
                return (null, null, "Истекло время ожидания ответа Roblox при обработке ссылки.");
            }
            catch (HttpRequestException)
            {
                return (null, null, "Не удалось связаться с Roblox для проверки ссылки.");
            }
            catch (JsonException)
            {
                return (null, null, "Roblox вернул некорректный ответ при обработке ссылки.");
            }
        }

        private static string? GetQueryParameter(string input, string parameterName)
        {
            int queryStart = input.IndexOf('?');
            if (queryStart < 0 || queryStart == input.Length - 1)
                return null;

            string query = input[(queryStart + 1)..].Split('#', 2)[0].Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
            foreach (string segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = segment.Split('=', 2);
                string key = Uri.UnescapeDataString(pair[0].Replace("+", " ", StringComparison.Ordinal));
                if (!string.Equals(key, parameterName, StringComparison.OrdinalIgnoreCase) || pair.Length < 2)
                    continue;

                return Uri.UnescapeDataString(pair[1].Replace("+", " ", StringComparison.Ordinal));
            }

            return null;
        }

        public static string? ExtractShareCode(string input1, string input2)
        {
            foreach (var s in new[] { input1, input2 })
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                string? code = GetQueryParameter(s, "code");
                if (!string.IsNullOrWhiteSpace(code) && Regex.IsMatch(code, @"^[a-zA-Z0-9_-]{8,128}$"))
                    return code;
            }
            return null;
        }

        private static string GetShareLinkType(string input1, string input2)
        {
            foreach (var input in new[] { input1, input2 })
            {
                if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(GetQueryParameter(input, "code")))
                    continue;

                string? type = GetQueryParameter(input, "type");
                if (!string.IsNullOrWhiteSpace(type))
                    return type;
            }

            return "Server";
        }

        public static (long? placeId, string? privateLinkCode, string? jobId) ParseGameTargets(string placeInput, string vipOrJobInput)
        {
            long? placeId = null;
            string? privateLinkCode = null;
            string? jobId = null;

            string p = (placeInput ?? string.Empty).Trim();
            string v = (vipOrJobInput ?? string.Empty).Trim();

            if (long.TryParse(p, out long parsedId))
            {
                placeId = parsedId;
            }
            else if (!string.IsNullOrEmpty(p))
            {
                var gameMatch = Regex.Match(p, @"games/(\d+)", RegexOptions.IgnoreCase);
                if (gameMatch.Success && long.TryParse(gameMatch.Groups[1].Value, out long urlPlaceId))
                {
                    placeId = urlPlaceId;
                }

                privateLinkCode = GetQueryParameter(p, "privateServerLinkCode");
            }

            if (!string.IsNullOrEmpty(v))
            {
                string? parsedPrivateLink = GetQueryParameter(v, "privateServerLinkCode");
                if (!string.IsNullOrWhiteSpace(parsedPrivateLink))
                {
                    privateLinkCode = parsedPrivateLink;
                    if (placeId == null)
                    {
                        var gameMatch = Regex.Match(v, @"games/(\d+)", RegexOptions.IgnoreCase);
                        if (gameMatch.Success && long.TryParse(gameMatch.Groups[1].Value, out long urlPlaceId))
                        {
                            placeId = urlPlaceId;
                        }
                    }
                }
                else if (Guid.TryParse(v, out _))
                {
                    jobId = v;
                }
                else if (TryParseUsername(v, out _))
                {
                    // Ник игрока обрабатывается отдельно в LaunchAccountAsync (вход к игроку).
                }
                else if (Regex.IsMatch(v, @"^[a-zA-Z0-9_-]{16,128}$"))
                {
                    privateLinkCode = v;
                }
            }

            return (placeId, privateLinkCode, jobId);
        }

        public async Task<(bool success, string message, Process? startedProcess, string cleanCookie, long browserTrackerId)> LaunchAccountAsync(RobloxAccount account, string placeInput, string vipOrJobInput)
        {
            string cookie = AccountStorageService.DecryptCookie(account.EncryptedCookie);
            if (string.IsNullOrEmpty(cookie))
            {
                return (false, "Не удалось расшифровать токен сессии", null, string.Empty, 0);
            }

            if (string.IsNullOrWhiteSpace(placeInput) && string.IsNullOrWhiteSpace(vipOrJobInput))
            {
                return LaunchToHome(account, cookie);
            }

            if (TryParseUsername(vipOrJobInput, out string targetUsername))
            {
                return await LaunchToPlayerAsync(account, cookie, targetUsername);
            }

            var (placeId, privateLinkCode, jobId) = ParseGameTargets(placeInput, vipOrJobInput);

            string? shareCode = ExtractShareCode(placeInput, vipOrJobInput);
            if (!string.IsNullOrEmpty(shareCode))
            {
                string shareLinkType = GetShareLinkType(placeInput, vipOrJobInput);
                var (resolvedPlaceId, resolvedLinkCode, resolveError) = await ResolveShareLinkAsync(cookie, shareCode, shareLinkType);
                if (resolveError != null)
                {
                    return (false, resolveError, null, string.Empty, 0);
                }

                if (resolvedPlaceId != null && resolvedPlaceId > 0)
                {
                    placeId = resolvedPlaceId;
                }
                if (!string.IsNullOrEmpty(resolvedLinkCode))
                {
                    privateLinkCode = resolvedLinkCode;
                }

                if (string.Equals(shareLinkType, "Server", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(privateLinkCode))
                {
                    return (false, "Ссылка на приватный сервер не содержит доступного кода приглашения.", null, string.Empty, 0);
                }
            }

            if (placeId == null || placeId <= 0)
            {
                return (false, "Укажите Place ID, ссылку на игру или ссылку на приватку (share?code=...)", null, string.Empty, 0);
            }

            long browserTrackerId = Random.Shared.NextInt64(10000000000L, 99999999999L);
            bool usePrivateServer = !string.IsNullOrWhiteSpace(privateLinkCode);

            // Получаем одноразовый Authentication Ticket для прямого подключения к игре / приватке.
            // Если получить тикет не удалось из-за таймаута/429, не отменяем запуск — клиент войдет через DPAPI-сессию RobloxCookies.dat!
            var (ticket, ticketError) = await GetAuthTicketAsync(cookie);
            if (string.IsNullOrEmpty(ticket) &&
                ticketError != null &&
                (ticketError.Contains("401", StringComparison.Ordinal) || ticketError.Contains("Токен сессии пуст", StringComparison.Ordinal)))
            {
                return (false, $"Сессия аккаунта устарела ({ticketError}). Перезайдите в аккаунт.", null, string.Empty, 0);
            }

            if (!WriteRobloxCookiesDat(cookie, account.UserId, browserTrackerId))
            {
                return (false, "Не удалось записать сессию выбранного аккаунта перед запуском Roblox.", null, string.Empty, 0);
            }

            string placeLauncherUrl;
            if (usePrivateServer)
            {
                placeLauncherUrl =
                    $"https://www.roblox.com/Game/PlaceLauncher.ashx?request=RequestPrivateGame" +
                    $"&browserTrackerId={browserTrackerId}&placeId={placeId}&accessCode=" +
                    $"&linkCode={Uri.EscapeDataString(privateLinkCode!)}" +
                    $"&joinAttemptId={Guid.NewGuid():D}&joinAttemptOrigin=PlayButton";
            }
            else if (!string.IsNullOrEmpty(jobId))
            {
                placeLauncherUrl =
                    $"https://www.roblox.com/Game/PlaceLauncher.ashx?request=RequestGameJob&browserTrackerId={browserTrackerId}&placeId={placeId}&gameId={Uri.EscapeDataString(jobId)}&isPlayTogetherGame=false";
            }
            else
            {
                placeLauncherUrl =
                    $"https://www.roblox.com/Game/PlaceLauncher.ashx?request=RequestGame&browserTrackerId={browserTrackerId}&placeId={placeId}&isPlayTogetherGame=false";
            }

            string modeDesc = usePrivateServer ? $"Запуск приватного сервера (Place {placeId})" : $"Запущен (Place {placeId})";
            return StartRobloxClient(account, cookie, browserTrackerId, placeLauncherUrl, ticket, modeDesc);
        }

        /// <summary>
        /// Ник Roblox: 3–20 символов, латиница/цифры/подчёркивание. Ссылки, JobId и коды приглашений сюда не попадают.
        /// </summary>
        public static bool TryParseUsername(string? input, out string username)
        {
            username = string.Empty;
            string v = (input ?? string.Empty).Trim();
            if (v.StartsWith('@')) v = v[1..];
            if (v.Length < 3 || v.Length > 20 || Guid.TryParse(v, out _))
                return false;
            if (!Regex.IsMatch(v, @"^[A-Za-z0-9_]+$") || v.StartsWith('_') || v.EndsWith('_'))
                return false;
            username = v;
            return true;
        }

        public async Task<(long? userId, string? exactName, string? error)> ResolveUsernameAsync(string cookie, string username)
        {
            if (long.TryParse(username, out long numericId) && numericId > 1000)
            {
                return (numericId, username, null);
            }

            if (UsernameCache.TryGetValue(username, out var cached))
            {
                return (cached.userId, cached.exactName, null);
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                // Поиск по нику публичный, куку не передаём, чтобы не упираться в лимиты сессии
                string body = JsonSerializer.Serialize(new { usernames = new[] { username }, excludeBannedUsers = true });

                using var resp = await SendWithFallbackAsync(() =>
                {
                    var r = CreateRequest(HttpMethod.Post, "https://users.roblox.com/v1/usernames/users");
                    r.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    return r;
                }, cts.Token);
                if ((int)resp.StatusCode == 429)
                    return (null, null, "Roblox временно ограничил поиск по нику (HTTP 429). Повторите через несколько секунд.");
                if (!resp.IsSuccessStatusCode)
                    return (null, null, $"Не удалось найти игрока @{username} (HTTP {(int)resp.StatusCode})");

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0)
                    return (null, null, $"Игрок @{username} не найден");

                long userId = data[0].GetProperty("id").GetInt64();
                string exactName = data[0].TryGetProperty("name", out var n) ? n.GetString() ?? username : username;

                UsernameCache[username] = (userId, exactName);
                UsernameCache[exactName] = (userId, exactName);

                return (userId, exactName, null);
            }
            catch (OperationCanceledException)
            {
                return (null, null, $"Roblox не ответил за 8 сек при поиске ника @{username}. Проверьте соединение и повторите.");
            }
            catch (Exception ex)
            {
                return (null, null, $"Ошибка поиска игрока @{username}: {ex.Message}");
            }
        }

        /// <summary>
        /// Presence с точки зрения выбранного аккаунта. Быстрый опрос с таймаутом 6с.
        /// </summary>
        private static async Task<(int presenceType, long? placeId, string? gameId)> GetPresenceAsync(string cookie, long userId)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                string body = JsonSerializer.Serialize(new { userIds = new[] { userId } });

                async Task<HttpResponseMessage> Send(string? csrf)
                {
                    return await SendWithFallbackAsync(() =>
                    {
                        var req = CreateRequest(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users", cookie, csrf);
                        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        return req;
                    }, cts.Token);
                }

                var resp = await Send(null);
                if ((int)resp.StatusCode == 403 && resp.Headers.TryGetValues("x-csrf-token", out var tokens))
                {
                    resp.Dispose();
                    resp = await Send(tokens.FirstOrDefault());
                }

                using (resp)
                {
                    if (!resp.IsSuccessStatusCode)
                        return (-1, null, null);

                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                    var arr = doc.RootElement.GetProperty("userPresences");
                    if (arr.GetArrayLength() == 0)
                        return (-1, null, null);

                    var p = arr[0];
                    int type = p.TryGetProperty("userPresenceType", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : -1;
                    long? placeId = p.TryGetProperty("placeId", out var pl) && pl.ValueKind == JsonValueKind.Number ? pl.GetInt64() : null;
                    string? gameId = p.TryGetProperty("gameId", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
                    return (type, placeId, gameId);
                }
            }
            catch
            {
                return (-1, null, null);
            }
        }

        private async Task<(bool success, string message, Process? startedProcess, string cleanCookie, long browserTrackerId)> LaunchToPlayerAsync(
            RobloxAccount account, string cookie, string username)
        {
            var (targetUserId, exactName, resolveError) = await ResolveUsernameAsync(cookie, username);
            if (targetUserId == null)
                return (false, resolveError ?? $"Игрок @{username} не найден", null, string.Empty, 0);

            string name = exactName ?? username;
            var (presenceType, presencePlaceId, presenceGameId) = await GetPresenceAsync(cookie, targetUserId.Value);

            if (presenceType == 0)
                return (false, $"@{name} сейчас не в игре (Offline)", null, string.Empty, 0);
            if (presenceType == 1)
                return (false, $"@{name} сейчас на сайте Roblox, а не в игре", null, string.Empty, 0);
            if (presenceType == 3)
                return (false, $"@{name} сейчас в Roblox Studio, а не в игре", null, string.Empty, 0);

            long browserTrackerId = Random.Shared.NextInt64(10000000000L, 99999999999L);

            // Получаем одноразовый Authentication Ticket для входа на сервер игрока.
            // При сетевом сбое или 429 продолжаем запуск через DPAPI-куку RobloxCookies.dat.
            var (ticket, ticketError) = await GetAuthTicketAsync(cookie);
            if (string.IsNullOrEmpty(ticket) &&
                ticketError != null &&
                (ticketError.Contains("401", StringComparison.Ordinal) || ticketError.Contains("Токен сессии пуст", StringComparison.Ordinal)))
            {
                return (false, $"Сессия аккаунта устарела ({ticketError}). Перезайдите в аккаунт.", null, string.Empty, 0);
            }

            if (!WriteRobloxCookiesDat(cookie, account.UserId, browserTrackerId))
                return (false, "Не удалось записать сессию выбранного аккаунта перед запуском Roblox.", null, string.Empty, 0);

            string placeLauncherUrl;
            string modeDesc;
            if (presencePlaceId is > 0 && !string.IsNullOrWhiteSpace(presenceGameId))
            {
                // Сервер игрока виден напрямую — подключаемся прямо по PlaceId и JobId
                placeLauncherUrl =
                    $"https://www.roblox.com/Game/PlaceLauncher.ashx?request=RequestGameJob&browserTrackerId={browserTrackerId}" +
                    $"&placeId={presencePlaceId}&gameId={Uri.EscapeDataString(presenceGameId!)}&isPlayTogetherGame=false";
                modeDesc = $"Вход к @{name} (Place {presencePlaceId})";
            }
            else
            {
                // Сервер скрыт настройками или presence вернул RequestFollowUser.
                // Передаём запрос FollowUser напрямую клиенту RobloxPlayerBeta
                placeLauncherUrl =
                    $"https://www.roblox.com/Game/PlaceLauncher.ashx?request=RequestFollowUser&browserTrackerId={browserTrackerId}" +
                    $"&userId={targetUserId}&joinAttemptId={Guid.NewGuid():D}&joinAttemptOrigin=JoinUser";
                modeDesc = $"Вход к @{name}";
            }

            return StartRobloxClient(account, cookie, browserTrackerId, placeLauncherUrl, ticket, modeDesc);
        }

        /// <summary>
        /// Оба поля пустые — открываем клиент Roblox на главной странице под выбранным аккаунтом.
        /// </summary>
        private static (bool success, string message, Process? startedProcess, string cleanCookie, long browserTrackerId) LaunchToHome(
            RobloxAccount account, string cookie)
        {
            long browserTrackerId = Random.Shared.NextInt64(10000000000L, 99999999999L);
            if (!WriteRobloxCookiesDat(cookie, account.UserId, browserTrackerId))
                return (false, "Не удалось записать сессию выбранного аккаунта перед запуском Roblox.", null, string.Empty, 0);

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string launchUri =
                $"roblox-player:1+launchmode:app+launchtime:{nowMs}+browsertrackerid:{browserTrackerId}" +
                "+robloxLocale:en_us+gameLocale:en_us+channel:";
            return StartRobloxUri(account, cookie, browserTrackerId, launchUri, "Открыта главная Roblox");
        }

        private static (bool success, string message, Process? startedProcess, string cleanCookie, long browserTrackerId) StartRobloxClient(
            RobloxAccount account, string cookie, long browserTrackerId, string placeLauncherUrl, string? gameInfo, string modeDesc)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string encodedPlaceLauncherUrl = Uri.EscapeDataString(placeLauncherUrl);
            string launchUri =
                $"roblox-player:1+launchmode:play" +
                (gameInfo == null ? string.Empty : $"+gameinfo:{gameInfo}") +
                $"+launchtime:{nowMs}+placelauncherurl:{encodedPlaceLauncherUrl}+browsertrackerid:{browserTrackerId}+robloxLocale:en_us+gameLocale:en_us+channel:";

            return StartRobloxUri(account, cookie, browserTrackerId, launchUri, modeDesc);
        }

        private static (bool success, string message, Process? startedProcess, string cleanCookie, long browserTrackerId) StartRobloxUri(
            RobloxAccount account, string cookie, long browserTrackerId, string launchUri, string modeDesc)
        {
            try
            {
                Process? proc = null;
                string? exePath = FindRobloxPlayerBetaExe();
                if (!string.IsNullOrEmpty(exePath))
                {
                    proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = launchUri,
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                        UseShellExecute = false
                    });
                }
                else
                {
                    proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = launchUri,
                        UseShellExecute = true
                    });
                }

                account.LastUsed = DateTime.Now;
                return (true, modeDesc, proc, cookie, browserTrackerId);
            }
            catch (Exception ex)
            {
                return (false, $"Ошибка запуска клиента Roblox: {ex.Message}", null, string.Empty, 0);
            }
        }
    }
}
