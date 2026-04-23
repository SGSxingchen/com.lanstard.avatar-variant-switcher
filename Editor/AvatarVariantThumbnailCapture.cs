using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    public static class AvatarVariantThumbnailCapture
    {
        private const int ThumbnailSize = 1024;
        private const string ThumbnailFolder = "Assets/AvatarVariantSwitcher/Generated/Thumbnails";
        private const string DialogTitle = "Avatar 装扮切换器";

        public static void CaptureMissing(AvatarVariantSwitchConfig cfg)
        {
            if (!Precheck(cfg))
            {
                return;
            }

            var targets = new List<AvatarVariantEntry>();
            foreach (var variant in cfg.variants)
            {
                if (variant == null) continue;
                if (variant.thumbnail != null) continue;
                targets.Add(variant);
            }

            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog(DialogTitle, "所有装扮都已有缩略图，无需重新生成。", "确定");
                return;
            }

            CaptureCore(cfg, targets);
        }

        public static void CaptureAll(AvatarVariantSwitchConfig cfg)
        {
            if (!Precheck(cfg))
            {
                return;
            }

            var targets = new List<AvatarVariantEntry>();
            foreach (var variant in cfg.variants)
            {
                if (variant == null) continue;
                targets.Add(variant);
            }

            if (targets.Count == 0)
            {
                return;
            }

            var ok = EditorUtility.DisplayDialog(
                DialogTitle,
                string.Format("即将重新渲染并覆盖 {0} 张缩略图。继续吗？", targets.Count),
                "继续",
                "取消");
            if (!ok)
            {
                return;
            }

            CaptureCore(cfg, targets);
        }

        private static bool Precheck(AvatarVariantSwitchConfig cfg)
        {
            if (cfg == null || cfg.avatarDescriptor == null)
            {
                EditorUtility.DisplayDialog(DialogTitle, "请先为 Config 指定 VRCAvatarDescriptor。", "确定");
                return false;
            }

            if (cfg.variants == null || cfg.variants.Count == 0)
            {
                EditorUtility.DisplayDialog(DialogTitle, "当前没有任何装扮，无法生成缩略图。", "确定");
                return false;
            }

            return true;
        }

        private static void CaptureCore(AvatarVariantSwitchConfig cfg, List<AvatarVariantEntry> targets)
        {
            Directory.CreateDirectory(ThumbnailFolder);

            var controlled = CollectControlledObjects(cfg);
            var activeSnapshot = new Dictionary<GameObject, bool>();
            foreach (var go in controlled)
            {
                if (go == null) continue;
                activeSnapshot[go] = go.activeSelf;
            }

            GameObject tempLight = null;
            GameObject cameraHost = null;
            Camera camera = null;
            RenderTexture rt = null;
            var sceneDirty = false;

            try
            {
                if (!HasActiveDirectionalLight())
                {
                    tempLight = new GameObject("_TempThumbnailLight");
                    tempLight.hideFlags = HideFlags.HideAndDontSave;
                    var light = tempLight.AddComponent<Light>();
                    light.type = LightType.Directional;
                    light.intensity = 1.0f;
                    light.color = Color.white;
                    tempLight.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                }

                cameraHost = new GameObject("_TempThumbnailCamera");
                cameraHost.hideFlags = HideFlags.HideAndDontSave;
                camera = cameraHost.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.18f, 0.18f, 0.20f, 1f);
                camera.fieldOfView = 30f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 10f;
                camera.allowHDR = false;
                camera.allowMSAA = true;
                camera.enabled = false;

                rt = new RenderTexture(ThumbnailSize, ThumbnailSize, 24, RenderTextureFormat.ARGB32);
                rt.Create();

                PositionCamera(cfg, camera.transform);

                var scene = cfg.gameObject != null ? cfg.gameObject.scene : default(UnityEngine.SceneManagement.Scene);

                for (var i = 0; i < targets.Count; i++)
                {
                    var variant = targets[i];
                    var label = string.IsNullOrWhiteSpace(variant.displayName) ? "未命名装扮" : variant.displayName;
                    var progress = (float)i / Mathf.Max(1, targets.Count);
                    if (EditorUtility.DisplayCancelableProgressBar(DialogTitle, string.Format("渲染缩略图 {0}/{1}: {2}", i + 1, targets.Count, label), progress))
                    {
                        break;
                    }

                    sceneDirty |= ApplyVisibility(controlled, BuildShowSet(variant));

                    camera.targetTexture = rt;
                    camera.Render();
                    camera.targetTexture = null;

                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(ThumbnailSize, ThumbnailSize, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, ThumbnailSize, ThumbnailSize), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    var png = tex.EncodeToPNG();
                    UnityEngine.Object.DestroyImmediate(tex);

                    var path = string.Format("{0}/{1}.png", ThumbnailFolder, SanitizeKey(variant.variantKey));
                    File.WriteAllBytes(path, png);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer != null)
                    {
                        importer.textureType = TextureImporterType.Default;
                        importer.sRGBTexture = true;
                        importer.maxTextureSize = 1024;
                        importer.SaveAndReimport();
                    }

                    var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (imported != null)
                    {
                        variant.thumbnail = imported;
                    }

                    EditorUtility.SetDirty(cfg);
                }

                if (sceneDirty && scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                }
                AssetDatabase.SaveAssets();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog(DialogTitle, string.Format("生成缩略图失败：{0}", e.Message), "确定");
            }
            finally
            {
                try { EditorUtility.ClearProgressBar(); }
                catch (Exception e) { Debug.LogException(e); }

                foreach (var pair in activeSnapshot)
                {
                    try
                    {
                        if (pair.Key != null && pair.Key.activeSelf != pair.Value)
                        {
                            pair.Key.SetActive(pair.Value);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                try { if (camera != null) camera.targetTexture = null; }
                catch (Exception e) { Debug.LogException(e); }
                try
                {
                    if (rt != null)
                    {
                        rt.Release();
                        UnityEngine.Object.DestroyImmediate(rt);
                    }
                }
                catch (Exception e) { Debug.LogException(e); }
                try { if (cameraHost != null) UnityEngine.Object.DestroyImmediate(cameraHost); }
                catch (Exception e) { Debug.LogException(e); }
                try { if (tempLight != null) UnityEngine.Object.DestroyImmediate(tempLight); }
                catch (Exception e) { Debug.LogException(e); }

                try { AssetDatabase.Refresh(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        private static List<GameObject> CollectControlledObjects(AvatarVariantSwitchConfig cfg)
        {
            var result = new List<GameObject>();
            var seen = new HashSet<GameObject>();
            if (cfg.variants == null) return result;

            foreach (var variant in cfg.variants)
            {
                if (variant == null) continue;
                if (variant.includedRoots != null)
                {
                    foreach (var go in variant.includedRoots)
                    {
                        if (go == null) continue;
                        if (seen.Add(go)) result.Add(go);
                    }
                }

                if (variant.accessories != null)
                {
                    foreach (var acc in variant.accessories)
                    {
                        if (acc == null || acc.target == null) continue;
                        if (seen.Add(acc.target)) result.Add(acc.target);
                    }
                }
            }

            return result;
        }

        private static HashSet<GameObject> BuildShowSet(AvatarVariantEntry variant)
        {
            var set = new HashSet<GameObject>();
            if (variant == null) return set;

            if (variant.includedRoots != null)
            {
                foreach (var go in variant.includedRoots)
                {
                    if (go != null) set.Add(go);
                }
            }

            if (variant.accessories != null)
            {
                foreach (var acc in variant.accessories)
                {
                    if (acc == null || acc.target == null) continue;
                    if (!acc.defaultOn) continue;
                    set.Add(acc.target);
                }
            }

            return set;
        }

        private static bool ApplyVisibility(List<GameObject> controlled, HashSet<GameObject> showSet)
        {
            var touched = false;
            foreach (var go in controlled)
            {
                if (go == null) continue;
                var shouldShow = showSet.Contains(go);
                if (go.activeSelf != shouldShow)
                {
                    go.SetActive(shouldShow);
                    touched = true;
                }
            }
            return touched;
        }

        private static bool HasActiveDirectionalLight()
        {
            var lights = UnityEngine.Object.FindObjectsOfType<Light>();
            foreach (var light in lights)
            {
                if (light == null) continue;
                if (light.type != LightType.Directional) continue;
                if (!light.isActiveAndEnabled) continue;
                return true;
            }
            return false;
        }

        private static void PositionCamera(AvatarVariantSwitchConfig cfg, Transform cam)
        {
            var rootTr = cfg.AvatarRoot.transform;
            Vector3 headWorld;
            if (cfg.avatarDescriptor != null)
            {
                var viewPosLocal = cfg.avatarDescriptor.ViewPosition;
                headWorld = rootTr.TransformPoint(viewPosLocal);
            }
            else
            {
                headWorld = rootTr.position + Vector3.up * 1.6f;
            }

            var chestWorld = headWorld - rootTr.up * 0.22f;
            var camPos = chestWorld + rootTr.forward * 1.3f + rootTr.up * 0.05f;
            cam.SetPositionAndRotation(camPos, Quaternion.LookRotation(chestWorld - camPos, rootTr.up));
        }

        private static string SanitizeKey(string variantKey)
        {
            if (string.IsNullOrWhiteSpace(variantKey)) return "unnamed";
            return variantKey.Trim();
        }
    }
}
