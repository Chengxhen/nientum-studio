using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NientumCustomCharts
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.local.nientum.gallery", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.nientum.customcharts";
        public const string PluginName = "Nientum Custom Charts";
        public const string PluginVersion = "1.0.1";

        internal static ManualLogSource Log;
        internal static ConfigEntry<string> ExtraChartsDirectory;
        internal static ConfigEntry<bool> EnableAutoScan;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            string defaultExtra = Path.Combine(Paths.GameRootPath, "CustomCharts");
            ExtraChartsDirectory = Config.Bind(
                "Paths",
                "ExtraChartsDirectory",
                defaultExtra,
                "Optional secondary folder (e.g. editor workspace) to also load custom charts from.");

            EnableAutoScan = Config.Bind(
                "General",
                "AutoScanOnFreePlay",
                true,
                "Automatically rescan custom charts whenever entering FreePlay.");

            DiagLog.InitSession();
            // Ensure primary CustomCharts folder exists inside the game directory
            string gameChartsDir = Path.Combine(Paths.GameRootPath, "CustomCharts");
            Directory.CreateDirectory(gameChartsDir);

            CustomTrackManager.ReloadAll();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Patches));
            TryPatchGalleryCompat();

            DiagLog.Info($"{PluginName} v{PluginVersion} loaded! Found {CustomTrackManager.Count} custom chart(s).");
        }

        private void TryPatchGalleryCompat()
        {
            const string galleryGuid = "com.local.nientum.gallery";
            if (!Chainloader.PluginInfos.TryGetValue(galleryGuid, out var galleryInfo) || galleryInfo?.Instance == null)
            {
                return;
            }

            try
            {
                Type galleryType = galleryInfo.Instance.GetType();
                MethodInfo showMethod = AccessTools.Method(galleryType, "Show");
                MethodInfo hideMethod = AccessTools.Method(galleryType, "Hide");
                if (showMethod != null)
                {
                    _harmony.Patch(showMethod, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnGalleryShow)));
                }
                if (hideMethod != null)
                {
                    _harmony.Patch(hideMethod, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnGalleryHide)));
                }
                Log.LogInfo($"[Compat] 检测到 Nientum 鉴赏室 ({galleryInfo.Metadata.Version})，已启用音频避让与兼容补丁。");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Compat] 鉴赏室兼容补丁挂载失败（不影响主功能）: {ex.Message}");
            }
        }

        private static void OnGalleryShow()
        {
            FmodAudioEngine.SetPreviewDucked(true);
        }

        private static void OnGalleryHide()
        {
            FmodAudioEngine.SetPreviewDucked(false);
        }

        private void Update()
        {
            // F8 hotkey for manual hot-reload
            if (Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame)
            {
                CustomTrackManager.ReloadAll();
                Log.LogInfo($"[HotReload] Reloaded custom charts. Count = {CustomTrackManager.Count}");
            }
        }

        private void OnDestroy()
        {
            FmodAudioEngine.ReleaseAll();
            _harmony?.UnpatchSelf();
        }
    }
}
