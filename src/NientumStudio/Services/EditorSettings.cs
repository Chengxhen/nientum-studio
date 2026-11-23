using System;
using System.IO;
using Newtonsoft.Json;

namespace NientumStudio.Services
{
    /// <summary>编辑器用户设置（发布目录 ui_settings.json）。</summary>
    public sealed class EditorSettings
    {
        public int SettingsVersion { get; set; } = 0;
        public string GamePath { get; set; } = "";
        public bool Hitsound { get; set; } = true;
        public bool Metronome { get; set; }
        public bool FollowPlayhead { get; set; } = true;
        public bool AutoMiddle { get; set; } = true;
        public bool ShowWaveform { get; set; } = true;
        public double MasterVolume { get; set; } = 1.0;
        public double HitsoundVolume { get; set; } = 0.6;
        public int HitLeadMs { get; set; } = 20;
        public double MetronomeVolume { get; set; } = 0.55;
        public double DefaultRate { get; set; } = 1.0;
        public int SnapDiv { get; set; } = 4;
        public int PreviewGridDiv { get; set; } = 2;
        public bool PanelLeftVisible { get; set; } = true;
        public bool PanelRightVisible { get; set; } = true;
        public bool PanelLibrary { get; set; } = true;
        public bool PanelMeta { get; set; } = true;
        public bool PanelHealth { get; set; } = true;
        public bool PanelInspector { get; set; } = true;
        // 性能选项（默认档针对本机 RTX 5070 Laptop / 2560x1600@165Hz）
        public bool GpuAcceleration { get; set; } = true;
        public double PreviewScale { get; set; } = 1.0;
        public int MaxFps { get; set; } = 0;
        public string RenderQuality { get; set; } = "high";

        public static string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui_settings.json");

        private static EditorSettings _current;
        public static EditorSettings Current
        {
            get
            {
                if (_current == null) _current = Load();
                return _current;
            }
        }

        public static EditorSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonConvert.DeserializeObject<EditorSettings>(File.ReadAllText(FilePath));
                    if (s != null)
                    {
                        if (s.SettingsVersion < 4)
                        {
                            // v4：打击音默认开启；左右整体面板状态迁移到四个独立面板
                            s.Hitsound = true;
                            if (s.SettingsVersion < 3) s.Metronome = false;
                            s.PanelLibrary = s.PanelLibrary && s.PanelLeftVisible;
                            s.PanelMeta = s.PanelMeta && s.PanelLeftVisible;
                            s.PanelHealth = s.PanelHealth && s.PanelRightVisible;
                            s.PanelInspector = s.PanelInspector && s.PanelRightVisible;
                            s.SettingsVersion = 4;
                            s.Save();
                        }
                        if (s.SettingsVersion < 5)
                        {
                            // v5：打击音提前量补偿输出延迟；默认跟随显示器刷新率（消除不均匀的帧节流）
                            s.HitLeadMs = 20;
                            s.MaxFps = 0;
                            s.SettingsVersion = 5;
                            s.Save();
                        }
                        return s;
                    }
                }
            }
            catch { }
            return new EditorSettings();
        }

        public void Save()
        {
            SettingsVersion = Math.Max(SettingsVersion, 5);
            try { File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented)); }
            catch { }
        }
    }
}