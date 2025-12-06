using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using App.Services;
using System.Media;

namespace PS5_OS
{
    public partial class AllGamesPage : UserControl, INotifyPropertyChanged
    {
        private readonly List<Button> _platformButtons = new();
        private int _currentPlatformIndex = 0;

        // game navigation state
        private bool _inGamesMode;
        private int _currentGameIndex;

        // remember platform we manually highlighted so we can clear it
        private Button? _manualHighlightedPlatform;

        public ObservableCollection<GameItem> Games { get; } = new();

        // Platform definitions - tag is used for the platform id when you add real lookup later
        private static readonly string[] Platforms = new[]
        {
            "3DS","DSI","DS","Switch","N64","NES","SNES","Xbox","Xbox 360","PS1","PS2","PS3","PS4","PSP",
            "PSV","GB","GBA","GBC","PC", "Wii"
        };

        // Grid layout for games: keep this in sync with XAML UniformGrid Columns="8"
        private const int GridColumns = 8;

        // Audio players (optional)
        private SoundPlayer? _navPlayer;
        private SoundPlayer? _actPlayer;

        public AllGamesPage()
        {
            InitializeComponent();
            DataContext = this;

            // audio
            TryLoadAudioPlayers();

            // create platform buttons dynamically (keeps XAML concise)
            foreach (var p in Platforms)
            {
                var b = new Button
                {
                    Content = p,
                    Tag = p,
                    Style = (Style)Resources["PlatformButtonStyle"],
                    MinWidth = 64
                };
                b.Click += PlatformButton_Click;
                PlatformsPanel.Children.Add(b);
                _platformButtons.Add(b);
            }

            // Do NOT load last-played/unknown placeholder games here.
            // Instead we auto-load the first platform that actually contains games on Loaded.

            GameItemsControl.ItemsSource = Games;
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

        // Make Loaded async so we can probe available platforms and auto-load the first one with games.
        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // start entrance animation
            if (Resources["EntranceStoryboard"] is Storyboard sb)
            {
                sb.Begin(this);
            }

            // focus first platform button
            if (_platformButtons.Count > 0)
            {
                _currentPlatformIndex = 0;
                _platformButtons[0].Focus();
            }

            // Auto-load first available platform's games (do not show LastPlayed/Unknown)
            await AutoLoadFirstAvailablePlatformGames().ConfigureAwait(true);
        }

        // Probes FindGames once and picks the first platform in Platforms[] that has results.
        private async Task AutoLoadFirstAvailablePlatformGames()
        {
            try
            {
                var found = await FindGames.FindGamesAsync(progress: null, cancellationToken: CancellationToken.None).ConfigureAwait(true);
                if (found == null) return;

                for (var pIdx = 0; pIdx < Platforms.Length; pIdx++)
                {
                    var platform = Platforms[pIdx];
                    bool hasAny = false;

                    if (string.Equals(platform, "PC", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAny = found.Any(g => g.IsWindowsExecutable && g.ExecutablePaths != null && g.ExecutablePaths.Count > 0);
                    }
                    else if (string.Equals(platform, "3DS", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAny = found.Any(g =>
                        {
                            if (g.IsWindowsExecutable) return false;
                            if (string.IsNullOrEmpty(g.InstallPath)) return false;
                            var ext = Path.GetExtension(g.InstallPath ?? string.Empty);
                            return string.Equals(ext, ".3ds", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(ext, ".cia", StringComparison.OrdinalIgnoreCase);
                        });
                    }
                    else
                    {
                        hasAny = found.Any(g => string.Equals(g.Platform, platform, StringComparison.OrdinalIgnoreCase));
                    }

                    if (!hasAny) continue;

                    var idx = pIdx;
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        _currentPlatformIndex = idx;

                        if (_currentPlatformIndex >= 0 && _currentPlatformIndex < _platformButtons.Count)
                        {
                            _platformButtons[_currentPlatformIndex].Focus();
                        }

                        // Load and show games for the platform
                        await LoadGamesForPlatform(platform);

                        if (Games.Count > 0)
                        {
                            _currentGameIndex = 0;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                HighlightSelectedGame();
                                BringGameIntoView(_currentGameIndex);
                            }, DispatcherPriority.Background);
                        }
                    }, DispatcherPriority.Background);

                    break;
                }
            }
            catch
            {
                // silent fail — prefer empty list over showing unknown/recent placeholders
            }
        }

        // When user clicks a platform button we now await loading and then highlight the first game.
        private async void PlatformButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b)
            {
                var platform = b.Tag?.ToString();

                // load games for platform (await ensures UI collection is populated before we highlight)
                await LoadGamesForPlatform(platform);

                // highlight first game (if any). Do not enter games mode / change focus.
                if (Games.Count > 0)
                {
                    _currentGameIndex = 0;
                    // Delay to ensure item containers are generated and layout completed
                    await Dispatcher.InvokeAsync(() =>
                    {
                        HighlightSelectedGame();
                        BringGameIntoView(_currentGameIndex);
                    }, DispatcherPriority.Background);
                }
            }
        }

        // Load games for the requested platform.
        private async Task LoadGamesForPlatform(string? platform)
        {
            SelectedPlatform = platform ?? string.Empty;

            // clear previous results
            Games.Clear();

            if (string.Equals(platform, "PC", StringComparison.OrdinalIgnoreCase))
            {
                var previousLabel = SelectedPlatform;
                SelectedPlatform = $"{platform} (Loading...)  ";

                try
                {
                    var found = await FindGames.FindGamesAsync(progress: null, cancellationToken: CancellationToken.None).ConfigureAwait(true);

                    // Now FindGames returns all executables for a game in ExecutablePaths.
                    // Consider a PC game valid if ExecutablePaths contains at least one entry.
                    var pcGames = found
                        .Where(g => g.IsWindowsExecutable && g.ExecutablePaths != null && g.ExecutablePaths.Count > 0)
                        .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();

                    if (pcGames.Count == 0)
                    {
                        MessageBox.Show("No PC games were found in any <Drive>:\\Games\\<GameName> folders.", "No PC Games", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        foreach (var g in pcGames)
                        {
                            // prefer first executable for LaunchPath but pass full list through GameItem
                            var launch = g.ExecutablePaths?.FirstOrDefault() ?? g.InstallPath;
                            Games.Add(new GameItem(g.Name, "/Images/sample1.jpg", launch, "PC", g.Region, g.Languages, g.ExecutablePaths));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to load PC games: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    SelectedPlatform = previousLabel;
                }

                return;
            }

            if (string.Equals(platform, "3DS", StringComparison.OrdinalIgnoreCase))
            {
                var previousLabel = SelectedPlatform;
                SelectedPlatform = $"{platform} (Loading...)  ";

                try
                {
                    var found = await FindGames.FindGamesAsync(progress: null, cancellationToken: CancellationToken.None).ConfigureAwait(true);

                    // Find entries that represent 3DS ROMs (FindGames returns rom entries with InstallPath pointing at the file)
                    var roms = found
                        .Where(g => !g.IsWindowsExecutable && !string.IsNullOrEmpty(g.InstallPath))
                        .Where(g =>
                        {
                            var ext = Path.GetExtension(g.InstallPath ?? string.Empty);
                            return string.Equals(ext, ".3ds", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(ext, ".cia", StringComparison.OrdinalIgnoreCase);
                        })
                        .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();

                    if (roms.Count == 0)
                    {
                        MessageBox.Show("No 3DS ROMs were found under any <Drive>:\\Roms\\Nintendo - 3DS\\Games folders.", "No 3DS ROMs", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        foreach (var r in roms)
                        {
                            // Use the rom file path as LaunchPath so ActivateGame can open it (via default app or emulator)
                            Games.Add(new GameItem(r.Name, "/Images/sample1.jpg", r.InstallPath, "3DS", r.Region, r.Languages, null));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // cancelled
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to load 3DS ROMs: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    SelectedPlatform = previousLabel;
                }

                return;
            }

            // Non-PC and non-3DS platforms: query FindGames and show matching platform entries.
            try
            {
                var found = await FindGames.FindGamesAsync(progress: null, cancellationToken: CancellationToken.None).ConfigureAwait(true);

                // Filter by requested platform id (case-insensitive). FindGames now sets GameInfo.Platform.
                var platformResults = found
                    .Where(g => string.Equals(g.Platform, platform, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                if (platformResults.Count == 0)
                {
                    // show a friendly message only for explicit user-initiated platform selections
                    // (optional — remove if you prefer silent empty result)
                    // MessageBox.Show($"No {platform} entries were found.", $"No {platform} Games", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    foreach (var e in platformResults)
                    {
                        // For roms use InstallPath as LaunchPath so ActivateGame can open it.
                        Games.Add(new GameItem(e.Name, "/Images/sample1.jpg", e.InstallPath, platform, e.Region, e.Languages, null));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // cancelled
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load {platform} games: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return;
        }

        private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_platformButtons.Count == 0) return;

            // If we're in "games mode" handle navigation inside games first
            if (_inGamesMode)
            {
                switch (e.Key)
                {
                    case Key.Left:
                        PlayNavigation();
                        MoveGameHorizontal(-1);
                        e.Handled = true;
                        break;
                    case Key.Right:
                        PlayNavigation();
                        MoveGameHorizontal(1);
                        e.Handled = true;
                        break;
                    case Key.Up:
                        // If on top row, refocus platforms; otherwise move up one row
                        if (IsOnTopRow(_currentGameIndex))
                        {
                            PlayNavigation();
                            ExitGamesModeToPlatform();
                        }
                        else
                        {
                            PlayNavigation();
                            MoveGameVertical(-1);
                        }
                        e.Handled = true;
                        break;
                    case Key.Down:
                        PlayNavigation();
                        MoveGameVertical(1);
                        e.Handled = true;
                        break;
                    case Key.Enter:
                    case Key.Space:
                        PlayActivation();
                        ActivateGame();
                        e.Handled = true;
                        break;
                    case Key.Escape:
                        // close parent overlay window if present
                        var w2 = Window.GetWindow(this);
                        w2?.Close();
                        e.Handled = true;
                        break;
                }

                return;
            }

            // Not in games mode — platform navigation / actions
            switch (e.Key)
            {
                case Key.Left:
                    PlayNavigation();
                    MovePlatformHorizontal(-1);
                    e.Handled = true;
                    break;
                case Key.Right:
                    PlayNavigation();
                    MovePlatformHorizontal(1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                case Key.Space:
                    PlayActivation();
                    // activate platform (entering games mode)
                    ActivatePlatform();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    // close parent overlay window if present
                    var w = Window.GetWindow(this);
                    w?.Close();
                    e.Handled = true;
                    break;
            }
        }

        // Move left/right among platform buttons
        private void MovePlatformHorizontal(int delta)
        {
            var count = _platformButtons.Count;
            var newIdx = ((_currentPlatformIndex + delta) % count + count) % count;
            _currentPlatformIndex = newIdx;

            // if we previously applied a manual highlight (because we entered games mode), clear it:
            if (_manualHighlightedPlatform != null && _manualHighlightedPlatform != _platformButtons[_currentPlatformIndex])
            {
                ClearPlatformManualHighlight(_manualHighlightedPlatform);
                _manualHighlightedPlatform = null;
            }

            _platformButtons[_currentPlatformIndex].Focus();
        }

        // Enter "games mode" — keep platform visually highlighted and focus games below
        private async void ActivatePlatform()
        {
            var b = _platformButtons[_currentPlatformIndex];
            var platform = b.Tag?.ToString();

            // ensure games are loaded before focusing / highlighting
            await LoadGamesForPlatform(platform);

            // apply a manual visual highlight on the platform so it stays highlighted while games are focused
            ApplyPlatformManualHighlight(b);
            _manualHighlightedPlatform = b;

            // switch to games mode
            _inGamesMode = true;

            // ensure there's at least one game selected (start at 0)
            _currentGameIndex = Math.Min(0, Math.Max(0, Games.Count - 1));

            // ensure the GameItemsControl can receive keyboard focus at runtime and focus it
            GameItemsControl.Focusable = true;
            GameItemsControl.Focus();
            Keyboard.Focus(GameItemsControl);

            // select and highlight the first game (or preserved index)
            await Dispatcher.InvokeAsync(() =>
            {
                HighlightSelectedGame();
                BringGameIntoView(_currentGameIndex);
            }, DispatcherPriority.Background);
        }

        // Exit games mode and focus back on platform buttons
        private void ExitGamesModeToPlatform()
        {
            _inGamesMode = false;

            // clear manual highlight so the focused platform shows using keyboard focus styling
            if (_manualHighlightedPlatform != null)
            {
                ClearPlatformManualHighlight(_manualHighlightedPlatform);
                _manualHighlightedPlatform = null;
            }

            // focus the platform button (keyboard focus will apply the PlatformButtonStyle trigger)
            if (_currentPlatformIndex >= 0 && _currentPlatformIndex < _platform_buttons_count())
            {
                _platform_buttons()[_currentPlatformIndex].Focus();
            }
        }

        // helper for guard readability
        private int _platform_buttons_count() => _platformButtons.Count;
        private IList<Button> _platform_buttons() => _platformButtons;

        // Move left/right among games in the grid. Clamp to valid indices.
        private void MoveGameHorizontal(int delta)
        {
            if (Games.Count == 0) return;

            var count = Games.Count;
            var newIndex = _currentGameIndex + delta;

            // clamp to same row if possible, otherwise clamp to bounds
            if (newIndex < 0) newIndex = 0;
            if (newIndex >= count) newIndex = count - 1;

            _currentGameIndex = newIndex;

            HighlightSelectedGame();
            BringGameIntoView(_currentGameIndex);
        }

        // Move up/down by rows (rowDelta = -1 for up, +1 for down)
        private void MoveGameVertical(int rowDelta)
        {
            if (Games.Count == 0) return;

            var count = Games.Count;
            var target = _currentGameIndex + rowDelta * GridColumns;

            if (target < 0)
            {
                // if moving up beyond the top row, return focus to platforms
                ExitGamesModeToPlatform();
                return;
            }

            if (target >= count)
            {
                // clamp to last item if requested row is beyond available items
                target = count - 1;
            }

            _currentGameIndex = target;

            HighlightSelectedGame();
            BringGameIntoView(_currentGameIndex);
        }

        // Activate the current game (Enter/Space)
        private void ActivateGame()
        {
            if (_currentGameIndex < 0 || _currentGameIndex >= Games.Count) return;
            var game = Games[_currentGameIndex];

            try
            {
                // Show the Game Info window as a modal owned by the current window.
                var owner = Window.GetWindow(this);
                var info = new GameInfoWindow(game)
                {
                    Owner = owner
                };

                // Use ShowDialog so keyboard focus and modality are predictable.
                info.ShowDialog();
            }
            catch (Exception ex)
            {
                // Fallback: if window creation fails, show a message so user isn't left wondering.
                MessageBox.Show($"Unable to show game info: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Helper used by Up key handling to determine if current item is on top row
        private static bool IsOnTopRow(int index) => index >= 0 && index < GridColumns;

        // Visually highlight the selected game (by changing the game's BorderBrush)
        private void HighlightSelectedGame()
        {
            // Use ItemContainerGenerator.ContainerFromIndex instead of ItemsPanelRoot (compatibility).
            for (var i = 0; i < Games.Count; i++)
            {
                var container = GameItemsControl.ItemContainerGenerator.ContainerFromIndex(i) as DependencyObject;
                if (container == null) continue;

                var possibleBorder = FindVisualChild<Border>(container);
                if (possibleBorder == null) continue;

                if (i == _currentGameIndex)
                {
                    possibleBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xCC, 0x00));
                    possibleBorder.BorderThickness = new Thickness(2);
                }
                else
                {
                    possibleBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
                    possibleBorder.BorderThickness = new Thickness(2);
                }
            }
        }

        private void BringGameIntoView(int index)
        {
            // Use ItemContainerGenerator.ContainerFromIndex instead of ItemsPanelRoot (compatibility).
            if (index < 0 || index >= Games.Count) return;

            var container = GameItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as DependencyObject;
            if (container == null) return;

            var elementToScroll = FindVisualChild<FrameworkElement>(container) ?? FindVisualChild<Border>(container);
            elementToScroll?.BringIntoView();
        }

        // Helper: find first visual child of type T in the subtree
        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;

                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }

            return null;
        }

        private void ApplyPlatformManualHighlight(Button b)
        {
            // apply a border highlight on the button so it looks selected while games are focused
            b.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xCC, 0x00));
            b.BorderThickness = new Thickness(2);
        }

        private void ClearPlatformManualHighlight(Button b)
        {
            // clear local values so style triggers take over again
            b.ClearValue(Control.BorderBrushProperty);
            b.ClearValue(Control.BorderThicknessProperty);
        }

        private string? _selectedPlatform;
        public string? SelectedPlatform
        {
            get => _selectedPlatform;
            set
            {
                if (_selectedPlatform == value) return;
                _selectedPlatform = value;
                OnPropertyChanged();
            }
        }

        // --- New: load last played games from account LastPlayed.json ---
        // Kept for backward compatibility but no longer used automatically at open.
        private void LoadLastPlayedGames()
        {
            try
            {
                var accountFolder = GetAccountFolder();
                var lastPlayedPath = Path.Combine(accountFolder, "LastPlayed.json");

                // create file if missing
                if (!File.Exists(lastPlayedPath))
                {
                    File.WriteAllText(lastPlayedPath, "[]");
                    return;
                }

                var json = File.ReadAllText(lastPlayedPath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    // ensure file contains at least an empty array
                    File.WriteAllText(lastPlayedPath, "[]");
                    return;
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var entries = JsonSerializer.Deserialize<List<LastPlayedEntry>>(json, options);
                if (entries == null || entries.Count == 0) return;

                // take up to last 5 entries (file should already provide that)
                var take = Math.Min(5, entries.Count);
                for (var i = 0; i < take; i++)
                {
                    var e = entries[i];
                    // ensure we have valid fields; fall back to placeholder cover if missing
                    var title = string.IsNullOrWhiteSpace(e.Title) ? "Unknown" : e.Title;
                    var cover = string.IsNullOrWhiteSpace(e.CoverUri) ? "/Images/sample1.jpg" : e.CoverUri;
                    Games.Add(new GameItem(title, cover));
                }
            }
            catch (Exception ex)
            {
                // don't crash UI; show info for diagnosis and leave Games empty
                MessageBox.Show($"Unable to load last played games: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

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

        private sealed class LastPlayedEntry
        {
            public string? Title { get; set; }
            public string? CoverUri { get; set; }
        }

        // --- end new code ---

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class GameItem
    {
        public string Title { get; }
        public string CoverUri { get; }

        // Optional: path to launch (executable for PC, ROM file for consoles like 3DS)
        public string? LaunchPath { get; }

        // Optional full list of executables discovered for this game (PC). May be null or empty.
        public IReadOnlyList<string>? ExecutablePaths { get; }

        // Optional platform identifier (e.g. "PC", "3DS")
        public string? Platform { get; }

        // Optional region token extracted from raw name (e.g. "USA", "Europe")
        public string? Region { get; }

        // Optional languages extracted from raw name (e.g. "EN","FR","DE")
        public IReadOnlyList<string>? Languages { get; }

        public GameItem(string title, string coverUri)
            : this(title, coverUri, null, null, null, null, null)
        {
        }

        // Backwards-compatible constructor (keeps existing call sites working)
        public GameItem(string title, string coverUri, string? launchPath, string? platform)
            : this(title, coverUri, launchPath, platform, null, null, null)
        {
        }

        // Full constructors including region and languages
        public GameItem(string title, string coverUri, string? launchPath, string? platform, string? region)
            : this(title, coverUri, launchPath, platform, region, null, null)
        {
        }

        // New full constructor that accepts an executable paths list
        public GameItem(string title, string coverUri, string? launchPath, string? platform, string? region, IReadOnlyList<string>? languages, IReadOnlyList<string>? executablePaths)
        {
            Title = title ?? string.Empty;
            LaunchPath = launchPath;
            Platform = platform;
            Region = region;
            Languages = languages;
            ExecutablePaths = executablePaths;

            // If caller provided a non-placeholder coverUri use it; otherwise compute default path:
            if (!string.IsNullOrWhiteSpace(coverUri) && coverUri != "/Images/sample1.jpg")
            {
                CoverUri = coverUri;
            }
            else
            {
                CoverUri = ComputeDefaultCoverUri(DisplayTitle, Platform);
            }
        }

        // Presentation-friendly title: removes parenthetical region/language tokens from display.
        public string DisplayTitle
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Title)) return Title;

                var result = Regex.Replace(Title, @"\(([^)]*)\)", match =>
                {
                    var token = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(token) && Regex.IsMatch(token, @"^[A-Za-z0-9,\s\-_/]+$"))
                        return string.Empty;
                    return match.Value;
                });

                result = Regex.Replace(result, @"\s{2,}", " ").Trim();
                return result;
            }
        }

        // Comma-separated languages for display (e.g. "EN, FR, DE")
        public string DisplayLanguages => Languages != null && Languages.Count > 0 ? string.Join(", ", Languages) : string.Empty;

        // Compute default cover URI from AppContext.BaseDirectory.
        // Tries mapped platform folder names (e.g. "3DS" -> "Nintendo - 3DS") and a set of common filenames.
        // Falls back to existing sample image when not found.
        private static string ComputeDefaultCoverUri(string title, string? platform)
        {
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

                // Build a set of title variants to handle common naming differences:
                // - original title
                // - replace ":" with " - "  (e.g. "Call of Duty: Black Ops II" -> "Call of Duty - Black Ops II")
                // - replace ":" with "-"    (no spaces)
                // This ensures folders that used '-' instead of ':' will be found.
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
                        // avoid checking duplicate paths across variants
                        if (!checkedPaths.Add(p)) continue;

                        try
                        {
                            if (File.Exists(p))
                            {
                                return new Uri(p).AbsoluteUri;
                            }
                        }
                        catch
                        {
                            // ignore path issues and try next candidate
                        }
                    }
                }
            }
            catch
            {
                // ignore and fall back
            }

            // fallback local resource
            return "/Images/sample1.jpg";
        }

        // Sanitize a string for use as a folder/file name.
        private static string SanitizeForPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var s = input.Trim();

            // Remove surrounding quotes
            if ((s.StartsWith("\"") && s.EndsWith("\"")) || (s.StartsWith("'") && s.EndsWith("'")))
                s = s.Substring(1, s.Length - 2).Trim();

            // Replace underscores with spaces to match expected folder naming and collapse spaces
            s = s.Replace('_', ' ');
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();

            // Remove invalid filename characters
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (invalid.Contains(ch) || ch == ':' || ch == '?' || ch == '*' || ch == '\"' || ch == '<' || ch == '>' || ch == '|')
                    sb.Append('_');
                else
                    sb.Append(ch);
            }

            // Collapse multiple underscores that may have been introduced
            var cleaned = Regex.Replace(sb.ToString(), @"_+", "_").Trim();
            return cleaned;
        }
    }
}