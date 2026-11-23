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
            if (chartPath.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
            {
                byte[] raw = File.ReadAllBytes(chartPath);
                var err = reader.DoPlayDataReader(raw);
                return err == CRAPlayDataBase.EPlayDataReadError.None;
            }

            string jsonText = File.ReadAllText(chartPath);
            var doc = JObject.Parse(jsonText);

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
                    Plugin.Log?.LogWarning($"Unknown chart item type: {typeName}");
                    continue;
                }

                // Clone and remove metadata keys
                var clean = (JObject)obj.DeepClone();
                clean.Remove("$type");
                clean.Remove("$typeName");
                clean.Remove("$markerType");

                var item = (CRAPlayDataBase.CRAFormatDataBase)clean.ToObject(t);
                if (item.hFormatDataID == 0)
                {
                    item.hFormatDataID = autoId++;
                }
                else
                {
                    autoId = Math.Max(autoId, item.hFormatDataID + 1);
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

            var onRead = AccessTools.Method(typeof(LioraPlayDataReader), "OnPlayDataRead");
            onRead?.Invoke(reader, new object[] { CRAPlayDataBase.EPlayDataReadError.None });
            return true;
        }
    }
}
