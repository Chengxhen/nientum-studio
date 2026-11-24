using System.Windows.Media;

namespace NientumStudio.Services
{
    public enum BeatLineKind
    {
        Bar,
        SubAccent,
        Beat,
        Half,
        Triplet,
        Sub
    }

    /// <summary>
    /// 统一的分格线拍子重音分类器与配色/线宽规范（供时间轴、预览网格与设置图例共用）。
    /// </summary>
    public static class BeatGridStyle
    {
        public static BeatLineKind ClassifyGridLine(int subIndex, int div, int beatsPerBar)
        {
            if (div <= 0) div = 1;
            if (beatsPerBar <= 0) beatsPerBar = 4;

            int mod = ((subIndex % div) + div) % div;
            bool isBeat = mod == 0;
            if (isBeat)
            {
                int beatIdx = subIndex / div;
                int beatInBar = ((beatIdx % beatsPerBar) + beatsPerBar) % beatsPerBar;
                if (beatInBar == 0) return BeatLineKind.Bar;

                bool isSubAccent = beatsPerBar switch
                {
                    4 => beatInBar == 2,
                    6 => beatInBar == 3,
                    8 => beatInBar == 4,
                    9 => beatInBar == 3 || beatInBar == 6,
                    12 => beatInBar == 3 || beatInBar == 6 || beatInBar == 9,
                    _ => false
                };
                return isSubAccent ? BeatLineKind.SubAccent : BeatLineKind.Beat;
            }

            if (div % 2 == 0 && mod == div / 2)
                return BeatLineKind.Half;
            if (div % 3 == 0 && (mod * 3) % div == 0)
                return BeatLineKind.Triplet;
            return BeatLineKind.Sub;
        }

        public static (Color color, double thickness) GetAccentPenSpec(BeatLineKind kind, bool isLight, bool isPreview)
        {
            return kind switch
            {
                BeatLineKind.Bar => (
                    isLight ? Color.FromRgb(184, 122, 20) : Color.FromRgb(245, 196, 81),
                    isPreview ? 2.4 : 2.6),
                BeatLineKind.SubAccent => (
                    isLight ? Color.FromRgb(70, 86, 114) : Color.FromRgb(220, 230, 245),
                    isPreview ? 1.7 : 1.9),
                BeatLineKind.Beat => (
                    isLight ? Color.FromRgb(108, 126, 153) : Color.FromRgb(143, 164, 196),
                    isPreview ? 1.2 : 1.3),
                BeatLineKind.Half => (
                    isLight ? Color.FromRgb(29, 120, 189) : Color.FromRgb(82, 178, 245),
                    1.0),
                BeatLineKind.Triplet => (
                    isLight ? Color.FromRgb(123, 76, 224) : Color.FromRgb(179, 136, 255),
                    0.9),
                _ => (
                    isLight ? Color.FromArgb(110, 110, 125, 150) : Color.FromArgb(110, 145, 160, 185),
                    0.8)
            };
        }

        public static readonly (BeatLineKind kind, string name, string desc)[] LegendEntries =
        {
            (BeatLineKind.Bar, "小节强拍（第 1 拍）", "所有拍号 1–16 通用，一眼定位小节起始"),
            (BeatLineKind.SubAccent, "次强拍（半小节/分组重音）", "自动匹配 4/4(第3拍)、6/8(第4拍)、8/8(第5拍)、9/8(第4,7拍)、12/8(第4,7,10拍)"),
            (BeatLineKind.Beat, "普通整拍", "其余常规整拍定位线"),
            (BeatLineKind.Half, "1/2 细分拍（半拍）", "八分音符等半拍位置"),
            (BeatLineKind.Triplet, "1/3 / 1/6 细分拍（三连音）", "三连音与六连音位置"),
            (BeatLineKind.Sub, "1/4 及更细细分格", "十六分及更密细分辅助线")
        };
    }
}
