using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using NientumStudio.Audio;
using NientumStudio.Controls;
using NientumStudio.Dialogs;
using NientumStudio.Models;
using NientumStudio.Services;

namespace NientumStudio
{
    public partial class MainWindow : Window
    {
        private readonly OfficialAudioEngine _officialAudio;
        private readonly FmodEngine _fmod;
        private readonly Stack<string> _undo = new Stack<string>();
        private readonly Stack<string> _redo = new Stack<string>();
        private readonly List<JObject> _clipboard = new List<JObject>();
        private readonly List<long> _tapTimesMs = new List<long>();

        private SongEntry _currentSong;
        private string _currentDiff = "normal";
        private JObject _chart = ChartStorage.CreateDefaultChart(120f);
        private float[] _waveform;
        private EditorTool _tool = EditorTool.Select;
        private double _playTime;
        private double _duration = 120.0;
        private int _lastBeatIdx = -1;
        private bool _suppressSongSelect;
        private double? _loopA;
        private double? _loopB;
        private ChartReport _lastReport;
        private int _officialSeekGuard;
        private bool _uiDirty = true;
        private int _frameTick;
        private bool _statsDirty;
        private readonly System.Windows.Threading.DispatcherTimer _statsTimer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        private readonly System.Windows.Threading.DispatcherTimer _longPressTimer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        private Window _previewPopup;
        private Window _timelinePopup;
        private bool _closing;
        private bool _applyingSettings;
        private bool _loadingSong;
        private bool _isDirty;
        private bool _wasPlaying;
        private int _songLoadSerial;
        private List<SongEntry> _allSongs = new List<SongEntry>();
        private readonly long[] _lastKindHitsoundMs = new long[16];
        private long _lastTextMs;
        private long _fpsWindowStart;
        private int _fpsFrames;
        private readonly List<(double time, OfficialAudioEngine.GameHitSound kind, float volScale, float pitch, bool fallbackDodge)> _hitNotes =
            new List<(double, OfficialAudioEngine.GameHitSound, float, float, bool)>();
        private bool _hitIndexDirty = true;
        private int _hitCursor;
        private int _chartVersion;
        private long _lastPaintTicks;
        private int _maxFps = 120;
        private double _offsetSec;
        private List<ChartLayer> _layers = new List<ChartLayer> { new ChartLayer() };
        private int _activeLayerIndex;
        private static readonly string[] LayerPalette = { "#2CC6C0", "#E070C0", "#E3B85C", "#9D7BFF", "#4ADE9B", "#FF9F43", "#6FA8FF", "#E45858" };
        private long _lastMetronomeMs;

        private static void ApplyDiffButtonStyle(Button btn, bool active)
        {
            btn.SetResourceReference(BackgroundProperty, active ? "ToolCheckedBg" : "ToolBg");
            btn.SetResourceReference(BorderBrushProperty, active ? "ToolCheckedBorder" : "BorderBrush");
            btn.SetResourceReference(ForegroundProperty, active ? "ToolCheckedForeground" : "TextSecondary");
            btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        public MainWindow()
        {
            AppLog.InitSession();
            GameLocator.Refresh();
            AppLog.Info($"Startup GamePath={EditorSettings.Current.GamePath ?? "(empty)"}");
            _officialAudio = new OfficialAudioEngine();
            _fmod = new FmodEngine(_officialAudio.CoreSystem);
            InitializeComponent();
            InitEventSelector();
            GameBackdropImage.Source = SpriteCache.PlaylistBg;
            GameBrandIcon.Source = SpriteCache.ButterflyGold;

            Timeline.GetItems = () => _chart?["items"] as JArray;
            Timeline.GetPlayTime = () => _playTime;
            Timeline.GetDuration = () => _duration;
            Timeline.GetDefaultBpm = () => _currentSong?.Meta?.bpm ?? 120.0;
            Timeline.GetDefaultBeat = () => Math.Clamp(_currentSong?.Meta?.defaultBeat ?? 4, 1, 16);
            Timeline.GetWaveform = () => EditorSettings.Current.ShowWaveform ? _waveform : null;
            Timeline.GetAudioName = () =>
            {
                if (_currentSong == null) return "未选择曲目";
                if (_currentSong.IsOfficial) return $"官方音轨 · {_currentSong.Meta.officialKey}";
                return string.IsNullOrEmpty(_currentSong.Meta.audioFile) ? "未导入音频" : _currentSong.Meta.audioFile;
            };
            Timeline.GetTool = () => _tool;
            Timeline.GetSnapDiv = GetSelectedSnapDiv;
            Timeline.GetNewEventType = GetSelectedEventType;
            Timeline.GetAutoMiddle = () => ChkAutoMiddle.IsChecked == true;
            Timeline.GetLoopRange = () => (_loopA.HasValue && _loopB.HasValue && _loopB > _loopA)
                ? (_loopA.Value, _loopB.Value) : ((double, double)?)null;
            Timeline.GetLoopA = () => _loopA;
            Timeline.OnSeek = SeekTo;
            Timeline.OnSelectionChanged = _ => BuildInspector();
            // 拖动时每帧只标记脏；体检分析由 160ms 定时器节流，避免编辑卡顿
            Timeline.OnChartMutated = () => { MarkDirty(); PostStatsUpdate(); InvalidatePreview(); RequestRedraw(); };
            Timeline.OnPushUndo = PushUndo;

            _statsTimer.Tick += (_, _) =>
            {
                if (_statsDirty)
                {
                    _statsDirty = false;
                    UpdateStats();
                }
                else
                {
                    _statsTimer.Stop();
                }
            };
            Timeline.OnZoomChanged = px => SldZoom.Value = px;
            Timeline.OnContextAction = HandleContextAction;

            Playfield.GetItems = () => _chart?["items"] as JArray;
            Playfield.GetPlayTime = () => _playTime;
            Playfield.GetDefaultBpm = () => _currentSong?.Meta?.bpm ?? 120.0;
            Playfield.GetCustomGridBpm = GetCustomGridBpm;
            Playfield.GetBeatsPerBar = GetBeatsPerBar;
            Playfield.GetGridDiv = GetPreviewGridDiv;
            Playfield.GetPxPerSec = () => Timeline.PxPerSec;
            Playfield.GetOffsetSec = () => _offsetSec;
            Timeline.GetOffsetSec = () => _offsetSec;
            Timeline.GetLayers = () => _layers;
            Timeline.GetActiveLayerIndex = () => _activeLayerIndex;
            Timeline.GetLayerBpm = () => ActiveLayerBpm;
            Timeline.IsSoloActiveLayer = () => _soloActiveLayer;
            Playfield.GetLayers = () => _layers;
            Playfield.GetActiveLayerIndex = () => _activeLayerIndex;
            Playfield.GetLayerBpm = () => ActiveLayerBpm;
            Playfield.IsSoloActiveLayer = () => _soloActiveLayer;
            Playfield.GetShowProjectionLines = () => ChkProjLines?.IsChecked == true;
            Playfield.OnGridPlaceNote = PlaceNoteFromPlayfield;
            Playfield.OnGridDeleteNote = DeleteNoteFromPlayfield;

            ProjectionBox.GetItems = () => _chart?["items"] as JArray;
            ProjectionBox.GetPlayTime = () => _playTime;
            ProjectionBox.GetDefaultBpm = () => _currentSong?.Meta?.bpm ?? 120.0;
            ProjectionBox.GetCustomGridBpm = GetCustomGridBpm;
            ProjectionBox.GetBeatsPerBar = GetBeatsPerBar;
            ProjectionBox.GetGridDiv = GetPreviewGridDiv;
            ProjectionBox.GetPxPerSec = () => Timeline.PxPerSec;
            ProjectionBox.GetOffsetSec = () => _offsetSec;
            ProjectionBox.GetLayers = () => _layers;
            ProjectionBox.GetActiveLayerIndex = () => _activeLayerIndex;
            ProjectionBox.GetLayerBpm = () => ActiveLayerBpm;
            ProjectionBox.IsSoloActiveLayer = () => _soloActiveLayer;
            ProjectionBox.OnGridPlaceNote = PlaceNoteFromPlayfield;
            ProjectionBox.OnGridDeleteNote = DeleteNoteFromPlayfield;

            Playfield.UseExternalBackground = true;
            Playfield.BackgroundImageChanged += (_, _) => UpdatePreviewBackgrounds();
            HighlightToolButtons();
            UpdateThemeUi();
            ThemeManager.ThemeChanged += OnThemeChanged;
            _longPressTimer.Tick += (_, _) =>
            {
                _longPressTimer.Stop();
                if (Mouse.LeftButton == MouseButtonState.Pressed)
                    PopOutPreview();
            };
            ApplySettings();
            LoadOfficialList();
            RefreshSongs();

            CompositionTarget.Rendering += OnRenderFrame;
            if (!GameLocator.HasGame)
                SetStatus("未检测到游戏目录：同步前请在 设置 → 文件位置 选择 Liora 游戏文件夹");
            BuildLayersPanel();
        }

        private void OnThemeChanged()
        {
            Timeline.InvalidateTheme();
            Playfield.InvalidateTheme();
            ProjectionBox?.InvalidateTheme();
            NpsGraph.InvalidateVisual();
            UpdateThemeUi();
            UpdateDiffButtons();
            UpdateStats();
            BuildLayersPanel();
            RequestRedraw();
            BuildInspector();
        }

        private void InvalidatePreview()
        {
            Playfield?.InvalidateVisual();
            ProjectionBox?.InvalidateVisual();
        }

        private void UpdateThemeUi()
        {
            BtnTheme.Content = ThemeManager.DisplayName(ThemeManager.Current);
            if (ThemeManager.IsGame)
            {
                BtnTheme.SetResourceReference(BorderBrushProperty, "AccentGold");
                BtnTheme.SetResourceReference(ForegroundProperty, "AccentGold");
            }
            else
            {
                BtnTheme.SetResourceReference(BorderBrushProperty, "ButtonBorder");
                BtnTheme.SetResourceReference(ForegroundProperty, "ButtonText");
            }
        }

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Cycle();
        }

        private void MenuTheme_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && Enum.TryParse<NientumStudio.Services.ThemeMode>(mi.Tag?.ToString(), out var mode))
                ThemeManager.Apply(mode);
        }

        private void MarkDirty()
        {
            if (_loadingSong) return;
            _hitIndexDirty = true;
            _chartVersion++;
            if (Playfield != null) Playfield.ChartVersion = _chartVersion;
            if (ProjectionBox != null) ProjectionBox.ChartVersion = _chartVersion;
            if (Timeline != null) Timeline.ChartVersion = _chartVersion;
            if (!_isDirty)
            {
                _isDirty = true;
                UpdateWindowTitle();
            }
        }

        private void ClearDirty()
        {
            _hitIndexDirty = true;
            _chartVersion++;
            if (Playfield != null) Playfield.ChartVersion = _chartVersion;
            if (ProjectionBox != null) ProjectionBox.ChartVersion = _chartVersion;
            if (Timeline != null) Timeline.ChartVersion = _chartVersion;
            _isDirty = false;
            UpdateWindowTitle();
        }

        private void UpdateWindowTitle()
        {
            string star = _isDirty ? "* " : "";
            Title = _currentSong != null
                ? $"{star}{_currentSong.DisplayTitle} [{_currentDiff}] - Nientum Studio · 实验版（斜轨与直线投影）"
                : $"{star}Nientum Studio · 实验版（斜轨与直线投影）";
        }

        private bool ConfirmDiscardOrSave()
        {
            if (!_isDirty || _currentSong == null) return true;
            var res = MessageBox.Show(this,
                $"曲目「{_currentSong.DisplayTitle} [{_currentDiff}]」有未保存的修改，是否先保存？",
                "未保存的修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (res == MessageBoxResult.Cancel) return false;
            if (res == MessageBoxResult.Yes) SaveCurrent();
            return true;
        }

        private JArray Items => _chart?["items"] as JArray;

        private void InitEventSelector()
        {
            foreach (var ev in ChartConstants.EventTypes)
                CmbNewEvent.Items.Add(new ComboBoxItem { Content = ev.Display, Tag = ev.TypeName });
            CmbNewEvent.SelectedIndex = 2; // 默认场景主题，与原版一致
        }

        private int GetSelectedSnapDiv()
        {
            if (CmbSnap.SelectedItem is ComboBoxItem ci && int.TryParse(ci.Tag?.ToString(), out int v)) return v;
            return 4;
        }

        private int GetPreviewGridDiv()
        {
            if (CmbPreviewGrid.SelectedItem is ComboBoxItem ci && int.TryParse(ci.Tag?.ToString(), out int v)) return v;
            return 2;
        }

        private double? GetCustomGridBpm()
        {
            if (TxtGridBpm != null && double.TryParse(TxtGridBpm.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 20 && v <= 600)
                return v;
            return null;
        }

        private int GetBeatsPerBar()
        {
            if (TxtGridBeatsPerBar != null && int.TryParse(TxtGridBeatsPerBar.Text, out int b) && b >= 1 && b <= 16)
                return b;
            return 4;
        }

        private void TxtBpm_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingSong || _currentSong?.Meta == null) return;
            if (float.TryParse(TxtBpm.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float bpm) && bpm >= 20 && bpm <= 600)
            {
                _currentSong.Meta.bpm = bpm;
                if (Items != null)
                {
                    var first = Items.OfType<JObject>().FirstOrDefault(o => o.GetTypeName() == "CRAFormatDataEventBPMBar");
                    if (first != null) first["fBPM"] = bpm;
                }
                MarkDirty();
                PostStatsUpdate();
                Timeline?.InvalidateVisual();
                InvalidatePreview();
            }
        }

        private void MetaField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender == TxtOffsetMs)
            {
                _hitIndexDirty = true;
                _offsetSec = double.TryParse(TxtOffsetMs.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double offsetMs)
                    ? offsetMs / 1000.0 : 0;
                Timeline?.InvalidateVisual();
                InvalidatePreview();
                RequestRedraw();
            }
            if (_loadingSong || _currentSong == null) return;
            MarkDirty();
        }

        private void TxtGridBpm_TextChanged(object sender, TextChangedEventArgs e) => InvalidatePreview();

        private string GetSelectedEventType()
        {
            return (CmbNewEvent.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "CRAFormatDataEventSpeed";
        }

        private void SetStatus(string msg) => TxtStatusMsg.Text = msg;

        // ---------------- 渲染与播放循环 ----------------
        private bool IsOfficialSong => _currentSong?.IsOfficial == true && _officialAudio.HasTrack && _officialAudio.LoadedKey == _currentSong.Meta.officialKey;

        private void OnRenderFrame(object sender, EventArgs e)
        {
            long nowMs = Environment.TickCount64;
            bool official = IsOfficialSong;
            bool playing = official ? _officialAudio.IsPlaying : _fmod.IsPlaying;
            if (playing != _wasPlaying)
            {
                _wasPlaying = playing;
                BtnPlay.Content = playing ? "⏸ 暂停" : "▶ 播放";
                if (!playing) { _fmod.StopAllSfx(); _officialAudio.StopGameHits(); }
            }
            if (playing)
            {
                double prevT = _playTime;
                if (official && _officialSeekGuard > 0)
                    _officialSeekGuard--; // FMOD 时间轴跳转需一帧生效，短暂保持 UI 位置避免回跳
                else
                    _playTime = official ? _officialAudio.GetPositionSec() : _fmod.GetPositionSec();

                if (_loopA.HasValue && _loopB.HasValue && _playTime >= _loopB.Value)
                {
                    SeekTo(_loopA.Value);
                    prevT = _playTime;
                }

                // 仅在正常推进时触发打击音；拖动标尺/跳转/循环回跳导致的大跨度一律跳过，避免批量叠音。
                // 按游戏 SoundManagerBase 语义：同类音效 20ms 去重，不同音效（如双押的地面+空中动作音）同时触发不互相吞音。
                double span = _playTime - prevT;
                double hitLead = Math.Clamp(EditorSettings.Current.HitLeadMs, 0, 120) / 1000.0;
                if (_hitIndexDirty) RebuildHitIndex();
                if (ChkHitsound.IsChecked == true && span > 0 && span < 0.25)
                {
                    while (_hitCursor < _hitNotes.Count && _hitNotes[_hitCursor].time <= prevT + hitLead) _hitCursor++;
                    double hitUpper = _playTime + hitLead;
                    int played = 0;
                    while (_hitCursor < _hitNotes.Count && _hitNotes[_hitCursor].time <= hitUpper)
                    {
                        var hit = _hitNotes[_hitCursor++];
                        int kIdx = Math.Clamp((int)hit.kind, 0, _lastKindHitsoundMs.Length - 1);
                        if (played < 12 && nowMs - _lastKindHitsoundMs[kIdx] >= 20)
                        {
                            PlayHitSfx(hit.kind, hit.volScale, hit.pitch, hit.fallbackDodge);
                            played++;
                            _lastKindHitsoundMs[kIdx] = nowMs;
                        }
                    }
                }
                else
                {
                    _hitCursor = HitLowerBound(prevT + hitLead);
                }

                if (ChkMetronome.IsChecked == true)
                {
                    int curBeat = (int)Math.Floor(TimeToBeat(_playTime));
                    if (curBeat > _lastBeatIdx)
                    {
                        if (curBeat - _lastBeatIdx <= 2 && nowMs - _lastMetronomeMs >= 60)
                        {
                            bool isBar = curBeat % 4 == 0;
                            float mVol = Math.Clamp((float)EditorSettings.Current.MetronomeVolume * (isBar ? 0.95f : 0.65f), 0f, 1.2f);
                            if (!_officialAudio.PlayGameHit(OfficialAudioEngine.GameHitSound.Metronome, mVol, isBar ? 1.32f : 1.0f))
                                _fmod.PlayMetronome(isBar);
                            _lastMetronomeMs = nowMs;
                        }
                        _lastBeatIdx = curBeat;
                    }
                }

                if (ChkFollow.IsChecked == true && Timeline.ActualWidth > 0)
                {
                    double px = (_playTime - Timeline.ViewStartSec) * Timeline.PxPerSec;
                    if (px > Timeline.ActualWidth * 0.78 || px < 0)
                        Timeline.ViewStartSec = Math.Max(0, _playTime - (Timeline.ActualWidth * 0.2) / Timeline.PxPerSec);
                }
            }

            _frameTick++;
            if (playing || _uiDirty)
            {
                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                double minIntervalMs = _maxFps > 0 ? 1000.0 / _maxFps : 0;
                double sinceMs = (nowTicks - _lastPaintTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (_uiDirty || sinceMs >= minIntervalMs)
                {
                    // 文本读数 30Hz 足够，避免每帧排版拖慢渲染
                    if (nowMs - _lastTextMs >= 33)
                    {
                        _lastTextMs = nowMs;
                        int mins = (int)(_playTime / 60.0);
                        double secs = _playTime % 60.0;
                        TxtReadout.Text = $"{mins:00}:{secs:00.000}";
                        TxtBeat.Text = $"Beat {TimeToBeat(_playTime):F2}";
                    }
                    Timeline.InvalidateVisual();
                    InvalidatePreview();
                    _lastPaintTicks = nowTicks;
                    _uiDirty = false;
                    _fpsFrames++;
                    if (nowMs - _fpsWindowStart >= 500)
                    {
                        double fpsValue = _fpsFrames * 1000.0 / Math.Max(1, nowMs - _fpsWindowStart);
                        TxtFps.Text = $"{fpsValue:F0} FPS";
                        _fpsFrames = 0;
                        _fpsWindowStart = nowMs;
                    }
                }
            }
            // 命中音效在本帧触发后同步提交 Studio 命令，减少一帧的启动延迟
            _officialAudio.Update();
            _fmod.Update();
            SyncCustomBgVideo(playing, false);
        }

        private void RequestRedraw() => _uiDirty = true;

        private void PostStatsUpdate()
        {
            _statsDirty = true;
            if (!_statsTimer.IsEnabled) _statsTimer.Start();
        }

        private double TimeToBeat(double t)
        {
            var bpms = Timeline.GetBpmSegments();
            double beat = 0, prevT = 0, curBpm = bpms[0].bpm;
            foreach (var b in bpms)
            {
                if (t <= b.time) break;
                if (b.time > prevT)
                {
                    beat += ((b.time - prevT) * curBpm) / 60.0;
                    prevT = b.time;
                }
                curBpm = b.bpm;
            }
            beat += ((t - prevT) * curBpm) / 60.0;
            return beat;
        }

        private void PlayHitSfx(OfficialAudioEngine.GameHitSound kind, float volScale = 1f, float pitch = 1f, bool fallbackDodge = false)
        {
            float baseVol = Math.Min(1.2f, (float)EditorSettings.Current.HitsoundVolume * 1.15f);
            if (!_officialAudio.PlayGameHit(kind, baseVol * volScale, pitch))
            {
                if (kind != OfficialAudioEngine.GameHitSound.Metronome)
                    _fmod.PlayHitsound(fallbackDodge);
            }
        }

        private void PlayLiveNoteHit(int lane, EditorTool tool)
        {
            if (tool == EditorTool.Coin)
            {
                PlayHitSfx(OfficialAudioEngine.GameHitSound.Coin, 0.95f);
                return;
            }
            if (tool == EditorTool.SuperJump)
            {
                PlayHitSfx(OfficialAudioEngine.GameHitSound.SuperJump, 1.0f);
                return;
            }
            if (tool == EditorTool.Orb)
            {
                PlayHitSfx(OfficialAudioEngine.GameHitSound.LongStart, 1.0f);
                return;
            }
            if (tool == EditorTool.MultiHit)
            {
                PlayHitSfx(OfficialAudioEngine.GameHitSound.MultiHit, 0.95f);
                return;
            }
            bool isDodge = tool == EditorTool.Dodge;
            PlayHitSfx(OfficialAudioEngine.GameHitSound.Normal, 1.0f, 1.0f, isDodge);
            PlayHitSfx(lane == 1 ? OfficialAudioEngine.GameHitSound.Jump : OfficialAudioEngine.GameHitSound.Ground, 0.78f);
        }

        private void RebuildHitIndex()
        {
            _hitIndexDirty = false;
            _hitNotes.Clear();
            _hitCursor = 0;
            if (Items == null) return;
            foreach (var tok in Items)
            {
                if (tok is not JObject o) continue;
                string tn = o.GetTypeName() ?? "";
                if (tn == "CRAFormatDataNoteLongMiddle")
                {
                    double mt = o.GetTime() + _offsetSec;
                    _hitNotes.Add((mt, OfficialAudioEngine.GameHitSound.Long, 0.92f, 1.0f, false));
                    continue;
                }
                if (!o.IsNote()) continue;

                double t = o.GetTime() + _offsetSec;
                int lane = o.GetLane();
                if (tn == "CRAFormatDataNoteCrystal")
                {
                    bool dodge = o.IsDodge();
                    _hitNotes.Add((t, OfficialAudioEngine.GameHitSound.Normal, 1.0f, 1.0f, dodge));
                    _hitNotes.Add((t, lane == 1 ? OfficialAudioEngine.GameHitSound.Jump : OfficialAudioEngine.GameHitSound.Ground, 0.78f, 1.0f, false));
                }
                else if (tn == "CRAFormatDataNoteOrb")
                {
                    _hitNotes.Add((t, OfficialAudioEngine.GameHitSound.LongStart, 1.0f, 1.0f, false));
                    double e = o.GetEndTime() + _offsetSec;
                    if (e > t + 0.02)
                        _hitNotes.Add((e, OfficialAudioEngine.GameHitSound.LongEnd, 1.0f, 1.0f, false));
                }
                else if (tn == "CRAFormatDataCoin")
                {
                    _hitNotes.Add((t, OfficialAudioEngine.GameHitSound.Coin, 0.95f, 1.0f, false));
                }
                else if (tn == "CRAFormatDataSuperJumpBody")
                {
                    _hitNotes.Add((t, OfficialAudioEngine.GameHitSound.SuperJump, 1.0f, 1.0f, false));
                }
                else if (tn == "CRAFormatDataNoteMultiHit")
                {
                    double e = o.GetEndTime() + _offsetSec;
                    if (e > t + 0.06)
                    {
                        int hitCount = o.Value<int?>("iHitCount") ?? 0;
                        int steps = hitCount > 0
                            ? Math.Clamp(hitCount, 2, 32)
                            : Math.Clamp((int)Math.Round((e - t) * 8.0), 3, 24);
                        for (int k = 0; k < steps; k++)
                        {
                            double ht = t + (e - t) * (k / (double)steps);
                            _hitNotes.Add((ht, OfficialAudioEngine.GameHitSound.MultiHit, 0.85f, Math.Min(1.6f, 1.0f + k * 0.03f), false));
                            _hitNotes.Add((ht, OfficialAudioEngine.GameHitSound.MultiHitPitch, 0.55f, Math.Min(5.5f, 4.0f + k * k * 0.01f), false));
                        }
                        _hitNotes.Add((e, OfficialAudioEngine.GameHitSound.MultiHitStrike, 1.0f, 1.0f, false));
                    }
                    else
                    {
                        _hitNotes.Add((t, OfficialAudioEngine.GameHitSound.MultiHit, 1.0f, 1.0f, false));
                    }
                }
                else
                {
                    _hitNotes.Add((t, OfficialAudioEngine.GameHitSound.Normal, 1.0f, 1.0f, false));
                }
            }
            _hitNotes.Sort((a, b) => a.time.CompareTo(b.time));
        }

        private int HitLowerBound(double t)
        {
            int lo = 0, hi = _hitNotes.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_hitNotes[mid].time <= t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private void TogglePlay()
        {
            if (IsOfficialSong)
            {
                if (_officialAudio.IsPlaying)
                {
                    _officialAudio.Pause();
                    _fmod.StopAllSfx();
                    _officialAudio.StopGameHits();
                    BtnPlay.Content = "▶ 播放";
                }
                else
                {
                    if (_playTime >= _duration - 0.05) _playTime = 0;
                    _lastBeatIdx = (int)Math.Floor(TimeToBeat(_playTime));
                    RebuildHitIndex();
                    _hitCursor = HitLowerBound(_playTime - 0.05);
                    _officialAudio.Play(_playTime);
                    BtnPlay.Content = "⏸ 暂停";
                }
                return;
            }
            if (_fmod.IsPlaying)
            {
                _fmod.Pause();
                BtnPlay.Content = "▶ 播放";
            }
            else
            {
                if (_playTime >= _duration - 0.05) _playTime = 0;
                _lastBeatIdx = (int)Math.Floor(TimeToBeat(_playTime));
                RebuildHitIndex();
                _hitCursor = HitLowerBound(_playTime - 0.05);
                _fmod.Play(_playTime);
                BtnPlay.Content = "⏸ 暂停";
            }
        }

        private void SeekTo(double sec)
        {
            _playTime = Math.Max(0, sec);
            _hitCursor = HitLowerBound(_playTime - 0.05);
            _lastBeatIdx = (int)Math.Floor(TimeToBeat(_playTime));
            _fmod.Seek(_playTime);
            if (IsOfficialSong)
            {
                _officialAudio.Seek(_playTime);
                _officialSeekGuard = 3;
            }
            SyncCustomBgVideo(_wasPlaying, true);
            Timeline.InvalidateVisual();
            InvalidatePreview();
            RequestRedraw();
        }

        // ---------------- 曲目与难度 ----------------
        private void LoadOfficialList()
        {
            CmbOfficial.ItemsSource = ChartStorage.ListOfficialCharts();
            if (CmbOfficial.Items.Count > 0) CmbOfficial.SelectedIndex = 0;
        }

        private void RefreshSongs(string selectFolder = null)
        {
            string target = selectFolder ?? _currentSong?.FolderName;
            _allSongs = ChartStorage.ListSongs();
            var filtered = FilterSongs(_allSongs);
            _suppressSongSelect = true;
            LstSongs.ItemsSource = filtered;
            if (filtered.Count > 0)
            {
                var found = filtered.FirstOrDefault(s => s.FolderName == target) ?? filtered[0];
                LstSongs.SelectedItem = found;
                _suppressSongSelect = false;
                SelectSong(found);
            }
            else
            {
                _suppressSongSelect = false;
            }
        }

        private List<SongEntry> FilterSongs(List<SongEntry> source)
        {
            string q = (TxtSongSearch?.Text ?? "").Trim();
            if (string.IsNullOrEmpty(q)) return source;
            return source.Where(s =>
                (s.DisplayTitle?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.Meta?.artist?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.FolderName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        private void TxtSongSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            var filtered = FilterSongs(_allSongs);
            _suppressSongSelect = true;
            LstSongs.ItemsSource = filtered;
            if (_currentSong != null)
            {
                var cur = filtered.FirstOrDefault(s => s.FolderPath == _currentSong.FolderPath);
                if (cur != null) LstSongs.SelectedItem = cur;
            }
            _suppressSongSelect = false;
        }

        private void LstSongs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSongSelect) return;
            if (LstSongs.SelectedItem is SongEntry entry)
            {
                if (_currentSong != null && entry.FolderPath == _currentSong.FolderPath) return;
                if (!ConfirmDiscardOrSave())
                {
                    _suppressSongSelect = true;
                    LstSongs.SelectedItem = _allSongs.FirstOrDefault(s => s.FolderPath == _currentSong?.FolderPath) ?? _currentSong;
                    _suppressSongSelect = false;
                    return;
                }
                SelectSong(entry);
            }
        }

        private void SelectSong(SongEntry entry)
        {
            _fmod.Pause();
            _officialAudio.Pause();
            _wasPlaying = false;
            BtnPlay.Content = "▶ 播放";
            _playTime = 0;
            Timeline.ViewStartSec = 0;
            _currentSong = entry;

            _loadingSong = true;
            try
            {
                var m = entry.Meta;
                TxtTitle.Text = m.title ?? "";
                TxtArtist.Text = m.artist ?? "";
                TxtCharter.Text = m.charter ?? "";
                TxtBpm.Text = m.bpm.ToString(CultureInfo.InvariantCulture);
                TxtOffsetMs.Text = m.offsetMs.ToString(CultureInfo.InvariantCulture);
                TxtPreviewSec.Text = m.previewStartSec.ToString(CultureInfo.InvariantCulture);
                int defBeat = Math.Clamp(m.defaultBeat > 0 ? m.defaultBeat : 4, 1, 16);
                m.defaultBeat = defBeat;
                CmbDefaultBeat.Text = defBeat.ToString(CultureInfo.InvariantCulture);
                TxtGridBeatsPerBar.Text = defBeat.ToString(CultureInfo.InvariantCulture);
                SldBgDim.Value = Math.Clamp(m.bgDim * 100.0, 0, 90);
                TxtBgDim.Text = $"{(int)Math.Round(SldBgDim.Value)}%";
                TxtBgOffset.Text = m.bgVideoOffsetSec.ToString(CultureInfo.InvariantCulture);
                double sc = Math.Clamp((m.bgScale > 0.01f ? m.bgScale : 1f) * 100.0, 50, 250);
                SldBgScale.Value = sc;
                TxtBgScale.Text = $"{(int)Math.Round(sc)}%";
                double px = Math.Clamp(m.bgPosX, -100, 100);
                SldBgPosX.Value = px;
                TxtBgPosX.Text = $"{(int)Math.Round(px)}%";
                double py = Math.Clamp(m.bgPosY, -100, 100);
                SldBgPosY.Value = py;
                TxtBgPosY.Text = $"{(int)Math.Round(py)}%";
                double gainPct = Math.Clamp((m.bgmGain > 0.01f ? m.bgmGain : 1f) * 100.0, 20, 300);
                SldBgmGain.Value = gainPct;
                TxtBgmGain.Text = $"{(int)Math.Round(gainPct)}%";
            }
            finally
            {
                _loadingSong = false;
            }

            ApplySongVolume();
            UpdateCoverPreview();
            LoadCustomBgForCurrentSong();
            LoadDifficulty("normal");
            _ = LoadAudioForCurrentSongAsync();
        }

        private async System.Threading.Tasks.Task LoadAudioForCurrentSongAsync()
        {
            int serial = ++_songLoadSerial;
            var entry = _currentSong;
            _waveform = null;
            if (entry?.Meta == null) return;
            var m = entry.Meta;
            string ownAudioPath = string.IsNullOrEmpty(m.audioFile) ? null : Path.Combine(entry.FolderPath, m.audioFile);
            bool hasOwnAudio = !string.IsNullOrEmpty(ownAudioPath) && File.Exists(ownAudioPath);

            if (hasOwnAudio)
            {
                // 用户自行导入的音频优先（官方副本也允许覆盖官方音轨）
                _officialAudio.UnloadTrack();
                TxtAudioStatus.Text = $"{m.audioFile} · 正在解码...";
                TxtAudioStatus.Foreground = (Brush)FindResource("TextDim");

                var (wavBytes, mfPeaks) = await System.Threading.Tasks.Task.Run(() =>
                {
                    byte[] wav = Audio.MediaFoundationDecoder.DecodeToWav48k16BitStereo(ownAudioPath, out _);
                    float[] pks = wav != null ? Audio.MediaFoundationDecoder.ExtractPeaksFromWav48k16BitStereo(wav) : null;
                    return (wav, pks);
                });
                if (serial != _songLoadSerial || _closing) return;

                if (wavBytes != null)
                {
                    _fmod.LoadSongFromMemory(wavBytes);
                    _duration = Math.Max(10.0, _fmod.DurationSec);
                    AppLog.Info($"Audio loaded via MediaFoundation: {ownAudioPath} ({_fmod.DurationSec:F2}s, {wavBytes.Length / 1024}KB)");
                    TxtAudioStatus.Text = $"{m.audioFile} · {_fmod.DurationSec:F1}s";
                    TxtAudioStatus.Foreground = (Brush)FindResource("AccentGround");
                    _waveform = mfPeaks;
                    UpdateStats();
                    Timeline.InvalidateVisual();
                    RequestRedraw();
                }
                else
                {
                    // 回退至 FMOD 原生加载（兼容未装 MF 解码器的老系统 OGG）
                    AppLog.Warn($"MediaFoundation decode returned null, fallback to FMOD stream: {ownAudioPath}");
                    _fmod.LoadSong(ownAudioPath);
                    _duration = Math.Max(10.0, _fmod.DurationSec);
                    TxtAudioStatus.Text = $"{m.audioFile} · {_fmod.DurationSec:F1}s";
                    TxtAudioStatus.Foreground = (Brush)FindResource("AccentGround");
                    UpdateStats();
                    var peaks = await _fmod.ExtractWaveformPeaksAsync(ownAudioPath);
                    if (serial == _songLoadSerial && !_closing)
                    {
                        _waveform = peaks;
                        Timeline.InvalidateVisual();
                        RequestRedraw();
                    }
                }
            }
            else if (entry.IsOfficial && !string.IsNullOrEmpty(m.officialKey))
            {
                _fmod.UnloadSong();
                if (_officialAudio.LoadTrack(m.officialKey))
                {
                    double len = _officialAudio.LengthSec;
                    _duration = len > 5 ? Math.Max(10.0, len) : 120.0;
                    TxtAudioStatus.Text = $"官方音轨 · {m.officialKey}";
                    TxtAudioStatus.Foreground = (Brush)FindResource("AccentJump");
                }
                else
                {
                    _duration = 120.0;
                    TxtAudioStatus.Text = _officialAudio.IsAvailable ? "官方音轨缺失（可手动导入音频）" : "官方音频不可用（可手动导入音频）";
                    TxtAudioStatus.Foreground = (Brush)FindResource("TextDim");
                }
                if (_officialAudio.LengthSec <= 0 && Items != null)
                {
                    double last = Items.OfType<JObject>().Where(o => o.IsNote())
                        .Select(o => o.IsLong() ? o.GetEndTime() : o.GetTime())
                        .DefaultIfEmpty(0).Max();
                    if (last > 10) _duration = Math.Max(_duration, last + 3);
                }
                UpdateStats();
            }
            else
            {
                _officialAudio.UnloadTrack();
                _fmod.UnloadSong();
                _duration = 120.0;
                TxtAudioStatus.Text = "未导入音频";
                TxtAudioStatus.Foreground = (Brush)FindResource("TextDim");
                UpdateStats();
            }
        }

        private void UpdateDiffButtons()
        {
            ApplyDiffButtonStyle(BtnDiffNormal, _currentDiff == "normal");
            ApplyDiffButtonStyle(BtnDiffHard, _currentDiff == "hard");
            ApplyDiffButtonStyle(BtnDiffExtreme, _currentDiff == "extreme");
        }

        private void UpdateCoverPreview()
        {
            if (_currentSong == null || string.IsNullOrEmpty(_currentSong.Meta.coverFile))
            {
                ImgCover.Source = null;
                return;
            }
            string p = Path.Combine(_currentSong.FolderPath, _currentSong.Meta.coverFile);
            if (!File.Exists(p)) { ImgCover.Source = null; return; }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(p, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                ImgCover.Source = bmp;
            }
            catch { ImgCover.Source = null; }
        }

        private void LoadDifficulty(string diff)
        {
            _currentDiff = diff;
            UpdateDiffButtons();

            _loadingSong = true;
            try
            {
                var dMeta = GetDiffMeta(_currentSong?.Meta, diff);
                TxtDiffLevel.Text = (dMeta?.level ?? 0).ToString();
                string file = dMeta?.chartFile ?? $"{diff}.json";
                _chart = _currentSong != null
                    ? ChartStorage.LoadChart(_currentSong.FolderPath, file, _currentSong.Meta.bpm)
                    : ChartStorage.CreateDefaultChart(120f);
            }
            finally
            {
                _loadingSong = false;
            }

            BtnRestoreOfficial.Visibility = _currentSong?.IsOfficial == true ? Visibility.Visible : Visibility.Collapsed;
            if (_currentSong?.IsOfficial == true && _officialAudio.LengthSec <= 0 && _chart["items"] is JArray arr)
            {
                double last = arr.OfType<JObject>().Where(o => o.IsNote())
                    .Select(o => o.IsLong() ? o.GetEndTime() : o.GetTime())
                    .DefaultIfEmpty(0).Max();
                if (last > 10) _duration = Math.Max(_duration, last + 3);
            }

            _layers = (_currentSong != null
                ? ChartStorage.LoadLayers(_currentSong.FolderPath, GetDiffMeta(_currentSong.Meta, diff)?.chartFile ?? $"{diff}.json")
                : null) ?? new List<ChartLayer>();
            if (_layers.Count == 0) _layers.Add(new ChartLayer());
            _activeLayerIndex = 0;
            BuildLayersPanel();

            _undo.Clear();
            _redo.Clear();
            Timeline.SelectedIndices.Clear();
            ClearDirty();
            BuildInspector();
            UpdateStats();
            RequestRedraw();
        }

        private static DifficultyMeta GetDiffMeta(CustomTrackMeta m, string diff)
        {
            if (m?.difficulties == null) return null;
            return diff switch
            {
                "normal" => m.difficulties.normal,
                "hard" => m.difficulties.hard,
                "extreme" => m.difficulties.extreme,
                _ => null
            };
        }

        private void SyncMetaInputs()
        {
            if (_currentSong == null) return;
            var m = _currentSong.Meta;
            m.title = string.IsNullOrWhiteSpace(TxtTitle.Text) ? _currentSong.FolderName : TxtTitle.Text.Trim();
            m.artist = TxtArtist.Text.Trim();
            m.charter = TxtCharter.Text.Trim();
            if (float.TryParse(TxtBpm.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float bpm) && bpm > 10) m.bpm = bpm;
            if (float.TryParse(TxtOffsetMs.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float off)) m.offsetMs = off;
            if (float.TryParse(TxtPreviewSec.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float prev)) m.previewStartSec = prev;
            if (int.TryParse(CmbDefaultBeat.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int db)) m.defaultBeat = Math.Clamp(db, 1, 16);
            m.bgDim = (float)Math.Clamp(SldBgDim.Value / 100.0, 0.0, 0.9);
            if (float.TryParse(TxtBgOffset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float bgo)) m.bgVideoOffsetSec = bgo;
            m.bgScale = (float)Math.Clamp(SldBgScale.Value / 100.0, 0.5, 2.5);
            m.bgPosX = (float)Math.Clamp(SldBgPosX.Value, -100.0, 100.0);
            m.bgPosY = (float)Math.Clamp(SldBgPosY.Value, -100.0, 100.0);
            m.bgmGain = (float)Math.Clamp(SldBgmGain.Value / 100.0, 0.2, 3.0);

            m.difficulties ??= new DifficultiesMap();
            var dm = GetDiffMeta(m, _currentDiff);
            if (dm == null)
            {
                dm = new DifficultyMeta { chartFile = $"{_currentDiff}.json" };
                if (_currentDiff == "hard") m.difficulties.hard = dm;
                else if (_currentDiff == "extreme") m.difficulties.extreme = dm;
                else m.difficulties.normal = dm;
            }
            if (int.TryParse(TxtDiffLevel.Text, out int lv)) dm.level = Math.Max(0, lv);
            if (string.IsNullOrEmpty(dm.chartFile)) dm.chartFile = $"{_currentDiff}.json";
        }

        private void SaveCurrent()
        {
            if (_currentSong == null) return;
            SyncMetaInputs();

            if (Items != null)
            {
                var selectedRefs = Timeline.SelectedIndices
                    .Where(idx => idx >= 0 && idx < Items.Count)
                    .Select(idx => Items[idx] as JObject)
                    .Where(o => o != null)
                    .ToHashSet();

                var sorted = Items.OfType<JObject>().OrderBy(o => o.GetTime()).ToList();
                var newArr = new JArray();
                Timeline.SelectedIndices.Clear();
                for (int i = 0; i < sorted.Count; i++)
                {
                    sorted[i]["iFormatDataIndex"] = i;
                    newArr.Add(sorted[i]);
                    if (selectedRefs.Contains(sorted[i]))
                        Timeline.SelectedIndices.Add(i);
                }
                _chart["items"] = newArr;
            }

            var dm = GetDiffMeta(_currentSong.Meta, _currentDiff);
            if (dm.level < 0) dm.level = 0;
            _loadingSong = true;
            try { TxtDiffLevel.Text = dm.level.ToString(); }
            finally { _loadingSong = false; }

            ChartStorage.SaveMeta(_currentSong.FolderPath, _currentSong.Meta);
            ChartStorage.SaveChart(_currentSong.FolderPath, dm.chartFile, _chart);
            ChartStorage.SaveLayers(_currentSong.FolderPath, dm.chartFile, _layers);
            ClearDirty();
            SetStatus($"已保存：{_currentSong.Meta.title} [{_currentDiff}] · {DateTime.Now:HH:mm:ss}");
            // 只刷新列表显示，不重建选中项（避免重载音频与封面）
            RefreshSongListOnly();
        }

        private void RefreshSongListOnly()
        {
            string target = _currentSong?.FolderPath;
            _allSongs = ChartStorage.ListSongs();
            var filtered = FilterSongs(_allSongs);
            _suppressSongSelect = true;
            LstSongs.ItemsSource = filtered;
            var found = filtered.FirstOrDefault(s => s.FolderPath == target);
            if (found != null)
            {
                LstSongs.SelectedItem = found;
                _currentSong = found;
            }
            _suppressSongSelect = false;
            UpdateWindowTitle();
        }

        private void MenuOpenSongFolder_Click(object sender, RoutedEventArgs e)
        {
            var target = (LstSongs.SelectedItem as SongEntry) ?? _currentSong;
            if (target == null || !Directory.Exists(target.FolderPath)) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + target.FolderPath + "\""); } catch { }
        }

        private void MenuDeleteSong_Click(object sender, RoutedEventArgs e)
        {
            var target = (LstSongs.SelectedItem as SongEntry) ?? _currentSong;
            if (target == null) return;
            string kind = target.IsOfficial ? "官方工作区副本" : "自定义曲目";
            var res = MessageBox.Show(this,
                $"确定删除{kind}「{target.DisplayTitle}」？\n磁盘目录将被移除：{target.FolderPath}",
                "删除曲目", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            if (_currentSong != null && target.FolderPath == _currentSong.FolderPath)
            {
                _fmod.UnloadSong();
                _officialAudio.UnloadTrack();
                _wasPlaying = false;
                BtnPlay.Content = "▶ 播放";
                ClearDirty();
            }
            try
            {
                if (Directory.Exists(target.FolderPath))
                    Directory.Delete(target.FolderPath, true);
                SetStatus($"已删除：{target.DisplayTitle}");
            }
            catch (Exception ex)
            {
                SetStatus("删除失败：" + ex.Message);
                return;
            }
            _currentSong = null;
            RefreshSongs();
        }

        // ---------------- 撤销 / 编辑辅助 ----------------
        private void PushUndo()
        {
            _undo.Push(_chart.ToString(Newtonsoft.Json.Formatting.None));
            _redo.Clear();
            MarkDirty();
        }

        private void DoUndo()
        {
            if (_undo.Count == 0) return;
            _redo.Push(_chart.ToString(Newtonsoft.Json.Formatting.None));
            _chart = JObject.Parse(_undo.Pop());
            Timeline.SelectedIndices.Clear();
            MarkDirty();
            BuildInspector();
            UpdateStats();
            InvalidatePreview();
            Timeline.InvalidateVisual();
            RequestRedraw();
        }

        private void DoRedo()
        {
            if (_redo.Count == 0) return;
            _undo.Push(_chart.ToString(Newtonsoft.Json.Formatting.None));
            _chart = JObject.Parse(_redo.Pop());
            Timeline.SelectedIndices.Clear();
            MarkDirty();
            BuildInspector();
            UpdateStats();
            InvalidatePreview();
            Timeline.InvalidateVisual();
            RequestRedraw();
        }

        private void PlaceNoteFromPlayfield(double rawTime, int lane)
        {
            if (Items == null) return;
            if (!EnsureActiveLayerEditable()) return;
            int prevDiv = GetSelectedSnapDiv();
            int gDiv = GetPreviewGridDiv();
            double snapped = SnapWithDiv(rawTime, gDiv > 0 ? gDiv : prevDiv);
            EditorTool t = (_tool == EditorTool.Select || _tool == EditorTool.Event) ? EditorTool.Crystal : _tool;
            var item = Timeline.CreateItem(Items, t, snapped - _offsetSec, lane);
            if (item != null)
            {
                PushUndo();
                Items.Add(item);
                if (t == EditorTool.Orb && ChkAutoMiddle.IsChecked == true)
                    Timeline.EnsureLongMiddles(Items, item);
                Timeline.SelectedIndices.Clear();
                Timeline.SelectedIndices.Add(Items.Count - 1);
                if (ChkHitsound.IsChecked == true)
                    PlayLiveNoteHit(lane, t);
                BuildInspector();
                UpdateStats();
            }
        }

        private void DeleteNoteFromPlayfield(double rawTime, int lane)
        {
            if (Items == null) return;
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (Items[i] is JObject o && o.IsNote() && o.GetLane() == lane && Math.Abs(o.GetTime() + _offsetSec - rawTime) < 0.12)
                {
                    PushUndo();
                    TimelineControl.RemoveItemAt(Items, i);
                    Timeline.SelectedIndices.Clear();
                    BuildInspector();
                    UpdateStats();
                    return;
                }
            }
        }

        private double SnapWithDiv(double t, int div)
        {
            if (t < 0) t = 0;
            if (div <= 0) return Math.Round(t, 4);
            double? customBpm = GetCustomGridBpm();
            if (customBpm.HasValue)
            {
                double cStep = 60.0 / customBpm.Value / div;
                return Math.Max(0, Math.Round(Math.Round(t / cStep) * cStep, 4));
            }
            var bpms = Timeline.GetBpmSegments();
            double segStart = 0, curBpm = bpms[0].bpm;
            foreach (var b in bpms) { if (t >= b.time) { segStart = b.time; curBpm = b.bpm; } }
            double step = 60.0 / curBpm / div;
            return Math.Max(0, Math.Round(segStart + Math.Round((t - segStart) / step) * step, 4));
        }

        private void DeleteSelected()
        {
            if (Items == null || Timeline.SelectedIndices.Count == 0) return;
            DeleteIndices(Timeline.SelectedIndices.ToArray());
        }

        private void DeleteIndices(int[] indices)
        {
            if (Items == null || indices.Length == 0) return;
            indices = indices.Where(i => !(i >= 0 && i < Items.Count && Items[i] is JObject oo && IsObjectLocked(oo))).ToArray();
            if (indices.Length == 0)
            {
                SetStatus("选中的对象位于锁定图层，无法删除");
                return;
            }
            PushUndo();
            foreach (int idx in indices.OrderByDescending(x => x))
                TimelineControl.RemoveItemAt(Items, idx);
            Timeline.SelectedIndices.Clear();
            BuildInspector();
            UpdateStats();
        }

        private void FlipSelectedLanes() => FlipIndices(Timeline.SelectedIndices.ToArray());

        private void FlipIndices(int[] indices)
        {
            if (Items == null || indices.Length == 0) return;
            indices = indices.Where(i => !(i >= 0 && i < Items.Count && Items[i] is JObject oo && IsObjectLocked(oo))).ToArray();
            if (indices.Length == 0)
            {
                SetStatus("选中的对象位于锁定图层");
                return;
            }
            PushUndo();
            foreach (int idx in indices)
            {
                if (idx >= 0 && idx < Items.Count && Items[idx] is JObject o && o.IsNote())
                    TimelineControl.SetNoteLaneWithAttachments(Items, o, o.GetLane() == 1 ? 0 : 1);
            }
            BuildInspector();
            UpdateStats();
        }

        private void CopyIndices(int[] indices)
        {
            if (Items == null || indices.Length == 0) return;
            _clipboard.Clear();
            foreach (int idx in indices.OrderBy(x => x))
            {
                if (idx >= 0 && idx < Items.Count && Items[idx] is JObject o)
                    _clipboard.Add((JObject)o.DeepClone());
            }
            SetStatus($"已复制 {_clipboard.Count} 个对象");
        }

        private static void ClearMiddles(JArray items, JObject orb)
        {
            var ids = (orb["LongMiddleIds"] as JArray)?.Values<int>().ToList() ?? new List<int>();
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (items[i] is JObject d && d.GetTypeName() == "CRAFormatDataNoteLongMiddle" && ids.Contains(d.GetId()))
                    items.RemoveAt(i);
            }
            orb["LongMiddleIds"] = new JArray();
        }

        private void HandleContextAction(string action, int[] indices)
        {
            if (Items == null) return;
            switch (action)
            {
                case "delete":
                    DeleteIndices(indices);
                    break;
                case "copy":
                    CopyIndices(indices);
                    break;
                case "flip":
                    FlipIndices(indices);
                    break;
                case "addmiddle":
                case "clearmiddle":
                    PushUndo();
                    foreach (int idx in indices)
                    {
                        if (idx >= 0 && idx < Items.Count && Items[idx] is JObject orb && orb.GetTypeName() == "CRAFormatDataNoteOrb")
                        {
                            ClearMiddles(Items, orb);
                            if (action == "addmiddle")
                                Timeline.EnsureLongMiddles(Items, orb);
                        }
                    }
                    BuildInspector();
                    UpdateStats();
                    SetStatus(action == "addmiddle" ? "已补全长条中段" : "已清除长条中段");
                    break;
            }
        }

        // ---------------- 体检 ----------------
        private void UpdateStats()
        {
            var items = Items;
            float offsetMs = float.TryParse(TxtOffsetMs.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float om) ? om : 0f;
            double bpm = _currentSong?.Meta?.bpm ?? 120.0;
            var report = ChartValidator.Analyze(items, _duration > 1 ? _duration : 0, offsetMs, bpm);
            _lastReport = report;

            TxtStatTotal.Text = report.TotalNotes.ToString();
            TxtStatNps.Text = $"{report.AvgNps:F2} / {report.PeakNps:F1}";
            TxtStatMisc.Text = $"{report.EventCount} · {report.LastNoteTime:F1}s";
            TxtStats.Text =
                $"水晶 {report.CrystalCount} · 闪避 {report.DodgeCount} · 长条 {report.OrbCount}（中段 {report.LongMiddleCount}）· 连打 {report.MultiHitCount} · 金币 {report.CoinCount} · 大跳 {report.SuperJumpCount}";

            TxtLevelBadge.Text = report.TotalNotes > 0
                ? $"建议 Lv.{report.SuggestedLevel} · {report.SuggestedLevelText}"
                : "难度 —";

            NpsGraph.SetValues(report.NpsPerSecond);

            IssuesPanel.Children.Clear();
            var sorted = report.Issues
                .OrderBy(i => i.Level == IssueLevel.Error ? 0 : i.Level == IssueLevel.Warning ? 1 : 2)
                .ToList();
            int shown = Math.Min(sorted.Count, 28);
            for (int i = 0; i < shown; i++)
            {
                var issue = sorted[i];
                var color = issue.Level == IssueLevel.Error ? ThemeManager.IssueError
                    : issue.Level == IssueLevel.Warning ? ThemeManager.IssueWarning : ThemeManager.IssueInfo;
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(20, color.R, color.G, color.B)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(72, color.R, color.G, color.B)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(9, 6, 9, 6),
                    Margin = new Thickness(0, 0, 0, 5)
                };
                var sp = new DockPanel();
                var dot = new TextBlock { Text = "●", FontSize = 9, Foreground = new SolidColorBrush(color), Margin = new Thickness(0, 3, 7, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(dot, Dock.Left);
                sp.Children.Add(dot);
                var msgTb = new TextBlock
                {
                    Text = issue.Message,
                    FontSize = 11,
                    LineHeight = 17,
                    TextWrapping = TextWrapping.Wrap
                };
                msgTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                sp.Children.Add(msgTb);
                card.Child = sp;
                IssuesPanel.Children.Add(card);
            }
            if (sorted.Count > shown)
                IssuesPanel.Children.Add(new TextBlock { Text = $"… 另有 {sorted.Count - shown} 条提示", FontSize = 11, Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(2, 4, 0, 0) });
            if (sorted.Count == 0 && report.TotalNotes > 0)
            {
                var okCard = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(20, 76, 224, 157)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(75, 76, 224, 157)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 7, 10, 7)
                };
                okCard.Child = new TextBlock { Text = "✔ 谱面体检通过 · 未发现问题", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("AccentGreen") };
                IssuesPanel.Children.Add(okCard);
            }
            RequestRedraw();
        }

        private void BtnFixMiddles_Click(object sender, RoutedEventArgs e)
        {
            if (Items == null) return;
            var orbs = Items.OfType<JObject>().Where(o => o.GetTypeName() == "CRAFormatDataNoteOrb").ToList();
            if (orbs.Count == 0)
            {
                SetStatus("当前谱面没有长条");
                return;
            }
            PushUndo();
            int added = 0;
            foreach (var orb in orbs)
            {
                double span = orb.GetEndTime() - orb.GetTime();
                if (span < 2.0) continue;
                ClearMiddles(Items, orb);
                int before = Items.Count;
                Timeline.EnsureLongMiddles(Items, orb);
                added += Items.Count - before;
            }
            UpdateStats();
            SetStatus($"已补全长条中段：新增 {added} 个判定点");
        }

        // ---------------- 检查器 ----------------
        private void BuildInspector()
        {
            InspectorPanel.Children.Clear();
            if (Items == null || Timeline.SelectedIndices.Count == 0)
            {
                BtnDeleteSelected.IsEnabled = false;
                var emptyCard = new Border
                {
                    Background = (Brush)FindResource("BgCard"),
                    BorderBrush = (Brush)FindResource("BorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(13, 12, 13, 12)
                };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock
                {
                    Text = "在时间轴或画布中点击选中音符 / 事件",
                    Foreground = (Brush)FindResource("TextPrimary"),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11.5,
                    Margin = new Thickness(0, 0, 0, 8)
                });
                sp.Children.Add(new TextBlock
                {
                    Text = "• J / K 键随音乐实时踩点打谱（地面 / 空中）\n• 右键音符可复制、删除、翻转轨或补长条中段\n• ← / → 按吸附步长微调，Shift 加大到整小节\n• 选中音符时实时画面将显示 ±90/130/200ms 判定窗",
                    Foreground = (Brush)FindResource("TextDim"),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    LineHeight = 19
                });
                emptyCard.Child = sp;
                InspectorPanel.Children.Add(emptyCard);
                return;
            }
            BtnDeleteSelected.IsEnabled = true;

            if (Timeline.SelectedIndices.Count > 1)
            {
                InspectorPanel.Children.Add(new TextBlock
                {
                    Text = $"已批量选中 {Timeline.SelectedIndices.Count} 个对象",
                    Foreground = (Brush)FindResource("AccentBlue"),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 6)
                });
                InspectorPanel.Children.Add(new TextBlock
                {
                    Text = "F 翻转上下轨 · Ctrl+C 复制 · Del 删除 · ←/→ 微调",
                    Foreground = (Brush)FindResource("TextDim"),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11
                });
                return;
            }

            int idx = Timeline.SelectedIndices.First();
            if (idx < 0 || idx >= Items.Count || Items[idx] is not JObject it) return;
            if (IsObjectLocked(it))
            {
                BtnDeleteSelected.IsEnabled = false;
                InspectorPanel.Children.Add(new TextBlock
                {
                    Text = "所属图层已锁定。请在左侧图层面板点击“解锁”后再编辑。",
                    Foreground = (Brush)FindResource("AccentGold"),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11
                });
                return;
            }

            string tn = it.GetTypeName();
            var info = ChartConstants.FindEvent(tn);

            // 标题 + 类型色标
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var chip = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(TimelineControl.GetItemColor(it)),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            head.Children.Add(chip);
            var title = new TextBlock
            {
                Text = info?.Display ?? PrettyType(tn),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = (Brush)FindResource("TextPrimary")
            };
            head.Children.Add(title);
            InspectorPanel.Children.Add(head);
            if (info != null && !string.IsNullOrEmpty(info.Hint))
                InspectorPanel.Children.Add(new TextBlock
                {
                    Text = info.Hint,
                    Foreground = (Brush)FindResource("TextDim"),
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6)
                });

            AddSectionHeader("时间");
            AddNumericField("时间（秒）", it.GetTime(), v =>
            {
                double oldT = it.GetTime();
                double newT = Math.Max(0, v);
                double dur = it.IsLong() ? it.GetEndTime() - oldT : 0;
                it.SetTime(newT);
                if (dur > 0) it.SetEndTime(newT + dur);
                TimelineControl.ShiftLinkedAttachments(Items, it, newT - oldT);
            });
            if (it.IsLong())
            {
                AddNumericField("结束时间（秒）", it.GetEndTime(), v =>
                {
                    double endT = Math.Max(it.GetTime() + 0.05, v);
                    it.SetEndTime(endT);
                    if (tn == "CRAFormatDataSuperJumpBody")
                    {
                        var div = FindDivider(it);
                        div?.SetTime((it.GetTime() + endT) / 2.0);
                    }
                    else if (tn == "CRAFormatDataNoteOrb")
                    {
                        if (ChkAutoMiddle.IsChecked == true) Timeline.RebuildLongMiddles(Items, it);
                        else TimelineControl.TrimMiddles(Items, it);
                    }
                });
                AddReadonlyField($"时长 {it.GetEndTime() - it.GetTime():F2}s · 约第 {TimeToBeat(it.GetTime()):F2} 拍");
            }
            else
            {
                AddReadonlyField($"第 {TimeToBeat(it.GetTime()):F2} 拍");
            }

            if (_layers.Count > 1)
            {
                AddComboField("图层", _layers.Select((l, i) => (i, $"{i + 1}. {l.Name}" + (Math.Abs(l.Angle) > 0.1 ? $" ({l.Angle:+0;-0;0}°)" : ""))).ToArray(), it.GetEditorLayer(), v =>
                {
                    it["editorLayer"] = v;
                    Timeline.InvalidateVisual();
                    InvalidatePreview();
                    MarkDirty();
                });
            }

            if (it.IsNote())
            {
                AddSectionHeader("轨道");
                if (tn == "CRAFormatDataSuperJumpBody")
                    AddReadonlyField("固定空中大跳轨 (Track 5)");
                else
                    AddComboField("轨道", new[] { (0, "地面轨 Ground（J 键）"), (1, "空中轨 Jump（K 键）") }, it.GetLane(),
                        v => TimelineControl.SetNoteLaneWithAttachments(Items, it, v));
            }

            switch (tn)
            {
                case "CRAFormatDataNoteCrystal":
                case "CRAFormatDataCoin":
                case "CRAFormatDataNoteOrb":
                case "CRAFormatDataNoteMultiHit":
                case "CRAFormatDataSuperJumpBody":
                    BuildNoteProperties(it, tn);
                    break;
            }

            if (info != null)
            {
                AddSectionHeader("事件参数");
                BuildEventProperties(it, tn);
            }

            // 附属信息
            if (tn == "CRAFormatDataSuperJumpBody")
            {
                var div = FindDivider(it);
                if (div == null)
                    InspectorPanel.Children.Add(new TextBlock { Text = "⚠ 找不到配对的跳跃分隔点（游戏内不会生效）", Foreground = (Brush)FindResource("AccentRed"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            }
        }

        private JObject FindDivider(JObject body)
        {
            if (Items == null) return null;
            int divId = body.Value<int?>("DividerId") ?? 0;
            return Items.OfType<JObject>().FirstOrDefault(d => d.GetId() == divId);
        }

        private void BuildNoteProperties(JObject it, string tn)
        {
            AddSectionHeader("音符属性");

            if (tn == "CRAFormatDataNoteCrystal" || tn == "CRAFormatDataNoteOrb")
                AddComboField("闪避障碍", new[] { (0, "否（击打）"), (1, "是（闪避）") }, it.IsDodge() ? 1 : 0, v => it["bIsDodge"] = v == 1);

            if (it.ContainsKey("bIsSimultaneous"))
                AddComboField("双押高亮", new[] { (0, "否"), (1, "是") }, it.IsSimultaneous() ? 1 : 0, v => it["bIsSimultaneous"] = v == 1);

            if (it.ContainsKey("bIsHard"))
                AddComboField("硬质（仅演出）", new[] { (0, "否"), (1, "是（不生成判定块）") }, it.IsHard() ? 1 : 0, v => it["bIsHard"] = v == 1);

            if (it.ContainsKey("eInteractorType"))
                AddComboField("触发者", ChartConstants.InteractorOptions.Select((s, i) => (i, s)).ToArray(), it.Value<int?>("eInteractorType") ?? 0, v => it["eInteractorType"] = v);

            if (it.ContainsKey("fDamage"))
            {
                AddNumericField("伤害 fDamage（0 = 默认 0.1，-1 = 无伤害）", it.Value<double?>("fDamage") ?? 0.0, v => it["fDamage"] = Math.Clamp(v, -1, 1));
            }

            if (it.ContainsKey("eFFObjectPreset"))
                AddComboField("对象样式", ChartConstants.ObjectPresetOptions.Select((s, i) => (i, s)).ToArray(), it.Value<int?>("eFFObjectPreset") ?? 0, v => it["eFFObjectPreset"] = v);

            if (tn == "CRAFormatDataNoteMultiHit")
            {
                AddNumericField("连打次数（0 = 自动按区间）", it.Value<int?>("iHitCount") ?? 0, v => it["iHitCount"] = Math.Max(0, (int)v));
                AddComboField("锁定镜头位置", new[] { (0, "否"), (1, "是") }, (it.Value<bool?>("isNotMoveCameraPosition") ?? false) ? 1 : 0, v => it["isNotMoveCameraPosition"] = v == 1);
            }

            if (tn == "CRAFormatDataSuperJumpBody")
            {
                AddSectionHeader("大跳曲线");
                var easeOpts = ChartConstants.EaseOptions.Select((s, i) => (i + 1, s)).ToArray();
                AddComboField("上升缓动 FirstEase", easeOpts, it.Value<int?>("FirstEase") ?? 3, v => it["FirstEase"] = v);
                AddComboField("下落缓动 SecondEase", easeOpts, it.Value<int?>("SecondEase") ?? 12, v => it["SecondEase"] = v);
                var div = FindDivider(it);
                if (div != null)
                    AddNumericField("跳跃高度 MaxHeight（分隔点）", div.Value<double?>("MaxHeight") ?? 6.0, v => div["MaxHeight"] = Math.Clamp(v, 0.5, 20));
            }

            if (tn == "CRAFormatDataNoteOrb")
            {
                AddSectionHeader("长条中段判定");
                int count = (it["LongMiddleIds"] as JArray)?.Count ?? 0;
                AddReadonlyField($"当前中段节点：{count} 个");
                var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var btnFix = new Button { Content = "补全（每拍）", FontSize = 11, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 4, 0) };
                btnFix.Click += (_, _) => HandleContextAction("addmiddle", new[] { Items.IndexOf(it) });
                var btnClear = new Button { Content = "清除中段", FontSize = 11, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(4, 0, 0, 0) };
                btnClear.Click += (_, _) => HandleContextAction("clearmiddle", new[] { Items.IndexOf(it) });
                Grid.SetColumn(btnClear, 1);
                row.Children.Add(btnFix);
                row.Children.Add(btnClear);
                InspectorPanel.Children.Add(row);
            }
        }

        private void BuildEventProperties(JObject it, string tn)
        {
            switch (tn)
            {
                case "CRAFormatDataEventBPMBar":
                    AddNumericField("BPM", it.Value<double?>("fBPM") ?? 120.0, v => it["fBPM"] = Math.Clamp(v, 20, 400));
                    AddNumericField("基准拍基 fBPMBase", it.Value<double?>("fBPMBase") ?? 4.0, v => it["fBPMBase"] = Math.Clamp(v, 1, 16));
                    AddNumericField("拍号 iBPMBeat（每小节拍数）", it.Value<int?>("iBPMBeat") ?? 4, v => it["iBPMBeat"] = (int)Math.Clamp(v, 1, 16));
                    break;
                case "CRAFormatDataEventSpeed":
                    AddNumericField("卷轴速度倍率 (0.01–999)", it.Value<double?>("fSpeedValue") ?? 1.2, v => it["fSpeedValue"] = Math.Clamp(v, 0.01, 999.0));
                    break;
                case "CRAFormatDataFFChangeTheme":
                    AddIndexedComboField("场景主题（越界会被游戏回退）", ChartConstants.ThemeOptions.Select(x => x.label).ToArray(),
                        IndexOfTheme(it.Value<int?>("eFFTheme") ?? 1), v => it["eFFTheme"] = ChartConstants.ThemeOptions[v].val);
                    break;
                case "CRAFormatDataFFChangeGroundPreset":
                    AddIndexedComboField("地面样式", ChartConstants.GroundOptions, it.Value<int?>("eFFGroundPreset") ?? 0, v => it["eFFGroundPreset"] = v);
                    break;
                case "CRAFormatDataFFChangeCameraPreset":
                    AddIndexedComboField("镜头预设", ChartConstants.CameraOptions, it.Value<int?>("eFFCameraPreset") ?? 0, v => it["eFFCameraPreset"] = v);
                    AddNumericField("自定义水平偏移 X (-30~+30)", it.Value<double?>("camOffsetX") ?? 0.0, v => it["camOffsetX"] = Math.Clamp(v, -30.0, 30.0));
                    AddNumericField("自定义垂直偏移 Y (+往上飞/-往下)", it.Value<double?>("camOffsetY") ?? 0.0, v => it["camOffsetY"] = Math.Clamp(v, -30.0, 30.0));
                    AddNumericField("推拉缩放 Z (+拉近/-拉远)", it.Value<double?>("camOffsetZ") ?? 0.0, v => it["camOffsetZ"] = Math.Clamp(v, -20.0, 20.0));
                    AddIndexedComboField("过渡缓动曲线 (配合上方持续时间控制快慢)", ChartConstants.CameraEaseOptions, Math.Clamp(it.Value<int?>("camEase") ?? 0, 0, ChartConstants.CameraEaseOptions.Length - 1), v => it["camEase"] = v);
                    break;
                case "CRAFormatDataFFChangeAnglePreset":
                    AddIndexedComboField("轨道倾角", ChartConstants.AngleOptions, it.Value<int?>("eFFAnglePreset") ?? 0, v => it["eFFAnglePreset"] = v);
                    break;
                case "CRAFormatDataFFChangeMoodPreset":
                    AddIndexedComboField("氛围", ChartConstants.MoodOptions, it.Value<int?>("eFFMoodPreset") ?? 1, v => it["eFFMoodPreset"] = v);
                    break;
                case "CRAFormatDataFFChangeSpeedPreset":
                    AddIndexedComboField("冲刺特效", ChartConstants.SpeedPresetOptions, it.Value<int?>("eFFSpeedPreset") ?? 0, v => it["eFFSpeedPreset"] = v);
                    break;
                case "CRAFormatDataFFNPCAction":
                    AddIndexedComboField("NPC 动作", ChartConstants.NPCActionOptions, it.Value<int?>("eFFNPCAction") ?? 0, v => it["eFFNPCAction"] = v);
                    AddNumericField("NPC 编号（1–5）", it.Value<int?>("iTrackIndexStart") ?? 1, v =>
                    {
                        int n = (int)Math.Clamp(v, 1, 5);
                        it["iTrackIndexStart"] = n;
                        it["iTrackIndexEnd"] = n;
                    });
                    break;
                case "CRAFormatDataStopMap":
                    AddReadonlyField($"卷轴在 {it.GetTime():F2}s–{it.GetEndTime():F2}s 之间冻结");
                    break;
                case "CRAFormatDataEndOfTrack":
                    AddReadonlyField("标记曲目结束。游戏会在此时间之后收尾。");
                    break;
            }
        }

        private static int IndexOfTheme(int theme)
        {
            for (int i = 0; i < ChartConstants.ThemeOptions.Length; i++)
                if (ChartConstants.ThemeOptions[i].val == theme) return i;
            return 1; // 默认奥兹森林
        }

        private void AddSectionHeader(string text)
        {
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = (Brush)FindResource("AccentGold"),
                Margin = new Thickness(0, 16, 0, 6),
                FontWeight = FontWeights.SemiBold
            });
        }

        private void AddReadonlyField(string text)
        {
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11,
                LineHeight = 18,
                Foreground = (Brush)FindResource("TextDim"),
                Margin = new Thickness(0, 4, 0, 4),
                TextWrapping = TextWrapping.Wrap
            });
        }

        private void AddNumericField(string label, double val, Action<double> apply)
        {
            InspectorPanel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 0, 0, 5) });
            var tb = new TextBox { Text = val.ToString("0.####", CultureInfo.InvariantCulture), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 14) };
            string committed = tb.Text;
            void Commit()
            {
                if (tb.Text == committed) return;
                if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    PushUndo();
                    apply(v);
                    committed = tb.Text;
                    UpdateStats();
                    Timeline.InvalidateVisual();
                    InvalidatePreview();
                }
            }
            tb.LostFocus += (_, _) => Commit();
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            InspectorPanel.Children.Add(tb);
        }

        private void AddIndexedComboField(string label, string[] options, int curVal, Action<int> apply)
        {
            AddComboField(label, options.Select((l, i) => (i, l)).ToArray(), curVal, apply);
        }

        private void AddComboField(string label, (int val, string text)[] options, int curVal, Action<int> apply)
        {
            InspectorPanel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 0, 0, 5) });
            var cb = new ComboBox { Margin = new Thickness(0, 0, 0, 14) };
            int selIdx = 0;
            for (int i = 0; i < options.Length; i++)
            {
                cb.Items.Add(new ComboBoxItem { Content = options[i].text, Tag = options[i].val });
                if (options[i].val == curVal) selIdx = i;
            }
            cb.SelectedIndex = selIdx;
            cb.SelectionChanged += (_, _) =>
            {
                if (cb.SelectedItem is ComboBoxItem ci && ci.Tag is int v)
                {
                    PushUndo();
                    apply(v);
                    UpdateStats();
                    Timeline.InvalidateVisual();
                    InvalidatePreview();
                }
            };
            InspectorPanel.Children.Add(cb);
        }

        private static string PrettyType(string tn) => tn.Replace("CRAFormatData", "");

        // ---------------- 工具栏与按钮 ----------------
        private void HighlightToolButtons()
        {
            foreach (var tgl in new[] { TglSelect, TglCrystal, TglDodge, TglOrb, TglMultiHit, TglCoin, TglSuperJump, TglEvent })
                tgl.IsChecked = tgl.Tag?.ToString() == _tool.ToString();
        }

        private void BtnTool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ButtonBase b && Enum.TryParse<EditorTool>(b.Tag?.ToString(), out var t))
            {
                _tool = t;
                HighlightToolButtons();
            }
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e) => TogglePlay();

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _fmod.Pause();
            _fmod.StopAllSfx();
            _officialAudio.StopGameHits();
            _officialAudio.Stop();
            BtnPlay.Content = "▶ 播放";
            SeekTo(0);
            Timeline.ViewStartSec = 0;
        }

        private void BtnRewind_Click(object sender, RoutedEventArgs e) => SeekTo(0);

        private void BtnUndo_Click(object sender, RoutedEventArgs e) => DoUndo();
        private void BtnRedo_Click(object sender, RoutedEventArgs e) => DoRedo();
        private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveCurrent();
        private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e) => DeleteSelected();
        private void BtnFlipLanes_Click(object sender, RoutedEventArgs e) => FlipSelectedLanes();
        private void BtnDiff_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string diff && diff != _currentDiff)
            {
                if (!ConfirmDiscardOrSave()) return;
                LoadDifficulty(diff);
            }
        }
        private void CmbSnap_SelectionChanged(object sender, SelectionChangedEventArgs e) => Timeline?.InvalidateVisual();
        private void CmbPreviewGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => InvalidatePreview();

        private void CmbRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbRate.SelectedItem is ComboBoxItem ci && float.TryParse(ci.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r))
            {
                _fmod.SetPitch(r);
                _officialAudio.SetPitch(r);
            }
        }

        private void SldZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Timeline != null)
            {
                Timeline.PxPerSec = e.NewValue;
                Timeline.InvalidateVisual();
                InvalidatePreview();
            }
        }

        private void BtnLoopAB_Click(object sender, RoutedEventArgs e)
        {
            if (!_loopA.HasValue)
            {
                _loopA = _playTime;
                _loopB = null;
                BtnLoopAB.IsChecked = true;
                SetStatus($"已设定循环起点 A = {_playTime:F2}s，再点击设定 B");
            }
            else if (!_loopB.HasValue)
            {
                if (_playTime > _loopA.Value + 0.2)
                {
                    _loopB = _playTime;
                    BtnLoopAB.IsChecked = true;
                    SetStatus($"循环区间 {_loopA.Value:F2}s – {_loopB.Value:F2}s");
                }
                else
                {
                    SetStatus("结束点 B 必须在开始点 A 之后");
                }
            }
            else
            {
                _loopA = null;
                _loopB = null;
                BtnLoopAB.IsChecked = false;
                SetStatus("已清除 A-B 循环");
            }
            Timeline.InvalidateVisual();
        }

        private void BtnSync_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSong == null) return;
            if (_currentSong.IsOfficial)
            {
                SetStatus("官方曲目副本只保存在本地工作区 OfficialPlayground，不会写入游戏");
                return;
            }
            if (!GameLocator.HasGame)
            {
                SetStatus("未找到游戏目录：请打开 设置 → 文件位置 选择 Liora 游戏文件夹");
                return;
            }
            SaveCurrent();
            ChartStorage.SyncToGame(_currentSong.FolderPath, _currentSong.FolderName);
            SetStatus($"已同步至游戏目录：{ChartStorage.GameCustomChartsDir}\\{_currentSong.FolderName}");
            RefreshSongListOnly();
        }

        private void BtnNewSong_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new NewSongDialog { Owner = Window.GetWindow(sender as DependencyObject) ?? this };
            if (dlg.ShowDialog() == true)
            {
                var entry = ChartStorage.CreateNewSong(dlg.SongId, dlg.SongTitle, dlg.Artist, dlg.Bpm);
                if (!string.IsNullOrEmpty(dlg.AudioSourcePath) && File.Exists(dlg.AudioSourcePath))
                {
                    try
                    {
                        string ext = Path.GetExtension(dlg.AudioSourcePath).ToLowerInvariant();
                        string destName = "song" + ext;
                        SafeCopyFile(dlg.AudioSourcePath, Path.Combine(entry.FolderPath, destName));
                        entry.Meta.audioFile = destName;
                        ChartStorage.SaveMeta(entry.FolderPath, entry.Meta);
                    }
                    catch (Exception ex)
                    {
                        SetStatus("新建曲目成功，但导入音频失败：" + ex.Message);
                    }
                }
                RefreshSongs(entry.FolderName);
                SetStatus($"已新建曲目：{entry.Meta.title}");
            }
        }

        private void BtnOfficialLibrary_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OfficialLibraryWindow { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                SetStatus($"已导入 {dlg.ImportedCount} 首官方曲目到本地工作区（不会写入游戏）");
                RefreshSongs();
            }
        }

        private void BtnRestoreOfficial_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSong?.IsOfficial != true || string.IsNullOrEmpty(_currentSong.Meta.officialKey)) return;
            var result = MessageBox.Show(this,
                "恢复官方原版会覆盖该曲目工作区里的本地修改，继续？",
                "恢复官方原版", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            if (OfficialLibrary.RestoreTrack(_currentSong.Meta.officialKey))
            {
                SetStatus("已恢复官方原版");
                RefreshSongs(_currentSong.FolderName);
            }
        }

        private static void SafeCopyFile(string src, string dst)
        {
            if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                File.Copy(src, dst, true);
        }

        private async void BtnImportAudio_Click(object sender, RoutedEventArgs e)
        {
            var owner = Window.GetWindow(sender as DependencyObject) ?? this;
            if (_currentSong == null)
            {
                var ask = MessageBox.Show(owner,
                    "当前未选中任何曲目，无法导入音频。\n是否立即新建一首自定义曲目？",
                    "请先选择或新建曲目", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (ask == MessageBoxResult.Yes) BtnNewSong_Click(sender, e);
                return;
            }
            var dlg = new OpenFileDialog
            {
                Title = "选择曲目音频（支持 OGG / MP3 / WAV / FLAC）",
                Filter = "支持的音频文件 (*.ogg;*.mp3;*.wav;*.flac)|*.ogg;*.mp3;*.wav;*.flac|OGG 音频|*.ogg|MP3 音频|*.mp3|WAV 无损|*.wav|FLAC 无损|*.flac"
            };
            if (dlg.ShowDialog(owner) == true)
            {
                try
                {
                    // 先释放当前 FMOD 流句柄，防止覆盖同名 song.mp3/ogg 时文件被占用
                    _fmod.UnloadSong();
                    string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                    string destName = "song" + ext;
                    SafeCopyFile(dlg.FileName, Path.Combine(_currentSong.FolderPath, destName));
                    _currentSong.Meta.audioFile = destName;
                    ChartStorage.SaveMeta(_currentSong.FolderPath, _currentSong.Meta);
                    RefreshSongListOnly();
                    await LoadAudioForCurrentSongAsync();
                    SetStatus($"已导入音频：{destName}");
                }
                catch (Exception ex) { SetStatus("导入音频失败：" + ex.Message); }
            }
        }

        private void BtnImportCover_Click(object sender, RoutedEventArgs e)
        {
            var owner = Window.GetWindow(sender as DependencyObject) ?? this;
            if (_currentSong == null)
            {
                MessageBox.Show(owner, "请先在左侧新建或选择一首曲目，再导入封面。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new OpenFileDialog { Title = "选择封面图片", Filter = "图片文件|*.png;*.jpg;*.jpeg" };
            if (dlg.ShowDialog(owner) == true)
            {
                try
                {
                    string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                    string destName = "cover" + ext;
                    SafeCopyFile(dlg.FileName, Path.Combine(_currentSong.FolderPath, destName));
                    _currentSong.Meta.coverFile = destName;
                    ChartStorage.SaveMeta(_currentSong.FolderPath, _currentSong.Meta);
                    UpdateCoverPreview();
                    RefreshSongListOnly();
                    SetStatus($"已导入封面：{destName}");
                }
                catch (Exception ex) { SetStatus("导入封面失败：" + ex.Message); }
            }
        }

        private double GetCurrentPlayRate() =>
            (CmbRate?.SelectedItem is ComboBoxItem ci && double.TryParse(ci.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double r)) ? r : 1.0;

        private bool _hasCustomVideoBg;
        private bool _customVideoPlaying;
        private long _lastVideoSeekMs;

        private static bool IsVideoExtension(string ext) =>
            ext == ".mp4" || ext == ".webm" || ext == ".mov";

        private void UnloadCustomBg()
        {
            _hasCustomVideoBg = false;
            _customVideoPlaying = false;
            try
            {
                PreviewCustomBgVideo.Stop();
                PreviewCustomBgVideo.Source = null;
            }
            catch { }
            PreviewCustomBgVideo.Visibility = Visibility.Collapsed;
            PreviewCustomBgImage.Source = null;
            PreviewCustomBgImage.Visibility = Visibility.Collapsed;
            PreviewCustomBgDim.Visibility = Visibility.Collapsed;
            if (PreviewViewportGuide != null) PreviewViewportGuide.Visibility = Visibility.Collapsed;
        }

        private void UpdateViewportGuide()
        {
            if (PreviewViewportGuide == null || PreviewStageLayer == null) return;
            bool hasCustom = PreviewCustomBgImage.Visibility == Visibility.Visible || PreviewCustomBgVideo.Visibility == Visibility.Visible;
            if (!hasCustom)
            {
                PreviewViewportGuide.Visibility = Visibility.Collapsed;
                return;
            }
            double w = PreviewStageLayer.ActualWidth;
            double h = PreviewStageLayer.ActualHeight;
            if (w <= 10 || h <= 10) return;

            double targetW = w;
            double targetH = w * 9.0 / 16.0;
            if (targetH > h)
            {
                targetH = h;
                targetW = h * 16.0 / 9.0;
            }
            PreviewViewportGuide.Width = Math.Max(40, targetW - 2);
            PreviewViewportGuide.Height = Math.Max(40, targetH - 2);
            PreviewViewportGuide.Visibility = Visibility.Visible;
        }

        private void ApplyCustomBgTransform()
        {
            if (SldBgScale == null || SldBgPosX == null || SldBgPosY == null) return;
            double scale = Math.Clamp(SldBgScale.Value / 100.0, 0.5, 2.5);
            double px = Math.Clamp(SldBgPosX.Value, -100.0, 100.0);
            double py = Math.Clamp(SldBgPosY.Value, -100.0, 100.0);

            double refW = PreviewStageLayer?.ActualWidth > 10 ? PreviewStageLayer.ActualWidth : 960;
            double refH = PreviewStageLayer?.ActualHeight > 10 ? PreviewStageLayer.ActualHeight : 540;
            double tx = (px / 100.0) * refW * 0.5;
            double ty = -(py / 100.0) * refH * 0.5;

            var tg = new TransformGroup();
            tg.Children.Add(new ScaleTransform(scale, scale));
            tg.Children.Add(new TranslateTransform(tx, ty));
            tg.Freeze();

            if (PreviewCustomBgImage != null) PreviewCustomBgImage.RenderTransform = tg;
            if (PreviewCustomBgVideo != null) PreviewCustomBgVideo.RenderTransform = tg;
        }

        private void PreviewStageLayer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateViewportGuide();
            ApplyCustomBgTransform();
        }

        private void SldBgTransform_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtBgScale == null || TxtBgPosX == null || TxtBgPosY == null) return;
            TxtBgScale.Text = $"{(int)Math.Round(SldBgScale.Value)}%";
            TxtBgPosX.Text = $"{(int)Math.Round(SldBgPosX.Value)}%";
            TxtBgPosY.Text = $"{(int)Math.Round(SldBgPosY.Value)}%";
            ApplyCustomBgTransform();
            if (_loadingSong || _currentSong?.Meta == null) return;
            _currentSong.Meta.bgScale = (float)Math.Clamp(SldBgScale.Value / 100.0, 0.5, 2.5);
            _currentSong.Meta.bgPosX = (float)Math.Clamp(SldBgPosX.Value, -100.0, 100.0);
            _currentSong.Meta.bgPosY = (float)Math.Clamp(SldBgPosY.Value, -100.0, 100.0);
            MarkDirty();
        }

        private void BtnResetBgTransform_Click(object sender, RoutedEventArgs e)
        {
            SldBgScale.Value = 100;
            SldBgPosX.Value = 0;
            SldBgPosY.Value = 0;
            ApplyCustomBgTransform();
        }

        private void SldBgmGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtBgmGain == null) return;
            TxtBgmGain.Text = $"{(int)Math.Round(SldBgmGain.Value)}%";
            ApplySongVolume();
            if (_loadingSong || _currentSong?.Meta == null) return;
            _currentSong.Meta.bgmGain = (float)Math.Clamp(SldBgmGain.Value / 100.0, 0.2, 3.0);
            MarkDirty();
        }

        private void ApplySongVolume()
        {
            float gain = (_currentSong?.Meta != null && _currentSong.Meta.bgmGain > 0.01f)
                ? Math.Clamp(_currentSong.Meta.bgmGain, 0.2f, 3.0f)
                : (SldBgmGain != null ? (float)Math.Clamp(SldBgmGain.Value / 100.0, 0.2, 3.0) : 1.0f);
            _fmod.SetVolume((float)EditorSettings.Current.MasterVolume * gain);
        }

        private void LoadCustomBgForCurrentSong()
        {
            UnloadCustomBg();
            if (_currentSong?.Meta == null)
            {
                TxtBgStatus.Text = "背景：默认主题";
                BtnClearBg.IsEnabled = false;
                return;
            }
            var m = _currentSong.Meta;
            string bgFile = m.bgFile;
            string fullPath = string.IsNullOrEmpty(bgFile) ? null : Path.Combine(_currentSong.FolderPath, bgFile);
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                TxtBgStatus.Text = "背景：默认主题";
                BtnClearBg.IsEnabled = false;
                return;
            }

            BtnClearBg.IsEnabled = true;
            TxtBgStatus.Text = $"背景：{bgFile}";
            PreviewCustomBgDim.Opacity = Math.Clamp(m.bgDim, 0f, 0.9f);
            PreviewCustomBgDim.Visibility = Visibility.Visible;

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (IsVideoExtension(ext))
            {
                try
                {
                    _hasCustomVideoBg = true;
                    PreviewCustomBgVideo.Source = new Uri(fullPath, UriKind.Absolute);
                    PreviewCustomBgVideo.Visibility = Visibility.Visible;
                    PreviewCustomBgVideo.SpeedRatio = GetCurrentPlayRate();
                    PreviewCustomBgVideo.Play();
                    PreviewCustomBgVideo.Pause();
                    SyncCustomBgVideo(false, true);
                    UpdateViewportGuide();
                    ApplyCustomBgTransform();
                    AppLog.Info($"Loaded custom video bg: {fullPath}");
                }
                catch (Exception ex)
                {
                    AppLog.Error("Failed to load custom video bg", ex);
                    UnloadCustomBg();
                    TxtBgStatus.Text = "背景：视频加载失败";
                }
            }
            else
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 1920;
                    bmp.UriSource = new Uri(fullPath, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    PreviewCustomBgImage.Source = bmp;
                    PreviewCustomBgImage.Visibility = Visibility.Visible;
                    UpdateViewportGuide();
                    ApplyCustomBgTransform();
                    AppLog.Info($"Loaded custom image bg: {fullPath}");
                }
                catch (Exception ex)
                {
                    AppLog.Error("Failed to load custom image bg", ex);
                    UnloadCustomBg();
                    TxtBgStatus.Text = "背景：图片加载失败";
                }
            }
        }

        private void SyncCustomBgVideo(bool playing, bool forceSeek)
        {
            if (!_hasCustomVideoBg || _currentSong?.Meta == null) return;
            try
            {
                double targetSec = Math.Max(0.0, _playTime + _currentSong.Meta.bgVideoOffsetSec);
                double baseRate = GetCurrentPlayRate();
                long nowMs = Environment.TickCount64;

                if (playing)
                {
                    if (!_customVideoPlaying)
                    {
                        PreviewCustomBgVideo.SpeedRatio = baseRate;
                        PreviewCustomBgVideo.Play();
                        _customVideoPlaying = true;
                        double initCur = PreviewCustomBgVideo.Position.TotalSeconds;
                        if (forceSeek || Math.Abs(initCur - targetSec) > 0.35)
                        {
                            _lastVideoSeekMs = nowMs;
                            PreviewCustomBgVideo.Position = TimeSpan.FromSeconds(targetSec);
                        }
                        return;
                    }

                    double cur = PreviewCustomBgVideo.Position.TotalSeconds;
                    double diff = targetSec - cur;
                    double absDiff = Math.Abs(diff);

                    if (forceSeek)
                    {
                        _lastVideoSeekMs = nowMs;
                        PreviewCustomBgVideo.SpeedRatio = baseRate;
                        PreviewCustomBgVideo.Position = TimeSpan.FromSeconds(targetSec);
                    }
                    else if (absDiff <= 0.25)
                    {
                        if (Math.Abs(PreviewCustomBgVideo.SpeedRatio - baseRate) > 0.01)
                            PreviewCustomBgVideo.SpeedRatio = baseRate;
                    }
                    else if (absDiff <= 0.75)
                    {
                        // 温和倍速微调追帧，避免 4K H.265 频繁硬 Seek 卡死 UI
                        double adjRate = Math.Clamp(baseRate * (diff > 0 ? 1.06 : 0.94), 0.1, 4.0);
                        if (Math.Abs(PreviewCustomBgVideo.SpeedRatio - adjRate) > 0.01)
                            PreviewCustomBgVideo.SpeedRatio = adjRate;
                    }
                    else if (nowMs - _lastVideoSeekMs >= 1800)
                    {
                        _lastVideoSeekMs = nowMs;
                        PreviewCustomBgVideo.SpeedRatio = baseRate;
                        PreviewCustomBgVideo.Position = TimeSpan.FromSeconds(targetSec);
                    }
                }
                else
                {
                    if (Math.Abs(PreviewCustomBgVideo.SpeedRatio - baseRate) > 0.01)
                        PreviewCustomBgVideo.SpeedRatio = baseRate;
                    if (_customVideoPlaying)
                    {
                        PreviewCustomBgVideo.Pause();
                        _customVideoPlaying = false;
                    }
                    if (forceSeek && nowMs - _lastVideoSeekMs >= 120)
                    {
                        _lastVideoSeekMs = nowMs;
                        PreviewCustomBgVideo.Position = TimeSpan.FromSeconds(targetSec);
                    }
                }
            }
            catch { }
        }

        private void PreviewCustomBgVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            AppLog.Error("PreviewCustomBgVideo MediaFailed", e?.ErrorException);
            UnloadCustomBg();
            TxtBgStatus.Text = "背景：视频编码不支持";
            MessageBox.Show(this,
                "当前系统无法解码该视频格式（常见于 4K HEVC/H.265 或 AV1 未安装系统解码扩展）。\n\n建议转换为 H.264 (AVC) 编码的 MP4 后重新导入。",
                "视频解码失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async void BtnImportBg_Click(object sender, RoutedEventArgs e)
        {
            var owner = Window.GetWindow(sender as DependencyObject) ?? this;
            if (_currentSong == null)
            {
                MessageBox.Show(owner, "请先在左侧新建或选择一首曲目，再导入自定义背景。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new OpenFileDialog
            {
                Title = "选择自定义背景（支持 4K 图片/视频）",
                Filter = "图片与视频|*.png;*.jpg;*.jpeg;*.bmp;*.mp4;*.webm;*.mov|图片文件|*.png;*.jpg;*.jpeg;*.bmp|视频文件|*.mp4;*.webm;*.mov"
            };
            if (dlg.ShowDialog(owner) == true)
            {
                try
                {
                    UnloadCustomBg();
                    string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                    string destName = "bg" + ext;
                    string destPath = Path.Combine(_currentSong.FolderPath, destName);
                    SafeCopyFile(dlg.FileName, destPath);
                    _currentSong.Meta.bgFile = destName;
                    _currentSong.Meta.bgDim = (float)Math.Clamp(SldBgDim.Value / 100.0, 0.0, 0.9);
                    ChartStorage.SaveMeta(_currentSong.FolderPath, _currentSong.Meta);
                    LoadCustomBgForCurrentSong();
                    SetStatus($"已导入自定义背景：{destName}");

                    if (IsVideoExtension(ext))
                    {
                        var ask = MessageBox.Show(owner,
                            "是否同时提取该视频中的音频作为当前曲目的 BGM？",
                            "提取视频音频", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (ask == MessageBoxResult.Yes)
                        {
                            SetStatus("正在从视频提取音轨...");
                            _fmod.UnloadSong();
                            var (wav, dur) = await System.Threading.Tasks.Task.Run(() =>
                            {
                                byte[] bytes = Audio.MediaFoundationDecoder.DecodeToWav48k16BitStereo(destPath, out double d);
                                return (bytes, d);
                            });

                            if (wav != null && wav.Length > 44)
                            {
                                string wavPath = Path.Combine(_currentSong.FolderPath, "song.wav");
                                await File.WriteAllBytesAsync(wavPath, wav);
                                _currentSong.Meta.audioFile = "song.wav";
                                ChartStorage.SaveMeta(_currentSong.FolderPath, _currentSong.Meta);
                                RefreshSongListOnly();
                                await LoadAudioForCurrentSongAsync();
                                SetStatus($"已导入背景并提取音频：song.wav ({dur:F1}s)");
                                AppLog.Info($"Extracted audio from video bg: {destPath} -> song.wav ({dur:F2}s)");
                            }
                            else
                            {
                                AppLog.Warn($"No usable audio track extracted from video: {destPath}");
                                MessageBox.Show(owner,
                                    "未在该视频中检测到可用音轨（或音频格式不支持），已仅导入背景画面。",
                                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                                SetStatus($"已导入背景：{destName}（无可用音轨）");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("BtnImportBg_Click failed", ex);
                    SetStatus("导入背景失败：" + ex.Message);
                }
            }
        }

        private void BtnClearBg_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSong?.Meta == null) return;
            UnloadCustomBg();
            _currentSong.Meta.bgFile = "";
            ChartStorage.SaveMeta(_currentSong.FolderPath, _currentSong.Meta);
            LoadCustomBgForCurrentSong();
            SetStatus("已恢复默认场景背景");
        }

        private void SldBgDim_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtBgDim == null) return;
            int pct = (int)Math.Round(SldBgDim.Value);
            TxtBgDim.Text = $"{pct}%";
            PreviewCustomBgDim.Opacity = Math.Clamp(pct / 100.0, 0.0, 0.9);
            if (_loadingSong || _currentSong?.Meta == null) return;
            _currentSong.Meta.bgDim = (float)Math.Clamp(pct / 100.0, 0.0, 0.9);
            MarkDirty();
        }

        private void TxtBgOffset_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loadingSong || _currentSong?.Meta == null) return;
            if (float.TryParse(TxtBgOffset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            {
                _currentSong.Meta.bgVideoOffsetSec = v;
                SyncCustomBgVideo(_wasPlaying, true);
                MarkDirty();
            }
        }

        private void CmbDefaultBeat_Changed(object sender, EventArgs e)
        {
            if (_loadingSong || _currentSong?.Meta == null) return;
            string raw = (CmbDefaultBeat.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? CmbDefaultBeat.Text;
            if (int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int beat))
            {
                beat = Math.Clamp(beat, 1, 16);
                _currentSong.Meta.defaultBeat = beat;
                TxtGridBeatsPerBar.Text = beat.ToString(CultureInfo.InvariantCulture);
                Timeline.InvalidateVisual();
                Playfield.InvalidateVisual();
                MarkDirty();
            }
        }

        private void BtnImportOfficial_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSong == null || CmbOfficial.SelectedItem is not string name) return;
            var loaded = ChartStorage.LoadOfficialChart(name);
            if (loaded == null) return;
            PushUndo();
            _chart = loaded;
            Timeline.SelectedIndices.Clear();
            BuildInspector();
            UpdateStats();
            SetStatus($"已导入官方参考谱面：{name}");
        }

        private void BtnAssets_Click(object sender, RoutedEventArgs e)
        {
            var win = new GameAssetsWindow
            {
                Owner = this,
                OnUseAsCover = path =>
                {
                    if (_currentSong == null) return;
                    SafeCopyFile(path, Path.Combine(_currentSong.FolderPath, "cover.png"));
                    _currentSong.Meta.coverFile = "cover.png";
                    ChartStorage.SaveMeta(_currentSong.FolderPath, _currentSong.Meta);
                    UpdateCoverPreview();
                    RefreshSongListOnly();
                    SetStatus($"已将游戏素材设为封面：{Path.GetFileName(path)}");
                },
                OnUseAsBackground = path =>
                {
                    Playfield.SetCustomBackground(path);
                    SetStatus($"已切换预览背景：{Path.GetFileName(path)}");
                }
            };
            win.Show();
        }

        private void BtnTapBpm_Click(object sender, RoutedEventArgs e)
        {
            long now = Environment.TickCount64;
            if (_tapTimesMs.Count > 0 && now - _tapTimesMs[^1] > 2000) _tapTimesMs.Clear();
            _tapTimesMs.Add(now);
            if (_tapTimesMs.Count >= 2)
            {
                double avg = (_tapTimesMs[^1] - _tapTimesMs[0]) / (double)(_tapTimesMs.Count - 1);
                double bpm = Math.Round(60000.0 / avg, 1);
                TxtBpm.Text = bpm.ToString(CultureInfo.InvariantCulture);
                if (Items != null)
                {
                    var first = Items.OfType<JObject>().FirstOrDefault(o => o.GetTypeName() == "CRAFormatDataEventBPMBar");
                    if (first != null) first["fBPM"] = bpm;
                }
                SetStatus($"测算 BPM：{bpm}");
            }
        }

        private void BtnSetEndOfTrack_Click(object sender, RoutedEventArgs e)
        {
            if (Items == null) return;
            if (!EnsureActiveLayerEditable()) return;
            PushUndo();
            var eot = Items.OfType<JObject>().FirstOrDefault(o => o.GetTypeName() == "CRAFormatDataEndOfTrack");
            if (eot == null)
            {
                eot = Timeline.CreateEventItem("CRAFormatDataEndOfTrack", _playTime - _offsetSec, TimelineControl.MaxId(Items) + 1);
                Items.Add(eot);
            }
            else
            {
                eot.SetTime(_playTime - _offsetSec);
            }
            UpdateStats();
            SetStatus($"曲终标记已设在 {_playTime:F2}s");
        }

        private void BtnAddEventAtPlayhead_Click(object sender, RoutedEventArgs e)
        {
            if (Items == null) return;
            if (!EnsureActiveLayerEditable()) return;
            var ev = Timeline.CreateEventItem(GetSelectedEventType(), Timeline.SnapTime(_playTime) - _offsetSec, TimelineControl.MaxId(Items) + 1);
            if (ev != null)
            {
                PushUndo();
                Items.Add(ev);
                Timeline.SelectedIndices.Clear();
                Timeline.SelectedIndices.Add(Items.Count - 1);
                BuildInspector();
                UpdateStats();
                SetStatus($"已添加事件：{ChartConstants.FindEvent(ev.GetTypeName())?.Display} @ {ev.GetTime():F2}s");
            }
        }

        // ---------------- 图层 ----------------
        private double ActiveLayerBpm
        {
            get
            {
                if (_activeLayerIndex >= 0 && _activeLayerIndex < _layers.Count)
                {
                    double b = _layers[_activeLayerIndex].Bpm;
                    if (b > 10) return b;
                }
                return 0;
            }
        }

        private static Color ParseLayerColor(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return Color.FromRgb(44, 198, 192); }
        }

        private void BtnAddLayer_Click(object sender, RoutedEventArgs e)
        {
            if (_layers.Count >= 8)
            {
                SetStatus("最多支持 8 个图层");
                return;
            }
            int n = _layers.Count;
            _layers.Add(new ChartLayer
            {
                Name = $"图层 {n + 1}",
                Color = LayerPalette[n % LayerPalette.Length],
                Bpm = 0,
                Speed = 1.0,
                Visible = true
            });
            _activeLayerIndex = n;
            BuildLayersPanel();
            Timeline?.InvalidateVisual();
            InvalidatePreview();
            UpdateStats();
            MarkDirty();
            SetStatus($"已添加图层 {n + 1}");
        }

        private void BtnAddAngledLayer_Click(object sender, RoutedEventArgs e)
        {
            if (_layers.Count >= 8)
            {
                SetStatus("最多支持 8 个图层");
                return;
            }
            int n = _layers.Count;
            double[] presets = { 30.0, -30.0, 45.0, -45.0, 20.0, -20.0 };
            double ang = presets[Math.Max(0, n - 1) % presets.Length];
            _layers.Add(new ChartLayer
            {
                Name = $"斜轨 {(ang >= 0 ? "+" : "")}{ang:0}°",
                Color = LayerPalette[n % LayerPalette.Length],
                Bpm = 0,
                Speed = 1.0,
                Angle = ang,
                Visible = true
            });
            _activeLayerIndex = n;
            BuildLayersPanel();
            Timeline?.InvalidateVisual();
            InvalidatePreview();
            UpdateStats();
            MarkDirty();
            SetStatus($"已添加斜行轨道图层（{ang:+0;-0;0}°），音符按垂直投影对齐人物直线");
        }

        private void ChkProjOption_Click(object sender, RoutedEventArgs e)
        {
            ApplyProjectionVisibility();
            InvalidatePreview();
            PersistUiSettings();
        }

        private void ApplyProjectionVisibility()
        {
            bool showBox = ChkProjBox?.IsChecked == true;
            if (ProjectionHost != null && ProjectionSplitter != null && ProjectionRow != null)
            {
                ProjectionHost.Visibility = showBox ? Visibility.Visible : Visibility.Collapsed;
                ProjectionSplitter.Visibility = showBox ? Visibility.Visible : Visibility.Collapsed;
                ProjectionRow.MinHeight = showBox ? 72 : 0;
                ProjectionRow.Height = showBox ? new GridLength(96) : new GridLength(0);
            }
        }

        private void RemoveLayer(int idx)
        {
            if (_layers.Count <= 1)
            {
                SetStatus("至少保留一个图层");
                return;
            }
            if (IsLayerLocked(idx))
            {
                SetStatus("该图层已锁定，请先解锁");
                return;
            }
            PushUndo();
            if (Items != null)
            {
                foreach (var tok in Items.OfType<JObject>())
                {
                    int li = tok.GetEditorLayer();
                    if (li == idx) tok["editorLayer"] = 0;
                    else if (li > idx) tok["editorLayer"] = li - 1;
                }
            }
            _layers.RemoveAt(idx);
            if (_activeLayerIndex >= _layers.Count) _activeLayerIndex = _layers.Count - 1;
            BuildLayersPanel();
            BuildInspector();
            UpdateStats();
            Timeline?.InvalidateVisual();
            InvalidatePreview();
            MarkDirty();
        }

        private bool IsLayerLocked(int idx)
        {
            return idx >= 0 && idx < _layers.Count && _layers[idx].Locked;
        }

        private bool IsObjectLocked(JObject o)
        {
            return o != null && IsLayerLocked(o.GetEditorLayer());
        }

        private bool ActiveLayerLocked => IsLayerLocked(_activeLayerIndex);

        private bool EnsureActiveLayerEditable()
        {
            if (!ActiveLayerLocked) return true;
            SetStatus("当前图层已锁定，请先在左侧图层面板点“解锁”");
            return false;
        }

        private bool _soloActiveLayer = true;

        private void ChkSoloLayer_Click(object sender, RoutedEventArgs e)
        {
            _soloActiveLayer = ChkSoloLayer.IsChecked == true;
            if (_soloActiveLayer) CleanTimelineSelectionForActiveLayer();
            BuildLayersPanel();
            Timeline?.InvalidateVisual();
            InvalidatePreview();
        }

        private void CleanTimelineSelectionForActiveLayer()
        {
            if (Timeline == null || Items == null || !_soloActiveLayer) return;
            bool changed = false;
            var toRemove = new List<int>();
            foreach (int si in Timeline.SelectedIndices)
            {
                if (si >= 0 && si < Items.Count && Items[si] is JObject o && o.IsNote())
                {
                    if (o.GetEditorLayer() != _activeLayerIndex)
                    {
                        toRemove.Add(si);
                        changed = true;
                    }
                }
            }
            foreach (int r in toRemove) Timeline.SelectedIndices.Remove(r);
            if (changed)
            {
                Timeline.OnSelectionChanged?.Invoke(Timeline.SelectedIndices.Count > 0 ? Timeline.SelectedIndices.First() : -1);
            }
        }

        private void BuildLayersPanel()
        {
            if (LayersPanel == null) return;
            LayersPanel.Children.Clear();
            for (int i = 0; i < _layers.Count; i++)
            {
                int idx = i;
                var layer = _layers[i];
                bool locked = layer.Locked;
                bool isSelected = idx == _activeLayerIndex;
                var card = new Border
                {
                    Background = (Brush)(TryFindResource(isSelected ? "CardHeaderBg" : "BgCard") ?? FindResource("BgCard")),
                    BorderBrush = (Brush)(TryFindResource(isSelected ? "AccentJump" : (locked ? "AccentGold" : "BorderBrush")) ?? FindResource("BorderBrush")),
                    BorderThickness = new Thickness(isSelected ? 2 : 1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 9, 10, 9),
                    Margin = new Thickness(0, 0, 0, 8),
                    Cursor = Cursors.Hand
                };

                // 整卡点击激活并独占聚焦该图层
                card.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.OriginalSource is DependencyObject dep)
                    {
                        DependencyObject cur = dep;
                        while (cur != null && cur != card)
                        {
                            if (cur is Button || cur is TextBox || cur is Slider || cur is CheckBox)
                                return;
                            cur = VisualTreeHelper.GetParent(cur);
                        }
                    }
                    if (_activeLayerIndex != idx)
                    {
                        _activeLayerIndex = idx;
                        CleanTimelineSelectionForActiveLayer();
                        BuildLayersPanel();
                        Timeline?.InvalidateVisual();
                        InvalidatePreview();
                        UpdateStats();
                    }
                };
                var root = new StackPanel();

                var row1 = new DockPanel();
                var radio = new RadioButton
                {
                    IsChecked = isSelected,
                    GroupName = "chartLayers",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    ToolTip = "设为当前图层（点击整个图层卡片框也可直接切换）"
                };
                radio.Checked += (_, _) =>
                {
                    if (_activeLayerIndex == idx) return;
                    _activeLayerIndex = idx;
                    CleanTimelineSelectionForActiveLayer();
                    BuildLayersPanel();
                    Timeline?.InvalidateVisual();
                    InvalidatePreview();
                    UpdateStats();
                };
                var swatch = new Border
                {
                    Width = 11,
                    Height = 11,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(ParseLayerColor(layer.Color)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                var nameBox = new TextBox
                {
                    Text = layer.Name,
                    FontSize = 11,
                    Padding = new Thickness(6, 3, 6, 3),
                    MinWidth = 82,
                    IsEnabled = !locked,
                    ToolTip = "图层名称"
                };
                nameBox.LostFocus += (_, _) =>
                {
                    string n = nameBox.Text.Trim();
                    if (!string.IsNullOrEmpty(n) && n != layer.Name)
                    {
                        layer.Name = n;
                        MarkDirty();
                        BuildInspector();
                    }
                };
                var lockBtn = new Button
                {
                    Content = locked ? "解锁" : "锁定",
                    Width = 46,
                    Height = 24,
                    FontSize = 10.5,
                    Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = locked ? "点击解锁该图层" : "锁定后该图层与其中音符不可修改"
                };
                lockBtn.Click += (_, _) =>
                {
                    layer.Locked = !layer.Locked;
                    BuildLayersPanel();
                    Timeline?.InvalidateVisual();
                    InvalidatePreview();
                    BuildInspector();
                    MarkDirty();
                };
                var del = new Button
                {
                    Content = "×",
                    Width = 24,
                    Height = 24,
                    FontSize = 12,
                    Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    IsEnabled = !locked,
                    ToolTip = "删除图层（该层音符并入第 1 层）"
                };
                del.Click += (_, _) => RemoveLayer(idx);
                DockPanel.SetDock(del, Dock.Right);
                DockPanel.SetDock(lockBtn, Dock.Right);
                row1.Children.Add(del);
                row1.Children.Add(lockBtn);

                if (isSelected)
                {
                    var tagBadge = new Border
                    {
                        Background = (Brush)(TryFindResource("AccentJump") ?? Brushes.Cyan),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(5, 1, 5, 1),
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "当前正独占聚焦编辑该图层"
                    };
                    var tagText = new TextBlock
                    {
                        Text = _soloActiveLayer ? "独占编辑中" : "当前层",
                        FontSize = 9.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.Black
                    };
                    tagBadge.Child = tagText;
                    DockPanel.SetDock(tagBadge, Dock.Right);
                    row1.Children.Add(tagBadge);
                }

                row1.Children.Add(radio);
                row1.Children.Add(swatch);
                row1.Children.Add(nameBox);
                root.Children.Add(row1);

                var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
                var bpmLbl = new TextBlock { Text = "BPM", FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                bpmLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
                var bpmBox = new TextBox
                {
                    Text = layer.Bpm <= 0 ? "0" : layer.Bpm.ToString("0.##", CultureInfo.InvariantCulture),
                    Width = 48,
                    FontSize = 10.5,
                    Padding = new Thickness(3, 1, 3, 1),
                    IsEnabled = !locked,
                    ToolTip = "0 = 跟随谱面 BPM；填写后该层网格与吸附按此 BPM"
                };
                bpmBox.LostFocus += (_, _) =>
                {
                    if (double.TryParse(bpmBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b >= 0 && b <= 400)
                    {
                        layer.Bpm = b;
                        Timeline?.InvalidateVisual();
                        InvalidatePreview();
                        UpdateStats();
                        MarkDirty();
                    }
                };
                var spdLbl = new TextBlock { Text = "流速", FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) };
                spdLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
                var spdBox = new TextBox
                {
                    Text = layer.Speed.ToString("0.##", CultureInfo.InvariantCulture),
                    Width = 38,
                    FontSize = 10.5,
                    Padding = new Thickness(3, 1, 3, 1),
                    IsEnabled = !locked,
                    ToolTip = "仅影响编辑器预览的滚动速度（0.25–4.0）"
                };
                spdBox.LostFocus += (_, _) =>
                {
                    if (double.TryParse(spdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double sp) && sp >= 0.25 && sp <= 4.0)
                    {
                        layer.Speed = sp;
                        InvalidatePreview();
                        MarkDirty();
                    }
                };
                var vis = new CheckBox
                {
                    Content = "预览",
                    IsChecked = layer.Visible,
                    FontSize = 10.5,
                    IsEnabled = !locked,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                    ToolTip = "仅控制画面预览是否显示该层音符"
                };
                vis.Click += (_, _) =>
                {
                    layer.Visible = vis.IsChecked == true;
                    InvalidatePreview();
                    MarkDirty();
                };
                row2.Children.Add(bpmLbl);
                row2.Children.Add(bpmBox);
                row2.Children.Add(spdLbl);
                row2.Children.Add(spdBox);
                row2.Children.Add(vis);
                root.Children.Add(row2);

                var row3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
                var opLbl = new TextBlock { Text = "透明", FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                opLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
                var opTxt = new TextBlock
                {
                    Text = $"{(int)Math.Round(layer.Opacity * 100)}%",
                    FontSize = 10,
                    Width = 34,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                };
                opTxt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                var opSld = new Slider
                {
                    Minimum = 20,
                    Maximum = 100,
                    Value = Math.Clamp(layer.Opacity * 100, 20, 100),
                    Width = 96,
                    IsEnabled = !locked,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "该图层音符在预览与时间轴中的透明度"
                };
                bool opReady = false;
                opSld.ValueChanged += (_, _) =>
                {
                    if (!opReady) return;
                    layer.Opacity = Math.Clamp(opSld.Value / 100.0, 0.2, 1.0);
                    opTxt.Text = $"{(int)Math.Round(opSld.Value)}%";
                    InvalidatePreview();
                    Timeline?.InvalidateVisual();
                    MarkDirty();
                };
                row3.Children.Add(opLbl);
                row3.Children.Add(opSld);
                row3.Children.Add(opTxt);
                root.Children.Add(row3);
                opReady = true;

                // 实验版：图层轨道来袭方向（-180° ~ +180° 全方向，到达判定线时精确重合）
                var row4 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var angLbl = new TextBlock { Text = "方向", FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                angLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
                var angBox = new TextBox
                {
                    Text = $"{Math.Clamp(layer.Angle, -180, 180):+0;-0;0}°",
                    Width = 42,
                    FontSize = 10,
                    Padding = new Thickness(3, 1, 3, 1),
                    IsEnabled = !locked,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "全方向来袭角度（-180°~+180°）：0°=右方水平，+90°=正上方，-90°=正下方，±180°=左方来袭"
                };
                var angSld = new Slider
                {
                    Minimum = -180,
                    Maximum = 180,
                    TickFrequency = 15,
                    Value = Math.Clamp(layer.Angle, -180, 180),
                    Width = 72,
                    Margin = new Thickness(4, 0, 4, 0),
                    IsEnabled = !locked,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "拖动调整全方向来袭角度（-180° ~ +180°）"
                };
                bool angReady = false;
                void SetLayerAngle(double deg)
                {
                    deg = Math.Round(Math.Clamp(deg, -180.0, 180.0));
                    layer.Angle = deg;
                    angReady = false;
                    angSld.Value = deg;
                    angBox.Text = $"{deg:+0;-0;0}°";
                    angReady = true;
                    InvalidatePreview();
                    MarkDirty();
                }
                angSld.ValueChanged += (_, _) =>
                {
                    if (!angReady) return;
                    SetLayerAngle(angSld.Value);
                };
                angBox.LostFocus += (_, _) =>
                {
                    string raw = (angBox.Text ?? "").Replace("°", "").Trim();
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        SetLayerAngle(v);
                    else
                        angBox.Text = $"{Math.Clamp(layer.Angle, -180, 180):+0;-0;0}°";
                };
                row4.Children.Add(angLbl);
                row4.Children.Add(angSld);
                row4.Children.Add(angBox);
                foreach (double preset in new[] { 0.0, 30.0, -30.0 })
                {
                    double pVal = preset;
                    var pb = new Button
                    {
                        Content = pVal == 0 ? "0°" : $"{pVal:+0;-0}°",
                        Width = pVal == 0 ? 22 : 28,
                        Height = 20,
                        FontSize = 9.5,
                        Padding = new Thickness(0),
                        Margin = new Thickness(2, 0, 0, 0),
                        IsEnabled = !locked,
                        ToolTip = pVal == 0 ? "重置为水平直轨" : $"设为 {pVal:+0;-0}° 斜轨"
                    };
                    pb.Click += (_, _) => SetLayerAngle(pVal);
                    row4.Children.Add(pb);
                }
                root.Children.Add(row4);
                angReady = true;

                card.Child = root;
                LayersPanel.Children.Add(card);
            }

            var hint = new TextBlock
            {
                Text = "调整「方向」可让该层轨道斜向飞入判定框，并在下方“直轨投影框”实时投影到人物水平直线上（X 对齐打击时刻）。",
                FontSize = 10.5,
                LineHeight = 17,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 6, 0, 0)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
            LayersPanel.Children.Add(hint);
        }


        // ---------------- 设置与面板 ----------------
        private void ApplySettings()
        {
            _applyingSettings = true;
            var s = EditorSettings.Current;
            try
            {
            ChkHitsound.IsChecked = s.Hitsound;
            ChkMetronome.IsChecked = s.Metronome;
            ChkFollow.IsChecked = s.FollowPlayhead;
            ChkAutoMiddle.IsChecked = s.AutoMiddle;
            if (ChkProjLines != null) ChkProjLines.IsChecked = s.ShowProjectionLines;
            if (ChkProjBox != null) ChkProjBox.IsChecked = s.ShowProjectionBox;
            ApplyProjectionVisibility();
            SetComboTag(CmbSnap, s.SnapDiv);
            SetComboTag(CmbPreviewGrid, s.PreviewGridDiv);
            SetComboTag(CmbRate, s.DefaultRate);
            SetPanelChecked("library", s.PanelLibrary);
            SetPanelChecked("meta", s.PanelMeta);
            SetPanelChecked("health", s.PanelHealth);
            SetPanelChecked("inspector", s.PanelInspector);
            ApplyPanelVisibility();
            ApplySongVolume();
            _fmod.SetHitsoundVolume((float)s.HitsoundVolume);
            _fmod.SetMetronomeVolume((float)s.MetronomeVolume);
            _officialAudio.SetVolume((float)s.MasterVolume);
            _officialAudio.ReloadBanksIfNeeded();
            }
            finally
            {
                _applyingSettings = false;
            }
            ApplyPanelVisibility();
            Playfield.RenderScale = Math.Clamp(s.PreviewScale, 0.5, 1.0);
            Playfield.ApplyRenderQuality(s.RenderQuality != "performance");
            if (ProjectionBox != null)
            {
                ProjectionBox.RenderScale = Math.Clamp(s.PreviewScale, 0.5, 1.0);
                ProjectionBox.ApplyRenderQuality(s.RenderQuality != "performance");
            }
            Timeline.ApplyRenderQuality(s.RenderQuality != "performance");
            _maxFps = s.MaxFps;
            Timeline.InvalidateVisual();
            InvalidatePreview();
            RequestRedraw();
        }

        private static void SetComboTag(ComboBox cb, double value)
        {
            for (int i = 0; i < cb.Items.Count; i++)
            {
                if (cb.Items[i] is ComboBoxItem ci && ci.Tag != null &&
                    double.TryParse(ci.Tag.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) &&
                    Math.Abs(v - value) < 0.001)
                {
                    cb.SelectedIndex = i;
                    return;
                }
            }
        }

        private void PersistUiSettings()
        {
            var s = EditorSettings.Current;
            s.Hitsound = ChkHitsound.IsChecked == true;
            s.Metronome = ChkMetronome.IsChecked == true;
            s.FollowPlayhead = ChkFollow.IsChecked == true;
            s.AutoMiddle = ChkAutoMiddle.IsChecked == true;
            if (ChkProjLines != null) s.ShowProjectionLines = ChkProjLines.IsChecked == true;
            if (ChkProjBox != null) s.ShowProjectionBox = ChkProjBox.IsChecked == true;
            s.PanelLibrary = MenuPanelLibrary.IsChecked;
            s.PanelMeta = MenuPanelMeta.IsChecked;
            s.PanelHealth = MenuPanelHealth.IsChecked;
            s.PanelInspector = MenuPanelInspector.IsChecked;
            s.PanelLeftVisible = s.PanelLibrary || s.PanelMeta;
            s.PanelRightVisible = s.PanelHealth || s.PanelInspector;
            if (CmbSnap.SelectedItem is ComboBoxItem si && double.TryParse(si.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sv))
                s.SnapDiv = (int)sv;
            if (CmbPreviewGrid.SelectedItem is ComboBoxItem gi && double.TryParse(gi.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double gv))
                s.PreviewGridDiv = (int)gv;
            if (CmbRate.SelectedItem is ComboBoxItem ri && double.TryParse(ri.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double rv))
                s.DefaultRate = rv;
            s.Save();
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow { Owner = this };
            dlg.Applied = ApplySettings;
            dlg.ShowDialog();
        }

        private readonly Dictionary<string, Window> _panelWindows = new Dictionary<string, Window>();

        private static string PanelKeyOf(object sender) => (sender as FrameworkElement)?.Tag?.ToString() ?? "";

        private FrameworkElement PanelHostFor(string key) => key switch
        {
            "library" => LibraryHost,
            "meta" => MetaHost,
            "health" => HealthHost,
            "inspector" => InspectorHost,
            _ => null
        };

        private FrameworkElement PanelContentFor(string key) => key switch
        {
            "library" => LibraryPanel,
            "meta" => MetaPanelContent,
            "health" => HealthPanel,
            "inspector" => InspectorPanelContent,
            _ => null
        };

        private FrameworkElement PanelHintFor(string key) => key switch
        {
            "library" => LibraryPopoutHint,
            "meta" => MetaPopoutHint,
            "health" => HealthPopoutHint,
            "inspector" => InspectorPopoutHint,
            _ => null
        };

        private bool PanelVisible(string key) => key switch
        {
            "library" => MenuPanelLibrary.IsChecked,
            "meta" => MenuPanelMeta.IsChecked,
            "health" => MenuPanelHealth.IsChecked,
            "inspector" => MenuPanelInspector.IsChecked,
            _ => false
        };

        private void SetPanelChecked(string key, bool value)
        {
            switch (key)
            {
                case "library": MenuPanelLibrary.IsChecked = value; break;
                case "meta": MenuPanelMeta.IsChecked = value; break;
                case "health": MenuPanelHealth.IsChecked = value; break;
                case "inspector": MenuPanelInspector.IsChecked = value; break;
            }
        }

        private void ApplyPanelVisibility()
        {
            foreach (string key in new[] { "library", "meta", "health", "inspector" })
            {
                bool visible = PanelVisible(key);
                var host = PanelHostFor(key);
                if (host != null) host.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (!visible && _panelWindows.TryGetValue(key, out var popup))
                    popup.Close();
            }
            bool left = PanelVisible("library") || PanelVisible("meta");
            bool right = PanelVisible("health") || PanelVisible("inspector");
            ColLeft.Width = left ? new GridLength(336) : new GridLength(0);
            ColRight.Width = right ? new GridLength(356) : new GridLength(0);
            BdLeft.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
            BdRight.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnPanels_Click(object sender, RoutedEventArgs e)
        {
            if (BtnPanels.ContextMenu == null) return;
            BtnPanels.ContextMenu.PlacementTarget = BtnPanels;
            BtnPanels.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            BtnPanels.ContextMenu.IsOpen = true;
        }

        private void MenuPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            ApplyPanelVisibility();
            PersistUiSettings();
            RequestRedraw();
        }

        private void BtnPopPanel_Click(object sender, RoutedEventArgs e) => PopOutPanel(PanelKeyOf(sender));

        private void BtnClosePanel_Click(object sender, RoutedEventArgs e)
        {
            string key = PanelKeyOf(sender);
            SetPanelChecked(key, false);
            ApplyPanelVisibility();
            PersistUiSettings();
            RequestRedraw();
        }

        private void BtnDockPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_panelWindows.TryGetValue(PanelKeyOf(sender), out var win)) win.Close();
        }

        private void PopOutPanel(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (_panelWindows.TryGetValue(key, out var existing)) { existing.Activate(); return; }
            var host = PanelHostFor(key);
            var content = PanelContentFor(key);
            var hint = PanelHintFor(key);
            if (host == null || content == null || hint == null) return;

            if (content.Parent is Panel parent) parent.Children.Remove(content);
            hint.Visibility = Visibility.Visible;

            var (title, width, height) = key switch
            {
                "library" => ("曲目库 · Nientum Studio", 400, 780),
                "meta" => ("难度与曲目信息 · Nientum Studio", 430, 880),
                "health" => ("谱面体检 · Nientum Studio", 440, 580),
                "inspector" => ("属性检查器 · Nientum Studio", 470, 860),
                _ => ("面板 · Nientum Studio", 420, 700)
            };
            var win = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                MinWidth = 320,
                MinHeight = 360,
                Owner = this,
                Background = (Brush)FindResource("BgPanel"),
                Content = content,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            win.Closed += (_, _) =>
            {
                win.Content = null;
                DockPanelContent(key);
            };
            win.PreviewKeyDown += (_, e2) => { if (!e2.Handled) Window_PreviewKeyDown(this, e2); };
            _panelWindows[key] = win;
            win.Show();
            RequestRedraw();
        }

        private void DockPanelContent(string key)
        {
            _panelWindows.Remove(key);
            if (_closing) return;
            var host = PanelHostFor(key);
            var content = PanelContentFor(key);
            var hint = PanelHintFor(key);
            if (host is Border hostBorder && hostBorder.Child is Grid hostGrid && content != null)
            {
                if (content.Parent is Panel oldParent) oldParent.Children.Remove(content);
                hostGrid.Children.Add(content);
                Grid.SetRow(content, 0);
                Grid.SetColumn(content, 0);
            }
            if (hint != null) hint.Visibility = Visibility.Collapsed;
            RequestRedraw();
        }

        private void UpdatePreviewBackgrounds()
        {
            if (PreviewBgImage != null) PreviewBgImage.Source = Playfield.BackgroundImage;
        }

        private void BtnPopPreview_Click(object sender, RoutedEventArgs e) => PopOutPreview();
        private void BtnDockPreview_Click(object sender, RoutedEventArgs e) => _previewPopup?.Close();
        private void BtnPopTimeline_Click(object sender, RoutedEventArgs e) => PopOutTimeline();
        private void BtnDockTimeline_Click(object sender, RoutedEventArgs e) => _timelinePopup?.Close();

        private void PopOutPreview()
        {
            if (_previewPopup != null) { _previewPopup.Activate(); return; }
            if (PreviewStageLayer.Parent is Panel host) host.Children.Remove(PreviewStageLayer);
            PreviewPopoutHint.Visibility = Visibility.Visible;
            var win = new Window
            {
                Title = "画面预览 (16:9) · Nientum Studio",
                Width = 1120,
                Height = 630,
                MinWidth = 480,
                MinHeight = 270,
                Owner = this,
                Content = PreviewStageLayer,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = Math.Max(0, Left + 70),
                Top = Math.Max(0, Top + 90)
            };
            win.SetResourceReference(BackgroundProperty, "BgDeep");
            win.Closed += (_, _) => DockPreview();
            win.PreviewKeyDown += (_, e2) => { if (!e2.Handled) Window_PreviewKeyDown(this, e2); };
            _previewPopup = win;
            win.Show();
            SyncCustomBgVideo(_wasPlaying, false);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                if (_previewPopup == win)
                {
                    UpdateViewportGuide();
                    ApplyCustomBgTransform();
                    RequestRedraw();
                }
            }));
        }

        private void DockPreview()
        {
            if (_previewPopup == null) return;
            var content = _previewPopup.Content;
            _previewPopup.Content = null;
            if (!_closing && content is UIElement stage)
            {
                if (!PreviewCanvasHost.Children.Contains(stage))
                    PreviewCanvasHost.Children.Insert(0, stage);
                PreviewPopoutHint.Visibility = Visibility.Collapsed;
            }
            _previewPopup = null;
            SyncCustomBgVideo(_wasPlaying, false);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                if (!_closing)
                {
                    UpdatePreviewBackgrounds();
                    UpdateViewportGuide();
                    ApplyCustomBgTransform();
                    RequestRedraw();
                }
            }));
        }

        private void BtnToggleFold_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string key) return;
            FrameworkElement body = key switch
            {
                "library" => LibraryBody,
                "health" => HealthBody,
                _ => null
            };
            if (body == null) return;
            bool collapsed = body.Visibility == Visibility.Visible;
            body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            btn.Content = collapsed ? "▸ 展开" : "▾ 收起";
        }

        private void PopOutTimeline()
        {
            if (_timelinePopup != null) { _timelinePopup.Activate(); return; }
            if (Timeline.Parent is Grid host) host.Children.Remove(Timeline);
            TimelinePopoutHint.Visibility = Visibility.Visible;
            var win = new Window
            {
                Title = "时间轴 · Nientum Studio",
                Width = 1400,
                Height = 420,
                MinWidth = 640,
                MinHeight = 220,
                Owner = this,
                Content = Timeline,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = Math.Max(0, Left + 40),
                Top = Math.Max(0, Top + 260)
            };
            win.SetResourceReference(BackgroundProperty, "BgDeep");
            win.Closed += (_, _) => DockTimeline();
            win.PreviewKeyDown += (_, e2) => { if (!e2.Handled) Window_PreviewKeyDown(this, e2); };
            _timelinePopup = win;
            win.Show();
            RequestRedraw();
        }

        private void DockTimeline()
        {
            if (_timelinePopup == null) return;
            var content = _timelinePopup.Content;
            _timelinePopup.Content = null;
            if (!_closing && content is TimelineControl tl)
            {
                TimelineHost.Children.Add(tl);
                Grid.SetRow(tl, 0);
                TimelinePopoutHint.Visibility = Visibility.Collapsed;
            }
            _timelinePopup = null;
            RequestRedraw();
        }

        private void PreviewHeader_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && IsInteractiveElement(d)) return;
            _longPressTimer.Start();
        }

        private void PreviewHeader_MouseUp(object sender, MouseButtonEventArgs e) => _longPressTimer.Stop();
        private void PreviewHeader_MouseLeave(object sender, MouseEventArgs e) => _longPressTimer.Stop();

        private static bool IsInteractiveElement(DependencyObject d)
        {
            while (d != null && d is not Window)
            {
                if (d is System.Windows.Controls.Primitives.ButtonBase || d is TextBox || d is ComboBox || d is Slider)
                    return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        // ---------------- 拖放导入 ----------------
        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (_currentSong == null)
            {
                SetStatus("请先在左侧选择一首曲目，再拖入音频或封面");
                return;
            }
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            string file = files[0];
            string ext = Path.GetExtension(file).ToLowerInvariant();
            try
            {
                if (ext is ".ogg" or ".mp3" or ".wav" or ".flac")
                {
                    _fmod.UnloadSong();
                    string dest = "song" + ext;
                    SafeCopyFile(file, Path.Combine(_currentSong.FolderPath, dest));
                    _currentSong.Meta.audioFile = dest;
                    ChartStorage.SaveMeta(_currentSong.FolderPath, _currentSong.Meta);
                    RefreshSongListOnly();
                    await LoadAudioForCurrentSongAsync();
                    SetStatus($"已导入音频：{Path.GetFileName(file)}（波形已同步到时间轴，预览同步缩放）");
                }
                else if (ext is ".png" or ".jpg" or ".jpeg")
                {
                    string dest = "cover" + ext;
                    SafeCopyFile(file, Path.Combine(_currentSong.FolderPath, dest));
                    _currentSong.Meta.coverFile = dest;
                    ChartStorage.SaveMeta(_currentSong.FolderPath, _currentSong.Meta);
                    UpdateCoverPreview();
                    RefreshSongListOnly();
                    SetStatus($"已导入封面：{Path.GetFileName(file)}");
                }
                else
                {
                    SetStatus("仅支持拖入音频（ogg / mp3 / wav / flac）或图片（png / jpg）");
                }
            }
            catch (Exception ex)
            {
                SetStatus("拖放导入失败：" + ex.Message);
            }
        }

        // ---------------- 快捷键 ----------------
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (e.Key == Key.Space) { e.Handled = true; TogglePlay(); }
            else if (e.Key == Key.Home) { e.Handled = true; SeekTo(0); }
            else if (e.Key == Key.Delete) { e.Handled = true; DeleteSelected(); }
            else if (ctrl && e.Key == Key.S) { e.Handled = true; SaveCurrent(); }
            else if (ctrl && e.Key == Key.Z) { e.Handled = true; DoUndo(); }
            else if (ctrl && e.Key == Key.Y) { e.Handled = true; DoRedo(); }
            else if (ctrl && e.Key == Key.C && Items != null && Timeline.SelectedIndices.Count > 0)
            {
                e.Handled = true;
                CopyIndices(Timeline.SelectedIndices.OrderBy(x => x).ToArray());
            }
            else if (ctrl && e.Key == Key.V && Items != null && _clipboard.Count > 0)
            {
                e.Handled = true;
                if (!EnsureActiveLayerEditable()) return;
                PushUndo();
                double baseT = _clipboard.Min(o => o.GetTime());
                double targetT = Timeline.SnapTime(_playTime) - _offsetSec;
                int nextId = TimelineControl.MaxId(Items) + 1;
                Timeline.SelectedIndices.Clear();
                foreach (var src in _clipboard)
                {
                    var clone = (JObject)src.DeepClone();
                    double dt = clone.GetTime() - baseT;
                    double dur = clone.IsLong() ? Math.Max(0, clone.GetEndTime() - clone.GetTime()) : 0;
                    clone["hFormatDataID"] = nextId++;
                    clone.SetTime(targetT + dt);
                    if (dur > 0) clone.SetEndTime(targetT + dt + dur);
                    // 复制的中段不会跟着过来，清掉引用后按需重建，避免悬空 ID
                    if (clone.GetTypeName() == "CRAFormatDataNoteOrb")
                        clone["LongMiddleIds"] = new JArray();
                    // 复制大跳时，同时创建配对的 Divider，避免悬空 DividerId
                    if (clone.GetTypeName() == "CRAFormatDataSuperJumpBody")
                    {
                        int divId = nextId++;
                        double divT = targetT + dt + (dur > 0 ? dur * 0.5 : 0.5);
                        Items.Add(new JObject
                        {
                            ["$type"] = "CRAFormatDataSuperJumpDivider",
                            ["MaxHeight"] = 6.0,
                            ["bAutoHit"] = false,
                            ["hFormatDataID"] = divId,
                            ["iFormatDataType"] = 4,
                            ["iFormatDataIndex"] = Items.Count,
                            ["iTrackIndexStart"] = 5,
                            ["iTrackIndexEnd"] = 5,
                            ["fTimePlay"] = Math.Round(divT, 4),
                            ["fTimeTrack"] = Math.Round(divT, 4)
                        });
                        clone["DividerId"] = divId;
                    }
                    Items.Add(clone);
                    Timeline.SelectedIndices.Add(Items.Count - 1);
                    if (clone.GetTypeName() == "CRAFormatDataNoteOrb" && ChkAutoMiddle.IsChecked == true)
                        Timeline.EnsureLongMiddles(Items, clone);
                }
                BuildInspector();
                UpdateStats();
            }
            else if (!ctrl && e.Key == Key.F) { e.Handled = true; FlipSelectedLanes(); }
            else if (!ctrl && e.Key == Key.H) { e.Handled = true; ChkHitsound.IsChecked = !(ChkHitsound.IsChecked == true); }
            else if (!ctrl && e.Key == Key.M) { e.Handled = true; ChkMetronome.IsChecked = !(ChkMetronome.IsChecked == true); }
            else if (!ctrl && e.Key == Key.OemOpenBrackets)
            {
                e.Handled = true;
                _loopA = _playTime;
                _loopB = null;
                BtnLoopAB.IsChecked = true;
                SetStatus($"A 点 = {_playTime:F2}s");
                Timeline.InvalidateVisual();
            }
            else if (!ctrl && e.Key == Key.OemCloseBrackets)
            {
                e.Handled = true;
                if (_loopA.HasValue && _playTime > _loopA.Value + 0.2)
                {
                    _loopB = _playTime;
                    BtnLoopAB.IsChecked = true;
                    SetStatus($"循环区间 {_loopA.Value:F2}s – {_loopB.Value:F2}s");
                    Timeline.InvalidateVisual();
                }
            }
            else if (!ctrl && e.Key == Key.L)
            {
                e.Handled = true;
                BtnLoopAB_Click(this, null);
            }
            else if (!ctrl && (e.Key == Key.J || e.Key == Key.K) && Items != null)
            {
                e.Handled = true;
                // 实时打谱
                if (!EnsureActiveLayerEditable()) return;
                int lane = e.Key == Key.K ? 1 : 0;
                double t = Timeline.SnapTime(_playTime) - _offsetSec;
                var note = Timeline.CreateItem(Items, EditorTool.Crystal, t, lane);
                if (note != null)
                {
                    PushUndo();
                    Items.Add(note);
                    if (ChkHitsound.IsChecked == true)
                        PlayLiveNoteHit(lane, EditorTool.Crystal);
                    UpdateStats();
                    Timeline.InvalidateVisual();
                    InvalidatePreview();
                }
            }
            else if (!ctrl && e.Key >= Key.D1 && e.Key <= Key.D8)
            {
                e.Handled = true;
                _tool = (EditorTool)(e.Key - Key.D1);
                HighlightToolButtons();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!ConfirmDiscardOrSave())
            {
                e.Cancel = true;
                return;
            }
            _closing = true;
            PersistUiSettings();
            _longPressTimer.Stop();
            _previewPopup?.Close();
            _timelinePopup?.Close();
            foreach (var popup in _panelWindows.Values.ToList()) popup.Close();
            CompositionTarget.Rendering -= OnRenderFrame;
            _fmod.Dispose();
            _officialAudio.Dispose();
        }
    }
}