using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Media;
using System.IO;
using System.Text.Json;
using System;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using System.Diagnostics;
using System.Windows.Controls.Primitives;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Net;

namespace PS5_OS
{
    public partial class Dashboard : UserControl, INotifyPropertyChanged
    {
        private string? _selectedTitle;
        public string? SelectedTitle
        {
            get => _selectedTitle;
            set
            {
                if (_selectedTitle == value) return;
                _selectedTitle = value;
                OnPropertyChanged();
            }
        }

        // Achievements summary for the currently selected card (display under Play button)
        private string _achievementsSummary = string.Empty;
        public string AchievementsSummary
        {
            get => _achievementsSummary;
            private set
            {
                if (_achievementsSummary == value) return;
                _achievementsSummary = value;
                OnPropertyChanged();
            }
        }

        private bool _hasAchievements;
        public bool HasAchievements
        {
            get => _hasAchievements;
            private set
            {
                if (_hasAchievements == value) return;
                _hasAchievements = value;
                OnPropertyChanged();
            }
        }

        // Achievements collection exposed for binding (small preview list)
        public ObservableCollection<AchievementEntry> Achievements { get; } = new();

        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName ?? string.Empty));
        }

        // Navigation list is now rebuilt after loading last-played so it only contains visible items
        private readonly List<Control> _navItems = new();
        private int _currentIndex;
        private int _lastCardIndex;

        // CardsCount is computed from the current _navItems (radio buttons = cards)
        private int CardsCount => _navItems.Count(x => x is RadioButton);
        private int ActionStartIndex => CardsCount;
        private int ActionCount => Math.Max(0, _navItems.Count - ActionStartIndex);

        // Audio players for navigation and activation
        private SoundPlayer? _navPlayer;
        private SoundPlayer? _actPlayer;

        // File system watcher to refresh dashboard when LastPlayed.json changes
        private FileSystemWatcher? _lastPlayedWatcher;
        private string? _watchedFolder;
        private string? _watchedFile;
        private readonly DispatcherTimer _reloadDebounceTimer;

        public Dashboard()
        {
            InitializeComponent();
            DataContext = this;

            // create debounce timer to coalesce rapid FS events
            _reloadDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _reloadDebounceTimer.Tick += ReloadDebounceTimer_Tick;

            // load audio (optional; files are expected under <exeDir>/Data/Resources/Dashboard/)
            TryLoadAudioPlayers();

            // Populate recent games from account LastPlayed.json (Data/Accounts/<Account>/LastPlayed.json)
            // LoadLastPlayedGames will set per-card Visibility and Content and set up watcher.
            LoadLastPlayedGames();

            // Build navigation order based on what's visible: cards (rbStore + visible game radios + rbAllGames) then center buttons.
            RebuildNavItems();

            // determine initial card index (if any radio is pre-checked)
            for (var i = 0; i < CardsCount && i < _navItems.Count; i++)
            {
                if (_navItems[i] is RadioButton radio && radio.IsChecked == true)
                {
                    _lastCardIndex = i;
                    break;
                }
            }

            // clean up watcher when control unloaded
            this.Unloaded += Dashboard_Unloaded;

            // ensure action buttons visibility is correct at startup
            UpdateActionButtonsVisibility();
        }

        private void Dashboard_Unloaded(object? sender, RoutedEventArgs e)
        {
            DisposeWatcher();
            _reloadDebounceTimer.Stop();
            _reloadDebounceTimer.Tick -= ReloadDebounceTimer_Tick;
            this.Unloaded -= Dashboard_Unloaded;
        }

        private void ReloadDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _reloadDebounceTimer.Stop();
            // Ensure we execute on UI thread (DispatcherTimer already runs on UI thread)
            try
            {
                LoadLastPlayedGames();
                RebuildNavItems();

                // ensure focus stays consistent: if nothing focused, focus first visible item
                if (_navItems.Count > 0 && !IsKeyboardFocusWithin)
                {
                    _currentIndex = 0;
                    FocusAndSelect(_navItems[0]);
                }

                UpdateActionButtonsVisibility();
            }
            catch
            {
                // non-fatal, ignore errors during reload
            }
        }

        private void SetupLastPlayedWatcher(string accountFolder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(accountFolder)) return;

                var filePath = Path.Combine(accountFolder, "LastPlayed.json");
                var folder = Path.GetDirectoryName(filePath) ?? accountFolder;
                var fileName = Path.GetFileName(filePath);

                // Only reconfigure watcher if path changed
                if (string.Equals(folder, _watchedFolder, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(fileName, _watchedFile, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                DisposeWatcher();

                _lastPlayedWatcher = new FileSystemWatcher(folder)
                {
                    Filter = fileName,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                _lastPlayedWatcher.Changed += LastPlayedWatcher_Event;
                _lastPlayedWatcher.Created += LastPlayedWatcher_Event;
                _lastPlayedWatcher.Deleted += LastPlayedWatcher_Event;
                _lastPlayedWatcher.Renamed += LastPlayedWatcher_Renamed;

                _watchedFolder = folder;
                _watchedFile = fileName;
            }
            catch
            {
                DisposeWatcher();
            }
        }

        private void DisposeWatcher()
        {
            try
            {
                if (_lastPlayedWatcher != null)
                {
                    _lastPlayedWatcher.Changed -= LastPlayedWatcher_Event;
                    _lastPlayedWatcher.Created -= LastPlayedWatcher_Event;
                    _lastPlayedWatcher.Deleted -= LastPlayedWatcher_Event;
                    _lastPlayedWatcher.Renamed -= LastPlayedWatcher_Renamed;
                    _lastPlayedWatcher.EnableRaisingEvents = false;
                    _lastPlayedWatcher.Dispose();
                    _lastPlayedWatcher = null;
                }
            }
            catch { /* ignore cleanup errors */ }
            finally
            {
                _watchedFolder = null;
                _watchedFile = null;
            }
        }

        private void LastPlayedWatcher_Renamed(object sender, RenamedEventArgs e) => LastPlayedWatcher_Event(sender, e);

        // coalesce rapid events by restarting the debounce timer
        private void LastPlayedWatcher_Event(object sender, FileSystemEventArgs e)
        {
            // start/restart debounce timer on UI thread
            try
            {
                Dispatcher.Invoke(() =>
                {
                    _reloadDebounceTimer.Stop();
                    _reloadDebounceTimer.Start();
                });
            }
            catch
            {
                // If Dispatcher not available, ignore
            }
        }

        private void RebuildNavItems()
        {
            _navItems.Clear();

            // Always include Store if present
            if (rbStore != null && rbStore.Visibility == Visibility.Visible) _navItems.Add(rbStore);

            // add only visible recent game radios, preserving order
            var gameRbs = new[] { rbGame1, rbGame2, rbGame3, rbGame4, rbGame5 };
            foreach (var gameRadio in gameRbs)
            {
                if (gameRadio != null && gameRadio.Visibility == Visibility.Visible)
                    _navItems.Add(gameRadio);
            }

            // Always include AllGames
            if (rbAllGames != null && rbAllGames.Visibility == Visibility.Visible) _navItems.Add(rbAllGames);

            // Then action buttons (only if visible)
            if (btnPlay != null && btnPlay.Visibility == Visibility.Visible) _navItems.Add(btnPlay);
            if (btnMore != null && btnMore.Visibility == Visibility.Visible) _navItems.Add(btnMore);
        }

        private void TryLoadAudioPlayers()
        {
            try
            {
                var baseDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "Resources", "Dashboard");
                var navPath = System.IO.Path.Combine(baseDir, "navigation.wav");
                var actPath = System.IO.Path.Combine(baseDir, "activation.wav");

                if (System.IO.File.Exists(navPath))
                {
                    try
                    {
                        _navPlayer = new SoundPlayer(navPath);
                        _navPlayer.Load();
                    }
                    catch
                    {
                        _navPlayer = null;
                    }
                }

                if (System.IO.File.Exists(actPath))
                {
                    try
                    {
                        _actPlayer = new SoundPlayer(actPath);
                        _actPlayer.Load();
                    }
                    catch
                    {
                        _actPlayer = null;
                    }
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
            try { _navPlayer?.Play(); } catch { /* ignore */ }
        }

        private void PlayActivation()
        {
            try { _actPlayer?.Play(); } catch { /* ignore */ }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Force default focus to first visible nav item (Store usually)
            _currentIndex = 0;
            _lastCardIndex = 0;

            if (_navItems.Count > 0)
            {
                FocusAndSelect(_navItems[0]);
            }

            // ensure action buttons reflect the currently focused item
            UpdateActionButtonsVisibility();
        }

        // handle arrow keys and Enter
        // Only Left/Right move in the current row. Down jumps to action row.
        // Up returns to the last highlighted card.
        private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_navItems.Count == 0) return;

            switch (e.Key)
            {
                case Key.Left:
                    PlayNavigation();
                    MoveHorizontal(-1);
                    e.Handled = true;
                    break;
                case Key.Right:
                    PlayNavigation();
                    MoveHorizontal(1);
                    e.Handled = true;
                    break;
                case Key.Down:
                    // jump to first action button (Play) and focus there
                    PlayNavigation();
                    if (ActionCount > 0)
                    {
                        _currentIndex = ActionStartIndex;
                        FocusAndSelect(_navItems[_currentIndex]);
                    }
                    e.Handled = true;
                    break;
                case Key.Up:
                    // return to previously selected card
                    PlayNavigation();
                    _currentIndex = Math.Min(_lastCardIndex, Math.Max(0, CardsCount - 1));
                    if (_currentIndex >= 0 && _currentIndex < _navItems.Count)
                        FocusAndSelect(_navItems[_currentIndex]);
                    e.Handled = true;
                    break;
                case Key.Home:
                    PlayNavigation();
                    _currentIndex = 0;
                    FocusAndSelect(_navItems[_currentIndex]);
                    e.Handled = true;
                    break;
                case Key.End:
                    PlayNavigation();
                    _currentIndex = _navItems.Count - 1;
                    FocusAndSelect(_navItems[_currentIndex]);
                    e.Handled = true;
                    break;
                case Key.Enter:
                case Key.Space:
                    // If a real recent game card is focused, treat Enter/Space like Down: focus Play (action row).
                    if (_currentIndex < CardsCount && _navItems[_currentIndex] is RadioButton rbEnter && rbEnter.Visibility == Visibility.Visible
                        && rbEnter != rbStore && rbEnter != rbAllGames)
                    {
                        PlayNavigation();
                        if (ActionCount > 0)
                        {
                            _currentIndex = ActionStartIndex;
                            FocusAndSelect(_navItems[_currentIndex]);
                        }
                    }
                    else
                    {
                        // default activation behavior for non-game cards and action buttons
                        PlayActivation();
                        ActivateCurrent();
                    }
                    e.Handled = true;
                    break;
            }
        }

        // Move left/right within the current row (cards or action buttons) only.
        // Skips over any collapsed controls because they are not present in _navItems.
        private void MoveHorizontal(int delta)
        {
            if (_navItems.Count == 0) return;

            if (_currentIndex < CardsCount)
            {
                // currently in cards row -> move within cards
                var start = 0;
                var length = CardsCount;
                _currentIndex = ((_currentIndex - start + delta) % length + length) % length + start;
            }
            else
            {
                // currently in actions row -> move within actions
                var start = ActionStartIndex;
                var length = ActionCount;
                if (length == 0) return;
                var relative = _currentIndex - start;
                relative = ((relative + delta) % length + length) % length;
                _currentIndex = start + relative;
            }

            FocusAndSelect(_navItems[_currentIndex]);
        }

        private void FocusAndSelect(Control c)
        {
            // Update _currentIndex so subsequent MoveHorizontal/Up/Down logic is correct
            var idx = _navItems.IndexOf(c);
            if (idx >= 0) _currentIndex = idx;

            c.Focus();

            if (c is RadioButton radio)
            {
                // visually select card as user navigates
                radio.IsChecked = true;
                SelectedTitle = radio.Tag?.ToString();
                // remember last card selection so Up returns here later
                var idx2 = _navItems.IndexOf(radio);
                if (idx2 >= 0 && idx2 < CardsCount) _lastCardIndex = idx2;

                // Load achievements summary for the selected game (best-effort)
                try
                {
                    var title = radio.Tag?.ToString() ?? radio.Content?.ToString();
                    if (!string.IsNullOrWhiteSpace(title) && radio != rbStore && radio != rbAllGames)
                        LoadAchievementsSummaryForTitle(title);
                    else
                        ClearAchievementsSummary();
                }
                catch { ClearAchievementsSummary(); }
            }
            else if (c is Button b)
            {
                // set SelectedTitle to show which action is focused
                SelectedTitle = b.Content?.ToString();
            }

            // Update action buttons visibility based on the item we just focused.
            UpdateActionButtonsVisibility(c);
        }

        private void ClearAchievementsSummary()
        {
            HasAchievements = false;
            AchievementsSummary = string.Empty;
            Achievements.Clear(); // clear preview list
        }

        // Try to locate achievements JSON for a given title and compute a simple summary like "3/42 unlocked".
        // Also populate a small preview set of achievements (up to 3) for the dashboard panel.
        private void LoadAchievementsSummaryForTitle(string title)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    ClearAchievementsSummary();
                    return;
                }

                // Use same sanitization as GameInfoWindow (important — this aligns path resolution)
                var sanitizedTitle = SanitizeForPath(title);
                var account = GetAccountFolder();
                if (string.IsNullOrWhiteSpace(account) || !Directory.Exists(account))
                {
                    ClearAchievementsSummary();
                    return;
                }

                var achRoot = Path.Combine(account, "Achievements");
                if (!Directory.Exists(achRoot))
                {
                    ClearAchievementsSummary();
                    return;
                }

                // First try platform-specific path if there is a hint (ToolTip or All.GamesData.json)
                string? found = null;

                var platformHint = GetPlatformForTitle(title);
                if (!string.IsNullOrWhiteSpace(platformHint))
                {
                    try
                    {
                        var platformFolder = GetPlatformFolderName(platformHint);
                        var candidateFile = Path.Combine(achRoot, platformFolder, sanitizedTitle, sanitizedTitle + ".json");
                        if (File.Exists(candidateFile))
                            found = candidateFile;
                    }
                    catch { /* ignore and fall back to global scan */ }
                }

                // Fallback: search all platform folders for a matching sanitizedTitle.json
                if (string.IsNullOrWhiteSpace(found))
                {
                    try
                    {
                        foreach (var platformDir in Directory.EnumerateDirectories(achRoot))
                        {
                            var candidateFile = Path.Combine(platformDir, sanitizedTitle, sanitizedTitle + ".json");
                            if (File.Exists(candidateFile))
                            {
                                found = candidateFile;
                                break;
                            }

                            // Also try common variant where title uses " - " instead of ":" etc.
                            var variant = SanitizeForPath(title.Replace(":", " - "));
                            candidateFile = Path.Combine(platformDir, variant, variant + ".json");
                            if (File.Exists(candidateFile))
                            {
                                found = candidateFile;
                                break;
                            }
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(found))
                {
                    ClearAchievementsSummary();
                    return;
                }

                string json;
                try { json = File.ReadAllText(found); }
                catch { ClearAchievementsSummary(); return; }

                if (string.IsNullOrWhiteSpace(json)) { ClearAchievementsSummary(); return; }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                IEnumerable<JsonElement> items = Enumerable.Empty<JsonElement>();

                if (root.ValueKind == JsonValueKind.Array)
                {
                    items = root.EnumerateArray();
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("achievements", out var p) && p.ValueKind == JsonValueKind.Array)
                        items = p.EnumerateArray();
                    else if (root.TryGetProperty("items", out var p2) && p2.ValueKind == JsonValueKind.Array)
                        items = p2.EnumerateArray();
                    else if (root.TryGetProperty("data", out var p3) && p3.ValueKind == JsonValueKind.Array)
                        items = p3.EnumerateArray();
                    else
                    {
                        var list = new List<JsonElement>();
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                                list.Add(prop.Value);
                        }
                        if (list.Count > 0) items = list;
                        else items = new[] { root };
                    }
                }

                // Build and parse achievements (use same decoding approach as GameInfoWindow)
                var parsed = new List<AchievementEntry>();

                foreach (var elt in items)
                {
                    try
                    {
                        var name = TryGetStringPropertyLocal(elt, new[] { "name", "title", "label", "id" }) ?? "(unnamed)";
                        var desc = TryGetStringPropertyLocal(elt, new[] { "description", "desc", "details" });

                        // decode HTML-escaped description/name if present
                        if (!string.IsNullOrWhiteSpace(desc))
                            desc = WebUtility.HtmlDecode(desc).Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                            name = WebUtility.HtmlDecode(name).Trim();

                        var unlocked = TryGetBoolPropertyLocal(elt, new[] { "unlocked", "achieved", "completed", "isUnlocked", "is_achieved" });
                        var unlockedDate = TryGetStringPropertyLocal(elt, new[]
                        {
                            "DateUnlocked", "dateUnlocked", "dateunlocked",
                            "unlockedAt", "unlockedDate", "achievedAt", "date", "achieved_at"
                        });

                        // Backwards compatibility: if a date exists, consider unlocked
                        if (!unlocked && !string.IsNullOrWhiteSpace(unlockedDate)) unlocked = true;

                        parsed.Add(new AchievementEntry
                        {
                            Name = name,
                            Description = desc,
                            Unlocked = unlocked,
                            UnlockedDate = unlockedDate
                        });
                    }
                    catch { /* per-item ignore */ }
                }

                // Sort so unlocked items show first and most-recently unlocked items first
                var ordered = parsed
                    .OrderByDescending(p => p.Unlocked)
                    .ThenByDescending(p => ParseUnlockedDate(p.UnlockedDate))
                    .ToList();

                var total = ordered.Count;
                var unlockedCount = ordered.Count(p => p.Unlocked);

                if (total == 0)
                {
                    ClearAchievementsSummary();
                    return;
                }

                AchievementsSummary = $"{unlockedCount}/{total} unlocked";
                HasAchievements = true;

                // Populate preview list (up to 3 items). Clear existing then add.
                Achievements.Clear();
                foreach (var a in ordered.Take(3))
                    Achievements.Add(a);
            }
            catch
            {
                ClearAchievementsSummary();
            }
        }

        // Try to find platform string previously stored on the radio buttons (we set ToolTip = platform when populating cards),
        // or fall back to All.GamesData.json per-account metadata if present.
        private string? GetPlatformForTitle(string title)
        {
            try
            {
                // first try ToolTip on existing radio buttons
                foreach (var rb in new[] { rbGame1, rbGame2, rbGame3, rbGame4, rbGame5, rbStore, rbAllGames })
                {
                    if (rb == null) continue;
                    var t = rb.Tag?.ToString();
                    if (!string.IsNullOrWhiteSpace(t) && string.Equals(t, title, StringComparison.OrdinalIgnoreCase))
                    {
                        var tooltip = rb.ToolTip as string;
                        if (!string.IsNullOrWhiteSpace(tooltip)) return tooltip;
                    }
                }

                // fallback: check per-account All.GamesData.json for a matching title and return PlatformName
                var account = GetAccountFolder();
                if (string.IsNullOrWhiteSpace(account)) return null;
                var allDataFile = Path.Combine(account, "All.GamesData.json");
                if (!File.Exists(allDataFile)) return null;

                try
                {
                    var json = File.ReadAllText(allDataFile);
                    if (string.IsNullOrWhiteSpace(json)) return null;

                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var parsed = JsonSerializer.Deserialize<List<AllGamesDataEntry>>(json, opts);
                    if (parsed == null || parsed.Count == 0) return null;

                    var match = parsed.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.GameName)
                                                           && string.Equals(e.GameName.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (match != null && !string.IsNullOrWhiteSpace(match.PlatformName))
                        return match.PlatformName;
                }
                catch { /* ignore parse errors */ }
            }
            catch { /* ignore */ }

            return null;
        }

        // Local simplified JSON helpers (similar to GameInfoWindow)
        private static string? TryGetStringPropertyLocal(JsonElement elt, string[] candidates)
        {
            if (elt.ValueKind != JsonValueKind.Object) return null;
            foreach (var c in candidates)
            {
                if (elt.TryGetProperty(c, out var p))
                {
                    if (p.ValueKind == JsonValueKind.String) return p.GetString();
                    try { return p.ToString(); } catch { }
                }
            }
            foreach (var prop in elt.EnumerateObject())
            {
                foreach (var c in candidates)
                {
                    if (string.Equals(prop.Name, c, StringComparison.OrdinalIgnoreCase) || prop.Name.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var p = prop.Value;
                        if (p.ValueKind == JsonValueKind.String) return p.GetString();
                        try { return p.ToString(); } catch { }
                    }
                }
            }
            return null;
        }

        private static bool TryGetBoolPropertyLocal(JsonElement elt, string[] candidates)
        {
            if (elt.ValueKind != JsonValueKind.Object) return false;
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
            foreach (var prop in elt.EnumerateObject())
            {
                foreach (var c in candidates)
                {
                    if (string.Equals(prop.Name, c, StringComparison.OrdinalIgnoreCase) || prop.Name.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)
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

        // Parse unlocked date string to DateTime; return DateTime.MinValue when parse fails.
        private static DateTime ParseUnlockedDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
            if (DateTime.TryParse(s, out var dt)) return dt;
            // try common ISO formats and tolerant parse
            try
            {
                return DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }
            catch { }
            return DateTime.MinValue;
        }

        // Navigation actions (Buttons and radio buttons)
        // --- Game launch / Action button integration (exactly like GameInfoWindow) ---

        // Activate current selection (game or action button)
        private void ActivateCurrent()
        {
            var c = _navItems[_currentIndex];

            // Play activation for programmatic activation as well
            PlayActivation();

            if (c is RadioButton radio)
            {
                // if this is the AllGames radio, open the AllGames page with animation
                if (radio.Name == "rbAllGames")
                {
                    // mark selected visually then open page
                    radio.IsChecked = true;
                    SelectedTitle = radio.Tag?.ToString() ?? radio.Content?.ToString() ?? "All Games";
                    OpenAllGamesOverlay();
                    return;
                }

                // Activate other cards – show message only on Enter/Space
                radio.IsChecked = true;
                SelectedTitle = radio.Tag?.ToString() ?? radio.Content?.ToString() ?? "Button";
                MessageBox.Show($"{SelectedTitle} pressed", "Button pressed", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (c is Button b)
            {
                // If this is the Play action, open a GameInfoWindow for the currently selected game and launch it.
                if (b == btnPlay)
                {
                    try
                    {
                        // Find selected game radiobutton among visible cards
                        RadioButton? selectedRadio = null;

                        // Prefer the radio that is currently checked
                        var gameRbs = new[] { rbGame1, rbGame2, rbGame3, rbGame4, rbGame5 };
                        foreach (var gameRadio in gameRbs)
                        {
                            if (gameRadio != null && gameRadio.Visibility == Visibility.Visible && gameRadio.IsChecked == true)
                            {
                                selectedRadio = gameRadio;
                                break;
                            }
                        }

                        // Fallback to the last remembered card index if none checked
                        if (selectedRadio == null)
                        {
                            if (_lastCardIndex >= 0 && _lastCardIndex < _navItems.Count && _navItems[_lastCardIndex] is RadioButton rr && rr.Visibility == Visibility.Visible)
                                selectedRadio = rr;
                        }

                        if (selectedRadio == null)
                        {
                            MessageBox.Show("No game is selected to play.", "Cannot Play", MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }

                        var title = selectedRadio.Tag?.ToString() ?? selectedRadio.Content?.ToString() ?? "Unknown";
                        // Resolve a best-effort cover (GameInfoWindow will handle missing launch path)
                        var coverUri = ResolveCoverUri(null, title, null);

                        // Construct a minimal GameItem (GameInfoWindow expects one)
                        var game = new GameItem(title, coverUri);

                        var owner = Window.GetWindow(this);
                        var info = new GameInfoWindow(game)
                        {
                            Owner = owner
                        };

                        // Attempt to launch immediately using the same logic as the Launch button on GameInfoWindow
                        info.LaunchGame();
                    }
                    catch
                    {
                        MessageBox.Show("Failed to start game.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }

                    return;
                }

                // Activate action button (Play / More) for non-Play actions
                var name = b.Content?.ToString() ?? "Button";
                SelectedTitle = name;
                MessageBox.Show($"{name} pressed", "Button pressed", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // Intercept keys while Play/... are focused so Up/Escape return to highlighted game
        private void ActionButton_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                switch (e.Key)
                {
                    case Key.Up:
                    case Key.Escape:
                        PlayNavigation();
                        // return to the last card we remembered
                        _currentIndex = Math.Min(_lastCardIndex, Math.Max(0, CardsCount - 1));
                        if (_currentIndex >= 0 && _currentIndex < _navItems.Count)
                            FocusAndSelect(_navItems[_currentIndex]);
                        e.Handled = true;
                        break;
                    case Key.Left:
                        PlayNavigation();
                        MoveHorizontal(-1);
                        e.Handled = true;
                        break;
                    case Key.Right:
                        PlayNavigation();
                        MoveHorizontal(1);
                        e.Handled = true;
                        break;
                    case Key.Enter:
                    case Key.Space:
                        // allow activation to be handled by normal click/PreviewKeyDown bubbling
                        break;
                }
            }
            catch
            {
                // ignore
            }
        }

        // open the AllGames page in a fullscreen overlay with a simple transition
        private async void OpenAllGamesOverlay()
        {
            // subtle dashboard scale/fade for transition (keeps UX similar)
            var rt = new ScaleTransform(1.0, 1.0);
            this.RenderTransformOrigin = new Point(0.5, 0.5);
            this.RenderTransform = rt;

            var sb = new Storyboard();
            var scaleA = new DoubleAnimation(1.0, 0.96, new Duration(System.TimeSpan.FromMilliseconds(220))) { EasingFunction = new QuadraticEase() };
            var scaleB = new DoubleAnimation(1.0, 0.96, new Duration(System.TimeSpan.FromMilliseconds(220))) { EasingFunction = new QuadraticEase() };
            Storyboard.SetTarget(scaleA, this);
            Storyboard.SetTargetProperty(scaleA, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
            Storyboard.SetTarget(scaleB, this);
            Storyboard.SetTargetProperty(scaleB, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
            sb.Children.Add(scaleA);
            sb.Children.Add(scaleB);

            var fade = new DoubleAnimation(1.0, 0.86, new Duration(System.TimeSpan.FromMilliseconds(220)));
            Storyboard.SetTarget(fade, this);
            Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
            sb.Children.Add(fade);

            sb.Begin();

            // small delay to let the dashboard animation run
            await System.Threading.Tasks.Task.Delay(220);

            // create overlay window and show the AllGamesPage
            var owner = Window.GetWindow(this);
            var page = new AllGamesPage();

            var overlay = new Window
            {
                Content = page,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Black,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowState = WindowState.Maximized,
                ShowInTaskbar = false
            };

            // entrance animation on overlay is defined in AllGamesPage (it animates itself on Loaded)
            overlay.ShowDialog();

            // restore dashboard
            var restoreSb = new Storyboard();
            var rscaleX = new DoubleAnimation(0.96, 1.0, new Duration(System.TimeSpan.FromMilliseconds(180)));
            var rscaleY = new DoubleAnimation(0.96, 1.0, new Duration(System.TimeSpan.FromMilliseconds(180)));
            var ropacity = new DoubleAnimation(0.86, 1.0, new Duration(System.TimeSpan.FromMilliseconds(180)));
            Storyboard.SetTarget(rscaleX, this);
            Storyboard.SetTargetProperty(rscaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
            Storyboard.SetTarget(rscaleY, this);
            Storyboard.SetTargetProperty(rscaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
            Storyboard.SetTarget(ropacity, this);
            Storyboard.SetTargetProperty(ropacity, new PropertyPath("Opacity"));
            restoreSb.Children.Add(rscaleX);
            restoreSb.Children.Add(rscaleY);
            restoreSb.Children.Add(ropacity);
            restoreSb.Begin();
        }

        // RadioButton (card) checked -> update selection only (do NOT show message here)
        private void GameButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radio)
            {
                SelectedTitle = radio.Tag?.ToString() ?? radio.Content?.ToString() ?? "Button";
                var idx = _navItems.IndexOf(radio);
                if (idx >= 0 && idx < CardsCount) _lastCardIndex = idx;

                // Ensure action buttons visibility is updated for mouse/checked changes
                UpdateActionButtonsVisibility(radio);

                // Achievements loading handled in FocusAndSelect
            }
        }

        // Center action buttons Click -> update selection only (do NOT show message here)
        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b)
            {
                SelectedTitle = b.Content?.ToString() ?? "Button";
                // do not display message here; Enter/Space activation shows it
            }
        }

        // --- New helper: show action buttons only when a real Game card is focused/selected ---
        private void UpdateActionButtonsVisibility(Control? focused = null)
        {
            try
            {
                bool show = false;

                // determine current focus if none provided
                if (focused == null)
                {
                    if (_navItems.Count > 0 && _currentIndex >= 0 && _currentIndex < _navItems.Count)
                        focused = _navItems[_currentIndex];
                }

                // If there are zero recent game cards visible, always hide actions
                var anyGameVisible = new[] { rbGame1, rbGame2, rbGame3, rbGame4, rbGame5 }
                    .Any(x => x != null && x.Visibility == Visibility.Visible);
                if (!anyGameVisible)
                {
                    show = false;
                    btnPlay.Visibility = Visibility.Collapsed;
                    btnMore.Visibility = Visibility.Collapsed;
                    RebuildNavItems();
                    return;
                }

                // If the focused element is one of the action buttons, keep them visible
                if (focused is Button fb)
                {
                    if (fb == btnPlay || fb == btnMore)
                    {
                        show = true;
                    }
                    else
                    {
                        show = false;
                    }
                }
                else if (focused is RadioButton focusedRadio && focusedRadio.Visibility == Visibility.Visible)
                {
                    // store and all-games are not "Game" items — hide actions for them
                    if (focusedRadio == rbStore || focusedRadio == rbAllGames)
                        show = false;
                    else
                        show = true;
                }
                else
                {
                    show = false;
                }

                btnPlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                btnMore.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

                // after changing action buttons visibility, rebuild nav so navigation order is correct
                RebuildNavItems();
            }
            catch
            {
                // ignore unexpected errors; do not collapse buttons while focused
            }
        }

        // -------------------- Added: LastPlayed loader (updated) --------------------

        // Minimal schema for LastPlayed.json (mirrors All.GamesData schema where possible)
        private sealed class LastPlayedEntry
        {
            // The file may use "Title" or "GameName". Support both.
            public string? Title { get; set; }
            public string? GameName { get; set; }

            // Optional CoverUri
            public string? CoverUri { get; set; }

            // Optional platform
            public string? PlatformName { get; set; }

            // Convenience to get canonical title
            public string? CanonicalTitle => !string.IsNullOrWhiteSpace(GameName) ? GameName : Title;
        }

        // Small helper to read All.GamesData.json entries (used for platform fallback lookup)
        private sealed class AllGamesDataEntry
        {
            public string? GameName { get; set; }
            public string? PlatformName { get; set; }
            public string? LastPlayedDate { get; set; }
            public double TotalPlayTime { get; set; }
            public int TotalTimesPlayed { get; set; }
        }

        // Load last played games and populate the game cards (rbGame1..rbGame5).
        // Behavior:
        //  - If LastPlayed.json is missing or empty -> hide recent game cards on dashboard (only Store + All Games remain visible).
        //  - Otherwise use the last-played entries (up to 5) to populate cards.
        //  - Only the number of cards matching available entries will be shown.
        private void LoadLastPlayedGames()
        {
            try
            {
                var accountFolder = GetAccountFolder();
                var file = Path.Combine(accountFolder, "LastPlayed.json");

                // ensure watcher monitors current account folder/file
                SetupLastPlayedWatcher(accountFolder);

                List<LastPlayedEntry> entries = new();

                if (!File.Exists(file))
                {
                    // No last-played file -> hide recent cards
                    HideAllRecentGameCards();
                    return;
                }

                try
                {
                    var json = File.ReadAllText(file);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        HideAllRecentGameCards();
                        return;
                    }

                    // Try to parse objects first
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    try
                    {
                        var parsedObjects = JsonSerializer.Deserialize<List<JsonElement>>(json, opts);
                        if (parsedObjects != null && parsedObjects.Count > 0)
                        {
                            foreach (var el in parsedObjects)
                            {
                                try
                                {
                                    var lp = new LastPlayedEntry();

                                    if (el.ValueKind == JsonValueKind.Object)
                                    {
                                        if (el.TryGetProperty("GameName", out var gn) && gn.ValueKind == JsonValueKind.String)
                                            lp.GameName = gn.GetString();
                                        else if (el.TryGetProperty("Title", out var t) && t.ValueKind == JsonValueKind.String)
                                            lp.Title = t.GetString();

                                        if (el.TryGetProperty("PlatformName", out var pf) && pf.ValueKind == JsonValueKind.String)
                                            lp.PlatformName = pf.GetString();

                                        if (el.TryGetProperty("CoverUri", out var cu) && cu.ValueKind == JsonValueKind.String)
                                            lp.CoverUri = cu.GetString();
                                    }
                                    else if (el.ValueKind == JsonValueKind.String)
                                    {
                                        lp.Title = el.GetString();
                                    }

                                    if (!string.IsNullOrWhiteSpace(lp.CanonicalTitle))
                                        entries.Add(lp);
                                }
                                catch { /* continue on per-entry errors */ }
                            }
                        }
                    }
                    catch
                    {
                        // fallback: try simple array of strings
                        try
                        {
                            var arr = JsonSerializer.Deserialize<List<string>>(json, opts);
                            if (arr != null)
                            {
                                foreach (var s in arr)
                                {
                                    if (!string.IsNullOrWhiteSpace(s))
                                        entries.Add(new LastPlayedEntry { Title = s });
                                }
                            }
                        }
                        catch
                        {
                            // give up - treat as empty
                            entries.Clear();
                        }
                    }
                }
                catch
                {
                    HideAllRecentGameCards();
                    return;
                }

                // Ensure we have entries
                entries = entries.Where(e => !string.IsNullOrWhiteSpace(e?.CanonicalTitle)).Take(5).ToList();

                if (entries.Count == 0)
                {
                    HideAllRecentGameCards();
                    return;
                }

                // Map entries to radio buttons rbGame1..rbGame5 (preserve order)
                var rbs = new[] { rbGame1, rbGame2, rbGame3, rbGame4, rbGame5 };

                // First hide them all, we'll show only those we populate
                foreach (var rbItem in rbs) if (rbItem != null) rbItem.Visibility = Visibility.Collapsed;

                for (var i = 0; i < entries.Count && i < rbs.Length; i++)
                {
                    var radio = rbs[i];
                    var e = entries[i];
                    var title = e.CanonicalTitle ?? "Unknown";
                    var platform = e.PlatformName;

                    // Resolve cover URI using same behavior as AllGamesPage/GameItem
                    var coverUri = ResolveCoverUri(e.CoverUri, title, platform);

                    // Create Image only and assign it as the RadioButton Content.
                    // Title (Tag) is shown below the image by the radio template.
                    var img = new Image
                    {
                        Width = 136,
                        Height = 88,
                        Stretch = Stretch.UniformToFill,
                        Margin = new Thickness(0, 6, 0, 6)
                    };

                    // Try load image robustly and silently fall back to sample image
                    ImageSource? loaded = null;
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;

                        if (Uri.TryCreate(coverUri, UriKind.Absolute, out var absoluteUri))
                        {
                            bmp.UriSource = absoluteUri;
                        }
                        else if (coverUri.StartsWith("/", StringComparison.Ordinal))
                        {
                            bmp.UriSource = new Uri($"pack://application:,,,{coverUri}", UriKind.Absolute);
                        }
                        else
                        {
                            bmp.UriSource = new Uri(coverUri, UriKind.RelativeOrAbsolute);
                        }

                        bmp.EndInit();
                        bmp.Freeze();
                        loaded = bmp;
                    }
                    catch
                    {
                        // try pack fallback explicitly
                        try
                        {
                            var fallback = new BitmapImage(new Uri("pack://application:,,,/Images/sample1.jpg", UriKind.Absolute));
                            fallback.Freeze();
                            loaded = fallback;
                        }
                        catch
                        {
                            loaded = null;
                        }
                    }

                    img.Source = loaded;

                    if (radio != null)
                    {
                        // Put image into Content and leave the title in Tag so the template places it below the box
                        radio.Content = img;
                        radio.Tag = title; // keep tag for SelectedTitle and other code

                        // store platform hint in ToolTip so LoadAchievementsSummaryForTitle can prefer the platform folder
                        radio.ToolTip = platform ?? string.Empty;

                        // Show the populated radio
                        radio.Visibility = Visibility.Visible;
                        // note: do NOT clear ToolTip here (previous code cleared it which prevented platform lookup)
                    }
                }

                // Ensure Store and AllGames remain visible
                rbStore.Visibility = Visibility.Visible;
                rbAllGames.Visibility = Visibility.Visible;

                // After changing visibilities, rebuild nav items so navigation uses visible items only
                RebuildNavItems();

                // Update action buttons visibility after refresh
                UpdateActionButtonsVisibility();
            }
            catch
            {
                // non-fatal - don't crash the dashboard if last played cannot be read
                HideAllRecentGameCards();
            }
        }

        private void HideAllRecentGameCards()
        {
            try
            {
                rbGame1.Visibility = Visibility.Collapsed;
                rbGame2.Visibility = Visibility.Collapsed;
                rbGame3.Visibility = Visibility.Collapsed;
                rbGame4.Visibility = Visibility.Collapsed;
                rbGame5.Visibility = Visibility.Collapsed;

                // keep Store and AllGames visible
                rbStore.Visibility = Visibility.Visible;
                rbAllGames.Visibility = Visibility.Visible;

                // Rebuild nav to reflect visible items only
                RebuildNavItems();

                // Hide action buttons because no recent games exist
                UpdateActionButtonsVisibility();
            }
            catch { /* ignore */ }
        }

        // Attempts to locate the account folder using the same heuristics used elsewhere in the app.
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

        // --- Added helpers used by Dashboard to match other pages' behavior ---

        private static string GetPlatformFolderName(string? platform)
        {
            if (string.IsNullOrWhiteSpace(platform)) return "Unknown";
            if (string.Equals(platform, "PC", StringComparison.OrdinalIgnoreCase)) return "PC (Windows)";
            return SanitizeForPath(platform ?? "Unknown");
        }

        // Resolve cover URI the same way AllGamesPage.GameItem does:
        // prefer provided URI, then search a set of candidate filenames under the platform folder,
        // finally fall back to the sample pack image.
        private static string ResolveCoverUri(string? providedCoverUri, string title, string? platform)
        {
            // 1) prefer provided CoverUri if valid
            if (!string.IsNullOrWhiteSpace(providedCoverUri))
            {
                try
                {
                    if (Uri.TryCreate(providedCoverUri, UriKind.Absolute, out var u))
                    {
                        if (u.IsFile)
                        {
                            var local = u.LocalPath;
                            if (File.Exists(local))
                                return u.AbsoluteUri;
                        }
                        else
                        {
                            // remote or pack URI - accept it
                            return providedCoverUri!;
                        }
                    }
                    else
                    {
                        // relative/pack-style, accept as-is
                        return providedCoverUri!;
                    }
                }
                catch { /* fall through to fallback search */ }
            }

            // 2) try multiple candidate filenames in Data/Resources/Game Covers/<PlatformFolder>/<SanitizedTitle>/
            try
            {
                // Map known platform identifiers to folder names used in your Data tree.
                var platformMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

                        { "Xbox", "Microsoft - Xbox" },
                        { "Xbox 360", "Microsoft - Xbox 360" },

                        { "PS1", "Sony - Playstation" },
                        { "PS2", "Sony - Playstation 2" },
                        { "PS3", "Sony - Playstation 3" },
                        { "PS4", "Sony - Playstation 4" },
                        { "PSP", "Sony - PSP" },
                        { "PSV", "Sony - PSV" },

                        // PC covers are stored under "PC (Windows)"
                        { "PC", "PC (Windows)" }
                    };

                var platformKey = string.IsNullOrWhiteSpace(platform) ? string.Empty : platform.Trim();
                string platformFolder;
                if (!string.IsNullOrEmpty(platformKey) && platformMap.TryGetValue(platformKey, out var mapped))
                {
                    platformFolder = mapped;
                }
                else
                {
                    platformFolder = string.IsNullOrWhiteSpace(platform) ? "Unknown" : SanitizeForPath(platform!);
                }

                // Build a set of title variants similar to GameItem (handle ":" differences)
                var titleVariants = new List<string> { title ?? string.Empty };
                if (!string.IsNullOrEmpty(title))
                {
                    var v1 = title.Replace(":", " - ");
                    var v2 = title.Replace(":", "-");
                    if (!titleVariants.Contains(v1, StringComparer.OrdinalIgnoreCase)) titleVariants.Add(v1);
                    if (!titleVariants.Contains(v2, StringComparer.OrdinalIgnoreCase)) titleVariants.Add(v2);
                }

                var baseDir = AppContext.BaseDirectory;
                var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var variant in titleVariants)
                {
                    var gameFolder = string.IsNullOrWhiteSpace(variant) ? "Unknown" : SanitizeForPath(variant);

                    var candidates = new[]
                    {
                            Path.Combine(baseDir, "Data", "Resources", "Game Covers", platformFolder, gameFolder, "Cover.png"),
                            Path.Combine(baseDir, "Data", "Resources", "Game Covers", platformFolder, gameFolder, "cover.png"),
                            Path.Combine(baseDir, "Data", "Resources", "Game Covers", platformFolder, gameFolder, "Cover.jpg"),
                            Path.Combine(baseDir, "Data", "Resources", "Game Covers", platformFolder, gameFolder, "cover.jpg"),
                            Path.Combine(baseDir, "Data", "Resources", "Game Covers", platformFolder, gameFolder, "cover.jpeg")
                        };

                    foreach (var p in candidates)
                    {
                        if (!checkedPaths.Add(p)) continue;

                        try
                        {
                            if (File.Exists(p))
                            {
                                return new Uri(p, UriKind.Absolute).AbsoluteUri;
                            }
                        }
                        catch
                        {
                            // ignore and try next candidate
                        }
                    }
                }
            }
            catch
            {
                // ignore and fall back
            }

            // 3) fallback local resource
            return "/Images/sample1.jpg";
        }

        // Sanitize a string for use as a folder/file name.
        // This matches the behaviour used by GameInfoWindow.SanitizeForPath so dashboard finds covers the same way.
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
        // --- end helpers ---  

        // Small achievement model used for the dashboard preview list
        public sealed class AchievementEntry
        {
            public string Name { get; set; } = "(unnamed)";
            public string? Description { get; set; }
            public bool Unlocked { get; set; }
            public string? UnlockedDate { get; set; }

            public string StatusSymbol => Unlocked ? "✓" : "🔒";
            public Brush StatusBrush => Unlocked ? Brushes.LimeGreen : Brushes.LightGray;
        }
    }
}