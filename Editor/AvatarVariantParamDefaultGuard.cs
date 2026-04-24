using System;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    /// <summary>
    /// 批量上传期间临时把 _AvatarSwitcherMenu/ModularAvatarParameters 里
    /// AvatarVariant 那一项的 defaultValue 改成当前 variant 的 paramValue，
    /// 结束时（正常 / 取消 / 异常）还原到进入批处理前的值。
    ///
    /// 并列于 AvatarVariantTagGuard——各自管自己一类状态，互不依赖。
    /// </summary>
    public sealed class AvatarVariantParamDefaultGuard : IDisposable
    {
        private readonly ModularAvatarParameters _component;
        private readonly int _paramIndex;
        private readonly float _originalDefault;
        private bool _restored;

        private AvatarVariantParamDefaultGuard(
            ModularAvatarParameters component,
            int paramIndex,
            float originalDefault)
        {
            _component = component;
            _paramIndex = paramIndex;
            _originalDefault = originalDefault;
        }

        public static AvatarVariantParamDefaultGuard Capture(AvatarVariantSwitchConfig cfg)
        {
            if (cfg == null)
            {
                throw new ArgumentNullException("cfg");
            }
            if (cfg.AvatarRoot == null)
            {
                throw new InvalidOperationException("无法解析 Avatar Root。");
            }

            var menuRoot = cfg.AvatarRoot.transform.Find(AvatarVariantMenuBuilder.GeneratedMenuRootName);
            if (menuRoot == null)
            {
                throw new InvalidOperationException("找不到 _AvatarSwitcherMenu——批处理前 Generate 是否执行过？");
            }

            var component = menuRoot.GetComponent<ModularAvatarParameters>();
            if (component == null)
            {
                throw new InvalidOperationException("_AvatarSwitcherMenu 上缺少 ModularAvatarParameters 组件。");
            }

            var paramName = (cfg.parameterName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(paramName))
            {
                throw new InvalidOperationException("cfg.parameterName 为空。");
            }

            var list = component.parameters;
            if (list == null)
            {
                throw new InvalidOperationException("ModularAvatarParameters.parameters 为空。");
            }

            var index = -1;
            for (var i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (string.IsNullOrWhiteSpace(entry.nameOrPrefix))
                {
                    continue;
                }

                if (string.Equals(entry.nameOrPrefix.Trim(), paramName, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                throw new InvalidOperationException(
                    string.Format("在 ModularAvatarParameters.parameters 里找不到名为 \"{0}\" 的条目。", paramName));
            }

            return new AvatarVariantParamDefaultGuard(component, index, list[index].defaultValue);
        }

        public void SetDefault(int value)
        {
            if (_component == null)
            {
                return;
            }

            var list = _component.parameters;
            if (list == null || _paramIndex < 0 || _paramIndex >= list.Count)
            {
                return;
            }

            var entry = list[_paramIndex];
            var newDefault = (float)value;
            if (entry.defaultValue == newDefault)
            {
                return;
            }

            Undo.RecordObject(_component, "Set AvatarVariant default value");
            entry.defaultValue = newDefault;
            list[_paramIndex] = entry;
            EditorUtility.SetDirty(_component);
        }

        public void Restore()
        {
            if (_restored)
            {
                return;
            }

            if (_component == null)
            {
                _restored = true;
                return;
            }

            var list = _component.parameters;
            if (list != null && _paramIndex >= 0 && _paramIndex < list.Count)
            {
                var entry = list[_paramIndex];
                if (entry.defaultValue != _originalDefault)
                {
                    Undo.RecordObject(_component, "Restore AvatarVariant default value");
                    entry.defaultValue = _originalDefault;
                    list[_paramIndex] = entry;
                    EditorUtility.SetDirty(_component);
                }
            }

            _restored = true;
        }

        public void Dispose()
        {
            Restore();
        }
    }
}
