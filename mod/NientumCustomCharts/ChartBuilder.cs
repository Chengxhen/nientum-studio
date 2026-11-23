using System;
using System.Collections.Generic;
using System.IO;
using FormatLiora;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace NientumCustomCharts
{
    public static class ChartBuilder
    {
        public static string PendingChartPath { get; set; }
        public static float PendingOffsetSec { get; set; }

        private static readonly Type HeaderType =
            typeof(CRAPlayDataBase).GetNestedType("SPlayDataHeader", System.Reflection.BindingFlags.NonPublic);

        public static bool PopulateFromFile(LioraPlayDataReader reader, string chartPath, float offsetSec)
        {
            try
            {
                DiagLog.Info($"[ChartBuilder] PopulateFromFile start: {chartPath}, offset={offsetSec:F3}s");
                if (string.IsNullOrEmpty(chartPath) || !File.Exists(chartPath))
                {
                    DiagLog.Error($"[ChartBuilder] Chart file not found: {chartPath}");
                    return false;
                }

                if (chartPath.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] raw = File.ReadAllBytes(chartPath);
                    var err = reader.DoPlayDataReader(raw);
                    DiagLog.Info($"[ChartBuilder] Binary chart read result={err}, bytes={raw.Length}");
                    return err == CRAPlayDataBase.EPlayDataReadError.None;
                }

                string jsonText = File.ReadAllText(chartPath);
                var doc = JObject.Parse(jsonText);

                // 实验版：读取同目录 <diff>.layers.json 的斜轨方向设置
                var layerAngles = LoadLayerAngles(chartPath);
                var blockAngles = new Dictionary<int, float>();
                var camKeyframes = new List<CustomCameraController.CameraKeyframe>();

                var list = reader.GetList();
                list.Clear();

                var items = doc["items"] as JArray ?? new JArray();
                var radAsm = typeof(CRAPlayDataBase).Assembly;

                int autoId = 1;
                bool hasEndOfTrack = false;
                float maxTime = 0f;

                foreach (JToken token in items)
                {
                    if (!(token is JObject obj)) continue;
                    string typeName = obj.Value<string>("$type");
                    if (string.IsNullOrEmpty(typeName)) continue;

                    Type t = radAsm.GetType("FormatLiora." + typeName, throwOnError: false);
                    if (t == null || !typeof(CRAPlayDataBase.CRAFormatDataBase).IsAssignableFrom(t))
                    {
                        DiagLog.Warn($"[ChartBuilder] Unknown chart item type: {typeName}");
                        continue;
                    }

                    // 提取实验版自定义镜头参数，并在反序列化前移除自定义键
                    float camX = obj.Value<float?>("camOffsetX") ?? 0f;
                    float camY = obj.Value<float?>("camOffsetY") ?? 0f;
                    float camZ = obj.Value<float?>("camOffsetZ") ?? 0f;
                    int camEase = obj.Value<int?>("camEase") ?? 0;

                    var clean = (JObject)obj.DeepClone();
                    clean.Remove("$type");
                    clean.Remove("$typeName");
                    clean.Remove("$markerType");
                    clean.Remove("camOffsetX");
                    clean.Remove("camOffsetY");
                    clean.Remove("camOffsetZ");
                    clean.Remove("camEase");

                    var item = (CRAPlayDataBase.CRAFormatDataBase)clean.ToObject(t);
                    if (item.hFormatDataID == 0)
                    {
                        item.hFormatDataID = autoId++;
                    }
                    else
                    {
                        autoId = Math.Max(autoId, item.hFormatDataID + 1);
                    }

                    // 记录所属图层的斜轨角度（仅当非 0° 时记录）
                    int layerIdx = obj.Value<int?>("editorLayer") ?? 0;
                    if (layerIdx >= 0 && layerIdx < layerAngles.Count)
                    {
                        float ang = layerAngles[layerIdx];
                        if (Math.Abs(ang) > 0.1f)
                        {
                            blockAngles[item.hFormatDataID] = ang;
                            if (obj["LongMiddleIds"] is JArray mids)
                            {
                                foreach (var mt in mids)
                                {
                                    int midId = mt.Value<int>();
                                    if (midId > 0) blockAngles[midId] = ang;
                                }
                            }
                        }
                    }

                    // Apply chart-wide audio offset (positive offset shifts notes later)
                    if (Math.Abs(offsetSec) > 0.0001f)
                    {
                        item.fTimePlay = Math.Max(0f, item.fTimePlay + offsetSec);
                        if (item is CRAFormatDataNoteLongBase longItem)
                        {
                            longItem.fLongNotePairTimePlay = Math.Max(item.fTimePlay, longItem.fLongNotePairTimePlay + offsetSec);
                        }
                    }

                    if (item is CRAFormatDataFFChangeCameraPreset camPreset &&
                        (Math.Abs(camX) > 0.001f || Math.Abs(camY) > 0.001f || Math.Abs(camZ) > 0.001f || camEase != 0))
                    {
                        float st = Math.Max(0f, camPreset.fTimePlay);
                        float et = Math.Max(st + 0.05f, camPreset.fLongNotePairTimePlay > st ? camPreset.fLongNotePairTimePlay : st + 0.5f);
                        camKeyframes.Add(new CustomCameraController.CameraKeyframe
                        {
                            StartTime = st,
                            EndTime = et,
                            TargetOffset = new UnityEngine.Vector3(camX, camY, camZ),
                            Ease = camEase
                        });
                    }

                    // Guard EFFTheme against unmapped enum values so StageMapLoader doesn't throw KeyNotFoundException
                    if (item is CRAFormatDataFFChangeTheme themeItem)
                    {
                        if (!MapLoaderHelper.FFTHEME_KEY.ContainsKey(themeItem.eFFTheme))
                        {
                            themeItem.eFFTheme = EFFTheme.ForestOz;
                        }
                    }

                    if (item is CRAFormatDataEndOfTrack)
                    {
                        hasEndOfTrack = true;
                    }

                    maxTime = Math.Max(maxTime, item.fTimePlay);
                    if (item is CRAFormatDataNoteLongBase lb)
                    {
                        maxTime = Math.Max(maxTime, lb.fLongNotePairTimePlay);
                    }

                    item.iFormatDataIndex = list.Count;
                    list.Add(item);
                }

                if (!hasEndOfTrack)
                {
                    var eot = new CRAFormatDataEndOfTrack
                    {
                        hFormatDataID = autoId++,
                        fTimePlay = maxTime + 3.0f,
                        iFormatDataIndex = list.Count
                    };
                    list.Add(eot);
                }

                AngledTrackController.ConfigureChart(blockAngles);
                CustomCameraController.ConfigureChart(camKeyframes);

                var onRead = AccessTools.Method(typeof(LioraPlayDataReader), "OnPlayDataRead");
                onRead?.Invoke(reader, new object[] { CRAPlayDataBase.EPlayDataReadError.None });
                DiagLog.Info($"[ChartBuilder] PopulateFromFile OK: items={list.Count}, maxTime={maxTime:F2}s, angled={blockAngles.Count}, camKf={camKeyframes.Count}");
                return true;
            }
            catch (Exception ex)
            {
                DiagLog.Error($"[ChartBuilder] PopulateFromFile FAILED: {chartPath}", ex);
                return false;
            }
        }

        private static List<float> LoadLayerAngles(string chartPath)
        {
            var angles = new List<float>();
            try
            {
                string dir = Path.GetDirectoryName(chartPath);
                string layersPath = Path.Combine(dir ?? "", Path.GetFileNameWithoutExtension(chartPath) + ".layers.json");
                if (!File.Exists(layersPath)) return angles;

                var ldoc = JObject.Parse(File.ReadAllText(layersPath));
                if (ldoc["Layers"] is JArray arr)
                {
                    foreach (var tok in arr)
                    {
                        float ang = tok?["Angle"]?.Value<float>() ?? 0f;
                        if (ang < -180f) ang = -180f;
                        if (ang > 180f) ang = 180f;
                        angles.Add(ang);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[Exp] Failed to load layers.json: {ex.Message}");
            }
            return angles;
        }
    }
}
