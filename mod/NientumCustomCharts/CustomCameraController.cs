using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NientumCustomCharts
{
    /// <summary>
    /// 实验版：支持自定义镜头变化幅度 (camOffsetX/Y/Z) 与速度/缓动 (camEase + Duration)，
    /// 在游戏内通过增量差值平滑叠加到 StageCameraController.CameraRoot，实现“重音镜头向上飞”等张力表现。
    /// </summary>
    public static class CustomCameraController
    {
        public struct CameraKeyframe
        {
            public float StartTime;
            public float EndTime;
            public Vector3 TargetOffset;
            public int Ease;
        }

        private static readonly FieldInfo FieldCameraController =
            AccessTools.Field(typeof(IngameScene), "_cameraController");
        private static readonly FieldInfo FieldCameraRoot =
            AccessTools.Field(typeof(StageCameraController), "CameraRoot");
        private static readonly FieldInfo FieldCurrentTime =
            AccessTools.Field(typeof(IngameScene), "currentTime");

        private static List<CameraKeyframe> _keyframes = new List<CameraKeyframe>();
        private static IngameScene _boundScene;
        private static Vector3 _appliedOffset = Vector3.zero;

        public static void Clear()
        {
            _keyframes.Clear();
            _boundScene = null;
            _appliedOffset = Vector3.zero;
        }

        public static void ConfigureChart(List<CameraKeyframe> keyframes)
        {
            _keyframes = keyframes ?? new List<CameraKeyframe>();
            _keyframes.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            _boundScene = null;
            _appliedOffset = Vector3.zero;
            if (_keyframes.Count > 0)
            {
                DiagLog.Info($"[Exp] Configured {_keyframes.Count} custom camera keyframe(s)");
            }
        }

        public static void UpdateScene(IngameScene scene)
        {
            if (scene == null || _keyframes.Count == 0) return;

            var camCtrl = FieldCameraController?.GetValue(scene) as StageCameraController;
            var camRoot = camCtrl != null ? FieldCameraRoot?.GetValue(camCtrl) as StageCameraRoot : null;
            if (camRoot == null) return;
            Transform root = camRoot.transform;

            if (_boundScene != scene)
            {
                _boundScene = scene;
                _appliedOffset = Vector3.zero;
            }

            float curTime = 0f;
            if (FieldCurrentTime != null)
            {
                object v = FieldCurrentTime.GetValue(scene);
                if (v is float ft) curTime = ft;
            }

            // 先减去上一帧叠加的偏移，避免与原版镜头动画累积偏移
            if (_appliedOffset.sqrMagnitude > 1e-8f)
            {
                root.localPosition -= _appliedOffset;
                _appliedOffset = Vector3.zero;
            }

            if (scene.GameState != EGameState.Playing) return;

            Vector3 target = Vector3.zero;
            for (int i = 0; i < _keyframes.Count; i++)
            {
                var kf = _keyframes[i];
                if (curTime < kf.StartTime) break;
                float dur = Mathf.Max(0.02f, kf.EndTime - kf.StartTime);
                float u = curTime >= kf.EndTime ? 1f : EvalEase((curTime - kf.StartTime) / dur, kf.Ease);
                target = Vector3.LerpUnclamped(target, kf.TargetOffset, u);
            }

            if (target.sqrMagnitude > 1e-8f)
            {
                root.localPosition += target;
                _appliedOffset = target;
            }
        }

        private static float EvalEase(float u, int ease)
        {
            u = Mathf.Clamp01(u);
            switch (ease)
            {
                case 1: // OutExpo
                    return u >= 1f ? 1f : 1f - Mathf.Pow(2f, -10f * u);
                case 2: // Linear
                    return u;
                case 3: // InOutQuad
                    return u < 0.5f ? 2f * u * u : 1f - Mathf.Pow(-2f * u + 2f, 2f) * 0.5f;
                case 4: // OutBack
                    float c1 = 1.70158f;
                    float c3 = c1 + 1f;
                    return 1f + c3 * Mathf.Pow(u - 1f, 3f) + c1 * Mathf.Pow(u - 1f, 2f);
                default: // OutCubic
                    return 1f - Mathf.Pow(1f - u, 3f);
            }
        }
    }
}
