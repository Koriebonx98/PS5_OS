using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Diagnostics;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using HtmlAgilityPack;

namespace PS5_OS
{
    public partial class PropertiesWindow : Window
    {
        public GameItem Game { get; } = default!;
        public string? SelectedExe { get; private set; }
        public string? SelectedExeArgs => _argsTextBox?.Text;

        // keep a reference so async population methods can update the args box
        private TextBox? _argsTextBox;

        // ID section controls
        private TextBox? _steamIdBox;
        private TextBox? _msIdBox;
        private TextBox? _epicIdBox;
        private TextBox? _eaIdBox;
        private TextBox? _uplayIdBox;
        private TextBox? _gogIdBox;
        private TextBlock? _idsSaveStatus;
        // Add new field for Exophase ID
        private TextBox? _exophaseIdBox;

        public PropertiesWindow(GameItem game)
        {
            InitializeComponent();

            Game = game ?? throw new ArgumentNullException(nameof(game));
            DataContext = Game;

            Loaded += PropertiesWindow_Loaded;
        }

        private void PropertiesWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            // default to Exe section
            SectionExe.IsChecked = true;
            Keyboard.Focus(this);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
                e.Handled = true;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // Commit selection
            try
            {
                // Persist the user's preferred executable choice and arguments for this game
                if (!string.IsNullOrWhiteSpace(SelectedExe) || !string.IsNullOrWhiteSpace(SelectedExeArgs))
                {
                    PreferredExeStore.SetPreferredExe(Game.Title, SelectedExe, SelectedExeArgs);
                }
                else
                {
                    // If both empty, remove any existing preference
                    PreferredExeStore.SetPreferredExe(Game.Title, null, null);
                }
            }
            catch
            {
                // ignore store errors but still close and return the selected exe to caller
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Section_Checked(object sender, RoutedEventArgs e)
        {
            SectionContent.Children.Clear();

            if (SectionRoms.IsChecked == true)
            {
                SectionContent.Children.Add(new TextBlock { Text = "Roms: (not implemented)", Foreground = System.Windows.Media.Brushes.LightGray });
            }
            else if (SectionExe.IsChecked == true)
            {
                _ = BuildExeSectionAsync();
            }
            else if (SectionUrl.IsChecked == true)
            {
                SectionContent.Children.Add(new TextBlock { Text = "URL: (not implemented)", Foreground = System.Windows.Media.Brushes.LightGray });
            }
            else if (SectionEmulator.IsChecked == true)
            {
                SectionContent.Children.Add(new TextBlock { Text = "Emulator: (not implemented)", Foreground = System.Windows.Media.Brushes.LightGray });
            }
            else if (SectionManage.IsChecked == true)
            {
                SectionContent.Children.Add(new TextBlock { Text = "Manage: (not implemented)", Foreground = System.Windows.Media.Brushes.LightGray });
            }
            else if (SectionIds.IsChecked == true)
            {
                BuildIdsSection();
            }
        }

        // Build Exe UI and populate drives/exes (same behavior as earlier popup, but in a full-screen window).
        private async Task BuildExeSectionAsync()
        {
            SectionContent.Children.Clear();

            var container = new Grid();
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

            // Left: exe list
            var leftStack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            leftStack.Children.Add(new TextBlock { Text = "Executables found", Foreground = System.Windows.Media.Brushes.LightGray, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });

            var exeList = new ListBox { Height = 420 };
            exeList.SelectionChanged += (s, e) =>
            {
                if (exeList.SelectedItem is string path && !path.StartsWith("("))
                {
                    SelectedExe = path;
                    // If a saved preferred entry exists for this game and matches the selected exe, populate args box
                    try
                    {
                        var savedPath = PreferredExeStore.GetPreferredExe(Game.Title);
                        var savedArgs = PreferredExeStore.GetPreferredExeArguments(Game.Title);
                        if (!string.IsNullOrWhiteSpace(savedPath) &&
                            string.Equals(savedPath, path, StringComparison.OrdinalIgnoreCase) &&
                            _argsTextBox != null)
                        {
                            _argsTextBox.Text = savedArgs ?? string.Empty;
                        }
                    }
                    catch { }
                }
            };

            leftStack.Children.Add(exeList);
            leftStack.Children.Add(new TextBlock { Text = "Click an entry to select it as the runtime exe for this game.", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) });

            Grid.SetColumn(leftStack, 0);
            container.Children.Add(leftStack);

            // Right: drives + refresh + hints + arguments box
            var rightStack = new StackPanel();
            rightStack.Children.Add(new TextBlock { Text = "Drives", Foreground = System.Windows.Media.Brushes.LightGray, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });

            var drivesCombo = new ComboBox { MinWidth = 280, Margin = new Thickness(0, 0, 0, 6) };
            rightStack.Children.Add(drivesCombo);

            var refreshBtn = new Button { Content = "Refresh", Width = 100, Margin = new Thickness(0, 6, 0, 6) };
            rightStack.Children.Add(refreshBtn);

            // Arguments label + textbox
            rightStack.Children.Add(new TextBlock { Text = "Command-line arguments (optional)", Foreground = System.Windows.Media.Brushes.LightGray, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });

            _argsTextBox = new TextBox { MinWidth = 280, Margin = new Thickness(0, 0, 0, 6) };
            _argsTextBox.ToolTip = "Enter command-line arguments to pass when launching the selected executable. Example: -multiplayer \"-map zm\"";
            rightStack.Children.Add(_argsTextBox);

            rightStack.Children.Add(new TextBlock { Text = "Drive scanning looks under <Drive>:\\Games\\<GameFolder> for executables. Results include top-level and one-level-deep .exe files. Use \"All Drives\" to search all ready drives and include launch path + known exe entries.", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) });

            Grid.SetColumn(rightStack, 1);
            container.Children.Add(rightStack);

            SectionContent.Children.Add(container);

            // populate drives
            var drives = await Task.Run(() =>
            {
                try
                {
                    return DriveInfo.GetDrives()
                        .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                        .Select(d => d.Name)
                        .ToList();
                }
                catch
                {
                    return new List<string>();
                }
            }).ConfigureAwait(true);

            // Insert an "All Drives" option to run the broader scan (same as GameInfo page)
            var displayDrives = new List<string> { "(All Drives)" };
            displayDrives.AddRange(drives);

            drivesCombo.ItemsSource = displayDrives;
            if (drivesCombo.Items.Count > 0) drivesCombo.SelectedIndex = 0;

            drivesCombo.SelectionChanged += async (s, e) =>
            {
                if (drivesCombo.SelectedItem is string driveName)
                {
                    if (string.Equals(driveName, "(All Drives)", StringComparison.OrdinalIgnoreCase))
                        await PopulateAllExesAsync(exeList).ConfigureAwait(true);
                    else
                        await PopulateExesForDriveAsync(driveName, exeList).ConfigureAwait(true);
                }
            };

            refreshBtn.Click += async (s, e) =>
            {
                if (drivesCombo.SelectedItem is string driveName)
                {
                    if (string.Equals(driveName, "(All Drives)", StringComparison.OrdinalIgnoreCase))
                        await PopulateAllExesAsync(exeList).ConfigureAwait(true);
                    else
                        await PopulateExesForDriveAsync(driveName, exeList).ConfigureAwait(true);
                }
            };

            // initial population - selects "(All Drives)" so run full scan by default
            if (drivesCombo.SelectedItem is string initialDrive)
            {
                if (string.Equals(initialDrive, "(All Drives)", StringComparison.OrdinalIgnoreCase))
                    await PopulateAllExesAsync(exeList).ConfigureAwait(true);
                else
                    await PopulateExesForDriveAsync(initialDrive, exeList).ConfigureAwait(true);
            }

            // Prefill args textbox with any saved arguments for this game (user may not change exe selection)
            try
            {
                var existingArgs = PreferredExeStore.GetPreferredExeArguments(Game.Title);
                if (_argsTextBox != null)
                    _argsTextBox.Text = existingArgs ?? string.Empty;
            }
            catch { }
        }

        // Build the new "ID" section UI
        private void BuildIdsSection()
        {
            SectionContent.Children.Clear();

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            panel.Children.Add(new TextBlock { Text = "Platform IDs", Foreground = System.Windows.Media.Brushes.LightGray, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Rows (Steam, Microsoft, Epic, EA, Uplay, GOG, Exophase)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0 Steam
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1 Microsoft
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2 Epic
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3 EA
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4 Uplay
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 5 GOG
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 6 Exophase

            // Steam
            grid.Children.Add(new TextBlock { Text = "Steam App ID:", Foreground = System.Windows.Media.Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
            _steamIdBox = new TextBox { MinWidth = 260, Margin = new Thickness(6, 4, 0, 4) };
            Grid.SetColumn(_steamIdBox, 1);
            Grid.SetRow(_steamIdBox, 0);
            grid.Children.Add(_steamIdBox);

            // Microsoft
            var msLabel = new TextBlock { Text = "Microsoft ID:", Foreground = System.Windows.Media.Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(msLabel, 1);
            grid.Children.Add(msLabel);
            _msIdBox = new TextBox { MinWidth = 260, Margin = new Thickness(6, 4, 0, 4) };
            Grid.SetColumn(_msIdBox, 1);
            Grid.SetRow(_msIdBox, 1);
            grid.Children.Add(_msIdBox);

            // Epic
            var epicLabel = new TextBlock { Text = "Epic ID:", Foreground = System.Windows.Media.Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(epicLabel, 2);
            grid.Children.Add(epicLabel);
            _epicIdBox = new TextBox { MinWidth = 260, Margin = new Thickness(6, 4, 0, 4) };
            Grid.SetColumn(_epicIdBox, 1);
            Grid.SetRow(_epicIdBox, 2);
            grid.Children.Add(_epicIdBox);

            // EA
            var eaLabel = new TextBlock { Text = "EA ID:", Foreground = System.Windows.Media.Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(eaLabel, 3);
            grid.Children.Add(eaLabel);
            _eaIdBox = new TextBox { MinWidth = 260, Margin = new Thickness(6, 4, 0, 4) };
            Grid.SetColumn(_eaIdBox, 1);
            Grid.SetRow(_eaIdBox, 3);
            grid.Children.Add(_eaIdBox);

            // Uplay
            var uplayLabel = new TextBlock { Text = "Uplay ID:", Foreground = System.Windows.Media.Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(uplayLabel, 4);
            grid.Children.Add(uplayLabel);
            _uplayIdBox = new TextBox { MinWidth = 260, Margin = new Thickness(6, 4, 0, 4) };
            Grid.SetColumn(_uplayIdBox, 1);
            Grid.SetRow(_uplayIdBox, 4);
            grid.Children.Add(_uplayIdBox);

            // GOG
            var gogLabel = new TextBlock { Text = "GOG ID:", Foreground = System.Windows.Media.Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(gogLabel, 5);
            grid.Children.Add(gogLabel);
            _gogIdBox = new TextBox { MinWidth = 260, Margin = new Thickness(6, 4, 0, 4) };
            Grid.SetColumn(_gogIdBox, 1);
            Grid.SetRow(_gogIdBox, 5);
            grid.Children.Add(_gogIdBox);

            // Exophase (new)
            var exoLabel = new TextBlock { Text = "Exophase ID / URL:", Foreground = System.Windows.Media.Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(exoLabel, 6);
            grid.Children.Add(exoLabel);
            _exophaseIdBox = new TextBox { MinWidth = 260, Margin = new Thickness(6, 4, 0, 4), ToolTip = "Paste the Exophase game page URL or Exophase identifier here." };
            Grid.SetColumn(_exophaseIdBox, 1);
            Grid.SetRow(_exophaseIdBox, 6);
            grid.Children.Add(_exophaseIdBox);

            panel.Children.Add(grid);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };

            // Fetch Steam achievements button
            var fetchSteamBtn = new Button
            {
                Content = "Fetch Steam Achievements",
                Padding = new Thickness(8, 4, 8, 4),
                Background = System.Windows.Media.Brushes.Green,
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 8, 0)
            };
            fetchSteamBtn.Click += async (s, e) =>
            {
                if (_idsSaveStatus != null) _idsSaveStatus.Text = "Fetching...";
                try
                {
                    await FetchAndSaveSteamAchievementsAsync().ConfigureAwait(true);
                }
                catch
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_idsSaveStatus != null) _idsSaveStatus.Text = "Fetch failed";
                    });
                }
            };
            btnPanel.Children.Add(fetchSteamBtn);

            // Save IDs button
            var saveBtn = new Button { Content = "Save IDs", Padding = new Thickness(8, 4, 8, 4), Background = System.Windows.Media.Brushes.DodgerBlue, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 8, 0) };
            saveBtn.Click += SaveIdsButton_Click;
            btnPanel.Children.Add(saveBtn);

            // Exophase button - now shown for all platforms (placed immediately after "Save IDs")
            var openExophaseBtn = new Button
            {
                Content = "Find on Exophase",
                Padding = new Thickness(8, 4, 8, 4),
                Background = System.Windows.Media.Brushes.Gray,
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Open Exophase home page"
            };
            openExophaseBtn.Click += (s, e) =>
            {
                try
                {
                    // Open Exophase home page directly
                    var url = "https://www.exophase.com/";
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch
                {
                    // ignore browser launch failures
                }
            };

            btnPanel.Children.Add(openExophaseBtn);

            // New: Get Exophase Achievements button
            var getExoBtn = new Button
            {
                Content = "Get Exophase Ach",
                Padding = new Thickness(8, 4, 8, 4),
                Background = System.Windows.Media.Brushes.Orange,
                Foreground = System.Windows.Media.Brushes.Black,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Scrape Exophase achievements page and save to achievements JSON"
            };
            getExoBtn.Click += async (s, e) =>
            {
                if (_idsSaveStatus != null) _idsSaveStatus.Text = "Fetching Exophase...";
                try
                {
                    await FetchAndSaveExophaseAchievementsAsync().ConfigureAwait(true);
                }
                catch
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_idsSaveStatus != null) _idsSaveStatus.Text = "Fetch failed";
                    });
                }
            };
            btnPanel.Children.Add(getExoBtn);

            // SteamDB button remains PC-only
            try
            {
                if (string.Equals(GetPlatformFolderName(Game.Platform), "PC (Windows)", StringComparison.OrdinalIgnoreCase))
                {
                    var openSteamDbBtn = new Button
                    {
                        Content = "Find on SteamDB",
                        Padding = new Thickness(8, 4, 8, 4),
                        Background = System.Windows.Media.Brushes.Gray,
                        Foreground = System.Windows.Media.Brushes.White,
                        Margin = new Thickness(0, 0, 8, 0),
                        ToolTip = "Open SteamDB search page to help find the Steam App ID"
                    };
                    openSteamDbBtn.Click += (s, e) =>
                    {
                        try
                        {
                            var query = Uri.EscapeDataString(Game?.Title ?? string.Empty);
                            var url = $"https://steamdb.info/search/?a=app&q={query}";
                            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        }
                        catch
                        {
                            // ignore browser launch failures
                        }
                    };
                    btnPanel.Children.Add(openSteamDbBtn);
                }
            }
            catch
            {
                // ignore mapping errors
            }

            _idsSaveStatus = new TextBlock { Margin = new Thickness(12, 6, 0, 0), Foreground = System.Windows.Media.Brushes.LightGray };
            btnPanel.Children.Add(_idsSaveStatus);

            panel.Children.Add(btnPanel);

            SectionContent.Children.Add(panel);

            // populate values if metadata already loaded
            _ = LoadPlatformIdsAsync();
        }

        // --- Platform ID persistence helpers ---

        private sealed class PlatformIds
        {
            public string? SteamAppId { get; set; }
            public string? MicrosoftId { get; set; }
            public string? EpicId { get; set; }
            public string? EAId { get; set; }
            public string? UplayId { get; set; }
            public string? GogId { get; set; }

            // New Exophase id
            public string? ExophaseId { get; set; }
        }

        private async Task LoadPlatformIdsAsync()
        {
            try
            {
                var accountFolder = GetAccountFolder();

                // New metadata location: Metadata/<PlatformFolder>/<SanitizedGameTitle>/<SanitizedGameTitle>.json
                var platformFolder = GetPlatformFolderName(Game.Platform);
                var metaDirNew = Path.Combine(accountFolder, "Metadata", platformFolder, SanitizeForPath(Game.Title));
                var fileNew = Path.Combine(metaDirNew, SanitizeForPath(Game.Title) + ".json");

                // Backwards-compatible fallback: Metadata/<SanitizedGameTitle>.json
                var metaDirOld = Path.Combine(accountFolder, "Metadata");
                var fileOld = Path.Combine(metaDirOld, SanitizeForPath(Game.Title) + ".json");

                string? fileToRead = null;
                if (File.Exists(fileNew)) fileToRead = fileNew;
                else if (File.Exists(fileOld)) fileToRead = fileOld;

                if (fileToRead == null) return;

                var json = await File.ReadAllTextAsync(fileToRead).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json)) return;

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var ids = JsonSerializer.Deserialize<PlatformIds>(json, options);
                if (ids == null) return;

                // marshal to UI thread and populate boxes if they exist
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_steamIdBox != null) _steamIdBox.Text = ids.SteamAppId ?? string.Empty;
                    if (_msIdBox != null) _msIdBox.Text = ids.MicrosoftId ?? string.Empty;
                    if (_epicIdBox != null) _epicIdBox.Text = ids.EpicId ?? string.Empty;
                    if (_eaIdBox != null) _eaIdBox.Text = ids.EAId ?? string.Empty;
                    if (_uplayIdBox != null) _uplayIdBox.Text = ids.UplayId ?? string.Empty;
                    if (_gogIdBox != null) _gogIdBox.Text = ids.GogId ?? string.Empty;
                    if (_exophaseIdBox != null) _exophaseIdBox.Text = ids.ExophaseId ?? string.Empty;

                    if (_idsSaveStatus != null) _idsSaveStatus.Text = string.Empty;
                });
            }
            catch
            {
                // ignore errors
            }
        }

        private async void SaveIdsButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var ids = new PlatformIds
                {
                    SteamAppId = string.IsNullOrWhiteSpace(_steamIdBox?.Text) ? null : _steamIdBox?.Text.Trim(),
                    MicrosoftId = string.IsNullOrWhiteSpace(_msIdBox?.Text) ? null : _msIdBox?.Text.Trim(),
                    EpicId = string.IsNullOrWhiteSpace(_epicIdBox?.Text) ? null : _epicIdBox?.Text.Trim(),
                    EAId = string.IsNullOrWhiteSpace(_eaIdBox?.Text) ? null : _eaIdBox?.Text.Trim(),
                    UplayId = string.IsNullOrWhiteSpace(_uplayIdBox?.Text) ? null : _uplayIdBox?.Text.Trim(),
                    GogId = string.IsNullOrWhiteSpace(_gogIdBox?.Text) ? null : _gogIdBox?.Text.Trim(),
                    ExophaseId = string.IsNullOrWhiteSpace(_exophaseIdBox?.Text) ? null : _exophaseIdBox?.Text.Trim()
                };

                var accountFolder = GetAccountFolder();

                // Save to new required path: Metadata/<PlatformFolder>/<GameFolder>/<GameFile>.json
                var platformFolder = GetPlatformFolderName(Game.Platform);
                var metaDir = Path.Combine(accountFolder, "Metadata", platformFolder, SanitizeForPath(Game.Title));
                Directory.CreateDirectory(metaDir);

                var file = Path.Combine(metaDir, SanitizeForPath(Game.Title) + ".json");
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(ids, options);
                await File.WriteAllTextAsync(file, json).ConfigureAwait(false);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_idsSaveStatus != null) _idsSaveStatus.Text = "Saved";
                });
            }
            catch
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_idsSaveStatus != null) _idsSaveStatus.Text = "Save failed";
                });
            }
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

        // Helper to compute sanitized platform folder name (keeps parity with GameInfoWindow)
        private static string GetPlatformFolderName(string? platform)
        {
            if (string.IsNullOrWhiteSpace(platform)) return "Unknown";
            if (string.Equals(platform, "PC", StringComparison.OrdinalIgnoreCase)) return "PC (Windows)";
            return SanitizeForPath(platform);
        }

        // Updated sanitizer: produce "Call of Duty - Black Ops 2" from "Call of Duty: Black Ops 2"
        private static string SanitizeForPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "Unknown";

            var s = input.Trim();

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

        // Added helper methods to populate executable lists for a single drive and for all drives.
        private async Task PopulateExesForDriveAsync(string driveName, ListBox exeList)
        {
            if (exeList == null) return;
            var results = await Task.Run(() =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    // Prefer explicit launch path and preferred entry if present
                    try
                    {
                        var lp = Game?.LaunchPath;
                        if (!string.IsNullOrWhiteSpace(lp) && File.Exists(lp))
                            set.Add(lp!);
                    }
                    catch { }

                    try
                    {
                        var pref = PreferredExeStore.GetPreferredExe(Game.Title);
                        if (!string.IsNullOrWhiteSpace(pref) && File.Exists(pref))
                            set.Add(pref!);
                    }
                    catch { }

                    // Look under <Drive>:\Games\<GameFolder> (top-level and one-level deep)
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(driveName))
                        {
                            var gameFolder = Path.Combine(driveName.TrimEnd('\\'), "Games", SanitizeForPath(Game.Title));
                            if (Directory.Exists(gameFolder))
                            {
                                foreach (var f in Directory.EnumerateFiles(gameFolder, "*.exe", SearchOption.TopDirectoryOnly))
                                    set.Add(f);

                                foreach (var sub in Directory.EnumerateDirectories(gameFolder))
                                {
                                    try
                                    {
                                        foreach (var f in Directory.EnumerateFiles(sub, "*.exe", SearchOption.TopDirectoryOnly))
                                            set.Add(f);
                                    }
                                    catch { }
                                }
                            }

                            // Also check a folder named after the game directly under the drive root
                            var altFolder = Path.Combine(driveName.TrimEnd('\\'), SanitizeForPath(Game.Title));
                            if (Directory.Exists(altFolder))
                            {
                                foreach (var f in Directory.EnumerateFiles(altFolder, "*.exe", SearchOption.TopDirectoryOnly))
                                    set.Add(f);
                            }
                        }
                    }
                    catch { }
                }
                catch { }

                var list = set.OrderBy(p => Path.GetFileName(p)).ToList();
                if (list.Count == 0) list.Add("(No executables found)");
                return list;
            }).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                exeList.ItemsSource = results;
                // Select preferred exe if present in results
                try
                {
                    var pref = PreferredExeStore.GetPreferredExe(Game.Title);
                    if (!string.IsNullOrWhiteSpace(pref) && results.Any(r => string.Equals(r, pref, StringComparison.OrdinalIgnoreCase)))
                        exeList.SelectedItem = pref;
                }
                catch { }
            });
        }

        private async Task PopulateAllExesAsync(ListBox exeList)
        {
            if (exeList == null) return;
            var results = await Task.Run(() =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    // Preferred / launch path
                    try
                    {
                        var lp = Game?.LaunchPath;
                        if (!string.IsNullOrWhiteSpace(lp) && File.Exists(lp))
                            set.Add(lp!);
                    }
                    catch { }

                    try
                    {
                        var pref = PreferredExeStore.GetPreferredExe(Game.Title);
                        if (!string.IsNullOrWhiteSpace(pref) && File.Exists(pref))
                            set.Add(pref!);
                    }
                    catch { }

                    // Scan ready fixed/removable drives under <Drive>:\Games\<GameFolder> (top-level + one-level)
                    try
                    {
                        foreach (var d in DriveInfo.GetDrives()
                                     .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)))
                        {
                            try
                            {
                                var root = d.Name.TrimEnd('\\');
                                var gameFolder = Path.Combine(root, "Games", SanitizeForPath(Game.Title));
                                if (Directory.Exists(gameFolder))
                                {
                                    foreach (var f in Directory.EnumerateFiles(gameFolder, "*.exe", SearchOption.TopDirectoryOnly))
                                        set.Add(f);

                                    foreach (var sub in Directory.EnumerateDirectories(gameFolder))
                                    {
                                        try
                                        {
                                            foreach (var f in Directory.EnumerateFiles(sub, "*.exe", SearchOption.TopDirectoryOnly))
                                                set.Add(f);
                                        }
                                        catch { }
                                    }
                                }

                                // Also check <Drive>:\<GameFolder>
                                var altFolder = Path.Combine(root, SanitizeForPath(Game.Title));
                                if (Directory.Exists(altFolder))
                                {
                                    foreach (var f in Directory.EnumerateFiles(altFolder, "*.exe", SearchOption.TopDirectoryOnly))
                                        set.Add(f);
                                }
                            }
                            catch { /* per-drive errors ignored */ }
                        }
                    }
                    catch { }
                }
                catch { }

                var list = set.OrderBy(p => Path.GetFileName(p)).ToList();
                if (list.Count == 0) list.Add("(No executables found)");
                return list;
            }).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                exeList.ItemsSource = results;
                try
                {
                    var pref = PreferredExeStore.GetPreferredExe(Game.Title);
                    if (!string.IsNullOrWhiteSpace(pref) && results.Any(r => string.Equals(r, pref, StringComparison.OrdinalIgnoreCase)))
                        exeList.SelectedItem = pref;
                }
                catch { }
            });
        }

        // Add this helper method to the PropertiesWindow class (near other helpers).
        private async Task FetchAndSaveSteamAchievementsAsync()
        {
            var appId = _steamIdBox?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(appId))
            {
                await Application.Current.Dispatcher.InvokeAsync(() => { if (_idsSaveStatus != null) _idsSaveStatus.Text = "Steam App ID is required."; });
                return;
            }

            try
            {
                var accountFolder = GetAccountFolder();
                if (string.IsNullOrWhiteSpace(accountFolder))
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => { if (_idsSaveStatus != null) _idsSaveStatus.Text = "Account folder not found."; });
                    return;
                }

                // Read optional ApiKey
                string steamSettingsPath = Path.Combine(accountFolder, "Lib", "Steam", "steam.json");
                string? apiKey = null;
                if (File.Exists(steamSettingsPath))
                {
                    try
                    {
                        var txt = await File.ReadAllTextAsync(steamSettingsPath).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(txt))
                        {
                            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(txt, opts);
                            if (dict != null && dict.TryGetValue("ApiKey", out var k)) apiKey = k;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Fetch] failed reading steam.json: {ex}");
                    }
                }

                // --- Updated Steam SaveContainerAsync (use MapPlatformToFolderInternal) ---
                async Task SaveContainerAsync(List<Dictionary<string, object?>> items)
                {
                    var platformFolder = MapPlatformToFolderInternal(Game);
                    var sanitizedTitle = SanitizeForPath(Game.Title);
                    var saveDir = Path.Combine(accountFolder, "Achievements", platformFolder, sanitizedTitle);
                    Directory.CreateDirectory(saveDir);
                    var saveFile = Path.Combine(saveDir, sanitizedTitle + ".json");

                    var container = new Dictionary<string, object?> { ["achievements"] = items };
                    var writeOptions = new JsonSerializerOptions { WriteIndented = true };
                    var outJson = JsonSerializer.Serialize(container, writeOptions);
                    await File.WriteAllTextAsync(saveFile, outJson).ConfigureAwait(false);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_idsSaveStatus != null) _idsSaveStatus.Text = $"Saved achievements to: {saveFile}";
                    });
                }

                async Task ScrapeSteamAchievements()
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

                    // 1) Try Steam Web API if ApiKey available
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        try
                        {
                            var schemaUrl = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={Uri.EscapeDataString(apiKey)}&appid={Uri.EscapeDataString(appId)}";
                            System.Diagnostics.Debug.WriteLine($"[Fetch] Calling Steam API: {schemaUrl}");
                            var resp = await http.GetAsync(schemaUrl).ConfigureAwait(false);
                            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            System.Diagnostics.Debug.WriteLine($"[Fetch] Steam API status: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                            System.Diagnostics.Debug.WriteLine($"[Fetch] Steam API body preview: {TrimForLog(body)}");

                            if (resp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body))
                            {
                                using var doc = JsonDocument.Parse(body);
                                var root = doc.RootElement;
                                if (root.TryGetProperty("game", out var gameObj) &&
                                    gameObj.TryGetProperty("availableGameStats", out var stats) &&
                                    stats.TryGetProperty("achievements", out var achArray) &&
                                    achArray.ValueKind == JsonValueKind.Array)
                                {
                                    var items = new List<Dictionary<string, object?>>();
                                    foreach (var a in achArray.EnumerateArray())
                                    {
                                        string? name = a.TryGetProperty("name", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null;
                                        string? desc = a.TryGetProperty("description", out var pd) && pd.ValueKind == JsonValueKind.String ? pd.GetString() : null;
                                        string? icon = a.TryGetProperty("icon", out var pi) && pi.ValueKind == JsonValueKind.String ? pi.GetString() : null;

                                        items.Add(new Dictionary<string, object?> {
                                            ["name"] = name ?? "(unnamed)",
                                            ["description"] = desc ?? string.Empty,
                                            ["icon"] = icon ?? string.Empty,
                                            ["hidden"] = false,
                                            ["percent"] = 0.0,
                                            ["unlocked"] = false,
                                            ["DateUnlocked"] = (string?)null
                                        });
                                    }
                                    await SaveContainerAsync(items).ConfigureAwait(false);
                                    return;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Fetch] Steam API call failed: {ex}");
                        }
                    }

                    // 2) Fallback to SteamDB public endpoint
                    try
                    {
                        var steamDbUrl = $"https://steamdb.info/api/SteamAchievementSchema/?appid={Uri.EscapeDataString(appId)}";
                        System.Diagnostics.Debug.WriteLine($"[Fetch] Calling SteamDB: {steamDbUrl}");
                        var resp2 = await http.GetAsync(steamDbUrl).ConfigureAwait(false);
                        var body2 = await resp2.Content.ReadAsStringAsync().ConfigureAwait(false);
                        System.Diagnostics.Debug.WriteLine($"[Fetch] SteamDB status: {(int)resp2.StatusCode} {resp2.ReasonPhrase}");
                        System.Diagnostics.Debug.WriteLine($"[Fetch] SteamDB body preview: {TrimForLog(body2)}");

                        if (resp2.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body2))
                        {
                            using var doc = JsonDocument.Parse(body2);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                            {
                                var items = new List<Dictionary<string, object?>>();
                                foreach (var a in data.EnumerateArray())
                                {
                                    string? name = a.TryGetProperty("name", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null;
                                    string? desc = a.TryGetProperty("desc", out var pd) && pd.ValueKind == JsonValueKind.String ? pd.GetString() : null;

                                    items.Add(new Dictionary<string, object?> {
                                        ["name"] = name ?? "(unnamed)",
                                        ["description"] = desc ?? string.Empty,
                                        ["icon"] = string.Empty,
                                        ["hidden"] = false,
                                        ["percent"] = 0.0,
                                        ["unlocked"] = false,
                                        ["DateUnlocked"] = (string?)null
                                    });
                                }
                                await SaveContainerAsync(items).ConfigureAwait(false);
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Fetch] SteamDB call failed: {ex}");
                    }

                    await Application.Current.Dispatcher.InvokeAsync(() => { if (_idsSaveStatus != null) _idsSaveStatus.Text = "Unable to fetch achievements from Steam or SteamDB."; });
                }

                await ScrapeSteamAchievements();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Fetch] Unexpected error: {ex}");
                await Application.Current.Dispatcher.InvokeAsync(() => { if (_idsSaveStatus != null) _idsSaveStatus.Text = "Unexpected error during fetch."; });
            }

            static string TrimForLog(string s, int max = 2000)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;
                if (s.Length <= max) return s;
                return s.Substring(0, max) + "...(truncated)";
            }
        }

        // Replaced FetchAndSaveExophaseAchievementsAsync and ExtractUnlockDateFromHtmlFragment
        // to correctly decode Exophase's escaped `data-tippy-content`, extract description,
        // and reliably detect unlocked achievements (date/span, award-earned element or unlock icon).
        private async Task FetchAndSaveExophaseAchievementsAsync()
        {
            var idOrUrl = _exophaseIdBox?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(idOrUrl))
            {
                await Application.Current.Dispatcher.InvokeAsync(() => { if (_idsSaveStatus != null) _idsSaveStatus.Text = "Exophase ID or URL is required."; });
                return;
            }

            // prefer full URL; otherwise treat input as search term/slug
            var initialUrl = idOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? idOrUrl
                : $"https://www.exophase.com/search/?q={Uri.EscapeDataString(idOrUrl)}";

            await Task.Run(async () =>
            {
                try
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => { if (_idsSaveStatus != null) _idsSaveStatus.Text = "Launching headless browser..."; });

                    var chromeOptions = new ChromeOptions();
                    chromeOptions.AddArgument("--headless=new");
                    chromeOptions.AddArgument("--disable-gpu");
                    chromeOptions.AddArgument("--no-sandbox");
                    chromeOptions.AddArgument("--disable-dev-shm-usage");
                    chromeOptions.AddArgument("--window-size=1920,1080");
                    chromeOptions.AddArgument("user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36");
                    chromeOptions.AddExcludedArgument("enable-logging");
                    chromeOptions.AddExcludedArgument("enable-automation");

                    using var driver = new ChromeDriver(chromeOptions);
                    try
                    {
                        driver.Navigate().GoToUrl(initialUrl);

                        // wait until an achievements list appears (either desktop or mobile markup)
                        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(25));
                        wait.Until(d =>
                        {
                            try
                            {
                                return d.FindElements(By.CssSelector("ul.achievement")).Count > 0
                                    || d.FindElements(By.CssSelector("ul.row")).Any(el => el.GetAttribute("class")?.Contains("achievement") == true)
                                    || d.PageSource.IndexOf("class=\"award", StringComparison.OrdinalIgnoreCase) >= 0;
                            }
                            catch { return false; }
                        });

                        var pageSource = driver.PageSource;

                        // If we landed on a search page, try to follow the first /game/ or /achievement/ link
                        if (pageSource.IndexOf("/game/", StringComparison.OrdinalIgnoreCase) >= 0 || pageSource.IndexOf("/achievement/", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var docForSearch = new HtmlDocument();
                            docForSearch.LoadHtml(pageSource);
                            var firstLink = docForSearch.DocumentNode.SelectSingleNode("//a[contains(@href,'/game/') or contains(@href,'/achievement/')]");
                            if (firstLink != null)
                            {
                                var href = firstLink.GetAttributeValue("href", string.Empty);
                                if (!string.IsNullOrEmpty(href))
                                {
                                    var resolved = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : ("https://www.exophase.com" + href);
                                    driver.Navigate().GoToUrl(resolved);
                                    wait.Until(d =>
                                    {
                                        try
                                        {
                                            return d.FindElements(By.CssSelector("ul.achievement")).Count > 0
                                                || d.PageSource.IndexOf("class=\"award", StringComparison.OrdinalIgnoreCase) >= 0;
                                        }
                                        catch { return false; }
                                    });
                                    pageSource = driver.PageSource;
                                }
                            }
                        }

                        // parse pageSource with HtmlAgilityPack
                        var htmlDoc = new HtmlDocument();
                        htmlDoc.LoadHtml(pageSource);

                        // attempt to get game name
                        string gameName = "Game";
                        var titleNode = htmlDoc.DocumentNode.SelectSingleNode("//title");
                        if (titleNode != null)
                        {
                            var match = Regex.Match(titleNode.InnerText.Trim(), @"(.+?) Achievements", RegexOptions.IgnoreCase);
                            if (match.Success) gameName = match.Groups[1].Value.Trim();
                            else gameName = titleNode.InnerText.Trim();
                        }
                        else
                        {
                            var h1 = htmlDoc.DocumentNode.SelectSingleNode("//h1");
                            if (h1 != null) gameName = HtmlEntity.DeEntitize(h1.InnerText.Trim());
                        }

                        var achievements = new List<Dictionary<string, object?>>();

                        // Find all UL nodes that contain 'achievement' in class
                        var ulNodes = htmlDoc.DocumentNode.SelectNodes("//ul[contains(@class,'achievement') or contains(@class,'achievement-list')]");
                        if (ulNodes == null)
                        {
                            // some pages use different markup; also try any UL containing li with class 'award'
                            ulNodes = htmlDoc.DocumentNode.SelectNodes("//ul[.//li[contains(@class,'award')]]");
                        }

                        if (ulNodes != null)
                        {
                            foreach (var ul in ulNodes)
                            {
                                var liNodes = ul.SelectNodes(".//li");
                                if (liNodes == null) continue;

                                foreach (var li in liNodes)
                                {
                                    // Get icon img (may be null)
                                    var img = li.SelectSingleNode(".//img[contains(@class,'award-image') or contains(@class,'trophy-image')]");
                                    var icon = img?.GetAttributeValue("src", "").Trim() ?? string.Empty;

                                    // Title link / name
                                    var titleNodeDiv = li.SelectSingleNode(".//div[contains(@class,'award-title')]");
                                    var a = titleNodeDiv?.SelectSingleNode(".//a");
                                    var name = a != null ? HtmlEntity.DeEntitize(a.InnerText.Trim()) : string.Empty;

                                    // points
                                    var pointsNode = li.SelectSingleNode(".//div[contains(@class,'award-points')]//span");
                                    double points = 0;
                                    if (pointsNode != null && int.TryParse(Regex.Replace(pointsNode.InnerText, @"\D", ""), out var p)) points = p;

                                    // percent - from data-average attribute on li
                                    double percent = 0.0;
                                    var avgAttr = li.GetAttributeValue("data-average", null);
                                    if (!string.IsNullOrWhiteSpace(avgAttr) && double.TryParse(avgAttr, out var avgVal)) percent = avgVal;

                                    // tippy-like content: try multiple attribute locations (img, anchor, li)
                                    string tippyRaw = string.Empty;
                                    if (img != null) tippyRaw = img.GetAttributeValue("data-tippy-content", string.Empty);
                                    if (string.IsNullOrWhiteSpace(tippyRaw) && a != null) tippyRaw = a.GetAttributeValue("data-tippy-content", string.Empty);
                                    if (string.IsNullOrWhiteSpace(tippyRaw)) tippyRaw = li.GetAttributeValue("data-tippy-content", string.Empty);
                                    if (string.IsNullOrWhiteSpace(tippyRaw)) tippyRaw = img?.GetAttributeValue("data-original-title", string.Empty) ?? string.Empty;
                                    if (string.IsNullOrWhiteSpace(tippyRaw)) tippyRaw = a?.GetAttributeValue("title", string.Empty) ?? string.Empty;

                                    // decode + de-entitize tooltip HTML so escaped fragments become real tags/text
                                    var decodedTippy = string.IsNullOrWhiteSpace(tippyRaw)
                                        ? string.Empty
                                        : HtmlEntity.DeEntitize(WebUtility.HtmlDecode(tippyRaw));

                                    // description extraction (prefer <p> inside decoded tippy, otherwise award-description div)
                                    string description = string.Empty;
                                    if (!string.IsNullOrWhiteSpace(decodedTippy))
                                    {
                                        var mm = Regex.Match(decodedTippy, @"<p>(.*?)<\/p>", RegexOptions.Singleline);
                                        if (mm.Success) description = HtmlEntity.DeEntitize(Regex.Replace(mm.Groups[1].Value, "<.*?>", string.Empty)).Trim();
                                        else description = HtmlEntity.DeEntitize(Regex.Replace(decodedTippy, "<.*?>", string.Empty)).Trim();
                                    }

                                    if (string.IsNullOrWhiteSpace(description))
                                    {
                                        var descDiv = li.SelectSingleNode(".//div[contains(@class,'award-description')]");
                                        if (descDiv != null) description = HtmlEntity.DeEntitize(Regex.Replace(descDiv.InnerHtml, "<.*?>", string.Empty)).Trim();
                                    }

                                    // unlocked date detection (decoded tippy first, then award-earned element)
                                    string? unlockedAt = ExtractUnlockDateFromHtmlFragment(decodedTippy);
                                    if (string.IsNullOrWhiteSpace(unlockedAt))
                                    {
                                        var earnedDiv = li.SelectSingleNode(".//div[contains(@class,'award-earned') or contains(@class,'earned')]");
                                        if (earnedDiv != null)
                                        {
                                            var span = earnedDiv.SelectSingleNode(".//span");
                                            unlockedAt = span != null ? HtmlEntity.DeEntitize(span.InnerText).Trim() : HtmlEntity.DeEntitize(earnedDiv.InnerText).Trim();
                                        }
                                    }

                                    // If no explicit date, presence of unlock icon implies unlocked
                                    var hasUnlockIconInTippy = !string.IsNullOrWhiteSpace(decodedTippy) && decodedTippy.IndexOf("exo-icon-unlock", StringComparison.OrdinalIgnoreCase) >= 0;
                                    var hasUnlockIconInDom = li.SelectSingleNode(".//i[contains(@class,'exo-icon-unlock')]") != null;

                                    bool unlocked = !string.IsNullOrEmpty(unlockedAt) || hasUnlockIconInTippy || hasUnlockIconInDom;

                                    // hidden flag
                                    bool hidden = img != null && (img.GetAttributeValue("class", "")?.Contains("hidden") == true);

                                    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(icon))
                                    {
                                        // skip incomplete
                                        continue;
                                    }

                                    achievements.Add(new Dictionary<string, object?>
                                    {
                                        ["name"] = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name,
                                        ["description"] = description ?? string.Empty,
                                        ["icon"] = icon ?? string.Empty,
                                        ["hidden"] = hidden,
                                        ["percent"] = percent,
                                        ["unlocked"] = unlocked,
                                        ["DateUnlocked"] = unlocked && !string.IsNullOrWhiteSpace(unlockedAt) ? unlockedAt : null,
                                        ["points"] = points
                                    });
                                }
                            }
                        }

                        if (achievements.Count == 0)
                        {
                            await Application.Current.Dispatcher.InvokeAsync(() => { if (_idsSaveStatus != null) _idsSaveStatus.Text = "No achievements parsed from Exophase page."; });
                            return;
                        }

                        // Save to same container structure used elsewhere
                        var accountFolder = GetAccountFolder();
                        var platformFolder = MapPlatformToFolderInternal(Game);
                        var sanitizedTitle = SanitizeForPath(Game.Title);
                        var saveDir = Path.Combine(accountFolder, "Achievements", platformFolder, sanitizedTitle);
                        Directory.CreateDirectory(saveDir);
                        var saveFile = Path.Combine(saveDir, sanitizedTitle + ".json");

                        var container = new Dictionary<string, object?> { ["game"] = gameName, ["achievements"] = achievements };
                        var writeOptions = new JsonSerializerOptions { WriteIndented = true };
                        var outJson = JsonSerializer.Serialize(container, writeOptions);
                        await File.WriteAllTextAsync(saveFile, outJson).ConfigureAwait(false);

                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (_idsSaveStatus != null) _idsSaveStatus.Text = $"Saved achievements to: {saveFile}";
                        });
                    }
                    finally
                    {
                        try { driver.Quit(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Exophase Fetch] error: {ex}");
                    await Application.Current.Dispatcher.InvokeAsync(() => { if (_idsSaveStatus != null) _idsSaveStatus.Text = "Unexpected error during Exophase fetch."; });
                }
            }).ConfigureAwait(false);
        }

        // Helper: robustly extract an earned/unlock date from an HTML fragment or plain text (returns null if not found)
        private static string? ExtractUnlockDateFromHtmlFragment(string? htmlFragment)
        {
            if (string.IsNullOrWhiteSpace(htmlFragment))
                return null;

            try
            {
                // Decode input (handles escaped fragments like \u003Cspan\u003E...) then parse
                var decoded = HtmlEntity.DeEntitize(WebUtility.HtmlDecode(htmlFragment));
                var wrapped = $"<div>{decoded}</div>";
                var doc = new HtmlDocument();
                doc.LoadHtml(wrapped);

                // 1) prefer a span or time element
                var span = doc.DocumentNode.SelectSingleNode("//span") ?? doc.DocumentNode.SelectSingleNode("//time");
                if (span != null)
                {
                    var txt = HtmlEntity.DeEntitize(span.InnerText).Trim();
                    if (!string.IsNullOrWhiteSpace(txt))
                        return txt;
                }

                // 2) fallback: entire text content - try to find a month-day-year pattern (e.g. October 30, 2017)
                var fullText = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText).Trim();
                if (string.IsNullOrWhiteSpace(fullText)) return null;

                // common date patterns with month names
                var monthPattern = @"\b(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},\s+\d{4}(?:[^\r\n()]*)";
                var m = Regex.Match(fullText, monthPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (m.Success)
                {
                    return m.Value.Trim();
                }

                // ISO / numeric date fallback (YYYY-MM-DD, DD/MM/YYYY etc.)
                var isoMatch = Regex.Match(fullText, @"\b\d{4}-\d{2}-\d{2}\b");
                if (isoMatch.Success) return isoMatch.Value;

                var altMatch = Regex.Match(fullText, @"\b\d{1,2}\/\d{1,2}\/\d{2,4}\b");
                if (altMatch.Success) return altMatch.Value;

                // If nothing matched, but text contains familiar keywords like "UTC" or a year, return trimmed text
                if (fullText.IndexOf("UTC", StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(fullText, @"\b\d{4}\b"))
                    return fullText;

                return null;
            }
            catch
            {
                return null;
            }
        }

        // --- New: static mapper used by PropertiesWindow (copy of GameInfoWindow mapping) ---
        private static string MapPlatformToFolderInternal(GameItem? game)
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
    }

    // Simple per-user store for preferred executables keyed by game title.
    // File is placed under %AppData%\PS5_OS\preferred_exes.json
    // Changed to store structured entries { "Path": "...", "Arguments": "..." } but will remain backward-compatible
    internal static class PreferredExeStore
    {
        private static readonly object _sync = new();
        private static readonly string _filePath;

        private class PreferredEntry
        {
            public string? Path { get; set; }
            public string? Arguments { get; set; }
        }

        static PreferredExeStore()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PS5_OS");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                _filePath = Path.Combine(dir, "preferred_exes.json");
            }
            catch
            {
                // fall back to AppContext.BaseDirectory if AppData unavailable
                _filePath = Path.Combine(AppContext.BaseDirectory, "preferred_exes.json");
            }
        }

        // Returns stored exe path (legacy-friendly)
        public static string? GetPreferredExe(string gameTitle)
        {
            if (string.IsNullOrWhiteSpace(gameTitle)) return null;

            try
            {
                lock (_sync)
                {
                    if (!File.Exists(_filePath)) return null;
                    var json = File.ReadAllText(_filePath);
                    if (string.IsNullOrWhiteSpace(json)) return null;

                    // Attempt to read structured entries first
                    try
                    {
                        var dict = JsonSerializer.Deserialize<Dictionary<string, PreferredEntry>>(json);
                        if (dict != null && dict.TryGetValue(gameTitle, out var entry))
                            return entry?.Path;
                    }
                    catch { /* try legacy */ }

                    // Legacy: dictionary<string,string>
                    try
                    {
                        var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        if (legacy != null && legacy.TryGetValue(gameTitle, out var path))
                            return path;
                    }
                    catch { }

                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        // Returns stored arguments for a game (if any)
        public static string? GetPreferredExeArguments(string gameTitle)
        {
            if (string.IsNullOrWhiteSpace(gameTitle)) return null;

            try
            {
                lock (_sync)
                {
                    if (!File.Exists(_filePath)) return null;
                    var json = File.ReadAllText(_filePath);
                    if (string.IsNullOrWhiteSpace(json)) return null;

                    try
                    {
                        var dict = JsonSerializer.Deserialize<Dictionary<string, PreferredEntry>>(json);
                        if (dict != null && dict.TryGetValue(gameTitle, out var entry))
                            return entry?.Arguments;
                    }
                    catch { /* if file is legacy string map, no args present */ }

                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        // Set both path and arguments. Passing null for exePath removes the entry entirely.
        public static void SetPreferredExe(string gameTitle, string? exePath, string? arguments)
        {
            if (string.IsNullOrWhiteSpace(gameTitle)) return;

            try
            {
                lock (_sync)
                {
                    Dictionary<string, PreferredEntry> dict;
                    if (File.Exists(_filePath))
                    {
                        try
                        {
                            var existing = File.ReadAllText(_filePath);
                            // Try to deserialize structured first
                            var structured = JsonSerializer.Deserialize<Dictionary<string, PreferredEntry>>(existing);
                            if (structured != null)
                            {
                                dict = new Dictionary<string, PreferredEntry>(structured, StringComparer.OrdinalIgnoreCase);
                            }
                            else
                            {
                                // Try legacy string map and convert
                                var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(existing);
                                if (legacy != null)
                                {
                                    dict = legacy.ToDictionary(kvp => kvp.Key, kvp => new PreferredEntry { Path = kvp.Value, Arguments = null }, StringComparer.OrdinalIgnoreCase);
                                }
                                else
                                {
                                    dict = new Dictionary<string, PreferredEntry>(StringComparer.OrdinalIgnoreCase);
                                }
                            }
                        }
                        catch
                        {
                            dict = new Dictionary<string, PreferredEntry>(StringComparer.OrdinalIgnoreCase);
                        }
                    }
                    else
                    {
                        dict = new Dictionary<string, PreferredEntry>(StringComparer.OrdinalIgnoreCase);
                    }

                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        // remove entry
                        if (dict.ContainsKey(gameTitle))
                            dict.Remove(gameTitle);
                    }
                    else
                    {
                        dict[gameTitle] = new PreferredEntry { Path = exePath, Arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments };
                    }

                    var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_filePath, json);
                }
            }
            catch
            {
                // ignore persistence errors
            }
        }
    }
}