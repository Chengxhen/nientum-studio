using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NientumStudio.Models;

namespace NientumStudio.Services
{
    public static class ChartStorage
    {
        // 便携包：exe 同目录带 unpacked 素材；否则回退到程序目录
        public static readonly string WorkspaceRoot = ResolveWorkspaceRoot();
        public static readonly string CustomChartsDir = Path.Combine(WorkspaceRoot, "CustomCharts");
        public static readonly string OfficialJsonDir = Path.Combine(WorkspaceRoot, "unpacked", "charts_json");
        public static readonly string GameSpritesDir = Path.Combine(WorkspaceRoot, "unpacked", "assets", "Sprites");
        public static string GameCustomChartsDir => GameLocator.CustomChartsDir;

        private static string ResolveWorkspaceRoot()
        {
            // 便携包与开发环境一致：素材与 exe 同目录（unpacked\...）
            return AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        }

        static ChartStorage()
        {
            Directory.CreateDirectory(CustomChartsDir);
        }

        public static string SafeId(string raw)
        {
            string s = Regex.Replace((raw ?? "").Trim(), @"[^a-zA-Z0-9._-]", "_").Trim('.', '_');
            return string.IsNullOrEmpty(s) ? "custom_song" : s;
        }

        public static List<SongEntry> ListSongs()
        {
            var list = new List<SongEntry>();
            if (Directory.Exists(CustomChartsDir))
            {
                foreach (string dir in Directory.GetDirectories(CustomChartsDir).OrderBy(d => d))
                {
                    var entry = LoadSongEntry(dir, isOfficial: false);
                    if (entry != null) list.Add(entry);
                }
            }
            // 官方曲目副本（本地工作区，永不同步进游戏）
            list.AddRange(OfficialLibrary.ListWorkspaceSongs());
            return list;
        }

        public static SongEntry LoadSongEntry(string dir, bool isOfficial)
        {
            string metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath)) return null;
            try
            {
                var meta = JsonConvert.DeserializeObject<CustomTrackMeta>(File.ReadAllText(metaPath));
                if (meta == null) return null;
                string folder = Path.GetFileName(dir);
                bool hasAudio = !string.IsNullOrEmpty(meta.audioFile) && File.Exists(Path.Combine(dir, meta.audioFile));
                bool synced = IsSyncedToGame(folder);
                return new SongEntry
                {
                    FolderName = folder,
                    FolderPath = dir,
                    Meta = meta,
                    HasAudio = hasAudio,
                    SyncedToGame = synced,
                    IsOfficial = isOfficial || meta.isOfficialCopy,
                    CoverImage = LoadCoverThumb(dir, meta.coverFile)
                };
            }
            catch
            {
                return null;
            }
        }

        private static readonly Dictionary<string, BitmapImage> CoverCache = new Dictionary<string, BitmapImage>();

        private static BitmapImage LoadCoverThumb(string dir, string coverFile)
        {
            if (string.IsNullOrEmpty(coverFile)) return null;
            string p = Path.Combine(dir, coverFile);
            if (!File.Exists(p)) return null;
            if (CoverCache.TryGetValue(p, out var cached)) return cached;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(p, UriKind.Absolute);
                bmp.DecodePixelWidth = 96;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                CoverCache[p] = bmp;
                return bmp;
            }
            catch { return null; }
        }

        private static void InvalidateCoverCache(string dir)
        {
            foreach (string key in CoverCache.Keys.Where(k =>
                         k.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                CoverCache.Remove(key);
            }
        }

        public static SongEntry CreateNewSong(string rawId, string title, string artist, float bpm)
        {
            string folder = SafeId(string.IsNullOrWhiteSpace(rawId) ? title.ToLowerInvariant() : rawId);
            string dir = Path.Combine(CustomChartsDir, folder);
            int suffix = 2;
            while (Directory.Exists(dir))
            {
                folder = SafeId(rawId) + "_" + suffix++;
                dir = Path.Combine(CustomChartsDir, folder);
            }
            Directory.CreateDirectory(dir);

            var meta = new CustomTrackMeta
            {
                id = "custom." + folder,
                title = string.IsNullOrWhiteSpace(title) ? "New Song" : title.Trim(),
                artist = string.IsNullOrWhiteSpace(artist) ? "Unknown" : artist.Trim(),
                charter = "User",
                bpm = bpm > 10 ? bpm : 120f
            };
            SaveMeta(dir, meta);
            SaveChart(dir, "normal.json", CreateDefaultChart(meta.bpm));

            return new SongEntry
            {
                FolderName = folder,
                FolderPath = dir,
                Meta = meta,
                HasAudio = false,
                SyncedToGame = false
            };
        }

        public static void SaveMeta(string songDir, CustomTrackMeta meta)
        {
            File.WriteAllText(Path.Combine(songDir, "meta.json"), JsonConvert.SerializeObject(meta, Formatting.Indented));
            InvalidateCoverCache(songDir);
        }

        public static JObject LoadChart(string songDir, string chartFile, float defaultBpm)
        {
            string path = Path.Combine(songDir, chartFile);
            if (!File.Exists(path)) return CreateDefaultChart(defaultBpm);
            try
            {
                return JObject.Parse(File.ReadAllText(path));
            }
            catch
            {
                // 损坏的谱面文件回退为默认谱面，避免编辑器启动崩溃
                return CreateDefaultChart(defaultBpm);
            }
        }

        public static void SaveChart(string songDir, string chartFile, JObject chart)
        {
            File.WriteAllText(Path.Combine(songDir, chartFile), chart.ToString(Formatting.Indented));
        }

        public static bool IsSyncedToGame(string folder)
        {
            string dir = GameCustomChartsDir;
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, folder, "meta.json"));
        }

        public static void SyncToGame(string songDir, string folderName)
        {
            string target = GameCustomChartsDir;
            if (string.IsNullOrEmpty(target)) return;
            Directory.CreateDirectory(target);
            string dst = Path.Combine(target, folderName);
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            CopyDirectory(songDir, dst);
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
            foreach (string sub in Directory.GetDirectories(src))
                CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
        }

        public static List<string> ListOfficialCharts()
        {
            if (!Directory.Exists(OfficialJsonDir)) return new List<string>();
            return Directory.GetFiles(OfficialJsonDir, "FreePlay_*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(x => x)
                .ToList();
        }

        public static JObject LoadOfficialChart(string name)
        {
            string path = Path.Combine(OfficialJsonDir, name + ".json");
            return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
        }

        public static List<ChartLayer> LoadLayers(string songDir, string chartFile)
        {
            try
            {
                string path = Path.Combine(songDir, Path.GetFileNameWithoutExtension(chartFile) + ".layers.json");
                if (!File.Exists(path)) return null;
                var doc = JsonConvert.DeserializeObject<LayerDocument>(File.ReadAllText(path));
                return doc?.Layers;
            }
            catch { return null; }
        }

        public static void SaveLayers(string songDir, string chartFile, List<ChartLayer> layers)
        {
            try
            {
                string path = Path.Combine(songDir, Path.GetFileNameWithoutExtension(chartFile) + ".layers.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(new LayerDocument { Layers = layers }, Formatting.Indented));
            }
            catch { }
        }

        public static JObject CreateDefaultChart(float bpm)
        {
            return new JObject
            {
                ["header"] = new JObject
                {
                    ["$type"] = "SPlayDataHeader",
                    ["hMusicID"] = 0,
                    ["hOwnerID"] = 0,
                    ["iPlayDataCategory"] = 0,
                    ["iPlayDataDifficulty"] = 0,
                    ["iPlayDataHeaderVersion"] = 1,
                    ["maker"] = "NientumStudio",
                    ["description"] = ""
                },
                ["items"] = new JArray
                {
                    new JObject { ["$type"] = "CRAFormatDataEventBPMBar", ["iBPMBarType"] = 0, ["fBPM"] = bpm, ["fBPMBase"] = 4.0, ["iBPMBeat"] = 4, ["bAutoHit"] = false, ["hFormatDataID"] = 1, ["iFormatDataType"] = 100, ["iFormatDataIndex"] = 0, ["iTrackIndexStart"] = 0, ["iTrackIndexEnd"] = 0, ["fTimePlay"] = 0.0, ["fTimeTrack"] = 0.0 },
                    new JObject { ["$type"] = "CRAFormatDataEventSpeed", ["fSpeedValue"] = 1.2, ["bAutoHit"] = false, ["hFormatDataID"] = 2, ["iFormatDataType"] = 101, ["iFormatDataIndex"] = 1, ["iTrackIndexStart"] = 1, ["iTrackIndexEnd"] = 1, ["fTimePlay"] = 0.0, ["fTimeTrack"] = 0.0 },
                    new JObject { ["$type"] = "CRAFormatDataFFChangeTheme", ["eFFTheme"] = 1, ["bAutoHit"] = false, ["hFormatDataID"] = 3, ["iFormatDataType"] = 700, ["iFormatDataIndex"] = 2, ["iTrackIndexStart"] = 2, ["iTrackIndexEnd"] = 2, ["fTimePlay"] = 0.0, ["fTimeTrack"] = 0.0 },
                    new JObject { ["$type"] = "CRAFormatDataFFChangeCameraPreset", ["eFFCameraPreset"] = 0, ["fLongNotePairTimePlay"] = 0.0, ["fLongNotePairTimeTrack"] = 0.0, ["bAutoHit"] = false, ["hFormatDataID"] = 4, ["iFormatDataType"] = 704, ["iFormatDataIndex"] = 3, ["iTrackIndexStart"] = 3, ["iTrackIndexEnd"] = 3, ["fTimePlay"] = 0.0, ["fTimeTrack"] = 0.0 },
                    new JObject { ["$type"] = "CRAFormatDataFFChangeGroundPreset", ["eFFGroundPreset"] = 0, ["bAutoHit"] = false, ["hFormatDataID"] = 5, ["iFormatDataType"] = 705, ["iFormatDataIndex"] = 4, ["iTrackIndexStart"] = 4, ["iTrackIndexEnd"] = 4, ["fTimePlay"] = 0.0, ["fTimeTrack"] = 0.0 },
                    new JObject { ["$type"] = "CRAFormatDataEndOfTrack", ["bAutoHit"] = false, ["hFormatDataID"] = 6, ["iFormatDataType"] = 502, ["iFormatDataIndex"] = 5, ["iTrackIndexStart"] = 0, ["iTrackIndexEnd"] = 0, ["fTimePlay"] = 30.0, ["fTimeTrack"] = 30.0 }
                }
            };
        }
    }
}
