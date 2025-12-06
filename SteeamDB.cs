using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace PS5_OS
{
    internal static class SteeamDB
    {
        private static readonly string DataFolder = Path.Combine(AppContext.BaseDirectory, "Data");
        private static readonly string DataFile = Path.Combine(DataFolder, "SteamDb.json");
        private static readonly Uri DefaultSteamAppListUri = new("https://api.steampowered.com/ISteamApps/GetAppList/v2/");
        private static readonly HttpClient Http;

        static SteeamDB()
        {
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            Http.DefaultRequestHeaders.UserAgent.Clear();
            Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PS5_OS", "1.0"));
            Http.DefaultRequestHeaders.Accept.Clear();
            Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // Accept compressed responses if server supplies them
            Http.DefaultRequestHeaders.AcceptEncoding.Clear();
        }

        // Call once at application startup (fire-and-forget OK). Safe to call repeatedly.
        public static async Task InitializeAsync()
        {
            try
            {
                await UpdateIfNeededAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteeamDB] InitializeAsync failed: {ex}");
            }
        }

        // Public: force update using a custom URI (useful for mirrors)
        public static Task ForceUpdateFromUriAsync(Uri uri) => FetchAndWriteAsync(uri);

        // Fetch remote app list and update Data/SteamDb.json only if there are new entries.
        public static async Task UpdateIfNeededAsync()
        {
            Directory.CreateDirectory(DataFolder);

            // Load existing apps (if any)
            var existing = LoadExistingDictionary();

            // Try primary API then optional fallback from env var
            var fallbackUri = GetFallbackUriFromEnvironment();
            var candidates = new List<Uri> { DefaultSteamAppListUri };
            if (fallbackUri != null) candidates.Add(fallbackUri);

            SteamAppListResponse? remote = null;
            foreach (var candidate in candidates)
            {
                remote = await TryFetchWithRetriesAsync(candidate).ConfigureAwait(false);
                if (remote?.Applist?.Apps != null)
                {
                    Debug.WriteLine($"[SteeamDB] Successfully fetched from {candidate}");
                    break;
                }

                // If a server returned a 404 for this endpoint, don't retry other endpoints unnecessarily,
                // but continue to next candidate (fallback) if one is configured.
                Debug.WriteLine($"[SteeamDB] No apps returned from {candidate}");
            }

            // If remote fetch failed and the file doesn't exist create an empty placeholder so callers have deterministic file
            if (remote?.Applist?.Apps == null)
            {
                if (!File.Exists(DataFile))
                {
                    var empty = new SteamDbContainer
                    {
                        LastUpdatedUtc = DateTime.UtcNow.ToString("o"),
                        Total = 0,
                        Apps = new List<SteamAppEntry>()
                    };

                    try
                    {
                        var opts = new JsonSerializerOptions { WriteIndented = true };
                        var outJson = JsonSerializer.Serialize(empty, opts);
                        await File.WriteAllTextAsync(DataFile, outJson).ConfigureAwait(false);
                        Debug.WriteLine($"[SteeamDB] Wrote empty {DataFile}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SteeamDB] Failed to write empty DataFile: {ex}");
                    }
                }

                // nothing else to do
                return;
            }

            // Build a merged list and detect new entries
            var merged = new Dictionary<int, string>(existing);
            var added = 0;
            foreach (var app in remote.Applist.Apps)
            {
                var id = app.AppId;
                var nm = app.Name ?? string.Empty;
                if (!merged.ContainsKey(id))
                {
                    merged[id] = nm;
                    added++;
                }
                else
                {
                    // update name if changed
                    if (!string.Equals(merged[id], nm, StringComparison.Ordinal))
                        merged[id] = nm;
                }
            }

            // If no existing file, or new apps found, write updated file.
            if (!File.Exists(DataFile) || added > 0)
            {
                // Create deterministic sorted list by AppId
                var list = merged.OrderBy(kv => kv.Key)
                                 .Select(kv => new SteamAppEntry { AppId = kv.Key, Name = kv.Value })
                                 .ToList();

                var containerOut = new SteamDbContainer
                {
                    LastUpdatedUtc = DateTime.UtcNow.ToString("o"),
                    Total = list.Count,
                    Apps = list
                };

                try
                {
                    var opts = new JsonSerializerOptions { WriteIndented = true };
                    var outJson = JsonSerializer.Serialize(containerOut, opts);
                    await File.WriteAllTextAsync(DataFile, outJson).ConfigureAwait(false);
                    Debug.WriteLine($"[SteeamDB] Wrote {DataFile} ({containerOut.Total} apps, added {added})");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SteeamDB] Failed to write DataFile: {ex}");
                }
            }
            else
            {
                Debug.WriteLine("[SteeamDB] No new apps; DataFile not updated.");
            }
        }

        // New: fetch from the provided URI and write result to DataFile (merges with existing entries).
        private static async Task FetchAndWriteAsync(Uri uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));

            Directory.CreateDirectory(DataFolder);

            var remote = await TryFetchWithRetriesAsync(uri).ConfigureAwait(false);
            if (remote?.Applist?.Apps == null)
            {
                Debug.WriteLine($"[SteeamDB] FetchAndWriteAsync: failed to fetch apps from {uri}");
                throw new InvalidOperationException($"Failed to fetch app list from {uri}");
            }

            // Load existing entries and merge
            var merged = new Dictionary<int, string>(LoadExistingDictionary());
            foreach (var app in remote.Applist.Apps)
            {
                var id = app.AppId;
                var nm = app.Name ?? string.Empty;
                if (!merged.ContainsKey(id))
                    merged[id] = nm;
                else if (!string.Equals(merged[id], nm, StringComparison.Ordinal))
                    merged[id] = nm;
            }

            // Build deterministic sorted container and write file
            var list = merged.OrderBy(kv => kv.Key)
                             .Select(kv => new SteamAppEntry { AppId = kv.Key, Name = kv.Value })
                             .ToList();

            var containerOut = new SteamDbContainer
            {
                LastUpdatedUtc = DateTime.UtcNow.ToString("o"),
                Total = list.Count,
                Apps = list
            };

            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                var outJson = JsonSerializer.Serialize(containerOut, opts);
                await File.WriteAllTextAsync(DataFile, outJson).ConfigureAwait(false);
                Debug.WriteLine($"[SteeamDB] FetchAndWriteAsync wrote {DataFile} ({containerOut.Total} apps)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteeamDB] FetchAndWriteAsync failed to write DataFile: {ex}");
                throw;
            }
        }

        // Optional helper: get AppId by name (exact match, case-insensitive)
        public static int? TryFindAppIdByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                if (!File.Exists(DataFile)) return null;
                var txt = File.ReadAllText(DataFile);
                var container = JsonSerializer.Deserialize<SteamDbContainer>(txt, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (container?.Apps == null) return null;
                var match = container.Apps.Find(a => string.Equals(a.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
                return match?.AppId;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteeamDB] TryFindAppIdByName error: {ex}");
                return null;
            }
        }

        // Helper: load existing file into dictionary
        private static Dictionary<int, string> LoadExistingDictionary()
        {
            var existing = new Dictionary<int, string>();
            try
            {
                if (!File.Exists(DataFile)) return existing;
                var txt = File.ReadAllText(DataFile);
                if (string.IsNullOrWhiteSpace(txt)) return existing;

                var container = JsonSerializer.Deserialize<SteamDbContainer>(txt, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (container?.Apps != null)
                {
                    foreach (var a in container.Apps)
                    {
                        if (!existing.ContainsKey(a.AppId))
                            existing[a.AppId] = a.Name ?? string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteeamDB] Failed to read existing DataFile: {ex}");
                existing.Clear();
            }

            return existing;
        }

        // Try fetching the applist with retries. Returns null if not successful.
        private static async Task<SteamAppListResponse?> TryFetchWithRetriesAsync(Uri uri)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using var resp = await Http.GetAsync(uri).ConfigureAwait(false);

                    // If 404, server indicates the method is not available - bail early for this URI
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        Debug.WriteLine($"[SteeamDB] {uri} returned 404 (Not Found).");
                        return null;
                    }

                    resp.EnsureSuccessStatusCode();

                    await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var remote = await JsonSerializer.DeserializeAsync<SteamAppListResponse>(stream, opts).ConfigureAwait(false);

                    if (remote?.Applist?.Apps != null)
                    {
                        Debug.WriteLine($"[SteeamDB] Fetched {remote.Applist.Apps.Count} apps from {uri} (attempt {attempt}).");
                        return remote;
                    }

                    Debug.WriteLine($"[SteeamDB] Parsed response but no apps found from {uri} (attempt {attempt}).");
                    return null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SteeamDB] Attempt {attempt} for {uri} failed: {ex.Message}");
                    if (attempt < maxAttempts)
                        await Task.Delay(TimeSpan.FromSeconds(2 * attempt)).ConfigureAwait(false);
                }
            }

            return null;
        }

        // Read optional fallback URI from environment variable to allow mirrors without code change
        private static Uri? GetFallbackUriFromEnvironment()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable("STEAM_APPLIST_FALLBACK_URI");
                if (string.IsNullOrWhiteSpace(raw)) return null;
                if (Uri.TryCreate(raw, UriKind.Absolute, out var u))
                    return u;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteeamDB] Failed to read fallback URI from environment: {ex.Message}");
            }

            return null;
        }

        // DTOs for Steam API and local file
        private sealed class SteamApp
        {
            [JsonPropertyName("appid")]
            public int AppId { get; set; } // maps to "appid"

            [JsonPropertyName("name")]
            public string? Name { get; set; } // maps to "name"
        }

        private sealed class SteamApps
        {
            [JsonPropertyName("apps")]
            public List<SteamApp>? Apps { get; set; }
        }

        private sealed class SteamAppListResponse
        {
            [JsonPropertyName("applist")]
            public SteamApps? Applist { get; set; }
        }

        private sealed class SteamAppEntry
        {
            // serialized property names chosen for readability
            public int AppId { get; set; }
            public string? Name { get; set; }
        }

        private sealed class SteamDbContainer
        {
            public string? LastUpdatedUtc { get; set; }
            public int Total { get; set; }
            public List<SteamAppEntry>? Apps { get; set; }
        }
    }
}