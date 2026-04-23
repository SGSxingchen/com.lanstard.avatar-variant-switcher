using System;
using System.Reflection;
using UnityEditor;
using UnityEngine.UIElements;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Elements;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    internal sealed class AvatarVariantSdkModalGuard : IDisposable
    {
        private const string CopyrightAgreementTitle = "Copyright ownership agreement";
        private static readonly MethodInfo ClickableInvokeMethod = typeof(Clickable).GetMethod(
            "Invoke",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(EventBase) },
            null);

        private readonly Action<string> _log;
        private bool _disposed;

        private AvatarVariantSdkModalGuard(Action<string> log)
        {
            _log = log;
            EditorApplication.update += OnEditorUpdate;
        }

        public static AvatarVariantSdkModalGuard Start(Action<string> log = null)
        {
            return new AvatarVariantSdkModalGuard(log);
        }

        private void OnEditorUpdate()
        {
            try
            {
                TryAutoConfirmCopyrightAgreement();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
            }
        }

        private void TryAutoConfirmCopyrightAgreement()
        {
            var root = VRCSdkControlPanel.window?.rootVisualElement;
            if (root == null)
            {
                return;
            }

            if (!TryFindOpenCopyrightAgreementModal(root, out var modal))
            {
                return;
            }

            var actionButton = modal.Q<Button>("modal-action-button");
            if (actionButton == null || actionButton.clickable == null)
            {
                return;
            }

            if (ClickableInvokeMethod == null)
            {
                return;
            }

            ClickableInvokeMethod.Invoke(actionButton.clickable, new object[] { null });
            _log?.Invoke("检测到 VRChat SDK 的版权确认弹窗，已自动点击 OK。");
        }

        private static bool TryFindOpenCopyrightAgreementModal(VisualElement root, out Modal modal)
        {
            if (root is Modal candidate && candidate.IsOpen && IsCopyrightAgreementModal(candidate))
            {
                modal = candidate;
                return true;
            }

            foreach (var child in root.Children())
            {
                if (child == null)
                {
                    continue;
                }

                if (TryFindOpenCopyrightAgreementModal(child, out modal))
                {
                    return true;
                }
            }

            modal = null;
            return false;
        }

        private static bool IsCopyrightAgreementModal(Modal modal)
        {
            if (modal == null)
            {
                return false;
            }

            var title = modal.Q<Label>("modal-title");
            return string.Equals(title?.text, CopyrightAgreementTitle, StringComparison.Ordinal);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            EditorApplication.update -= OnEditorUpdate;
            _disposed = true;
        }
    }
}
