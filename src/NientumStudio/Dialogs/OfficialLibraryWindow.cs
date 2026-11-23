using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NientumStudio.Services;

namespace NientumStudio.Dialogs
{
    /// <summary>官方 FreePlay 曲目库：导入到本地工作区（不写游戏）。</summary>
    public class OfficialLibraryWindow : Window
    {
        public int ImportedCount { get; private set; }

        private readonly List<(OfficialTrackInfo info, CheckBox check, Border card)> _rows = new List<(OfficialTrackInfo, CheckBox, Border)>();
        private readonly Button _importButton;
        private readonly TextBlock _summary;
        private readonly TextBox _searchBox;

        private Brush TextPrimary;
        private Brush TextDim;
        private Brush Gold;
        private Brush Pink;
        private Brush Teal;
        private Brush BgDeepBrush;
        private Brush BgCardBrush;
        private Brush BorderBrushRes;
        private Brush BgInputBrush;
        private Brush BorderLightBrush;
        private Brush GoldBgBrush;
        private Brush GoldBorderBrush;
        private Brush GoldTextBrush;

        public OfficialLibraryWindow()
        {
            Title = "官方曲目库 · Nientum Op.ZERO";
            Width = 780;
            Height = 660;
            MinWidth = 620;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            TextPrimary = Brush("TextPrimary");
            TextDim = Brush("TextDim");
            Gold = Brush("AccentGold");
            Pink = Brush("AccentJump");
            Teal = Brush("AccentGround");
            BgDeepBrush = Brush("BgDeep");
            BgCardBrush = Brush("BgCard");
            BorderBrushRes = Brush("BorderBrush");
            BgInputBrush = Brush("BgInput");
            BorderLightBrush = Brush("BorderLight");
            GoldBgBrush = Brush("GoldBg");
            GoldBorderBrush = Brush("GoldBorder");
            GoldTextBrush = Brush("GoldText");

            Background = BgDeepBrush;
            Foreground = TextPrimary;
            FontFamily = new FontFamily("Microsoft YaHei UI");

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 顶部说明 + 全选
            var topStack = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
            var head = new DockPanel();
            var tip = new StackPanel();
            tip.Children.Add(new TextBlock
            {
                Text = "选择要导入的官方曲目",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary
            });
            tip.Children.Add(new TextBlock
            {
                Text = "导入后复制到本地工作区 OfficialPlayground，可自由查看、编辑、试听（含官方音轨）；保存与修改不会写入游戏。",
                FontSize = 10.5,
                Foreground = TextDim,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                Margin = new Thickness(0, 3, 0, 0)
            });
            head.Children.Add(tip);
            var selPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var btnAll = new Button { Content = "全选可见", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            btnAll.Click += (_, _) => SetAll(true);
            var btnNone = new Button { Content = "清除可见", Padding = new Thickness(10, 3, 10, 3) };
            btnNone.Click += (_, _) => SetAll(false);
            selPanel.Children.Add(btnAll);
            selPanel.Children.Add(btnNone);
            DockPanel.SetDock(selPanel, Dock.Right);
            head.Children.Add(selPanel);
            topStack.Children.Add(head);

            _searchBox = new TextBox { ToolTip = "输入曲名或 track.key 筛选" };
            var searchRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            searchRow.Children.Add(new TextBlock { Text = "筛选：", FontSize = 11, Foreground = TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _searchBox.TextChanged += (_, _) => ApplyFilter();
            searchRow.Children.Add(_searchBox);
            topStack.Children.Add(searchRow);
            root.Children.Add(topStack);

            // 曲目列表
            var list = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
            var tracks = OfficialLibrary.ScanTracks();
            foreach (var info in tracks)
                list.Children.Add(BuildRow(info));

            var scroll = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            // 底部
            var bottom = new DockPanel { Margin = new Thickness(16, 8, 16, 14) };
            _summary = new TextBlock { FontSize = 11, Foreground = TextDim, VerticalAlignment = VerticalAlignment.Center };
            bottom.Children.Add(_summary);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var cancel = new Button { Content = "取消", Width = 76, Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += (_, _) => DialogResult = false;
            _importButton = new Button
            {
                Content = "导入选中",
                Width = 120,
                Style = (Style)TryFindResource("GoldButton"),
                Padding = new Thickness(7, 5, 7, 5)
            };
            _importButton.Click += (_, _) => ImportSelected();
            buttons.Children.Add(cancel);
            buttons.Children.Add(_importButton);
            DockPanel.SetDock(buttons, Dock.Right);
            bottom.Children.Add(buttons);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);

            Content = root;
            UpdateSummary();
        }

        private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

        private Border BuildRow(OfficialTrackInfo info)
        {
            var card = new Border
            {
                Background = BgCardBrush,
                BorderBrush = BorderBrushRes,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(4, 3, 4, 3)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var check = new CheckBox { IsChecked = !info.Imported, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
            check.Click += (_, _) => UpdateSummary();
            grid.Children.Add(check);

            var cover = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(6),
                Background = BgInputBrush,
                BorderBrush = BorderLightBrush,
                BorderThickness = new Thickness(1)
            };
            if (!string.IsNullOrEmpty(info.CoverPath) && File.Exists(info.CoverPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(info.CoverPath, UriKind.Absolute);
                    bmp.DecodePixelWidth = 56;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    cover.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                }
                catch { }
            }
            Grid.SetColumn(cover, 1);
            grid.Children.Add(cover);

            var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = info.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextPrimary, TextTrimming = TextTrimming.CharacterEllipsis });
            var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            sub.Children.Add(new TextBlock { Text = $"{info.Bpm:F0} BPM", FontSize = 10.5, Foreground = Teal });
            sub.Children.Add(new TextBlock { Text = "   ", FontSize = 10.5 });
            if (info.HasNormal) sub.Children.Add(new TextBlock { Text = "Normal", FontSize = 10.5, Foreground = Gold, Margin = new Thickness(0, 0, 8, 0) });
            if (info.HasHard) sub.Children.Add(new TextBlock { Text = "Hard", FontSize = 10.5, Foreground = Pink });
            sub.Children.Add(new TextBlock { Text = $"   {info.Key}", FontSize = 9.5, Foreground = TextDim });
            text.Children.Add(sub);
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);

            var badge = new TextBlock
            {
                Text = info.Imported ? "已导入" : "",
                FontSize = 10.5,
                Foreground = Gold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0)
            };
            if (info.Imported) badge.ToolTip = "重新导入会覆盖该曲目工作区里的本地修改";
            Grid.SetColumn(badge, 3);
            grid.Children.Add(badge);

            card.Child = grid;
            _rows.Add((info, check, card));
            return card;
        }

        private void ApplyFilter()
        {
            string q = (_searchBox?.Text ?? "").Trim();
            foreach (var row in _rows)
            {
                bool match = string.IsNullOrEmpty(q)
                    || (row.info.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (row.info.Key?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
                row.card.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateSummary();
        }

        private void SetAll(bool selected)
        {
            foreach (var row in _rows)
                if (row.card.Visibility == Visibility.Visible)
                    row.check.IsChecked = selected;
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int selected = _rows.Count(r => r.check.IsChecked == true);
            int imported = _rows.Count(r => r.info.Imported);
            _summary.Text = $"共 {_rows.Count} 首官方曲目 · 已导入 {imported} 首 · 已选择 {selected} 首";
            _importButton.IsEnabled = selected > 0;
            _importButton.Content = selected > 0 ? $"导入选中（{selected}）" : "导入选中";
        }

        private void ImportSelected()
        {
            int n = 0, failed = 0;
            foreach (var row in _rows.Where(r => r.check.IsChecked == true))
            {
                try
                {
                    OfficialLibrary.ImportTrack(row.info);
                    n++;
                }
                catch { failed++; }
            }
            if (failed > 0)
                MessageBox.Show(this, $"成功导入 {n} 首，有 {failed} 首导入失败。", "官方曲目库", MessageBoxButton.OK, MessageBoxImage.Warning);
            ImportedCount = n;
            DialogResult = true;
        }
    }
}