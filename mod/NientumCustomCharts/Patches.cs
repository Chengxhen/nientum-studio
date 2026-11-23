using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameBase;
using HarmonyLib;
using KalpaCosmicSymphony.UI;
using Nientum.Models;
using UnityEngine;

namespace NientumCustomCharts
{
    internal static class Patches
    {
        private static readonly FieldInfo FieldOfflineData =
            AccessTools.Field(typeof(OfflinePatternLoadHelper), "data");

        private static readonly FieldInfo FieldUserTracksBacking =
            AccessTools.Field(typeof(User), "<Tracks>k__BackingField");

        private static readonly FieldInfo FieldCurrentFreePlayTrack =
            AccessTools.Field(typeof(ControlFreePlayTrackInfo), "currentFreePlayTrack");

        private static readonly FieldInfo FieldCurrentDifficulty =
            AccessTools.Field(typeof(ControlFreePlayTrackInfo), "currentDiffuclty");

        private static readonly FieldInfo FieldBgmName =
            AccessTools.Field(typeof(StageSoundController), "bgmName");

        private static readonly FieldInfo FieldPartEndTimes =
            AccessTools.Field(typeof(StageSoundController), "partEndTimes");

        private static readonly FieldInfo FieldUserSync =
            AccessTools.Field(typeof(StageSoundController), "userSync");

        private static string _activeIngameCustomKey;

        // ---------------------------------------------------------
        // 1. FreePlay Track List & Unlock Patches
        // ---------------------------------------------------------

        [HarmonyPatch(typeof(ControlFreePlayScrollView), "InitFreePlayTrack")]
        [HarmonyPrefix]
        private static void InitFreePlayTrack_Prefix()
        {
            if (Plugin.EnableAutoScan.Value)
            {
                CustomTrackManager.ReloadAll();
            }

            if (Global.User == null) return;

            var existing = Global.User.Tracks != null
                ? Global.User.Tracks.Where(t => !CustomTrackManager.IsCustomTrack(t.TrackItemKey)).ToList()
                : new List<Track>();

            foreach (var entry in CustomTrackManager.Entries)
            {
                existing.Add(entry.GameTrack);
            }

            FieldUserTracksBacking?.SetValue(Global.User, existing);
        }

        [HarmonyPatch(typeof(User), nameof(User.GetItemUnlocked), new[] { typeof(string) })]
        [HarmonyPrefix]
        private static bool GetItemUnlocked_Prefix(string itemKey, ref bool __result)
        {
            if (CustomTrackManager.IsCustomTrack(itemKey))
            {
                __result = true;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(ControlFreePlayTrackInfo), nameof(ControlFreePlayTrackInfo.GetLockType))]
        [HarmonyPostfix]
        private static void GetLockType_Postfix(ControlFreePlayTrackInfo __instance, ref FreePlayTrackLockType __result)
        {
            var fpTrack = FieldCurrentFreePlayTrack?.GetValue(__instance) as FreePlayTrack;
            if (fpTrack?.Track != null && CustomTrackManager.IsCustomTrack(fpTrack.Track.TrackItemKey))
            {
                int diff = (int)(FieldCurrentDifficulty?.GetValue(__instance) ?? 0);
                if (fpTrack.GetLevelFromDifficulty(diff) > 0)
                {
                    __result = FreePlayTrackLockType.NONE;
                }
            }
        }

        // ---------------------------------------------------------
        // 2. Cover & Thumbnail Patches
        // ---------------------------------------------------------

        private static string ExtractTrackKeyFromCoverName(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return null;
            if (filename.StartsWith("trackcover_", StringComparison.OrdinalIgnoreCase))
                return filename.Substring("trackcover_".Length);
            if (filename.StartsWith("trackthumbnail_", StringComparison.OrdinalIgnoreCase))
                return filename.Substring("trackthumbnail_".Length);
            return null;
        }

        [HarmonyPatch(typeof(TrackCoverManager), nameof(TrackCoverManager.IsTrackCoverLoaded))]
        [HarmonyPrefix]
        private static bool IsTrackCoverLoaded_Prefix(string filename, ref bool __result)
        {
            string key = ExtractTrackKeyFromCoverName(filename);
            if (CustomTrackManager.IsCustomTrack(key))
            {
                __result = true;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(TrackCoverManager), nameof(TrackCoverManager.LoadTrackCover))]
        [HarmonyPrefix]
        private static bool LoadTrackCover_Prefix(string filename, ref IEnumerator __result)
        {
            string key = ExtractTrackKeyFromCoverName(filename);
            if (CustomTrackManager.IsCustomTrack(key))
            {
                CustomTrackManager.GetOrLoadCoverSprite(key);
                __result = EmptyCoroutine();
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(TrackCoverManager), nameof(TrackCoverManager.GetTrackCover))]
        [HarmonyPrefix]
        private static bool GetTrackCover_Prefix(string filename, ref Sprite __result)
        {
            string key = ExtractTrackKeyFromCoverName(filename);
            if (CustomTrackManager.IsCustomTrack(key))
            {
                Sprite custom = CustomTrackManager.GetOrLoadCoverSprite(key);
                if (custom != null)
                {
                    __result = custom;
                    return false;
                }
                // Let original return spriteNoneCover if no custom cover file exists
                return true;
            }
            return true;
        }

        [HarmonyPatch(typeof(TrackCoverManager), nameof(TrackCoverManager.GetTrackThumbnail))]
        [HarmonyPrefix]
        private static bool GetTrackThumbnail_Prefix(string filename, ref Sprite __result)
        {
            string key = ExtractTrackKeyFromCoverName(filename);
            if (CustomTrackManager.IsCustomTrack(key))
            {
                Sprite custom = CustomTrackManager.GetOrLoadCoverSprite(key);
                if (custom != null)
                {
                    __result = custom;
                    return false;
                }
                return true;
            }
            return true;
        }

        private static IEnumerator EmptyCoroutine()
        {
            yield break;
        }

        // ---------------------------------------------------------
        // 3. FreePlay Preview Audio Patches
        // ---------------------------------------------------------

        [HarmonyPatch(typeof(SoundManager), nameof(SoundManager.PlayPreviewSound))]
        [HarmonyPrefix]
        private static bool PlayPreviewSound_Prefix(SoundManager __instance, string soundName)
        {
            if (CustomTrackManager.TryGetEntry(soundName, out var entry))
            {
                // Stop any official FMOD Studio preview first
                __instance.StopPreviewSound();
                string audioPath = entry.GetAudioPath();
                if (!string.IsNullOrEmpty(audioPath))
                {
                    FmodAudioEngine.StartPreview(audioPath, entry.Meta?.previewStartSec ?? 0f);
                }
                return false;
            }

            FmodAudioEngine.StopPreview();
            return true;
        }

        [HarmonyPatch(typeof(SoundManager), nameof(SoundManager.StopPreviewSound))]
        [HarmonyPrefix]
        private static void StopPreviewSound_Prefix()
        {
            FmodAudioEngine.StopPreview();
        }

        [HarmonyPatch(typeof(SoundManager), nameof(SoundManager.FadeOutPreviewSound))]
        [HarmonyPrefix]
        private static void FadeOutPreviewSound_Prefix()
        {
            FmodAudioEngine.StopPreview();
        }

        // ---------------------------------------------------------
        // 4. Ingame Chart Loading Patches
        // ---------------------------------------------------------

        [HarmonyPatch(typeof(StageMapLoader), nameof(StageMapLoader.LoadFreePlay))]
        [HarmonyPrefix]
        private static void LoadFreePlay_Prefix(string key, int difficulty)
        {
            ChartBuilder.PendingChartPath = null;
            ChartBuilder.PendingOffsetSec = 0f;

            if (CustomTrackManager.TryGetEntry(key, out var entry))
            {
                string chartPath = entry.GetChartPath(difficulty);
                if (!string.IsNullOrEmpty(chartPath))
                {
                    OfflinePatternLoadHelper.isTest = true;
                    FieldOfflineData?.SetValue(null, new byte[] { 1 });
                    ChartBuilder.PendingChartPath = chartPath;
                    ChartBuilder.PendingOffsetSec = (entry.Meta?.offsetMs ?? 0f) / 1000f;
                    Plugin.Log?.LogInfo($"[Ingame] Preparing custom chart: {chartPath}");
                }
            }
        }

        [HarmonyPatch(typeof(CRAPlayDataReaderBase), nameof(CRAPlayDataReaderBase.DoPlayDataReader))]
        [HarmonyPrefix]
        private static bool DoPlayDataReader_Prefix(CRAPlayDataReaderBase __instance, ref CRAPlayDataBase.EPlayDataReadError __result)
        {
            if (string.IsNullOrEmpty(ChartBuilder.PendingChartPath)) return true;

            string path = ChartBuilder.PendingChartPath;
            float offset = ChartBuilder.PendingOffsetSec;
            ChartBuilder.PendingChartPath = null;
            ChartBuilder.PendingOffsetSec = 0f;

            if (__instance is LioraPlayDataReader reader)
            {
                bool ok = ChartBuilder.PopulateFromFile(reader, path, offset);
                __result = ok ? CRAPlayDataBase.EPlayDataReadError.None : CRAPlayDataBase.EPlayDataReadError.SignatureNotMatch;
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------
        // 5. Ingame Audio Playback Patches (StageSoundController)
        // ---------------------------------------------------------

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.SetBGM))]
        [HarmonyPostfix]
        private static void SetBGM_Postfix(string bgmName)
        {
            _activeIngameCustomKey = CustomTrackManager.IsCustomTrack(bgmName) ? bgmName : null;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.IsBgmLoad), MethodType.Getter)]
        [HarmonyPrefix]
        private static bool IsBgmLoad_Prefix(ref bool __result)
        {
            if (_activeIngameCustomKey != null)
            {
                __result = FmodAudioEngine.HasIngameBgm;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.LoadBGM))]
        [HarmonyPrefix]
        private static bool LoadBGM_Prefix(StageSoundController __instance, ref IEnumerator __result)
        {
            string bgmName = FieldBgmName?.GetValue(__instance) as string;
            if (CustomTrackManager.TryGetEntry(bgmName, out var entry))
            {
                _activeIngameCustomKey = bgmName;
                string audioPath = entry.GetAudioPath();
                FmodAudioEngine.LoadIngame(audioPath);
                __result = EmptyCoroutine();
                return false;
            }
            _activeIngameCustomKey = null;
            return true;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.PlayTrackSound))]
        [HarmonyPrefix]
        private static bool PlayTrackSound_Prefix(StageSoundController __instance, int index, float startTime, bool shouldStartPlayBGM, bool isGoToNextPartInMiddle)
        {
            if (_activeIngameCustomKey == null) return true;

            if (shouldStartPlayBGM | isGoToNextPartInMiddle)
            {
                float offset = GetPartTimeOffset(__instance, index);
                FmodAudioEngine.PlayIngame(offset + startTime);
            }
            return false;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.SetTrackTimelinePosition))]
        [HarmonyPrefix]
        private static bool SetTrackTimelinePosition_Prefix(StageSoundController __instance, int index, float startTime)
        {
            if (_activeIngameCustomKey == null) return true;
            float offset = GetPartTimeOffset(__instance, index);
            FmodAudioEngine.SetIngamePosition(offset + startTime);
            return false;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.PlayBGM))]
        [HarmonyPrefix]
        private static bool PlayBGM_Prefix()
        {
            if (_activeIngameCustomKey == null) return true;
            FmodAudioEngine.PlayIngameCurrent();
            return false;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.StopCurrentBGM))]
        [HarmonyPrefix]
        private static bool StopCurrentBGM_Prefix()
        {
            if (_activeIngameCustomKey == null) return true;
            FmodAudioEngine.StopIngame();
            return false;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.Pause))]
        [HarmonyPrefix]
        private static bool Pause_Prefix()
        {
            if (_activeIngameCustomKey == null) return true;
            FmodAudioEngine.PauseIngame();
            return false;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.Resume))]
        [HarmonyPrefix]
        private static bool Resume_Prefix()
        {
            if (_activeIngameCustomKey == null) return true;
            FmodAudioEngine.ResumeIngame();
            return false;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.SetTrackSoundPitch))]
        [HarmonyPrefix]
        private static bool SetTrackSoundPitch_Prefix(float pitch)
        {
            if (_activeIngameCustomKey == null) return true;
            FmodAudioEngine.SetIngamePitch(pitch);
            return false;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.SetVolume))]
        [HarmonyPrefix]
        private static bool SetVolume_Prefix(float value)
        {
            if (_activeIngameCustomKey == null) return true;
            FmodAudioEngine.SetIngameVolume(value);
            return false;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.DoFadeNotCheckPause))]
        [HarmonyPrefix]
        private static bool DoFadeNotCheckPause_Prefix(float value, float duration, ref IEnumerator __result)
        {
            if (_activeIngameCustomKey == null) return true;
            __result = FadeIngameCoroutine(1f, value, duration, stopAtEnd: false);
            return false;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.Stop))]
        [HarmonyPrefix]
        private static bool Stop_Prefix(bool isFadeOut, ref IEnumerator __result)
        {
            if (_activeIngameCustomKey == null) return true;
            if (isFadeOut)
            {
                __result = FadeIngameCoroutine(1f, 0f, 0.1f, stopAtEnd: true);
            }
            else
            {
                FmodAudioEngine.StopIngame();
                __result = EmptyCoroutine();
            }
            return false;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.IsTrackPlaying))]
        [HarmonyPrefix]
        private static bool IsTrackPlaying_Prefix(ref bool __result)
        {
            if (_activeIngameCustomKey == null) return true;
            __result = FmodAudioEngine.IsIngamePlaying();
            return false;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.GetMusicPartTime))]
        [HarmonyPrefix]
        private static bool GetMusicPartTime_Prefix(StageSoundController __instance, int index, ref float __result)
        {
            if (_activeIngameCustomKey == null) return true;
            uint posMs = FmodAudioEngine.GetIngamePositionMs();
            int userSync = (int)(FieldUserSync?.GetValue(__instance) ?? 0);
            float prevParts = GetPartTimeOffset(__instance, index);
            __result = (float)((int)posMs + userSync) / 1000f - prevParts;
            return false;
        }

        [HarmonyPatch(typeof(StageSoundController), nameof(StageSoundController.Release))]
        [HarmonyPrefix]
        private static bool Release_Prefix()
        {
            if (_activeIngameCustomKey == null) return true;
            FmodAudioEngine.ReleaseIngame();
            _activeIngameCustomKey = null;
            SoundManager.Instance?.ResetSound();
            return false;
        }

        private static float GetPartTimeOffset(StageSoundController controller, int index)
        {
            var list = FieldPartEndTimes?.GetValue(controller) as List<float>;
            if (list == null) return 0f;
            float sum = 0f;
            for (int i = 0; i < list.Count && i < index; i++)
                sum += list[i];
            return sum;
        }

        private static IEnumerator FadeIngameCoroutine(float from, float to, float duration, bool stopAtEnd)
        {
            if (duration <= 0f)
            {
                FmodAudioEngine.SetIngameVolume(to);
                if (stopAtEnd) FmodAudioEngine.StopIngame();
                yield break;
            }
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                FmodAudioEngine.SetIngameVolume(Mathf.Lerp(from, to, elapsed / duration));
                yield return null;
            }
            FmodAudioEngine.SetIngameVolume(to);
            if (stopAtEnd) FmodAudioEngine.StopIngame();
        }
    }
}
