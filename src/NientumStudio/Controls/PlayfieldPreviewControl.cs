using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using NientumStudio.Models;
using NientumStudio.Services;

namespace NientumStudio.Controls
{
    public class PlayfieldPreviewControl : FrameworkElement
    {
        private ImageSource _customBg;

        public Func<JArray> GetItems { get; set; }
        public Func<double> GetPlayTime { get; set; }
        public Func<double> GetDefaultBpm { get; set; }
        public Func<double?> GetCustomGridBpm { get; set; }
        public Func<int> GetBeatsPerBar { get; set; }
        public Func<int> GetGridDiv { get; set; }
        /// <summary>与时间轴共享的缩放（像素/秒），实现预览与谱面同步放缩。</summary>
        public Func<double> GetPxPerSec { get; set; }
        /// <summary>谱面偏移（秒），正值表示游戏内音符整体后移。</summary>
        public Func<double> GetOffsetSec { get; set; }
        public Func<List<ChartLayer>> GetLayers { get; set; }
        public Func<int> GetActiveLayerIndex { get; set; }
        public Func<double> GetLayerBpm { get; set; }
        /// <summary>独占模式：仅显示并允许编辑当前图层</summary>
        public Func<bool> IsSoloActiveLayer { get; set; }
        /// <summary>实验版：是否为“人物直轨水平投影基准框”（把所有斜轨音符投影到直线上）。</summary>
        public bool IsProjectionBox { get; set; }
        /// <summary>实验版：主预览中是否绘制斜轨虚线垂线与基准直线影子框。</summary>
        public Func<bool> GetShowProjectionLines { get; set; }
        private double Off => GetOffsetSec?.Invoke() ?? 0;
        private double Disp(double raw) => raw + Off;
        public Action<double, int> OnGridPlaceNote { get; set; }
        public Action<double, int> OnGridDeleteNote { get; set; }

        private static Color ParseLayerColor(string hex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c)
                    return c;
            }
            catch { }
            return Color.FromRgb(44, 198, 192);
        }

        private double ActiveLayerAngle()
        {
            var layers = GetLayers?.Invoke();
            int idx = GetActiveLayerIndex?.Invoke() ?? 0;
            if (layers != null && idx >= 0 && idx < layers.Count)
                return Math.Clamp(layers[idx].Angle, -180.0, 180.0);
            return 0.0;
        }

        private double LayerOpacityFor(JObject o)
        {
            var ls = GetLayers?.Invoke();
            if (ls == null || ls.Count == 0) return 1.0;
            int i = Math.Clamp(o.GetEditorLayer(), 0, ls.Count - 1);
            return Math.Clamp(ls[i].Opacity, 0.2, 1.0);
        }

        private double ActiveLayerSpeed()
        {
            var layers = GetLayers?.Invoke();
            int idx = GetActiveLayerIndex?.Invoke() ?? 0;
            if (layers != null && idx >= 0 && idx < layers.Count)
                return Math.Clamp(layers[idx].Speed, 0.01, 999.0);
            return 1.0;
        }

        private static Color ColorAir => ThemeManager.TimelineAir;
        private static Color ColorGround => ThemeManager.TimelineGround;
        private static Color ColorGold => ThemeManager.ItemCoin;
        private static readonly Typeface TfMono = new Typeface("Consolas");
        private static readonly Typeface TfUi = new Typeface("Microsoft YaHei UI");

        // ---- 渲染缓存（避免每帧重建画刷/文字/渐变） ----
        private RenderTargetBitmap _bgCache;
        private double _bgCacheW, _bgCacheH;
        private int _bgCacheTheme = int.MinValue;

        // ---- 轻量谱面缓存：避免每帧遍历 JArray/JObject（播放流畅性的关键） ----
        private sealed class CachedNote
        {
            public double Time, End;
            public string Type;
            public int Lane, Layer;
            public bool IsLong, IsDodge, IsOrb, IsCoin, IsMulti;
            public int[] MidIds;
            public Color Color;
        }

        private sealed class CachedEvent
        {
            public string Type;
            public double Time, End;
            public double Speed;
            public int Theme, Ground, Angle, Mood;
            public double JumpMax;
            public double CamX, CamY, CamZ;
            public int CamEase;
        }

        private List<CachedNote> _cacheNotes;
        private List<CachedEvent> _cacheEvents;
        private List<(double raw, double bpm, int beat)> _cacheBpmEvents;
        private Dictionary<int, double> _midTimes;
        private int _cacheVersion = -1;
        private int _cacheCount = -1;

        /// <summary>背景由外部 Image 承载时，控件只绘制前景（减少每帧大图绘制）。</summary>
        public bool UseExternalBackground { get; set; }
        public ImageSource BackgroundImage { get; private set; }
        public event EventHandler BackgroundImageChanged;

        /// <summary>谱面结构版本（由主窗口在编辑后递增）。</summary>
        public int ChartVersion { get; set; }

        public void InvalidateChartCache() => _cacheVersion = -1;

        private readonly Dictionary<Color, SolidColorBrush> _brushCache = new();
        private readonly Dictionary<(Color, int), Pen> _penCache = new();
        private readonly Dictionary<Color, (Brush fill, Pen pen)> _holdCache = new();
        private readonly Dictionary<(string text, int size10, int argb, bool mono), FormattedText> _textCache = new();
        private static Pen MakePen(Color c, double thickness, bool dash = false)
        {
            var pen = new Pen(new SolidColorBrush(c), thickness);
            if (dash) pen.DashStyle = DashStyles.Dash;
            pen.Freeze();
            return pen;
        }

        private Pen _airPen;
        private Pen _judgePen;
        private Pen AirPen
        {
            get
            {
                if (_airPen == null)
                {
                    _airPen = new Pen(BrushOf(Color.FromArgb((byte)(ThemeManager.IsLight ? 150 : 90), ColorAir.R, ColorAir.G, ColorAir.B)), 1.4)
                        { DashStyle = DashStyles.Dash };
                    _airPen.Freeze();
                }
                return _airPen;
            }
        }

        private Pen JudgePen
        {
            get
            {
                if (_judgePen == null)
                {
                    _judgePen = new Pen(BrushOf(ThemeManager.PreviewJudgeLine), 1.8);
                    _judgePen.Freeze();
                }
                return _judgePen;
            }
        }

        private SolidColorBrush BrushOf(Color c)
        {
            if (_brushCache.TryGetValue(c, out var b)) return b;
            b = new SolidColorBrush(c);
            b.Freeze();
            _brushCache[c] = b;
            return b;
        }

        private Pen PenOf(Color c, double thickness)
        {
            var key = (c, (int)(thickness * 10));
            if (_penCache.TryGetValue(key, out var p)) return p;
            p = new Pen(BrushOf(c), thickness);
            p.Freeze();
            _penCache[key] = p;
            return p;
        }

        private (Brush fill, Pen pen) HoldOf(Color c)
        {
            if (_holdCache.TryGetValue(c, out var v)) return v;
            var fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(130, c.R, c.G, c.B), 0),
                    new GradientStop(Color.FromArgb(60, c.R, c.G, c.B), 1)
                }, 0);
            fill.Freeze();
            var pen = new Pen(BrushOf(Color.FromArgb(170, c.R, c.G, c.B)), 1.2);
            pen.Freeze();
            v = (fill, pen);
            _holdCache[c] = v;
            return v;
        }

        private FormattedText TextOf(string text, bool mono, double size, Color color, double dpi)
        {
            int argb = (color.A << 24) | (color.R << 16) | (color.G << 8) | color.B;
            var key = (text, (int)Math.Round(size * 10), argb, mono);
            if (_textCache.TryGetValue(key, out var ft)) return ft;
            if (_textCache.Count > 800) _textCache.Clear();
            ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(mono ? "Consolas" : "Microsoft YaHei UI"), size, BrushOf(color), dpi);
            _textCache[key] = ft;
            return ft;
        }

        // 主题色调（游戏内主题背景被 prefab 场景承载，预览用色调近似）
        private static readonly Dictionary<int, (Color tint, string name)> ThemeTints = new()
        {
            { 0, (Color.FromRgb(122, 98, 62), "钟楼") },
            { 1, (Color.FromRgb(36, 94, 68), "奥兹森林") },
            { 2, (Color.FromRgb(124, 74, 112), "灰姑娘森林") },
            { 5, (Color.FromRgb(134, 92, 52), "和风白昼") },
            { 7, (Color.FromRgb(74, 64, 52), "中世纪下水道") },
            { 8, (Color.FromRgb(126, 52, 32), "中世纪火焰") },
            { 9, (Color.FromRgb(52, 84, 116), "中世纪冰雪") },
            { 11, (Color.FromRgb(134, 62, 92), "马戏团") },
            { 13, (Color.FromRgb(94, 88, 72), "工厂") },
            { 17, (Color.FromRgb(114, 94, 62), "剧场 5") },
            { 18, (Color.FromRgb(64, 64, 116), "剧场 8") },
            { 19, (Color.FromRgb(64, 64, 116), "剧场 8-4") },
        };

        public PlayfieldPreviewControl()
        {
            ClipToBounds = true;
            MouseDown += OnMouseDownHandler;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
        }

        private StreamGeometry _diamondGeo, _dodgeGeo, _jumpGeo, _midGeo;
        private double _geoNoteSize = -1;

        private static StreamGeometry BuildGeo(params Point[] pts)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(pts[0], true, true);
                for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i], true, false);
            }
            g.Freeze();
            return g;
        }

        private void EnsureNoteGeo(double noteSize)
        {
            if (_diamondGeo != null && Math.Abs(_geoNoteSize - noteSize) < 0.01) return;
            _geoNoteSize = noteSize;
            _diamondGeo = BuildGeo(new Point(0, -10), new Point(8, 0), new Point(0, 10), new Point(-8, 0));
            _dodgeGeo = BuildGeo(new Point(0, -noteSize * 0.44), new Point(noteSize * 0.42, noteSize * 0.38), new Point(-noteSize * 0.42, noteSize * 0.38));
            _jumpGeo = BuildGeo(new Point(0, -noteSize * 0.46), new Point(noteSize * 0.40, 0), new Point(0, noteSize * 0.46), new Point(-noteSize * 0.40, 0));
            _midGeo = BuildGeo(new Point(0, -4.5), new Point(4.5, 0), new Point(0, 4.5), new Point(-4.5, 0));
        }

        public void SetCustomBackground(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(filePath, UriKind.Absolute);
                bmp.DecodePixelWidth = 960;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                _customBg = bmp;
                _bgCache = null;
                InvalidateVisual();
            }
            catch { }
        }

        private sealed class Snapshot
        {
            public double Speed = 1.2;
            public int Theme = 1;
            public string ThemeName = "奥兹森林";
            public Color ThemeTint = Color.FromRgb(36, 94, 68);
            public int GroundPreset;
            public int AnglePreset;
            public int MoodPreset = 1;
            public bool IsStopped;
            public double JumpHeight = -1; // >=0 表示正在大跳
            public double CamX, CamY, CamZ;
            public double CurrentBpm = 120.0;
            public List<(double time, double bpm, int beat)> Bpms;
        }

        private static double EvalCamEase(double u, int ease)
        {
            u = Math.Clamp(u, 0.0, 1.0);
            return ease switch
            {
                1 => u >= 1.0 ? 1.0 : 1.0 - Math.Pow(2.0, -10.0 * u), // OutExpo
                2 => u, // Linear
                3 => u < 0.5 ? 2.0 * u * u : 1.0 - Math.Pow(-2.0 * u + 2.0, 2.0) / 2.0, // InOutQuad
                4 => 1.0 + 2.70158 * Math.Pow(u - 1.0, 3.0) + 1.70158 * Math.Pow(u - 1.0, 2.0), // OutBack
                _ => 1.0 - Math.Pow(1.0 - u, 3.0) // OutCubic
            };
        }

        private static double GetAngleDegrees(int preset) => preset switch
        {
            1 => 5, 2 => 10, 3 => 20, 4 => 30,
            5 => -5, 6 => -10, 7 => -20, 8 => -30,
            _ => 0
        };

        private void EnsureChartCache(JArray items)
        {
            int count = items?.Count ?? -1;
            if (_cacheVersion == ChartVersion && _cacheCount == count && _cacheNotes != null)
                return;
            _cacheVersion = ChartVersion;
            _cacheCount = count;
            _cacheNotes = new List<CachedNote>();
            _cacheEvents = new List<CachedEvent>();
            _cacheBpmEvents = new List<(double, double, int)>();
            _midTimes = new Dictionary<int, double>();
            if (items == null) return;
            int customBeat = GetBeatsPerBar?.Invoke() ?? 4;

            var dividerMax = new Dictionary<int, double>();
            foreach (var tok in items)
            {
                if (tok is JObject d && d["MaxHeight"] != null && d.Value<int?>("DividerId") is int did)
                    dividerMax[did] = d.Value<double?>("MaxHeight") ?? 6.0;
            }

            foreach (var tok in items)
            {
                if (tok is not JObject o) continue;
                string tn = o.GetTypeName() ?? "";
                if (o.IsNote())
                {
                    bool orb = tn == "CRAFormatDataNoteOrb";
                    var cn = new CachedNote
                    {
                        Time = o.GetTime(),
                        End = o.IsLong() ? o.GetEndTime() : o.GetTime(),
                        Type = tn,
                        Lane = o.GetLane(),
                        Layer = o.GetEditorLayer(),
                        IsLong = o.IsLong(),
                        IsDodge = o.IsDodge(),
                        IsOrb = orb,
                        IsCoin = tn.IndexOf("Coin", StringComparison.OrdinalIgnoreCase) >= 0,
                        IsMulti = tn.IndexOf("MultiHit", StringComparison.OrdinalIgnoreCase) >= 0,
                        MidIds = orb ? ((o["LongMiddleIds"] as JArray)?.Values<int>().ToArray() ?? Array.Empty<int>()) : null,
                        Color = TimelineControl.GetItemColor(o)
                    };
                    _cacheNotes.Add(cn);
                    if (tn == "CRAFormatDataNoteLongMiddle" && o.GetId() > 0)
                        _midTimes[o.GetId()] = cn.Time;
                    continue;
                }
                switch (tn)
                {
                    case "CRAFormatDataEventBPMBar":
                        _cacheBpmEvents.Add((o.GetTime(), o.Value<double?>("fBPM") ?? 120.0, o.Value<int?>("iBPMBeat") ?? customBeat));
                        break;
                    case "CRAFormatDataEventSpeed":
                        _cacheEvents.Add(new CachedEvent { Type = tn, Time = o.GetTime(), Speed = o.Value<double?>("fSpeedValue") ?? 1.2 });
                        break;
                    case "CRAFormatDataFFChangeTheme":
                        _cacheEvents.Add(new CachedEvent { Type = tn, Time = o.GetTime(), Theme = o.Value<int?>("eFFTheme") ?? 1 });
                        break;
                    case "CRAFormatDataFFChangeGroundPreset":
                        _cacheEvents.Add(new CachedEvent { Type = tn, Time = o.GetTime(), Ground = o.Value<int?>("eFFGroundPreset") ?? 0 });
                        break;
                    case "CRAFormatDataFFChangeAnglePreset":
                        _cacheEvents.Add(new CachedEvent { Type = tn, Time = o.GetTime(), Angle = o.Value<int?>("eFFAnglePreset") ?? 0 });
                        break;
                    case "CRAFormatDataFFChangeCameraPreset":
                        _cacheEvents.Add(new CachedEvent
                        {
                            Type = tn,
                            Time = o.GetTime(),
                            End = o.GetEndTime(),
                            CamX = o.Value<double?>("camOffsetX") ?? 0.0,
                            CamY = o.Value<double?>("camOffsetY") ?? 0.0,
                            CamZ = o.Value<double?>("camOffsetZ") ?? 0.0,
                            CamEase = o.Value<int?>("camEase") ?? 0
                        });
                        break;
                    case "CRAFormatDataFFChangeMoodPreset":
                        _cacheEvents.Add(new CachedEvent { Type = tn, Time = o.GetTime(), Mood = o.Value<int?>("eFFMoodPreset") ?? 1 });
                        break;
                    case "CRAFormatDataStopMap":
                        _cacheEvents.Add(new CachedEvent { Type = tn, Time = o.GetTime(), End = o.GetEndTime() });
                        break;
                    case "CRAFormatDataSuperJumpBody":
                        double maxH = 6.0;
                        if (o.Value<int?>("DividerId") is int dvId && dividerMax.TryGetValue(dvId, out double mh)) maxH = mh;
                        _cacheEvents.Add(new CachedEvent { Type = tn, Time = o.GetTime(), End = o.GetEndTime(), JumpMax = maxH });
                        break;
                }
            }
        }

        private Snapshot BuildSnapshot(double playTime)
        {
            var s = new Snapshot();
            s.CurrentBpm = GetDefaultBpm?.Invoke() ?? 120.0;
            if (_cacheEvents != null)
            {
                foreach (var e in _cacheEvents)
                {
                    double t = Disp(e.Time);
                    switch (e.Type)
                    {
                        case "CRAFormatDataEventSpeed":
                            if (t <= playTime) s.Speed = e.Speed;
                            break;
                        case "CRAFormatDataFFChangeTheme":
                            if (t <= playTime) s.Theme = e.Theme;
                            break;
                        case "CRAFormatDataFFChangeGroundPreset":
                            if (t <= playTime) s.GroundPreset = e.Ground;
                            break;
                        case "CRAFormatDataFFChangeAnglePreset":
                            if (t <= playTime) s.AnglePreset = e.Angle;
                            break;
                        case "CRAFormatDataFFChangeMoodPreset":
                            if (t <= playTime) s.MoodPreset = e.Mood;
                            break;
                        case "CRAFormatDataStopMap":
                            if (t <= playTime && playTime <= Disp(e.End)) s.IsStopped = true;
                            break;
                        case "CRAFormatDataSuperJumpBody":
                            if (t <= playTime && playTime <= Disp(e.End)) s.JumpHeight = e.JumpMax;
                            break;
                        case "CRAFormatDataFFChangeCameraPreset":
                            if (t <= playTime)
                            {
                                double tEnd = Disp(e.End);
                                double dur = Math.Max(0.05, tEnd - t);
                                double u = playTime >= tEnd ? 1.0 : EvalCamEase((playTime - t) / dur, e.CamEase);
                                s.CamX += (e.CamX - s.CamX) * u;
                                s.CamY += (e.CamY - s.CamY) * u;
                                s.CamZ += (e.CamZ - s.CamZ) * u;
                            }
                            break;
                    }
                }
            }
            if (ThemeTints.TryGetValue(s.Theme, out var tint))
            {
                s.ThemeTint = tint.tint;
                s.ThemeName = tint.name;
            }
            s.Bpms = GetBpmSegments();
            foreach (var b in s.Bpms)
                if (b.time <= playTime && b.bpm > 0) s.CurrentBpm = b.bpm;
            return s;
        }

        private LinearGradientBrush _groundGrad;
        private Pen _groundPen;

        private void EnsureBackgroundCache(double w, double h, Snapshot snap)
        {
            if (_bgCache != null && Math.Abs(_bgCacheW - w) < 0.51 && Math.Abs(_bgCacheH - h) < 0.51
                && _bgCacheTheme == snap.Theme)
                return;

            bool light = ThemeManager.IsLight;
            bool game = ThemeManager.IsGame;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(BrushOf(ThemeManager.PreviewBase), null, new Rect(0, 0, w, h));
                var bg = _customBg ?? (game ? (SpriteCache.PlaylistBg ?? SpriteCache.DefaultBg) : SpriteCache.DefaultBg);
                if (bg != null)
                {
                    dc.PushOpacity(_customBg != null ? 0.42 : (game ? 0.52 : (light ? 0.16 : 0.32)));
                    dc.DrawImage(bg, new Rect(0, 0, w, h));
                    dc.Pop();
                }
                dc.DrawRectangle(BrushOf(Color.FromArgb((byte)(light ? 22 : (game ? 34 : 46)), snap.ThemeTint.R, snap.ThemeTint.G, snap.ThemeTint.B)), null, new Rect(0, 0, w, h));
                var v = ThemeManager.PreviewVignette;
                var vignette = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb((byte)(light ? 120 : 150), v.R, v.G, v.B), 0),
                        new GradientStop(Color.FromArgb(0, v.R, v.G, v.B), 0.22),
                        new GradientStop(Color.FromArgb(0, v.R, v.G, v.B), 0.82),
                        new GradientStop(Color.FromArgb((byte)(light ? 130 : 160), v.R, v.G, v.B), 1)
                    }, 90);
                vignette.Freeze();
                dc.DrawRectangle(vignette, null, new Rect(0, 0, w, h));

                if (game)
                {
                    var bf = SpriteCache.ButterflyGold;
                    if (bf != null)
                    {
                        double bs = Math.Clamp(h * 0.28, 36, 78);
                        dc.PushOpacity(0.14);
                        dc.DrawImage(bf, new Rect(w - bs - 14, 14, bs, bs));
                        dc.Pop();
                    }
                    dc.DrawRectangle(null, PenOf(Color.FromArgb(110, 236, 198, 116), 1.0), new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)));
                }
            }
            var rtb = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(w)), Math.Max(1, (int)Math.Ceiling(h)),
                96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            _bgCache = rtb;
            _bgCacheW = w;
            _bgCacheH = h;
            _bgCacheTheme = snap.Theme;
            BackgroundImage = _bgCache;
            BackgroundImageChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>主题或自定义背景变化时调用。</summary>
        public void InvalidateTheme()
        {
            _bgCache = null;
            _cacheVersion = -1;
            _textCache.Clear();
            _brushCache.Clear();
            _penCache.Clear();
            _holdCache.Clear();
            _groundGrad = null;
            _groundPen = null;
            _airPen = null;
            _judgePen = null;
            InvalidateVisual();
        }

        private (double judgeX, double groundY, double airY, double pxPerSec) GetMetrics(double w, double h, double speed)
        {
            double judgeX = Math.Round(w * 0.17);
            double groundY = IsProjectionBox ? Math.Round(h * 0.76) : Math.Round(h * 0.76);
            double airY = IsProjectionBox ? Math.Round(h * 0.43) : Math.Round(h * 0.36);
            double basePx = GetPxPerSec?.Invoke() ?? Math.Max(150, w * 0.42);
            double pxPerSec = Math.Max(80, basePx) * Math.Max(0.3, speed / 1.2);
            return (judgeX, groundY, airY, pxPerSec);
        }

        private void OnMouseDownHandler(object sender, MouseButtonEventArgs e)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            Point pos = e.GetPosition(this);
            double playTime = GetPlayTime?.Invoke() ?? 0;
            var items = GetItems?.Invoke();
            EnsureChartCache(items);
            var snap = BuildSnapshot(playTime);
            var (judgeX, groundY, airY, pxPerSec) = GetMetrics(w, h, snap.Speed);
            if (!IsProjectionBox)
            {
                double tiltDeg = GetAngleDegrees(snap.AnglePreset) * 0.45;
                if (Math.Abs(tiltDeg) > 0.1)
                {
                    double rad = -tiltDeg * Math.PI / 180.0;
                    double dx = pos.X - judgeX, dy = pos.Y - groundY;
                    pos = new Point(judgeX + dx * Math.Cos(rad) - dy * Math.Sin(rad),
                                    groundY + dx * Math.Sin(rad) + dy * Math.Cos(rad));
                }
            }
            double actAng = ActiveLayerAngle();
            double rawT;
            int lane;
            if (!IsProjectionBox && Math.Abs(actAng) > 0.1)
            {
                double rad = actAng * Math.PI / 180.0;
                double cosA = Math.Cos(rad), sinA = Math.Sin(rad);
                double midY = (airY + groundY) * 0.5;
                double d = (pos.X - judgeX) * cosA - (pos.Y - midY) * sinA;
                rawT = playTime + d / pxPerSec / ActiveLayerSpeed();
                if (rawT < 0) return;
                double pxA = judgeX + d * cosA, pyA = airY - d * sinA;
                double pxG = judgeX + d * cosA, pyG = groundY - d * sinA;
                double dA = (pos.X - pxA) * (pos.X - pxA) + (pos.Y - pyA) * (pos.Y - pyA);
                double dG = (pos.X - pxG) * (pos.X - pxG) + (pos.Y - pyG) * (pos.Y - pyG);
                lane = dA < dG ? 1 : 0;
            }
            else
            {
                rawT = playTime + (pos.X - judgeX) / pxPerSec / ActiveLayerSpeed();
                if (rawT < 0) return;
                lane = pos.Y < (airY + groundY) / 2.0 ? 1 : 0;
            }
            if (e.ChangedButton == MouseButton.Right)
                OnGridDeleteNote?.Invoke(rawT, lane);
            else if (e.ChangedButton == MouseButton.Left)
                OnGridPlaceNote?.Invoke(rawT, lane);
        }

        public double RenderScale { get; set; } = 1.0;

        public void ApplyRenderQuality(bool highQuality)
        {
            RenderOptions.SetBitmapScalingMode(this, highQuality ? BitmapScalingMode.HighQuality : BitmapScalingMode.LowQuality);
            InvalidateVisual();
        }

        private DrawingVisual _scaleVisual;
        private RenderTargetBitmap _scaleBitmap;
        private int _scaleW, _scaleH;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double scale = Math.Clamp(RenderScale, 0.5, 1.0);
            if (scale >= 0.999)
            {
                RenderCore(dc, w, h);
                return;
            }
            int pw = Math.Max(1, (int)Math.Ceiling(w * scale));
            int ph = Math.Max(1, (int)Math.Ceiling(h * scale));
            if (_scaleBitmap == null || _scaleW != pw || _scaleH != ph)
            {
                _scaleVisual = new DrawingVisual();
                _scaleBitmap = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                _scaleW = pw;
                _scaleH = ph;
            }
            using (var g = _scaleVisual.RenderOpen())
            {
                g.PushTransform(new ScaleTransform(scale, scale));
                RenderCore(g, w, h);
                g.Pop();
            }
            _scaleBitmap.Render(_scaleVisual);
            dc.DrawImage(_scaleBitmap, new Rect(0, 0, w, h));
        }

        private void RenderCore(DrawingContext dc, double w, double h)
        {
            double playTime = GetPlayTime?.Invoke() ?? 0;
            var items = GetItems?.Invoke();
            EnsureChartCache(items);
            var snap = BuildSnapshot(playTime);
            var (judgeX, groundY, airY, pxPerSec) = GetMetrics(w, h, snap.Speed);
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;



            // ================= 背景（缓存层，仅尺寸/主题变化时重建） =================
            if (IsProjectionBox)
            {
                var baseC = ThemeManager.PreviewBase;
                dc.DrawRectangle(BrushOf(baseC), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(BrushOf(Color.FromArgb((byte)(ThemeManager.IsLight ? 14 : 26), snap.ThemeTint.R, snap.ThemeTint.G, snap.ThemeTint.B)),
                    null, new Rect(0, 0, w, h));
            }
            else
            {
                EnsureBackgroundCache(w, h, snap);
                if (!UseExternalBackground && _bgCache != null)
                    dc.DrawImage(_bgCache, new Rect(0, 0, w, h));
            }

            // ================= BPM 分格 =================
            int gridDiv = GetGridDiv?.Invoke() ?? 2;
            if (gridDiv > 0)
            {
                double minT = playTime - judgeX / pxPerSec;
                double maxT = playTime + (w - judgeX) / pxPerSec;
                var penBar = PenOf(ThemeManager.PreviewGridBar, 1.2);
                var penBeat = PenOf(ThemeManager.PreviewGridBeat, 1.0);
                var penSub = PenOf(ThemeManager.PreviewGridSub, 1.0);
                bool useAccent = EditorSettings.Current.BeatGridAccent;
                bool isLightTheme = ThemeManager.IsLight;

                for (int i = 0; i < snap.Bpms.Count; i++)
                {
                    double segStart = snap.Bpms[i].time;
                    double segEnd = i + 1 < snap.Bpms.Count ? snap.Bpms[i + 1].time : maxT + 1.0;
                    double bpm = snap.Bpms[i].bpm > 0 ? snap.Bpms[i].bpm : 120.0;
                    int beatsPerBar = snap.Bpms[i].beat > 0 ? snap.Bpms[i].beat : 4;
                    double subSec = 60.0 / bpm / gridDiv;
                    double subPx = subSec * pxPerSec;
                    int lineStep = subPx >= 3.5 ? 1 : Math.Max(1, (int)Math.Ceiling(3.5 / Math.Max(0.1, subPx)));

                    int startSub = Math.Max(0, (int)Math.Floor((minT - segStart) / subSec));
                    int endSub = (int)Math.Ceiling((Math.Min(maxT, segEnd) - segStart) / subSec);

                    for (int s2 = startSub; s2 <= endSub; s2++)
                    {
                        double t = segStart + s2 * subSec;
                        double x = judgeX + (t - playTime) * pxPerSec;
                        if (x < 0 || x > w) continue;
                        bool isBeat = s2 % gridDiv == 0;
                        int beatNum = (int)Math.Round((double)s2 / gridDiv);
                        bool isBar = isBeat && beatNum % beatsPerBar == 0;
                        if (!isBeat && !isBar && s2 % lineStep != 0) continue;
                        Pen linePen;
                        if (useAccent)
                        {
                            var kind = BeatGridStyle.ClassifyGridLine(s2, gridDiv, beatsPerBar);
                            var (col, thick) = BeatGridStyle.GetAccentPenSpec(kind, isLightTheme, true);
                            linePen = PenOf(col, thick);
                        }
                        else
                        {
                            linePen = isBar ? penBar : (isBeat ? penBeat : penSub);
                        }
                        dc.DrawLine(linePen, new Point(x, 16), new Point(x, h - 6));
                        if (isBar && !IsProjectionBox)
                        {
                            int barNo = (beatNum / beatsPerBar) + 1;
                            double labelY = x < 340 ? 29 : 5;
                            dc.DrawText(TextOf($"M{barNo}", true, 9.5, ThemeManager.IsLight ? Color.FromArgb(170, 160, 116, 46) : Color.FromArgb(120, ColorGold.R, ColorGold.G, ColorGold.B), dpi),
                                new Point(x + 3, labelY));
                        }
                    }
                }
            }

            // ================= 轨道倾角与自定义镜头变换（投影框始终保持水平直轨） =================
            bool hasCamTx = !IsProjectionBox && (Math.Abs(snap.CamX) > 0.01 || Math.Abs(snap.CamY) > 0.01 || Math.Abs(snap.CamZ) > 0.01);
            if (hasCamTx)
            {
                double zoomScale = Math.Clamp(1.0 + snap.CamZ * 0.04, 0.45, 2.2);
                dc.PushTransform(new TranslateTransform(-snap.CamX * 14.0, snap.CamY * 14.0));
                dc.PushTransform(new ScaleTransform(zoomScale, zoomScale, judgeX, (airY + groundY) * 0.5));
            }
            double tiltDeg = IsProjectionBox ? 0.0 : GetAngleDegrees(snap.AnglePreset) * 0.45;
            bool tilted = Math.Abs(tiltDeg) > 0.1;
            if (tilted) dc.PushTransform(new RotateTransform(tiltDeg, judgeX, groundY));

            // ================= 轨道与舞台地平线 =================
            if (!IsProjectionBox && snap.GroundPreset != 2)
            {
                var floorBrush = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb((byte)(ThemeManager.IsLight ? 26 : 36), ColorGround.R, ColorGround.G, ColorGround.B), 0),
                        new GradientStop(Color.FromArgb(0, ColorGround.R, ColorGround.G, ColorGround.B), 1)
                    }, 90);
                floorBrush.Freeze();
                dc.DrawRectangle(floorBrush, null, new Rect(0, groundY, w, Math.Max(0, h - groundY + 40)));
            }
            switch (IsProjectionBox ? 0 : snap.GroundPreset)
            {
                case 2: // None
                    break;
                case 1: // Thin
                    dc.DrawLine(PenOf(Color.FromArgb(120, ColorGround.R, ColorGround.G, ColorGround.B), 1.3), new Point(0, groundY), new Point(w, groundY));
                    break;
                default:
                    dc.DrawLine(PenOf(Color.FromArgb(38, ColorGround.R, ColorGround.G, ColorGround.B), 7.0), new Point(0, groundY), new Point(w, groundY));
                    if (_groundGrad == null)
                    {
                        _groundGrad = new LinearGradientBrush(
                            new GradientStopCollection
                            {
                                new GradientStop(Color.FromArgb(38, ColorGround.R, ColorGround.G, ColorGround.B), 0),
                                new GradientStop(Color.FromArgb(220, ColorGround.R, ColorGround.G, ColorGround.B), 1)
                            }, 0);
                        _groundGrad.Freeze();
                        _groundPen = new Pen(_groundGrad, 2.6);
                        _groundPen.Freeze();
                    }
                    dc.DrawLine(_groundPen, new Point(0, groundY), new Point(w, groundY));
                    break;
            }
            dc.DrawLine(PenOf(Color.FromArgb(26, ColorAir.R, ColorAir.G, ColorAir.B), 5.0), new Point(0, airY), new Point(w, airY));
            dc.DrawLine(AirPen, new Point(0, airY), new Point(w, airY));

            var layersList = GetLayers?.Invoke();
            // ================= 实验版：绘制各可见斜行图层的方向导轨 =================
            if (!IsProjectionBox && layersList != null)
            {
                int activeIdx = GetActiveLayerIndex?.Invoke() ?? 0;
                for (int li = 0; li < layersList.Count; li++)
                {
                    var ly = layersList[li];
                    if (!ly.Visible) continue;
                    if (IsSoloActiveLayer?.Invoke() == true && li != activeIdx) continue;
                    double lAng = Math.Clamp(ly.Angle, -180.0, 180.0);
                    if (Math.Abs(lAng) <= 0.1) continue;
                    double rad = lAng * Math.PI / 180.0;
                    double cosA = Math.Cos(rad), sinA = Math.Sin(rad);
                    Color lc = ParseLayerColor(ly.Color);
                    bool isAct = li == activeIdx;
                    var railPen = new Pen(BrushOf(Color.FromArgb((byte)(isAct ? 175 : 95), lc.R, lc.G, lc.B)), isAct ? 1.8 : 1.1)
                    {
                        DashStyle = isAct ? new DashStyle(new double[] { 6, 4 }, 0) : DashStyles.Dash
                    };
                    railPen.Freeze();

                    double rayLen = Math.Max(w, h) * 1.4;
                    dc.DrawLine(railPen, new Point(judgeX, groundY), new Point(judgeX + rayLen * cosA, groundY - rayLen * sinA));
                    dc.DrawLine(railPen, new Point(judgeX, airY), new Point(judgeX + rayLen * cosA, airY - rayLen * sinA));

                    // 在射线上合适位置标出斜轨标签
                    double labelDist = Math.Min(w * 0.55, Math.Max(90.0, h * 0.38));
                    double labelX = Math.Clamp(judgeX + labelDist * cosA, 12, Math.Max(12, w - 110));
                    double refY = Math.Clamp((groundY + airY) * 0.5 - labelDist * sinA, 28, Math.Max(28, h - 46));
                    var tagFt = TextOf($"{ly.Name} {lAng:+0;-0;0}°", true, 9.5, Color.FromArgb(235, lc.R, lc.G, lc.B), dpi);
                    dc.DrawRoundedRectangle(BrushOf(Color.FromArgb(190, ThemeManager.PreviewBase.R, ThemeManager.PreviewBase.G, ThemeManager.PreviewBase.B)),
                        PenOf(Color.FromArgb(140, lc.R, lc.G, lc.B), 0.9),
                        new Rect(labelX, refY - 9, tagFt.Width + 10, 17), 4, 4);
                    dc.DrawText(tagFt, new Point(labelX + 5, refY - 7));
                }
            }

            // ================= 判定线与瞬时命中脉冲 =================
            bool hitPulseGround = false, hitPulseAir = false;
            if (_cacheNotes != null)
            {
                foreach (var hn in _cacheNotes)
                {
                    double hdt = Math.Abs(Disp(hn.Time) - playTime);
                    if (hdt <= 0.055)
                    {
                        if (hn.Lane == 1) hitPulseAir = true; else hitPulseGround = true;
                    }
                }
            }
            dc.DrawLine(PenOf(Color.FromArgb(40, ThemeManager.PreviewJudgeLine.R, ThemeManager.PreviewJudgeLine.G, ThemeManager.PreviewJudgeLine.B), 6.0),
                new Point(judgeX, 12), new Point(judgeX, h - 10));
            dc.DrawLine(JudgePen, new Point(judgeX, 12), new Point(judgeX, h - 10));
            if (!IsProjectionBox && ThemeManager.IsGame && SpriteCache.JudgeRhombus != null)
            {
                dc.DrawImage(SpriteCache.JudgeRhombus, new Rect(judgeX - 7, 10, 14, 14));
            }
            DrawJudgeMarker(dc, judgeX, groundY, ColorGround, hitPulseGround);
            DrawJudgeMarker(dc, judgeX, airY, ColorAir, hitPulseAir);

            // ================= 角色 =================
            if (!IsProjectionBox)
            {
                double charY = groundY;
                if (snap.JumpHeight > 0)
                {
                    // 大跳区间内角色悬空（简化抛物线）
                    var body = items?.OfType<JObject>().FirstOrDefault(o => o.GetTypeName() == "CRAFormatDataSuperJumpBody" && Disp(o.GetTime()) <= playTime && playTime <= Disp(o.GetEndTime()));
                    if (body != null)
                    {
                        double t0 = Disp(body.GetTime()), t1 = Disp(body.GetEndTime());
                        double u = (playTime - t0) / Math.Max(0.01, t1 - t0);
                        double arc = Math.Sin(Math.PI * Math.Clamp(u, 0, 1));
                        charY = groundY - arc * (h * 0.22) * Math.Min(1, snap.JumpHeight / 6.0);
                    }
                }
                var charSpr = SpriteCache.LioraIcon;
                double cs = Math.Max(30, h * 0.16);
                dc.DrawEllipse(BrushOf(Color.FromArgb(60, 0, 0, 0)), null, new Point(judgeX, groundY + 2), cs * 0.34, cs * 0.10);
                if (charSpr != null)
                {
                    dc.PushOpacity(0.92);
                    dc.DrawImage(charSpr, new Rect(judgeX - cs * 0.32, charY - cs, cs, cs));
                    dc.Pop();
                }
            }

            // ================= 音符（主预览按斜轨角斜向移动；直轨投影框全部投影至人物移动直线） =================
            if (_cacheNotes != null)
            {
                double noteSize = IsProjectionBox ? Math.Clamp(h * 0.21, 16, 23) : Math.Max(24, h * 0.135);
                EnsureNoteGeo(noteSize);
                double maxDt = (w - judgeX) / pxPerSec + 0.3;
                bool showProjLines = !IsProjectionBox && (GetShowProjectionLines?.Invoke() ?? true);
                foreach (var n in _cacheNotes)
                {
                    int layerIdx = Math.Clamp(n.Layer, 0, (layersList?.Count ?? 1) - 1);
                    double layerSpeed = 1.0;
                    double layerOp = 1.0;
                    double layerAngle = 0.0;
                    Color layerColor = ThemeManager.TimelineGround;
                    if (layersList != null && layerIdx < layersList.Count)
                    {
                        if (!layersList[layerIdx].Visible) continue;
                        if (IsSoloActiveLayer?.Invoke() == true)
                        {
                            int activeIdx = GetActiveLayerIndex?.Invoke() ?? 0;
                            if (layerIdx != activeIdx) continue;
                        }
                        layerSpeed = Math.Clamp(layersList[layerIdx].Speed, 0.01, 999.0);
                        layerOp = Math.Clamp(layersList[layerIdx].Opacity, 0.2, 1.0);
                        layerAngle = Math.Clamp(layersList[layerIdx].Angle, -180.0, 180.0);
                        layerColor = ParseLayerColor(layersList[layerIdx].Color);
                    }
                    string tName = n.Type;
                    double dt = (Disp(n.Time) - playTime) * layerSpeed;
                    double dtEnd = n.IsLong ? (Disp(n.End) - playTime) * layerSpeed : dt;
                    if (dtEnd < -0.30 || dt > maxDt) continue;

                    double d = dt * pxPerSec;
                    double dEnd = dtEnd * pxPerSec;
                    double targetY = n.Lane == 1 ? airY : groundY;
                    bool isAngled = Math.Abs(layerAngle) > 0.1;
                    double rad = isAngled ? layerAngle * Math.PI / 180.0 : 0.0;
                    double cosA = isAngled ? Math.Cos(rad) : 1.0;
                    double sinA = isAngled ? Math.Sin(rad) : 0.0;
                    double x = IsProjectionBox ? (judgeX + d) : (judgeX + d * cosA);
                    double xEnd = IsProjectionBox ? (judgeX + dEnd) : (judgeX + dEnd * cosA);
                    double y = IsProjectionBox ? targetY : (targetY - d * sinA);
                    double yEnd = IsProjectionBox ? targetY : (targetY - dEnd * sinA);
                    Color col = n.Color;
                    bool opPushed = layerOp < 0.999;
                    if (opPushed) dc.PushOpacity(layerOp);

                    // 主预览中的虚线垂线 + 人物直轨影子锚点
                    if (showProjLines && isAngled && x >= judgeX - 4 && x <= w + 20)
                    {
                        var dropPen = new Pen(BrushOf(Color.FromArgb(125, layerColor.R, layerColor.G, layerColor.B)), 1.0)
                        { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
                        dropPen.Freeze();
                        dc.DrawLine(dropPen, new Point(x, y), new Point(x, targetY));
                        dc.DrawRoundedRectangle(BrushOf(Color.FromArgb(40, layerColor.R, layerColor.G, layerColor.B)),
                            PenOf(Color.FromArgb(200, layerColor.R, layerColor.G, layerColor.B), 1.1),
                            new Rect(x - 5.5, targetY - 5.5, 11, 11), 2.5, 2.5);
                        dc.DrawEllipse(BrushOf(Color.FromArgb(220, layerColor.R, layerColor.G, layerColor.B)), null, new Point(x, targetY), 2.0, 2.0);
                    }

                    // 直轨投影框内，给来自斜轨的音符外加清晰投影框与角度角标
                    if (IsProjectionBox && isAngled)
                    {
                        double boxR = noteSize * 0.62;
                        dc.DrawRoundedRectangle(BrushOf(Color.FromArgb(36, layerColor.R, layerColor.G, layerColor.B)),
                            PenOf(Color.FromArgb(220, layerColor.R, layerColor.G, layerColor.B), 1.25),
                            new Rect(x - boxR, targetY - boxR, boxR * 2, boxR * 2), 4, 4);
                    }

                    // 长条身体（支持全方向长条与投影框水平拍平显示）
                    if (dEnd > d + 2)
                    {
                        var hold = HoldOf(col);
                        double holdH = noteSize * 0.48;
                        if (!IsProjectionBox && (Math.Abs(yEnd - y) > 0.5 || xEnd < x))
                        {
                            double hdx = xEnd - x, hdy = yEnd - y;
                            double len = Math.Sqrt(hdx * hdx + hdy * hdy);
                            double rotDeg = Math.Atan2(hdy, hdx) * 180.0 / Math.PI;
                            dc.PushTransform(new TranslateTransform(x, y));
                            dc.PushTransform(new RotateTransform(rotDeg));
                            dc.DrawRoundedRectangle(hold.fill, hold.pen, new Rect(0, -holdH / 2, Math.Max(8, len), holdH), 4, 4);
                            dc.Pop();
                            dc.Pop();
                        }
                        else
                        {
                            dc.DrawRoundedRectangle(hold.fill,
                                IsProjectionBox && isAngled ? PenOf(Color.FromArgb(195, layerColor.R, layerColor.G, layerColor.B), 1.2) : hold.pen,
                                new Rect(x, y - holdH / 2, Math.Max(8, dEnd - d), holdH), 4, 4);
                        }

                        if (n.IsOrb && n.MidIds != null)
                        {
                            foreach (int midId in n.MidIds)
                            {
                                if (!_midTimes.TryGetValue(midId, out double mTime)) continue;
                                double md = (Disp(mTime) - playTime) * pxPerSec * layerSpeed;
                                if (md < d || md > dEnd) continue;
                                double mx = IsProjectionBox ? (judgeX + md) : (judgeX + md * cosA);
                                double my = IsProjectionBox ? targetY : (targetY - md * sinA);
                                dc.PushTransform(new TranslateTransform(mx, my));
                                dc.DrawGeometry(BrushOf(Color.FromRgb(235, 252, 252)), null, _midGeo);
                                dc.Pop();
                            }
                        }
                    }

                    ImageSource spr = null;
                    double drawSize = noteSize;
                    if (n.IsCoin)
                    {
                        spr = SpriteCache.Coin; drawSize = noteSize * 0.78;
                    }
                    else if (n.IsMulti)
                    {
                        spr = SpriteCache.Multihit; drawSize = noteSize * 1.1;
                    }
                    else if (n.IsOrb)
                    {
                        spr = n.Lane == 1 ? SpriteCache.LongTopHead : SpriteCache.LongBottomHead;
                        drawSize = noteSize * 1.18;
                    }
                    else if (tName == "CRAFormatDataSuperJumpBody")
                    {
                        spr = null;
                    }
                    else if (tName == "CRAFormatDataNoteCrystal" && !n.IsDodge)
                    {
                        spr = n.Lane == 1 ? SpriteCache.NoteTop : SpriteCache.NoteBottom;
                    }

                    if (spr != null)
                    {
                        dc.DrawImage(spr, new Rect(x - drawSize / 2, y - drawSize / 2, drawSize, drawSize));
                    }
                    else if (n.IsDodge)
                    {
                        dc.PushTransform(new TranslateTransform(x, y));
                        dc.DrawGeometry(BrushOf(Color.FromRgb(228, 88, 88)), PenOf(Colors.White, 1.4), _dodgeGeo);
                        dc.Pop();
                        dc.DrawText(TextOf("!", false, noteSize * 0.4, Colors.White, dpi),
                            new Point(x - noteSize * 0.07, y - noteSize * 0.05));
                    }
                    else if (tName == "CRAFormatDataSuperJumpBody")
                    {
                        dc.PushTransform(new TranslateTransform(x, y));
                        dc.DrawGeometry(BrushOf(Color.FromRgb(110, 208, 220)), PenOf(Colors.White, 1.5), _jumpGeo);
                        dc.Pop();
                    }
                    else
                    {
                        dc.PushTransform(new TranslateTransform(x, y));
                        dc.DrawGeometry(BrushOf(col), PenOf(Colors.White, 1), _diamondGeo);
                        dc.Pop();
                    }

                    // 直轨投影框下为斜轨音符标注来袭角度芯片
                    if (IsProjectionBox && isAngled)
                    {
                        var angFt = TextOf($"{layerAngle:+0;-0;0}°", true, 8.5, Color.FromArgb(240, layerColor.R, layerColor.G, layerColor.B), dpi);
                        double bx = x - angFt.Width * 0.5;
                        double by = n.Lane == 1 ? (targetY - noteSize * 0.65 - 12) : (targetY + noteSize * 0.65 + 1);
                        by = Math.Clamp(by, 2, h - 13);
                        dc.DrawText(angFt, new Point(bx, by));
                    }

                    // 长条尾巴标记
                    if (n.IsLong && xEnd > x + 2)
                    {
                        var tailSpr = n.Lane == 1 ? SpriteCache.LongTopTail : SpriteCache.LongBottomTail;
                        double ts = noteSize * 0.62;
                        if (tailSpr != null)
                            dc.PushOpacity(0.85);
                        if (tailSpr != null)
                        {
                            dc.DrawImage(tailSpr, new Rect(xEnd - ts / 2, yEnd - ts / 2, ts, ts));
                            dc.Pop();
                        }
                        else
                        {
                            dc.DrawRectangle(Brushes.White, null, new Rect(xEnd - 1.5, yEnd - 8, 3, 16));
                        }
                    }
                    if (opPushed) dc.Pop();
                }
            }

            if (tilted) dc.Pop();
            if (hasCamTx) { dc.Pop(); dc.Pop(); }

            // ================= 角标信息 & 判定窗口图例 =================
            string info = IsProjectionBox
                ? "直轨投影参考框 · 同 X 对齐时刻"
                : $"BPM {snap.CurrentBpm:F0}   ·   卷轴 {snap.Speed:F2}x   ·   主题 {snap.ThemeName}   ·   地面 {ChartConstants.GroundOptions[Math.Clamp(snap.GroundPreset, 0, 2)]}{(Math.Abs(GetAngleDegrees(snap.AnglePreset)) > 0.1 ? $"   ·   倾角 {GetAngleDegrees(snap.AnglePreset):+0;-0;0}°" : "")}{(snap.IsStopped ? "   ·   ⏸ 卷轴停顿" : "")}";
            var infoFt = TextOf(info, false, IsProjectionBox ? 9.5 : 10.5, ThemeManager.PreviewInfoText, dpi);
            var infoBg = ThemeManager.PreviewBase;
            Pen pillPen = PenOf(ThemeManager.IsGame ? Color.FromArgb(140, 236, 198, 116) : ThemeManager.TimelineDivider, 1.0);
            dc.DrawRoundedRectangle(BrushOf(Color.FromArgb((byte)(ThemeManager.IsLight ? 226 : 196), infoBg.R, infoBg.G, infoBg.B)), pillPen,
                new Rect(10, 4, infoFt.Width + 14, IsProjectionBox ? 18 : 21), 6, 6);
            dc.DrawText(infoFt, new Point(17, IsProjectionBox ? 6 : 7));

            if (!IsProjectionBox && w >= 460 && h >= 140)
            {
                var f1 = TextOf("±90ms", true, 9.5, ThemeManager.PreviewLegendText, dpi);
                var f2 = TextOf("±130ms", true, 9.5, ThemeManager.PreviewLegendText, dpi);
                var f3 = TextOf("±200ms", true, 9.5, ThemeManager.PreviewLegendText, dpi);
                double lw = 14 + f1.Width + 14 + f2.Width + 14 + f3.Width + 14;
                double lx0 = w - lw - 10;
                double ly0 = h - 24;
                dc.DrawRoundedRectangle(BrushOf(Color.FromArgb((byte)(ThemeManager.IsLight ? 218 : 185), infoBg.R, infoBg.G, infoBg.B)),
                    PenOf(ThemeManager.TimelineDivider, 0.8),
                    new Rect(lx0, ly0, lw, 19), 5, 5);
                double cx = lx0 + 9;
                double cy = ly0 + 9.5;
                dc.DrawEllipse(BrushOf(ThemeManager.PreviewRingPlus), null, new Point(cx, cy), 3, 3);
                dc.DrawText(f1, new Point(cx + 6, ly0 + 3));
                cx += 6 + f1.Width + 12;
                dc.DrawEllipse(BrushOf(ThemeManager.PreviewRingPerfect), null, new Point(cx, cy), 3, 3);
                dc.DrawText(f2, new Point(cx + 6, ly0 + 3));
                cx += 6 + f2.Width + 12;
                dc.DrawEllipse(BrushOf(ThemeManager.PreviewRingGreat), null, new Point(cx, cy), 3, 3);
                dc.DrawText(f3, new Point(cx + 6, ly0 + 3));
            }
        }

        private void DrawJudgeMarker(DrawingContext dc, double x, double y, Color accent, bool pulse)
        {
            if (pulse)
            {
                dc.DrawEllipse(BrushOf(Color.FromArgb(55, accent.R, accent.G, accent.B)),
                    PenOf(Color.FromArgb(200, accent.R, accent.G, accent.B), 1.6), new Point(x, y), 22, 22);
            }
            var circle = SpriteCache.JudgeCircle;
            double size = pulse ? 38 : 34;
            if (circle != null)
                dc.DrawImage(circle, new Rect(x - size / 2, y - size / 2, size, size));
            else
                dc.DrawEllipse(null, PenOf(Colors.White, 1.5), new Point(x, y), 12, 12);
            dc.DrawEllipse(BrushOf(Color.FromArgb(230, accent.R, accent.G, accent.B)), null, new Point(x, y), pulse ? 5.5 : 4.5, pulse ? 5.5 : 4.5);
        }

        private List<(double time, double bpm, int beat)> GetBpmSegments()
        {
            double? customBpm = GetCustomGridBpm?.Invoke();
            int customBeat = GetBeatsPerBar?.Invoke() ?? 4;
            if (customBpm.HasValue && customBpm.Value > 10)
                return new List<(double, double, int)> { (0.0, customBpm.Value, customBeat) };
            double layerBpm = GetLayerBpm?.Invoke() ?? 0;
            if (layerBpm > 10)
                return new List<(double, double, int)> { (Off, layerBpm, customBeat) };

            var list = new List<(double time, double bpm, int beat)>();
            if (_cacheBpmEvents != null)
            {
                foreach (var b in _cacheBpmEvents)
                    list.Add((Disp(b.raw), b.bpm, b.beat));
            }
            list.Sort((a, b) => a.time.CompareTo(b.time));
            if (list.Count == 0) list.Add((0.0, GetDefaultBpm?.Invoke() ?? 120.0, customBeat));
            return list;
        }
    }
}