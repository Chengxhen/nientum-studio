using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NientumStudio.Services;

namespace NientumStudio.Dialogs
{
    public class GameAssetsWindow : Window
    {
        public Action<string> OnUseAsCover { get; set; }
        public Action<string> OnUseAsBackground { get; set; }

        private readonly WrapPanel _grid = new WrapPanel { Margin = new Thickness(8) };
        private string _cat = "covers";

        public GameAssetsWindow()
        {
            Title = "游戏解包素材库 (Nientum - Op.ZERO)";
            Width = 860;
            Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BgDeep");
            SetResourceReference(ForegroundProperty, "TextPrimary");

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 6) };
            AddTab(tabs, "官方曲绘封面", "covers");
            AddTab(tabs, "场景与背景", "backgrounds");
            AddTab(tabs, "角色立绘", "characters");
            AddTab(tabs, "音符与判定贴图", "notes");
            AddTab(tabs, "UI 与装饰", "ui");
            root.Children.Add(tabs);

            var scroll = new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            Content = root;
            LoadCategory("covers");
        }

        private void AddTab(StackPanel parent, string title, string cat)
        {
            var btn = new Button { Content = title, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 5, 10, 5) };
            btn.Click += (_, _) => LoadCategory(cat);
            parent.Children.Add(btn);
        }

        private void LoadCategory(string cat)
        {
            _cat = cat;
            _grid.Children.Clear();
            if (!Directory.Exists(ChartStorage.GameSpritesDir)) return;

            var files = Directory.GetFiles(ChartStorage.GameSpritesDir, "*.png")
                .Where(p =>
                {
                    string low = Path.GetFileName(p).ToLowerInvariant();
                    return cat switch
                    {
                        "covers" => low.StartsWith("trackcover_"),
                        "backgrounds" => low.Contains("bg") || low.Contains("background"),
                        "characters" => low.Contains("liora") || low.Contains("aleph"),
                        "notes" => low.StartsWith("pzimgplay") || low.StartsWith("pzimgnote"),
                        "ui" => low.Contains("frame") || low.Contains("butterfly") || low.Contains("capsule") || low.Contains("memo") || low.Contains("crystal") || low.Contains("icon_") || low.Contains("playlist"),
                        _ => true
                    };
                })
                .OrderBy(p => p)
                .Take(120);

            foreach (string path in files)
            {
                _grid.Children.Add(BuildCard(path));
            }
        }

        private Border BuildCard(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var card = new Border
            {
                Width = 148,
                Margin = new Thickness(5),
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1)
            };
            card.SetResourceReference(Border.BackgroundProperty, "BgCard");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            var sp = new StackPanel();
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.DecodePixelWidth = 140;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                sp.Children.Add(new Image { Source = bmp, Height = 92, Stretch = Stretch.Uniform });
            }
            catch { }

            var nameLbl = new TextBlock
            {
                Text = name,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 4),
                ToolTip = name
            };
            nameLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            sp.Children.Add(nameLbl);

            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            var useCover = new Button { Content = "设为封面", FontSize = 10, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0) };
            useCover.Click += (_, _) => OnUseAsCover?.Invoke(path);
            btns.Children.Add(useCover);

            if (_cat == "backgrounds")
            {
                var useBg = new Button { Content = "预览背景", FontSize = 10, Padding = new Thickness(6, 2, 6, 2) };
                useBg.Click += (_, _) => OnUseAsBackground?.Invoke(path);
                btns.Children.Add(useBg);
            }
            sp.Children.Add(btns);
            card.Child = sp;
            return card;
        }
    }
}
