using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    public sealed class AvatarVariantUploadProgressWindow : EditorWindow
    {
        public enum VariantStatus
        {
            Pending,
            Uploading,
            Success,
            Failed
        }

        public readonly struct VariantPlanItem
        {
            public readonly string DisplayName;
            public readonly string VariantKey;
            public readonly Texture2D Thumbnail;

            public VariantPlanItem(string displayName, string variantKey, Texture2D thumbnail)
            {
                DisplayName = displayName;
                VariantKey = variantKey;
                Thumbnail = thumbnail;
            }
        }

        private sealed class VariantRow
        {
            public string DisplayName;
            public string VariantKey;
            public Texture2D Thumbnail;
            public VariantStatus Status;
            public string BlueprintId;
            public string ErrorMessage;
            public double StartedAt;
            public double FinishedAt;
        }

        private readonly List<VariantRow> _rows = new List<VariantRow>();
        private readonly List<string> _logs = new List<string>();
        private const int MaxLogLines = 200;
        private int _currentIndex = -1;
        private bool _cancelRequested;
        private bool _sessionEnded;
        private string _finalMessage;
        private MessageType _finalSeverity = MessageType.Info;
        private Vector2 _variantsScroll;
        private Vector2 _logScroll;
        private double _lastRepaint;

        private static GUIStyle _labelBold;
        private static GUIStyle _labelSub;
        private static GUIStyle _logStyle;
        private static GUIStyle _cellHeaderStyle;
        private static Texture2D _stripeTex;
        private static Texture2D _rowBgActive;
        private static Texture2D _rowBgSuccess;
        private static Texture2D _rowBgFailed;

        public bool CancelRequested { get { return _cancelRequested; } }
        public bool SessionEnded { get { return _sessionEnded; } }

        public static AvatarVariantUploadProgressWindow ShowAndBegin(IList<VariantPlanItem> plan)
        {
            var window = GetWindow<AvatarVariantUploadProgressWindow>(false, "Avatar 装扮批量上传", true);
            window.minSize = new Vector2(500f, 520f);
            window.Reset(plan);
            window.Show();
            window.Focus();
            window.Log(string.Format("开始批量上传（{0} 个装扮）。", plan == null ? 0 : plan.Count));
            return window;
        }

        public void MarkBegin(int index)
        {
            if (index < 0 || index >= _rows.Count) return;
            _currentIndex = index;
            var row = _rows[index];
            row.Status = VariantStatus.Uploading;
            row.StartedAt = EditorApplication.timeSinceStartup;
            row.FinishedAt = 0;
            row.BlueprintId = null;
            row.ErrorMessage = null;
            Log(string.Format("[{0}] 开始上传…", row.DisplayName));
            RequestRepaint();
        }

        public void MarkSuccess(int index, string blueprintId)
        {
            if (index < 0 || index >= _rows.Count) return;
            var row = _rows[index];
            row.Status = VariantStatus.Success;
            row.FinishedAt = EditorApplication.timeSinceStartup;
            row.BlueprintId = blueprintId ?? string.Empty;
            Log(string.Format("[{0}] 上传完成（{1}，耗时 {2}）。", row.DisplayName, string.IsNullOrEmpty(blueprintId) ? "无 ID" : blueprintId, FormatElapsed(row.FinishedAt - row.StartedAt)));
            RequestRepaint();
        }

        public void MarkFailure(int index, string message)
        {
            if (index < 0 || index >= _rows.Count) return;
            var row = _rows[index];
            row.Status = VariantStatus.Failed;
            row.FinishedAt = EditorApplication.timeSinceStartup;
            row.ErrorMessage = message ?? string.Empty;
            Log(string.Format("[{0}] 上传失败：{1}", row.DisplayName, row.ErrorMessage));
            RequestRepaint();
        }

        public void EndSessionSuccess(string message)
        {
            _sessionEnded = true;
            _finalMessage = string.IsNullOrEmpty(message) ? "批量上传完成。" : message;
            _finalSeverity = MessageType.Info;
            Log(_finalMessage);
            RequestRepaint();
        }

        public void EndSessionCancelled(string message)
        {
            _sessionEnded = true;
            _finalMessage = string.IsNullOrEmpty(message) ? "用户已取消批量上传。" : message;
            _finalSeverity = MessageType.Warning;
            Log(_finalMessage);
            RequestRepaint();
        }

        public void EndSessionFailed(string message)
        {
            _sessionEnded = true;
            _finalMessage = string.IsNullOrEmpty(message) ? "批量上传失败。" : message;
            _finalSeverity = MessageType.Error;
            Log(_finalMessage);
            RequestRepaint();
        }

        public void PrepareRetry(IList<int> indicesToRetry)
        {
            // 清掉"会话已结束"的视觉状态，让窗口回到"进行中"模式——取消按钮能重新按、底部
            // HelpBox 消失，_currentIndex 也复位（下一次 MarkBegin 会给它赋值）。
            _sessionEnded = false;
            _cancelRequested = false;
            _finalMessage = null;
            _finalSeverity = MessageType.Info;
            _currentIndex = -1;

            var resetCount = 0;
            if (indicesToRetry != null)
            {
                foreach (var idx in indicesToRetry)
                {
                    if (idx < 0 || idx >= _rows.Count) continue;
                    var row = _rows[idx];
                    row.Status = VariantStatus.Pending;
                    row.BlueprintId = null;
                    row.ErrorMessage = null;
                    row.StartedAt = 0;
                    row.FinishedAt = 0;
                    resetCount++;
                }
            }

            Log(string.Format("开始重试 {0} 个失败装扮。", resetCount));
            RequestRepaint();
        }

        public void Log(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var stamped = string.Format("{0:HH:mm:ss}  {1}", DateTime.Now, line);
            _logs.Add(stamped);
            if (_logs.Count > MaxLogLines)
            {
                _logs.RemoveRange(0, _logs.Count - MaxLogLines);
            }
            _logScroll.y = float.MaxValue;
            RequestRepaint();
        }

        private void Reset(IList<VariantPlanItem> plan)
        {
            _rows.Clear();
            _logs.Clear();
            _currentIndex = -1;
            _cancelRequested = false;
            _sessionEnded = false;
            _finalMessage = null;
            _finalSeverity = MessageType.Info;
            _variantsScroll = Vector2.zero;
            _logScroll = Vector2.zero;

            if (plan != null)
            {
                for (var i = 0; i < plan.Count; i++)
                {
                    var item = plan[i];
                    _rows.Add(new VariantRow
                    {
                        DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? "未命名装扮" : item.DisplayName,
                        VariantKey = item.VariantKey ?? string.Empty,
                        Thumbnail = item.Thumbnail,
                        Status = VariantStatus.Pending
                    });
                }
            }
        }

        private void OnEnable()
        {
            EditorApplication.update += OnUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnUpdate;
            DestroyTextures();
        }

        private void OnUpdate()
        {
            // 上传中的装扮：每 0.25s Repaint，好让耗时计数器实时走字。
            if (_currentIndex >= 0 && _currentIndex < _rows.Count && _rows[_currentIndex].Status == VariantStatus.Uploading)
            {
                if (EditorApplication.timeSinceStartup - _lastRepaint > 0.25)
                {
                    _lastRepaint = EditorApplication.timeSinceStartup;
                    Repaint();
                }
            }
        }

        private void RequestRepaint()
        {
            _lastRepaint = EditorApplication.timeSinceStartup;
            Repaint();
        }

        private void OnGUI()
        {
            EnsureStyles();

            DrawHeader();
            EditorGUILayout.Space();
            DrawVariantsList();
            EditorGUILayout.Space();
            DrawLog();
            EditorGUILayout.Space();
            DrawFooter();
        }

        private void DrawHeader()
        {
            var completed = 0;
            var failed = 0;
            foreach (var row in _rows)
            {
                if (row.Status == VariantStatus.Success) completed++;
                else if (row.Status == VariantStatus.Failed) failed++;
            }

            var total = _rows.Count;
            var progress = total == 0 ? 0f : (float)(completed + failed) / total;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Avatar 装扮批量上传", _labelBold);
                    GUILayout.FlexibleSpace();

                    string statusText;
                    if (_sessionEnded)
                    {
                        statusText = "已结束";
                    }
                    else if (_cancelRequested)
                    {
                        statusText = "正在取消…";
                    }
                    else if (_currentIndex >= 0 && _currentIndex < _rows.Count)
                    {
                        statusText = string.Format("{0}/{1} · {2}", _currentIndex + 1, total, _rows[_currentIndex].DisplayName);
                    }
                    else
                    {
                        statusText = string.Format("{0}/{1}", completed + failed, total);
                    }
                    GUILayout.Label(statusText, _labelSub);
                }

                var rect = GUILayoutUtility.GetRect(0, 18, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(rect, progress, string.Format("{0:0%} · 完成 {1} · 失败 {2}", progress, completed, failed));

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(_sessionEnded || _cancelRequested))
                    {
                        if (GUILayout.Button("取消", GUILayout.Width(80)))
                        {
                            if (EditorUtility.DisplayDialog("Avatar 装扮切换器", "确认取消正在进行的批量上传吗？当前装扮的 SDK 上传阶段可能无法立刻中断。", "取消上传", "继续"))
                            {
                                _cancelRequested = true;
                                Log("收到取消请求。");
                                RequestRepaint();
                            }
                        }
                    }
                    if (_sessionEnded)
                    {
                        if (GUILayout.Button("关闭窗口", GUILayout.Width(80)))
                        {
                            Close();
                        }
                    }
                }
            }
        }

        private void DrawVariantsList()
        {
            GUILayout.Label("装扮状态", _cellHeaderStyle);

            _variantsScroll = EditorGUILayout.BeginScrollView(_variantsScroll, GUILayout.ExpandHeight(true));

            for (var i = 0; i < _rows.Count; i++)
            {
                DrawVariantRow(i, _rows[i]);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawVariantRow(int index, VariantRow row)
        {
            var rect = GUILayoutUtility.GetRect(0, 74, GUILayout.ExpandWidth(true));
            var bg = ResolveRowBackground(row.Status);
            if (bg != null)
            {
                GUI.DrawTexture(rect, bg, ScaleMode.StretchToFill);
            }

            var padded = new Rect(rect.x + 6, rect.y + 4, rect.width - 12, rect.height - 8);

            var thumbRect = new Rect(padded.x, padded.y, 64, 64);
            if (row.Thumbnail != null)
            {
                GUI.DrawTexture(thumbRect, row.Thumbnail, ScaleMode.ScaleToFit);
            }
            else
            {
                EditorGUI.DrawRect(thumbRect, new Color(0.15f, 0.15f, 0.15f, 1f));
                GUI.Label(thumbRect, "no img", _labelSub);
            }

            var textRect = new Rect(padded.x + 72, padded.y, padded.width - 72, padded.height);
            var titleRect = new Rect(textRect.x, textRect.y, textRect.width, 18);
            GUI.Label(titleRect, string.Format("{0}. {1}", index + 1, row.DisplayName), _labelBold);

            var statusRect = new Rect(textRect.x, textRect.y + 20, textRect.width, 18);
            GUI.Label(statusRect, ResolveStatusLine(row), EditorStyles.label);

            var detailRect = new Rect(textRect.x, textRect.y + 40, textRect.width, 18);
            GUI.Label(detailRect, ResolveDetailLine(row), _labelSub);
        }

        private string ResolveStatusLine(VariantRow row)
        {
            switch (row.Status)
            {
                case VariantStatus.Pending:
                    return "· 等待中";
                case VariantStatus.Uploading:
                    var elapsed = EditorApplication.timeSinceStartup - row.StartedAt;
                    return string.Format("· 上传中  耗时 {0}", FormatElapsed(elapsed));
                case VariantStatus.Success:
                    return string.Format("✔ 已完成  耗时 {0}", FormatElapsed(row.FinishedAt - row.StartedAt));
                case VariantStatus.Failed:
                    return "✘ 失败";
                default:
                    return string.Empty;
            }
        }

        private static string ResolveDetailLine(VariantRow row)
        {
            if (row.Status == VariantStatus.Success && !string.IsNullOrEmpty(row.BlueprintId))
            {
                return row.BlueprintId;
            }

            if (row.Status == VariantStatus.Failed && !string.IsNullOrEmpty(row.ErrorMessage))
            {
                return row.ErrorMessage;
            }

            if (!string.IsNullOrEmpty(row.VariantKey))
            {
                var key = row.VariantKey.Length > 12 ? row.VariantKey.Substring(0, 12) : row.VariantKey;
                return string.Format("key: {0}", key);
            }

            return string.Empty;
        }

        private Texture2D ResolveRowBackground(VariantStatus status)
        {
            switch (status)
            {
                case VariantStatus.Uploading:
                    return _rowBgActive;
                case VariantStatus.Success:
                    return _rowBgSuccess;
                case VariantStatus.Failed:
                    return _rowBgFailed;
                default:
                    return _stripeTex;
            }
        }

        private void DrawLog()
        {
            GUILayout.Label("日志", _cellHeaderStyle);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(120));
            foreach (var line in _logs)
            {
                GUILayout.Label(line, _logStyle);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawFooter()
        {
            if (_sessionEnded && !string.IsNullOrEmpty(_finalMessage))
            {
                EditorGUILayout.HelpBox(_finalMessage, _finalSeverity);
            }
        }

        private static string FormatElapsed(double seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds < 60) return string.Format("{0:0.0}s", seconds);
            var minutes = (int)(seconds / 60);
            var rest = seconds - minutes * 60;
            return string.Format("{0}m{1:00}s", minutes, (int)rest);
        }

        private static void EnsureStyles()
        {
            if (_labelBold == null)
            {
                _labelBold = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 13
                };
            }
            if (_labelSub == null)
            {
                _labelSub = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 10,
                    wordWrap = false
                };
            }
            if (_logStyle == null)
            {
                _logStyle = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 11,
                    richText = false,
                    wordWrap = true
                };
            }
            if (_cellHeaderStyle == null)
            {
                _cellHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 12
                };
            }
            if (_stripeTex == null) _stripeTex = MakeSolidTexture(new Color(0.18f, 0.18f, 0.20f, 1f));
            if (_rowBgActive == null) _rowBgActive = MakeSolidTexture(new Color(0.22f, 0.30f, 0.42f, 1f));
            if (_rowBgSuccess == null) _rowBgSuccess = MakeSolidTexture(new Color(0.20f, 0.33f, 0.22f, 1f));
            if (_rowBgFailed == null) _rowBgFailed = MakeSolidTexture(new Color(0.42f, 0.22f, 0.22f, 1f));
        }

        private static Texture2D MakeSolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        private static void DestroyTextures()
        {
            if (_stripeTex != null) { UnityEngine.Object.DestroyImmediate(_stripeTex); _stripeTex = null; }
            if (_rowBgActive != null) { UnityEngine.Object.DestroyImmediate(_rowBgActive); _rowBgActive = null; }
            if (_rowBgSuccess != null) { UnityEngine.Object.DestroyImmediate(_rowBgSuccess); _rowBgSuccess = null; }
            if (_rowBgFailed != null) { UnityEngine.Object.DestroyImmediate(_rowBgFailed); _rowBgFailed = null; }
        }
    }
}
