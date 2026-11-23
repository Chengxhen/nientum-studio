using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace NientumStudio.Audio
{
    public sealed class FmodEngine : IDisposable
    {
        private const string Dll = "fmodstudio";
        private const uint FmodHeaderVersion = 131857u;

        [Flags]
        private enum MODE : uint
        {
            DEFAULT = 0x00000000,
            LOOP_OFF = 0x00000001,
            LOOP_NORMAL = 0x00000002,
            CREATESTREAM = 0x00000080,
            CREATESAMPLE = 0x00000100,
            OPENMEMORY = 0x00000800,
            ACCURATETIME = 0x00004000
        }

        private enum TIMEUNIT : uint
        {
            MS = 0x00000001,
            PCM = 0x00000002,
            PCMBYTES = 0x00000004
        }

        private enum SOUND_FORMAT
        {
            NONE,
            PCM8,
            PCM16,
            PCM24,
            PCM32,
            PCMFLOAT
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CREATESOUNDEXINFO
        {
            public int cbsize;
            public uint length;
            public uint fileoffset;
            public int numchannels;
            public int defaultfrequency;
            public SOUND_FORMAT format;
            public uint decodebuffersize;
            public int initialsubsound;
            public int numsubsounds;
            public IntPtr inclusionlist;
            public int inclusionlistnum;
            public IntPtr pcmreadcallback;
            public IntPtr pcmsetposcallback;
            public IntPtr nonblockcallback;
            public IntPtr dlsname;
            public IntPtr encryptionkey;
            public int maxpolyphony;
            public IntPtr userdata;
            public int suggestedsoundtype;
            public IntPtr fileuseropen;
            public IntPtr fileuserclose;
            public IntPtr fileuserread;
            public IntPtr fileuserseek;
            public IntPtr fileuserasyncread;
            public IntPtr fileuserasynccancel;
            public IntPtr fileuserdata;
            public int filebuffersize;
            public int channelorder;
            public IntPtr initialsoundgroup;
            public uint initialseekposition;
            public TIMEUNIT initialseekpostype;
            public int ignoresetfilesystem;
            public uint audioqueuepolicy;
            public uint minmidigranularity;
            public int nonblockthreadid;
            public IntPtr fsbguid;
        }

        [DllImport(Dll)] private static extern int FMOD5_System_Create(out IntPtr system, uint headerversion);
        [DllImport(Dll)] private static extern int FMOD5_System_Release(IntPtr system);
        [DllImport(Dll)] private static extern int FMOD5_System_SetDSPBufferSize(IntPtr system, uint bufferlength, int numbuffers);
        [DllImport(Dll)] private static extern int FMOD5_System_Init(IntPtr system, int maxchannels, uint flags, IntPtr extradriverdata);
        [DllImport(Dll)] private static extern int FMOD5_System_Update(IntPtr system);
        [DllImport(Dll)] private static extern int FMOD5_System_GetMasterChannelGroup(IntPtr system, out IntPtr channelgroup);
        [DllImport(Dll)] private static extern int FMOD5_System_CreateChannelGroup(IntPtr system, byte[] name, out IntPtr channelgroup);
        [DllImport(Dll)] private static extern int FMOD5_System_CreateSound(IntPtr system, byte[] nameOrData, MODE mode, ref CREATESOUNDEXINFO exinfo, out IntPtr sound);
        [DllImport(Dll)] private static extern int FMOD5_System_CreateStream(IntPtr system, byte[] nameOrData, MODE mode, ref CREATESOUNDEXINFO exinfo, out IntPtr sound);
        [DllImport(Dll)] private static extern int FMOD5_System_PlaySound(IntPtr system, IntPtr sound, IntPtr channelgroup, bool paused, out IntPtr channel);

        [DllImport(Dll)] private static extern int FMOD5_ChannelGroup_Stop(IntPtr channelgroup);
        [DllImport(Dll)] private static extern int FMOD5_ChannelGroup_Release(IntPtr channelgroup);
        [DllImport(Dll)] private static extern int FMOD5_ChannelGroup_IsPlaying(IntPtr channelgroup, out bool isplaying);

        [DllImport(Dll)] private static extern int FMOD5_Sound_Release(IntPtr sound);
        [DllImport(Dll)] private static extern int FMOD5_Sound_GetMode(IntPtr sound, out MODE mode);
        [DllImport(Dll)] private static extern int FMOD5_Sound_GetLength(IntPtr sound, out uint length, TIMEUNIT lengthtype);
        [DllImport(Dll)] private static extern int FMOD5_Sound_GetFormat(IntPtr sound, out int type, out SOUND_FORMAT format, out int channels, out int bits);
        [DllImport(Dll)] private static extern int FMOD5_Sound_Lock(IntPtr sound, uint offset, uint length, out IntPtr ptr1, out IntPtr ptr2, out uint len1, out uint len2);
        [DllImport(Dll)] private static extern int FMOD5_Sound_Unlock(IntPtr sound, IntPtr ptr1, IntPtr ptr2, uint len1, uint len2);

        [DllImport(Dll)] private static extern int FMOD5_Channel_Stop(IntPtr channel);
        [DllImport(Dll)] private static extern int FMOD5_Channel_SetPaused(IntPtr channel, bool paused);
        [DllImport(Dll)] private static extern int FMOD5_Channel_SetPosition(IntPtr channel, uint position, TIMEUNIT postype);
        [DllImport(Dll)] private static extern int FMOD5_Channel_GetPosition(IntPtr channel, out uint position, TIMEUNIT postype);
        [DllImport(Dll)] private static extern int FMOD5_Channel_SetVolume(IntPtr channel, float volume);
        [DllImport(Dll)] private static extern int FMOD5_Channel_SetPitch(IntPtr channel, float pitch);
        [DllImport(Dll)] private static extern int FMOD5_Channel_IsPlaying(IntPtr channel, out bool isplaying);

        private IntPtr _system;
        private IntPtr _masterGroup;
        private IntPtr _sfxGroup;
        private IntPtr _songSound;
        private IntPtr _songChannel;

        private IntPtr _sfxHitNormal;
        private IntPtr _sfxHitDodge;
        private IntPtr _sfxMetroBar;
        private IntPtr _sfxMetroBeat;

        public bool IsInitialized { get; private set; }
        public bool HasSong => _songSound != IntPtr.Zero;
        public double DurationSec { get; private set; }
        public bool IsPlaying
        {
            get
            {
                if (_songChannel == IntPtr.Zero) return false;
                if (FMOD5_Channel_IsPlaying(_songChannel, out bool playing) != 0) return false;
                return playing && !_isPaused;
            }
        }

        private bool _isPaused = true;
        private float _hitsoundVolume = 1f;
        private float _metronomeVolume = 1f;
        private float _volume = 0.85f;
        private float _pitch = 1.0f;

        private bool _ownsSystem;
        private volatile bool _disposed;

        /// <summary>共用官方音轨的 Core System，避免一个进程里开两套音频设备。</summary>
        public FmodEngine(IntPtr sharedCoreSystem = default)
        {
            try
            {
                if (sharedCoreSystem != IntPtr.Zero)
                {
                    _system = sharedCoreSystem;
                    _ownsSystem = false;
                }
                else
                {
                    if (FMOD5_System_Create(out _system, FmodHeaderVersion) != 0) return;
                    FMOD5_System_SetDSPBufferSize(_system, 256, 4);
                    if (FMOD5_System_Init(_system, 64, 0, IntPtr.Zero) != 0) return;
                    _ownsSystem = true;
                }
                FMOD5_System_GetMasterChannelGroup(_system, out _masterGroup);
                FMOD5_System_CreateChannelGroup(_system, Encoding.UTF8.GetBytes("NientumSfx\0"), out _sfxGroup);
                IsInitialized = true;

                _sfxHitNormal = CreateSynthHitSound(1480f, 920f, 0.042, 0.36, true);
                _sfxHitDodge = CreateSynthHitSound(980f, 440f, 0.048, 0.34, true);
                _sfxMetroBar = CreateSynthBeepSound(1120f, 1120f, 0.028, 0.24);
                _sfxMetroBeat = CreateSynthBeepSound(820f, 820f, 0.024, 0.17);
            }
            catch
            {
                IsInitialized = false;
            }
        }

        public void Update()
        {
            if (IsInitialized && _ownsSystem && _system != IntPtr.Zero)
            {
                FMOD5_System_Update(_system);
            }
        }

        public void LoadSong(string filePath)
        {
            UnloadSong();
            if (!IsInitialized || string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            byte[] pathBytes = Encoding.UTF8.GetBytes(filePath + "\0");
            var ex = new CREATESOUNDEXINFO { cbsize = Marshal.SizeOf<CREATESOUNDEXINFO>() };
            if (FMOD5_System_CreateStream(_system, pathBytes, MODE.CREATESTREAM | MODE.ACCURATETIME, ref ex, out _songSound) == 0 && _songSound != IntPtr.Zero)
            {
                FMOD5_Sound_GetLength(_songSound, out uint lenMs, TIMEUNIT.MS);
                DurationSec = Math.Max(1.0, lenMs / 1000.0);
                FMOD5_System_PlaySound(_system, _songSound, _masterGroup, true, out _songChannel);
                FMOD5_Channel_SetVolume(_songChannel, _volume);
                FMOD5_Channel_SetPitch(_songChannel, _pitch);
                _isPaused = true;
            }
        }

        public Task<float[]> ExtractWaveformPeaksAsync(string filePath, int bucketsPerSecond = 120)
        {
            return Task.Run(() =>
            {
                IntPtr sys = _system;
                if (_disposed || !IsInitialized || sys == IntPtr.Zero || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return null;

                IntPtr tempSound = IntPtr.Zero;
                try
                {
                    byte[] pathBytes = Encoding.UTF8.GetBytes(filePath + "\0");
                    var ex = new CREATESOUNDEXINFO { cbsize = Marshal.SizeOf<CREATESOUNDEXINFO>() };
                    if (FMOD5_System_CreateSound(sys, pathBytes, MODE.CREATESAMPLE, ref ex, out tempSound) != 0 || tempSound == IntPtr.Zero)
                        return null;

                    FMOD5_Sound_GetFormat(tempSound, out _, out SOUND_FORMAT fmt, out int channels, out int bits);
                    FMOD5_Sound_GetLength(tempSound, out uint pcmBytes, TIMEUNIT.PCMBYTES);
                    FMOD5_Sound_GetLength(tempSound, out uint lenMs, TIMEUNIT.MS);

                    if (pcmBytes == 0 || channels <= 0 || lenMs == 0) return null;

                    int totalBuckets = Math.Clamp((int)(lenMs / 1000.0 * bucketsPerSecond), 600, 120000);
                    float[] peaks = new float[totalBuckets];

                    if (FMOD5_Sound_Lock(tempSound, 0, pcmBytes, out IntPtr p1, out IntPtr p2, out uint l1, out uint l2) == 0 && p1 != IntPtr.Zero && l1 > 0)
                    {
                        try
                        {
                            int bytesPerSample = Math.Max(1, bits / 8);
                            int frameSize = bytesPerSample * channels;
                            long totalFrames = l1 / frameSize;
                            if (totalFrames > 0)
                            {
                                byte[] raw = new byte[l1];
                                Marshal.Copy(p1, raw, 0, (int)l1);
                                for (int b = 0; b < totalBuckets; b++)
                                {
                                    long f0 = (long)b * totalFrames / totalBuckets;
                                    long f1 = Math.Max(f0 + 1, (long)(b + 1) * totalFrames / totalBuckets);
                                    long step = Math.Max(1, (f1 - f0) / 16);
                                    float max = 0f;
                                    for (long f = f0; f < f1 && f < totalFrames; f += step)
                                    {
                                        int idx = (int)(f * frameSize);
                                        float sample = 0f;
                                        if (fmt == SOUND_FORMAT.PCM16 && idx + 1 < raw.Length)
                                        {
                                            sample = Math.Abs(BitConverter.ToInt16(raw, idx) / 32768f);
                                        }
                                        else if (fmt == SOUND_FORMAT.PCMFLOAT && idx + 3 < raw.Length)
                                        {
                                            sample = Math.Abs(BitConverter.ToSingle(raw, idx));
                                        }
                                        else if (fmt == SOUND_FORMAT.PCM8 && idx < raw.Length)
                                        {
                                            sample = Math.Abs((raw[idx] - 128) / 128f);
                                        }
                                        if (sample > max) max = sample;
                                    }
                                    peaks[b] = Math.Min(1f, max);
                                }
                            }
                        }
                        finally
                        {
                            FMOD5_Sound_Unlock(tempSound, p1, p2, l1, l2);
                        }
                    }
                    return peaks;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    if (tempSound != IntPtr.Zero) FMOD5_Sound_Release(tempSound);
                }
            });
        }

        public void Play(double fromSec)
        {
            if (_songSound == IntPtr.Zero) return;
            // 暂停状态下复用原通道，避免每次恢复都新建通道造成泄漏/叠音
            bool finished = _songChannel == IntPtr.Zero;
            if (!finished && !_isPaused)
            {
                if (FMOD5_Channel_IsPlaying(_songChannel, out bool playing) != 0 || !playing)
                    finished = true;
            }
            if (finished)
            {
                if (_songChannel != IntPtr.Zero) FMOD5_Channel_Stop(_songChannel);
                FMOD5_System_PlaySound(_system, _songSound, _masterGroup, true, out _songChannel);
                FMOD5_Channel_SetVolume(_songChannel, _volume);
                FMOD5_Channel_SetPitch(_songChannel, _pitch);
            }
            uint ms = (uint)Math.Max(0, fromSec * 1000.0);
            FMOD5_Channel_SetPosition(_songChannel, ms, TIMEUNIT.MS);
            FMOD5_Channel_SetPaused(_songChannel, false);
            _isPaused = false;
        }

        public void Pause()
        {
            if (_songChannel != IntPtr.Zero)
            {
                FMOD5_Channel_SetPaused(_songChannel, true);
            }
            _isPaused = true;
            StopAllSfx();
        }

        public void Seek(double sec)
        {
            if (_songChannel == IntPtr.Zero) return;
            uint ms = (uint)Math.Max(0, sec * 1000.0);
            FMOD5_Channel_SetPosition(_songChannel, ms, TIMEUNIT.MS);
        }

        public double GetPositionSec()
        {
            if (_songChannel == IntPtr.Zero) return 0;
            if (FMOD5_Channel_GetPosition(_songChannel, out uint ms, TIMEUNIT.MS) != 0) return 0;
            return ms / 1000.0;
        }

        public void SetPitch(float pitch)
        {
            _pitch = Math.Clamp(pitch, 0.25f, 2.0f);
            if (_songChannel != IntPtr.Zero) FMOD5_Channel_SetPitch(_songChannel, _pitch);
        }

        public void SetVolume(float volume)
        {
            _volume = Math.Clamp(volume, 0f, 1f);
            if (_songChannel != IntPtr.Zero) FMOD5_Channel_SetVolume(_songChannel, _volume);
        }

        public void SetHitsoundVolume(float volume) => _hitsoundVolume = Math.Clamp(volume, 0f, 1f);
        public void SetMetronomeVolume(float volume) => _metronomeVolume = Math.Clamp(volume, 0f, 1f);

        public bool IsSfxPlaying
        {
            get
            {
                if (!IsInitialized || _sfxGroup == IntPtr.Zero) return false;
                return FMOD5_ChannelGroup_IsPlaying(_sfxGroup, out bool playing) == 0 && playing;
            }
        }

        public bool AreSfxSoundsOneShot()
        {
            foreach (var sfx in new[] { _sfxHitNormal, _sfxHitDodge, _sfxMetroBar, _sfxMetroBeat })
            {
                if (sfx == IntPtr.Zero) return false;
                if (FMOD5_Sound_GetMode(sfx, out MODE m) != 0) return false;
                if ((m & MODE.LOOP_NORMAL) != 0) return false;
            }
            return true;
        }

        public void StopAllSfx()
        {
            if (IsInitialized && _sfxGroup != IntPtr.Zero)
                FMOD5_ChannelGroup_Stop(_sfxGroup);
        }

        public void PlayHitsound(bool isDodge)
        {
            PlayOneShot(isDodge ? _sfxHitDodge : _sfxHitNormal, 0.65f * _hitsoundVolume);
        }

        public void PlayMetronome(bool isBar)
        {
            PlayOneShot(isBar ? _sfxMetroBar : _sfxMetroBeat, 0.55f * _metronomeVolume);
        }

        private void PlayOneShot(IntPtr sfxSound, float vol)
        {
            if (!IsInitialized || sfxSound == IntPtr.Zero || vol <= 0.001f) return;
            IntPtr targetGroup = _sfxGroup != IntPtr.Zero ? _sfxGroup : _masterGroup;
            if (FMOD5_System_PlaySound(_system, sfxSound, targetGroup, true, out IntPtr ch) == 0 && ch != IntPtr.Zero)
            {
                FMOD5_Channel_SetVolume(ch, vol);
                FMOD5_Channel_SetPaused(ch, false);
            }
        }

        private IntPtr CreateSynthHitSound(float f0, float f1, double durSec, double amp, bool addHarmonic)
        {
            const int sr = 44100;
            int samples = Math.Max(1, (int)(sr * durSec));
            int dataBytes = samples * 2;
            byte[] wav = new byte[44 + dataBytes];

            using (var ms = new MemoryStream(wav))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + dataBytes);
                bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                bw.Write(16);
                bw.Write((short)1);
                bw.Write((short)1);
                bw.Write(sr);
                bw.Write(sr * 2);
                bw.Write((short)2);
                bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(dataBytes);

                double p1 = 0, p2 = 0;
                int fadeInSamples = Math.Max(1, (int)(sr * 0.0008));
                int fadeOutSamples = Math.Max(1, (int)(sr * 0.004));
                for (int i = 0; i < samples; i++)
                {
                    double p = (double)i / samples;
                    double freq = f0 + (f1 - f0) * Math.Pow(p, 0.45);
                    p1 += 2.0 * Math.PI * freq / sr;
                    p2 += 2.0 * Math.PI * (freq * 1.98) / sr;

                    double fadeIn = i < fadeInSamples ? (double)i / fadeInSamples : 1.0;
                    int rem = samples - 1 - i;
                    double fadeOut = rem < fadeOutSamples ? (double)rem / fadeOutSamples : 1.0;
                    double env = Math.Exp(-p * 7.5) * fadeIn * fadeOut;
                    double body = Math.Sin(p1) + (addHarmonic ? 0.42 * Math.Sin(p2) * Math.Exp(-p * 10.0) : 0.0);
                    double s = Math.Clamp(body * env * amp, -1.0, 1.0);
                    bw.Write((short)(s * 32767.0));
                }
            }

            var ex = new CREATESOUNDEXINFO
            {
                cbsize = Marshal.SizeOf<CREATESOUNDEXINFO>(),
                length = (uint)wav.Length
            };
            FMOD5_System_CreateSound(_system, wav, MODE.OPENMEMORY | MODE.CREATESAMPLE | MODE.LOOP_OFF, ref ex, out IntPtr sound);
            return sound;
        }

        private IntPtr CreateSynthBeepSound(float f0, float f1, double durSec, double amp) =>
            CreateSynthHitSound(f0, f1, durSec, amp, false);

        public void UnloadSong()
        {
            StopAllSfx();
            if (_songChannel != IntPtr.Zero)
            {
                FMOD5_Channel_Stop(_songChannel);
                _songChannel = IntPtr.Zero;
            }
            if (_songSound != IntPtr.Zero)
            {
                FMOD5_Sound_Release(_songSound);
                _songSound = IntPtr.Zero;
            }
            _isPaused = true;
            DurationSec = 0;
        }

        public void Dispose()
        {
            _disposed = true;
            UnloadSong();
            if (_sfxHitNormal != IntPtr.Zero) FMOD5_Sound_Release(_sfxHitNormal);
            if (_sfxHitDodge != IntPtr.Zero) FMOD5_Sound_Release(_sfxHitDodge);
            if (_sfxMetroBar != IntPtr.Zero) FMOD5_Sound_Release(_sfxMetroBar);
            if (_sfxMetroBeat != IntPtr.Zero) FMOD5_Sound_Release(_sfxMetroBeat);
            if (_sfxGroup != IntPtr.Zero) FMOD5_ChannelGroup_Release(_sfxGroup);
            if (_system != IntPtr.Zero && _ownsSystem) FMOD5_System_Release(_system);
            _sfxGroup = IntPtr.Zero;
            _system = IntPtr.Zero;
            IsInitialized = false;
        }
    }
}
