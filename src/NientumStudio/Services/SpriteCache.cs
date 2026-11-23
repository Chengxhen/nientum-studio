using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NientumStudio.Services
{
    /// <summary>游戏解包贴图缓存（延迟加载 + 冻结避免跨线程问题）。</summary>
    public static class SpriteCache
    {
        private static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>();

        public static ImageSource Get(string fileName, int decodeWidth = 0)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            string key = decodeWidth > 0 ? $"{fileName}@{decodeWidth}" : fileName;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            string path = Path.Combine(ChartStorage.GameSpritesDir, fileName);
            if (!File.Exists(path))
            {
                Cache[key] = null;
                return null;
            }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                Cache[key] = bmp;
                return bmp;
            }
            catch
            {
                Cache[key] = null;
                return null;
            }
        }

        public static ImageSource NoteBottom => Get("PZImgPlayBasicNoteBottom.png", 96);
        public static ImageSource NoteTop => Get("PZImgPlayBasicNoteTop.png", 96);
        public static ImageSource LongBottomHead => Get("PZImgPlayLongNoteBottomHead.png", 128);
        public static ImageSource LongBottomTail => Get("PZImgPlayLongNoteBottomTail.png", 96);
        public static ImageSource LongTopHead => Get("PZImgPlayLongNoteTopHead.png", 128);
        public static ImageSource LongTopTail => Get("PZImgPlayLongNoteTopTail.png", 96);
        public static ImageSource Multihit => Get("PZImgPlayMultihitNote.png", 128);
        public static ImageSource Coin => Get("PZImgPlayCoinMiddle.png", 96);
        public static ImageSource JudgeCircle => Get("PZImgPlayJudgementLineCircle.png", 96);
        public static ImageSource JudgeCenterBottom => Get("PZImgPlayJudgementLineCenterBottom.png", 48);
        public static ImageSource JudgeCenterTop => Get("PZImgPlayJudgementLineCenterTop.png", 48);
        public static ImageSource JudgePerfect => Get("PZImgPlayJudgmentLumina.png", 220);
        public static ImageSource JudgePerfectPlus => Get("PZImgPlayJudgmentLuminaPlus.png", 220);
        public static ImageSource JudgeGreat => Get("PZImgPlayJudgmentClaris.png", 220);
        public static ImageSource JudgeMiss => Get("PZImgPlayJudgmentMiss.png", 200);
        public static ImageSource JudgeFast => Get("PZImgPlayJudgmentFast.png", 140);
        public static ImageSource JudgeSlow => Get("PZImgPlayJudgmentSlow.png", 140);
        public static ImageSource LioraIcon => Get("Icon_LioraActive.png", 160);
        public static ImageSource DefaultBg => Get("PZImgFcgAlephbornBackground.png", 960);

        // 游戏 UI 主题素材
        public static ImageSource PlaylistBg => Get("BG_Playlist.png", 1280);
        public static ImageSource ModeSelectFrame => Get("PZImgPlayModeSelectFrame.png", 256);
        public static ImageSource CoinBarGold => Get("PZImgNoteCoinBarGold.png", 256);
        public static ImageSource ButterflyGold => Get("PZImgLioraButterflyAwakenGold.png", 128);
        public static ImageSource ButterflyBlue => Get("PZImgLioraButterflyAwakenBlue.png", 128);
        public static ImageSource ButterflyPink => Get("PZImgLioraButterflyAwakenPink.png", 128);
        public static ImageSource JudgeRhombus => Get("PZImgPlayJudgementLineRhombus.png", 48);
        public static ImageSource LioraPortrait => Get("Liora_Portrait_1.png", 240);
    }
}