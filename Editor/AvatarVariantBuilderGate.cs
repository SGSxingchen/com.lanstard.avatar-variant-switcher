using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine.UIElements;
using VRC.Core;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    public static class AvatarVariantBuilderGate
    {
        public static async Task<IVRCSdkAvatarBuilderApi> AcquireAsync(CancellationToken ct)
        {
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
            for (var i = 0; i < 100; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder))
                {
                    if (!APIUser.IsLoggedIn)
                    {
                        throw new InvalidOperationException("请先在 VRChat SDK 面板登录。");
                    }

                    await WaitForBuilderPanelReadyAsync(ct);
                    return builder;
                }

                await Task.Delay(100, ct);
            }

            throw new InvalidOperationException("无法获取 VRChat Avatar Builder；请手动打开 SDK 面板。");
        }

        private static async Task WaitForBuilderPanelReadyAsync(CancellationToken ct)
        {
            for (var i = 0; i < 30; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (GetBuilderUploadSection() != null)
                {
                    return;
                }

                await Task.Delay(100, ct);
            }
        }

        private static VisualElement GetBuilderUploadSection()
        {
            return VRCSdkControlPanel.window?.rootVisualElement?.Q("builder-panel")?.Q("section-3");
        }
    }
}
