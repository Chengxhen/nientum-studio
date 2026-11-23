using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NientumCustomCharts
{
    /// <summary>
    /// 实验版：在游戏内按图层方向实时偏移斜轨音符位置，
    /// 采用“同 X 坐标对齐”公式 dy = (noteBaseX - charX) * tan(angle)，
    /// 确保音符与判定框在同一 X 坐标时精确重合并击中。
    /// </summary>
    public static class AngledTrackController
    {
        private static readonly FieldInfo FieldBlockController =
            AccessTools.Field(typeof(IngameScene), "blockController");
        private static readonly FieldInfo FieldCharacterController =
            AccessTools.Field(typeof(IngameScene), "_characterController");
        private static readonly FieldInfo FieldMovingRoot =
            AccessTools.Field(typeof(StageCharacterController), "_characterMovingRoot");
        private static readonly FieldInfo FieldOrbTail =
            AccessTools.Field(typeof(MapBlockJudgeOrb), "TailTransform");
        private static readonly FieldInfo FieldOrbLine =
            AccessTools.Field(typeof(MapBlockJudgeOrb), "NoteLineRenderer");

        private sealed class TrackedBlock
        {
            public MapBlockBase Block;
            public Vector3 BasePos;
            public float CosAngle;
            public float SinAngle;

            // 长条专有缓存
            public bool IsOrb;
            public Transform TailTransform;
            public Vector3 TailBasePos;
            public LineRenderer LineRenderer;
            public bool LineWorldSpace;
            public Vector3[] LineBasePts;
            public Vector3[] LineWorkPts;
            public List<(Transform tf, Vector3 basePos)> MiddleList;
        }

        private static Dictionary<int, float> _anglesById = new Dictionary<int, float>();
        private static readonly List<TrackedBlock> _tracked = new List<TrackedBlock>();
        private static IngameScene _boundScene;
        private static bool _initialized;

        public static void Clear()
        {
            _anglesById.Clear();
            _tracked.Clear();
            _boundScene = null;
            _initialized = false;
        }

        public static void ConfigureChart(Dictionary<int, float> anglesById)
        {
            _anglesById = anglesById ?? new Dictionary<int, float>();
            _tracked.Clear();
            _boundScene = null;
            _initialized = false;
            if (_anglesById.Count > 0)
            {
                Plugin.Log?.LogInfo($"[Exp] Configured {_anglesById.Count} angled note entries from layers.json");
            }
        }

        public static void UpdateScene(IngameScene scene)
        {
            if (scene == null || _anglesById.Count == 0) return;
            if (scene.GameState != EGameState.Playing) return;

            if (_boundScene != scene || !_initialized)
            {
                BindSceneBlocks(scene);
                _boundScene = scene;
                _initialized = true;
            }

            if (_tracked.Count == 0) return;

            var charCtrl = FieldCharacterController?.GetValue(scene) as StageCharacterController;
            if (charCtrl == null) return;
            var movingRoot = FieldMovingRoot?.GetValue(charCtrl) as Transform;
            float charX = movingRoot != null ? movingRoot.position.x : charCtrl.transform.position.x;

            for (int i = 0; i < _tracked.Count; i++)
            {
                var tb = _tracked[i];
                if (tb.Block == null || !tb.Block.gameObject.activeInHierarchy) continue;

                float dx = tb.BasePos.x - charX;
                float clampedDx = dx < -0.8f ? -0.8f : dx;
                float newX = charX + clampedDx * tb.CosAngle;
                float newY = tb.BasePos.y + clampedDx * tb.SinAngle;
                tb.Block.transform.position = new Vector3(newX, newY, tb.BasePos.z);

                if (tb.IsOrb)
                {
                    if (tb.TailTransform != null)
                    {
                        float tdx = tb.TailBasePos.x - charX;
                        float tClamped = tdx < -0.8f ? -0.8f : tdx;
                        tb.TailTransform.position = new Vector3(
                            charX + tClamped * tb.CosAngle,
                            tb.TailBasePos.y + tClamped * tb.SinAngle,
                            tb.TailBasePos.z);
                    }
                    if (tb.MiddleList != null)
                    {
                        for (int m = 0; m < tb.MiddleList.Count; m++)
                        {
                            var (mtf, mBase) = tb.MiddleList[m];
                            if (mtf == null) continue;
                            float mdx = mBase.x - charX;
                            float mClamped = mdx < -0.8f ? -0.8f : mdx;
                            mtf.position = new Vector3(
                                charX + mClamped * tb.CosAngle,
                                mBase.y + mClamped * tb.SinAngle,
                                mBase.z);
                        }
                    }
                    if (tb.LineRenderer != null && tb.LineBasePts != null && tb.LineBasePts.Length > 0)
                    {
                        for (int p = 0; p < tb.LineBasePts.Length; p++)
                        {
                            Vector3 bp = tb.LineBasePts[p];
                            float worldX = tb.LineWorldSpace ? bp.x : (tb.BasePos.x + bp.x);
                            float pdx = worldX - charX;
                            float pClamped = pdx < -0.8f ? -0.8f : pdx;
                            float pWorldX = charX + pClamped * tb.CosAngle;
                            float pWorldY = (tb.LineWorldSpace ? bp.y : (tb.BasePos.y + bp.y)) + pClamped * tb.SinAngle;
                            tb.LineWorkPts[p] = tb.LineWorldSpace
                                ? new Vector3(pWorldX, pWorldY, bp.z)
                                : new Vector3(pWorldX - newX, pWorldY - newY, bp.z);
                        }
                        tb.LineRenderer.SetPositions(tb.LineWorkPts);
                    }
                }
            }
        }

        private static void BindSceneBlocks(IngameScene scene)
        {
            _tracked.Clear();
            var bc = FieldBlockController?.GetValue(scene) as StageBlockController;
            if (bc == null) return;

            var dict = bc.GetJudgeBlockDictionary();
            if (dict != null)
            {
                foreach (var kv in dict)
                {
                    if (kv.Value?.Blocks == null) continue;
                    foreach (var blk in kv.Value.Blocks)
                        TryAddBlock(blk);
                }
            }

            var cols = bc.GetCollisionBlocks();
            if (cols != null)
            {
                foreach (var blk in cols)
                    TryAddBlock(blk);
            }
        }

        private static void TryAddBlock(MapBlockBase blk)
        {
            if (blk == null) return;
            if (!_anglesById.TryGetValue(blk.BlockId, out float angleDeg)) return;
            if (Math.Abs(angleDeg) <= 0.1f) return;

            float rad = angleDeg * Mathf.Deg2Rad;
            var tb = new TrackedBlock
            {
                Block = blk,
                BasePos = blk.transform.position,
                CosAngle = Mathf.Cos(rad),
                SinAngle = Mathf.Sin(rad)
            };

            if (blk is MapBlockJudgeOrb orb)
            {
                tb.IsOrb = true;
                tb.TailTransform = FieldOrbTail?.GetValue(orb) as Transform;
                if (tb.TailTransform != null) tb.TailBasePos = tb.TailTransform.position;

                var lr = FieldOrbLine?.GetValue(orb) as LineRenderer;
                if (lr != null && lr.positionCount > 0)
                {
                    tb.LineRenderer = lr;
                    tb.LineWorldSpace = lr.useWorldSpace;
                    tb.LineBasePts = new Vector3[lr.positionCount];
                    tb.LineWorkPts = new Vector3[lr.positionCount];
                    lr.GetPositions(tb.LineBasePts);
                }

                var mList = orb.GetLongMiddles();
                if (mList != null && mList.Count > 0)
                {
                    tb.MiddleList = new List<(Transform, Vector3)>(mList.Count);
                    foreach (var mid in mList)
                    {
                        if (mid != null)
                            tb.MiddleList.Add((mid.transform, mid.transform.position));
                    }
                }
            }

            _tracked.Add(tb);
        }
    }
}
