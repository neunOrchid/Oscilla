using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

// =================================================================
// 【核心修复】：砍掉旧的 UI.core 引用，换上剥离 UI. 前缀后的标准三层架构引用
// =================================================================
using Oscilla.Core;   // 【新增】引入音频核心层，认出 Track 类
using Oscilla.Logic;  // 【新增】引入业务逻辑层，认出 LibraryManager 和 Logger

namespace Oscilla.UI
{
    public class PlaylistRenamedEventArgs : EventArgs
    {
        public string OldName { get; set; } = string.Empty;
        public string NewName { get; set; } = string.Empty;
    }

    public class PlaylistDeleteEventArgs : EventArgs
    {
        public RadioButton? PlaylistElement { get; set; }
        public string PlaylistName { get; set; } = string.Empty;
    }

    public partial class NavigationSidebar : UserControl
    {
        public static readonly RoutedEvent NavSelectionChangedEvent = EventManager.RegisterRoutedEvent(
            "NavSelectionChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(NavigationSidebar));

        public event RoutedEventHandler NavSelectionChanged
        {
            add { AddHandler(NavSelectionChangedEvent, value); }
            remove { RemoveHandler(NavSelectionChangedEvent, value); }
        }

        public event EventHandler<PlaylistRenamedEventArgs>? PlaylistRenamed;
        public event EventHandler<PlaylistDeleteEventArgs>? PlaylistDeleteRequested;

        private int _playlistCount = 1;
        private int _playlistTagCount = 1;
        private double _targetVerticalOffset = 0;
        private bool _isScrolling = false;
        private Action? _cancelCurrentEdit;

        // 用于记录当前库下新建或改名的歌单，防止操作期间被误隐藏
        private HashSet<string> _sessionActivePlaylists = new HashSet<string>();

        public NavigationSidebar()
        {
            InitializeComponent();

            this.Loaded += (s, e) =>
            {
                // 核心改动：订阅刷新事件
                LibraryManager.LibraryUpdated += OnLibraryUpdated;

                // 初始同步
                AutoSyncPlaylists();
                SyncPlaylistsVisibility();
            };

            this.Unloaded += (s, e) =>
            {
                LibraryManager.LibraryUpdated -= OnLibraryUpdated;
            };
        }

        // ==========================================
        // 【特殊清理逻辑】：当库更新（换库/刷新）时触发
        // ==========================================
        private void OnLibraryUpdated()
        {
            Dispatcher.Invoke(() =>
            {
                // 1. 清空当前会话的活跃记录。
                // 这样那些没加歌的“空歌单”在换库或刷新后，就失去了“保命符”
                _sessionActivePlaylists.Clear();

                // 2. 执行标准的同步与可见性检查
                AutoSyncPlaylists();
                SyncPlaylistsVisibility();
            });
        }

        private void AutoSyncPlaylists()
        {
            var activeNamesInCurrentLibrary = LibraryManager.GetUniquePlaylistNames();
            var currentUiNames = GetAllPlaylistNames();

            foreach (var name in activeNamesInCurrentLibrary)
            {
                if (!currentUiNames.Contains(name))
                {
                    AddPlaylistExternally(name);
                }
            }
        }

        // ==========================================
        // 【核心改进】：实现空歌单的物理删除而非单纯隐藏
        // ==========================================
        private void SyncPlaylistsVisibility()
        {
            var activeNamesInLog = LibraryManager.GetUniquePlaylistNames();

            // 使用临时列表记录需要移除的元素，避免在遍历 Children 集合时直接修改集合导致异常
            List<RadioButton> toRemove = new List<RadioButton>();

            foreach (UIElement child in PlaylistsContainer.Children)
            {
                if (child is RadioButton rb && rb.Content is Grid grid && grid.Children[0] is TextBlock tb)
                {
                    string pName = tb.Text;

                    // 判定是否应当保留：
                    // 1. Log 里真实存在的（有歌的）
                    // 2. 或是用户当前正在选中的（即使为空，正在操作中也不应消失）
                    // 3. 或是本次刷新前新建/改名且还在保命期内的
                    bool shouldKeep = activeNamesInLog.Contains(pName) ||
                                     rb.IsChecked == true ||
                                     _sessionActivePlaylists.Contains(pName);

                    if (!shouldKeep)
                    {
                        toRemove.Add(rb);
                    }
                }
            }

            // 物理删除不符合条件的歌单控件
            foreach (var rb in toRemove)
            {
                PlaylistsContainer.Children.Remove(rb);
            }

            RefreshPlaylistCounter();
        }

        public void AddPlaylistExternally(string name, string? tag = null)
        {
            RadioButton newPlaylist = new RadioButton
            {
                Style = (Style)FindResource("NavButtonStyle"),
                Tag = tag ?? "Playlist_" + _playlistTagCount,
                GroupName = "MainNav",
                Opacity = 0,
                RenderTransform = new TranslateTransform(-8, 0)
            };

            Grid container = new Grid { Margin = new Thickness(15, 0, 5, 0), Background = Brushes.Transparent };
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock displayText = new TextBlock
            {
                Text = name,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            displayText.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) });
            Grid.SetColumn(displayText, 0);

            TextBox editBox = new TextBox { Text = displayText.Text, Visibility = Visibility.Collapsed, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Foreground = (Brush)new BrushConverter().ConvertFrom("#00FFAB"), CaretBrush = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(-2, 0, 0, 0) };
            Grid.SetColumn(editBox, 0);

            StackPanel normalActions = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            Button editBtn = CreateActionBtn("M 2,12 L 2,14 L 4,14 L 12,6 L 10,4 Z M 11,5 L 13,3 L 11,1 L 9,3 Z", "#888888");
            Button deleteBtn = CreateActionBtn("M 2,3 L 12,3 M 5,1 L 9,1 M 4,3 L 4,12 L 10,12 L 10,3", "#888888");
            normalActions.Children.Add(editBtn);
            normalActions.Children.Add(deleteBtn);
            Grid.SetColumn(normalActions, 1);

            StackPanel editActions = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            Button confirmBtn = CreateActionBtn("M 2,7 L 5,10 L 11,3", "#00FFAB");
            Button cancelBtn = CreateActionBtn("M 3,3 L 11,11 M 11,3 L 3,11", "#888888");
            editActions.Children.Add(confirmBtn);
            editActions.Children.Add(cancelBtn);
            Grid.SetColumn(editActions, 1);

            container.Children.Add(displayText);
            container.Children.Add(editBox);
            container.Children.Add(normalActions);
            container.Children.Add(editActions);
            newPlaylist.Content = container;

            // 动画效果设置
            DoubleAnimation fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200));
            DoubleAnimation fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));

            void ShowNormalActions(bool animated = true)
            {
                if (editActions.Visibility != Visibility.Collapsed) return;

                normalActions.BeginAnimation(UIElement.OpacityProperty, null);
                normalActions.Visibility = Visibility.Visible;
                normalActions.IsHitTestVisible = true;

                if (animated)
                {
                    normalActions.Opacity = 0;
                    normalActions.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
                else
                {
                    normalActions.Opacity = 1;
                }
            }

            void HideNormalActions()
            {
                normalActions.BeginAnimation(UIElement.OpacityProperty, null);
                normalActions.Opacity = 0;
                normalActions.IsHitTestVisible = false;
                normalActions.Visibility = Visibility.Collapsed;
            }

            void ShowEditActions()
            {
                editActions.BeginAnimation(UIElement.OpacityProperty, null);
                editActions.Visibility = Visibility.Visible;
                editActions.IsHitTestVisible = true;
                editActions.Opacity = 1;
            }

            void HideEditActions()
            {
                editActions.BeginAnimation(UIElement.OpacityProperty, null);
                editActions.Opacity = 0;
                editActions.IsHitTestVisible = false;
                editActions.Visibility = Visibility.Collapsed;
            }

            newPlaylist.MouseEnter += (s, ev) =>
            {
                if (editActions.Visibility == Visibility.Collapsed)
                    ShowNormalActions();
            };

            newPlaylist.MouseLeave += (s, ev) =>
            {
                if (editActions.Visibility != Visibility.Collapsed) return;

                normalActions.BeginAnimation(UIElement.OpacityProperty, null);
                normalActions.Visibility = Visibility.Visible;
                normalActions.IsHitTestVisible = false;

                fadeOut.Completed -= OnNormalActionsFadeOutCompleted;
                fadeOut.Completed += OnNormalActionsFadeOutCompleted;
                normalActions.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };

            void OnNormalActionsFadeOutCompleted(object? sender, EventArgs e)
            {
                fadeOut.Completed -= OnNormalActionsFadeOutCompleted;
                if (!newPlaylist.IsMouseOver && editActions.Visibility == Visibility.Collapsed)
                {
                    HideNormalActions();
                }
            }

            // 联动操作代理
            Action cancelEdit = () =>
            {
                editBox.Visibility = Visibility.Collapsed;
                HideEditActions();
                displayText.Visibility = Visibility.Visible;

                if (newPlaylist.IsMouseOver) ShowNormalActions(false);
                else HideNormalActions();

                _cancelCurrentEdit = null;
            };

            Action commitEdit = () =>
            {
                string oldName = displayText.Text;
                string newName = editBox.Text.Trim();
                if (!string.IsNullOrWhiteSpace(newName) && oldName != newName)
                {
                    displayText.Text = newName;
                    _sessionActivePlaylists.Add(newName);
                    PlaylistRenamed?.Invoke(this, new PlaylistRenamedEventArgs { OldName = oldName, NewName = newName });
                }
                cancelEdit();
            };

            editBtn.Click += (s, ev) =>
            {
                _cancelCurrentEdit?.Invoke();
                _cancelCurrentEdit = cancelEdit;

                displayText.Visibility = Visibility.Collapsed;
                HideNormalActions();
                editBox.Visibility = Visibility.Visible;
                ShowEditActions();
                editBox.Text = displayText.Text;
                editBox.Focus();
                editBox.SelectAll();
            };

            editBox.LostFocus += (s, ev) => { Dispatcher.BeginInvoke(new Action(() => { if (editBox.Visibility == Visibility.Visible) cancelEdit(); }), System.Windows.Threading.DispatcherPriority.Input); };
            editBox.KeyDown += (s, ev) => { if (ev.Key == Key.Enter) { commitEdit(); ev.Handled = true; } else if (ev.Key == Key.Escape) { cancelEdit(); ev.Handled = true; } };
            confirmBtn.PreviewMouseDown += (s, ev) => { commitEdit(); ev.Handled = true; };
            cancelBtn.PreviewMouseDown += (s, ev) => { cancelEdit(); ev.Handled = true; };
            deleteBtn.Click += (s, ev) => { PlaylistDeleteRequested?.Invoke(this, new PlaylistDeleteEventArgs { PlaylistElement = newPlaylist, PlaylistName = displayText.Text }); };

            newPlaylist.Checked += NavButton_Checked;
            newPlaylist.AllowDrop = true;
            newPlaylist.DragEnter += (s, ev) => { if (ev.Data.GetDataPresent("OscillaTrack")) newPlaylist.Background = new SolidColorBrush(Color.FromRgb(26, 26, 26)); };
            newPlaylist.DragLeave += (s, ev) => { newPlaylist.Background = Brushes.Transparent; };
            newPlaylist.Drop += PlaylistButton_Drop;

            PlaylistsContainer.Children.Add(newPlaylist);
            _playlistTagCount++;
            RefreshPlaylistCounter();

            var slideIn = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            newPlaylist.RenderTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);
            newPlaylist.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
        }

        private void PlaylistButton_Drop(object sender, DragEventArgs e)
        {
            if (sender is RadioButton rb)
            {
                rb.Background = Brushes.Transparent;
                if (e.Data.GetDataPresent("OscillaTrack"))
                {
                    Track? droppedTrack = e.Data.GetData("OscillaTrack") as Track;
                    if (rb.Content is Grid grid && grid.Children[0] is TextBlock tb)
                    {
                        string playlistName = tb.Text;
                        if (string.Equals(playlistName, "All Songs", StringComparison.OrdinalIgnoreCase)) return;
                        if (droppedTrack != null && !string.IsNullOrEmpty(playlistName))
                        {
                            Logger.LogPlaylistAction(droppedTrack, playlistName);

                            // 这里刷新会触发 OnLibraryUpdated，进而执行物理清理
                            LibraryManager.RefreshLibrary();

                            DoubleAnimation flashAnim = new DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(400));
                            tb.BeginAnimation(UIElement.OpacityProperty, flashAnim);
                        }
                    }
                }
            }
        }

        public List<string> GetAllPlaylistNames()
        {
            List<string> names = new List<string>();
            foreach (UIElement child in PlaylistsContainer.Children)
                if (child is RadioButton rb && rb.Content is Grid grid && grid.Children[0] is TextBlock tb) names.Add(tb.Text);
            return names;
        }

        public void RemovePlaylist(RadioButton playlist)
        {
            string? playlistName = GetPlaylistName(playlist);
            if (!string.IsNullOrEmpty(playlistName))
            {
                _sessionActivePlaylists.Remove(playlistName);
            }

            PlaylistsContainer.Children.Remove(playlist);
            RefreshPlaylistCounter();
        }

        private string? GetPlaylistName(RadioButton playlist)
        {
            if (playlist.Content is Grid grid && grid.Children.Count > 0 && grid.Children[0] is TextBlock tb)
                return tb.Text;

            return null;
        }

        private void RefreshPlaylistCounter()
        {
            var existingNames = new HashSet<string>(GetAllPlaylistNames(), StringComparer.OrdinalIgnoreCase);
            _playlistCount = 1;

            while (existingNames.Contains("New Playlist " + _playlistCount))
            {
                _playlistCount++;
            }
        }

        private void NavButton_Checked(object sender, RoutedEventArgs e)
        {
            var btn = sender as RadioButton;
            if (btn == null) return;
            string? tag = btn.Tag?.ToString();
            if (tag != "Visualizer" && tag != "Equalizer")
                if (AudioExpander.IsChecked == true) AudioExpander.IsChecked = false;

            SyncPlaylistsVisibility();
            RaiseEvent(new RoutedEventArgs(NavSelectionChangedEvent, sender));
        }

        private void AudioExpander_Checked(object sender, RoutedEventArgs e)
        {
            if (AudioSubMenuScale == null) return;
            DoubleAnimation anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(250)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } };
            AudioSubMenuScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        private void AudioExpander_Unchecked(object sender, RoutedEventArgs e)
        {
            if (AudioSubMenuScale == null) return;
            DoubleAnimation anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(250)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } };
            AudioSubMenuScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        private void AddPlaylist_Click(object sender, RoutedEventArgs e)
        {
            string name = "New Playlist " + _playlistCount;
            _sessionActivePlaylists.Add(name);
            AddPlaylistExternally(name);
            if (PlaylistsContainer.Children.Count > 0)
                ((RadioButton)PlaylistsContainer.Children[PlaylistsContainer.Children.Count - 1]).IsChecked = true;
        }

        private Button CreateActionBtn(string pathData, string colorHex)
        {
            Button btn = new Button { Style = (Style)FindResource("ActionBtnStyle") };
            System.Windows.Shapes.Path icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(pathData), Stroke = (Brush)new BrushConverter().ConvertFrom(colorHex), StrokeThickness = 1.5, Stretch = Stretch.Uniform, Width = 12, Height = 12 };
            btn.Content = icon;
            return btn;
        }

        private void NavScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true; _targetVerticalOffset -= e.Delta * 0.5;
            if (_targetVerticalOffset < 0) _targetVerticalOffset = 0;
            if (_targetVerticalOffset > NavScrollViewer.ScrollableHeight) _targetVerticalOffset = NavScrollViewer.ScrollableHeight;
            if (!_isScrolling) { _isScrolling = true; CompositionTarget.Rendering += SmoothScroll_Rendering; }
        }

        private void SmoothScroll_Rendering(object? sender, EventArgs e)
        {
            double currentOffset = NavScrollViewer.VerticalOffset; double delta = _targetVerticalOffset - currentOffset;
            if (Math.Abs(delta) < 0.5) { NavScrollViewer.ScrollToVerticalOffset(_targetVerticalOffset); CompositionTarget.Rendering -= SmoothScroll_Rendering; _isScrolling = false; }
            else NavScrollViewer.ScrollToVerticalOffset(currentOffset + delta * 0.15);
        }
    }
}
