using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace PS5_OS
{
    // Background helper to fetch Steam app list and persist per-account metadata JSON files.
    // Usage: fire-and-forget on app start while intro video plays:
    // _ = SteamAppListUpdater.EnsureSteamMetadataAsync();
    public static class SteamAppListUpdater
    {
        private const string SteamGetAppListUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";

        public static Task EnsureSteamMetadataAsync()
            => EnsureSteamMetadataAsync(null, "PC (Windows)");

        // accountFolder: optional. If null the method resolves the currently-logged in account folder consistent with other code.
        // platformName: typically "PC (Windows)"
        public static async Task EnsureSteamMetadataAsync(string? accountFolder, string platformName)
        {
            try
            {
                accountFolder ??= ResolveAccountFolder();
                if (string.IsNullOrWhiteSpace(accountFolder)) return;

                var metadataRoot = Path.Combine(accountFolder, "Metadata", platformName);
                Directory.CreateDirectory(metadataRoot);

                // If metadataRoot contains any game metadata JSON already, treat this as subsequent run;
                // otherwise treat as first-run and create entries for all Steam apps.
                bool hasExisting = Directory.EnumerateFiles(metadataRoot, "*.json", SearchOption.AllDirectories).Any();
                var apps = await FetchSteamAppListAsync().ConfigureAwait(false);
                if (apps == null || apps.Count == 0) return;

                // Write only missing entries on subsequent runs; on first run create all (i.e. write if file missing).
                // Perform writes on background thread to avoid UI blocking.
                await Task.Run(() =>
                {
                    var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var app in apps)
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(app.Name)) continue;

                            var sanitized = SanitizeForPath(app.Name);
                            if (string.IsNullOrWhiteSpace(sanitized)) sanitized = $"App_{app.AppId}";

                            // Avoid duplicate directory collisions in case different app names sanitize to same string.
                            // If sanitized directory already used for a different AppId, append appid suffix.
                            var candidateDir = Path.Combine(metadataRoot, sanitized);
                            if (seenDirs.Contains(candidateDir))
                            {
                                candidateDir = Path.Combine(metadataRoot, $"{sanitized} ({app.AppId})");
                            }

                            // If directory already exists examine file presence and content
                            var finalFile = Path.Combine(candidateDir, sanitized + ".json");
                            if (File.Exists(finalFile))
                            {
                                // subsequent run: skip existing
                                if (hasExisting) continue;

                                // first run: if file exists, still skip
                                continue;
                            }

                            // Ensure unique dir name to avoid second collision
                            var ensureDir = candidateDir;
                            var idx = 1;
                            while (seenDirs.Contains(ensureDir))
                            {
                                ensureDir = Path.Combine(metadataRoot, $"{sanitized}-{idx}");
                                idx++;
                            }

                            Directory.CreateDirectory(ensureDir);
                            seenDirs.Add(ensureDir);

                            var obj = new
                            {
                                SteamAppId = app.AppId.ToString(),
                                MicrosoftId = (string?)null,
                                EpicId = (string?)null,
                                EAId = (string?)null,
                                UplayId = (string?)null,
                                GogId = (string?)null
                            };

                            var options = new JsonSerializerOptions { WriteIndented = true };
                            var json = JsonSerializer.Serialize(obj, options);

                            // Write file
                            File.WriteAllText(Path.Combine(ensureDir, sanitized + ".json"), json);
                        }
                        catch
                        {
                            // ignore per-app write errors
                        }
                    }
                }).ConfigureAwait(false);
            }
            catch
            {
                // non-fatal: do not throw on app start
            }
        }

        private static async Task<List<(int AppId, string Name)>> FetchSteamAppListAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("PS5_OS_MetadataUpdater/1.0");
                var resp = await client.GetAsync(SteamGetAppListUrl).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return new List<(int, string)>();

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json)) return new List<(int, string)>();

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("applist", out var applist)) return new List<(int, string)>();
                if (!applist.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array) return new List<(int, string)>();

                var list = new List<(int, string)>();
                foreach (var a in apps.EnumerateArray())
                {
                    try
                    {
                        if (a.TryGetProperty("appid", out var idProp) && a.TryGetProperty("name", out var nameProp))
                        {
                            if (idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt32(out var id))
                            {
                                var name = nameProp.GetString() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(name))
                                    list.Add((id, name));
                            }
                        }
                    }
                    catch { }
                }

                return list;
            }
            catch
            {
                return new List<(int, string)>();
            }
        }

        // Resolve account folder consistently with other pages in the app
        private static string ResolveAccountFolder()
        {
            try
            {
                if (Application.Current?.Properties["LoggedInAccountPath"] is string p && !string.IsNullOrWhiteSpace(p))
                {
                    if (!Directory.Exists(p)) Directory.CreateDirectory(p);
                    return p;
                }

                if (Application.Current?.Properties["LoggedInAccount"] is string name && !string.IsNullOrWhiteSpace(name))
                {
                    var folder = Path.Combine(AppContext.BaseDirectory, "Data", "Accounts", name);
                    if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                    return folder;
                }

                var guest = Path.Combine(AppContext.BaseDirectory, "Data", "Accounts", "Guest");
                if (!Directory.Exists(guest)) Directory.CreateDirectory(guest);
                return guest;
            }
            catch
            {
                var safe = Path.Combine(AppContext.BaseDirectory, "Data", "Accounts", "Guest");
                Directory.CreateDirectory(safe);
                return safe;
            }
        }

        // Local copy of project's sanitizer to ensure file names match other metadata paths
        private static string SanitizeForPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "Unknown";

            var s = input.Trim();

            if ((s.StartsWith("\"") && s.EndsWith("\"")) || (s.StartsWith("'") && s.EndsWith("'")))
                s = s.Substring(1, s.Length - 2).Trim();

            s = s.Replace(":", " - ");
            s = s.Replace("／", " - ").Replace("/", " - ").Replace("\\", " - ");
            s = s.Replace("–", "-").Replace("—", "-");
            s = s.Replace('_', ' ');

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (invalid.Contains(ch))
                    sb.Append(' ');
                else
                    sb.Append(ch);
            }
            s = sb.ToString();

            s = Regex.Replace(s, @"\s*-\s*", " - ");
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            s = Regex.Replace(s, @"-+", "-");
            s = s.Trim(' ', '-', '_');
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();

            return string.IsNullOrWhiteSpace(s) ? "Unknown" : s;
        }
    }
}