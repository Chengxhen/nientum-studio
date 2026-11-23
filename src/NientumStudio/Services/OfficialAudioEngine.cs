using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;

namespace NientumStudio.Services
{
    /// <summary>
    /// 官方曲目音轨播放器：直接驱动游戏同款 fmodstudio.dll（Studio API），
    /// 按曲目加载对应 bank 并播放 event:/MUSIC_FREEPLAY/track.*。
    /// 与编辑器 FmodEngine（Core API）在同一进程共存（已验证）。
    /// </summary>
    public sealed class OfficialAudioEngine : IDisposable
    {
        private const uint FmodStudioVersion = 131857u;
        private const string Dll = "fmodstudio.dll";
        private const string EventPrefix = "event:/MUSIC_FREEPLAY/";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_System_Create(out IntPtr system, uint headerversion);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_System_Initialize(IntPtr system, int maxchannels, uint studioflags, uint flags, IntPtr extradriverdata);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_System_Release(IntPtr system);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_System_GetCoreSystem(IntPtr system, out IntPtr core);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_System_Update(IntPtr system);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD5_System_SetDSPBufferSize(IntPtr system, uint bufferlength, int numbuffers);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD5_System_GetDSPBufferSize(IntPtr system, out uint bufferlength, out int numbuffers);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD5_System_GetSoftwareFormat(IntPtr system, out int samplerate, out int speakermode, out int numrawspeakers);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_System_GetEvent(IntPtr system, byte[] path, out IntPtr eventdesc);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_System_LoadBankFile(IntPtr system, byte[] filename, uint flags, out IntPtr bank);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_Bank_Unload(IntPtr bank);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_Bank_GetEventList(IntPtr bank, IntPtr[] array, int capacity, out int count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_Bank_GetStringCount(IntPtr bank, out int count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_Bank_GetStringInfo(IntPtr bank, int index, out Guid id, byte[] path, int size, out int retrieved);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventDescription_GetID(IntPtr ev, out Guid id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventDescription_GetLength(IntPtr ev, out int length, int lengthtype);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventDescription_LoadSampleData(IntPtr ev);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventDescription_GetSampleLoadingState(IntPtr ev, out int state);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventDescription_CreateInstance(IntPtr ev, out IntPtr instance);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventInstance_SetTimelinePosition(IntPtr instance, int position);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventInstance_GetTimelinePosition(IntPtr instance, out int position);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventInstance_SetPaused(IntPtr instance, bool paused);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventInstance_Start(IntPtr instance);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventInstance_Stop(IntPtr instance, int mode);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventInstance_GetPlaybackState(IntPtr instance, out int state);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventInstance_Release(IntPtr instance);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventInstance_SetPitch(IntPtr instance, float pitch);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int FMOD_Studio_EventInstance_SetVolume(IntPtr instance, float volume);

        private IntPtr _system = IntPtr.Zero;
        private IntPtr _stringsBank = IntPtr.Zero;
        private IntPtr _trackBank = IntPtr.Zero;
        private IntPtr _eventDesc = IntPtr.Zero;
        private IntPtr _instance = IntPtr.Zero;
        private Dictionary<string, (string bankFile, string eventPath)> _map;
        private string _loadedKey;
        private bool _paused;
        private bool _started;
        private bool _stoppedAtZero;
        private float _volume = 1f;

        /// <summary>打击音种类：严格对应游戏 Assembly-CSharp 实际调用的 FMOD SFX 事件。</summary>
        public enum GameHitSound
        {
            Normal,
            Ground,
            Jump,
            SuperJump,
            LongStart,
            Long,
            LongEnd,
            Coin,
            MultiHit,
            MultiHitPitch,
            MultiHitStrike,
            Metronome
        }

        private const string SfxBankFile = "MMuR8p5Sy_CJpL6GDMJThNgEqEq.bank";
        private const int MaxHitInstancesPerKind = 16;
        private readonly Dictionary<GameHitSound, IntPtr> _hitDescs = new Dictionary<GameHitSound, IntPtr>();
        private readonly Dictionary<GameHitSound, List<IntPtr>> _hitInsts = new Dictionary<GameHitSound, List<IntPtr>>();
        private IntPtr _sfxBank = IntPtr.Zero;
        private bool _sfxLoadTried;
        private bool _sfxReady;

        public bool IsAvailable { get; private set; }
        /// <summary>供 FmodEngine 共用的 Core System 句柄（0 表示不可用）。</summary>
        public IntPtr CoreSystem { get; private set; }
        public bool HasTrack => _eventDesc != IntPtr.Zero && _instance != IntPtr.Zero;
        public string LoadedKey => _loadedKey;
        public double LengthSec { get; private set; }

        public bool IsPlaying
        {
            get
            {
                if (_instance == IntPtr.Zero || _paused) return false;
                if (FMOD_Studio_EventInstance_GetPlaybackState(_instance, out int state) != 0) return false;
                return state == 0; // FMOD_STUDIO_PLAYBACK_PLAYING
            }
        }

        public bool IsPaused => _paused;

        public OfficialAudioEngine()
        {
            try
            {
                int r = FMOD_Studio_System_Create(out _system, FmodStudioVersion);
                if (r != 0 || _system == IntPtr.Zero)
                {
                    _system = IntPtr.Zero;
                    return;
                }
                // 独立异步音频线程 + 1024x4 DSP 缓冲（~21.3ms/块），彻底消除 192kHz FLAC / 高码率音频受 UI 帧率拖累引起的卡顿与跳帧
                if (FMOD_Studio_System_GetCoreSystem(_system, out IntPtr coreEarly) == 0 && coreEarly != IntPtr.Zero)
                    FMOD5_System_SetDSPBufferSize(coreEarly, 1024, 4);
                r = FMOD_Studio_System_Initialize(_system, 64, 0 /* 异步音频线程 */, 0, IntPtr.Zero);
                if (r != 0)
                {
                    FMOD_Studio_System_Release(_system);
                    _system = IntPtr.Zero;
                    return;
                }
                if (FMOD_Studio_System_GetCoreSystem(_system, out IntPtr core) == 0)
                    CoreSystem = core;
                LoadCoreAndSfxBanks();
            }
            catch (Exception ex)
            {
                AppLog.Error("OfficialAudioEngine init failed", ex);
                try { if (_system != IntPtr.Zero) FMOD_Studio_System_Release(_system); } catch { }
                _system = IntPtr.Zero;
                CoreSystem = IntPtr.Zero;
                IsAvailable = false;
            }
        }

        public bool IsHitSfxReady => _sfxReady;

        public void ReloadBanksIfNeeded()
        {
            if (_system == IntPtr.Zero) return;
            if (IsAvailable && _sfxReady) return;
            LoadCoreAndSfxBanks();
        }

        private void LoadCoreAndSfxBanks()
        {
            if (_stringsBank == IntPtr.Zero)
            {
                string masterPath = ResolveBankPath("Master.bank");
                string stringsPath = ResolveBankPath("Master.strings.bank");
                if (!string.IsNullOrEmpty(masterPath)) LoadBankFile(masterPath);
                if (!string.IsNullOrEmpty(stringsPath)) _stringsBank = LoadBankFile(stringsPath);
                IsAvailable = _stringsBank != IntPtr.Zero;
                AppLog.Info($"OfficialAudioEngine banks: Master={(masterPath ?? "missing")}, Strings={(stringsPath ?? "missing")}, IsAvailable={IsAvailable}");
            }
            if (IsAvailable && !_sfxReady)
            {
                _sfxLoadTried = false;
                EnsureHitSfx();
                AppLog.Info($"OfficialAudioEngine HitSfx ready={_sfxReady} (count={_hitDescs.Count})");
            }
        }

        private static string ResolveBankPath(string fileName)
        {
            string local = Path.Combine(OfficialLibrary.BanksDir, fileName);
            if (File.Exists(local)) return local;

            string gameDir = EditorSettings.Current?.GamePath;
            if (!string.IsNullOrWhiteSpace(gameDir) && Directory.Exists(gameDir))
            {
                string sa = Path.Combine(gameDir, "Nientum_Data", "StreamingAssets");
                if (Directory.Exists(sa))
                {
                    string[] candidates =
                    {
                        Path.Combine(sa, fileName),
                        Path.Combine(sa, "Desktop", fileName),
                        Path.Combine(sa, "FMODBanks", fileName),
                        Path.Combine(sa, "Audio", fileName),
                    };
                    foreach (string c in candidates)
                        if (File.Exists(c)) return c;

                    try
                    {
                        foreach (string sub in Directory.GetDirectories(sa))
                        {
                            string p = Path.Combine(sub, fileName);
                            if (File.Exists(p)) return p;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        public (uint bufferLength, int buffers, int sampleRate, int setResult) GetDspInfo()
        {
            uint len = 0;
            int n = 0;
            int rate = 0;
            int setResult = -1;
            if (CoreSystem != IntPtr.Zero)
            {
                FMOD5_System_GetDSPBufferSize(CoreSystem, out len, out n);
                FMOD5_System_GetSoftwareFormat(CoreSystem, out rate, out _, out _);
                setResult = 0;
            }
            return (len, n, rate, setResult);
        }

        public void Update()
        {
            if (_system == IntPtr.Zero) return;
            FMOD_Studio_System_Update(_system);
            TickWarmup();
        }

        public bool LoadTrack(string key)
        {
            if (!IsAvailable || string.IsNullOrEmpty(key)) return false;
            if (_loadedKey == key && HasTrack) return true;

            UnloadTrack();
            EnsureMap();
            if (_map == null || !_map.TryGetValue(key, out var entry)) return false;
            string bankPath = Path.Combine(OfficialLibrary.BanksDir, entry.bankFile);
            if (!File.Exists(bankPath)) return false;

            _trackBank = LoadBankFile(bankPath);
            if (_trackBank == IntPtr.Zero) return false;
            if (FMOD_Studio_System_GetEvent(_system, Encoding.UTF8.GetBytes(entry.eventPath + "\0"), out _eventDesc) != 0)
                return false;
            if (FMOD_Studio_EventDescription_CreateInstance(_eventDesc, out _instance) != 0)
                return false;

            LengthSec = 0;
            if (FMOD_Studio_EventDescription_GetLength(_eventDesc, out int ms, 0) == 0 && ms > 0)
                LengthSec = ms / 1000.0;
            FMOD_Studio_EventInstance_SetVolume(_instance, _volume);
            _loadedKey = key;
            _paused = false;
            _started = false;
            _stoppedAtZero = false;
            return true;
        }

        public void UnloadTrack()
        {
            if (_instance != IntPtr.Zero)
            {
                FMOD_Studio_EventInstance_Stop(_instance, 0);
                FMOD_Studio_EventInstance_Release(_instance);
                _instance = IntPtr.Zero;
            }
            _eventDesc = IntPtr.Zero;
            if (_trackBank != IntPtr.Zero)
            {
                FMOD_Studio_Bank_Unload(_trackBank);
                _trackBank = IntPtr.Zero;
            }
            _loadedKey = null;
            LengthSec = 0;
            _paused = false;
            _started = false;
        }

        public void Play(double sec)
        {
            if (_instance == IntPtr.Zero) return;
            // 首次播放或自然播完(STOPPED=2)后需重新 Start；暂停恢复不再重复 Start 避免从头重启
            bool needStart = !_started;
            if (!needStart && FMOD_Studio_EventInstance_GetPlaybackState(_instance, out int state) == 0 && state == 2)
                needStart = true;

            FMOD_Studio_EventInstance_SetTimelinePosition(_instance, (int)Math.Max(0, sec * 1000));
            if (needStart)
            {
                _started = true;
                _stoppedAtZero = false;
                FMOD_Studio_EventInstance_Start(_instance);
            }
            FMOD_Studio_EventInstance_SetPaused(_instance, false);
            _paused = false;
        }

        /// <summary>完全停止并回到开头（停止按钮用）。</summary>
        public void Stop()
        {
            if (_instance == IntPtr.Zero) return;
            FMOD_Studio_EventInstance_Stop(_instance, 0);
            FMOD_Studio_EventInstance_SetTimelinePosition(_instance, 0);
            _started = false;
            _paused = false;
            _stoppedAtZero = true;
        }

        public void Pause()
        {
            if (_instance == IntPtr.Zero) return;
            FMOD_Studio_EventInstance_SetPaused(_instance, true);
            _paused = true;
        }

        public void Seek(double sec)
        {
            if (_instance == IntPtr.Zero) return;
            FMOD_Studio_EventInstance_SetTimelinePosition(_instance, (int)Math.Max(0, sec * 1000));
        }

        public double GetPositionSec()
        {
            if (_instance == IntPtr.Zero || _stoppedAtZero) return 0;
            return FMOD_Studio_EventInstance_GetTimelinePosition(_instance, out int ms) == 0 ? ms / 1000.0 : 0;
        }

        public void SetPitch(float pitch)
        {
            if (_instance != IntPtr.Zero)
                FMOD_Studio_EventInstance_SetPitch(_instance, pitch);
        }

        public void SetVolume(float volume)
        {
            _volume = Math.Clamp(volume, 0f, 1f);
            if (_instance != IntPtr.Zero)
                FMOD_Studio_EventInstance_SetVolume(_instance, _volume);
        }

        private IntPtr LoadBankFile(string path)
        {
            if (_system == IntPtr.Zero || !File.Exists(path)) return IntPtr.Zero;
            return FMOD_Studio_System_LoadBankFile(_system, Encoding.UTF8.GetBytes(path + "\0"), 0, out IntPtr bank) == 0
                ? bank : IntPtr.Zero;
        }

        private string MapCachePath => Path.Combine(ChartStorage.WorkspaceRoot, "unpacked", "official_audio_map.json");

        private void EnsureMap()
        {
            if (_map != null) return;
            var map = new Dictionary<string, (string, string)>();

            if (File.Exists(MapCachePath))
            {
                try
                {
                    var doc = JObject.Parse(File.ReadAllText(MapCachePath));
                    foreach (var prop in doc.Properties())
                    {
                        string bank = prop.Value?.Value<string>("bank");
                        string ev = prop.Value?.Value<string>("event");
                        if (!string.IsNullOrEmpty(bank) && !string.IsNullOrEmpty(ev)
                            && File.Exists(Path.Combine(OfficialLibrary.BanksDir, bank)))
                            map[prop.Name] = (bank, ev);
                    }
                    if (map.Count > 0)
                    {
                        _map = map;
                        return;
                    }
                }
                catch { }
            }

            // 扫描 bank 建立 track key -> (bank, event) 映射
            var guidToPath = ReadStringTable(_stringsBank);
            var bankSizes = new Dictionary<string, long>();
            foreach (string file in Directory.GetFiles(OfficialLibrary.BanksDir, "*.bank"))
            {
                string name = Path.GetFileName(file);
                if (name == "Master.bank" || name == "Master.strings.bank") continue;
                IntPtr bank = LoadBankFile(file);
                if (bank == IntPtr.Zero) continue;
                try
                {
                    var probe = new IntPtr[1024];
                    if (FMOD_Studio_Bank_GetEventList(bank, probe, probe.Length, out int count) != 0) continue;
                    for (int i = 0; i < count; i++)
                    {
                        if (FMOD_Studio_EventDescription_GetID(probe[i], out Guid gid) != 0) continue;
                        if (!guidToPath.TryGetValue(gid, out string path)) continue;
                        if (!path.StartsWith(EventPrefix, StringComparison.Ordinal)) continue;
                        string key = path.Substring(EventPrefix.Length);
                        if (string.IsNullOrEmpty(key)) continue;
                        long size = new FileInfo(file).Length;
                        if (!map.TryGetValue(key, out var existing) || size < bankSizes[existing.Item1])
                        {
                            map[key] = (name, path);
                            bankSizes[name] = size;
                        }
                    }
                }
                finally
                {
                    FMOD_Studio_Bank_Unload(bank);
                }
            }

            _map = map;
            try
            {
                var doc = new JObject();
                foreach (var kv in map)
                    doc[kv.Key] = new JObject { ["bank"] = kv.Value.Item1, ["event"] = kv.Value.Item2 };
                File.WriteAllText(MapCachePath, doc.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch { }
        }

        private static Dictionary<Guid, string> ReadStringTable(IntPtr stringsBank)
        {
            var table = new Dictionary<Guid, string>();
            if (stringsBank == IntPtr.Zero) return table;
            if (FMOD_Studio_Bank_GetStringCount(stringsBank, out int n) != 0 || n <= 0) return table;
            for (int i = 0; i < n; i++)
            {
                var buf = new byte[512];
                if (FMOD_Studio_Bank_GetStringInfo(stringsBank, i, out Guid id, buf, buf.Length, out int len) == 0)
                    table[id] = Encoding.UTF8.GetString(buf, 0, Math.Max(0, len - 1));
            }
            return table;
        }

        /// <summary>播放游戏原始命中音效（音符与判定线重合瞬间调用）。返回 false 表示音效不可用。</summary>
        public bool PlayGameHit(GameHitSound kind, float volume) => PlayGameHit(kind, volume, 1f);

        /// <summary>播放游戏原始命中音效（支持自定义音高倍率）。返回 false 表示音效不可用。</summary>
        public bool PlayGameHit(GameHitSound kind, float volume, float pitch)
        {
            if (volume <= 0.001f) return false;
            if (!EnsureHitSfx()) return false;
            if (!_hitDescs.TryGetValue(kind, out IntPtr desc) || desc == IntPtr.Zero) return false;
            if (!_hitInsts.TryGetValue(kind, out var list))
            {
                list = new List<IntPtr>();
                _hitInsts[kind] = list;
            }
            IntPtr inst = IntPtr.Zero;
            foreach (var cand in list)
            {
                if (FMOD_Studio_EventInstance_GetPlaybackState(cand, out int st) == 0 && st == 2 /*STOPPED*/)
                {
                    inst = cand;
                    break;
                }
            }
            if (inst == IntPtr.Zero)
            {
                if (FMOD_Studio_EventDescription_CreateInstance(desc, out inst) != 0 || inst == IntPtr.Zero)
                    return false;
                list.Add(inst);
                if (list.Count > MaxHitInstancesPerKind)
                {
                    var oldest = list[0];
                    list.RemoveAt(0);
                    FMOD_Studio_EventInstance_Stop(oldest, 1);
                    FMOD_Studio_EventInstance_Release(oldest);
                }
            }
            FMOD_Studio_EventInstance_SetVolume(inst, Math.Clamp(volume, 0f, 1.5f));
            FMOD_Studio_EventInstance_SetPitch(inst, Math.Clamp(pitch, 0.25f, 6.0f));
            FMOD_Studio_EventInstance_Start(inst);
            return true;
        }

        private void WarmupHitSfx()
        {
            // 先请求加载每条音效的采样数据（MMuR bank 达 79MB，不显式 LoadSampleData 首次触发会静音或掉音）
            foreach (var desc in _hitDescs.Values)
            {
                FMOD_Studio_EventDescription_LoadSampleData(desc);
            }
            for (int step = 0; step < 12; step++)
            {
                FMOD_Studio_System_Update(_system);
                bool allLoaded = true;
                foreach (var desc in _hitDescs.Values)
                {
                    if (FMOD_Studio_EventDescription_GetSampleLoadingState(desc, out int st) == 0 && st != 3 /*LOADED*/)
                    {
                        allLoaded = false;
                        break;
                    }
                }
                if (allLoaded) break;
                System.Threading.Thread.Sleep(4);
            }
            foreach (var desc in _hitDescs.Values)
            {
                if (FMOD_Studio_EventDescription_CreateInstance(desc, out IntPtr inst) != 0 || inst == IntPtr.Zero) continue;
                FMOD_Studio_EventInstance_SetVolume(inst, 0f);
                FMOD_Studio_EventInstance_Start(inst);
                _warmupInsts.Add(inst);
            }
            // 静音播放一小段，触发 DSP 解码；下一帧由 Update 推进（不可在 UI 线程长时间等待）
            _warmupTicks = 2;
        }

        private readonly List<IntPtr> _warmupInsts = new List<IntPtr>();
        private int _warmupTicks;

        private void TickWarmup()
        {
            if (_warmupTicks <= 0) return;
            _warmupTicks--;
            if (_warmupTicks > 0) return;
            foreach (var inst in _warmupInsts)
            {
                FMOD_Studio_EventInstance_Stop(inst, 1);
                FMOD_Studio_EventInstance_Release(inst);
            }
            _warmupInsts.Clear();
        }

        public void StopGameHits()
        {
            foreach (var list in _hitInsts.Values)
                foreach (var inst in list)
                    FMOD_Studio_EventInstance_Stop(inst, 1);
        }

        private bool EnsureHitSfx()
        {
            if (_sfxLoadTried) return _sfxReady;
            _sfxLoadTried = true;
            if (_system == IntPtr.Zero) return false;
            try
            {
                string path = ResolveBankPath(SfxBankFile);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    AppLog.Warn($"HitSfx bank missing: {SfxBankFile}");
                    return false;
                }
                _sfxBank = LoadBankFile(path);
                if (_sfxBank == IntPtr.Zero) return false;

                void Bind(GameHitSound kind, string eventPath)
                {
                    if (FMOD_Studio_System_GetEvent(_system, Encoding.UTF8.GetBytes(eventPath + "\0"), out IntPtr desc) == 0 && desc != IntPtr.Zero)
                        _hitDescs[kind] = desc;
                }
                // 与游戏 Assembly-CSharp (IngameScene / CharacterState* / MapBlockJudge* / MapBlockCollisionCoin) 完全对齐
                Bind(GameHitSound.Normal, "event:/SFX/SfxNormal");
                Bind(GameHitSound.Ground, "event:/SFX/SfxGround");
                Bind(GameHitSound.Jump, "event:/SFX/SfxJump");
                Bind(GameHitSound.SuperJump, "event:/SFX/SfxSuperJump");
                Bind(GameHitSound.LongStart, "event:/SFX/SfxLongStart");
                Bind(GameHitSound.Long, "event:/SFX/SfxLongMiddle");
                Bind(GameHitSound.LongEnd, "event:/SFX/SfxLongEnd");
                Bind(GameHitSound.Coin, "event:/SFX/SfxCoinMiddle");
                Bind(GameHitSound.MultiHit, "event:/SFX/SfxMultihitHitBase");
                Bind(GameHitSound.MultiHitPitch, "event:/SFX/SfxMultihitHitPitch");
                Bind(GameHitSound.MultiHitStrike, "event:/SFX/SfxMultihitStrike");
                Bind(GameHitSound.Metronome, "event:/SFX/PZSfxMetronomeSingle");
                _sfxReady = _hitDescs.Count > 0;
                if (_sfxReady) WarmupHitSfx();
            }
            catch
            {
                _sfxReady = false;
            }
            return _sfxReady;
        }

        private void ReleaseHitSfx()
        {
            foreach (var inst in _warmupInsts)
            {
                FMOD_Studio_EventInstance_Stop(inst, 1);
                FMOD_Studio_EventInstance_Release(inst);
            }
            _warmupInsts.Clear();
            foreach (var list in _hitInsts.Values)
            {
                foreach (var inst in list)
                {
                    FMOD_Studio_EventInstance_Stop(inst, 1);
                    FMOD_Studio_EventInstance_Release(inst);
                }
            }
            _hitInsts.Clear();
            _hitDescs.Clear();
            if (_sfxBank != IntPtr.Zero)
            {
                FMOD_Studio_Bank_Unload(_sfxBank);
                _sfxBank = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            ReleaseHitSfx();
            UnloadTrack();
            if (_stringsBank != IntPtr.Zero)
            {
                FMOD_Studio_Bank_Unload(_stringsBank);
                _stringsBank = IntPtr.Zero;
            }
            if (_system != IntPtr.Zero)
            {
                FMOD_Studio_System_Release(_system);
                _system = IntPtr.Zero;
            }
        }
    }
}