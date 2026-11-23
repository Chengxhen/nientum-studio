using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace NientumStudio.Services
{
    public enum ThemeMode { Dark, Light, Game }

    /// <summary>
    /// 深色 / 浅色 / 游戏剧场 (Op.ZERO) 三套主题。
    /// 切换时替换 Application.Resources 中的颜色 Brush，
    /// XAML 内部全部使用 DynamicResource，自绘控件读取这里的语义色。
    /// </summary>
    public static class ThemeManager
    {
        public static ThemeMode Current { get; private set; } = ThemeMode.Dark;
        public static bool IsLight => Current == ThemeMode.Light;
        public static bool IsGame => Current == ThemeMode.Game;
        public static event Action ThemeChanged;

        // ---- 自绘控件：时间轴 ----
        public static Color TimelineRulerBg { get; private set; }
        public static Color TimelineWaveBg { get; private set; }
        public static Color TimelineLaneBg { get; private set; }
        public static Color TimelineEventBg { get; private set; }
        public static Color TimelineAir { get; private set; }
        public static Color TimelineGround { get; private set; }
        public static Color TimelinePlayhead { get; private set; }
        public static Color TimelineSelection { get; private set; }
        public static Color TimelineTextDim { get; private set; }
        public static Color TimelineGridBar { get; private set; }
        public static Color TimelineGridBeat { get; private set; }
        public static Color TimelineGridSub { get; private set; }
        public static Color TimelineDivider { get; private set; }
        public static Color TimelineOutline { get; private set; }
        public static Color TimelineWaveBar { get; private set; }
        public static Color TimelineWaveTop { get; private set; }

        // ---- 自绘控件：画面预览 ----
        public static Color PreviewBase { get; private set; }
        public static Color PreviewVignette { get; private set; }
        public static Color PreviewGridBar { get; private set; }
        public static Color PreviewGridBeat { get; private set; }
        public static Color PreviewGridSub { get; private set; }
        public static Color PreviewInfoText { get; private set; }
        public static Color PreviewJudgeLine { get; private set; }
        public static Color PreviewRingPlus { get; private set; }
        public static Color PreviewRingPerfect { get; private set; }
        public static Color PreviewRingGreat { get; private set; }
        public static Color PreviewLegendText { get; private set; }

        // ---- NPS 曲线 ----
        public static Color GraphBg { get; private set; }
        public static Color GraphGrid { get; private set; }
        public static Color GraphLine { get; private set; }

        // ---- 体检 ----
        public static Color IssueError { get; private set; }
        public static Color IssueWarning { get; private set; }
        public static Color IssueInfo { get; private set; }

        // ---- 时间轴/预览对象色 ----
        public static Color ItemCrystalGround { get; private set; }
        public static Color ItemCrystalAir { get; private set; }
        public static Color ItemDodge { get; private set; }
        public static Color ItemOrbGround { get; private set; }
        public static Color ItemOrbAir { get; private set; }
        public static Color ItemMultiHit { get; private set; }
        public static Color ItemCoin { get; private set; }
        public static Color ItemSuperJump { get; private set; }
        public static Color ItemLongMiddle { get; private set; }

        // 调色板：(深色, 浅色, 游戏UI剧场风 — 取自 BG_Playlist / Liora_Portrait / CapsuleCoin / ModeSelectFrame)
        private static readonly Dictionary<string, (Color dark, Color light, Color game)> Palette = new()
        {
            ["BgDeep"] = (Hex("080B13"), Hex("EDF1F7"), Hex("120D0B")),
            ["BgPanel"] = (Hex("0E131F"), Hex("F8FAFD"), Hex("1A1310")),
            ["BgCard"] = (Hex("141B2B"), Hex("FFFFFF"), Hex("241B16")),
            ["BgInput"] = (Hex("0A0F1A"), Hex("F5F8FC"), Hex("110C0A")),
            ["HeaderBg"] = (Hex("0F1524"), Hex("FFFFFF"), Hex("1D1512")),
            ["CardHeaderBg"] = (Hex("151D2F"), Hex("F1F5FB"), Hex("2A201A")),
            ["StatusBg"] = (Hex("0B0F1B"), Hex("E7ECF4"), Hex("110C0A")),
            ["BorderBrush"] = (Hex("222C42"), Hex("D7E0EE"), Hex("4E3B2D")),
            ["BorderLight"] = (Hex("2F3D5A"), Hex("C2CEE0"), Hex("7A5D44")),
            ["TextPrimary"] = (Hex("EDF2FA"), Hex("1B2332"), Hex("F7EDE0")),
            ["TextSecondary"] = (Hex("94A1B8"), Hex("515E73"), Hex("CCB79D")),
            ["TextDim"] = (Hex("64728C"), Hex("838FA4"), Hex("96806B")),
            ["AccentBlue"] = (Hex("6FA8FF"), Hex("2B6AE0"), Hex("7AB8FF")),
            ["AccentGround"] = (Hex("2EE0D8"), Hex("0D948E"), Hex("3DE0D8")),
            ["AccentJump"] = (Hex("EC74C8"), Hex("C03B9D"), Hex("F27AD0")),
            ["AccentGold"] = (Hex("ECC064"), Hex("B07519"), Hex("ECC674")),
            ["AccentRed"] = (Hex("E85A5A"), Hex("C0392B"), Hex("EA6262")),
            ["AccentGreen"] = (Hex("4CE09D"), Hex("1A8A5D"), Hex("5CE0A2")),
            ["ItemHoverBg"] = (Hex("1A2338"), Hex("EEF4FC"), Hex("2E231D")),
            ["ItemSelectedBg"] = (Hex("1E2F4F"), Hex("DCEAFE"), Hex("35271C")),
            ["PopupBg"] = (Hex("151D2E"), Hex("FFFFFF"), Hex("231B16")),
            ["PopupBorder"] = (Hex("313F5C"), Hex("CBD5E4"), Hex("806247")),
            ["PopupHover"] = (Hex("222E47"), Hex("EEF4FC"), Hex("342820")),
            ["PopupSelected"] = (Hex("2A3E66"), Hex("DCEAFE"), Hex("3D2D20")),
            ["ButtonBg"] = (Hex("182032"), Hex("FFFFFF"), Hex("2A201B")),
            ["ButtonBorder"] = (Hex("2B3752"), Hex("CED8E8"), Hex("6D533E")),
            ["ButtonHover"] = (Hex("222D45"), Hex("F0F5FC"), Hex("3A2C24")),
            ["ButtonPressed"] = (Hex("2B3856"), Hex("E2EAF6"), Hex("1F1713")),
            ["ButtonText"] = (Hex("EAF0F9"), Hex("253042"), Hex("F7EDE0")),
            ["PrimaryBg"] = (Hex("3A70DB"), Hex("346AD6"), Hex("334D7B")),
            ["PrimaryBorder"] = (Hex("5B8EF0"), Hex("4E82E6"), Hex("7DA0DB")),
            ["PrimaryHover"] = (Hex("4A80EB"), Hex("4278E3"), Hex("405F96")),
            ["PrimaryPressed"] = (Hex("2D5BBD"), Hex("2958B8"), Hex("26395E")),
            ["PrimaryText"] = (Hex("F6F9FF"), Hex("FFFFFF"), Hex("FFF9EF")),
            ["GoldBg"] = (Hex("AD7F34"), Hex("BE8B37"), Hex("A67830")),
            ["GoldBorder"] = (Hex("DFB256"), Hex("D4A144"), Hex("ECC674")),
            ["GoldHover"] = (Hex("C6943E"), Hex("D09A3F"), Hex("BF8D3C")),
            ["GoldPressed"] = (Hex("8D6627"), Hex("9E7229"), Hex("805B21")),
            ["GoldText"] = (Hex("FFF7E6"), Hex("FFFFFF"), Hex("FFF8EA")),
            ["SyncBg"] = (Hex("257D5D"), Hex("237859"), Hex("246E55")),
            ["SyncBorder"] = (Hex("3DAD84"), Hex("38A27B"), Hex("46BA8F")),
            ["SyncHover"] = (Hex("2E946F"), Hex("2B8D69"), Hex("2E8668")),
            ["SyncPressed"] = (Hex("1B5E46"), Hex("1A5C44"), Hex("1A513F")),
            ["SyncText"] = (Hex("EAFFF7"), Hex("FFFFFF"), Hex("EEFFF8")),
            ["DangerBg"] = (Hex("A33038"), Hex("B83B44"), Hex("982F36")),
            ["DangerBorder"] = (Hex("D65560"), Hex("D4535E"), Hex("DA646D")),
            ["DangerText"] = (Hex("FFF2F3"), Hex("FFFFFF"), Hex("FFF2F2")),
            ["ToolBg"] = (Hex("131A29"), Hex("F4F7FB"), Hex("241B17")),
            ["ToolBorder"] = (Hex("253049"), Hex("D0DAE8"), Hex("5D4635")),
            ["ToolForeground"] = (Hex("BCC7DA"), Hex("3B475C"), Hex("D8C4AC")),
            ["ToolHoverBorder"] = (Hex("42547A"), Hex("A7B8D4"), Hex("BA9254")),
            ["ToolCheckedBg"] = (Hex("1D3657"), Hex("E3EEFF"), Hex("3D2D1C")),
            ["ToolCheckedForeground"] = (Hex("F0F6FF"), Hex("143970"), Hex("FFEBBF")),
            ["TooltipBg"] = (Hex("192235"), Hex("FFFFFF"), Hex("241B16")),
            ["TooltipBorder"] = (Hex("354463"), Hex("CBD5E4"), Hex("8E6F4B")),
            ["ScrollThumbColor"] = (Hex("2D3954"), Hex("BEC8DA"), Hex("654D3B")),
            ["CheckBoxBg"] = (Hex("0B101C"), Hex("FFFFFF"), Hex("130E0B")),
            ["CheckBoxBorder"] = (Hex("334161"), Hex("B0C0D8"), Hex("705640")),
            ["CheckBoxCheck"] = (Hex("ECC064"), Hex("B07519"), Hex("ECC674")),
            ["SliderTrackBg"] = (Hex("202A40"), Hex("D6E0EE"), Hex("3B2C23")),
            ["SliderThumbColor"] = (Hex("ECC064"), Hex("B07519"), Hex("ECC674")),
            ["SectionFg"] = (Hex("D2DCF0"), Hex("1E2738"), Hex("F3DDB8")),
            ["TextBoxBorder"] = (Hex("26324B"), Hex("CED8E8"), Hex("5C4635")),
            ["PillBg"] = (Hex("111827"), Hex("EDF2F9"), Hex("1E1612")),
        };

        private static Color Hex(string s) => (Color)ColorConverter.ConvertFromString("#" + s);

        private static readonly string PrefPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui_theme.txt");

        public static void Load()
        {
            ThemeMode mode = ThemeMode.Dark;
            try
            {
                if (File.Exists(PrefPath))
                {
                    string s = File.ReadAllText(PrefPath).Trim().ToLowerInvariant();
                    if (s == "light") mode = ThemeMode.Light;
                    else if (s == "game") mode = ThemeMode.Game;
                }
            }
            catch { }
            Apply(mode, persist: false);
        }

        public static void Toggle() => Cycle();

        public static void Cycle()
        {
            var next = Current switch
            {
                ThemeMode.Dark => ThemeMode.Light,
                ThemeMode.Light => ThemeMode.Game,
                _ => ThemeMode.Dark
            };
            Apply(next);
        }

        public static string DisplayName(ThemeMode mode) => mode switch
        {
            ThemeMode.Light => "浅色",
            ThemeMode.Game => "游戏UI",
            _ => "深色"
        };

        public static void Apply(ThemeMode mode, bool persist = true)
        {
            Current = mode;
            var res = Application.Current?.Resources;
            if (res != null)
            {
                foreach (var kv in Palette)
                {
                    var c = mode switch
                    {
                        ThemeMode.Light => kv.Value.light,
                        ThemeMode.Game => kv.Value.game,
                        _ => kv.Value.dark
                    };
                    var brush = new SolidColorBrush(c);
                    brush.Freeze();
                    res[kv.Key] = brush;
                }

                // 工具选中边框在 Game 主题下点亮为鎏金色，Dark/Light 为 AccentBlue
                var checkedBorder = new SolidColorBrush(mode == ThemeMode.Game ? Hex("ECC674") : (mode == ThemeMode.Light ? Hex("2F6FD6") : Hex("6FA8FF")));
                checkedBorder.Freeze();
                res["ToolCheckedBorder"] = checkedBorder;

                // 歌剧舞台透明面板底（用于主窗口 Game 主题下微透出 BG_Playlist 幕布纹理，同时保持文字清晰）
                var panelBg = mode == ThemeMode.Game ? Color.FromArgb(240, 26, 19, 16) : (mode == ThemeMode.Light ? Hex("F8FAFD") : Hex("0E131F"));
                var panelBrush = new SolidColorBrush(panelBg);
                panelBrush.Freeze();
                res["BgPanelStage"] = panelBrush;

                res["GameThemeVisibility"] = mode == ThemeMode.Game ? Visibility.Visible : Visibility.Collapsed;
                res["StandardBrandVisibility"] = mode == ThemeMode.Game ? Visibility.Collapsed : Visibility.Visible;
            }
            UpdateSemanticColors();
            ThemeChanged?.Invoke();
            if (persist)
            {
                string tag = mode switch
                {
                    ThemeMode.Light => "light",
                    ThemeMode.Game => "game",
                    _ => "dark"
                };
                try { File.WriteAllText(PrefPath, tag); } catch { }
            }
        }

        private static void UpdateSemanticColors()
        {
            if (Current == ThemeMode.Dark)
            {
                TimelineRulerBg = Hex("10141F");
                TimelineWaveBg = Hex("0B0F18");
                TimelineLaneBg = Hex("0B0E16");
                TimelineEventBg = Hex("0E121C");
                TimelineAir = Hex("E070C0");
                TimelineGround = Hex("2CC6C0");
                TimelinePlayhead = Hex("F47298");
                TimelineSelection = Hex("6FA8FF");
                TimelineTextDim = Hex("5F6B82");
                TimelineGridBar = Color.FromArgb(80, 255, 255, 255);
                TimelineGridBeat = Color.FromArgb(34, 255, 255, 255);
                TimelineGridSub = Color.FromArgb(13, 255, 255, 255);
                TimelineDivider = Hex("242C3E");
                TimelineOutline = Hex("090C12");
                TimelineWaveBar = Color.FromArgb(200, 58, 110, 150);
                TimelineWaveTop = Color.FromArgb(120, 108, 178, 220);

                PreviewBase = Hex("080B13");
                PreviewVignette = Hex("04060C");
                PreviewGridBar = Color.FromArgb(60, 227, 184, 92);
                PreviewGridBeat = Color.FromArgb(26, 210, 220, 235);
                PreviewGridSub = Color.FromArgb(12, 210, 220, 235);
                PreviewInfoText = Color.FromArgb(150, 190, 200, 220);
                PreviewJudgeLine = Color.FromArgb(190, 236, 110, 150);
                PreviewRingPlus = Color.FromArgb(210, 227, 184, 92);
                PreviewRingPerfect = Color.FromArgb(140, 255, 255, 255);
                PreviewRingGreat = Color.FromArgb(70, 44, 198, 192);
                PreviewLegendText = Color.FromArgb(130, 190, 200, 220);

                GraphBg = Hex("0B0F18");
                GraphGrid = Color.FromArgb(18, 255, 255, 255);
                GraphLine = Hex("E3B85C");

                IssueError = Hex("E45858");
                IssueWarning = Hex("E3B85C");
                IssueInfo = Hex("6FA8FF");

                ItemCrystalGround = Hex("2CC6C0");
                ItemCrystalAir = Hex("E070C0");
                ItemDodge = Hex("E45858");
                ItemOrbGround = Hex("48C4BE");
                ItemOrbAir = Hex("D878BE");
                ItemMultiHit = Hex("EC6EB4");
                ItemCoin = Hex("E3B85C");
                ItemSuperJump = Hex("78D2DC");
                ItemLongMiddle = Hex("A0E6E4");
            }
            else if (Current == ThemeMode.Light)
            {
                TimelineRulerBg = Hex("F4F6FB");
                TimelineWaveBg = Hex("F7F9FD");
                TimelineLaneBg = Hex("F7F9FD");
                TimelineEventBg = Hex("F2F5FA");
                TimelineAir = Hex("B23E93");
                TimelineGround = Hex("0F8A85");
                TimelinePlayhead = Hex("D6497A");
                TimelineSelection = Hex("2F6FD6");
                TimelineTextDim = Hex("6C7688");
                TimelineGridBar = Color.FromArgb(90, 95, 107, 130);
                TimelineGridBeat = Color.FromArgb(40, 95, 107, 130);
                TimelineGridSub = Color.FromArgb(18, 95, 107, 130);
                TimelineDivider = Hex("DCE2EC");
                TimelineOutline = Hex("3A4766");
                TimelineWaveBar = Color.FromArgb(190, 110, 150, 190);
                TimelineWaveTop = Color.FromArgb(110, 70, 120, 165);

                PreviewBase = Hex("E9EEF7");
                PreviewVignette = Hex("FFFFFF");
                PreviewGridBar = Color.FromArgb(110, 160, 116, 46);
                PreviewGridBeat = Color.FromArgb(45, 95, 107, 130);
                PreviewGridSub = Color.FromArgb(18, 95, 107, 130);
                PreviewInfoText = Color.FromArgb(220, 55, 66, 84);
                PreviewJudgeLine = Color.FromArgb(210, 214, 73, 122);
                PreviewRingPlus = Color.FromArgb(220, 168, 114, 30);
                PreviewRingPerfect = Color.FromArgb(200, 58, 71, 102);
                PreviewRingGreat = Color.FromArgb(140, 18, 154, 148);
                PreviewLegendText = Color.FromArgb(200, 70, 82, 104);

                GraphBg = Hex("F7F9FD");
                GraphGrid = Color.FromArgb(28, 95, 107, 130);
                GraphLine = Hex("B8862E");

                IssueError = Hex("C0392B");
                IssueWarning = Hex("8A6D1F");
                IssueInfo = Hex("2F6FD6");

                ItemCrystalGround = Hex("0E8E88");
                ItemCrystalAir = Hex("B23E93");
                ItemDodge = Hex("C0392B");
                ItemOrbGround = Hex("109A93");
                ItemOrbAir = Hex("C2479F");
                ItemMultiHit = Hex("C44A8E");
                ItemCoin = Hex("A8721E");
                ItemSuperJump = Hex("2C8E99");
                ItemLongMiddle = Hex("4FAFA8");
            }
            else // ThemeMode.Game (Op.ZERO 歌剧剧场主题)
            {
                TimelineRulerBg = Hex("1C1613");
                TimelineWaveBg = Hex("140F0D");
                TimelineLaneBg = Hex("151112");
                TimelineEventBg = Hex("191310");
                TimelineAir = Hex("EC79CA");
                TimelineGround = Hex("3AD6CF");
                TimelinePlayhead = Hex("ECC674");
                TimelineSelection = Hex("7AB8FF");
                TimelineTextDim = Hex("9D876E");
                TimelineGridBar = Color.FromArgb(105, 236, 198, 116);
                TimelineGridBeat = Color.FromArgb(42, 236, 198, 116);
                TimelineGridSub = Color.FromArgb(16, 236, 198, 116);
                TimelineDivider = Hex("4D3B2E");
                TimelineOutline = Hex("1A120E");
                TimelineWaveBar = Color.FromArgb(195, 120, 145, 180);
                TimelineWaveTop = Color.FromArgb(150, 224, 194, 138);

                PreviewBase = Hex("15100E");
                PreviewVignette = Hex("0B0807");
                PreviewGridBar = Color.FromArgb(95, 236, 198, 116);
                PreviewGridBeat = Color.FromArgb(34, 240, 220, 185);
                PreviewGridSub = Color.FromArgb(14, 240, 220, 185);
                PreviewInfoText = Color.FromArgb(225, 246, 236, 224);
                PreviewJudgeLine = Color.FromArgb(220, 236, 198, 116);
                PreviewRingPlus = Color.FromArgb(230, 255, 220, 130);
                PreviewRingPerfect = Color.FromArgb(170, 255, 250, 240);
                PreviewRingGreat = Color.FromArgb(110, 58, 214, 207);
                PreviewLegendText = Color.FromArgb(195, 230, 212, 185);

                GraphBg = Hex("130E0C");
                GraphGrid = Color.FromArgb(26, 236, 198, 116);
                GraphLine = Hex("ECC674");

                IssueError = Hex("EA6262");
                IssueWarning = Hex("ECC674");
                IssueInfo = Hex("7AB8FF");

                ItemCrystalGround = Hex("3AD6CF");
                ItemCrystalAir = Hex("EC79CA");
                ItemDodge = Hex("EA6262");
                ItemOrbGround = Hex("4EDAD3");
                ItemOrbAir = Hex("E57AC6");
                ItemMultiHit = Hex("F076BC");
                ItemCoin = Hex("ECC674");
                ItemSuperJump = Hex("88DCE4");
                ItemLongMiddle = Hex("B8F0EE");
            }
        }

        public static Brush Brush(string key)
        {
            return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
        }
    }
}
