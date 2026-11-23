using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NientumStudio.Models;

namespace NientumStudio.Services
{
    public sealed class OfficialTrackInfo
    {
        public string Key;
        public string Title;
        public float Bpm;
        public bool HasNormal;
        public bool HasHard;
        public string CoverPath;
        public bool Imported;
    }

    /// <summary>
    /// 官方 FreePlay 曲目库与本地工作区。
    /// 官方谱面只从 unpacked/charts_json 读取；导入后复制到 OfficialPlayground，
    /// 编辑与保存都留在工作区，绝不写入游戏 CustomCharts。
    /// </summary>
    public static class OfficialLibrary
    {
        public static readonly string ChartsDir = Path.Combine(ChartStorage.WorkspaceRoot, "unpacked", "charts_json");
        public static readonly string SpritesDir = ChartStorage.GameSpritesDir;
        public static readonly string WorkspaceDir = Path.Combine(ChartStorage.WorkspaceRoot, "OfficialPlayground");
        public static readonly string BanksDir = Path.Combine(ChartStorage.WorkspaceRoot, "unpacked", "assets", "FMODBanks");

        private static readonly Regex FileRegex = new Regex(@"^FreePlay_(.+)_(\d)$", RegexOptions.Compiled);

        public static string SafeFolder(string key) => Regex.Replace(key, @"[^a-zA-Z0-9._-]", "_");

        public static List<OfficialTrackInfo> ScanTracks()
        {
            var byKey = new Dictionary<string, OfficialTrackInfo>();
            if (Directory.Exists(ChartsDir))
            {
                foreach (string file in Directory.GetFiles(ChartsDir, "FreePlay_*.json"))
                {
                    var m = FileRegex.Match(Path.GetFileNameWithoutExtension(file));
                    if (!m.Success) continue;
                    string key = m.Groups[1].Value;
                    int diff = int.Parse(m.Groups[2].Value);
                    if (!byKey.TryGetValue(key, out var info))
                    {
                        info = new OfficialTrackInfo { Key = key };
                        byKey[key] = info;
                    }
                    if (diff == 0) info.HasNormal = true;
                    else if (diff == 1) info.HasHard = true;

                    if (string.IsNullOrEmpty(info.Title) || diff == 0)
                    {
                        try
                        {
                            var doc = JObject.Parse(File.ReadAllText(file));
                            string musicName = doc["header"]?.Value<string>("aMusicName");
                            if (!string.IsNullOrWhiteSpace(musicName)) info.Title = musicName.Trim();
                            var bpmEv = doc["items"]?.OfType<JObject>().FirstOrDefault(x => x.GetTypeName() == "CRAFormatDataEventBPMBar");
                            if (bpmEv != null) info.Bpm = bpmEv.Value<float?>("fBPM") ?? 120f;
                        }
                        catch { }
                    }
                }
            }

            foreach (var info in byKey.Values)
            {
                if (string.IsNullOrEmpty(info.Title))
                {
                    string pretty = info.Key.StartsWith("track.") ? info.Key.Substring(6) : info.Key;
                    info.Title = string.IsNullOrEmpty(pretty) ? info.Key : char.ToUpperInvariant(pretty[0]) + pretty.Substring(1);
                }
                if (info.Bpm <= 10) info.Bpm = 120f;
                string cover = Path.Combine(SpritesDir, $"trackcover_{info.Key}.png");
                info.CoverPath = File.Exists(cover) ? cover : "";
                string dir = Path.Combine(WorkspaceDir, SafeFolder(info.Key));
                info.Imported = File.Exists(Path.Combine(dir, "meta.json"));
            }

            return byKey.Values.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static SongEntry ImportTrack(OfficialTrackInfo info)
        {
            string folder = SafeFolder(info.Key);
            string dir = Path.Combine(WorkspaceDir, folder);
            Directory.CreateDirectory(dir);

            var meta = new CustomTrackMeta
            {
                id = "official." + info.Key,
                title = info.Title,
                localizedTitle = "",
                titleSuffix = "[官方]",
                artist = "Nientum · Op.ZERO",
                charter = "Official",
                bpm = info.Bpm,
                offsetMs = 0f,
                previewStartSec = 0f,
                audioFile = "",
                coverFile = "",
                isOriginal = true,
                isOfficialCopy = true,
                officialKey = info.Key,
                difficulties = new DifficultiesMap()
            };

            string normSrc = Path.Combine(ChartsDir, $"FreePlay_{info.Key}_0.json");
            if (File.Exists(normSrc))
            {
                File.Copy(normSrc, Path.Combine(dir, "normal.json"), true);
                meta.difficulties.normal = new DifficultyMeta { chartFile = "normal.json", level = EstimateLevel(normSrc, info.Bpm) };
            }
            else
            {
                meta.difficulties.normal = new DifficultyMeta { chartFile = "normal.json", level = 0 };
            }

            string hardSrc = Path.Combine(ChartsDir, $"FreePlay_{info.Key}_1.json");
            if (File.Exists(hardSrc))
            {
                File.Copy(hardSrc, Path.Combine(dir, "hard.json"), true);
                meta.difficulties.hard = new DifficultyMeta { chartFile = "hard.json", level = EstimateLevel(hardSrc, info.Bpm) };
            }
            else
            {
                meta.difficulties.hard = new DifficultyMeta { chartFile = "hard.json", level = 0 };
            }
            meta.difficulties.extreme = new DifficultyMeta { chartFile = "extreme.json", level = 0 };

            if (!string.IsNullOrEmpty(info.CoverPath) && File.Exists(info.CoverPath))
            {
                File.Copy(info.CoverPath, Path.Combine(dir, "cover.png"), true);
                meta.coverFile = "cover.png";
            }

            ChartStorage.SaveMeta(dir, meta);
            return ChartStorage.LoadSongEntry(dir, isOfficial: true);
        }

        private static int EstimateLevel(string chartPath, float bpm)
        {
            try
            {
                var doc = JObject.Parse(File.ReadAllText(chartPath));
                var report = ChartValidator.Analyze(doc["items"] as JArray, 0, 0, bpm);
                return Math.Clamp(report.SuggestedLevel, 1, 12);
            }
            catch { return 5; }
        }

        public static bool RestoreTrack(string officialKey)
        {
            if (string.IsNullOrEmpty(officialKey)) return false;
            var info = ScanTracks().FirstOrDefault(t => t.Key == officialKey);
            if (info == null) return false;
            ImportTrack(info);
            return true;
        }

        public static List<SongEntry> ListWorkspaceSongs()
        {
            var list = new List<SongEntry>();
            if (!Directory.Exists(WorkspaceDir)) return list;
            foreach (string dir in Directory.GetDirectories(WorkspaceDir).OrderBy(d => d))
            {
                var entry = ChartStorage.LoadSongEntry(dir, isOfficial: true);
                if (entry != null) list.Add(entry);
            }
            return list;
        }
    }
}