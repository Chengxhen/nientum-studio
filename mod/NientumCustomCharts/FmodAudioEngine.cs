using System;
using FMOD;
using FMODUnity;
using QGCore;
using UnityEngine;

namespace NientumCustomCharts
{
    public static class FmodAudioEngine
    {
        private static Sound _previewSound;
        private static Channel _previewChannel;
        private static bool _hasPreview;
        private static bool _previewDucked;

        private static Sound _ingameSound;
        private static Channel _ingameChannel;
        private static bool _hasIngame;
        private static float _ingameFadeMultiplier = 1f;

        public static bool HasIngameBgm => _hasIngame && _ingameChannel.hasHandle();

        public static float GetTargetMusicVolume()
        {
            try
            {
                var cfg = UserConfigManager.Instance?.GetConfig();
                if (cfg == null) return 0.8f;
                float master = cfg.MasterVolume <= 0.001f ? 0f : SoundManagerBase.GetVCAVolumeFromSliderValue(cfg.MasterVolume);
                float bgm = cfg.BgmSoundVolume <= 0.001f ? 0f : SoundManagerBase.GetVCAVolumeFromSliderValue(cfg.BgmSoundVolume);
                return Mathf.Clamp01(master * bgm);
            }
            catch
            {
                return 0.8f;
            }
        }

        public static void StartPreview(string audioPath, float startSec)
        {
            StopPreview();
            if (string.IsNullOrEmpty(audioPath)) return;

            try
            {
                var sys = RuntimeManager.CoreSystem;
                RESULT res = sys.createStream(audioPath, MODE.CREATESTREAM | MODE.ACCURATETIME | MODE.LOOP_NORMAL, out _previewSound);
                if (res != RESULT.OK)
                {
                    Plugin.Log?.LogError($"[Audio] Failed to create preview stream ({res}): {audioPath}");
                    return;
                }

                sys.getMasterChannelGroup(out ChannelGroup masterGroup);
                res = sys.playSound(_previewSound, masterGroup, true, out _previewChannel);
                if (res != RESULT.OK)
                {
                    _previewSound.release();
                    _previewSound.clearHandle();
                    return;
                }

                uint posMs = (uint)Mathf.Max(0f, startSec * 1000f);
                _previewSound.getLength(out uint lenMs, TIMEUNIT.MS);
                if (lenMs > 0 && posMs >= lenMs) posMs = 0;

                _previewChannel.setPosition(posMs, TIMEUNIT.MS);
                _previewChannel.setVolume(_previewDucked ? 0f : GetTargetMusicVolume());
                _previewChannel.setPaused(false);
                _hasPreview = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[Audio] StartPreview exception: {ex}");
            }
        }

        public static void SetPreviewDucked(bool ducked)
        {
            _previewDucked = ducked;
            if (!_hasPreview || !_previewChannel.hasHandle()) return;
            try
            {
                _previewChannel.setVolume(ducked ? 0f : GetTargetMusicVolume());
            }
            catch { }
        }

        public static void StopPreview()
        {
            if (!_hasPreview) return;
            try
            {
                if (_previewChannel.hasHandle())
                {
                    _previewChannel.stop();
                    _previewChannel.clearHandle();
                }
                if (_previewSound.hasHandle())
                {
                    _previewSound.release();
                    _previewSound.clearHandle();
                }
            }
            catch { }
            _hasPreview = false;
        }

        public static bool LoadIngame(string audioPath)
        {
            ReleaseIngame();
            if (string.IsNullOrEmpty(audioPath)) return false;

            try
            {
                var sys = RuntimeManager.CoreSystem;
                RESULT res = sys.createStream(audioPath, MODE.CREATESTREAM | MODE.ACCURATETIME, out _ingameSound);
                if (res != RESULT.OK)
                {
                    Plugin.Log?.LogError($"[Audio] Failed to create ingame stream ({res}): {audioPath}");
                    return false;
                }

                sys.getMasterChannelGroup(out ChannelGroup masterGroup);
                res = sys.playSound(_ingameSound, masterGroup, true, out _ingameChannel);
                if (res != RESULT.OK)
                {
                    Plugin.Log?.LogError($"[Audio] Failed to playSound ({res})");
                    _ingameSound.release();
                    _ingameSound.clearHandle();
                    return false;
                }

                _ingameFadeMultiplier = 1f;
                _ingameChannel.setVolume(GetTargetMusicVolume());
                _hasIngame = true;
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[Audio] LoadIngame exception: {ex}");
                return false;
            }
        }

        public static void PlayIngame(float startTimeSec)
        {
            if (!HasIngameBgm) return;
            uint posMs = (uint)Mathf.Max(0f, startTimeSec * 1000f);
            _ingameChannel.setPosition(posMs, TIMEUNIT.MS);
            _ingameChannel.setVolume(GetTargetMusicVolume() * _ingameFadeMultiplier);
            _ingameChannel.setPitch(1f);
            _ingameChannel.setPaused(false);
        }

        public static void PlayIngameCurrent()
        {
            if (!HasIngameBgm) return;
            _ingameChannel.setVolume(GetTargetMusicVolume() * _ingameFadeMultiplier);
            _ingameChannel.setPaused(false);
        }

        public static void SetIngamePosition(float timeSec)
        {
            if (!HasIngameBgm) return;
            uint posMs = (uint)Mathf.Max(0f, timeSec * 1000f);
            _ingameChannel.setPosition(posMs, TIMEUNIT.MS);
        }

        public static void PauseIngame()
        {
            if (!HasIngameBgm) return;
            _ingameChannel.setPaused(true);
        }

        public static void ResumeIngame()
        {
            if (!HasIngameBgm) return;
            _ingameChannel.setPitch(1f);
            _ingameChannel.setVolume(GetTargetMusicVolume() * _ingameFadeMultiplier);
            _ingameChannel.setPaused(false);
        }

        public static void SetIngamePitch(float pitch)
        {
            if (!HasIngameBgm) return;
            _ingameChannel.setPitch(pitch);
        }

        public static void SetIngameVolume(float ratio)
        {
            _ingameFadeMultiplier = Mathf.Clamp01(ratio);
            if (!HasIngameBgm) return;
            _ingameChannel.setVolume(GetTargetMusicVolume() * _ingameFadeMultiplier);
        }

        public static bool IsIngamePlaying()
        {
            if (!HasIngameBgm) return false;
            _ingameChannel.isPlaying(out bool playing);
            return playing;
        }

        public static uint GetIngamePositionMs()
        {
            if (!HasIngameBgm) return 0;
            _ingameChannel.getPosition(out uint posMs, TIMEUNIT.MS);
            return posMs;
        }

        public static void StopIngame()
        {
            if (!HasIngameBgm) return;
            try
            {
                _ingameChannel.stop();
            }
            catch { }
        }

        public static void ReleaseIngame()
        {
            if (!_hasIngame) return;
            try
            {
                if (_ingameChannel.hasHandle())
                {
                    _ingameChannel.stop();
                    _ingameChannel.clearHandle();
                }
                if (_ingameSound.hasHandle())
                {
                    _ingameSound.release();
                    _ingameSound.clearHandle();
                }
            }
            catch { }
            _hasIngame = false;
        }

        public static void ReleaseAll()
        {
            StopPreview();
            ReleaseIngame();
        }
    }
}
