using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NientumStudio.Services;

namespace NientumStudio.Dialogs
{
    public class SettingsWindow : Window
    {
        public Action Applied;

        private readonly ContentControl _pageHost = new ContentControl { Margin = new Thickness(24, 18, 24, 18) };
        private readonly ScrollViewer _scroll;
        private readonly StackPanel[] _pages = new StackPanel[7];
        private readonly ListBox _nav;

        private Button _bDark, _bLight, _bGame;
        private ComboBox _cmbSnap, _cmbGrid, _cmbRate;
        private ComboBox _cmbGpu, _cmbScale, _cmbFps, _cmbQuality, _cmbLead;
        private Slider _sldMaster, _sldHit, _sldMetro;
        private CheckBox _chkHitsound, _chkMetronome, _chkFollow, _chkAutoMiddle, _chkWaveform, _chkPanelLibrary, _chkPanelMeta, _chkPanelHealth, _chkPanelInspector;

        public SettingsWindow()
        {
            Title = "设置 · Nientum Studio";
            Width = 860;
            Height = 660;
            MinWidth = 640;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI");

            SetResourceReference(BackgroundProperty, "BgDeep");
            SetResourceReference(ForegroundProperty, "TextPrimary");

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(186) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _nav = new ListBox { Margin = new Thickness(14, 16, 8, 16), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            foreach (string item in new[] { "外观", "声音", "编辑", "试听与播放", "文件位置", "快捷键", "性能" })
                _nav.Items.Add(item);
            Grid.SetColumn(_nav, 0);
            root.Children.Add(_nav);

            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(6, 12, 12, 12)
            };
            border.SetResourceReference(Border.BackgroundProperty, "BgCard");
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            _scroll = new ScrollViewer
            {
                Content = _pageHost,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            border.Child = _scroll;
            Grid.SetColumn(border, 1);
            root.Children.Add(border);

            var bottomBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 12, 18, 14) };
            bottomBorder.SetResourceReference(Border.BackgroundProperty, "BgPanel");
            bottomBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var bottomDock = new DockPanel();
            var tip = new TextBlock { Text = "更改设置后点击「保存设置」即可生效", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            tip.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
            bottomDock.Children.Add(tip);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", Width = 88, IsCancel = true, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 10, 0) };
            cancel.Click += (_, _) => DialogResult = false;
            var save = new Button
            {
                Content = "保存设置",
                Width = 124,
                IsDefault = true,
                Style = (Style)TryFindResource("GoldButton"),
                Padding = new Thickness(10, 6, 10, 6)
            };
            save.Click += (_, _) => SaveAndClose();
            bottom.Children.Add(cancel);
            bottom.Children.Add(save);
            DockPanel.SetDock(bottom, Dock.Right);
            bottomDock.Children.Add(bottom);
            bottomBorder.Child = bottomDock;
            Grid.SetRow(bottomBorder, 1);
            Grid.SetColumnSpan(bottomBorder, 2);
            root.Children.Add(bottomBorder);

            Content = root;

            BuildAllPages();
            _nav.SelectionChanged += (_, _) => ShowPage(_nav.SelectedIndex);
            _nav.SelectedIndex = 0;
            ShowPage(0);
            UpdateThemeButtons();
        }

        private void BuildAllPages()
        {
            var s = EditorSettings.Current;
            for (int i = 0; i < _pages.Length; i++)
                _pages[i] = new StackPanel();

            // 0: 外观
            var p0 = _pages[0];
            AddHeader(p0, "外观");
            AddNote(p0, "主题切换会立即生效，并在下次启动时保持。");
            var themeRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 16) };
            _bDark = MakeThemeButton("深色主题", NientumStudio.Services.ThemeMode.Dark);
            _bLight = MakeThemeButton("浅色主题", NientumStudio.Services.ThemeMode.Light);
            _bGame = MakeThemeButton("游戏剧场 (Op.ZERO)", NientumStudio.Services.ThemeMode.Game);
            themeRow.Children.Add(_bDark);
            themeRow.Children.Add(_bLight);
            themeRow.Children.Add(_bGame);
            p0.Children.Add(themeRow);
            _chkWaveform = AddCheck(p0, "显示音频波形轨道", s.ShowWaveform);
            _chkPanelLibrary = AddCheck(p0, "显示曲目库", s.PanelLibrary);
            _chkPanelMeta = AddCheck(p0, "显示难度与曲目信息", s.PanelMeta);
            _chkPanelHealth = AddCheck(p0, "显示谱面体检", s.PanelHealth);
            _chkPanelInspector = AddCheck(p0, "显示属性检查器", s.PanelInspector);

            // 1: 声音
            var p1 = _pages[1];
            AddHeader(p1, "声音");
            AddNote(p1, "音量只影响编辑器试听，不会修改游戏。");
            _sldMaster = AddSlider(p1, "歌曲音量", s.MasterVolume);
            _sldHit = AddSlider(p1, "打击音音量", s.HitsoundVolume);
            _sldMetro = AddSlider(p1, "节拍器音量", s.MetronomeVolume);

            // 2: 编辑
            var p2 = _pages[2];
            AddHeader(p2, "编辑");
            _cmbSnap = AddCombo(p2, "默认吸附", new (string label, double value)[] { ("1/1", 1), ("1/2", 2), ("1/3", 3), ("1/4", 4), ("1/6", 6), ("1/8", 8), ("1/12", 12), ("1/16", 16), ("1/24", 24), ("1/32", 32), ("自由", 0) }, s.SnapDiv);
            _cmbGrid = AddCombo(p2, "预览分格密度", new (string label, double value)[] { ("关闭", 0), ("1/1 拍格", 1), ("1/2 拍格", 2), ("1/3 拍格", 3), ("1/4 拍格", 4), ("1/6 拍格", 6), ("1/8 拍格", 8), ("1/16 拍格", 16), ("1/24 拍格", 24), ("1/32 拍格", 32) }, s.PreviewGridDiv);
            _chkAutoMiddle = AddCheck(p2, "新建长条时自动补全中段", s.AutoMiddle);
            AddNote(p2, "中段节点是长条的途中判定点，可随时在检查器里补全或清除。");

            // 3: 试听与播放
            var p3 = _pages[3];
            AddHeader(p3, "试听与播放");
            _cmbRate = AddCombo(p3, "默认播放倍速", new (string label, double value)[] { ("0.5x", 0.5), ("0.75x", 0.75), ("1.0x", 1.0), ("1.25x", 1.25) }, s.DefaultRate);
            _chkHitsound = AddCheck(p3, "播放时播放打击音", s.Hitsound);
            _chkMetronome = AddCheck(p3, "播放节拍器", s.Metronome);
            _chkFollow = AddCheck(p3, "时间轴跟随播放指针", s.FollowPlayhead);

            // 4: 文件位置
            var p4 = _pages[4];
            AddHeader(p4, "文件位置");
            AddPathRow(p4, "自定义谱面目录", ChartStorage.CustomChartsDir);
            AddGameDirPicker(p4);
            AddPathRow(p4, "游戏 CustomCharts", ChartStorage.GameCustomChartsDir);
            AddPathRow(p4, "官方曲目工作区", OfficialLibrary.WorkspaceDir);
            AddPathRow(p4, "官方谱面解包目录", OfficialLibrary.ChartsDir);
            AddNote(p4, "官方曲目副本只保存在工作区，不会写入游戏。");

            // 6: 性能
            var p6 = _pages[6];
            AddHeader(p6, "性能");
            AddNote(p6, "以下选项只影响编辑器界面；游戏画质与游戏内滚动速度不受影响。");
            _cmbGpu = AddCombo(p6, "渲染引擎", new (string label, double value)[] {
                ("GPU 加速（推荐）", 1), ("软件渲染（重启生效）", 0) }, s.GpuAcceleration ? 1 : 0);
            _cmbScale = AddCombo(p6, "预览分辨率", new (string label, double value)[] {
                ("100%（最清晰）", 1.0), ("75%", 0.75), ("50%（最流畅）", 0.5) }, s.PreviewScale);
            _cmbLead = AddCombo(p6, "打击音提前量（毫秒，补偿音频输出延迟）", new (string label, double value)[] {
                ("0 ms", 0), ("10 ms", 10), ("20 ms", 20), ("30 ms", 30), ("40 ms", 40), ("60 ms", 60), ("80 ms", 80) }, s.HitLeadMs);
            _cmbFps = AddCombo(p6, "最大帧率", new (string label, double value)[] {
                ("60 FPS", 60), ("120 FPS", 120), ("144 FPS", 144), ("165 FPS", 165), ("不限（跟随显示器）", 0) }, s.MaxFps);
            _cmbQuality = AddCombo(p6, "渲染质量", new (string label, double value)[] {
                ("高质量（推荐）", 1), ("性能优先", 0) }, s.RenderQuality == "performance" ? 0 : 1);
            int tier = RenderCapability.Tier >> 16;
            AddNote(p6, $"本机：CPU {Environment.ProcessorCount} 线程 · 硬件加速 Tier {tier} · 当前渲染模式 {RenderOptions.ProcessRenderMode}");
            var recommend = new Button
            {
                Content = "按本机配置推荐",
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 4)
            };
            recommend.Click += (_, _) => ApplyHardwareRecommendation();
            p6.Children.Add(recommend);
            AddNote(p6, "修改渲染引擎需重启编辑器；其余选项保存后立即生效。双显卡笔记本若仍觉得卡，可在 NVIDIA 控制面板把 NientumStudio.exe 指定为高性能 GPU。");

            // 5: 快捷键
            var p5 = _pages[5];
            AddHeader(p5, "快捷键");
            foreach (var (key, desc) in new[]
            {
                ("空格", "播放 / 暂停"),
                ("Home", "回到起点"),
                ("1 – 8", "切换工具"),
                ("J / K", "实时打谱（地面 / 空中）"),
                ("F", "翻转选中轨"),
                ("Ctrl+Z / Ctrl+Y", "撤销 / 重做"),
                ("Ctrl+C / Ctrl+V", "复制 / 粘贴"),
                ("Ctrl+S", "保存"),
                ("Delete", "删除选中"),
                ("← / →", "按吸附步进微调（Shift 一小节）"),
                ("↑ / ↓", "切换选中音符轨道"),
                ("[ / ]", "设置 A / B 循环点"),
                ("L", "开 / 关 A-B 循环"),
                ("Ctrl+滚轮", "时间轴缩放（预览同步）"),
                ("右键对象", "复制 / 删除 / 翻转 / 补中段"),
            })
                AddShortcutRow(p5, key, desc);
        }

        private Button MakeThemeButton(string label, NientumStudio.Services.ThemeMode mode)
        {
            var btn = new Button
            {
                Content = label,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 6),
                Tag = mode
            };
            btn.Click += (_, _) =>
            {
                ThemeManager.Apply(mode);
                UpdateThemeButtons();
            };
            return btn;
        }

        private void UpdateThemeButtons()
        {
            foreach (var btn in new[] { _bDark, _bLight, _bGame })
            {
                if (btn?.Tag is NientumStudio.Services.ThemeMode m)
                {
                    bool active = ThemeManager.Current == m;
                    btn.SetResourceReference(BorderBrushProperty, active ? "AccentGold" : "ButtonBorder");
                    btn.SetResourceReference(BackgroundProperty, active ? "ToolCheckedBg" : "ButtonBg");
                    btn.SetResourceReference(ForegroundProperty, active ? "ToolCheckedForeground" : "ButtonText");
                }
            }
        }

        private void ShowPage(int index)
        {
            if (index < 0 || index >= _pages.Length) return;
            _pageHost.Content = _pages[index];
            _scroll?.ScrollToTop();
        }

        private static void AddHeader(Panel host, string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            host.Children.Add(tb);
        }

        private static void AddNote(Panel host, string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
            host.Children.Add(tb);
        }

        private static CheckBox AddCheck(Panel host, string text, bool value)
        {
            var cb = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 7, 0, 7) };
            host.Children.Add(cb);
            return cb;
        }

        private static Slider AddSlider(Panel host, string label, double value)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
            var head = new DockPanel();
            var t = new TextBlock { Text = label, FontSize = 11 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            var valText = new TextBlock { Text = $"{(int)(value * 100)}", FontSize = 11 };
            valText.SetResourceReference(TextBlock.ForegroundProperty, "AccentGold");
            DockPanel.SetDock(valText, Dock.Right);
            head.Children.Add(t);
            head.Children.Add(valText);
            sp.Children.Add(head);
            var sld = new Slider { Minimum = 0, Maximum = 100, Value = value * 100, Margin = new Thickness(0, 8, 0, 0) };
            sld.ValueChanged += (_, _) => valText.Text = ((int)sld.Value).ToString();
            sp.Children.Add(sld);
            host.Children.Add(sp);
            return sld;
        }

        private static ComboBox AddCombo(Panel host, string label, (string label, double value)[] options, double current)
        {
            var lbl = new TextBlock { Text = label, FontSize = 11.5, Margin = new Thickness(0, 10, 0, 4) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            host.Children.Add(lbl);
            var cb = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
            int sel = 0;
            for (int i = 0; i < options.Length; i++)
            {
                cb.Items.Add(new ComboBoxItem { Content = options[i].label, Tag = options[i].value });
                if (Math.Abs(options[i].value - current) < 0.001) sel = i;
            }
            cb.SelectedIndex = sel;
            host.Children.Add(cb);
            return cb;
        }

        private static void AddPathRow(Panel host, string label, string path)
        {
            var lbl = new TextBlock { Text = label, FontSize = 11.5, Margin = new Thickness(0, 10, 0, 4) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            host.Children.Add(lbl);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var box = new TextBox { Text = path, IsReadOnly = true, FontSize = 10.5 };
            var btn = new Button { Content = "打开", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0) };
            btn.Click += (_, _) =>
            {
                try
                {
                    if (System.IO.Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
                }
                catch { }
            };
            DockPanel.SetDock(btn, Dock.Right);
            row.Children.Add(btn);
            row.Children.Add(box);
            host.Children.Add(row);
        }

        private static void AddShortcutRow(Panel host, string key, string desc)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var k = new TextBlock { Text = key, FontFamily = new FontFamily("Consolas"), FontSize = 11, Width = 116 };
            k.SetResourceReference(TextBlock.ForegroundProperty, "AccentGold");
            var d = new TextBlock { Text = desc, FontSize = 11 };
            d.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            row.Children.Add(k);
            row.Children.Add(d);
            host.Children.Add(row);
        }

        private void AddGameDirPicker(StackPanel p)
        {
            var label = new TextBlock
            {
                Text = "游戏目录（包含 Nientum.exe 与 BepInEx）",
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 3)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            p.Children.Add(label);

            var current = new TextBlock
            {
                Text = GameLocator.HasGame ? GameLocator.GameDir : "未检测到，请手动选择",
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            current.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            p.Children.Add(current);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var pick = new Button { Content = "选择游戏文件夹…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            pick.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择 Liora 游戏目录（包含 Nientum.exe）" };
                if (dlg.ShowDialog() == true)
                {
                    if (GameLocator.IsValid(dlg.FolderName))
                    {
                        GameLocator.SetGameDir(dlg.FolderName);
                        current.Text = GameLocator.GameDir;
                    }
                    else
                    {
                        MessageBox.Show(this, "所选文件夹中没有 Nientum.exe，请选择游戏安装目录。", "游戏目录无效");
                    }
                }
            };
            var detect = new Button { Content = "自动检测", Padding = new Thickness(10, 3, 10, 3) };
            detect.Click += (_, _) =>
            {
                GameLocator.Refresh();
                current.Text = GameLocator.HasGame ? GameLocator.GameDir : "未检测到，请手动选择";
            };
            row.Children.Add(pick);
            row.Children.Add(detect);
            p.Children.Add(row);
            AddNote(p, "同步到游戏会把谱面写入游戏目录下的 CustomCharts 子目录，Mod 也从这里读取。");
        }

        private void ApplyHardwareRecommendation()
        {
            int tier = RenderCapability.Tier >> 16;
            int cores = Environment.ProcessorCount;
            if (tier >= 2 && cores >= 8)
            {
                if (_cmbGpu != null) _cmbGpu.SelectedIndex = 0;
                if (_cmbScale != null) _cmbScale.SelectedIndex = 0;
                if (_cmbFps != null) _cmbFps.SelectedIndex = 3;
                if (_cmbQuality != null) _cmbQuality.SelectedIndex = 0;
            }
            else if (tier >= 1)
            {
                if (_cmbGpu != null) _cmbGpu.SelectedIndex = 0;
                if (_cmbScale != null) _cmbScale.SelectedIndex = 1;
                if (_cmbFps != null) _cmbFps.SelectedIndex = 2;
                if (_cmbQuality != null) _cmbQuality.SelectedIndex = 0;
            }
            else
            {
                if (_cmbGpu != null) _cmbGpu.SelectedIndex = 1;
                if (_cmbScale != null) _cmbScale.SelectedIndex = 2;
                if (_cmbFps != null) _cmbFps.SelectedIndex = 1;
                if (_cmbQuality != null) _cmbQuality.SelectedIndex = 1;
            }
        }

        private void SaveAndClose()
        {
            var s = EditorSettings.Current;
            bool gpuBefore = s.GpuAcceleration;
            if (_cmbSnap?.SelectedItem is ComboBoxItem si && si.Tag is double sv) s.SnapDiv = (int)sv;
            if (_cmbGrid?.SelectedItem is ComboBoxItem gi && gi.Tag is double gv) s.PreviewGridDiv = (int)gv;
            if (_cmbRate?.SelectedItem is ComboBoxItem ri && ri.Tag is double rv) s.DefaultRate = rv;
            if (_sldMaster != null) s.MasterVolume = _sldMaster.Value / 100.0;
            if (_sldHit != null) s.HitsoundVolume = _sldHit.Value / 100.0;
            if (_sldMetro != null) s.MetronomeVolume = _sldMetro.Value / 100.0;
            if (_chkHitsound != null) s.Hitsound = _chkHitsound.IsChecked == true;
            if (_chkMetronome != null) s.Metronome = _chkMetronome.IsChecked == true;
            if (_chkPanelLibrary != null) s.PanelLibrary = _chkPanelLibrary.IsChecked == true;
            if (_chkPanelMeta != null) s.PanelMeta = _chkPanelMeta.IsChecked == true;
            if (_chkPanelHealth != null) s.PanelHealth = _chkPanelHealth.IsChecked == true;
            if (_chkPanelInspector != null) s.PanelInspector = _chkPanelInspector.IsChecked == true;
            s.PanelLeftVisible = s.PanelLibrary || s.PanelMeta;
            s.PanelRightVisible = s.PanelHealth || s.PanelInspector;
            if (_chkFollow != null) s.FollowPlayhead = _chkFollow.IsChecked == true;
            if (_chkAutoMiddle != null) s.AutoMiddle = _chkAutoMiddle.IsChecked == true;
            if (_chkWaveform != null) s.ShowWaveform = _chkWaveform.IsChecked == true;
            if (_cmbGpu?.SelectedItem is ComboBoxItem gi2 && gi2.Tag is double gv2) s.GpuAcceleration = gv2 > 0.5;
            if (_cmbScale?.SelectedItem is ComboBoxItem si2 && si2.Tag is double sv2) s.PreviewScale = sv2;
            if (_cmbFps?.SelectedItem is ComboBoxItem fi2 && fi2.Tag is double fv2) s.MaxFps = (int)fv2;
            if (_cmbLead?.SelectedItem is ComboBoxItem li && li.Tag is double lv) s.HitLeadMs = (int)lv;
            if (_cmbQuality?.SelectedItem is ComboBoxItem qi2 && qi2.Tag is double qv2) s.RenderQuality = qv2 > 0.5 ? "high" : "performance";
            s.Save();
            if (gpuBefore != s.GpuAcceleration)
                MessageBox.Show(this, "渲染引擎切换将在下次启动编辑器时生效。", "提示");
            Applied?.Invoke();
            DialogResult = true;
        }
    }
}
