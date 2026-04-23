using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.Api;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    public static class AvatarVariantSwitchWorkflow
    {
        private const string DialogTitle = "Avatar 装扮切换器";
        private const string GeneratedMenuRootName = "_AvatarSwitcherMenu";
        private static bool _busy;

        public static bool IsBusy
        {
            get { return _busy; }
        }

        public static void GenerateMenu(AvatarVariantSwitchConfig cfg)
        {
            if (!Validate(cfg, false, out var error))
            {
                EditorUtility.DisplayDialog(DialogTitle, error, "确定");
                return;
            }

            AvatarVariantMenuBuilder.Generate(cfg);
        }

        public static void WriteMap(AvatarVariantSwitchConfig cfg)
        {
            if (!Validate(cfg, false, out var error))
            {
                EditorUtility.DisplayDialog(DialogTitle, error, "确定");
                return;
            }

            try
            {
                var map = AvatarVariantMap.Read(cfg.outputMapPath);
                map.parameterName = cfg.parameterName.Trim();
                map.menuName = ResolveMenuName(cfg);
                map.defaultValue = cfg.defaultValue;

                foreach (var variant in cfg.variants)
                {
                    var existing = map.FindByKey(variant.variantKey);
                    if (existing == null || string.IsNullOrWhiteSpace(existing.blueprintId))
                    {
                        continue;
                    }

                    map.Upsert(variant.variantKey, variant.paramValue, variant.displayName, existing.blueprintId);
                }

                AvatarVariantMap.WriteAtomic(cfg.outputMapPath, map);
                EditorUtility.DisplayDialog(DialogTitle, "映射文件已写入。", "确定");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(DialogTitle, "写入映射文件失败：" + ex.Message, "确定");
            }
        }

        public static async void StartBatchUpload(AvatarVariantSwitchConfig cfg)
        {
            if (_busy)
            {
                EditorUtility.DisplayDialog(DialogTitle, "已有批量上传任务正在运行。", "确定");
                return;
            }

            if (!Validate(cfg, true, out var error))
            {
                EditorUtility.DisplayDialog(DialogTitle, error, "确定");
                return;
            }

            try
            {
                await RunBatchUploadAsync(cfg);
            }
            catch (OperationCanceledException)
            {
                Debug.Log("Batch upload cancelled.");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(DialogTitle, "批量上传失败：" + ex.Message, "确定");
            }
        }

        public static void PruneStaleMapKeys(AvatarVariantSwitchConfig cfg)
        {
            try
            {
                var report = BuildValidationReport(cfg, false);
                if (report.StaleVariants.Count == 0)
                {
                    EditorUtility.DisplayDialog(DialogTitle, "当前没有可清理的旧映射记录。", "确定");
                    return;
                }

                if (!EditorUtility.DisplayDialog(
                        DialogTitle,
                        string.Format("映射文件里有 {0} 条当前配置已不存在的旧记录，确认删除吗？", report.StaleVariants.Count),
                        "清理",
                        "取消"))
                {
                    return;
                }

                var map = AvatarVariantMap.Read(cfg.outputMapPath);
                var validKeys = new HashSet<string>(
                    (cfg.variants ?? new List<AvatarVariantEntry>())
                        .Where(variant => variant != null && !string.IsNullOrWhiteSpace(variant.variantKey))
                        .Select(variant => variant.variantKey));

                map.PruneKeysNotIn(validKeys);
                map.parameterName = cfg.parameterName.Trim();
                map.menuName = ResolveMenuName(cfg);
                map.defaultValue = cfg.defaultValue;
                AvatarVariantMap.WriteAtomic(cfg.outputMapPath, map);

                EditorUtility.DisplayDialog(DialogTitle, "旧映射记录已清理。", "确定");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(DialogTitle, "清理旧映射记录失败：" + ex.Message, "确定");
            }
        }

        public static bool Validate(AvatarVariantSwitchConfig cfg, bool requireThumbnails, out string error)
        {
            var report = BuildValidationReport(cfg, requireThumbnails);
            if (report.Errors.Count == 0)
            {
                error = string.Empty;
                return true;
            }

            error = string.Join("\n", report.Errors.ToArray());
            return false;
        }

        internal static ValidationReport BuildValidationReport(AvatarVariantSwitchConfig cfg, bool requireThumbnails)
        {
            var report = new ValidationReport();
            if (cfg == null)
            {
                report.Errors.Add("配置组件不存在。");
                return report;
            }

            EnsureVariantKeys(cfg);
            EnsureAvatarDescriptor(cfg);

            var avatarRoot = cfg.AvatarRoot;
            var avatarDescriptor = cfg.avatarDescriptor;
            var parameterName = (cfg.parameterName ?? string.Empty).Trim();

            if (avatarDescriptor == null)
            {
                report.Errors.Add("请把 Config 挂到带 VRCAvatarDescriptor 的 avatar root 上。");
                return report;
            }

            if (avatarRoot == null)
            {
                report.Errors.Add("无法解析 Avatar Root。");
                return report;
            }

            if (avatarRoot.GetComponent<VRCAvatarDescriptor>() == null)
            {
                report.Errors.Add("主 Avatar Root 缺少 VRCAvatarDescriptor。");
            }

            if (avatarRoot.GetComponent<PipelineManager>() == null)
            {
                report.Errors.Add("主 Avatar Root 缺少 PipelineManager。");
            }

            if (string.IsNullOrWhiteSpace(parameterName))
            {
                report.Errors.Add("参数名不能为空。");
            }

            var variants = cfg.variants ?? new List<AvatarVariantEntry>();
            if (variants.Count == 0)
            {
                report.Errors.Add("至少需要配置一个装扮。");
            }
            else if (variants.Count > 7)
            {
                report.Errors.Add("当前版本最多支持 7 个装扮；请先减少数量。");
            }

            var variantKeys = new HashSet<string>();
            var paramValues = new HashSet<int>();
            var controlledRoots = new List<GameObject>();

            for (var i = 0; i < variants.Count; i++)
            {
                var variant = variants[i];
                if (variant == null)
                {
                    report.Errors.Add(string.Format("第 {0} 个装扮为空。", i + 1));
                    continue;
                }

                var label = GetVariantLabel(variant, i);
                if (string.IsNullOrWhiteSpace(variant.displayName))
                {
                    report.Errors.Add(string.Format("装扮 {0} 的显示名称不能为空。", label));
                }

                if (string.IsNullOrWhiteSpace(variant.variantKey))
                {
                    report.Errors.Add(string.Format("装扮 {0} 缺少稳定标识 variantKey。", label));
                }
                else if (!variantKeys.Add(variant.variantKey))
                {
                    report.Errors.Add(string.Format("装扮 {0} 的 variantKey 与其他装扮重复。", label));
                }

                if (variant.paramValue < 0)
                {
                    report.Errors.Add(string.Format("装扮 {0} 的 paramValue 不能小于 0。", label));
                }
                else if (!paramValues.Add(variant.paramValue))
                {
                    report.Errors.Add(string.Format("装扮 {0} 的 paramValue 与其他装扮重复。", label));
                }

                if (variant.includedRoots == null)
                {
                    report.Errors.Add(string.Format("装扮 {0} 的 includedRoots 不能为空。", label));
                    continue;
                }

                foreach (var root in variant.includedRoots.Where(root => root != null))
                {
                    if (!root.transform.IsChildOf(avatarRoot.transform))
                    {
                        report.Errors.Add(string.Format("装扮 {0} 引用了不属于当前 Avatar Root 的对象：{1}。", label, root.name));
                        continue;
                    }

                    if (IsUnderMenuRoot(root, cfg))
                    {
                        report.Errors.Add(string.Format("装扮 {0} 不能包含 _AvatarSwitcherMenu 或其子物体：{1}。", label, root.name));
                        continue;
                    }

                    controlledRoots.Add(root);
                }

                if (requireThumbnails)
                {
                    try
                    {
                        ResolveThumbnailPath(variant.thumbnail, label);
                    }
                    catch (Exception ex)
                    {
                        report.Errors.Add(ex.Message);
                    }
                }
            }

            if (variants.Count > 0 && !variants.Any(variant => variant != null && variant.paramValue == cfg.defaultValue))
            {
                report.Errors.Add("defaultValue 必须对应某一个装扮的 paramValue。");
            }

            ValidateControlledRoots(controlledRoots, report);
            ValidateParameterConflicts(cfg, parameterName, report);
            ValidateOutputPath(cfg.outputMapPath, report);
            ValidateMenuCapacity(avatarDescriptor, report);
            PopulateStaleMapInfo(cfg, report);

            return report;
        }

        private static async Task RunBatchUploadAsync(AvatarVariantSwitchConfig cfg)
        {
            _busy = true;
            using var cts = new CancellationTokenSource();
            AvatarVariantTagGuard guard = null;
            EditorApplication.LockReloadAssemblies();

            try
            {
                var plan = BatchPlan.Snapshot(cfg);
                AvatarVariantMenuBuilder.Generate(cfg);

                var map = AvatarVariantMap.Read(plan.outputMapPath);
                map.parameterName = plan.parameterName;
                map.menuName = plan.menuName;
                map.defaultValue = plan.defaultValue;

                var pm = plan.avatarRoot.GetComponent<PipelineManager>();
                if (pm == null)
                {
                    throw new InvalidOperationException("主 Avatar Root 缺少 PipelineManager。");
                }

                var controlledRoots = plan.variants
                    .SelectMany(variant => variant.includedRoots)
                    .Where(root => root != null)
                    .Where(root => !IsUnderMenuRoot(root, cfg))
                    .Distinct()
                    .ToList();

                guard = AvatarVariantTagGuard.Capture(pm, controlledRoots);

                var builder = await AvatarVariantBuilderGate.AcquireAsync(cts.Token);
                builder.SelectAvatar(plan.avatarRoot);
                EditorSceneManager.SaveOpenScenes();

                for (var i = 0; i < plan.variants.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var variant = plan.variants[i];
                    if (EditorUtility.DisplayCancelableProgressBar(
                            DialogTitle,
                            string.Format("正在上传 {0}/{1}：{2}", i + 1, plan.variants.Count, variant.displayName),
                            (float)i / Math.Max(1, plan.variants.Count)))
                    {
                        cts.Cancel();
                    }

                    cts.Token.ThrowIfCancellationRequested();

                    var activeSet = new HashSet<GameObject>(variant.includedRoots.Where(root => root != null));
                    guard.ApplyActive(activeSet);

                    var existing = map.FindByKey(variant.variantKey);
                    guard.SetBlueprintId(existing != null ? existing.blueprintId : string.Empty);

                    EditorSceneManager.MarkSceneDirty(plan.avatarRoot.scene);
                    EditorSceneManager.SaveOpenScenes();

                    var record = new VRCAvatar
                    {
                        ID = pm.blueprintId ?? string.Empty,
                        Name = ResolveUploadedName(plan, variant),
                        Description = variant.uploadedDescription ?? string.Empty,
                        Tags = new List<string>(),
                        ReleaseStatus = plan.releaseStatus == ReleaseStatus.Public ? "public" : "private"
                    };

                    await builder.BuildAndUpload(plan.avatarRoot, record, variant.thumbnailPath, cts.Token);

                    var blueprintId = pm.blueprintId ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(blueprintId))
                    {
                        throw new InvalidOperationException(string.Format("装扮“{0}”上传完成后没有得到 blueprintId。", variant.displayName));
                    }

                    map.Upsert(variant.variantKey, variant.paramValue, variant.displayName, blueprintId);
                    AvatarVariantMap.WriteAtomic(plan.outputMapPath, map);

                    await Task.Delay(200, cts.Token);
                }

                EditorUtility.DisplayDialog(DialogTitle, "批量上传完成。", "确定");
            }
            finally
            {
                try
                {
                    if (guard != null)
                    {
                        guard.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }

                try
                {
                    EditorSceneManager.SaveOpenScenes();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }

                try
                {
                    EditorUtility.ClearProgressBar();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }

                try
                {
                    EditorApplication.UnlockReloadAssemblies();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }

                _busy = false;
            }
        }

        private static void EnsureAvatarDescriptor(AvatarVariantSwitchConfig cfg)
        {
            if (cfg.avatarDescriptor != null)
            {
                return;
            }

            cfg.avatarDescriptor = cfg.GetComponent<VRCAvatarDescriptor>();
            if (cfg.avatarDescriptor != null)
            {
                EditorUtility.SetDirty(cfg);
            }
        }

        private static void EnsureVariantKeys(AvatarVariantSwitchConfig cfg)
        {
            if (cfg.variants == null)
            {
                return;
            }

            var changed = false;
            foreach (var variant in cfg.variants)
            {
                if (variant == null || !string.IsNullOrWhiteSpace(variant.variantKey))
                {
                    continue;
                }

                variant.variantKey = Guid.NewGuid().ToString("N");
                changed = true;
            }

            if (changed)
            {
                EditorUtility.SetDirty(cfg);
            }
        }

        private static void ValidateControlledRoots(List<GameObject> controlledRoots, ValidationReport report)
        {
            var uniqueRoots = controlledRoots
                .Where(root => root != null)
                .Distinct()
                .ToList();

            for (var i = 0; i < uniqueRoots.Count; i++)
            {
                for (var j = i + 1; j < uniqueRoots.Count; j++)
                {
                    var a = uniqueRoots[i];
                    var b = uniqueRoots[j];
                    if (a.transform.IsChildOf(b.transform) || b.transform.IsChildOf(a.transform))
                    {
                        report.Errors.Add(string.Format("受控对象不能出现父子重叠：{0} / {1}。", a.name, b.name));
                    }
                }
            }
        }

        private static void ValidateParameterConflicts(AvatarVariantSwitchConfig cfg, string parameterName, ValidationReport report)
        {
            if (string.IsNullOrWhiteSpace(parameterName) || cfg.avatarDescriptor == null)
            {
                return;
            }

            var expressionParameters = cfg.avatarDescriptor.expressionParameters;
            if (expressionParameters != null && expressionParameters.parameters != null)
            {
                foreach (var parameter in expressionParameters.parameters)
                {
                    if (!string.Equals(parameter.name, parameterName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (parameter.valueType != VRCExpressionParameters.ValueType.Int)
                    {
                        report.Errors.Add(string.Format("参数名“{0}”已在 Expression Parameters 中存在，但类型不是 Int。", parameterName));
                    }
                }
            }

            var allParameters = cfg.AvatarRoot.GetComponentsInChildren<ModularAvatarParameters>(true);
            foreach (var parameterComponent in allParameters)
            {
                if (parameterComponent == null || IsUnderMenuRoot(parameterComponent.gameObject, cfg))
                {
                    continue;
                }

                if (parameterComponent.parameters == null)
                {
                    continue;
                }

                foreach (var parameter in parameterComponent.parameters)
                {
                    // ParameterConfig is a struct; skip default/empty entries by checking nameOrPrefix.
                    if (string.IsNullOrWhiteSpace(parameter.nameOrPrefix))
                    {
                        continue;
                    }

                    if (!string.Equals(parameter.nameOrPrefix.Trim(), parameterName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    report.Errors.Add(string.Format("参数名“{0}”已在其他 Modular Avatar Parameters 组件中定义。", parameterName));
                    return;
                }
            }
        }

        private static void ValidateOutputPath(string outputMapPath, ValidationReport report)
        {
            if (string.IsNullOrWhiteSpace(outputMapPath))
            {
                report.Errors.Add("outputMapPath 不能为空。");
                return;
            }

            if (outputMapPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                var packageRoot = GetPackageRoot(outputMapPath);
                if (!string.IsNullOrWhiteSpace(packageRoot))
                {
                    var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(packageRoot);
                    if (packageInfo != null && packageInfo.source != PackageSource.Embedded)
                    {
                        report.Warnings.Add("当前 outputMapPath 指向只读包目录，建议改到 Assets/...。");
                    }
                }
            }

            try
            {
                var _ = AvatarVariantMap.Read(outputMapPath);
            }
            catch (Exception ex)
            {
                report.Errors.Add("现有映射文件无法读取：" + ex.Message);
            }
        }

        private static void ValidateMenuCapacity(VRCAvatarDescriptor avatarDescriptor, ValidationReport report)
        {
            if (avatarDescriptor == null || avatarDescriptor.expressionsMenu == null)
            {
                return;
            }

            var controls = avatarDescriptor.expressionsMenu.controls;
            if (controls != null && controls.Count > 7)
            {
                report.Errors.Add("主 Expressions Menu 至少需要预留 1 个槽位给装扮切换入口。");
            }
        }

        private static void PopulateStaleMapInfo(AvatarVariantSwitchConfig cfg, ValidationReport report)
        {
            if (string.IsNullOrWhiteSpace(cfg.outputMapPath))
            {
                return;
            }

            AvatarVariantMap map;
            try
            {
                map = AvatarVariantMap.Read(cfg.outputMapPath);
            }
            catch
            {
                return;
            }

            report.Map = map;
            var validKeys = new HashSet<string>(
                (cfg.variants ?? new List<AvatarVariantEntry>())
                    .Where(variant => variant != null && !string.IsNullOrWhiteSpace(variant.variantKey))
                    .Select(variant => variant.variantKey));

            foreach (var variant in map.variants)
            {
                if (variant == null || string.IsNullOrWhiteSpace(variant.variantKey))
                {
                    continue;
                }

                if (!validKeys.Contains(variant.variantKey))
                {
                    report.StaleVariants.Add(variant);
                }
            }
        }

        private static string ResolveThumbnailPath(Texture2D thumbnail, string variantLabel)
        {
            if (thumbnail == null)
            {
                throw new InvalidOperationException(string.Format("装扮 {0} 缺少缩略图。", variantLabel));
            }

            var assetPath = AssetDatabase.GetAssetPath(thumbnail);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw new InvalidOperationException(string.Format("装扮 {0} 的缩略图不是有效的项目资源。", variantLabel));
            }

            var fullPath = Path.GetFullPath(assetPath);
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException(string.Format("装扮 {0} 的缩略图文件不存在：{1}", variantLabel, fullPath));
            }

            return fullPath;
        }

        private static string ResolveUploadedName(BatchPlan plan, BatchVariantPlan variant)
        {
            if (!string.IsNullOrWhiteSpace(variant.uploadedName))
            {
                return variant.uploadedName.Trim();
            }

            var resolved = string.Format("{0}{1} - {2}", plan.uploadedAvatarNamePrefix, plan.avatarRoot.name, variant.displayName).Trim();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            return string.Format("{0} - {1}", plan.avatarRoot.name, variant.displayName);
        }

        private static string ResolveMenuName(AvatarVariantSwitchConfig cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.menuName))
            {
                return cfg.menuName.Trim();
            }

            return (cfg.parameterName ?? string.Empty).Trim();
        }

        private static bool IsUnderMenuRoot(GameObject candidate, AvatarVariantSwitchConfig cfg)
        {
            if (candidate == null || cfg == null || cfg.AvatarRoot == null)
            {
                return false;
            }

            var menuRoot = FindMenuRoot(cfg.AvatarRoot.transform);
            if (menuRoot == null)
            {
                return false;
            }

            return candidate.transform == menuRoot || candidate.transform.IsChildOf(menuRoot);
        }

        private static Transform FindMenuRoot(Transform avatarRoot)
        {
            if (avatarRoot == null)
            {
                return null;
            }

            return avatarRoot.Find(GeneratedMenuRootName);
        }

        private static string GetPackageRoot(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return string.Empty;
            }

            var normalized = assetPath.Replace('\\', '/');
            var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[0], "Packages", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return "Packages/" + parts[1];
        }

        private static string GetVariantLabel(AvatarVariantEntry variant, int index)
        {
            if (variant == null)
            {
                return string.Format("#{0}", index + 1);
            }

            if (!string.IsNullOrWhiteSpace(variant.displayName))
            {
                return variant.displayName.Trim();
            }

            return string.Format("#{0}", index + 1);
        }

        internal sealed class ValidationReport
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<AvatarVariantMapVariant> StaleVariants = new List<AvatarVariantMapVariant>();
            public AvatarVariantMap Map;
        }

        private sealed class BatchPlan
        {
            public readonly GameObject avatarRoot;
            public readonly string parameterName;
            public readonly string menuName;
            public readonly int defaultValue;
            public readonly ReleaseStatus releaseStatus;
            public readonly string outputMapPath;
            public readonly string uploadedAvatarNamePrefix;
            public readonly List<BatchVariantPlan> variants;

            private BatchPlan(
                GameObject avatarRoot,
                string parameterName,
                string menuName,
                int defaultValue,
                ReleaseStatus releaseStatus,
                string outputMapPath,
                string uploadedAvatarNamePrefix,
                List<BatchVariantPlan> variants)
            {
                this.avatarRoot = avatarRoot;
                this.parameterName = parameterName;
                this.menuName = menuName;
                this.defaultValue = defaultValue;
                this.releaseStatus = releaseStatus;
                this.outputMapPath = outputMapPath;
                this.uploadedAvatarNamePrefix = uploadedAvatarNamePrefix;
                this.variants = variants;
            }

            public static BatchPlan Snapshot(AvatarVariantSwitchConfig cfg)
            {
                var variants = new List<BatchVariantPlan>();
                foreach (var variant in cfg.variants)
                {
                    variants.Add(new BatchVariantPlan(
                        variant.displayName ?? string.Empty,
                        variant.variantKey ?? string.Empty,
                        variant.paramValue,
                        ResolveThumbnailPath(variant.thumbnail, GetVariantLabel(variant, variants.Count)),
                        variant.uploadedName ?? string.Empty,
                        variant.uploadedDescription ?? string.Empty,
                        new List<GameObject>(variant.includedRoots ?? new List<GameObject>())));
                }

                return new BatchPlan(
                    cfg.AvatarRoot,
                    (cfg.parameterName ?? string.Empty).Trim(),
                    ResolveMenuName(cfg),
                    cfg.defaultValue,
                    cfg.releaseStatus,
                    cfg.outputMapPath,
                    cfg.uploadedAvatarNamePrefix ?? string.Empty,
                    variants);
            }
        }

        private sealed class BatchVariantPlan
        {
            public readonly string displayName;
            public readonly string variantKey;
            public readonly int paramValue;
            public readonly string thumbnailPath;
            public readonly string uploadedName;
            public readonly string uploadedDescription;
            public readonly List<GameObject> includedRoots;

            public BatchVariantPlan(
                string displayName,
                string variantKey,
                int paramValue,
                string thumbnailPath,
                string uploadedName,
                string uploadedDescription,
                List<GameObject> includedRoots)
            {
                this.displayName = displayName;
                this.variantKey = variantKey;
                this.paramValue = paramValue;
                this.thumbnailPath = thumbnailPath;
                this.uploadedName = uploadedName;
                this.uploadedDescription = uploadedDescription;
                this.includedRoots = includedRoots;
            }
        }
    }
}
