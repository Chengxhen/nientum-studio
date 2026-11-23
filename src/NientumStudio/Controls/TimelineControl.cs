using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using NientumStudio.Models;
using NientumStudio.Services;

namespace NientumStudio.Controls
{
    public class TimelineControl : FrameworkElement
    {
        public Func<JArray> GetItems { get; set; }
        public Func<double> GetPlayTime { get; set; }
        public Func<double> GetDuration { get; set; }
        public Func<double> GetDefaultBpm { get; set; }
        public Func<float[]> GetWaveform { get; set; }
        public Func<EditorTool> GetTool { get; set; }
        public Func<int> GetSnapDiv { get; set; }
        public Func<string> GetNewEventType { get; set; }
        public Func<bool> GetAutoMiddle { get; set; }
        public Func<(double a, double b)?> GetLoopRange { get; set; }
        public Func<double?> GetLoopA { get; set; }
        public Func<string> GetAudioName { get; set; }
        /// <summary>谱面偏移（秒）：正值表示游戏内音符整体后移。时间轴显示 = 谱面时间 + 偏移。</summary>
        public Func<double> GetOffsetSec { get; set; }
        public Func<List<ChartLayer>> GetLayers { get; set; }
        public Func<int> GetActiveLayerIndex { get; set; }
        public Func<double> GetLayerBpm { get; set; }
        /// <summary>独占模式：仅显示并允许编辑当前图层</summary>
        public Func<bool> IsSoloActiveLayer { get; set; }
        private double Off => GetOffsetSec?.Invoke() ?? 0;
        private double Disp(double raw) => raw + Off;

        public Action<double> OnSeek { get; set; }
        public Action<int> OnSelectionChanged { get; set; }
        public Action OnChartMutated { get; set; }
        public Action OnPushUndo { get; set; }
        public Action<double> OnZoomChanged { get; set; }
        // action: delete / copy / flip / addmiddle / clearmiddle
        public Action<string, int[]> OnContextAction { get; set; }

        public double ViewStartSec { get; set; }
        public double PxPerSec { get; set; } = 180.0;
        public HashSet<int> SelectedIndices { get; } = new HashSet<int>();

        private enum DragMode { None, Move, ResizeTail, BoxSelect, Scrub, Scrollbar }
        private DragMode _dragMode = DragMode.None;
        private Point _dragStartPt;
        private Point _dragCurPt;
        private double _dragAnchorTime;
        private double _scrollGrabOffset;
        private bool _resizeRequireThreshold;
        private bool _resizeThresholdPassed;
        private readonly Dictionary<int, (double t0, double t1, int lane)> _dragSnapshot = new();
        private readonly Dictionary<int, List<(JObject mid, double t)>> _dragMiddles = new();
        private readonly Dictionary<int, (JObject div, double t)> _dragDividers = new();
        private (double time, int lane)? _hover;

        // 主题色（随深色 / 浅色主题变化）
        private static Color ColorAirLane => ThemeManager.TimelineAir;
        private static Color ColorGroundLane => ThemeManager.TimelineGround;
        private static Color ColorBgLanes => ThemeManager.TimelineLaneBg;
        private static Color ColorBgEvent => ThemeManager.TimelineEventBg;
        private static Color ColorPlayhead => ThemeManager.TimelinePlayhead;
        private static Color ColorSelection => ThemeManager.TimelineSelection;

        /// <summary>主题切换后清空画刷与文字缓存。</summary>
        private RenderTargetBitmap _waveCache;
        private string _waveCacheKey;

        private void EnsureWaveCache(double w, double waveY, double waveH, float[] peaks, double dur)
        {
            string key = string.Format("{0:F1}|{1:F1}|{2}|{3:F4}|{4:F2}|{5}",
                w, waveH, RuntimeHelpers.GetHashCode(peaks ?? Array.Empty<float>()),
                ViewStartSec, PxPerSec, ThemeManager.IsLight);
            if (_waveCache != null && key == _waveCacheKey) return;
            if (w <= 1 || waveH <= 1) return;

            var dv = new DrawingVisual();
            using (var g = dv.RenderOpen())
            {
                g.DrawRectangle(BrushOf(ThemeManager.TimelineWaveBg), null, new Rect(0, 0, w, waveH));
                if (peaks != null && peaks.Length > 0)
                {
                    double midY = waveH / 2;
                    double amp = waveH * 0.46;
                    var barBrush = BrushOf(ThemeManager.TimelineWaveBar);
                    var topBrush = BrushOf(ThemeManager.TimelineWaveTop);
                    for (double x = 0; x < w; x += 2)
                    {
                        double t = XToTime(x);
                        if (t < 0 || t > dur) continue;
                        int idx = Math.Clamp((int)((t / dur) * peaks.Length), 0, peaks.Length - 1);
                        double bh = Math.Max(1, peaks[idx] * amp);
                        g.DrawRectangle(barBrush, null, new Rect(x, midY - bh, 1.8, bh * 2));
                        g.DrawRectangle(topBrush, null, new Rect(x, midY - bh, 1.8, Math.Max(1, bh * 0.35)));
                    }
                    g.DrawLine(PenOf(ThemeManager.TimelineGridSub, 1), new Point(0, midY), new Point(w, midY));
                }
            }
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var rtb = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(w * scale)),
                Math.Max(1, (int)Math.Ceiling(waveH * scale)),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            _waveCache = rtb;
            _waveCacheKey = key;
        }

        public void ApplyRenderQuality(bool highQuality)
        {
            RenderOptions.SetBitmapScalingMode(this, highQuality ? BitmapScalingMode.HighQuality : BitmapScalingMode.LowQuality);
            InvalidateVisual();
        }

        public void InvalidateTheme()
        {
            _brushCache.Clear();
            _penCache.Clear();
            _textCache.Clear();
            _waveCache = null;
            _waveCacheKey = null;
            InvalidateVisual();
        }
        private readonly Dictionary<(string text, int size10, int argb, bool mono), FormattedText> _textCache = new();
        private readonly Dictionary<Color, SolidColorBrush> _brushCache = new();
        private readonly Dictionary<(Color, int), Pen> _penCache = new();

        private Pen PenOf(Color c, double thickness)
        {
            var key = (c, (int)(thickness * 10));
            if (_penCache.TryGetValue(key, out var p)) return p;
            p = MakePen(c, thickness);
            _penCache[key] = p;
            return p;
        }

        private static Pen MakePen(Color c, double thickness)
        {
            var p = new Pen(new SolidColorBrush(c), thickness);
            p.Freeze();
            return p;
        }

        private SolidColorBrush BrushOf(Color c)
        {
            if (_brushCache.TryGetValue(c, out var b)) return b;
            b = new SolidColorBrush(c);
            b.Freeze();
            _brushCache[c] = b;
            return b;
        }

        private FormattedText TextOf(string text, Typeface tf, double size, Brush brush, bool mono, double dpi)
        {
            var c = (brush as SolidColorBrush)?.Color ?? Colors.White;
            int argb = (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
            var key = (text, (int)Math.Round(size * 10), argb, mono);
            if (_textCache.TryGetValue(key, out var ft)) return ft;
            if (_textCache.Count > 800) _textCache.Clear();
            ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, brush, dpi);
            _textCache[key] = ft;
            return ft;
        }

        public TimelineControl()
        {
            _timelineDiamond = BuildTimelineGeo(new Point(0, -10), new Point(8, 0), new Point(0, 10), new Point(-8, 0));
            _timelineDodge = BuildTimelineGeo(new Point(0, -10), new Point(10, 9), new Point(-10, 9));
            _timelineMid = BuildTimelineGeo(new Point(0, -4), new Point(4, 0), new Point(0, 4), new Point(-4, 0));
            ClipToBounds = true;
            Focusable = true;
            MouseDown += OnMouseDownHandler;
            MouseMove += OnMouseMoveHandler;
            MouseUp += OnMouseUpHandler;
            MouseWheel += OnMouseWheelHandler;
            MouseLeave += (_, _) => { _hover = null; InvalidateVisual(); };
        }

        private bool LayerLockedFor(JObject o)
        {
            if (IsSoloActiveLayer?.Invoke() == true)
            {
                int activeLayer = GetActiveLayerIndex?.Invoke() ?? 0;
                if (o.IsNote() && o.GetEditorLayer() != activeLayer) return true;
            }
            var ls = GetLayers?.Invoke();
            if (ls == null || ls.Count == 0) return false;
            int i = Math.Clamp(o.GetEditorLayer(), 0, ls.Count - 1);
            return ls[i].Locked;
        }

        private double LayerOpacityFor(JObject o)
        {
            var ls = GetLayers?.Invoke();
            if (ls == null || ls.Count == 0) return 1.0;
            int i = Math.Clamp(o.GetEditorLayer(), 0, ls.Count - 1);
            return Math.Clamp(ls[i].Opacity, 0.2, 1.0);
        }

        private bool ActiveLayerLocked
        {
            get
            {
                var ls = GetLayers?.Invoke();
                if (ls == null || ls.Count == 0) return false;
                int i = Math.Clamp(GetActiveLayerIndex?.Invoke() ?? 0, 0, ls.Count - 1);
                return ls[i].Locked;
            }
        }

        private Color LayerStrokeFor(JObject o)
        {
            var layers = GetLayers?.Invoke();
            if (layers == null || layers.Count <= 1) return ThemeManager.TimelineOutline;
            int i = Math.Clamp(o.GetEditorLayer(), 0, layers.Count - 1);
            try { return (Color)ColorConverter.ConvertFromString(layers[i].Color); } catch { return ThemeManager.TimelineOutline; }
        }

        private Color LayerBarColorFor(JObject o)
        {
            var layers = GetLayers?.Invoke();
            if (layers == null || layers.Count <= 1) return GetItemColor(o);
            int i = Math.Clamp(o.GetEditorLayer(), 0, layers.Count - 1);
            try { return (Color)ColorConverter.ConvertFromString(layers[i].Color); } catch { return GetItemColor(o); }
        }

        public static Color GetItemColor(JObject it)
        {
            var ev = ChartConstants.FindEvent(it.GetTypeName());
            if (ev != null) return ev.Color;
            return it.GetTypeName() switch
            {
                "CRAFormatDataNoteCrystal" => it.IsDodge() ? ThemeManager.ItemDodge : (it.GetLane() == 1 ? ThemeManager.ItemCrystalAir : ThemeManager.ItemCrystalGround),
                "CRAFormatDataNoteOrb" => it.GetLane() == 1 ? ThemeManager.ItemOrbAir : ThemeManager.ItemOrbGround,
                "CRAFormatDataNoteMultiHit" => ThemeManager.ItemMultiHit,
                "CRAFormatDataCoin" => ThemeManager.ItemCoin,
                "CRAFormatDataSuperJumpBody" => ThemeManager.ItemSuperJump,
                "CRAFormatDataNoteLongMiddle" => ThemeManager.ItemLongMiddle,
                _ => Color.FromRgb(148, 163, 184)
            };
        }

        private (double rulerH, double waveY, double waveH, double airY, double laneH, double groundY, double eventY, double eventH, double scrollY) GetLayout(double h)
        {
            double rulerH = 26;
            double scrollH = 12;
            double usable = Math.Max(140, h - rulerH - scrollH);
            double waveH = Math.Floor(usable * 0.24);
            double laneH = Math.Floor((usable - waveH) * 0.30);
            double eventH = Math.Max(64, usable - waveH - laneH * 2);
            return (rulerH, rulerH, waveH, rulerH + waveH, laneH, rulerH + waveH + laneH, rulerH + waveH + laneH * 2, eventH, h - scrollH);
        }

        private double TotalDuration => Math.Max(1.0, (GetDuration?.Invoke() ?? 60.0) + Math.Max(0, Off));

        private double TimeToX(double t) => (t - ViewStartSec) * PxPerSec;
        private double XToTime(double x) => ViewStartSec + x / PxPerSec;

        public double SnapTime(double t)
        {
            if (t < 0) t = 0;
            int div = GetSnapDiv?.Invoke() ?? 4;
            if (div <= 0) return Math.Round(t, 4);
            var bpms = GetBpmSegments();
            double segStart = 0, curBpm = bpms[0].bpm;
            foreach (var b in bpms)
            {
                if (t >= b.time) { segStart = b.time; curBpm = b.bpm; }
            }
            double step = 60.0 / curBpm / div;
            return Math.Max(0, Math.Round(segStart + Math.Round((t - segStart) / step) * step, 4));
        }

        // ---- 轻量谱面缓存：可见窗口二分 + 一次性解析，避免每帧全量遍历 JArray ----
        private sealed class ItemInfo
        {
            public int Index;
            public double Raw, End;
            public bool IsLong;
        }

        private List<ItemInfo> _itemCache;
        private int _itemVersion = -1;
        private int _itemCount = -1;
        private double _maxLongSec = 2;
        private Dictionary<int, double> _midTimes = new Dictionary<int, double>();
        private Dictionary<int, double> _divTimes = new Dictionary<int, double>();
        private StreamGeometry _timelineDiamond, _timelineDodge, _timelineMid;

        private static StreamGeometry BuildTimelineGeo(params Point[] pts)
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

        private List<(double raw, double bpm, int beat)> _bpmRawCache;
        private int _bpmVersion = -1;
        private int _bpmCount = -1;

        /// <summary>谱面结构版本（由主窗口在编辑后递增）。</summary>
        public int ChartVersion { get; set; }

        public void InvalidateChartCache() { _itemVersion = -1; _bpmVersion = -1; }

        private void EnsureItemCache(JArray items)
        {
            int count = items?.Count ?? -1;
            if (_itemVersion == ChartVersion && _itemCount == count && _itemCache != null) return;
            _itemVersion = ChartVersion;
            _itemCount = count;
            _itemCache = new List<ItemInfo>();
            _midTimes = new Dictionary<int, double>();
            _divTimes = new Dictionary<int, double>();
            _maxLongSec = 2;
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not JObject o || o.IsAttach()) continue;
                string st = o.GetTypeName() ?? "";
                if (st == "CRAFormatDataNoteLongMiddle") _midTimes[o.GetId()] = o.GetTime();
                else if (st == "CRAFormatDataSuperJumpDivider") _divTimes[o.GetId()] = o.GetTime();
                double raw = o.GetTime();
                bool isLong = o.IsLong() && o.GetEndTime() > raw;
                double end = isLong ? o.GetEndTime() : raw;
                if (isLong) _maxLongSec = Math.Max(_maxLongSec, end - raw);
                _itemCache.Add(new ItemInfo { Index = i, Raw = raw, End = end, IsLong = isLong });
            }
            _itemCache.Sort((a, b) => a.Raw.CompareTo(b.Raw));
        }

        private IEnumerable<(int index, double raw, double end, bool isLong)> EnumerateVisible(double viewEnd)
        {
            if (_itemCache == null) yield break;
            double t0 = ViewStartSec - Off - (_maxLongSec + 0.5);
            double t1 = viewEnd - Off + 0.3;
            int lo = 0, hi = _itemCache.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_itemCache[mid].Raw < t0) lo = mid + 1; else hi = mid;
            }
            for (int k = lo; k < _itemCache.Count; k++)
            {
                var it = _itemCache[k];
                if (it.Raw > t1) break;
                yield return (it.Index, it.Raw, it.End, it.IsLong);
            }
        }

        private void EnsureBpmCache()
        {
            var items = GetItems?.Invoke();
            int count = items?.Count ?? -1;
            if (_bpmVersion == ChartVersion && _bpmCount == count && _bpmRawCache != null) return;
            _bpmVersion = ChartVersion;
            _bpmCount = count;
            _bpmRawCache = new List<(double, double, int)>();
            if (items != null)
            {
                foreach (var tok in items)
                {
                    if (tok is JObject o && o.GetTypeName() == "CRAFormatDataEventBPMBar")
                        _bpmRawCache.Add((o.GetTime(), o.Value<double?>("fBPM") ?? 120.0, o.Value<int?>("iBPMBeat") ?? 4));
                }
            }
            _bpmRawCache.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        }

        public List<(double time, double bpm, int beat)> GetBpmSegments()
        {
            double layerBpm = GetLayerBpm?.Invoke() ?? 0;
            if (layerBpm > 10)
                return new List<(double, double, int)> { (Off, layerBpm, 4) };
            EnsureBpmCache();
            var list = new List<(double time, double bpm, int beat)>();
            if (_bpmRawCache != null)
            {
                foreach (var b in _bpmRawCache)
                    list.Add((b.raw + Off, b.bpm, b.beat));
            }
            list.Sort((a, b) => a.time.CompareTo(b.time));
            if (list.Count == 0) list.Add((0.0, GetDefaultBpm?.Invoke() ?? 120.0, 4));
            return list;
        }

        private double BpmAt(double t)
        {
            double bpm = GetDefaultBpm?.Invoke() ?? 120.0;
            foreach (var b in GetBpmSegments())
                if (t + Off >= b.time) bpm = b.bpm;
            return bpm > 0 ? bpm : 120.0;
        }

        private int? GetLaneFromY(double y, double h)
        {
            var L = GetLayout(h);
            if (y >= L.airY && y < L.airY + L.laneH) return 1;
            if (y >= L.groundY && y < L.groundY + L.laneH) return 0;
            if (y >= L.eventY) return -1;
            return null;
        }

        private (int idx, bool isTail)? HitTest(double x, double y, double h)
        {
            var items = GetItems?.Invoke();
            if (items == null) return null;
            var L = GetLayout(h);
            double t = XToTime(x);
            double tol = 12.0 / PxPerSec;

            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (items[i] is not JObject o) continue;
                string tn = o.GetTypeName();
                if (tn == "CRAFormatDataNoteLongMiddle" || tn == "CRAFormatDataSuperJumpDivider") continue;
                if (LayerLockedFor(o)) continue;
                if (IsSoloActiveLayer?.Invoke() == true && o.IsNote())
                {
                    int activeLayer = GetActiveLayerIndex?.Invoke() ?? 0;
                    if (o.GetEditorLayer() != activeLayer) continue;
                }

                if (o.IsNote())
                {
                    if (y < L.airY || y >= L.eventY) continue;
                    double cy = o.GetLane() == 1 ? L.airY + L.laneH / 2 : L.groundY + L.laneH / 2;
                    double yTol = Math.Max(20, L.laneH * 0.46);
                    if (Math.Abs(y - cy) > yTol) continue;
                }
                else
                {
                    if (y < L.eventY || y >= L.scrollY) continue;
                }

                double t0 = Disp(o.GetTime());
                double t1 = o.IsLong() ? Disp(o.GetEndTime()) : t0;
                if (o.IsLong() && t1 > t0 && Math.Abs(t - t1) <= tol) return (i, true);
                if (t >= t0 - tol && t <= t1 + tol) return (i, false);
            }
            return null;
        }

        private void OnMouseDownHandler(object sender, MouseButtonEventArgs e)
        {
            Focus();
            double h = ActualHeight;
            Point pt = e.GetPosition(this);
            var L = GetLayout(h);

            // 底部横向滚动条
            if (pt.Y >= L.scrollY && e.ChangedButton == MouseButton.Left)
            {
                double total = TotalDuration;
                double w = ActualWidth;
                double thumbW = Math.Max(18, Math.Min(w, w * (Math.Min(total, w / PxPerSec) / total)));
                double thumbX = total > 0 ? Math.Clamp(ViewStartSec / total * w, 0, Math.Max(0, w - thumbW)) : 0;
                if (pt.X >= thumbX && pt.X <= thumbX + thumbW)
                {
                    _scrollGrabOffset = pt.X - thumbX;
                }
                else
                {
                    _scrollGrabOffset = thumbW / 2;
                    ViewStartSec = Math.Clamp((pt.X - _scrollGrabOffset) / w * total, 0, Math.Max(0, total - w / PxPerSec));
                }
                _dragMode = DragMode.Scrollbar;
                CaptureMouse();
                InvalidateVisual();
                return;
            }

            // ruler / 波形区：按下并拖动可拖动播放进度
            if (pt.Y < L.airY)
            {
                OnSeek?.Invoke(Math.Max(0, XToTime(pt.X)));
                _dragMode = DragMode.Scrub;
                CaptureMouse();
                return;
            }

            var items = GetItems?.Invoke();
            if (items == null) return;
            var hit = HitTest(pt.X, pt.Y, h);

            if (e.ChangedButton == MouseButton.Right)
            {
                if (hit.HasValue)
                {
                    if (!SelectedIndices.Contains(hit.Value.idx))
                    {
                        SelectedIndices.Clear();
                        SelectedIndices.Add(hit.Value.idx);
                        OnSelectionChanged?.Invoke(hit.Value.idx);
                    }
                    ShowItemMenu(items[hit.Value.idx] as JObject);
                }
                return;
            }
            if (e.ChangedButton != MouseButton.Left) return;

            EditorTool tool = GetTool?.Invoke() ?? EditorTool.Select;

            if (hit.HasValue && (tool == EditorTool.Select || hit.Value.isTail))
            {
                int idx = hit.Value.idx;
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                if (ctrl)
                {
                    if (!SelectedIndices.Add(idx)) SelectedIndices.Remove(idx);
                }
                else if (!SelectedIndices.Contains(idx))
                {
                    SelectedIndices.Clear();
                    SelectedIndices.Add(idx);
                }
                OnSelectionChanged?.Invoke(SelectedIndices.Count > 0 ? idx : -1);

                OnPushUndo?.Invoke();
                _dragMode = hit.Value.isTail ? DragMode.ResizeTail : DragMode.Move;
                _resizeRequireThreshold = false;
                _resizeThresholdPassed = true;
                _dragStartPt = pt;
                _dragAnchorTime = SnapTime(XToTime(pt.X));
                _dragSnapshot.Clear();
                _dragMiddles.Clear();
                _dragDividers.Clear();
                foreach (int si in SelectedIndices)
                {
                    if (si >= 0 && si < items.Count && items[si] is JObject so)
                    {
                        _dragSnapshot[si] = (Disp(so.GetTime()), Disp(so.GetEndTime()), so.GetLane());
                        if (so.GetTypeName() == "CRAFormatDataNoteOrb")
                        {
                            var ids = (so["LongMiddleIds"] as JArray)?.Values<int>().ToList() ?? new List<int>();
                            var list = new List<(JObject, double)>();
                            foreach (var tokMid in items)
                                if (tokMid is JObject m && m.GetTypeName() == "CRAFormatDataNoteLongMiddle" && ids.Contains(m.GetId()))
                                    list.Add((m, Disp(m.GetTime())));
                            _dragMiddles[si] = list;
                        }
                        else if (so.GetTypeName() == "CRAFormatDataSuperJumpBody")
                        {
                            int divId = so.Value<int?>("DividerId") ?? 0;
                            foreach (var tokDiv in items)
                                if (tokDiv is JObject d && d.GetTypeName() == "CRAFormatDataSuperJumpDivider" && d.GetId() == divId)
                                {
                                    _dragDividers[si] = (d, Disp(d.GetTime()));
                                    break;
                                }
                        }
                    }
                }
                CaptureMouse();
                InvalidateVisual();
                return;
            }

            if (tool == EditorTool.Select)
            {
                // 选择模式下：左键双击空中/地面轨道空白处直接添加标准单音，单击拖动仍为框选
                int? dblLane = GetLaneFromY(pt.Y, h);
                if (e.ClickCount >= 2 && dblLane.HasValue && dblLane.Value >= 0 && !ActiveLayerLocked)
                {
                    tool = EditorTool.Crystal;
                }
                else
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) SelectedIndices.Clear();
                    OnSelectionChanged?.Invoke(-1);
                    _dragMode = DragMode.BoxSelect;
                    _dragStartPt = pt;
                    _dragCurPt = pt;
                    CaptureMouse();
                    InvalidateVisual();
                    return;
                }
            }

            if (ActiveLayerLocked) return;
            int? lane = GetLaneFromY(pt.Y, h);
            if (!lane.HasValue) return;
            double snapped = SnapTime(XToTime(pt.X));

            JObject created = CreateItem(items, tool, snapped - Off, lane.Value);
            if (created != null)
            {
                OnPushUndo?.Invoke();
                items.Add(created);
                int newIdx = items.Count - 1;
                if (created.GetTypeName() == "CRAFormatDataNoteOrb" && (GetAutoMiddle?.Invoke() ?? true))
                {
                    EnsureLongMiddles(items, created);
                }
                SelectedIndices.Clear();
                SelectedIndices.Add(newIdx);
                OnSelectionChanged?.Invoke(newIdx);
                OnChartMutated?.Invoke();

                if (created.IsLong())
                {
                    _dragMode = DragMode.ResizeTail;
                    _dragStartPt = pt;
                    _resizeRequireThreshold = true;
                    _resizeThresholdPassed = false;
                    _dragSnapshot.Clear();
                    _dragMiddles.Clear();
                    _dragDividers.Clear();
                    _dragSnapshot[newIdx] = (created.GetTime(), created.GetEndTime(), created.GetLane());
                    CaptureMouse();
                }
            }
        }

        private void ShowItemMenu(JObject o)
        {
            if (o == null) return;
            var menu = new ContextMenu();
            int[] sel() => SelectedIndices.OrderBy(x => x).ToArray();

            void Add(string header, string action)
            {
                var mi = new MenuItem { Header = header };
                mi.Click += (_, _) => OnContextAction?.Invoke(action, sel());
                menu.Items.Add(mi);
            }

            Add("删除", "delete");
            Add("复制 (Ctrl+C)", "copy");
            if (o.IsNote())
            {
                menu.Items.Add(new Separator());
                Add("翻转上下轨 (F)", "flip");
            }
            if (o.GetTypeName() == "CRAFormatDataNoteOrb")
            {
                menu.Items.Add(new Separator());
                Add("补全中段（每拍一个）", "addmiddle");
                Add("清除全部中段", "clearmiddle");
            }
            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }

        private void OnMouseMoveHandler(object sender, MouseEventArgs e)
        {
            double h = ActualHeight;
            Point pt = e.GetPosition(this);
            int? lane = GetLaneFromY(pt.Y, h);
            _hover = lane.HasValue ? (SnapTime(XToTime(pt.X)), lane.Value) : null;

            if (_dragMode == DragMode.None)
            {
                InvalidateVisual();
                return;
            }

            if (_dragMode == DragMode.Scrub)
            {
                OnSeek?.Invoke(Math.Max(0, XToTime(pt.X)));
                InvalidateVisual();
                return;
            }
            if (_dragMode == DragMode.Scrollbar)
            {
                double total = TotalDuration;
                double w2 = ActualWidth;
                ViewStartSec = Math.Clamp((pt.X - _scrollGrabOffset) / Math.Max(1, w2) * total,
                    0, Math.Max(0, total - w2 / PxPerSec));
                InvalidateVisual();
                return;
            }

            var items = GetItems?.Invoke();
            if (items == null) return;

            if (_dragMode == DragMode.BoxSelect)
            {
                _dragCurPt = pt;
                InvalidateVisual();
                return;
            }

            double curT = SnapTime(XToTime(pt.X));
            if (_dragMode == DragMode.Move)
            {
                double dt = curT - _dragAnchorTime;
                foreach (var kv in _dragSnapshot)
                {
                    if (kv.Key >= 0 && kv.Key < items.Count && items[kv.Key] is JObject so)
                    {
                        double newTd = kv.Value.t0 + dt;
                        double newT = Math.Max(0, newTd - Off);
                        double dur = Math.Max(0, kv.Value.t1 - kv.Value.t0);
                        double shift = newTd - kv.Value.t0;
                        so.SetTime(newT);
                        if (so.IsLong() && dur > 0) so.SetEndTime(newT + dur);
                        if (_dragMiddles.TryGetValue(kv.Key, out var mids))
                            foreach (var (mid, mt) in mids)
                                mid.SetTime(Math.Max(0, mt + shift - Off));
                        if (_dragDividers.TryGetValue(kv.Key, out var divInfo))
                            divInfo.div.SetTime(Math.Max(0, divInfo.t + shift - Off));
                        if (SelectedIndices.Count == 1 && so.IsNote() && so.GetTypeName() != "CRAFormatDataSuperJumpBody"
                            && lane.HasValue && (lane.Value == 0 || lane.Value == 1))
                        {
                            so.SetLane(lane.Value);
                            if (_dragMiddles.TryGetValue(kv.Key, out var laneMids))
                                foreach (var (mid, _) in laneMids)
                                    mid.SetLane(lane.Value);
                        }
                    }
                }
                OnChartMutated?.Invoke();
            }
            else if (_dragMode == DragMode.ResizeTail)
            {
                if (_resizeRequireThreshold && !_resizeThresholdPassed)
                {
                    if (Math.Abs(pt.X - _dragStartPt.X) < 6) return;
                    _resizeThresholdPassed = true;
                }
                foreach (var kv in _dragSnapshot)
                {
                    if (kv.Key >= 0 && kv.Key < items.Count && items[kv.Key] is JObject so)
                    {
                        double endT = Math.Max(so.GetTime() + 0.05, curT - Off);
                        so.SetEndTime(endT);
                        if (so.GetTypeName() == "CRAFormatDataSuperJumpBody")
                        {
                            int divId = so.Value<int?>("DividerId") ?? 0;
                            foreach (var tok in items)
                            {
                                if (tok is JObject d && d.GetId() == divId)
                                {
                                    d.SetTime((so.GetTime() + endT) / 2.0);
                                    break;
                                }
                            }
                        }
                    }
                }
                OnChartMutated?.Invoke();
            }
            InvalidateVisual();
        }

        /// <summary>长条缩尾后，剔除超出 [头, 尾] 区间的中段并同步 LongMiddleIds。</summary>
        public static void TrimMiddles(JArray items, JObject orb)
        {
            double t0 = orb.GetTime(), t1 = orb.GetEndTime();
            var ids = (orb["LongMiddleIds"] as JArray)?.Values<int>().ToList();
            if (ids == null || ids.Count == 0) return;
            var keepIds = new HashSet<int>();
            foreach (int id in ids)
            {
                var m = items.OfType<JObject>().FirstOrDefault(o =>
                    o.GetId() == id && o.GetTypeName() == "CRAFormatDataNoteLongMiddle");
                if (m == null) continue;
                double mt = m.GetTime();
                if (mt > t0 + 0.001 && mt < t1 - 0.001) keepIds.Add(id);
                else items.Remove(m);
            }
            var arr = new JArray();
            foreach (int id in ids)
                if (keepIds.Contains(id)) arr.Add(id);
            orb["LongMiddleIds"] = arr;
        }

        private void OnMouseUpHandler(object sender, MouseButtonEventArgs e)
        {
            if (_dragMode == DragMode.ResizeTail || _dragMode == DragMode.Move)
            {
                var items = GetItems?.Invoke();
                if (items != null)
                {
                    bool autoMid = GetAutoMiddle?.Invoke() ?? false;
                    foreach (var kv in _dragSnapshot)
                    {
                        if (kv.Key >= 0 && kv.Key < items.Count && items[kv.Key] is JObject o
                            && o.GetTypeName() == "CRAFormatDataNoteOrb")
                        {
                            if (_dragMode == DragMode.ResizeTail && _resizeThresholdPassed && autoMid)
                                RebuildLongMiddles(items, o);
                            else
                                TrimMiddles(items, o);
                        }
                    }
                    OnChartMutated?.Invoke();
                    if (SelectedIndices.Count == 1)
                        OnSelectionChanged?.Invoke(SelectedIndices.First());
                }
            }
            if (_dragMode == DragMode.BoxSelect)
            {
                var items = GetItems?.Invoke();
                if (items != null)
                {
                    double xMin = Math.Min(_dragStartPt.X, _dragCurPt.X);
                    double xMax = Math.Max(_dragStartPt.X, _dragCurPt.X);
                    double yMin = Math.Min(_dragStartPt.Y, _dragCurPt.Y);
                    double yMax = Math.Max(_dragStartPt.Y, _dragCurPt.Y);
                    var L = GetLayout(ActualHeight);

                    for (int i = 0; i < items.Count; i++)
                    {
                        if (items[i] is not JObject o || o.IsAttach() || LayerLockedFor(o)) continue;
                        double x = TimeToX(o.GetTime());
                        double y = o.IsNote()
                            ? (o.GetLane() == 1 ? L.airY + L.laneH / 2 : L.groundY + L.laneH / 2)
                            : L.eventY + L.eventH / 2;
                        double endX = o.IsLong() ? TimeToX(o.GetEndTime()) : x;
                        bool xHit = (x >= xMin && x <= xMax) || (endX >= xMin && endX <= xMax) || (x <= xMin && endX >= xMax);
                        if (xHit && y >= yMin && y <= yMax)
                        {
                            SelectedIndices.Add(i);
                        }
                    }
                    OnSelectionChanged?.Invoke(SelectedIndices.Count > 0 ? SelectedIndices.First() : -1);
                }
            }
            _dragMode = DragMode.None;
            ReleaseMouseCapture();
            InvalidateVisual();
        }

        private void OnMouseWheelHandler(object sender, MouseWheelEventArgs e)
        {
            Point pt = e.GetPosition(this);
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                double tAtCursor = XToTime(pt.X);
                double factor = e.Delta > 0 ? 1.15 : 0.87;
                PxPerSec = Math.Clamp(PxPerSec * factor, 60.0, 720.0);
                ViewStartSec = Math.Max(0, tAtCursor - pt.X / PxPerSec);
                OnZoomChanged?.Invoke(PxPerSec);
            }
            else
            {
                double dt = -(e.Delta / 120.0) * (120.0 / PxPerSec);
                ViewStartSec = Math.Max(0, ViewStartSec + dt);
            }
            InvalidateVisual();
        }

        // ---------------- 键盘微调 ----------------
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var items = GetItems?.Invoke();
            if (items == null || SelectedIndices.Count == 0) return;

            int div = GetSnapDiv?.Invoke() ?? 4;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (e.Key == Key.Left || e.Key == Key.Right)
            {
                double dir = e.Key == Key.Right ? 1.0 : -1.0;
                double move;
                if (div <= 0) move = dir * 0.01;
                else
                {
                    double baseT = items[SelectedIndices.First()] is JObject first ? first.GetTime() : (GetPlayTime?.Invoke() ?? 0);
                    double bpm = BpmAt(baseT);
                    double step = shift ? 60.0 / bpm * 4 : 60.0 / bpm / div; // Shift = 一小节
                    move = dir * step;
                }
                OnPushUndo?.Invoke();
                foreach (int idx in SelectedIndices.OrderBy(x => dir > 0 ? -x : x))
                {
                    if (idx >= 0 && idx < items.Count && items[idx] is JObject so)
                    {
                        if (LayerLockedFor(so)) continue;
                        double oldT = so.GetTime();
                        double nt = Math.Max(0, Math.Round(oldT + move, 4));
                        double dur = so.IsLong() ? so.GetEndTime() - oldT : 0;
                        so.SetTime(nt);
                        if (dur > 0) so.SetEndTime(nt + dur);
                        ShiftLinkedAttachments(items, so, nt - oldT);
                    }
                }
                OnChartMutated?.Invoke();
                if (SelectedIndices.Count == 1) OnSelectionChanged?.Invoke(SelectedIndices.First());
                InvalidateVisual();
                e.Handled = true;
            }
            else if (e.Key == Key.Up || e.Key == Key.Down)
            {
                int lane = e.Key == Key.Down ? 0 : 1;
                OnPushUndo?.Invoke();
                foreach (int idx in SelectedIndices)
                {
                    if (idx >= 0 && idx < items.Count && items[idx] is JObject so && so.IsNote() && !LayerLockedFor(so))
                        SetNoteLaneWithAttachments(items, so, lane);
                }
                OnChartMutated?.Invoke();
                if (SelectedIndices.Count == 1) OnSelectionChanged?.Invoke(SelectedIndices.First());
                InvalidateVisual();
                e.Handled = true;
            }
        }

        public static void ShiftLinkedAttachments(JArray items, JObject obj, double delta)
        {
            if (items == null || obj == null || Math.Abs(delta) < 1e-6) return;
            string tn = obj.GetTypeName();
            if (tn == "CRAFormatDataNoteOrb")
            {
                var ids = (obj["LongMiddleIds"] as JArray)?.Values<int>().ToList() ?? new List<int>();
                if (ids.Count == 0) return;
                foreach (var tok in items)
                {
                    if (tok is JObject m && m.GetTypeName() == "CRAFormatDataNoteLongMiddle" && ids.Contains(m.GetId()))
                        m.SetTime(Math.Max(0, m.GetTime() + delta));
                }
            }
            else if (tn == "CRAFormatDataSuperJumpBody")
            {
                int divId = obj.Value<int?>("DividerId") ?? 0;
                if (divId == 0) return;
                foreach (var tok in items)
                {
                    if (tok is JObject d && d.GetTypeName() == "CRAFormatDataSuperJumpDivider" && d.GetId() == divId)
                    {
                        d.SetTime(Math.Max(0, d.GetTime() + delta));
                        break;
                    }
                }
            }
        }

        public static void SetNoteLaneWithAttachments(JArray items, JObject obj, int lane)
        {
            if (obj == null || !obj.IsNote()) return;
            // 大跳主体与分隔点固定在 track 5，不能改到 7/8
            if (obj.GetTypeName() == "CRAFormatDataSuperJumpBody") return;
            obj.SetLane(lane);
            if (items != null && obj.GetTypeName() == "CRAFormatDataNoteOrb")
            {
                var ids = (obj["LongMiddleIds"] as JArray)?.Values<int>().ToList() ?? new List<int>();
                if (ids.Count > 0)
                {
                    foreach (var tok in items)
                        if (tok is JObject m && m.GetTypeName() == "CRAFormatDataNoteLongMiddle" && ids.Contains(m.GetId()))
                            m.SetLane(lane);
                }
            }
        }

        public void RebuildLongMiddles(JArray items, JObject orb)
        {
            if (items == null || orb == null) return;
            var ids = (orb["LongMiddleIds"] as JArray)?.Values<int>().ToList() ?? new List<int>();
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (items[i] is JObject d && d.GetTypeName() == "CRAFormatDataNoteLongMiddle" && ids.Contains(d.GetId()))
                    items.RemoveAt(i);
            }
            orb["LongMiddleIds"] = new JArray();
            EnsureLongMiddles(items, orb);
        }

        // ---------------- 删除与依附对象 ----------------
        public static void RemoveItemAt(JArray items, int idx)
        {
            if (idx < 0 || idx >= items.Count) return;
            var removed = items[idx] as JObject;
            items.RemoveAt(idx);
            if (removed == null) return;
            string tn = removed.GetTypeName();
            if (tn == "CRAFormatDataSuperJumpBody")
            {
                int divId = removed.Value<int?>("DividerId") ?? 0;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i] is JObject d && d.GetId() == divId)
                    {
                        items.RemoveAt(i);
                        break;
                    }
                }
            }
            else if (tn == "CRAFormatDataNoteOrb")
            {
                var ids = (removed["LongMiddleIds"] as JArray)?.Values<int>().ToList() ?? new List<int>();
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i] is JObject d && d.GetTypeName() == "CRAFormatDataNoteLongMiddle" && ids.Contains(d.GetId()))
                        items.RemoveAt(i);
                }
            }
        }

        /// <summary>为长条补齐中段：从头部起每拍一个，距尾不足半拍则停止。</summary>
        public void EnsureLongMiddles(JArray items, JObject orb)
        {
            double t0 = orb.GetTime();
            double t1 = orb.GetEndTime();
            var ids = new JArray();
            int nextId = MaxId(items) + 1;

            double t = t0;
            while (true)
            {
                double beat = 60.0 / BpmAt(t);
                double next = t + beat;
                if (next >= t1 - beat * 0.5) break;
                var mid = new JObject
                {
                    ["$type"] = "CRAFormatDataNoteLongMiddle",
                    ["bIsHard"] = false,
                    ["bAutoHit"] = false,
                    ["hFormatDataID"] = nextId++,
                    ["iFormatDataType"] = 10,
                    ["iFormatDataIndex"] = items.Count,
                    ["iTrackIndexStart"] = orb.Value<int?>("iTrackIndexStart") ?? 8,
                    ["iTrackIndexEnd"] = orb.Value<int?>("iTrackIndexEnd") ?? 8,
                    ["fTimePlay"] = Math.Round(next, 4),
                    ["fTimeTrack"] = Math.Round(next, 4)
                };
                items.Add(mid);
                ids.Add(mid["hFormatDataID"]);
                t = next;
            }
            orb["LongMiddleIds"] = ids;
        }

        public static int MaxId(JArray items)
        {
            int max = 100;
            foreach (var tok in items)
                if (tok is JObject o && o.GetId() > max) max = o.GetId();
            return max;
        }

        // ---------------- 创建对象 ----------------
        public JObject CreateItem(JArray items, EditorTool tool, double t, int lane)
        {
            int nextId = MaxId(items) + 1;
            if (tool == EditorTool.Event || lane == -1)
            {
                return CreateEventItem(GetNewEventType?.Invoke() ?? "CRAFormatDataEventSpeed", t, nextId);
            }

            int ti = lane == 1 ? 7 : 8;
            var obj = new JObject
            {
                ["bAutoHit"] = false,
                ["editorLayer"] = GetActiveLayerIndex?.Invoke() ?? 0,
                ["hFormatDataID"] = nextId,
                ["iFormatDataIndex"] = items.Count,
                ["iTrackIndexStart"] = ti,
                ["iTrackIndexEnd"] = ti,
                ["fTimePlay"] = Math.Round(t, 4),
                ["fTimeTrack"] = Math.Round(t, 4)
            };

            switch (tool)
            {
                case EditorTool.Crystal:
                    obj["$type"] = "CRAFormatDataNoteCrystal"; obj["iFormatDataType"] = 1;
                    obj["bIsDodge"] = false; obj["bIsSimultaneous"] = false; obj["bIsHard"] = false;
                    obj["eInteractorType"] = 0; obj["fDamage"] = 0.0; obj["eFFObjectPreset"] = 0;
                    return obj;
                case EditorTool.Dodge:
                    obj["$type"] = "CRAFormatDataNoteCrystal"; obj["iFormatDataType"] = 1;
                    obj["bIsDodge"] = true; obj["bIsSimultaneous"] = false; obj["bIsHard"] = false;
                    obj["eInteractorType"] = 0; obj["fDamage"] = 0.0; obj["eFFObjectPreset"] = 0;
                    return obj;
                case EditorTool.Orb:
                    obj["$type"] = "CRAFormatDataNoteOrb"; obj["iFormatDataType"] = 2;
                    obj["LongMiddleIds"] = new JArray(); obj["bIsSimultaneous"] = false; obj["bIsHard"] = false;
                    obj["eInteractorType"] = 0; obj["fDamage"] = 0.0; obj["eFFObjectPreset"] = 0;
                    obj.SetEndTime(t + 1.0);
                    return obj;
                case EditorTool.MultiHit:
                    obj["$type"] = "CRAFormatDataNoteMultiHit"; obj["iFormatDataType"] = 6;
                    obj["iHitCount"] = 0; obj["fDamage"] = 0.0; obj["isNotMoveCameraPosition"] = false;
                    obj.SetEndTime(t + 1.0);
                    return obj;
                case EditorTool.Coin:
                    obj["$type"] = "CRAFormatDataCoin"; obj["iFormatDataType"] = 5; obj["bIsHard"] = false;
                    return obj;
                case EditorTool.SuperJump:
                    int divId = nextId + 1;
                    // 与官方一致：大跳主体与分隔点都挂在 track 5（加载器固定判定为空中轨）
                    items.Add(new JObject
                    {
                        ["$type"] = "CRAFormatDataSuperJumpDivider",
                        ["MaxHeight"] = 6.0,
                        ["bAutoHit"] = false,
                        ["hFormatDataID"] = divId,
                        ["iFormatDataType"] = 4,
                        ["iFormatDataIndex"] = items.Count,
                        ["iTrackIndexStart"] = 5,
                        ["iTrackIndexEnd"] = 5,
                        ["fTimePlay"] = Math.Round(t + 0.5, 4),
                        ["fTimeTrack"] = Math.Round(t + 0.5, 4)
                    });
                    obj["$type"] = "CRAFormatDataSuperJumpBody"; obj["iFormatDataType"] = 3;
                    obj["FirstEase"] = 3; obj["SecondEase"] = 12; obj["DividerId"] = divId;
                    obj["bIsSimultaneous"] = false; obj["eInteractorType"] = 0; obj["fDamage"] = 0.0; obj["eFFObjectPreset"] = 0;
                    obj["iTrackIndexStart"] = 5; obj["iTrackIndexEnd"] = 5;
                    obj.SetEndTime(t + 1.8);
                    return obj;
            }
            return null;
        }

        public JObject CreateEventItem(string typeName, double t, int id)
        {
            var info = ChartConstants.FindEvent(typeName);
            int editorLayer = GetActiveLayerIndex?.Invoke() ?? 0;
            int formatType = info?.FormatType ?? 0;
            var obj = new JObject
            {
                ["$type"] = typeName,
                ["bAutoHit"] = false,
                ["editorLayer"] = editorLayer,
                ["hFormatDataID"] = id,
                ["iFormatDataIndex"] = 0,
                ["iTrackIndexStart"] = 0,
                ["iTrackIndexEnd"] = 0,
                ["fTimePlay"] = Math.Round(t, 4),
                ["fTimeTrack"] = Math.Round(t, 4),
                ["iFormatDataType"] = formatType
            };
            switch (typeName)
            {
                case "CRAFormatDataEventBPMBar":
                    obj["iBPMBarType"] = 0; obj["fBPM"] = GetDefaultBpm?.Invoke() ?? 120.0;
                    obj["fBPMBase"] = 4.0; obj["iBPMBeat"] = 4; return obj;
                case "CRAFormatDataEventSpeed":
                    obj["fSpeedValue"] = 1.3; return obj;
                case "CRAFormatDataFFChangeTheme":
                    obj["eFFTheme"] = 1; return obj;
                case "CRAFormatDataFFChangeGroundPreset":
                    obj["eFFGroundPreset"] = 0; return obj;
                case "CRAFormatDataFFChangeCameraPreset":
                    obj["eFFCameraPreset"] = 0; obj.SetEndTime(t + 0.5); return obj;
                case "CRAFormatDataFFChangeAnglePreset":
                    obj["eFFAnglePreset"] = 0; return obj;
                case "CRAFormatDataFFChangeMoodPreset":
                    obj["eFFMoodPreset"] = 1; return obj;
                case "CRAFormatDataFFChangeSpeedPreset":
                    obj["eFFSpeedPreset"] = 0; return obj;
                case "CRAFormatDataFFNPCAction":
                    obj["eFFNPCAction"] = 0;
                    obj["iTrackIndexStart"] = 1; obj["iTrackIndexEnd"] = 1;
                    return obj;
                case "CRAFormatDataStopMap":
                    // 与官方一致：track 6 表示地图控制事件
                    obj["iTrackIndexStart"] = 6; obj["iTrackIndexEnd"] = 6;
                    obj.SetEndTime(t + 1.0);
                    return obj;
                case "CRAFormatDataEndOfTrack":
                    return obj;
                default: return null;
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            var L = GetLayout(h);
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var tfMono = new Typeface("Consolas");
            var tfUi = new Typeface("Microsoft YaHei UI");

            // ---- 背景 ----
            byte tintAir = (byte)(ThemeManager.IsLight ? 22 : 15);
            byte tintGround = (byte)(ThemeManager.IsLight ? 20 : 13);
            dc.DrawRectangle(BrushOf(ThemeManager.TimelineRulerBg), null, new Rect(0, 0, w, L.rulerH));
            dc.DrawRectangle(BrushOf(ThemeManager.TimelineWaveBg), null, new Rect(0, L.waveY, w, L.waveH));
            dc.DrawRectangle(BrushOf(ColorBgLanes), null, new Rect(0, L.airY, w, L.eventY - L.airY));
            dc.DrawRectangle(BrushOf(ColorBgEvent), null, new Rect(0, L.eventY, w, L.eventH));
            dc.DrawRectangle(BrushOf(Color.FromArgb(tintAir, ColorAirLane.R, ColorAirLane.G, ColorAirLane.B)), null, new Rect(0, L.airY, w, L.laneH));
            dc.DrawRectangle(BrushOf(Color.FromArgb(tintGround, ColorGroundLane.R, ColorGroundLane.G, ColorGroundLane.B)), null, new Rect(0, L.groundY, w, L.laneH));

            // ---- A-B 循环区间 ----
            var loop = GetLoopRange?.Invoke();
            if (loop.HasValue)
            {
                double ax = Math.Max(0, TimeToX(loop.Value.a));
                double bx = Math.Min(w, TimeToX(loop.Value.b));
                if (bx > 0 && ax < w)
                {
                    var gc = ThemeManager.ItemCoin;
                    dc.DrawRectangle(BrushOf(Color.FromArgb((byte)(ThemeManager.IsLight ? 26 : 34), gc.R, gc.G, gc.B)), null, new Rect(ax, L.rulerH, Math.Max(0, bx - ax), h - L.rulerH));
                    var loopPen = PenOf(gc, 1.4);
                    dc.DrawLine(loopPen, new Point(ax, 0), new Point(ax, h));
                    dc.DrawLine(loopPen, new Point(bx, 0), new Point(bx, h));
                    var loopBrush = BrushOf(ThemeManager.ItemCoin);
                    dc.DrawText(TextOf("A", tfMono, 10, loopBrush, true, dpi), new Point(ax + 3, 1));
                    dc.DrawText(TextOf("B", tfMono, 10, loopBrush, true, dpi), new Point(bx + 3, 1));
                }
            }
            else
            {
                double? onlyA = GetLoopA?.Invoke();
                if (onlyA.HasValue)
                {
                    double ax = TimeToX(onlyA.Value);
                    if (ax >= 0 && ax <= w)
                    {
                        var gc = ThemeManager.ItemCoin;
                        dc.DrawLine(PenOf(gc, 1.4), new Point(ax, 0), new Point(ax, h));
                        dc.DrawText(TextOf("A", tfMono, 10, BrushOf(gc), true, dpi), new Point(ax + 3, 1));
                    }
                }
            }

            // ---- 波形（缓存位图，播放时不重建） ----
            float[] peaks = GetWaveform?.Invoke();
            double dur = Math.Max(1.0, GetDuration?.Invoke() ?? 60.0);
            EnsureWaveCache(w, L.waveY, L.waveH, peaks, dur);
            if (_waveCache != null)
                dc.DrawImage(_waveCache, new Rect(0, L.waveY, w, L.waveH));

            // 波形轨标签
            {
                string audio = GetAudioName?.Invoke();
                string waveLabel = peaks != null && peaks.Length > 0
                    ? $"音频波形 · {audio}"
                    : "音频轨道 · 拖入音频文件，或在左侧点「导入音频」";
                var wl = TextOf(waveLabel, tfUi, 10, BrushOf(ThemeManager.TimelineTextDim), false, dpi);
                var wlb = ThemeManager.TimelineWaveBg;
                dc.DrawRoundedRectangle(
                    BrushOf(Color.FromArgb((byte)(ThemeManager.IsLight ? 238 : 228), wlb.R, wlb.G, wlb.B)),
                    PenOf(ThemeManager.TimelineDivider, 0.8),
                    new Rect(6, L.waveY + 4, wl.Width + 14, 18), 5, 5);
                dc.DrawText(wl, new Point(13, L.waveY + 6));
            }

            // ---- 网格与小节 ----
            double viewEnd = XToTime(w);
            var items = GetItems?.Invoke();
            EnsureItemCache(items);
            var bpms = GetBpmSegments();
            int div = Math.Max(1, GetSnapDiv?.Invoke() ?? 4);
            var penBar = PenOf(ThemeManager.TimelineGridBar, 1.4);
            var penBeat = PenOf(ThemeManager.TimelineGridBeat, 1.0);
            var penSub = PenOf(ThemeManager.TimelineGridSub, 1.0);

            for (int i = 0; i < bpms.Count; i++)
            {
                double segStart = bpms[i].time;
                double segEnd = i + 1 < bpms.Count ? bpms[i + 1].time : Math.Max(viewEnd, dur);
                double bpm = bpms[i].bpm > 0 ? bpms[i].bpm : 120.0;
                int beatsPerBar = bpms[i].beat > 0 ? bpms[i].beat : 4;
                double subSec = 60.0 / bpm / div;
                double subPx = subSec * PxPerSec;
                int lineStep = subPx >= 3.5 ? 1 : Math.Max(1, (int)Math.Ceiling(3.5 / Math.Max(0.1, subPx)));

                int s0 = Math.Max(0, (int)Math.Floor((ViewStartSec - segStart) / subSec));
                int s1 = (int)Math.Ceiling((Math.Min(viewEnd, segEnd) - segStart) / subSec);
                for (int s = s0; s <= s1; s++)
                {
                    double t = segStart + s * subSec;
                    double x = Math.Round(TimeToX(t)) + 0.5;
                    if (x < 0 || x > w) continue;
                    bool isBeat = s % div == 0;
                    int beatNum = (int)Math.Round((double)s / div);
                    bool isBar = isBeat && beatNum % beatsPerBar == 0;
                    if (!isBeat && !isBar && s % lineStep != 0) continue;
                    dc.DrawLine(isBar ? penBar : (isBeat ? penBeat : penSub), new Point(x, L.rulerH), new Point(x, h));
                    if (isBar)
                    {
                        string barText = PxPerSec >= 150 ? $"M{(beatNum / beatsPerBar) + 1} · {t:F1}s" : $"M{(beatNum / beatsPerBar) + 1}";
                        var ft = TextOf(barText, tfMono, 10, BrushOf(ThemeManager.TimelineTextDim), true, dpi);
                        dc.DrawText(ft, new Point(x + 4, 5));
                    }
                    if (s == 0 && i > 0)
                    {
                        var bpmFt = TextOf($"♩ {bpm:F0}", tfMono, 10, BrushOf(Color.FromRgb(34, 160, 107)), true, dpi);
                        dc.DrawText(bpmFt, new Point(x + 4, 16));
                    }
                }
            }

            // ---- 行分隔与标签 ----
            var divPen = PenOf(ThemeManager.TimelineDivider, ThemeManager.IsGame ? 1.3 : 1.0);
            dc.DrawLine(divPen, new Point(0, L.waveY), new Point(w, L.waveY));
            dc.DrawLine(divPen, new Point(0, L.airY), new Point(w, L.airY));
            dc.DrawLine(divPen, new Point(0, L.groundY), new Point(w, L.groundY));
            dc.DrawLine(divPen, new Point(0, L.eventY), new Point(w, L.eventY));
            if (ThemeManager.IsGame)
            {
                // 剧场主题：轨道中心导引微光细线
                dc.DrawLine(PenOf(Color.FromArgb(40, ColorAirLane.R, ColorAirLane.G, ColorAirLane.B), 1),
                    new Point(0, L.airY + L.laneH / 2), new Point(w, L.airY + L.laneH / 2));
                dc.DrawLine(PenOf(Color.FromArgb(40, ColorGroundLane.R, ColorGroundLane.G, ColorGroundLane.B), 1),
                    new Point(0, L.groundY + L.laneH / 2), new Point(w, L.groundY + L.laneH / 2));
            }

            // 行标签绘制在对象之后（见下方），避免被音符图形遮挡

            // ---- 对象（只遍历可见窗口内的对象） ----
            double[] eventRowEndX = { -100, -100, -100 };
            if (items != null)
            {
                foreach (var vi in EnumerateVisible(viewEnd))
                {
                    int i = vi.index;
                    if (items[i] is not JObject o) continue;
                    string tn = o.GetTypeName();

                    double x1 = TimeToX(Disp(o.GetTime()));
                    bool isLong = o.IsLong() && o.GetEndTime() > o.GetTime();
                    double x2 = isLong ? TimeToX(Disp(o.GetEndTime())) : x1;
                    if (x2 < -30 || x1 > w + 30) continue;

                    bool isNote = o.IsNote();
                    if (isNote && IsSoloActiveLayer?.Invoke() == true)
                    {
                        int activeLayer = GetActiveLayerIndex?.Invoke() ?? 0;
                        if (o.GetEditorLayer() != activeLayer) continue;
                    }
                    bool opPushed = false;
                    if (isNote)
                    {
                        double layerOp = LayerOpacityFor(o);
                        if (layerOp < 0.999) { dc.PushOpacity(layerOp); opPushed = true; }
                    }
                    double cy = isNote ? (o.GetLane() == 1 ? L.airY + L.laneH / 2 : L.groundY + L.laneH / 2) : L.eventY + L.eventH / 2 + 8;
                    Color col = GetItemColor(o);
                    bool sel = SelectedIndices.Contains(i);

                    var stroke = sel
                        ? PenOf(ThemeManager.IsLight ? ThemeManager.TimelineSelection : Colors.White, 2.0)
                        : PenOf(LayerStrokeFor(o), 1.1);

                    if (isLong)
                    {
                        var fill = BrushOf(Color.FromArgb(72, col.R, col.G, col.B));
                        Color layerBar = LayerBarColorFor(o);
                        var barPen = sel
                            ? PenOf(ThemeManager.IsLight ? ThemeManager.TimelineSelection : Colors.White, 2.0)
                            : PenOf(Color.FromArgb(215, layerBar.R, layerBar.G, layerBar.B), 1.4);
                        double bh = isNote ? 20 : 10;
                        double by = isNote ? cy - bh / 2 : L.eventY + L.eventH - 15;
                        dc.DrawRoundedRectangle(fill, barPen, new Rect(x1, by, Math.Max(8, x2 - x1), bh), 5, 5);

                        if (isNote)
                        {
                            // 头 / 尾标记
                            dc.DrawEllipse(BrushOf(col), null, new Point(x1, cy), 4.2, 4.2);
                            dc.DrawRectangle(Brushes.White, null, new Rect(x2 - 1.5, cy - 11, 3, 22));

                            // 长条中段节点
                            if (tn == "CRAFormatDataNoteOrb")
                            {
                                var mids = (o["LongMiddleIds"] as JArray)?.Values<int>().ToList() ?? new List<int>();
                                foreach (int midId in mids)
                                {
                                    if (_midTimes.TryGetValue(midId, out double midT))
                                    {
                                        double mx = TimeToX(Disp(midT));
                                        if (mx >= -10 && mx <= w + 10)
                                        {
                                            dc.PushTransform(new TranslateTransform(mx, cy));
                                            dc.DrawGeometry(BrushOf(ThemeManager.IsLight ? Color.FromRgb(60, 128, 124) : Color.FromRgb(230, 250, 250)), null, _timelineMid);
                                            dc.Pop();
                                        }
                                    }
                                }
                            }
                            else if (tn == "CRAFormatDataNoteMultiHit")
                            {
                                var hatch = PenOf(ThemeManager.IsLight ? Color.FromArgb(110, 70, 82, 104) : Color.FromArgb(120, 255, 255, 255), 1);
                                for (double hx = x1 + 8; hx < x2; hx += 9)
                                    dc.DrawLine(hatch, new Point(hx, cy - 8), new Point(hx + 5, cy + 8));
                            }
                            else if (tn == "CRAFormatDataSuperJumpBody")
                            {
                                // 跳跃弧线
                                var arcPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 120, 220, 230)), 1.5) { DashStyle = DashStyles.Dash };
                                var arc = new StreamGeometry();
                                using (var g = arc.Open())
                                {
                                    g.BeginFigure(new Point(x1, cy - 6), false, false);
                                    g.QuadraticBezierTo(new Point((x1 + x2) / 2, cy - 30), new Point(x2, cy - 6), true, false);
                                }
                                dc.DrawGeometry(null, arcPen, arc);
                                // 顶点星标
                                if (_divTimes.TryGetValue(o.Value<int?>("DividerId") ?? 0, out double dvT))
                                {
                                    double dx = TimeToX(Disp(dvT));
                                    dc.DrawEllipse(BrushOf(Color.FromRgb(255, 236, 170)), null, new Point(dx, cy - 26), 3.5, 3.5);
                                }
                            }
                        }
                    }

                    if (isNote)
                    {
                        var colBrush = BrushOf(col);
                        if (sel)
                            dc.DrawEllipse(BrushOf(Color.FromArgb(46, col.R, col.G, col.B)), null, new Point(x1, cy), 14, 14);
                        if (tn == "CRAFormatDataCoin")
                        {
                            dc.DrawEllipse(colBrush, stroke, new Point(x1, cy), 7.8, 7.8);
                            dc.DrawEllipse(BrushOf(Color.FromRgb(255, 246, 210)), null, new Point(x1, cy), 3.2, 3.2);
                        }
                        else if (o.IsDodge())
                        {
                            dc.PushTransform(new TranslateTransform(x1, cy));
                            dc.DrawGeometry(colBrush, stroke, _timelineDodge);
                            dc.Pop();
                            dc.DrawText(TextOf("!", tfUi, 11, Brushes.White, false, dpi), new Point(x1 - 1.5, cy - 4));
                        }
                        else
                        {
                            dc.PushTransform(new TranslateTransform(x1, cy));
                            dc.DrawGeometry(colBrush, stroke, _timelineDiamond);
                            dc.Pop();
                            dc.DrawRectangle(BrushOf(ThemeManager.IsLight ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(115, 255, 255, 255)), null, new Rect(x1 - 1.4, cy - 5, 2.8, 10));
                        }
                    }
                    else if (tn != "CRAFormatDataStopMap")
                    {
                        // 事件旗标与错行防重叠标签（全部在上方轨名条之下渲染）
                        var flagBrush = BrushOf(col);
                        double flagTop = L.eventY + 24;
                        double flagH = Math.Max(10, L.eventH - 28);
                        dc.DrawRoundedRectangle(flagBrush, sel ? PenOf(Colors.White, 1.5) : PenOf(ThemeManager.TimelineOutline, 0.9),
                            new Rect(x1 - 3, flagTop, 6, flagH), 2, 2);
                        string lbl = FormatEventLabel(o);
                        if (!string.IsNullOrEmpty(lbl))
                        {
                            var ft = TextOf(lbl, tfMono, 9.8, flagBrush, true, dpi);
                            double lx = x1 + 5;
                            int chosenRow = 0;
                            for (int r = 0; r < eventRowEndX.Length; r++)
                            {
                                if (lx >= eventRowEndX[r] + 6)
                                {
                                    chosenRow = r;
                                    break;
                                }
                                chosenRow = (r + 1) % eventRowEndX.Length;
                            }
                            double ly = L.eventY + 24 + chosenRow * 14;
                            if (ly + 13 <= L.eventY + L.eventH)
                            {
                                var evBg = ThemeManager.TimelineEventBg;
                                dc.DrawRoundedRectangle(BrushOf(Color.FromArgb(205, evBg.R, evBg.G, evBg.B)), null,
                                    new Rect(lx - 2, ly - 1, ft.Width + 6, 13), 3, 3);
                                dc.DrawText(ft, new Point(lx + 1, ly));
                                eventRowEndX[chosenRow] = Math.Max(eventRowEndX[chosenRow], lx + ft.Width + 6);
                            }
                        }
                    }
                    else
                    {
                        string stopLbl = $"停顿 {o.GetEndTime() - o.GetTime():F2}s";
                        dc.DrawText(TextOf(stopLbl, tfMono, 9.8, BrushOf(col), true, dpi),
                            new Point(x1 + 5, L.eventY + L.eventH - 29));
                    }
                    if (opPushed) dc.Pop();
                }
            }

            // ---- 行标签（统一胶囊标签 + 左侧色条） ----
            void LaneTag(string text, Color color, double y)
            {
                var ft = TextOf(text, tfUi, 10.5, BrushOf(color), false, dpi);
                var bgc = ThemeManager.TimelineLaneBg;
                var pillRect = new Rect(6, y + 3, ft.Width + 20, 19);
                dc.DrawRoundedRectangle(BrushOf(Color.FromArgb((byte)(ThemeManager.IsLight ? 242 : 234), bgc.R, bgc.G, bgc.B)),
                    PenOf(ThemeManager.TimelineDivider, 0.8), pillRect, 5, 5);
                dc.DrawRoundedRectangle(BrushOf(color), null, new Rect(10, y + 7, 3, 11), 1.5, 1.5);
                dc.DrawText(ft, new Point(17, y + 5.5));
            }
            LaneTag("JUMP / 空中轨", ColorAirLane, L.airY);
            LaneTag("GROUND / 地面轨", ColorGroundLane, L.groundY);
            LaneTag("EVENTS / 演出事件", ThemeManager.TimelineTextDim, L.eventY);

            if (items != null && !items.OfType<JObject>().Any(o => !o.IsNote() && !o.IsAttach()))
            {
                var hint = TextOf("暂无演出事件 · 右侧「在指针处添加演出事件」", tfUi, 11, BrushOf(ThemeManager.TimelineTextDim), false, dpi);
                dc.DrawText(hint, new Point(12, L.eventY + 42));
            }

            // ---- 框选 ----
            if (_dragMode == DragMode.BoxSelect)
            {
                var r = new Rect(_dragStartPt, _dragCurPt);
                dc.DrawRectangle(BrushOf(Color.FromArgb(36, ColorSelection.R, ColorSelection.G, ColorSelection.B)),
                    PenOf(ColorSelection, 1), r);
            }

            // ---- Hover 预览 ----
            if (_hover.HasValue && (GetTool?.Invoke() ?? EditorTool.Select) != EditorTool.Select)
            {
                double gx = TimeToX(_hover.Value.time);
                int gl = _hover.Value.lane;
                if (gl == 0 || gl == 1)
                {
                    double gy = gl == 1 ? L.airY + L.laneH / 2 : L.groundY + L.laneH / 2;
                    var hc = gl == 1 ? ColorAirLane : ColorGroundLane;
                    dc.DrawEllipse(BrushOf(Color.FromArgb(90, hc.R, hc.G, hc.B)), null, new Point(gx, gy), 8, 8);
                    dc.DrawLine(PenOf(Color.FromArgb(70, hc.R, hc.G, hc.B), 1), new Point(gx, L.airY), new Point(gx, L.groundY + L.laneH));
                }
            }

            // ---- 播放头 ----
            double px = TimeToX(GetPlayTime?.Invoke() ?? 0);
            if (px >= 0 && px <= w)
            {
                if (ThemeManager.IsGame)
                {
                    var glow = BrushOf(Color.FromArgb(26, ColorPlayhead.R, ColorPlayhead.G, ColorPlayhead.B));
                    dc.DrawRectangle(glow, null, new Rect(px - 3, 0, 6, h));
                }
                var phPen = PenOf(ThemeManager.TimelinePlayhead, 1.8);
                dc.DrawLine(phPen, new Point(px, 0), new Point(px, h));
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    if (ThemeManager.IsGame)
                    {
                        g.BeginFigure(new Point(px, 0), true, true);
                        g.LineTo(new Point(px + 6, 5), true, false);
                        g.LineTo(new Point(px, 10), true, false);
                        g.LineTo(new Point(px - 6, 5), true, false);
                    }
                    else
                    {
                        g.BeginFigure(new Point(px - 5, 0), true, true);
                        g.LineTo(new Point(px + 5, 0), true, false);
                        g.LineTo(new Point(px, 7), true, false);
                    }
                }
                dc.DrawGeometry(BrushOf(ColorPlayhead), null, tri);
            }

            // ---- 底部横向滚动条（可拖动） ----
            {
                dc.DrawRectangle(BrushOf(ThemeManager.TimelineRulerBg), null, new Rect(0, L.scrollY, w, 12));
                double total = TotalDuration;
                double barY = L.scrollY + 3.5;
                dc.DrawRoundedRectangle(BrushOf(ThemeManager.TimelineGridBeat), null, new Rect(4, barY, Math.Max(0, w - 8), 5), 2.5, 2.5);
                double viewDur = w / PxPerSec;
                double thumbW = Math.Max(24, (w - 8) * Math.Min(1.0, viewDur / total));
                double thumbX = 4 + Math.Clamp(ViewStartSec / total * (w - 8), 0, Math.Max(0, w - 8 - thumbW));
                dc.DrawRoundedRectangle(BrushOf(ThemeManager.IsGame ? ThemeManager.ItemCoin : ThemeManager.TimelineTextDim), null, new Rect(thumbX, barY, thumbW, 5), 2.5, 2.5);
            }
        }

        private static string FormatEventLabel(JObject o)
        {
            var ev = ChartConstants.FindEvent(o.GetTypeName());
            string type = ev?.Display ?? o.GetTypeName().Replace("CRAFormatData", "");
            return o.GetTypeName() switch
            {
                "CRAFormatDataEventBPMBar" => $"BPM {o["fBPM"]?.Value<double?>() ?? 120:F0}",
                "CRAFormatDataEventSpeed" => $"速度 {o["fSpeedValue"]?.Value<double?>() ?? 1:F2}x",
                "CRAFormatDataFFChangeTheme" => $"主题 {o["eFFTheme"]?.Value<int?>() ?? 0}",
                "CRAFormatDataFFChangeGroundPreset" => $"地面 {ChartConstants.GroundOptions[Math.Clamp(o["eFFGroundPreset"]?.Value<int?>() ?? 0, 0, 2)]}",
                "CRAFormatDataFFChangeCameraPreset" => "镜头",
                "CRAFormatDataFFChangeAnglePreset" => $"倾角 {o["eFFAnglePreset"]?.Value<int?>() ?? 0}",
                "CRAFormatDataFFChangeMoodPreset" => ChartConstants.MoodOptions[Math.Clamp(o["eFFMoodPreset"]?.Value<int?>() ?? 0, 0, 2)],
                "CRAFormatDataFFChangeSpeedPreset" => "冲刺特效",
                "CRAFormatDataFFNPCAction" => $"NPC{o["iTrackIndexStart"]?.Value<int?>() ?? 1} {ChartConstants.NPCActionOptions[Math.Clamp(o["eFFNPCAction"]?.Value<int?>() ?? 0, 0, 2)]}",
                "CRAFormatDataEndOfTrack" => "曲终",
                _ => type
            };
        }
    }
}