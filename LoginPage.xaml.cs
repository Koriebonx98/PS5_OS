using System;
using System.Collections.ObjectModel;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shapes;
using System.Windows.Media.Effects;
using System.Media;

namespace PS5_OS
{
    // Public top-level model so XAML can reference "local:AccountItem"
    public sealed class AccountItem
    {
        public bool IsAdd { get; set; }
        public string Name { get; set; } = string.Empty;
        public override string ToString() => Name;
    }

    public partial class LoginPage : UserControl
    {
        private readonly string _accountsRoot;
        private readonly ObservableCollection<AccountItem> _accounts = new();

        // Audio players for navigation and activation (wav files live next to background)
        private SoundPlayer? _navPlayer;
        private SoundPlayer? _actPlayer;

        public LoginPage()
        {
            InitializeComponent();

            // Set runtime background from exe directory Data\Resources\Dashboard\PS5Background_All.jpg
            TrySetBackgroundFromAppDir();

            // Try to load audio players (non-fatal if files missing)
            TryLoadAudioPlayers();

            _accountsRoot = Path.Combine(AppContext.BaseDirectory, "Data", "Accounts");
            lstAccounts.ItemsSource = _accounts;

            // wire events
            lstAccounts.SelectionChanged += LstAccounts_SelectionChanged;
            lstAccounts.PreviewKeyDown += LstAccounts_PreviewKeyDown;
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
                        _navPlayer.LoadAsync(); // preload
                    }
                    catch { _navPlayer = null; }
                }

                if (File.Exists(actPath))
                {
                    try
                    {
                        _actPlayer = new SoundPlayer(actPath);
                        _actPlayer.LoadAsync(); // preload
                    }
                    catch { _actPlayer = null; }
                }
            }
            catch
            {
                // Swallow any errors - audio is optional
                _navPlayer = null;
                _actPlayer = null;
            }
        }

        private void PlayNavigation()
        {
            try
            {
                _navPlayer?.Play();
            }
            catch { /* ignore audio errors */ }
        }

        private void PlayActivation()
        {
            try
            {
                _actPlayer?.Play();
            }
            catch { /* ignore audio errors */ }
        }

        private void TrySetBackgroundFromAppDir()
        {
            try
            {
                var imagePath = Path.Combine(AppContext.BaseDirectory, "Data", "Resources", "Dashboard", "PS5Background_All.jpg");
                if (File.Exists(imagePath))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();

                    var brush = new ImageBrush(bi)
                    {
                        Stretch = Stretch.UniformToFill,
                        AlignmentX = AlignmentX.Center,
                        AlignmentY = AlignmentY.Center
                    };

                    // apply to the UserControl background so XAML overlay still renders above it
                    this.Background = brush;
                }
                else
                {
                    // Log missing file so you can diagnose startup issues
                    try
                    {
                        var logFile = Path.Combine(AppContext.BaseDirectory, "crash.log");
                        File.AppendAllText(logFile, $"{DateTime.UtcNow:u} LoginPage background missing: {imagePath}{Environment.NewLine}");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                // Log and swallow - avoid crashing before login
                try
                {
                    var logFile = Path.Combine(AppContext.BaseDirectory, "crash.log");
                    File.AppendAllText(logFile, $"{DateTime.UtcNow:u} LoginPage TrySetBackgroundFromAppDir error: {ex}{Environment.NewLine}");
                }
                catch { }
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureAccountsFolder();
            LoadAccounts();

            // focus the list so arrow keys work immediately
            if (_accounts.Count > 0)
            {
                // select first real account if present; otherwise select Add
                lstAccounts.SelectedIndex = _accounts.Count > 1 ? 1 : 0;
                lstAccounts.Focus();
            }
        }

        private void EnsureAccountsFolder()
        {
            try
            {
                if (!Directory.Exists(_accountsRoot))
                    Directory.CreateDirectory(_accountsRoot);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to prepare accounts folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadAccounts()
        {
            _accounts.Clear();

            // First item is the Add User placeholder
            _accounts.Add(new AccountItem { IsAdd = true, Name = "Add User" });

            try
            {
                if (!Directory.Exists(_accountsRoot)) return;

                var dirs = Directory.GetDirectories(_accountsRoot)
                                    .Select(Path.GetFileName)
                                    .OrderBy(n => n, StringComparer.InvariantCultureIgnoreCase);

                foreach (var d in dirs)
                {
                    _accounts.Add(new AccountItem { IsAdd = false, Name = d! });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to load accounts: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Add a new account (from Add item)
        private void BtnAdd_Click(object sender, RoutedEventArgs e) => AddAccountInteractive();

        private void AddAccountInteractive()
        {
            var name = PromptForAccountName();
            if (string.IsNullOrWhiteSpace(name)) return;

            name = SanitizeName(name);
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Invalid account name.", "Add account", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var path = Path.Combine(_accountsRoot, name);
            try
            {
                if (Directory.Exists(path))
                {
                    MessageBox.Show("An account with that name already exists.", "Add account", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Directory.CreateDirectory(path);

                // insert new account after the Add placeholder (index 0)
                var item = new AccountItem { IsAdd = false, Name = name };
                _accounts.Insert(1, item);
                lstAccounts.SelectedItem = item;
                lstAccounts.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to create account: {ex.Message}", "Add account", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Minimal inline prompt window (unchanged)
        private string? PromptForAccountName()
        {
            var prompt = new Window
            {
                Title = "Create account",
                Width = 420,
                Height = 140,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = Application.Current?.MainWindow,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };

            var tb = new TextBox { Margin = new Thickness(12), MinWidth = 360 };
            var btnOk = new Button { Content = "Create", Width = 88, IsDefault = true, Margin = new Thickness(6) };
            var btnCancel = new Button { Content = "Cancel", Width = 88, IsCancel = true, Margin = new Thickness(6) };

            btnOk.Click += (_, __) => prompt.DialogResult = true;
            btnCancel.Click += (_, __) => prompt.DialogResult = false;

            var spButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 12, 12) };
            spButtons.Children.Add(btnOk);
            spButtons.Children.Add(btnCancel);

            var root = new DockPanel();
            DockPanel.SetDock(tb, Dock.Top);
            root.Children.Add(tb);
            root.Children.Add(spButtons);

            prompt.Content = root;

            var result = prompt.ShowDialog();
            if (result == true)
            {
                return tb.Text?.Trim();
            }

            return null;
        }

        private static string SanitizeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '-');

            return string.Join(' ', name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private void LstAccounts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstAccounts.SelectedItem is AccountItem item)
            {
                txtSelected.Text = item.IsAdd ? "(Add User)" : item.Name;
            }
            else
            {
                txtSelected.Text = "(none)";
            }
        }

        // handle arrow keys and Enter for selection/navigation
        private async void LstAccounts_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_accounts.Count == 0) return;

            var lb = lstAccounts;
            int idx = lb.SelectedIndex >= 0 ? lb.SelectedIndex : 0;

            if (e.Key == Key.Left || e.Key == Key.Up)
            {
                // play navigation sound
                PlayNavigation();

                idx = (idx - 1 + _accounts.Count) % _accounts.Count;
                lb.SelectedIndex = idx;
                lb.ScrollIntoView(lb.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Right || e.Key == Key.Down)
            {
                // play navigation sound
                PlayNavigation();

                idx = (idx + 1) % _accounts.Count;
                lb.SelectedIndex = idx;
                lb.ScrollIntoView(lb.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                // play activation sound
                PlayActivation();

                if (lb.SelectedItem is AccountItem ai)
                {
                    if (ai.IsAdd)
                    {
                        // trigger add
                        AddAccountInteractive();
                    }
                    else
                    {
                        // trigger login flow for selected account (with animation)
                        await PerformLoginAsync(ai.Name);
                    }
                }
                e.Handled = true;
            }
        }

        // When login requested, set runtime-tracked logged-in account and switch to Dashboard.
        private async Task PerformLoginAsync(string name)
        {
            var accountPath = Path.Combine(_accountsRoot, name);
            if (!Directory.Exists(accountPath))
            {
                MessageBox.Show("Selected account folder is missing.", "Login", MessageBoxButton.OK, MessageBoxImage.Error);
                LoadAccounts();
                return;
            }

            // Track logged-in user at runtime
            Application.Current.Properties["LoggedInAccount"] = name;
            Application.Current.Properties["LoggedInAccountPath"] = accountPath;

            try
            {
                File.WriteAllText(Path.Combine(_accountsRoot, "last.txt"), name);
            }
            catch { /* ignore */ }

            try
            {
                if (Application.Current?.MainWindow != null)
                {
                    // obtain the container for the selected item
                    var selected = lstAccounts.SelectedItem;
                    ListBoxItem? container = null;
                    if (selected != null)
                    {
                        container = (ListBoxItem?)lstAccounts.ItemContainerGenerator.ContainerFromItem(selected);
                        if (container == null)
                        {
                            lstAccounts.UpdateLayout();
                            container = (ListBoxItem?)lstAccounts.ItemContainerGenerator.ContainerFromItem(selected);
                        }
                    }

                    try
                    {
                        if (container != null)
                        {
                            await PlayLoginAnimationAsync(container);
                        }
                        else
                        {
                            await SimpleFadeOutAsync();
                        }
                    }
                    catch
                    {
                        // ignore animation errors
                    }

                    // replace the content of the same main window with Dashboard (login and dashboard stay in same window)
                    Application.Current.MainWindow.Content = new Dashboard();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to open dashboard: {ex.Message}", "Login", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task SimpleFadeOutAsync()
        {
            TransitionLayer.Visibility = Visibility.Visible;
            TransitionLayer.IsHitTestVisible = true;

            var overlayAnim = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(400)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var tcs = new TaskCompletionSource<object?>();
            overlayAnim.Completed += (_, __) => tcs.TrySetResult(null);
            TransitionOverlay.BeginAnimation(OpacityProperty, overlayAnim);

            await tcs.Task;

            TransitionLayer.Visibility = Visibility.Collapsed;
            TransitionLayer.IsHitTestVisible = false;
        }

        // Rich PS5-like transition
        private async Task PlayLoginAnimationAsync(ListBoxItem itemContainer)
        {
            // Ensure layout is updated
            itemContainer.BringIntoView();
            itemContainer.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Render);

            // get position relative to this control
            var relativeTransform = itemContainer.TransformToVisual(this);
            var itemRect = relativeTransform.TransformBounds(new Rect(new Point(0, 0), itemContainer.RenderSize));

            // render the item to bitmap
            RenderTargetBitmap? rtb = null;
            try
            {
                var pxWidth = Math.Max(1, (int)Math.Ceiling(itemContainer.ActualWidth));
                var pxHeight = Math.Max(1, (int)Math.Ceiling(itemContainer.ActualHeight));
                rtb = new RenderTargetBitmap(pxWidth, pxHeight, 96, 96, PixelFormats.Pbgra32);

                var dv = new DrawingVisual();
                using (var ctx = dv.RenderOpen())
                {
                    var vb = new VisualBrush(itemContainer);
                    ctx.DrawRectangle(vb, null, new Rect(new Size(itemContainer.ActualWidth, itemContainer.ActualHeight)));
                }

                rtb.Render(dv);
            }
            catch
            {
                rtb = null;
            }

            // fallback if capture failed
            if (rtb == null)
            {
                await SimpleFadeOutAsync();
                return;
            }

            // prepare overlay/hosts
            TransitionLayer.Visibility = Visibility.Visible;
            TransitionLayer.IsHitTestVisible = true;
            TransitionOverlay.Opacity = 0;
            BurstEllipse.Opacity = 0;
            BurstEllipse.Width = 0;
            BurstEllipse.Height = 0;
            TransitionHost.Children.Clear();

            // create image
            var img = new Image
            {
                Source = rtb,
                Width = itemContainer.ActualWidth,
                Height = itemContainer.ActualHeight,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            // transforms
            var scale = new ScaleTransform(1, 1);
            var translate = new TranslateTransform(0, 0);
            var tg = new TransformGroup();
            tg.Children.Add(scale);
            tg.Children.Add(translate);
            img.RenderTransform = tg;

            // compute starting position within TransitionHost (coordinates relative to this)
            Canvas.SetLeft(img, itemRect.Left);
            Canvas.SetTop(img, itemRect.Top);

            // place the burst ellipse centered on the avatar
            var burst = BurstEllipse;
            double burstCenterX = itemRect.Left + itemContainer.ActualWidth / 2.0;
            double burstCenterY = itemRect.Top + itemContainer.ActualHeight / 2.0;
            // position ellipse's center (Canvas uses top-left)
            Canvas.SetLeft(burst, burstCenterX - (burst.Width / 2.0));
            Canvas.SetTop(burst, burstCenterY - (burst.Height / 2.0));

            // ensure there is a copy of burst in the host (BurstEllipse is defined in XAML tree; clone visual placement)
            // we'll instead create a runtime ellipse to animate (so we can add it to TransitionHost without disturbing original)
            var runtimeBurst = new Ellipse
            {
                Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                Opacity = 0,
                Width = 0,
                Height = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Effect = new BlurEffect { Radius = 40 }
            };
            runtimeBurst.RenderTransform = new ScaleTransform(1, 1);

            Canvas.SetLeft(runtimeBurst, burstCenterX);
            Canvas.SetTop(runtimeBurst, burstCenterY);

            // add to host (burst behind image)
            TransitionHost.Children.Add(runtimeBurst);
            TransitionHost.Children.Add(img);

            // target center = center of this control
            var targetCenter = new Point(ActualWidth / 2.0, ActualHeight / 2.0);
            var imgCenter = new Point(itemRect.Left + itemContainer.ActualWidth / 2.0, itemRect.Top + itemContainer.ActualHeight / 2.0);
            var delta = new Point(targetCenter.X - imgCenter.X, targetCenter.Y - imgCenter.Y);

            // prepare animations
            var totalMs = 800;
            var overlayAnim = new DoubleAnimation(0.0, 0.92, TimeSpan.FromMilliseconds(totalMs))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut }
            };

            var scaleAnim = new DoubleAnimation(1.0, 5.5, TimeSpan.FromMilliseconds(totalMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            var translateXAnim = new DoubleAnimation(0, delta.X, TimeSpan.FromMilliseconds(totalMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            var translateYAnim = new DoubleAnimation(0, delta.Y, TimeSpan.FromMilliseconds(totalMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            var fadeOutAnim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = TimeSpan.FromMilliseconds(totalMs * 0.55),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var burstSizeAnim = new DoubleAnimation(0, Math.Max(ActualWidth, ActualHeight) * 1.6, TimeSpan.FromMilliseconds(totalMs))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut }
            };

            var burstFadeAnim = new DoubleAnimation(0.0, 0.35, TimeSpan.FromMilliseconds(200))
            {
                BeginTime = TimeSpan.FromMilliseconds(40),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var blurEffect = MainContent.Effect as BlurEffect;
            var blurAnim = new DoubleAnimation(0.0, 22.0, TimeSpan.FromMilliseconds(totalMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var titleFade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = TimeSpan.FromMilliseconds(totalMs * 0.25),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var titleTranslate = new DoubleAnimation(0, -28, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = TimeSpan.FromMilliseconds(totalMs * 0.25),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            // apply animations
            var tcs = new TaskCompletionSource<object?>();
            int toComplete = 5;
            void OnAnimDone(object? s, EventArgs e)
            {
                toComplete--;
                if (toComplete <= 0)
                    tcs.TrySetResult(null);
            }

            overlayAnim.Completed += OnAnimDone;
            scaleAnim.Completed += OnAnimDone;
            fadeOutAnim.Completed += OnAnimDone;
            burstSizeAnim.Completed += OnAnimDone;
            blurAnim.Completed += OnAnimDone;

            // start animations
            TransitionOverlay.BeginAnimation(OpacityProperty, overlayAnim);

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            translate.BeginAnimation(TranslateTransform.XProperty, translateXAnim);
            translate.BeginAnimation(TranslateTransform.YProperty, translateYAnim);

            img.BeginAnimation(OpacityProperty, fadeOutAnim);

            // animate runtimeBurst width/height and opacity (set center by adjusting Canvas.Left/Top before/after)
            runtimeBurst.BeginAnimation(WidthProperty, burstSizeAnim);
            runtimeBurst.BeginAnimation(HeightProperty, burstSizeAnim);
            runtimeBurst.BeginAnimation(OpacityProperty, burstFadeAnim);

            // animate blur on main content
            if (blurEffect != null)
                blurEffect.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);

            // animate title (translate + fade) by applying RenderTransform
            TitlePanel.RenderTransform = new TranslateTransform(0, 0);
            TitlePanel.BeginAnimation(OpacityProperty, titleFade);
            (TitlePanel.RenderTransform as TranslateTransform)?.BeginAnimation(TranslateTransform.YProperty, titleTranslate);

            // wait for animations
            await tcs.Task;

            // small delay to let final frame settle
            await Task.Delay(80);

            // cleanup
            TransitionHost.Children.Clear();
            TransitionLayer.Visibility = Visibility.Collapsed;
            TransitionLayer.IsHitTestVisible = false;

            // reset blur
            if (blurEffect != null)
                blurEffect.Radius = 0;
            TitlePanel.Opacity = 1.0;
            TitlePanel.RenderTransform = null;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            // clear selection
            lstAccounts.SelectedIndex = -1;
            txtSelected.Text = "(none)";
        }
    }
}