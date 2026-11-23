using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NientumStudio.Models;

namespace NientumStudio.Services
{
    public enum IssueLevel { Error, Warning, Info }

    public sealed class ChartIssue
    {
        public IssueLevel Level;
        public string Category;
        public string Message;
    }

    public sealed class ChartReport
    {
        public int CrystalCount;
        public int DodgeCount;
        public int OrbCount;
        public int MultiHitCount;
        public int CoinCount;
        public int SuperJumpCount;
        public int LongMiddleCount;
        public int EventCount;
        public int TotalNotes;

        public double LastNoteTime;
        public double AvgNps;
        public double PeakNps;
        public List<double> NpsPerSecond = new List<double>(); // 每秒物量

        public int SuggestedLevel;
        public string SuggestedLevelText;

        public List<ChartIssue> Issues = new List<ChartIssue>();

        public int ErrorCount => Issues.Count(i => i.Level == IssueLevel.Error);
        public int WarnCount => Issues.Count(i => i.Level == IssueLevel.Warning);
        public int InfoCount => Issues.Count(i => i.Level == IssueLevel.Info);
    }

    public static class ChartValidator
    {
        public static ChartReport Analyze(JArray items, double audioDuration, float offsetMs, double defaultBpm)
        {
            var report = new ChartReport();
            if (items == null) return report;

            var objects = items.OfType<JObject>().ToList();
            var idCounts = new Dictionary<int, int>();
            var bpmSegments = new List<(double time, double bpm)>();
            var orbList = new List<JObject>();
            var longMiddleIds = new HashSet<int>();
            var longMiddleObjs = new List<JObject>();
            var superJumpBodies = new List<JObject>();
            var superJumpDividers = new List<JObject>();
            var noteTimes = new List<(double t, int lane)>();
            double lastNoteTime = 0, lastAnything = 0;
            int noteTotal = 0;

            foreach (var o in objects)
            {
                string tn = o.GetTypeName();
                double t = o.GetTime();
                int id = o.GetId();
                if (id != 0)
                {
                    idCounts.TryGetValue(id, out int c);
                    idCounts[id] = c + 1;
                }
                if (t < -0.001)
                    report.Issues.Add(new ChartIssue { Level = IssueLevel.Error, Category = "时间", Message = $"[{tn.Replace("CRAFormatData", "")}] 存在负时间 {t:F2}s" });

                if (tn == "CRAFormatDataEventBPMBar")
                {
                    double bpm = o.Value<double?>("fBPM") ?? defaultBpm;
                    double baseBeat = o.Value<double?>("fBPMBase") ?? 4;
                    int beat = o.Value<int?>("iBPMBeat") ?? 4;
                    bpmSegments.Add((t, bpm > 0 ? bpm : 120));
                    if (bpm < 20 || bpm > 400)
                        report.Issues.Add(new ChartIssue { Level = IssueLevel.Warning, Category = "BPM", Message = $"BPM 事件 @ {t:F2}s 的值 {bpm:F1} 超出 20-400 范围" });
                    if (beat < 1 || beat > 16)
                        report.Issues.Add(new ChartIssue { Level = IssueLevel.Warning, Category = "BPM", Message = $"BPM 事件 @ {t:F2}s 的拍号 {beat} 异常（应为 1-16）" });
                }
                else if (tn == "CRAFormatDataEventSpeed")
                {
                    double sp = o.Value<double?>("fSpeedValue") ?? 1.0;
                    if (sp < 0.01 || sp > 999.0)
                        report.Issues.Add(new ChartIssue { Level = IssueLevel.Warning, Category = "速度", Message = $"卷轴速度 @ {t:F2}s 的值 {sp:F2} 超出 0.01-999.0 范围" });
                }
                else if (tn == "CRAFormatDataFFChangeTheme")
                {
                    int theme = o.Value<int?>("eFFTheme") ?? 0;
                    if (ChartConstants.ThemeOptions.All(x => x.val != theme))
                        report.Issues.Add(new ChartIssue { Level = IssueLevel.Warning, Category = "主题", Message = $"主题 @ {t:F2}s 使用未映射值 {theme}，游戏内会被回退为 ForestOz（奥兹森林）" });
                }
                else if (tn == "CRAFormatDataNoteCrystal")
                {
                    noteTotal++;
                    if (o.IsDodge()) report.DodgeCount++; else report.CrystalCount++;
                    noteTimes.Add((t, o.GetLane()));
                    lastNoteTime = Math.Max(lastNoteTime, t);
                    lastAnything = Math.Max(lastAnything, t);
                }
                else if (tn == "CRAFormatDataCoin")
                {
                    noteTotal++;
                    report.CoinCount++;
                    lastNoteTime = Math.Max(lastNoteTime, t);
                    lastAnything = Math.Max(lastAnything, t);
                }
                else if (tn == "CRAFormatDataNoteOrb")
                {
                    noteTotal++;
                    report.OrbCount++;
                    orbList.Add(o);
                    noteTimes.Add((t, o.GetLane()));
                    double end = o.GetEndTime();
                    lastNoteTime = Math.Max(lastNoteTime, end);
                    lastAnything = Math.Max(lastAnything, end);
                    if (end <= t + 0.001)
                        report.Issues.Add(new ChartIssue { Level = IssueLevel.Error, Category = "长条", Message = $"长条 @ {t:F2}s 的结束时间不晚于开始时间" });
                }
                else if (tn == "CRAFormatDataNoteMultiHit")
                {
                    noteTotal++;
                    report.MultiHitCount++;
                    noteTimes.Add((t, o.GetLane()));
                    double end = o.GetEndTime();
                    lastNoteTime = Math.Max(lastNoteTime, end);
                    lastAnything = Math.Max(lastAnything, end);
                    if (end <= t + 0.001)
                        report.Issues.Add(new ChartIssue { Level = IssueLevel.Error, Category = "连打", Message = $"连打 @ {t:F2}s 的结束时间不晚于开始时间" });
                }
                else if (tn == "CRAFormatDataSuperJumpBody")
                {
                    noteTotal++;
                    report.SuperJumpCount++;
                    superJumpBodies.Add(o);
                    double end = o.GetEndTime();
                    lastNoteTime = Math.Max(lastNoteTime, end);
                    lastAnything = Math.Max(lastAnything, end);
                    if (end <= t + 0.001)
                        report.Issues.Add(new ChartIssue { Level = IssueLevel.Error, Category = "大跳", Message = $"大跳 @ {t:F2}s 的结束时间不晚于开始时间" });
                }
                else if (tn == "CRAFormatDataSuperJumpDivider")
                {
                    superJumpDividers.Add(o);
                }
                else if (tn == "CRAFormatDataNoteLongMiddle")
                {
                    report.LongMiddleCount++;
                    longMiddleIds.Add(id);
                    longMiddleObjs.Add(o);
                }
                else
                {
                    report.EventCount++;
                    lastAnything = Math.Max(lastAnything, t);
                }
            }

            report.TotalNotes = noteTotal;
            report.LastNoteTime = lastNoteTime;

            // ---- 配对检查：长条中段（按轨道预分组避免 O(n²)） ----
            var middleByLane = longMiddleObjs.GroupBy(m => m.GetLane()).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var orb in orbList)
            {
                double t0 = orb.GetTime(), t1 = orb.GetEndTime();
                var ids = (orb["LongMiddleIds"] as JArray)?.Values<int>().ToList() ?? new List<int>();
                foreach (int mid in ids)
                {
                    if (!longMiddleIds.Contains(mid))
                        report.Issues.Add(new ChartIssue { Level = IssueLevel.Error, Category = "长条", Message = $"长条 @ {t0:F2}s 引用的中段 #{mid} 不存在" });
                }
                if (middleByLane.TryGetValue(orb.GetLane(), out var laneMiddles))
                {
                    foreach (var midObj in laneMiddles)
                    {
                        if (midObj.GetTime() > t0 + 0.001 && midObj.GetTime() < t1 - 0.001 && !ids.Contains(midObj.GetId()))
                            report.Issues.Add(new ChartIssue { Level = IssueLevel.Info, Category = "长条", Message = $"中段 @ {midObj.GetTime():F2}s 位于长条内但没有挂接（未计入 LongMiddleIds）" });
                    }
                }
                if (ids.Count == 0 && t1 - t0 >= 2.0)
                    report.Issues.Add(new ChartIssue { Level = IssueLevel.Info, Category = "长条", Message = $"长条 @ {t0:F2}s 跨度 {t1 - t0:F1}s，没有中段判定点（可在检查器一键补全）" });
            }

            // 孤立中段（没有被任何长条引用）
            var referenced = new HashSet<int>(orbList.SelectMany(o =>
                (o["LongMiddleIds"] as JArray)?.Values<int>() ?? Enumerable.Empty<int>()));
            foreach (var midObj in longMiddleObjs)
            {
                if (!referenced.Contains(midObj.GetId()))
                    report.Issues.Add(new ChartIssue { Level = IssueLevel.Info, Category = "长条", Message = $"中段 @ {midObj.GetTime():F2}s 未被任何长条引用（孤立节点，建议删除）" });
            }

            // ---- 配对检查：大跳 ----
            var bodyDividerIds = new HashSet<int>(superJumpBodies.Select(b => b.Value<int?>("DividerId") ?? 0));
            var dividerIds = new HashSet<int>(superJumpDividers.Select(d => d.GetId()));
            foreach (var body in superJumpBodies)
            {
                int divId = body.Value<int?>("DividerId") ?? 0;
                if (divId == 0 || !dividerIds.Contains(divId))
                    report.Issues.Add(new ChartIssue { Level = IssueLevel.Error, Category = "大跳", Message = $"大跳 @ {body.GetTime():F2}s 缺少配对的分隔点（DividerId={divId}），游戏内不会生效" });
            }
            foreach (var div in superJumpDividers)
            {
                if (!bodyDividerIds.Contains(div.GetId()))
                    report.Issues.Add(new ChartIssue { Level = IssueLevel.Warning, Category = "大跳", Message = $"分隔点 @ {div.GetTime():F2}s 没有对应的跳跃主体" });
            }

            // ---- 同轨重合 / 双押统计 ----
            var byLane = noteTimes.GroupBy(n => n.lane);
            int overlapCount = 0;
            foreach (var g in byLane)
            {
                var sorted = g.OrderBy(n => n.t).ToList();
                for (int i = 1; i < sorted.Count; i++)
                {
                    double dt = sorted[i].t - sorted[i - 1].t;
                    if (dt < 0.045) overlapCount++;
                }
            }
            if (overlapCount > 0)
                report.Issues.Add(new ChartIssue { Level = IssueLevel.Warning, Category = "手感", Message = $"同轨音符重合 {overlapCount} 处（间隔 <45ms），判定可能互相干扰" });

            int simultCount = 0;
            var allSorted = noteTimes.OrderBy(n => n.t).ToList();
            for (int i = 1; i < allSorted.Count; i++)
                if (Math.Abs(allSorted[i].t - allSorted[i - 1].t) < 0.02 && allSorted[i].lane != allSorted[i - 1].lane) simultCount++;
            if (simultCount > 0)
                report.Issues.Add(new ChartIssue { Level = IssueLevel.Info, Category = "手感", Message = $"双轨同时押 {simultCount} 处" });

            // ---- 重复 ID ----
            int dupIds = idCounts.Count(kv => kv.Value > 1);
            if (dupIds > 0)
                report.Issues.Add(new ChartIssue { Level = IssueLevel.Warning, Category = "数据", Message = $"存在 {dupIds} 个重复的 hFormatDataID（保存时游戏可能加载异常）" });

            // ---- 曲终 / 音频 ----
            if (!objects.Any(o => o.GetTypeName() == "CRAFormatDataEndOfTrack"))
                report.Issues.Add(new ChartIssue { Level = IssueLevel.Error, Category = "结构", Message = "缺少曲终标记（EndOfTrack），游戏可能无法正常结束曲目" });

            if (noteTotal == 0)
                report.Issues.Add(new ChartIssue { Level = IssueLevel.Warning, Category = "结构", Message = "谱面还没有任何音符" });

            if (audioDuration > 1)
            {
                // 游戏内判定时间 = 谱面时间 + offset，音频检查要用偏移后的时间
                double lastGameTime = lastNoteTime + offsetMs / 1000.0;
                if (lastGameTime > audioDuration + 0.5)
                    report.Issues.Add(new ChartIssue { Level = IssueLevel.Warning, Category = "音频", Message = $"末音符游戏内时间 @ {lastGameTime:F1}s 超出音频时长 {audioDuration:F1}s（偏移 {offsetMs:F0}ms）" });
                else if (audioDuration - lastGameTime > 8)
                    report.Issues.Add(new ChartIssue { Level = IssueLevel.Info, Category = "音频", Message = $"音频尾部 {audioDuration - lastGameTime:F1}s 无音符（如为留白可忽略）" });
            }

            if (Math.Abs(offsetMs) > 500)
                report.Issues.Add(new ChartIssue { Level = IssueLevel.Info, Category = "音频", Message = $"音频偏移 {offsetMs:F0}ms 较大，请确认与游戏内一致" });

            // ---- NPS 曲线（每秒物量）----
            double span = Math.Max(lastAnything, audioDuration > 1 ? audioDuration : lastAnything);
            int buckets = Math.Min(600, Math.Max(1, (int)Math.Ceiling(span)));
            var counts = new double[buckets];
            foreach (var n in noteTimes)
            {
                int idx = (int)Math.Min(buckets - 1, Math.Max(0, Math.Floor(n.t)));
                counts[idx] += 1;
            }
            report.NpsPerSecond = counts.ToList();
            report.AvgNps = lastNoteTime > 1 ? noteTotal / lastNoteTime : 0;
            double peak = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                double sum = 0;
                for (int k = 0; k < 2 && i + k < counts.Length; k++) sum += counts[i + k];
                peak = Math.Max(peak, sum / Math.Min(2, counts.Length - i));
            }
            report.PeakNps = peak;

            // ---- 难度估算（启发式，仅供参考）----
            double specialWeight = report.MultiHitCount * 0.6 + report.SuperJumpCount * 1.0 + report.OrbCount * 0.3 + simultCount * 0.2;
            double raw = report.PeakNps * 0.55 + report.AvgNps * 0.8 + specialWeight / Math.Max(30, report.TotalNotes) * 12;
            int lv = (int)Math.Round(raw);
            lv = Math.Clamp(lv, 1, 12);
            report.SuggestedLevel = lv;
            report.SuggestedLevelText = lv <= 2 ? "入门" : lv <= 5 ? "普通" : lv <= 8 ? "进阶" : lv <= 10 ? "高手" : "极限";

            return report;
        }
    }
}