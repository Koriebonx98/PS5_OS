using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Net;

namespace PS5_OS
{
    public partial class GameInfoWindow : Window
    {
        public GameItem Game { get; } = default!;

        // Achievements collection exposed for binding
        public ObservableCollection<AchievementEntry> Achievements { get; } = new();

        // Display string for achievements file path (bound from XAML)
        public string AchievementsFileDisplay { get; private set; } = "(none)";

        // session-selected exe path (kept here for launch fallback if needed)
        private string? _selectedExeOverride;
        private string? _selectedExeOverrideArgs;

        // audio
        private SoundPlayer? _navPlayer;
        private SoundPlayer? _actPlayer;

        public GameInfoWindow(GameItem game)
        {
            InitializeComponent();

            Game = game ?? throw new ArgumentNullException(nameof(game));
            DataContext = Game;

            // audio
            TryLoadAudioPlayers();

            // load persisted platform ids / preferred exe handling (existing behavior)
            try
            {
                var saved = PreferredExeStore.GetPreferredExe(Game.Title);
                var savedArgs = PreferredExeStore.GetPreferredExeArguments(Game.Title);

                if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved))
                {
                    _selectedExeOverride = saved;
                    _selectedExeOverrideArgs = savedArgs;
                    UpdateSelectedText(_selectedExeOverride);
                    LaunchButton.IsEnabled = true;
                }
                else
                {
                    LaunchButton.IsEnabled = !string.IsNullOrWhiteSpace(Game.LaunchPath);
                    UpdateSelectedText(Game.LaunchPath);
                }
            }
            catch
            {
                LaunchButton.IsEnabled = !string.IsNullOrWhiteSpace(Game.LaunchPath);
                UpdateSelectedText(Game.LaunchPath);
            }

            // Load achievements for this game (non-blocking)
            try
            {
                LoadAchievements();
            }
            catch
            {
                // non-fatal - silently ignore errors loading achievements
            }

            Loaded += (s, e) =>
            {
                Keyboard.Focus(this);
            };
        }

        // Helper to safely execute UI updates on the application's Dispatcher
        private Task RunOnUiThreadAsync(Action action)
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null)
            {
                // no dispatcher available (unlikely in WPF), execute inline
                action();
                return Task.CompletedTask;
            }

            if (disp.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return disp.InvokeAsync(action).Task;
        }

        private void TryLoadAudioPlayers()
        {
            try
            {
                var baseDir = Path.Combine(AppContext.BaseDirectory, "Data", "Resources", "Dashboard");
                var navPath = Path.Combine(baseDir, "navigation.wav");
                var actPath = Path.Combine(baseDir, "activation.wav");

                if (File.Exists(navPath))
                {
                    try
                    {
                        _navPlayer = new SoundPlayer(navPath);
                        _navPlayer.LoadAsync();
                    }
                    catch { _navPlayer = null; }
                }

                if (File.Exists(actPath))
                {
                    try
                    {
                        _actPlayer = new SoundPlayer(actPath);
                        _actPlayer.LoadAsync();
                    }
                    catch { _actPlayer = null; }
                }
            }
            catch
            {
                _navPlayer = null;
                _actPlayer = null;
            }
        }

        private void PlayNavigation()
        {
            try { _navPlayer?.Play(); } catch { }
        }

        private void PlayActivation()
        {
            try { _actPlayer?.Play(); } catch { }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }

            // play navigation sound for arrow keys
            if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down)
            {
                PlayNavigation();
            }

            // play activation sound for Enter/Space
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                PlayActivation();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            // reuse the public method for the button handler so logic stays in one place
            LaunchGame();
        }

        public void LaunchGame()
        {
            var inputPath = (_selectedExeOverride ?? Game.LaunchPath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                MessageBox.Show("No launch path is available for this game.", "Cannot Launch", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                if (!TryResolveExecutable(inputPath, out var exePath, out var workingDir))
                {
                    MessageBox.Show("Could not locate a valid executable to launch in the provided path.", "Cannot Launch", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var args = _selectedExeOverrideArgs ?? PreferredExeStore.GetPreferredExeArguments(Game.Title) ?? string.Empty;

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = workingDir,
                    Arguments = args,
                    UseShellExecute = true
                };
                Process.Start(psi);

                // Update per-account All.GamesData.json and mirror top-5 into LastPlayed.json
                try
                {
                    var accountFolder = GetAccountFolder();
                    UpdateAllGamesData(accountFolder, Game);
                }
                catch
                {
                    // non-fatal - do not block launch on telemetry write failures
                }

                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch {Game.Title}: {ex.Message}", "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Update All.GamesData.json (Data/Accounts/<Account>/All.GamesData.json)
        // This method updates the full All.GamesData.json (adds or updates the launched game's entry),
        // then mirrors the most recent five entries into LastPlayed.json using the same schema.
        private void UpdateAllGamesData(string accountFolder, GameItem launchedGame)
        {
            if (launchedGame == null || string.IsNullOrWhiteSpace(launchedGame.Title)) return;

            var allDataFile = Path.Combine(accountFolder, "All.GamesData.json");
            var lastPlayedFile = Path.Combine(accountFolder, "LastPlayed.json");

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };

                // Load existing All.GamesData entries so we can preserve TotalPlayTime and prior counts
                var entries = new List<AllGamesDataEntry>();
                if (File.Exists(allDataFile))
                {
                    try
                    {
                        var json = File.ReadAllText(allDataFile);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var parsed = JsonSerializer.Deserialize<List<AllGamesDataEntry>>(json, options);
                            if (parsed != null) entries = parsed;
                        }
                    }
                    catch
                    {
                        entries = new List<AllGamesDataEntry>();
                    }
                }

                var nowIso = DateTime.UtcNow.ToString("o");
                var title = launchedGame.Title.Trim();

                // Find existing entry (case-insensitive)
                var match = entries.FirstOrDefault(e => string.Equals(e.GameName?.Trim(), title, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    match = new AllGamesDataEntry
                    {
                        GameName = title,
                        PlatformName = string.IsNullOrWhiteSpace(launchedGame.Platform) ? "Unknown" : launchedGame.Platform,
                        TotalPlayTime = 0.0,
                        TotalTimesPlayed = 1,
                        LastPlayedDate = nowIso
                    };
                    // Insert at front to keep newest first
                    entries.Insert(0, match);
                }
                else
                {
                    // Update existing entry
                    match.TotalTimesPlayed = (match.TotalTimesPlayed <= 0) ? 1 : match.TotalTimesPlayed + 1;
                    match.PlatformName = !string.IsNullOrWhiteSpace(launchedGame.Platform) ? launchedGame.Platform : match.PlatformName ?? "Unknown";
                    match.LastPlayedDate = nowIso;

                    // Move updated entry to front (most-recent)
                    entries.Remove(match);
                    entries.Insert(0, match);
                }

                // Persist full All.GamesData.json
                File.WriteAllText(allDataFile, JsonSerializer.Serialize(entries, options));

                // Mirror the top 5 most recent entries into LastPlayed.json using the same schema
                var lastFive = entries.Where(e => !string.IsNullOrWhiteSpace(e.LastPlayedDate))
                                     .OrderByDescending(e => ParseIsoDate(e.LastPlayedDate))
                                     .Take(5)
                                     .ToList();

                // If LastPlayed should include entries without LastPlayedDate, include those after dated ones
                if (lastFive.Count < 5)
                {
                    var additional = entries.Where(e => string.IsNullOrWhiteSpace(e.LastPlayedDate))
                                            .Take(5 - lastFive.Count);
                    lastFive.AddRange(additional);
                }

                File.WriteAllText(lastPlayedFile, JsonSerializer.Serialize(lastFive, options));
            }
            catch
            {
                // swallow errors - do not disrupt launch flow
            }
        }

        private static DateTime ParseIsoDate(string? iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return DateTime.MinValue;
            if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)) return dt;
            return DateTime.MinValue;
        }

        // Simple types used for serialization (All.GamesData.json and mirrored LastPlayed.json)
        private sealed class AllGamesDataEntry
        {
            public string? GameName { get; set; }
            public string? PlatformName { get; set; }

            // Total play time stored in seconds (double to allow fractional seconds if needed)
            public double TotalPlayTime { get; set; }

            public int TotalTimesPlayed { get; set; }

            // ISO 8601 timestamp for LastPlayedDate
            public string? LastPlayedDate { get; set; }
        }

        // Open the full-screen Properties window (modal)
        private void PropertiesButton_Click(object sender, RoutedEventArgs e)
        {
            var props = new PropertiesWindow(Game)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var result = props.ShowDialog();

            if (result == true && (!string.IsNullOrWhiteSpace(props.SelectedExe) || !string.IsNullOrWhiteSpace(props.SelectedExeArgs)))
            {
                _selectedExeOverride = props.SelectedExe;
                _selectedExeOverrideArgs = props.SelectedExeArgs;
                LaunchButton.IsEnabled = true;
                UpdateSelectedText(_selectedExeOverride);
                foreach (var rb in ExecutablesPanel?.Children.OfType<RadioButton>() ?? Enumerable.Empty<RadioButton>())
                {
                    rb.IsChecked = string.Equals(rb.Tag as string, _selectedExeOverride, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // Show only the game folder (drive + Games\<GameFolder>) — do not include the executable filename.
        private void UpdateSelectedText(string? path)
        {
            string displayFolder;
            if (string.IsNullOrWhiteSpace(path))
            {
                displayFolder = "(none)";
            }
            else
            {
                try
                {
                    displayFolder = GetGameFolderFromPath(path, Game.DisplayTitle);
                }
                catch
                {
                    try
                    {
                        var parent = Path.GetDirectoryName(path ?? string.Empty);
                        displayFolder = string.IsNullOrWhiteSpace(parent) ? (path ?? "(none)") : parent;
                    }
                    catch
                    {
                        displayFolder = path ?? "(none)";
                    }
                }
            }

            if (LaunchPathText != null)
                LaunchPathText.Text = displayFolder;

            var exeName = string.IsNullOrEmpty(path) ? "(none)" : System.IO.Path.GetFileName(path);
            if (CurrentPreferredText != null)
                CurrentPreferredText.Text = $"Current Preferred: {exeName}";
        }

        // Heuristic: given an exe path or folder, return the path up to (and including) the game folder in a Games tree:
        // - Prefer "X:\... \Games\<GameFolder>" if present in path.
        // - Otherwise, if a segment matches or contains displayTitle, return the containing folder.
        // - Otherwise return the parent directory (folder) portion.
        private string GetGameFolderFromPath(string path, string displayTitle)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "(none)";

            // Normalize to folder: if it's a file path, take its directory
            string folder = path;
            if (Path.HasExtension(path) || File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    folder = dir;
            }
            else if (Directory.Exists(path))
            {
                folder = Path.GetFullPath(path);
            }

            // Normalize separators to platform default
            folder = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Try to find "\Games\<GameFolder>" pattern
            var gamesToken = $"{Path.DirectorySeparatorChar}Games{Path.DirectorySeparatorChar}";
            var idx = folder.IndexOf(gamesToken, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                // afterGames points to start of game folder name
                var afterGames = idx + gamesToken.Length;
                // find next separator after the game folder name (if any)
                var nextSep = folder.IndexOf(Path.DirectorySeparatorChar, afterGames);
                if (nextSep < 0)
                {
                    var remaining = folder.Substring(afterGames);
                    var firstSeg = remaining;
                    var rebuilt = folder.Substring(0, afterGames) + firstSeg;
                    return rebuilt;
                }
                else
                {
                    // include up to the first separator after the game folder -> that's the exact Games\<GameFolder> path
                    return folder.Substring(0, nextSep);
                }
            }

            // If no explicit Games token, try to locate a segment matching displayTitle (case-insensitive)
            if (!string.IsNullOrWhiteSpace(displayTitle))
            {
                var matchIdx = folder.IndexOf(displayTitle, StringComparison.OrdinalIgnoreCase);
                if (matchIdx >= 0)
                {
                    // find previous separator before match (start from 0 to matchIdx)
                    var prevSep = folder.LastIndexOf(Path.DirectorySeparatorChar, Math.Max(0, matchIdx - 1));
                    var start = prevSep < 0 ? 0 : prevSep + 1;

                    // find next separator after the match
                    var afterMatch = matchIdx + displayTitle.Length;
                    var nextSep2 = folder.IndexOf(Path.DirectorySeparatorChar, afterMatch);
                    if (nextSep2 < 0)
                    {
                        var parent = Path.GetDirectoryName(folder) ?? folder;
                        return parent;
                    }
                    else
                    {
                        var candidate = folder.Substring(0, nextSep2);
                        return candidate;
                    }
                }
            }

            // Generic fallback: return parent directory (folder)
            try
            {
                var parent = Path.GetDirectoryName(folder);
                if (!string.IsNullOrWhiteSpace(parent))
                    return parent;
            }
            catch { /* ignore */ }

            return folder;
        }

        // Try to resolve an executable and its working directory from a provided path.
        // The provided input can be:
        // - full path to an exe,
        // - a folder containing the exe,
        // - a path to a file that doesn't exist (we'll try the parent folder).
        // The method uses simple heuristics:
        //  1) prefer an exe with the same name as Game.DisplayTitle,
        //  2) prefer any top-level exe in the folder,
        //  3) look into immediate subfolders (depth 1).
        private bool TryResolveExecutable(string? inputPath, out string exePath, out string workingDir)
        {
            exePath = string.Empty;
            workingDir = string.Empty;

            try
            {
                if (string.IsNullOrWhiteSpace(inputPath))
                    return false;

                // Normalize and expand
                var candidate = inputPath!;
                try { candidate = Path.GetFullPath(candidate); } catch { /* keep original */ }

                // If it's a file that exists and has an extension, use it directly
                if (Path.HasExtension(candidate) && File.Exists(candidate))
                {
                    exePath = candidate;
                    workingDir = Path.GetDirectoryName(candidate) ?? Environment.CurrentDirectory;
                    return true;
                }

                // If input looks like a file path but doesn't exist, try its parent as folder
                string folder = candidate;
                if (Path.HasExtension(candidate))
                {
                    var parent = Path.GetDirectoryName(candidate);
                    if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                        folder = parent;
                }
                else if (!Directory.Exists(folder))
                {
                    // try parent of the provided path
                    var parent = Path.GetDirectoryName(folder);
                    if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                        folder = parent;
                }

                if (!Directory.Exists(folder))
                    return false;

                // Heuristics search
                // 1) exact match Game.DisplayTitle.exe
                if (!string.IsNullOrWhiteSpace(Game?.DisplayTitle))
                {
                    try
                    {
                        var exact = Path.Combine(folder, Game.DisplayTitle + ".exe");
                        if (File.Exists(exact))
                        {
                            exePath = exact;
                            workingDir = folder;
                            return true;
                        }

                        // 1b) any exe whose filename contains the display title
                        var match = Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                                             .FirstOrDefault(f => Path.GetFileName(f).IndexOf(Game.DisplayTitle, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!string.IsNullOrWhiteSpace(match))
                        {
                            exePath = match;
                            workingDir = folder;
                            return true;
                        }
                    }
                    catch { /* ignore IO issues and continue */ }
                }

                // 2) any top-level exe in folder
                try
                {
                    var any = Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(any))
                    {
                        exePath = any;
                        workingDir = folder;
                        return true;
                    }
                }
                catch { /* ignore */ }

                // 3) search immediate subdirectories (depth 1)
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(folder))
                    {
                        try
                        {
                            var top = Directory.EnumerateFiles(sub, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(top))
                            {
                                exePath = top;
                                workingDir = Path.GetDirectoryName(top) ?? sub;
                                return true;
                            }

                            if (!string.IsNullOrWhiteSpace(Game?.DisplayTitle))
                            {
                                var nested = Directory.EnumerateFiles(sub, "*.exe", SearchOption.AllDirectories)
                                                      .FirstOrDefault(f => Path.GetFileName(f).IndexOf(Game.DisplayTitle, StringComparison.OrdinalIgnoreCase) >= 0);
                                if (!string.IsNullOrWhiteSpace(nested))
                                {
                                    exePath = nested;
                                    workingDir = Path.GetDirectoryName(nested) ?? sub;
                                    return true;
                                }
                            }
                        }
                        catch { /* continue to next subfolder */ }
                    }
                }
                catch { /* ignore */ }
            }
            catch { /* ignore high-level errors */ }

            return false;
        }

        // Account folder helper (keeps behavior consistent with other pages)
        private string GetAccountFolder()
        {
            try
            {
                // prefer explicit path set during login
                if (Application.Current?.Properties["LoggedInAccountPath"] is string p && !string.IsNullOrWhiteSpace(p))
                {
                    if (!Directory.Exists(p)) Directory.CreateDirectory(p);
                    return p;
                }

                // fallback to account name (LoginPage sets LoggedInAccount)
                if (Application.Current?.Properties["LoggedInAccount"] is string name && !string.IsNullOrWhiteSpace(name))
                {
                    var folder = Path.Combine(AppContext.BaseDirectory, "Data", "Accounts", name);
                    if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                    return folder;
                }

                // last resort, create a Guest account folder
                var guest = Path.Combine(AppContext.BaseDirectory, "Data", "Accounts", "Guest");
                if (!Directory.Exists(guest)) Directory.CreateDirectory(guest);
                return guest;
            }
            catch
            {
                // fallback to a safe location inside base directory
                var safe = Path.Combine(AppContext.BaseDirectory, "Data", "Accounts", "Guest");
                Directory.CreateDirectory(safe);
                return safe;
            }
        }

        // Sanitize a string for use as a filename.  Produces "Call of Duty - Black Ops 2" from "Call of Duty: Black Ops 2".
        // Matches sanitizer used by PropertiesWindow so both metadata and caches use identical names.
        private static string SanitizeForPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "Unknown";

            var s = input.Trim();

            // Remove surrounding quotes
            if ((s.StartsWith("\"") && s.EndsWith("\"")) || (s.StartsWith("'") && s.EndsWith("'")))
                s = s.Substring(1, s.Length - 2).Trim();

            // Replace common separators with a spaced hyphen
            s = s.Replace(":", " - ");
            s = s.Replace("／", " - ").Replace("/", " - ").Replace("\\", " - ");
            s = s.Replace("–", "-").Replace("—", "-");

            // Replace underscores with spaces
            s = s.Replace('_', ' ');

            // Remove invalid filename characters by replacing them with a space
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

            // Normalize hyphen spacing and collapse multiple spaces
            s = Regex.Replace(s, @"\s*-\s*", " - ");
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();

            // Collapse multiple hyphens into single and trim
            s = Regex.Replace(s, @"-+", "-");
            s = s.Trim(' ', '-', '_');

            // Final collapse of multiple spaces
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();

            return string.IsNullOrWhiteSpace(s) ? "Unknown" : s;
        }

        // Load achievements for the current game from the per-account Achievements folder.
        // Expected path pattern:
        // Data/Accounts/<Account>/Achievements/<Platform>/<SanitizedGameTitle>/<SanitizedGameTitle>.json
        private void LoadAchievements()
        {
            Achievements.Clear();

            try
            {
                var accountFolder = GetAccountFolder();
                if (string.IsNullOrWhiteSpace(accountFolder) || !Directory.Exists(accountFolder))
                {
                    OnPropertyChanged(nameof(AchievementsSummary));
                    return;
                }

                // Use robust detection that matches how achievements are stored (handles non-PC platforms)
                var platformFolder = MapPlatformToFolder(Game);
                var sanitizedTitle = SanitizeForPath(Game.Title ?? Game.DisplayTitle ?? "Unknown");

                var achFolder = Path.Combine(accountFolder, "Achievements", platformFolder, sanitizedTitle);
                var achFile = Path.Combine(achFolder, sanitizedTitle + ".json");

                // Check existence (no user-facing message box)
                bool fileExists;
                try
                {
                    fileExists = File.Exists(achFile);
                }
                catch
                {
                    fileExists = false;
                }

                if (!fileExists)
                {
                    // no achievements file present
                    AchievementsFileDisplay = "(none)";
                    OnPropertyChanged(nameof(AchievementsFileDisplay));
                    OnPropertyChanged(nameof(AchievementsSummary));
                    return;
                }

                var json = File.ReadAllText(achFile);

                // update display to the file we loaded
                AchievementsFileDisplay = achFile;
                OnPropertyChanged(nameof(AchievementsFileDisplay));

                if (string.IsNullOrWhiteSpace(json))
                {
                    OnPropertyChanged(nameof(AchievementsSummary));
                    return;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                IEnumerable<JsonElement> items = Enumerable.Empty<JsonElement>();

                if (root.ValueKind == JsonValueKind.Array)
                {
                    items = root.EnumerateArray();
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    // common container names
                    if (root.TryGetProperty("achievements", out var p) && p.ValueKind == JsonValueKind.Array)
                        items = p.EnumerateArray();
                    else if (root.TryGetProperty("items", out var p2) && p2.ValueKind == JsonValueKind.Array)
                        items = p2.EnumerateArray();
                    else if (root.TryGetProperty("data", out var p3) && p3.ValueKind == JsonValueKind.Array)
                        items = p3.EnumerateArray();
                    else
                    {
                        // fallback: object with properties where each property is an achievement object
                        var list = new List<JsonElement>();
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                                list.Add(prop.Value);
                        }
                        if (list.Count > 0)
                            items = list;
                        else
                        {
                            // final fallback: treat root as a single achievement object
                            items = new[] { root };
                        }
                    }
                }

                foreach (var elt in items)
                {
                    try
                    {
                        // name / description
                        string? name = TryGetStringProperty(elt, new[] { "name", "title", "label", "id" });
                        string? desc = TryGetStringProperty(elt, new[] { "description", "desc", "details" });

                        // decode HTML-escaped description/name if present (Exophase may store HTML-escaped fragments)
                        if (!string.IsNullOrWhiteSpace(desc))
                            desc = WebUtility.HtmlDecode(desc).Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                            name = WebUtility.HtmlDecode(name).Trim();

                        // unlocked flag (may be present or absent)
                        bool unlocked = TryGetBoolProperty(elt, new[] { "unlocked", "achieved", "completed", "isUnlocked", "is_achieved" });

                        // unlocked date - accept many common keys including Exophase's "DateUnlocked"
                        string? unlockedDate = TryGetStringProperty(elt, new[]
                        {
                            "DateUnlocked", "dateUnlocked", "dateunlocked",
                            "unlockedAt", "unlockedDate", "achievedAt", "date", "achieved_at"
                        });

                        if (!string.IsNullOrWhiteSpace(unlockedDate))
                            unlockedDate = WebUtility.HtmlDecode(unlockedDate).Trim();

                        // Backwards/forward compatibility:
                        // - If JSON contains a date but unlocked flag is missing/false, treat as unlocked.
                        if (!unlocked && !string.IsNullOrWhiteSpace(unlockedDate))
                            unlocked = true;

                        var entry = new AchievementEntry
                        {
                            Name = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name!,
                            Description = desc,
                            Unlocked = unlocked,
                            UnlockedDate = unlockedDate
                        };

                        Achievements.Add(entry);
                    }
                    catch
                    {
                        // ignore an individual item parse error
                    }
                }
            }
            catch
            {
                // swallow - achievements are non-critical
            }
            finally
            {
                OnPropertyChanged(nameof(AchievementsSummary));
                OnPropertyChanged(nameof(AchievementsFileDisplay));
            }
        }

        /// <summary>
        /// Determine the correct Achievements folder token for a GameItem.
        /// Uses emulator/folder/launch heuristics first, then falls back to the UI platform token.
        /// Returns values that match the roms folder names (for example: "PC (Windows)", "Microsoft - Xbox 360", "Sony - PlayStation", "Nintendo - Wii", etc.).
        /// </summary>
        private string MapPlatformToFolder(GameItem? game)
        {
            if (game == null) return "PC (Windows)";

            string? emulator = null;
            string? folder = null;
            string? launchPath = null;

            try
            {
                var t = game.GetType();
                var emProp = t.GetProperty("Emulator");
                if (emProp != null) emulator = emProp.GetValue(game) as string;

                var fProp = t.GetProperty("Folder") ?? t.GetProperty("InstallationFolder") ?? t.GetProperty("Path");
                if (fProp != null) folder = fProp.GetValue(game) as string;

                var lpProp = t.GetProperty("LaunchPath");
                if (lpProp != null) launchPath = lpProp.GetValue(game) as string;
            }
            catch { /* best-effort only */ }

            if (!string.IsNullOrWhiteSpace(emulator))
            {
                var exe = Path.GetFileNameWithoutExtension(emulator).ToLowerInvariant();
                if (exe.Contains("dolphin")) return "Nintendo - GameCube";
                if (exe.Contains("yuzu") || exe.Contains("ryujinx")) return "Nintendo - Switch";
                if (exe.Contains("xemu")) return "Microsoft - Xbox";
                if (exe.Contains("duckstation")) return "Sony - PlayStation";
                if (exe.Contains("pcsx2")) return "Sony - PlayStation 2";
                if (exe.Contains("rpcs3")) return "Sony - PlayStation 3";
                if (exe.Contains("ppsspp")) return "Sony - PlayStation Portable";
                if (exe.Contains("cemu")) return "Nintendo - Wii U";
                if (exe.Contains("lime 3ds") || exe.Contains("citra")) return "Nintendo - 3DS";
                if (exe.Contains("melon") || exe.Contains("desmume")) return "Nintendo - DS";
                if (exe.Contains("vita3k")) return "Sony - PlayStation Vita";
                if (exe.Contains("xenia")) return "Microsoft - Xbox 360";
                if (exe.Contains("cxbx")) return "Microsoft - Xbox";
                if (exe.Contains("mame")) return "Arcade";
                if (exe.Contains("snes9x") || exe.Contains("zsnes")) return "Nintendo - SNES";
                if (exe.Contains("fceux") || exe.Contains("nestopia")) return "Nintendo - NES";
                if (exe.Contains("mgba") || exe.Contains("visualboyadvance")) return "Nintendo - GameBoy Advance";
                if (exe.Contains("mednafen") || exe.Contains("pcsx")) return "Sony - PlayStation";
                if (exe.Contains("openbor")) return "OpenBOR";
                if (exe.Contains("retroarch")) return "RetroArch";
            }

            if (!string.IsNullOrWhiteSpace(folder))
            {
                var f = folder.Replace('/', '\\');
                if (f.Contains(@"\Repacks\", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains(@"\Games\", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains(@"\PC Games\", StringComparison.OrdinalIgnoreCase))
                    return "PC (Windows)";
                if (f.Contains(@"\Wii U\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - Wii U";
                if (f.Contains(@"\Wii\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - Wii";
                if (f.Contains(@"\Switch\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - Switch";
                if (f.Contains(@"\Xbox 360\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox 360";
                if (f.Contains(@"\Xbox One\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox One";
                if (f.Contains(@"\Xbox Series\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox Series";
                if (f.Contains(@"\Xbox Live Arcade\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox Live Arcade";
                if (f.Contains(@"\Xbox Live Indie\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox Live Indie";
                if (f.Contains(@"\Xbox\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox";
                if (f.Contains(@"\PlayStation 3\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PS3\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation 3";
                if (f.Contains(@"\PlayStation 4\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PS4\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation 4";
                if (f.Contains(@"\PlayStation 5\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PS5\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation 5";
                if (f.Contains(@"\PlayStation Vita\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PSV\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation Vita";
                if (f.Contains(@"\PlayStation Portable\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PSP\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation Portable";
                if (f.Contains(@"\PlayStation 2\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PS2\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation 2";
                if (f.Contains(@"\PlayStation\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PS1\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation";
                if (f.Contains(@"\Nintendo - 3DS\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - 3DS";
                if (f.Contains(@"\DSi\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - DSi";
                if (f.Contains(@"\DS\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - DS";
                if (f.Contains(@"\GameCube\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - GameCube";
                if (f.Contains(@"\SNES\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\Snes\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - SNES";
                if (f.Contains(@"\NES\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - NES";
                if (f.Contains(@"\GameBoy Advance\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - GameBoy Advance";
                if (f.Contains(@"\GameBoy Color\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - GameBoy Color";
                if (f.Contains(@"\GameBoy\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - GameBoy";
                if (f.Contains(@"\Arcade\", StringComparison.OrdinalIgnoreCase)) return "Arcade";
                if (f.Contains(@"\OpenBOR\", StringComparison.OrdinalIgnoreCase)) return "OpenBOR";
                if (f.Contains(@"\RetroArch\", StringComparison.OrdinalIgnoreCase)) return "RetroArch";
            }

            if (!string.IsNullOrWhiteSpace(launchPath))
            {
                try
                {
                    var exe = Path.GetFileNameWithoutExtension(launchPath).ToLowerInvariant();
                    if (exe.Contains("xenia")) return "Microsoft - Xbox 360";
                    if (exe.Contains("xemu")) return "Microsoft - Xbox";
                    if (exe.Contains("dolphin")) return "Nintendo - GameCube";
                    if (exe.Contains("pcsx2")) return "Sony - PlayStation 2";
                }
                catch { }
            }

            var uiPlatform = game.Platform;
            if (!string.IsNullOrWhiteSpace(uiPlatform))
            {
                var p = uiPlatform.Trim();

                // Normalize common UI platform values into the rom-folder tokens we use
                if (p.Equals("pc", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("pc ", StringComparison.OrdinalIgnoreCase) ||
                    p.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "PC (Windows)";

                if (p.IndexOf("xbox 360", StringComparison.OrdinalIgnoreCase) >= 0) return "Microsoft - Xbox 360";
                if (p.IndexOf("xbox one", StringComparison.OrdinalIgnoreCase) >= 0) return "Microsoft - Xbox One";
                if (p.IndexOf("xbox series", StringComparison.OrdinalIgnoreCase) >= 0) return "Microsoft - Xbox Series";
                if (p.IndexOf("xbox", StringComparison.OrdinalIgnoreCase) >= 0) return "Microsoft - Xbox";

                if (p.IndexOf("playstation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.IndexOf("sony", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Sony - PlayStation";

                if (p.IndexOf("wii u", StringComparison.OrdinalIgnoreCase) >= 0) return "Nintendo - Wii U";
                if (p.IndexOf("wii", StringComparison.OrdinalIgnoreCase) >= 0) return "Nintendo - Wii";
                if (p.IndexOf("switch", StringComparison.OrdinalIgnoreCase) >= 0) return "Nintendo - Switch";
                if (p.IndexOf("3ds", StringComparison.OrdinalIgnoreCase) >= 0) return "Nintendo - 3DS";
                if (p.IndexOf("ds", StringComparison.OrdinalIgnoreCase) >= 0) return "Nintendo - DS";

                // Fallback to the raw UI platform string if nothing matched
                return p;
            }

            return "PC (Windows)";
        }

        private static string? TryGetStringProperty(JsonElement elt, string[] candidates)
        {
            if (elt.ValueKind != JsonValueKind.Object)
                return null;

            // Try exact property matches first (fast path)
            foreach (var c in candidates)
            {
                if (elt.TryGetProperty(c, out var p))
                {
                    if (p.ValueKind == JsonValueKind.String) return p.GetString();
                    try { return p.ToString(); } catch { }
                }
            }

            // Case-insensitive / substring fallback to support different schemas (e.g. "Name", "IsUnlocked", "UnlockedAt", etc.)
            foreach (var prop in elt.EnumerateObject())
            {
                foreach (var c in candidates)
                {
                    if (string.Equals(prop.Name, c, StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var p = prop.Value;
                        if (p.ValueKind == JsonValueKind.String) return p.GetString();
                        try { return p.ToString(); } catch { }
                    }
                }
            }

            return null;
        }

        private static bool TryGetBoolProperty(JsonElement elt, string[] candidates)
        {
            if (elt.ValueKind != JsonValueKind.Object)
                return false;

            // Try exact property matches first
            foreach (var c in candidates)
            {
                if (elt.TryGetProperty(c, out var p))
                {
                    if (p.ValueKind == JsonValueKind.True) return true;
                    if (p.ValueKind == JsonValueKind.False) return false;
                    if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n != 0;
                    if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b)) return b;
                }
            }

            // Case-insensitive / substring fallback (handles "IsUnlocked", "IsUnlockedAt" etc.)
            foreach (var prop in elt.EnumerateObject())
            {
                foreach (var c in candidates)
                {
                    if (string.Equals(prop.Name, c, StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var p = prop.Value;
                        if (p.ValueKind == JsonValueKind.True) return true;
                        if (p.ValueKind == JsonValueKind.False) return false;
                        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n != 0;
                        if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b)) return b;
                    }
                }
            }

            return false;
        }

        // Achievements summary for binding (e.g. "3/42 unlocked" or "(no achievements)")
        public string AchievementsSummary
        {
            get
            {
                if (Achievements == null || Achievements.Count == 0)
                    return "(no achievements)";
                var unlocked = Achievements.Count(a => a.Unlocked);
                return $"{unlocked}/{Achievements.Count} unlocked";
            }
        }

        // Simple achievement model used for UI binding
        public sealed class AchievementEntry
        {
            public string Name { get; set; } = "(unnamed)";
            public string? Description { get; set; }
            public bool Unlocked { get; set; }
            public string? UnlockedDate { get; set; }

            // For simple templating in XAML
            public string StatusSymbol => Unlocked ? "✓" : "🔒";
            public Brush StatusBrush => Unlocked ? Brushes.LimeGreen : Brushes.LightGray;
        }

        // Sanity: notify view when properties change (AchievementsSummary)
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}