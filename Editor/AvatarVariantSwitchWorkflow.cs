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
                    var blueprintId = ResolveExistingBlueprintId(map, variant.variantKey, variant.paramValue, variant.legacyUploadedBlueprintId);
                    if (string.IsNullOrWhiteSpace(blueprintId))
                    {
                        continue;
                    }

                    map.Upsert(variant.variantKey, variant.paramValue, variant.displayName, blueprintId);
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

                if (variant.accessories != null)
                {
                    var seenTargets = new HashSet<GameObject>();
                    for (int ai = 0; ai < variant.accessories.Count; ai++)
                    {
                        var acc = variant.accessories[ai];
                        if (acc == null) continue;
                        if (acc.target == null)
                        {
                            report.Errors.Add(string.Format("装扮 {0} 的第 {1} 个配件 target 未指定。", label, ai + 1));
                            continue;
                        }
                        if (!acc.target.transform.IsChildOf(avatarRoot.transform))
                        {
                            report.Errors.Add(string.Format("装扮 {0} 的配件 {1} 不属于当前 Avatar Root。", label, acc.target.name));
                            continue;
                        }
                        if (!IsAccessoryOwnedByVariant(acc.target, variant))
                        {
                            report.Errors.Add("\u914d\u4ef6\u76ee\u6807\u5fc5\u987b\u4f4d\u4e8e\u5f53\u524d\u88c5\u626e\u7684 includedRoots \u4e4b\u4e0b\uff1a" + acc.target.name);
                            continue;
                        }
                        if (IsUnderMenuRoot(acc.target, cfg))
                        {
                            report.Errors.Add(string.Format("装扮 {0} 的配件不能指向 _AvatarSwitcherMenu 里的对象：{1}。", label, acc.target.name));
                            continue;
                        }
                        if (!seenTargets.Add(acc.target))
                        {
                            report.Errors.Add(string.Format("装扮 {0} 的配件列表里 {1} 出现了多次。", label, acc.target.name));
                        }
                    }
                }

                if (requireThumbnails)
                {
                    try
                    {
                        ResolveThumbnailPath(ResolveVariantThumbnail(cfg, variant), label);
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
            ValidateParameterBudget(cfg, report);
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
            AvatarVariantParamDefaultGuard paramDefaultGuard = null;
            AvatarVariantUploadProgressWindow progressWindow = null;
            EditorApplication.LockReloadAssemblies();

            try
            {
                var plan = BatchPlan.Snapshot(cfg);
                AvatarVariantMenuBuilder.Generate(cfg);

                var planItems = new List<AvatarVariantUploadProgressWindow.VariantPlanItem>();
                foreach (var v in plan.variants)
                {
                    planItems.Add(new AvatarVariantUploadProgressWindow.VariantPlanItem(
                        v.displayName, v.variantKey, v.thumbnailAsset));
                }
                progressWindow = AvatarVariantUploadProgressWindow.ShowAndBegin(planItems);
                using var sdkModalGuard = AvatarVariantSdkModalGuard.Start(progressWindow.Log);

                var map = AvatarVariantMap.Read(plan.outputMapPath);
                map.parameterName = plan.parameterName;
                map.menuName = plan.menuName;
                map.defaultValue = plan.defaultValue;

                var pm = plan.avatarRoot.GetComponent<PipelineManager>();
                if (pm == null)
                {
                    throw new InvalidOperationException("主 Avatar Root 缺少 PipelineManager。");
                }

                // includedRoots 并集——tag 翻转和 activeSelf 摆正都要覆盖它们；
                // 但只有 includedRoots 需要 activeSelf 摆正，accessory 菜单 / _AccessoriesMenu
                // 父节点不属于 activeSelf 作用域（它们靠 tag 的 EditorOnly 剥离）。
                var includedRootsUnion = plan.variants
                    .SelectMany(variant => variant.includedRoots)
                    .Where(root => root != null)
                    .Where(root => !IsUnderMenuRoot(root, cfg))
                    .Distinct()
                    .ToList();

                var controlledRoots = new List<GameObject>(includedRootsUnion);

                // 把生成的所有配件 toggle GameObject 也纳入受控集：
                // 每轮只保留当前装扮对应的 accessory 菜单项，其他装扮的配件菜单项本轮都 EditorOnly。
                var accessoryMenuGameObjects = AvatarVariantMenuBuilder
                    .EnumerateAllAccessoryMenuGameObjects(cfg)
                    .Where(go => go != null)
                    .ToList();
                foreach (var accGo in accessoryMenuGameObjects)
                {
                    if (!controlledRoots.Contains(accGo)) controlledRoots.Add(accGo);
                }

                var accessoriesMenuRoot = AvatarVariantMenuBuilder.FindAccessoriesMenuRoot(cfg);
                if (accessoriesMenuRoot != null && !controlledRoots.Contains(accessoriesMenuRoot.gameObject))
                {
                    controlledRoots.Add(accessoriesMenuRoot.gameObject);
                }

                guard = AvatarVariantTagGuard.Capture(pm, controlledRoots, includedRootsUnion);
                paramDefaultGuard = AvatarVariantParamDefaultGuard.Capture(cfg);

                var builder = await AvatarVariantBuilderGate.AcquireAsync(cts.Token);
                builder.SelectAvatar(plan.avatarRoot);
                EditorSceneManager.SaveOpenScenes();

                // 单个装扮的上传体。null = 成功；FailureRecord = 失败（progress window 已 MarkFailure）。
                // OperationCanceledException 照常上抛，触发整批的 EndSessionCancelled。
                async Task<FailureRecord?> TryUploadOneVariantAsync(int index)
                {
                    var variant = plan.variants[index];
                    progressWindow.MarkBegin(index);

                    var activeSet = new HashSet<GameObject>(variant.includedRoots.Where(root => root != null));
                    var hasActiveAccessoryMenuItems = false;
                    foreach (var accGo in AvatarVariantMenuBuilder.EnumerateAccessoryMenuGameObjectsFor(cfg, variant.variantKey))
                    {
                        if (accGo == null)
                        {
                            continue;
                        }

                        activeSet.Add(accGo);
                        hasActiveAccessoryMenuItems = true;
                    }
                    if (hasActiveAccessoryMenuItems && accessoriesMenuRoot != null)
                    {
                        activeSet.Add(accessoriesMenuRoot.gameObject);
                    }
                    guard.ApplyActive(activeSet);
                    paramDefaultGuard.SetDefault(variant.paramValue);

                    guard.SetBlueprintId(ResolveExistingBlueprintId(map, variant.variantKey, variant.paramValue, variant.legacyUploadedBlueprintId));

                    EditorSceneManager.MarkSceneDirty(plan.avatarRoot.scene);
                    EditorSceneManager.SaveOpenScenes();

                    var record = new VRCAvatar
                    {
                        ID = pm.blueprintId ?? string.Empty,
                        Name = ResolveUploadedName(plan, variant),
                        Description = ResolveUploadedDescription(plan, variant),
                        Tags = new List<string>(),
                        ReleaseStatus = plan.releaseStatus == ReleaseStatus.Public ? "public" : "private"
                    };

                    try
                    {
                        await builder.BuildAndUpload(plan.avatarRoot, record, variant.thumbnailPath, cts.Token);
                    }
                    catch (Exception ex) when (!string.IsNullOrWhiteSpace(record.ID) && IsThumbnailAlreadyUploadedError(ex))
                    {
                        // VRChat 的文件服务对用户账户做 MD5 去重：重传一张和服务器上已有的缩略图内容
                        // 完全一致的 PNG，SDK 会抛 UploadException("This file was already uploaded")。
                        // 这种情况下 bundle 已经更新成功，缩略图保持原样即是正确结果，按成功继续。
                        Debug.LogWarning(string.Format(
                            "[AvatarVariantSwitcher] 装扮 \"{0}\" 的缩略图与 VRChat 上已有的完全相同（MD5 重复），SDK 抛出 \"already uploaded\"。Bundle 已更新，缩略图保持原样，按成功继续。",
                            variant.displayName));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var shortMsg = ClassifyErrorForUser(ex);
                        progressWindow.MarkFailure(index, shortMsg);
                        return new FailureRecord(index, variant.displayName, shortMsg);
                    }

                    try
                    {
                        var blueprintId = pm.blueprintId ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(blueprintId))
                        {
                            throw new InvalidOperationException(string.Format("装扮“{0}”上传完成后没有得到 blueprintId。", variant.displayName));
                        }

                        map.Upsert(variant.variantKey, variant.paramValue, variant.displayName, blueprintId);
                        AvatarVariantMap.WriteAtomic(plan.outputMapPath, map);

                        progressWindow.MarkSuccess(index, blueprintId);
                        return null;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        progressWindow.MarkFailure(index, ClassifyErrorForUser(ex));
                        throw;
                    }
                }

                var pendingIndices = Enumerable.Range(0, plan.variants.Count).ToList();
                var lastFailures = new List<FailureRecord>();

                while (true)
                {
                    var passFailures = new List<FailureRecord>();
                    foreach (var i in pendingIndices)
                    {
                        if (progressWindow.CancelRequested)
                        {
                            cts.Cancel();
                        }
                        cts.Token.ThrowIfCancellationRequested();

                        var result = await TryUploadOneVariantAsync(i);
                        if (result.HasValue)
                        {
                            passFailures.Add(result.Value);
                        }

                        await Task.Delay(200, cts.Token);
                    }

                    lastFailures = passFailures;
                    if (passFailures.Count == 0)
                    {
                        break;
                    }

                    var dialogMessage = FormatFailureDialogMessage(passFailures);
                    var retryChosen = EditorUtility.DisplayDialog(
                        DialogTitle,
                        dialogMessage,
                        string.Format("重试失败的装扮（{0}）", passFailures.Count),
                        "放弃");
                    if (!retryChosen)
                    {
                        break;
                    }

                    pendingIndices = passFailures.Select(f => f.OriginalIndex).ToList();
                    progressWindow.PrepareRetry(pendingIndices);
                }

                if (lastFailures.Count == 0)
                {
                    progressWindow.EndSessionSuccess(null);
                    EditorUtility.DisplayDialog(DialogTitle, "批量上传完成。", "确定");
                }
                else
                {
                    progressWindow.EndSessionFailed(string.Format(
                        "部分失败：{0}/{1} 个装扮未能上传；成功的已写入映射，稍后可重新点批量上传继续处理失败装扮。",
                        lastFailures.Count, plan.variants.Count));
                }
            }
            catch (OperationCanceledException)
            {
                if (progressWindow != null) progressWindow.EndSessionCancelled(null);
                throw;
            }
            catch (Exception ex)
            {
                if (progressWindow != null) progressWindow.EndSessionFailed(ex.Message);
                throw;
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
                    if (paramDefaultGuard != null)
                    {
                        paramDefaultGuard.Dispose();
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

        private static bool IsAccessoryOwnedByVariant(GameObject accessoryTarget, AvatarVariantEntry variant)
        {
            if (accessoryTarget == null || variant == null || variant.includedRoots == null)
            {
                return false;
            }

            foreach (var root in variant.includedRoots)
            {
                if (root == null)
                {
                    continue;
                }

                if (accessoryTarget == root || accessoryTarget.transform.IsChildOf(root.transform))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ValidateParameterBudget(AvatarVariantSwitchConfig cfg, ValidationReport report)
        {
            if (cfg == null || cfg.avatarDescriptor == null || cfg.AvatarRoot == null)
            {
                return;
            }

            var mergedParameters = new Dictionary<string, VRCExpressionParameters.Parameter>(StringComparer.Ordinal);

            var expressionParameters = cfg.avatarDescriptor.expressionParameters;
            if (expressionParameters != null && expressionParameters.parameters != null)
            {
                foreach (var parameter in expressionParameters.parameters)
                {
                    if (parameter == null || string.IsNullOrWhiteSpace(parameter.name))
                    {
                        continue;
                    }

                    mergedParameters[parameter.name] = CloneExpressionParameter(parameter);
                }
            }

            var allParameters = cfg.AvatarRoot.GetComponentsInChildren<ModularAvatarParameters>(true);
            foreach (var parameterComponent in allParameters)
            {
                if (parameterComponent == null || IsUnderMenuRoot(parameterComponent.gameObject, cfg) || parameterComponent.parameters == null)
                {
                    continue;
                }

                foreach (var parameter in parameterComponent.parameters)
                {
                    if (!TryConvertParameterConfig(parameter, out var converted))
                    {
                        continue;
                    }

                    mergedParameters[converted.name] = converted;
                }
            }

            foreach (var parameter in AvatarVariantMenuBuilder.BuildAllParameters(cfg))
            {
                if (!TryConvertParameterConfig(parameter, out var converted))
                {
                    continue;
                }

                mergedParameters[converted.name] = converted;
            }

            var temp = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            try
            {
                temp.parameters = mergedParameters.Values.ToArray();
                var totalCost = temp.CalcTotalCost();
                if (totalCost > VRCExpressionParameters.MAX_PARAMETER_COST)
                {
                    report.Errors.Add(string.Format(
                        "\u53c2\u6570\u9884\u7b97\u8d85\u51fa\u4e0a\u9650\uff1a{0}/{1} Synced Bits\u3002\u5982\u679c accessory \u8fc7\u591a\uff0c\u8bf7\u51cf\u5c11\u914d\u4ef6 toggle \u6570\u91cf\uff0c\u6216\u6539\u6210 local-only \u53c2\u6570\u3002",
                        totalCost,
                        VRCExpressionParameters.MAX_PARAMETER_COST));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        private static VRCExpressionParameters.Parameter CloneExpressionParameter(VRCExpressionParameters.Parameter parameter)
        {
            return new VRCExpressionParameters.Parameter
            {
                name = parameter.name,
                valueType = parameter.valueType,
                saved = parameter.saved,
                defaultValue = parameter.defaultValue,
                networkSynced = parameter.networkSynced
            };
        }

        private static bool TryConvertParameterConfig(ParameterConfig parameterConfig, out VRCExpressionParameters.Parameter converted)
        {
            converted = null;

            if (parameterConfig.internalParameter || parameterConfig.isPrefix || parameterConfig.syncType == ParameterSyncType.NotSynced)
            {
                return false;
            }

            var resolvedName = !string.IsNullOrWhiteSpace(parameterConfig.remapTo)
                ? parameterConfig.remapTo.Trim()
                : (parameterConfig.nameOrPrefix ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                return false;
            }

            converted = new VRCExpressionParameters.Parameter
            {
                name = resolvedName,
                saved = parameterConfig.saved,
                defaultValue = parameterConfig.defaultValue,
                networkSynced = !parameterConfig.localOnly
            };

            switch (parameterConfig.syncType)
            {
                case ParameterSyncType.Bool:
                    converted.valueType = VRCExpressionParameters.ValueType.Bool;
                    return true;
                case ParameterSyncType.Float:
                    converted.valueType = VRCExpressionParameters.ValueType.Float;
                    return true;
                case ParameterSyncType.Int:
                    converted.valueType = VRCExpressionParameters.ValueType.Int;
                    return true;
                default:
                    return false;
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

        private static Texture2D ResolveVariantThumbnail(AvatarVariantSwitchConfig cfg, AvatarVariantEntry variant)
        {
            if (variant != null && variant.thumbnail != null)
            {
                return variant.thumbnail;
            }

            return cfg != null ? cfg.legacyThumbnail : null;
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

        private static string ResolveUploadedDescription(BatchPlan plan, BatchVariantPlan variant)
        {
            if (!string.IsNullOrWhiteSpace(variant.uploadedDescription))
            {
                return variant.uploadedDescription.Trim();
            }

            return plan.legacyUploadedAvatarDescription ?? string.Empty;
        }

        private static string ResolveExistingBlueprintId(AvatarVariantMap map, string variantKey, int paramValue, string legacyUploadedBlueprintId)
        {
            if (map != null)
            {
                var existing = map.FindByKeyOrParam(variantKey, paramValue);
                if (existing != null && !string.IsNullOrWhiteSpace(existing.blueprintId))
                {
                    return existing.blueprintId;
                }
            }

            return legacyUploadedBlueprintId ?? string.Empty;
        }

        private static bool IsThumbnailAlreadyUploadedError(Exception ex)
        {
            if (ex == null)
            {
                return false;
            }

            var hasMessage = false;
            for (var e = ex; e != null; e = e.InnerException)
            {
                var msg = e.Message ?? string.Empty;
                if (msg.IndexOf("already uploaded", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hasMessage = true;
                    break;
                }
            }
            if (!hasMessage)
            {
                return false;
            }

            // 用 ToString() 把异常链里所有 stack 拼起来检查；只接受确实从 UpdateAvatarImage
            // 这条路径上来的 "already uploaded"，避免误吞 bundle 上传的同名错误。
            var fullStack = ex.ToString() ?? string.Empty;
            return fullStack.IndexOf("UpdateAvatarImage", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private readonly struct FailureRecord
        {
            public readonly int OriginalIndex;
            public readonly string DisplayName;
            public readonly string ErrorMessage;

            public FailureRecord(int originalIndex, string displayName, string errorMessage)
            {
                OriginalIndex = originalIndex;
                DisplayName = displayName ?? string.Empty;
                ErrorMessage = errorMessage ?? string.Empty;
            }
        }

        private static string TruncateForDialog(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            const int limit = 120;
            if (s.Length <= limit) return s;
            return s.Substring(0, limit) + "…";
        }

        // 根据异常链里的关键字给出比 "An error occurred while sending the request"
        // 更能落到用户身上的错误描述。VRChat 的上传分两条线：业务 API 走
        // api.vrchat.cloud（Cloudflare），文件实体走 s3.us-east-1.amazonaws.com。
        // 国内用户最常踩的坑是代理/梯子只分流了 VRChat 的域名、没把 AWS 加进去，
        // 导致 S3 那条 TLS 握手直接被掐。
        private static string ClassifyErrorForUser(Exception ex)
        {
            if (ex == null) return string.Empty;

            var full = ex.ToString() ?? string.Empty;
            var raw = ex.Message ?? string.Empty;

            var hitS3 = full.IndexOf("amazonaws.com", StringComparison.OrdinalIgnoreCase) >= 0
                        || full.IndexOf("failed to upload Signature", StringComparison.OrdinalIgnoreCase) >= 0
                        || full.IndexOf("New image url is empty", StringComparison.OrdinalIgnoreCase) >= 0;

            var hitTls = full.IndexOf("SecureChannelFailure", StringComparison.OrdinalIgnoreCase) >= 0
                         || full.IndexOf("transport stream", StringComparison.OrdinalIgnoreCase) >= 0
                         || full.IndexOf("schannel", StringComparison.OrdinalIgnoreCase) >= 0
                         || full.IndexOf("handshake", StringComparison.OrdinalIgnoreCase) >= 0
                         || full.IndexOf("SSL", StringComparison.Ordinal) >= 0;

            if (hitS3 && hitTls)
            {
                return "【网络】连 AWS S3（us-east-1）的 TLS 握手失败——代理/梯子大概率没把 *.amazonaws.com 加进分流。原始错误：" + TruncateForDialog(raw);
            }
            if (hitS3)
            {
                return "【网络】连 AWS S3 出错——代理/梯子检查一下 *.amazonaws.com 的分流规则。原始错误：" + TruncateForDialog(raw);
            }
            if (hitTls)
            {
                return "【网络】TLS 握手失败——代理/梯子或系统网络问题。原始错误：" + TruncateForDialog(raw);
            }
            return TruncateForDialog(raw);
        }

        // 任何一个失败记录的文案里带了 "【网络】" 标记，就说明至少有一条是 S3/TLS 问题。
        private static bool AnyLooksLikeNetworkFailure(IList<FailureRecord> failures)
        {
            if (failures == null) return false;
            foreach (var f in failures)
            {
                var msg = f.ErrorMessage ?? string.Empty;
                if (msg.IndexOf("【网络】", StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static string FormatFailureDialogMessage(IList<FailureRecord> failures)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("以下 ").Append(failures.Count).AppendLine(" 个装扮上传失败：");
            sb.AppendLine();
            foreach (var f in failures)
            {
                sb.Append("• ").Append(string.IsNullOrWhiteSpace(f.DisplayName) ? "未命名装扮" : f.DisplayName);
                if (!string.IsNullOrWhiteSpace(f.ErrorMessage))
                {
                    sb.Append("  — ").Append(f.ErrorMessage);
                }
                sb.AppendLine();
            }
            sb.AppendLine();

            if (AnyLooksLikeNetworkFailure(failures))
            {
                sb.AppendLine("排查建议（90% 以上失败是这个）：");
                sb.AppendLine("· VRChat 上传分两条线：业务走 api.vrchat.cloud，文件走 s3.us-east-1.amazonaws.com。");
                sb.AppendLine("· 代理/梯子的分流规则一般只包含 VRChat 域名，不包含 AWS——检查把 *.amazonaws.com 也加进去。");
                sb.AppendLine("· 或换个代理节点（美东/日本通常对 S3 美东更稳），再点重试。");
                sb.AppendLine();
            }

            sb.Append("是否重试这 ").Append(failures.Count).Append(" 个？已成功的装扮不会再次上传。");
            return sb.ToString();
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

            foreach (var menuRoot in FindMenuRoots(cfg.AvatarRoot.transform))
            {
                if (candidate.transform == menuRoot || candidate.transform.IsChildOf(menuRoot))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<Transform> FindMenuRoots(Transform avatarRoot)
        {
            if (avatarRoot == null)
            {
                yield break;
            }

            var menuRoot = avatarRoot.Find(GeneratedMenuRootName);
            if (menuRoot != null)
            {
                yield return menuRoot;
            }

            var legacyMenuRoot = avatarRoot.Find("_AvatarVariantMenu");
            if (legacyMenuRoot != null)
            {
                yield return legacyMenuRoot;
            }
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
            public readonly string legacyUploadedAvatarDescription;
            public readonly List<BatchVariantPlan> variants;

            private BatchPlan(
                GameObject avatarRoot,
                string parameterName,
                string menuName,
                int defaultValue,
                ReleaseStatus releaseStatus,
                string outputMapPath,
                string uploadedAvatarNamePrefix,
                string legacyUploadedAvatarDescription,
                List<BatchVariantPlan> variants)
            {
                this.avatarRoot = avatarRoot;
                this.parameterName = parameterName;
                this.menuName = menuName;
                this.defaultValue = defaultValue;
                this.releaseStatus = releaseStatus;
                this.outputMapPath = outputMapPath;
                this.uploadedAvatarNamePrefix = uploadedAvatarNamePrefix;
                this.legacyUploadedAvatarDescription = legacyUploadedAvatarDescription;
                this.variants = variants;
            }

            public static BatchPlan Snapshot(AvatarVariantSwitchConfig cfg)
            {
                var variants = new List<BatchVariantPlan>();
                foreach (var variant in cfg.variants)
                {
                    var thumbnailAsset = ResolveVariantThumbnail(cfg, variant);
                    variants.Add(new BatchVariantPlan(
                        variant.displayName ?? string.Empty,
                        variant.variantKey ?? string.Empty,
                        variant.paramValue,
                        ResolveThumbnailPath(thumbnailAsset, GetVariantLabel(variant, variants.Count)),
                        thumbnailAsset,
                        variant.uploadedName ?? string.Empty,
                        variant.uploadedDescription ?? string.Empty,
                        variant.legacyUploadedBlueprintId ?? string.Empty,
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
                    cfg.legacyUploadedAvatarDescription ?? string.Empty,
                    variants);
            }
        }

        private sealed class BatchVariantPlan
        {
            public readonly string displayName;
            public readonly string variantKey;
            public readonly int paramValue;
            public readonly string thumbnailPath;
            public readonly Texture2D thumbnailAsset;
            public readonly string uploadedName;
            public readonly string uploadedDescription;
            public readonly string legacyUploadedBlueprintId;
            public readonly List<GameObject> includedRoots;

            public BatchVariantPlan(
                string displayName,
                string variantKey,
                int paramValue,
                string thumbnailPath,
                Texture2D thumbnailAsset,
                string uploadedName,
                string uploadedDescription,
                string legacyUploadedBlueprintId,
                List<GameObject> includedRoots)
            {
                this.displayName = displayName;
                this.variantKey = variantKey;
                this.paramValue = paramValue;
                this.thumbnailPath = thumbnailPath;
                this.thumbnailAsset = thumbnailAsset;
                this.uploadedName = uploadedName;
                this.uploadedDescription = uploadedDescription;
                this.legacyUploadedBlueprintId = legacyUploadedBlueprintId;
                this.includedRoots = includedRoots;
            }
        }
    }
}
