// (replace your current FindGames.cs with this)
using App.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace App.Services
{
    public static class FindGames
    {
        /// <summary>
        /// Result for a discovered game or rom.
        /// </summary>
        // ExecutablePaths contains all found .exe files under the game's folder (may be null/empty).
        public sealed record GameInfo(string Name, string InstallPath, IReadOnlyList<string>? ExecutablePaths, bool IsWindowsExecutable, string? Region, IReadOnlyList<string>? Languages, string? Platform);

        private static readonly string[] ThreeDsExtensions = new[] { ".3ds", ".cia" };

        // Extensions per platform (lowercase). This is a conservative, extensible list.
        private static readonly Dictionary<string, string[]> PlatformExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            { "3DS", new[] { ".3ds", ".cia" } },
            { "DS", new[] { ".nds" } },
            { "DSI", new[] { ".nds", ".dsi" } },
            { "Switch", new[] { ".xci", ".nsp" } },
            { "N64", new[] { ".z64", ".n64", ".v64" } },
            { "NES", new[] { ".nes" } },
            { "SNES", new[] { ".sfc", ".smc" } },
            { "GB", new[] { ".gb" } },
            { "GBC", new[] { ".gbc" } },
            { "GBA", new[] { ".gba" } },
            { "PS1", new[] { ".iso", ".bin", ".cue", ".chd" } },
            { "PS2", new[] { ".iso", ".chd"} },
            { "PS3", new[] { ".iso" } },
            { "PS4", new[] { ".iso" } },
            { "PSP", new[] { ".iso", ".cso" } },
            { "PSV", new[] { ".vpk", ".vpkx" } }, // heuristics
            { "Xbox", new[] { ".iso", ".xbox" } },
            { "Xbox 360", new[] { ".iso", ".zar", ".xex" } },
            { "Wii", new[] { ".iso", ".wbs", ".rvz" } },
            // PC is handled separately (folder-per-game with .exe detection)
        };

        // Map platform ids used in AllGamesPage to the folder names used under <Drive>:\Roms\<Folder>\Games
        private static readonly Dictionary<string, string> PlatformFolderMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "3DS", "Nintendo - 3DS" },
            { "DSI", "Nintendo - DSi" },
            { "DS", "Nintendo - DS" },
            { "Switch", "Nintendo - Switch" },
            { "N64", "Nintendo - N64" },
            { "NES", "Nintendo - NES" },
            { "SNES", "Nintendo - SNES" },
            { "GB", "Nintendo - GB" },
            { "GBA", "Nintendo - GBA" },
            { "GBC", "Nintendo - GBC" },
            { "Wii", "Nintendo - Wii" },

            { "Xbox", "Microsoft - Xbox" },
            { "Xbox 360", "Microsoft - Xbox 360" },

            { "PS1", "Sony - Playstation" },
            { "PS2", "Sony - Playstation 2" },
            { "PS3", "Sony - Playstation 3" },
            { "PS4", "Sony - Playstation 4" },
            { "PSP", "Sony - PSP" },
            { "PSV", "Sony - PSV" },

            // PC handled via <Drive>:\Games\<GameName>
            { "PC", "PC (Windows)" }
        };

        /// <summary>
        /// Scans all ready fixed and removable drives for configured locations and returns discovered games.
        /// Cancelable and reports coarse progress (0..1).
        /// </summary>
        public static Task<List<GameInfo>> FindGamesAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            var results = new List<GameInfo>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                    .ToArray();
            }
            catch
            {
                return Task.FromResult(results);
            }

            if (drives.Length == 0)
            {
                progress?.Report(1.0);
                return Task.FromResult(results);
            }

            int processed = 0;
            foreach (var drive in drives)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // 1) PC Games: <Root>\Games\<GameName>
                    string gamesRoot = Path.Combine(drive.RootDirectory.FullName, "Games");
                    if (Directory.Exists(gamesRoot))
                    {
                        IEnumerable<string> gameDirs;
                        try
                        {
                            gameDirs = Directory.EnumerateDirectories(gamesRoot, "*", SearchOption.TopDirectoryOnly);
                        }
                        catch
                        {
                            gameDirs = Array.Empty<string>();
                        }

                        foreach (var gameDir in gameDirs)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string rawName = Path.GetFileName(gameDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? gameDir;

                            // Recursive (safe) search for any executables under the game folder.
                            var exes = SafeEnumerateFiles(gameDir, "*.exe")
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList();

                            string? preferredExe = exes.FirstOrDefault();
                            bool isWin = exes.Count > 0;

                            var cleaned = CleanNameAndExtractMetadata(rawName, out var region, out var languages);

                            var key = $"{cleaned}|PC";
                            if (seenKeys.Add(key))
                            {
                                // set Platform to "PC" and include all found executables
                                results.Add(new GameInfo(cleaned, gameDir, exes.Count > 0 ? exes : null, isWin, region, languages, "PC"));
                            }
                        }
                    }

                    // 2) ROM folders: iterate platform folder mappings (skip PC)
                    foreach (var kv in PlatformFolderMap)
                    {
                        var platformId = kv.Key;
                        if (string.Equals(platformId, "PC", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var folderName = kv.Value;
                        var romRoot = Path.Combine(drive.RootDirectory.FullName, "Roms", folderName, "Games");
                        if (!Directory.Exists(romRoot)) continue;

                        // a) loose files in Games folder
                        IEnumerable<string> romFilesTop;
                        try
                        {
                            romFilesTop = Directory.EnumerateFiles(romRoot, "*.*", SearchOption.TopDirectoryOnly)
                                .Where(f => IsFileExtensionMatchingPlatform(f, platformId));
                        }
                        catch
                        {
                            romFilesTop = Array.Empty<string>();
                        }

                        foreach (var romFile in romFilesTop)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var raw = Path.GetFileNameWithoutExtension(romFile);
                            var cleaned = CleanNameAndExtractMetadata(raw, out var region, out var languages);

                            var key = $"{cleaned}|{platformId}";
                            if (seenKeys.Add(key))
                            {
                                // include platformId
                                results.Add(new GameInfo(cleaned, romFile, null, false, region, languages, platformId));
                            }
                        }

                        // b) immediate subfolders with rom files
                        IEnumerable<string> romSubDirs;
                        try
                        {
                            romSubDirs = Directory.EnumerateDirectories(romRoot, "*", SearchOption.TopDirectoryOnly);
                        }
                        catch
                        {
                            romSubDirs = Array.Empty<string>();
                        }

                        foreach (var sub in romSubDirs)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            IEnumerable<string> romsInSub;
                            try
                            {
                                romsInSub = Directory.EnumerateFiles(sub, "*.*", SearchOption.TopDirectoryOnly)
                                    .Where(f => IsFileExtensionMatchingPlatform(f, platformId));
                            }
                            catch
                            {
                                romsInSub = Array.Empty<string>();
                            }

                            foreach (var romFile in romsInSub)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                var raw = Path.GetFileNameWithoutExtension(romFile);
                                var cleaned = CleanNameAndExtractMetadata(raw, out var region, out var languages);

                                var key = $"{cleaned}|{platformId}";
                                if (seenKeys.Add(key))
                                {
                                    results.Add(new GameInfo(cleaned, romFile, null, false, region, languages, platformId));
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore drive-level exceptions and continue scanning other drives.
                }

                processed++;
                progress?.Report((double)processed / drives.Length);
            }

            return Task.FromResult(results);
        }

        private static bool IsFileExtensionMatchingPlatform(string path, string platformId)
        {
            try
            {
                var ext = Path.GetExtension(path) ?? string.Empty;
                if (string.IsNullOrEmpty(ext)) return false;
                ext = ext.ToLowerInvariant();

                if (PlatformExtensions.TryGetValue(platformId, out var arr))
                {
                    return arr.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
                }

                // If we don't know extensions for a platform, accept common ROM-like files
                var common = new[] { ".zip", ".7z", ".rar", ".iso", ".bin", ".nds", ".gba", ".3ds", ".xci", ".nsp", ".chd" };
                return common.Contains(ext, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // Cleans a raw name and extracts region token and language codes (if present).
        private static string CleanNameAndExtractMetadata(string raw, out string? region, out IReadOnlyList<string>? languages)
        {
            region = null;
            languages = null;

            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var s = raw.Trim();

            // remove surrounding quotes
            if ((s.StartsWith("\"") && s.EndsWith("\"")) || (s.StartsWith("'") && s.EndsWith("'")))
            {
                s = s.Substring(1, s.Length - 2).Trim();
            }

            // Replace underscores with spaces and collapse multiple spaces
            s = s.Replace('_', ' ');
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();

            // Find all parenthetical tokens and process them
            var matches = Regex.Matches(s, @"\(([^)]*)\)").Cast<Match>().ToList();
            foreach (var m in matches)
            {
                if (!m.Success || m.Groups.Count < 2) continue;

                var token = m.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(token)) continue;

                // Language list heuristics: e.g. "En,Fr,De,Es,It" or "EN,JA,FR"
                if (Regex.IsMatch(token, @"^[A-Za-z0-9]{1,4}(?:\s*[,/]\s*[A-Za-z0-9]{1,4})*$"))
                {
                    var parts = token.Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(p => p.Trim().ToUpperInvariant())
                                     .Where(p => p.Length > 0)
                                     .ToList();

                    if (parts.Count > 0)
                    {
                        var list = languages != null ? new List<string>(languages) : new List<string>();
                        foreach (var it in parts)
                        {
                            if (!list.Contains(it, StringComparer.OrdinalIgnoreCase))
                                list.Add(it);
                        }

                        languages = list;
                        s = s.Replace(m.Value, "").Trim();
                        continue;
                    }
                }

                // Region heuristic (short token, letters/digits/spaces/hyphen)
                if (token.Length <= 16 && Regex.IsMatch(token, @"^[A-Za-z0-9 \-_/]+$"))
                {
                    if (region == null)
                    {
                        region = token;
                        s = s.Replace(m.Value, "").Trim();
                        continue;
                    }
                }
            }

            // collapse spaces
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();

            // convert separator hyphen to colon when hyphen is not part of a hyphenated word
            var idx = s.IndexOf('-');
            if (idx >= 0)
            {
                bool leftIsLetter = idx > 0 && char.IsLetter(s[idx - 1]);
                bool rightIsLetter = idx + 1 < s.Length && char.IsLetter(s[idx + 1]);

                if (!(leftIsLetter && rightIsLetter))
                {
                    s = Regex.Replace(s, @"\s*-\s*", ": ", RegexOptions.None, TimeSpan.FromMilliseconds(100));
                }
            }

            s = s.Trim();
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();

            return s;
        }

        // Safe recursive enumerator for files that skips directories which throw (access denied, etc.)
        private static IEnumerable<string> SafeEnumerateFiles(string root, string searchPattern)
        {
            if (string.IsNullOrEmpty(root)) yield break;

            var q = new Queue<string>();
            q.Enqueue(root);

            while (q.Count > 0)
            {
                var dir = q.Dequeue();

                IEnumerable<string>? files = null;
                try
                {
                    files = Directory.EnumerateFiles(dir, searchPattern, SearchOption.TopDirectoryOnly);
                }
                catch { }

                if (files != null)
                {
                    foreach (var f in files)
                        yield return f;
                }

                IEnumerable<string>? subs = null;
                try
                {
                    subs = Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly);
                }
                catch { }

                if (subs != null)
                {
                    foreach (var s in subs)
                        q.Enqueue(s);
                }
            }
        }
    }
}