using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nientum.Models;
using UnityEngine;

namespace NientumCustomCharts
{
    public class CustomDifficultyMeta
    {
        public int level { get; set; }
        public string chartFile { get; set; }
    }

    public class CustomDifficultiesMap
    {
        public CustomDifficultyMeta normal { get; set; }
        public CustomDifficultyMeta hard { get; set; }
        public CustomDifficultyMeta extreme { get; set; }
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
        public string audioFile { get; set; } = "song.ogg";
        public string coverFile { get; set; } = "cover.png";
        public bool isOriginal { get; set; } = false;
        public CustomDifficultiesMap difficulties { get; set; } = new CustomDifficultiesMap();
    }

    public class CustomTrackEntry
    {
        public int NumericId { get; set; }
        public string TrackKey { get; set; }
        public string FolderPath { get; set; }
        public CustomTrackMeta Meta { get; set; }
        public Track GameTrack { get; set; }
        public Sprite CoverSprite { get; set; }
        public bool CoverLoadAttempted { get; set; }

        public string GetAudioPath()
        {
            if (Meta == null || string.IsNullOrEmpty(Meta.audioFile)) return null;
            string p = Path.Combine(FolderPath, Meta.audioFile);
            if (File.Exists(p)) return p;
            foreach (string ext in new[] { ".ogg", ".mp3", ".wav", ".flac" })
            {
                string alt = Path.Combine(FolderPath, "song" + ext);
                if (File.Exists(alt)) return alt;
            }
            return null;
        }

        public string GetChartPath(int difficulty)
        {
            CustomDifficultyMeta diffMeta = difficulty switch
            {
                0 => Meta?.difficulties?.normal,
                1 => Meta?.difficulties?.hard,
                2 => Meta?.difficulties?.extreme,
                _ => null
            };
            if (diffMeta == null || diffMeta.level <= 0 || string.IsNullOrEmpty(diffMeta.chartFile))
                return null;
            string p = Path.Combine(FolderPath, diffMeta.chartFile);
            return File.Exists(p) ? p : null;
        }
    }

    public static class CustomTrackManager
    {
        private static readonly Dictionary<string, CustomTrackEntry> _entriesByKey =
            new Dictionary<string, CustomTrackEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<CustomTrackEntry> _entriesList = new List<CustomTrackEntry>();
        private static int _nextId = 900001;

        public static int Count => _entriesList.Count;
        public static IReadOnlyList<CustomTrackEntry> Entries => _entriesList;

        public static void ReloadAll()
        {
            foreach (var entry in _entriesList)
            {
                if (entry.CoverSprite != null)
                {
                    if (entry.CoverSprite.texture != null)
                        UnityEngine.Object.Destroy(entry.CoverSprite.texture);
                    UnityEngine.Object.Destroy(entry.CoverSprite);
                }
            }
            _entriesByKey.Clear();
            _entriesList.Clear();
            _nextId = 900001;

            var searchDirs = new List<string>
            {
                Path.Combine(Paths.GameRootPath, "CustomCharts")
            };

            string extra = Plugin.ExtraChartsDirectory?.Value;
            if (!string.IsNullOrWhiteSpace(extra) && Directory.Exists(extra))
            {
                string fullExtra = Path.GetFullPath(extra);
                string fullMain = Path.GetFullPath(searchDirs[0]);
                if (!string.Equals(fullExtra, fullMain, StringComparison.OrdinalIgnoreCase))
                {
                    searchDirs.Add(fullExtra);
                }
            }

            foreach (string root in searchDirs)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    TryLoadFolder(dir);
                }
            }
        }

        private static void TryLoadFolder(string folderPath)
        {
            string metaPath = Path.Combine(folderPath, "meta.json");
            if (!File.Exists(metaPath)) return;

            try
            {
                var meta = JsonConvert.DeserializeObject<CustomTrackMeta>(File.ReadAllText(metaPath));
                if (meta == null) return;

                string rawId = string.IsNullOrWhiteSpace(meta.id)
                    ? Path.GetFileName(folderPath)
                    : meta.id.Trim();
                string trackKey = rawId.StartsWith("custom.", StringComparison.OrdinalIgnoreCase)
                    ? rawId
                    : "custom." + rawId;

                if (_entriesByKey.ContainsKey(trackKey))
                {
                    Plugin.Log?.LogWarning($"Duplicate custom track key '{trackKey}' at {folderPath}, skipping.");
                    return;
                }

                int normalLv = GetValidDifficultyLevel(folderPath, meta.difficulties?.normal);
                int hardLv = GetValidDifficultyLevel(folderPath, meta.difficulties?.hard);
                int extremeLv = GetValidDifficultyLevel(folderPath, meta.difficulties?.extreme);

                if (normalLv == 0 && hardLv == 0 && extremeLv == 0)
                {
                    Plugin.Log?.LogWarning($"Custom track '{trackKey}' has no valid chart files, skipping.");
                    return;
                }

                int pk = _nextId++;
                var jObj = new JObject
                {
                    ["pk"] = pk,
                    ["trackItemKey"] = trackKey,
                    ["title"] = meta.title ?? trackKey,
                    ["localizedTitle"] = meta.localizedTitle ?? "",
                    ["titleSuffix"] = meta.titleSuffix ?? "",
                    ["orderString"] = "zzz_" + (meta.title ?? trackKey),
                    ["state"] = 1, // ETrackState.Release
                    ["playVisualPartsSetType"] = 0,
                    ["new"] = true,
                    ["collaboration"] = false,
                    ["original"] = meta.isOriginal,
                    ["difficulty0"] = normalLv,
                    ["difficulty1"] = hardLv,
                    ["difficulty2"] = extremeLv,
                    ["artist"] = string.IsNullOrEmpty(meta.charter) ? (meta.artist ?? "Unknown") : $"{meta.artist} / Chart: {meta.charter}",
                    ["dlc"] = "",
                    ["createdAt"] = Directory.GetCreationTimeUtc(folderPath).ToString("o")
                };

                var entry = new CustomTrackEntry
                {
                    NumericId = pk,
                    TrackKey = trackKey,
                    FolderPath = folderPath,
                    Meta = meta,
                    GameTrack = new Track(jObj)
                };

                _entriesByKey[trackKey] = entry;
                _entriesList.Add(entry);
                Plugin.Log?.LogInfo($"Loaded custom track: {meta.title} ({trackKey}) [N:{normalLv} H:{hardLv} E:{extremeLv}]");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to load custom track in {folderPath}: {ex}");
            }
        }

        private static int GetValidDifficultyLevel(string folderPath, CustomDifficultyMeta diff)
        {
            if (diff == null || diff.level <= 0 || string.IsNullOrEmpty(diff.chartFile))
                return 0;
            return File.Exists(Path.Combine(folderPath, diff.chartFile)) ? diff.level : 0;
        }

        public static bool IsCustomTrack(string trackKey)
        {
            return !string.IsNullOrEmpty(trackKey) && _entriesByKey.ContainsKey(trackKey);
        }

        public static bool TryGetEntry(string trackKey, out CustomTrackEntry entry)
        {
            if (string.IsNullOrEmpty(trackKey))
            {
                entry = null;
                return false;
            }
            return _entriesByKey.TryGetValue(trackKey, out entry);
        }

        public static Sprite GetOrLoadCoverSprite(string trackKey)
        {
            if (!TryGetEntry(trackKey, out var entry)) return null;
            if (entry.CoverLoadAttempted) return entry.CoverSprite;

            entry.CoverLoadAttempted = true;
            try
            {
                string coverName = entry.Meta?.coverFile;
                string coverPath = !string.IsNullOrEmpty(coverName)
                    ? Path.Combine(entry.FolderPath, coverName)
                    : null;

                if (string.IsNullOrEmpty(coverPath) || !File.Exists(coverPath))
                {
                    foreach (string candidate in new[] { "cover.png", "cover.jpg", "cover.jpeg" })
                    {
                        string p = Path.Combine(entry.FolderPath, candidate);
                        if (File.Exists(p))
                        {
                            coverPath = p;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
                {
                    byte[] bytes = File.ReadAllBytes(coverPath);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (tex.LoadImage(bytes))
                    {
                        entry.CoverSprite = Sprite.Create(
                            tex,
                            new Rect(0, 0, tex.width, tex.height),
                            new Vector2(0.5f, 0.5f),
                            100f);
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(tex);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Failed to load cover for {trackKey}: {ex.Message}");
            }

            return entry.CoverSprite;
        }
    }
}
