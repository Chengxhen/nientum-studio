using System;
using System.Diagnostics;
using System.Threading.Tasks;
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
        private readonly StackPanel[] _pages = new StackPanel[8];
        private readonly ListBox _nav;

        private Button _bDark, _bLight, _bGame;
        private ComboBox _cmbSnap, _cmbGrid, _cmbRate;
        private ComboBox _cmbGpu, _cmbScale, _cmbFps, _cmbQuality, _cmbLead;
        private Slider _sldMaster, _sldHit, _sldMetro;
        private CheckBox _chkHitsound, _chkMetronome, _chkFollow, _chkAutoMiddle, _chkWaveform, _chkBeatGridAccent, _chkPanelLibrary, _chkPanelMeta, _chkPanelHealth, _chkPanelInspector;
        private TextBlock _txtUpdateCurrent, _txtUpdateStatus;
        private TextBox _txtUpdateNotes;
        private Button _btnCheckUpdate, _btnUpdateFull, _btnUpdatePatch, _btnUpdatePage;
        private ProgressBar _prgUpdate;
        private UpdateService.UpdateRelease _release;
        private bool _updateBusy;
        private bool _shuttingDown;

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
            foreach (string item in new[] { "外观", "声音", "编辑", "试听与播放", "文件位置", "快捷键", "性能", "更新" })
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
            _chkBeatGridAccent = AddCheck(p2, "启用分格线拍子重音加粗与分色", s.BeatGridAccent);
            p2.Children.Add(BuildBeatGridLegendCard());

            // 3: 试听与播放
            var p3 = _pages[3];
            AddHeader(p3, "试听与播放");
            _cmbRate = AddCombo(p3, "默认播放倍速", new (string label, double value)[] { ("0.1x", 0.1), ("0.2x", 0.2), ("0.25x", 0.25), ("0.3x", 0.3), ("0.4x", 0.4), ("0.5x", 0.5), ("0.75x", 0.75), ("1.0x", 1.0), ("1.25x", 1.25) }, s.DefaultRate);
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

            // 7: 更新
            var p7 = _pages[7];
            AddHeader(p7, "更新");
            AddNote(p7, "通过 GitHub 检查 Nientum 制谱器的新版本。更新只替换程序文件，保留 ui_settings.json、ui_theme.txt、studio_diag.log 与 CustomCharts；游戏目录里已装好的 Mod 不会自动改动，Mod 有变化时需重新运行安装包里的 安装.bat。");

            _txtUpdateCurrent = new TextBlock
            {
                Text = $"当前版本：{UpdateService.CurrentVersionText}    更新仓库：{UpdateService.Owner}/{UpdateService.Repo}",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            _txtUpdateCurrent.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            p7.Children.Add(_txtUpdateCurrent);

            var updateRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            _btnCheckUpdate = new Button
            {
                Content = "检查更新",
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 6),
                Style = (Style)TryFindResource("GoldButton")
            };
            _btnCheckUpdate.Click += async (_, _) => await CheckForUpdatesAsync();
            _btnUpdatePage = new Button { Content = "打开 Release 页面", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 6) };
            _btnUpdatePage.Click += (_, _) => OpenReleasePage();
            updateRow.Children.Add(_btnCheckUpdate);
            updateRow.Children.Add(_btnUpdatePage);
            p7.Children.Add(updateRow);

            _txtUpdateStatus = new TextBlock
            {
                Text = "尚未检查更新。",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            _txtUpdateStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            p7.Children.Add(_txtUpdateStatus);

            _prgUpdate = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 6,
                Margin = new Thickness(0, 0, 0, 12),
                Visibility = Visibility.Collapsed
            };
            p7.Children.Add(_prgUpdate);

            var installRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            _btnUpdateFull = new Button { Content = "下载并安装完整包", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 6), IsEnabled = false };
            _btnUpdateFull.Click += async (_, _) => await InstallUpdateAsync(false);
            _btnUpdatePatch = new Button { Content = "下载并安装增量包", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 6), IsEnabled = false };
            _btnUpdatePatch.Click += async (_, _) => await InstallUpdateAsync(true);
            installRow.Children.Add(_btnUpdateFull);
            installRow.Children.Add(_btnUpdatePatch);
            p7.Children.Add(installRow);

            var notesLabel = new TextBlock { Text = "更新说明", FontSize = 11.5, Margin = new Thickness(0, 4, 0, 4) };
            notesLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            p7.Children.Add(notesLabel);
            _txtUpdateNotes = new TextBox
            {
                Text = "（检查更新后显示）",
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 150,
                FontSize = 11,
                Padding = new Thickness(8, 6, 8, 6)
            };
            _txtUpdateNotes.SetResourceReference(TextBox.BackgroundProperty, "BgDeep");
            _txtUpdateNotes.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary");
            _txtUpdateNotes.SetResourceReference(TextBox.BorderBrushProperty, "BorderBrush");
            p7.Children.Add(_txtUpdateNotes);

            AddNote(p7, "增量包只适用于上一版本：当前版本必须与「上一版 tag」一致才会显示增量按钮，否则请使用完整包。下载完成后编辑器会关闭，替换成功会自动重启；替换前的文件备份在安装根目录的 _update_backup_* 文件夹，失败会自动回滚。");
        }

        private async Task CheckForUpdatesAsync()
        {
            if (_updateBusy) return;
            _updateBusy = true;
            _btnCheckUpdate.IsEnabled = false;
            _btnUpdateFull.IsEnabled = false;
            _btnUpdatePatch.IsEnabled = false;
            _prgUpdate.Visibility = Visibility.Visible;
            _prgUpdate.IsIndeterminate = true;
            _txtUpdateStatus.Text = "正在连接 GitHub 检查更新…";
            _txtUpdateNotes.Text = "（检查更新后显示）";
            try
            {
                var release = await UpdateService.CheckAsync();
                _release = release;
                _txtUpdateCurrent.Text = $"当前版本：{UpdateService.CurrentVersionText}    更新仓库：{UpdateService.Owner}/{UpdateService.Repo}";
                string date = release.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";
                if (!release.IsNewer)
                {
                    _txtUpdateStatus.Text = $"已是最新版本（{release.Tag}，发布于 {date}）。";
                    _btnUpdateFull.IsEnabled = false;
                    _btnUpdatePatch.IsEnabled = false;
                }
                else
                {
                    string patchState;
                    if (release.Patch == null) patchState = "本次发布没有增量包，请用完整包。";
                    else if (!release.CanUsePatch) patchState = "当前版本与上一版不匹配，增量包不可用，请用完整包。";
                    else patchState = "可用增量更新，体积远小于完整包。";
                    _txtUpdateStatus.Text = $"发现新版本 {release.Tag}（发布于 {date}）。{patchState}";
                    _btnUpdateFull.IsEnabled = release.Full != null;
                    _btnUpdatePatch.IsEnabled = release.CanUsePatch;
                }
                _txtUpdateNotes.Text = string.IsNullOrWhiteSpace(release.Body) ? "（该 Release 没有填写说明）" : release.Body.Replace("\r\n", "\n");
                AppLog.Info($"[Update] 检查完成 latest={release.Tag}, newer={release.IsNewer}, patch={release.CanUsePatch}");
            }
            catch (Exception ex)
            {
                _release = null;
                _txtUpdateStatus.Text = "检查失败：" + ex.Message;
                AppLog.Error("[Update] 检查更新失败", ex);
            }
            finally
            {
                _prgUpdate.IsIndeterminate = false;
                _prgUpdate.Visibility = Visibility.Collapsed;
                _btnCheckUpdate.IsEnabled = true;
                _updateBusy = false;
            }
        }

        private async Task InstallUpdateAsync(bool usePatch)
        {
            if (_updateBusy || _release == null) return;
            var asset = usePatch ? _release.Patch : _release.Full;
            if (asset == null)
            {
                MessageBox.Show(this, "该版本没有这种更新包，请改选另一种。", "更新");
                return;
            }
            double sizeMb = asset.Size / 1048576.0;
            string tip = usePatch
                ? $"将下载增量包 {asset.Name}（{sizeMb:F1} MB）。\n\n下载完成后编辑器会关闭，替换成功会自动重启；用户设置、主题、日志与 CustomCharts 会保留。\n\n继续吗？"
                : $"将下载完整包 {asset.Name}（{sizeMb:F0} MB）。\n\n完整包会覆盖安装目录里的编辑器、Mod 与安装脚本，但不会改动游戏目录。更新后如需同步游戏内 Mod，请重新运行安装包里的 安装.bat。\n\n继续吗？";
            if (MessageBox.Show(this, tip, usePatch ? "增量更新" : "完整更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _updateBusy = true;
            _btnCheckUpdate.IsEnabled = false;
            _btnUpdateFull.IsEnabled = false;
            _btnUpdatePatch.IsEnabled = false;
            _prgUpdate.IsIndeterminate = false;
            _prgUpdate.Visibility = Visibility.Visible;
            _prgUpdate.Value = 0;
            _txtUpdateStatus.Text = "准备下载…";
            try
            {
                var progress = new Progress<(long received, long total)>(p =>
                {
                    if (p.total > 0)
                    {
                        _prgUpdate.Value = Math.Min(100, p.received * 100.0 / p.total);
                        _txtUpdateStatus.Text = $"下载中 {p.received / 1048576.0:F1} / {p.total / 1048576.0:F1} MB（{_prgUpdate.Value:F0}%）…";
                    }
                    else
                    {
                        _txtUpdateStatus.Text = $"下载中 {p.received / 1048576.0:F1} MB…";
                    }
                });
                string archive = await UpdateService.DownloadAsync(_release, asset, usePatch, progress);
                _txtUpdateStatus.Text = "下载完成，正在校验并解压…";
                _prgUpdate.IsIndeterminate = true;
                string source = await Task.Run(() => UpdateService.PrepareSource(archive, usePatch,
                    new Progress<string>(m => _txtUpdateStatus.Text = m)));
                _prgUpdate.IsIndeterminate = false;
                _prgUpdate.Value = 100;

                if (MessageBox.Show(this,
                        $"更新已准备好。\n\n关闭编辑器后将替换程序文件并自动重启；替换前的文件会备份在安装根目录的 _update_backup_* 下。\n\n现在继续吗？",
                        "应用更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    _txtUpdateStatus.Text = "更新包已下载。再次点击对应按钮即可继续（替换需要关闭编辑器）。";
                    return;
                }

                UpdateService.BeginApply(source, usePatch);
                _shuttingDown = true;
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                _txtUpdateStatus.Text = "更新失败：" + ex.Message;
                AppLog.Error("[Update] 下载或准备更新失败", ex);
                MessageBox.Show(this, "更新失败：" + ex.Message, "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (!_shuttingDown)
                {
                    _prgUpdate.IsIndeterminate = false;
                    _prgUpdate.Visibility = Visibility.Collapsed;
                    _btnCheckUpdate.IsEnabled = true;
                    _btnUpdateFull.IsEnabled = _release?.IsNewer == true && _release?.Full != null;
                    _btnUpdatePatch.IsEnabled = _release?.CanUsePatch == true;
                    _updateBusy = false;
                }
            }
        }

        private void OpenReleasePage()
        {
            try
            {
                string url = _release?.PageUrl ?? UpdateService.RepoUrl + "/releases";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
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

        private static UIElement BuildBeatGridLegendCard()
        {
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 2, 0, 12)
            };
            card.SetResourceReference(Border.BackgroundProperty, "BgDeep");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            var sp = new StackPanel();
            var title = new TextBlock
            {
                Text = "分格线颜色与粗细图例（自动跟随曲目默认拍号或分段 BPM 事件的拍号 1–16）：",
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            sp.Children.Add(title);

            bool isLight = ThemeManager.IsLight;
            foreach (var (kind, name, desc) in BeatGridStyle.LegendEntries)
            {
                var (col, thick) = BeatGridStyle.GetAccentPenSpec(kind, isLight, false);
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var lineSample = new Border
                {
                    Width = 28,
                    Height = Math.Max(1.0, thick),
                    Background = new SolidColorBrush(col),
                    CornerRadius = new CornerRadius(0.5),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                Grid.SetColumn(lineSample, 0);
                row.Children.Add(lineSample);

                var lblName = new TextBlock
                {
                    Text = $"{name} ({thick:F1}px)",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                lblName.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                Grid.SetColumn(lblName, 1);
                row.Children.Add(lblName);

                var lblDesc = new TextBlock
                {
                    Text = desc,
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                lblDesc.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
                Grid.SetColumn(lblDesc, 2);
                row.Children.Add(lblDesc);

                sp.Children.Add(row);
            }

            card.Child = sp;
            return card;
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

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_updateBusy && !_shuttingDown)
            {
                e.Cancel = true;
                MessageBox.Show(this, "更新正在进行，请等下载和替换完成后再关闭设置窗口。", "更新中");
            }
            base.OnClosing(e);
        }

        private void SaveAndClose()
        {
            if (_updateBusy)
            {
                MessageBox.Show(this, "更新正在进行，请等下载和替换完成后再保存并关闭。", "更新中");
                return;
            }
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
            if (_chkBeatGridAccent != null) s.BeatGridAccent = _chkBeatGridAccent.IsChecked == true;
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
