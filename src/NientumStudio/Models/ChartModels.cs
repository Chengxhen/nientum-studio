using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;

namespace NientumStudio.Models
{
    public class DifficultyMeta
    {
        public int level { get; set; }
        public string chartFile { get; set; }
    }

    public class DifficultiesMap
    {
        public DifficultyMeta normal { get; set; } = new DifficultyMeta { level = 5, chartFile = "normal.json" };
        public DifficultyMeta hard { get; set; } = new DifficultyMeta { level = 0, chartFile = "hard.json" };
        public DifficultyMeta extreme { get; set; } = new DifficultyMeta { level = 0, chartFile = "extreme.json" };
    }

    public class CustomTrackMeta
    {
        public string id { get; set; }
        public string title { get; set; } = "Untitled";
        public string localizedTitle { get; set; } = "";
        public string titleSuffix { get; set; } = "[Custom]";
        public string artist { get; set; } = "Unknown";
        public string charter { get; set; } = "";
        public float bpm { get; set; } = 120f;
        public float offsetMs { get; set; } = 0f;
        public float previewStartSec { get; set; } = 0f;
        public float previewDurationSec { get; set; } = 15f;
        public string audioFile { get; set; } = "";
        public string coverFile { get; set; } = "";
        public int defaultBeat { get; set; } = 4;
        public string bgFile { get; set; } = "";
        public float bgDim { get; set; } = 0.35f;
        public float bgVideoOffsetSec { get; set; } = 0f;
        public float bgScale { get; set; } = 1.0f;
        public float bgPosX { get; set; } = 0f;
        public float bgPosY { get; set; } = 0f;
        public float bgmGain { get; set; } = 1.0f;
        public bool isOriginal { get; set; } = false;
        // 官方曲目副本标记：只写入本地工作区，绝不进入游戏目录
        public bool isOfficialCopy { get; set; } = false;
        public string officialKey { get; set; } = "";
        public DifficultiesMap difficulties { get; set; } = new DifficultiesMap();
    }

    public class SongEntry
    {
        public string FolderName { get; set; }
        public string FolderPath { get; set; }
        public CustomTrackMeta Meta { get; set; }
        public bool HasAudio { get; set; }
        public bool SyncedToGame { get; set; }
        public bool IsOfficial { get; set; }
        public BitmapImage CoverImage { get; set; }
        public string DisplayTitle => Meta?.title ?? FolderName;
        public string DisplaySub => $"{Meta?.artist ?? "Unknown"}";
        public string BadgeAudio => HasAudio ? "有音频" : (IsOfficial ? "官方音轨" : "缺音频");
        public string BadgeSync => IsOfficial ? "官方副本" : (SyncedToGame ? "已同步" : "未同步");
        private static readonly Brush FallbackOfficialBadgeBrush = FreezeBrush(Color.FromRgb(224, 112, 192));
        private static readonly Brush FallbackSyncedBadgeBrush = FreezeBrush(Color.FromRgb(227, 184, 92));
        private static readonly Brush FallbackUnsyncedBadgeBrush = FreezeBrush(Color.FromRgb(95, 107, 130));
        private static Brush FreezeBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        public Brush BadgeSyncBrush
        {
            get
            {
                string key = IsOfficial ? "AccentJump" : (SyncedToGame ? "AccentGold" : "TextDim");
                if (System.Windows.Application.Current?.TryFindResource(key) is Brush b) return b;
                return IsOfficial ? FallbackOfficialBadgeBrush : (SyncedToGame ? FallbackSyncedBadgeBrush : FallbackUnsyncedBadgeBrush);
            }
        }
        public override string ToString() => DisplayTitle;
    }

    /// <summary>事件类型元信息：驱动快速事件下拉、时间轴配色与检查器。</summary>
    public sealed class EventTypeInfo
    {
        public string TypeName;
        public string Display;
        public int FormatType;
        public Color Color;
        public bool HasDuration;
        public string Hint;
    }

    public static class ChartConstants
    {
        public static readonly HashSet<string> NoteTypes = new HashSet<string>
        {
            "CRAFormatDataNoteCrystal",
            "CRAFormatDataNoteOrb",
            "CRAFormatDataNoteMultiHit",
            "CRAFormatDataCoin",
            "CRAFormatDataSuperJumpBody"
        };

        // 带尾时间的对象（长条/连打/大跳/镜头过渡/卷轴停顿）
        public static readonly HashSet<string> LongTypes = new HashSet<string>
        {
            "CRAFormatDataNoteOrb",
            "CRAFormatDataNoteMultiHit",
            "CRAFormatDataSuperJumpBody",
            "CRAFormatDataFFChangeCameraPreset",
            "CRAFormatDataStopMap"
        };

        // 附属对象：不在时间轴上直接显示为独立音符
        public static readonly HashSet<string> AttachTypes = new HashSet<string>
        {
            "CRAFormatDataNoteLongMiddle",
            "CRAFormatDataSuperJumpDivider"
        };

        public static readonly EventTypeInfo[] EventTypes =
        {
            new EventTypeInfo { TypeName = "CRAFormatDataEventBPMBar", Display = "BPM 变化", FormatType = 100, Color = Color.FromRgb(52, 211, 153), Hint = "设定 BPM 与拍号，滚动网格同步变化" },
            new EventTypeInfo { TypeName = "CRAFormatDataEventSpeed", Display = "卷轴速度", FormatType = 101, Color = Color.FromRgb(96, 165, 250), Hint = "音符滚动速度倍率（与 BPM 无关）" },
            new EventTypeInfo { TypeName = "CRAFormatDataFFChangeTheme", Display = "场景主题", FormatType = 700, Color = Color.FromRgb(227, 184, 92), Hint = "切换演出主题（仅 12 个合法值，越界会被回退）" },
            new EventTypeInfo { TypeName = "CRAFormatDataFFChangeGroundPreset", Display = "地面样式", FormatType = 705, Color = Color.FromRgb(167, 139, 250), Hint = "切换地面轨道样式" },
            new EventTypeInfo { TypeName = "CRAFormatDataFFChangeAnglePreset", Display = "轨道倾角", FormatType = 701, Color = Color.FromRgb(244, 114, 182), Hint = "改变轨道倾斜角度" },
            new EventTypeInfo { TypeName = "CRAFormatDataFFChangeCameraPreset", Display = "镜头预设", FormatType = 704, Color = Color.FromRgb(56, 189, 248), HasDuration = true, Hint = "镜头位置/缩放变换，可设过渡时长" },
            new EventTypeInfo { TypeName = "CRAFormatDataFFChangeMoodPreset", Display = "氛围", FormatType = 702, Color = Color.FromRgb(251, 146, 60), Hint = "安静 / 常规 / 高潮" },
            new EventTypeInfo { TypeName = "CRAFormatDataFFChangeSpeedPreset", Display = "冲刺特效", FormatType = 703, Color = Color.FromRgb(244, 63, 94), Hint = "角色冲刺视觉特效" },
            new EventTypeInfo { TypeName = "CRAFormatDataFFNPCAction", Display = "NPC 动作", FormatType = 706, Color = Color.FromRgb(94, 234, 212), Hint = "NPC 出场 / 动作 / 退场（NPC 1-5）" },
            new EventTypeInfo { TypeName = "CRAFormatDataStopMap", Display = "卷轴停顿", FormatType = 309, Color = Color.FromRgb(148, 163, 184), HasDuration = true, Hint = "在区间内冻结卷轴（转场）" },
            new EventTypeInfo { TypeName = "CRAFormatDataEndOfTrack", Display = "曲终标记", FormatType = 502, Color = Color.FromRgb(244, 63, 94), Hint = "标记曲目结束位置" },
        };

        public static EventTypeInfo FindEvent(string typeName)
        {
            foreach (var e in EventTypes)
                if (e.TypeName == typeName) return e;
            return null;
        }

        // EFFTheme 只映射了这 12 个合法值（MapLoaderHelper.FFTHEME_KEY），越界会被 Mod 回退到 ForestOz
        public static readonly (int val, string label)[] ThemeOptions =
        {
            (0, "Clocktower (钟楼)"),
            (1, "ForestOz (奥兹森林)"),
            (2, "ForestCinderella (灰姑娘森林)"),
            (5, "JapaneseDay (和风白昼)"),
            (7, "MedievalSewer (中世纪下水道)"),
            (8, "MedievalFire (中世纪火焰)"),
            (9, "MedievalIce (中世纪冰雪)"),
            (11, "CircusA (马戏团)"),
            (13, "Factory (工厂)"),
            (17, "Story_Act5St5a3 (剧场 5)"),
            (18, "Story_Act8St2 (剧场 8)"),
            (19, "Story_Act8St2a4 (剧场 8-4)")
        };

        public static readonly string[] GroundOptions = { "Default (默认)", "Thin (细轨)", "None (无地面)" };
        public static readonly string[] CameraOptions = { "Default", "Up", "Down", "ZoomInDefault", "ZoomInUp", "ZoomInDown", "ZoomOutDefault", "ZoomOutUp", "ZoomOutDown" };
        public static readonly string[] CameraEaseOptions = { "OutCubic (平滑)", "OutExpo (极速爆发)", "Linear (匀速)", "InOutQuad (缓入缓出)", "OutBack (回弹张力)" };
        public static readonly string[] AngleOptions = { "Zero (0°)", "Plus_1 (+5°)", "Plus_2 (+10°)", "Plus_3 (+20°)", "Plus_4 (+30°)", "Minus_1 (-5°)", "Minus_2 (-10°)", "Minus_3 (-20°)", "Minus_4 (-30°)" };
        public static readonly string[] MoodOptions = { "Quiet (平静)", "Regular (常规)", "Climax (高潮)" };
        public static readonly string[] SpeedPresetOptions = { "Regular (常规)", "Dash (冲刺)" };
        public static readonly string[] NPCActionOptions = { "Appear (出场)", "Action (动作)", "Disappear (退场)" };
        public static readonly string[] ObjectPresetOptions = { "None (默认)", "A", "B", "C", "D", "E" };
        public static readonly string[] InteractorOptions = { "Character (角色本体)", "Subweapon (副武器)" };

        // EEase 全表（值从 1 开始）
        public static readonly string[] EaseOptions =
        {
            "Linear", "InSine", "OutSine", "InOutSine",
            "InQuad", "OutQuad", "InOutQuad",
            "InCubic", "OutCubic", "InOutCubic",
            "InQuart", "OutQuart", "InOutQuart",
            "InQuint", "OutQuint", "InOutQuint",
            "InExpo", "OutExpo", "InOutExpo",
            "InCirc", "OutCirc", "InOutCirc",
            "InElastic", "OutElastic", "InOutElastic",
            "InBack", "OutBack", "InOutBack",
            "InBounce", "OutBounce", "InOutBounce"
        };

        // 判定窗口（游戏 Constants.JudgeOffset，秒）
        public const double JudgePerfectPlus = 0.09;
        public const double JudgePerfect = 0.13;
        public const double JudgeGreat = 0.20;
    }

    /// <summary>编辑器图层：相互独立的 BPM / 流速 / 来袭方向 / 可见性；仅用于编辑与预览，不改变游戏内全局速度。</summary>
    public sealed class ChartLayer
    {
        public string Name { get; set; } = "主谱";
        public string Color { get; set; } = "#2CC6C0";
        public double Speed { get; set; } = 1.0;
        public double Bpm { get; set; } = 0;   // 0 = 跟随谱面事件 BPM
        public double Angle { get; set; } = 0; // 实验版：右边来袭斜角（-75°~+75°，0=水平直轨，正=右上俯冲，负=右下攀升）
        public bool Visible { get; set; } = true;
        public double Opacity { get; set; } = 1.0;
        public bool Locked { get; set; }
    }

    public sealed class LayerDocument
    {
        public List<ChartLayer> Layers { get; set; } = new List<ChartLayer>();
    }

    public enum EditorTool
    {
        Select,
        Crystal,
        Dodge,
        Orb,
        MultiHit,
        Coin,
        SuperJump,
        Event
    }

    public static class ChartItemExt
    {
        public static string GetTypeName(this JObject o) => o.Value<string>("$type") ?? "";
        public static double GetTime(this JObject o) => o.Value<double?>("fTimePlay") ?? 0.0;
        public static void SetTime(this JObject o, double t) { o["fTimePlay"] = Math.Round(t, 4); o["fTimeTrack"] = Math.Round(t, 4); }
        public static double GetEndTime(this JObject o) => Math.Max(o.GetTime(), o.Value<double?>("fLongNotePairTimePlay") ?? o.GetTime());
        public static void SetEndTime(this JObject o, double t) { o["fLongNotePairTimePlay"] = Math.Round(t, 4); o["fLongNotePairTimeTrack"] = Math.Round(t, 4); }
        public static int GetLane(this JObject o) => (o.Value<int?>("iTrackIndexStart") ?? 8) % 2;
        public static void SetLane(this JObject o, int lane) { int v = lane == 1 ? 7 : 8; o["iTrackIndexStart"] = v; o["iTrackIndexEnd"] = v; }
        public static bool IsNote(this JObject o) => ChartConstants.NoteTypes.Contains(o.GetTypeName());
        public static bool IsLong(this JObject o) => ChartConstants.LongTypes.Contains(o.GetTypeName());
        public static bool IsAttach(this JObject o) => ChartConstants.AttachTypes.Contains(o.GetTypeName());
        public static bool IsEvent(this JObject o) => !o.IsNote() && !o.IsAttach();
        public static int GetEditorLayer(this JObject o) => o.Value<int?>("editorLayer") ?? 0;
        public static bool IsDodge(this JObject o) => o.Value<bool?>("bIsDodge") ?? false;
        public static int GetId(this JObject o) => o.Value<int?>("hFormatDataID") ?? 0;
        public static bool IsHard(this JObject o) => o.Value<bool?>("bIsHard") ?? false;
        public static bool IsSimultaneous(this JObject o) => o.Value<bool?>("bIsSimultaneous") ?? false;
    }
}