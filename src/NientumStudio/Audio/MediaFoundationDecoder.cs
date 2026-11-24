using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NientumStudio.Audio
{
    /// <summary>
    /// 基于 Windows 原生 Media Foundation (mfplat/mfreadwrite) 的全格式离线音频解码器。
    /// 将 MP3 / FLAC / WAV / OGG / M4A / AAC 统一安全解码为标准 48000 Hz · 16-bit · 立体声 WAV 内存数据，
    /// 彻底规避 FMOD 流解码器在高码率/非标准音频（如 192kHz 24-bit FLAC）下卡顿、跳帧与倍速异常的问题。
    /// </summary>
    public static class MediaFoundationDecoder
    {
        private const uint MF_VERSION = 0x00020070;
        private const uint MF_STARTUP_NOSOCKET = 0x1;
        private const int MF_SOURCE_READER_FIRST_AUDIO_STREAM = unchecked((int)0xFFFFFFFD);
        private const int MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;

        private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");
        private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(uint version, uint dwFlags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMediaType(out IMFMediaType ppMFType);

        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int MFCreateSourceReaderFromURL(string pwszURL, IntPtr pAttributes, out IMFSourceReader ppSourceReader);

        /// <summary>
        /// 解码音频文件为标准 48000 Hz · 16-bit · Stereo WAV 字节数组。失败返回 null。
        /// </summary>
        public static byte[] DecodeToWav48k16BitStereo(string filePath, out double durationSec)
        {
            durationSec = 0;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

            bool started = false;
            IMFSourceReader reader = null;
            IMFMediaType outType = null;
            try
            {
                if (MFStartup(MF_VERSION, MF_STARTUP_NOSOCKET) != 0) return null;
                started = true;

                if (MFCreateSourceReaderFromURL(filePath, IntPtr.Zero, out reader) != 0 || reader == null)
                    return null;

                if (MFCreateMediaType(out outType) != 0 || outType == null)
                    return null;

                outType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                outType.SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
                outType.SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
                outType.SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000);
                outType.SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                outType.SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
                outType.SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 192000);

                if (reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, outType) != 0)
                    return null;

                using var ms = new MemoryStream(16 * 1024 * 1024);
                // 预留 44 字节 RIFF WAV 头
                ms.Write(new byte[44], 0, 44);

                byte[] tmp = Array.Empty<byte>();
                while (true)
                {
                    int hr = reader.ReadSample(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, out _, out int flags, out _, out IMFSample sample);
                    if (hr != 0) break;
                    if (sample != null)
                    {
                        try
                        {
                            if (sample.ConvertToContiguousBuffer(out IMFMediaBuffer buf) == 0 && buf != null)
                            {
                                try
                                {
                                    if (buf.Lock(out IntPtr ptr, out _, out int curLen) == 0 && ptr != IntPtr.Zero && curLen > 0)
                                    {
                                        try
                                        {
                                            if (tmp.Length < curLen) tmp = new byte[curLen];
                                            Marshal.Copy(ptr, tmp, 0, curLen);
                                            ms.Write(tmp, 0, curLen);
                                        }
                                        finally
                                        {
                                            buf.Unlock();
                                        }
                                    }
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(buf);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(sample);
                        }
                    }
                    if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
                }

                long dataLen = ms.Length - 44;
                if (dataLen <= 0) return null;

                byte[] wav = ms.ToArray();
                WriteWavHeader(wav, (int)dataLen, 48000, 2, 16);
                NormalizeWavLoudnessIfQuiet(wav);
                durationSec = dataLen / 192000.0; // 48000 * 2ch * 2bytes
                return wav;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (outType != null) try { Marshal.ReleaseComObject(outType); } catch { }
                if (reader != null) try { Marshal.ReleaseComObject(reader); } catch { }
                if (started) try { MFShutdown(); } catch { }
            }
        }

        /// <summary>
        /// 直接从标准 48kHz 16-bit Stereo WAV 字节流中极速提取波形峰值（<20ms 完成）。
        /// </summary>
        public static float[] ExtractPeaksFromWav48k16BitStereo(byte[] wav, int bucketsPerSecond = 120)
        {
            if (wav == null || wav.Length <= 48) return null;
            int dataBytes = wav.Length - 44;
            int totalFrames = dataBytes / 4;
            if (totalFrames <= 0) return null;

            double durSec = totalFrames / 48000.0;
            int totalBuckets = Math.Clamp((int)(durSec * bucketsPerSecond), 600, 120000);
            float[] peaks = new float[totalBuckets];

            for (int b = 0; b < totalBuckets; b++)
            {
                int f0 = (int)((long)b * totalFrames / totalBuckets);
                int f1 = Math.Max(f0 + 1, (int)((long)(b + 1) * totalFrames / totalBuckets));
                int step = Math.Max(1, (f1 - f0) / 16);
                float max = 0f;
                for (int f = f0; f < f1 && f < totalFrames; f += step)
                {
                    int idx = 44 + f * 4;
                    float l = Math.Abs(BitConverter.ToInt16(wav, idx) / 32768f);
                    float r = Math.Abs(BitConverter.ToInt16(wav, idx + 2) / 32768f);
                    float m = l > r ? l : r;
                    if (m > max) max = m;
                }
                peaks[b] = Math.Min(1f, max);
            }
            return peaks;
        }

        private static void NormalizeWavLoudnessIfQuiet(byte[] wavBytes)
        {
            if (wavBytes == null || wavBytes.Length <= 48) return;
            try
            {
                const int dataStart = 44;
                int totalSamples = (wavBytes.Length - dataStart) / 2;
                if (totalSamples < 1000) return;

                double sumSq = 0.0;
                int count = 0;
                const int stride = 8;
                for (int i = 0; i < totalSamples; i += stride)
                {
                    int idx = dataStart + i * 2;
                    short s = (short)(wavBytes[idx] | (wavBytes[idx + 1] << 8));
                    double norm = s / 32768.0;
                    sumSq += norm * norm;
                    count++;
                }
                if (count == 0) return;
                float rms = (float)Math.Sqrt(sumSq / count);
                if (rms <= 0.002f || rms >= 0.20f) return;

                float gain = Math.Clamp(0.24f / rms, 1f, 3.5f);
                if (gain <= 1.05f) return;

                for (int i = 0; i < totalSamples; i++)
                {
                    int idx = dataStart + i * 2;
                    short s = (short)(wavBytes[idx] | (wavBytes[idx + 1] << 8));
                    float v = (s / 32768f) * gain;
                    if (v > 0.85f) v = 0.85f + 0.12f * MathF.Tanh((v - 0.85f) / 0.12f);
                    else if (v < -0.85f) v = -0.85f - 0.12f * MathF.Tanh((-v - 0.85f) / 0.12f);

                    int outSample = Math.Clamp((int)Math.Round(v * 32767f), short.MinValue, short.MaxValue);
                    wavBytes[idx] = (byte)(outSample & 0xFF);
                    wavBytes[idx + 1] = (byte)((outSample >> 8) & 0xFF);
                }
            }
            catch { }
        }

        private static void WriteWavHeader(byte[] wav, int dataLen, int sampleRate, short channels, short bitsPerSample)
        {
            using var ms = new MemoryStream(wav, writable: true);
            using var bw = new BinaryWriter(ms);
            short blockAlign = (short)(channels * (bitsPerSample / 8));
            int byteRate = sampleRate * blockAlign;
            bw.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            bw.Write(36 + dataLen);
            bw.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write(channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write(bitsPerSample);
            bw.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            bw.Write(dataLen);
        }

        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(int dwStreamIndex, out bool pfSelected);
            [PreserveSig] int SetStreamSelection(int dwStreamIndex, bool fSelected);
            [PreserveSig] int GetNativeMediaType(int dwStreamIndex, int dwMediaTypeIndex, out IMFMediaType ppMediaType);
            [PreserveSig] int GetCurrentMediaType(int dwStreamIndex, out IMFMediaType ppMediaType);
            [PreserveSig] int SetCurrentMediaType(int dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
            [PreserveSig] int SetCurrentPosition(ref Guid guidTimeFormat, ref long varPosition);
            [PreserveSig] int ReadSample(int dwStreamIndex, int dwControlFlags, out int pdwActualStreamIndex, out int pdwStreamFlags, out long pllTimestamp, out IMFSample ppSample);
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            // IMFAttributes (30 methods)
            [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
            [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
            [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr Value, out bool pbResult);
            [PreserveSig] int Compare(IntPtr pTheirs, int MatchType, out bool pbResult);
            [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
            [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
            [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
            [PreserveSig] int GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
            [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
            [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
            [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
            [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(ref Guid guidKey, IntPtr Value);
            [PreserveSig] int DeleteItem(ref Guid guidKey, IntPtr reserved);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64([In] ref Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble([In] ref Guid guidKey, double fValue);
            [PreserveSig] int SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
            [PreserveSig] int SetString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob([In] ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
            [PreserveSig] int SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint pcItems);
            [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
            [PreserveSig] int CopyAllItems(IntPtr pDest);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            // IMFAttributes (30 methods)
            [PreserveSig] int GetItem(ref Guid guidKey, IntPtr pValue);
            [PreserveSig] int GetItemType(ref Guid guidKey, out int pType);
            [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr Value, out bool pbResult);
            [PreserveSig] int Compare(IntPtr pTheirs, int MatchType, out bool pbResult);
            [PreserveSig] int GetUINT32(ref Guid guidKey, out uint punValue);
            [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong punValue);
            [PreserveSig] int GetDouble(ref Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID(ref Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength(ref Guid guidKey, out uint pcchLength);
            [PreserveSig] int GetString(ref Guid guidKey, IntPtr pwszValue, uint cchBufSize, out uint pcchLength);
            [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
            [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
            [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
            [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(ref Guid guidKey, IntPtr Value);
            [PreserveSig] int DeleteItem(ref Guid guidKey, IntPtr reserved);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64([In] ref Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble([In] ref Guid guidKey, double fValue);
            [PreserveSig] int SetGUID([In] ref Guid guidKey, [In] ref Guid guidValue);
            [PreserveSig] int SetString([In] ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob([In] ref Guid guidKey, IntPtr pBuf, uint cbBufSize);
            [PreserveSig] int SetUnknown([In] ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint pcItems);
            [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
            [PreserveSig] int CopyAllItems(IntPtr pDest);

            // IMFSample methods
            [PreserveSig] int GetSampleFlags(out int pdwSampleFlags);
            [PreserveSig] int SetSampleFlags(int dwSampleFlags);
            [PreserveSig] int GetSampleTime(out long phnsSampleTime);
            [PreserveSig] int SetSampleTime(long hnsSampleTime);
            [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
            [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
            [PreserveSig] int GetBufferCount(out int pdwBufferCount);
            [PreserveSig] int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
            [PreserveSig] int RemoveBufferByIndex(int dwIndex);
            [PreserveSig] int RemoveAllBuffers();
            [PreserveSig] int GetTotalLength(out int pcbTotalLength);
            [PreserveSig] int CopyToBuffer(IMFMediaBuffer pBuffer);
        }

        [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
            [PreserveSig] int SetCurrentLength(int cbCurrentLength);
            [PreserveSig] int GetMaxLength(out int pcbMaxLength);
        }
    }
}
