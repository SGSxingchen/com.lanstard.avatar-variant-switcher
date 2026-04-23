using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    /// <summary>
    /// 负责把 OSC 桥从 GitHub Release 拉下来、校验、启动。
    /// 单独一个类，和 Inspector 解耦，方便以后也从菜单项或其他入口调用。
    /// </summary>
    internal static class AvatarVariantBridgeLauncher
    {
        // Inspector 代码兜底常量；优先走 package.json 的 repository.url。
        private const string DefaultRepoSlug = "SGSxingchen/com.lanstard.avatar-variant-switcher";

        private const string AssetNamePattern = "AvatarVariantOscBridge-{0}-win-x64.exe";
        private const string PackageFolder = "com.lanstard.avatar-variant-switcher";

        private static string CacheDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LanstardAvatarVariantBridge");

        private static string BridgeExePath => Path.Combine(CacheDirectory, "bridge.exe");
        private static string VersionPath => Path.Combine(CacheDirectory, "version.txt");

        /// <summary>
        /// 按钮入口：全链路跑完。失败时用 EditorUtility.DisplayDialog 报告。
        /// </summary>
        public static void DownloadAndLaunch(string absoluteMappingPath)
        {
            if (string.IsNullOrWhiteSpace(absoluteMappingPath) || !File.Exists(absoluteMappingPath))
            {
                EditorUtility.DisplayDialog(
                    "启动 OSC 桥",
                    "映射文件还不存在。请先至少执行一次 [批量上传所有装扮] 或 [写入映射文件]，生成 avatar-switch-map.json。",
                    "确定");
                return;
            }

            string exePath = null;
            try
            {
                EditorUtility.DisplayProgressBar("OSC 桥", "检查更新...", 0.1f);
                exePath = EnsureLatestExe();
            }
            catch (Exception ex)
            {
                if (File.Exists(BridgeExePath))
                {
                    Debug.LogWarning($"[AvatarVariantSwitcher] 未能检查更新，使用本地已有版本。原因：{ex.Message}");
                    exePath = BridgeExePath;
                }
                else
                {
                    EditorUtility.ClearProgressBar();
                    var repoUrl = $"https://github.com/{ResolveRepoSlug()}/releases/latest";
                    var click = EditorUtility.DisplayDialogComplex(
                        "启动 OSC 桥",
                        $"下载桥 exe 失败：{ex.Message}\n\n请检查网络，或手动打开 Release 页面下载。",
                        "打开 Release 页面",
                        "取消",
                        "重试");
                    switch (click)
                    {
                        case 0: Application.OpenURL(repoUrl); return;
                        case 2: DownloadAndLaunch(absoluteMappingPath); return;
                        default: return;
                    }
                }
            }

            try
            {
                EditorUtility.DisplayProgressBar("OSC 桥", "启动...", 0.9f);
                LaunchBridge(exePath, absoluteMappingPath);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    "启动 OSC 桥",
                    $"启动进程失败：{ex.Message}\n\nexe 路径：{exePath}",
                    "确定");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// 保证 CacheDirectory 里的 bridge.exe 是 latest release 的版本。
        /// 返回 exe 绝对路径。失败时抛异常，由调用方决定回退策略。
        /// </summary>
        private static string EnsureLatestExe()
        {
            Directory.CreateDirectory(CacheDirectory);

            var latest = QueryLatestRelease();
            var localVersion = File.Exists(VersionPath) ? File.ReadAllText(VersionPath).Trim() : null;

            if (File.Exists(BridgeExePath) && string.Equals(localVersion, latest.TagName, StringComparison.Ordinal))
            {
                return BridgeExePath; // 已是最新
            }

            EditorUtility.DisplayProgressBar("OSC 桥", $"下载 {latest.TagName}...", 0.4f);
            var tmp = BridgeExePath + ".tmp";
            DownloadFile(latest.ExeUrl, tmp);

            EditorUtility.DisplayProgressBar("OSC 桥", "校验 SHA256...", 0.8f);
            var shaResponse = DownloadString(latest.ShaUrl);
            var expectedSha = shaResponse
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0]
                .Trim().ToLowerInvariant();
            var actualSha = ComputeSha256(tmp);
            if (!string.Equals(expectedSha, actualSha, StringComparison.Ordinal))
            {
                File.Delete(tmp);
                throw new InvalidDataException($"SHA256 mismatch. expected={expectedSha} actual={actualSha}");
            }

            if (File.Exists(BridgeExePath))
                File.Replace(tmp, BridgeExePath, null);
            else
                File.Move(tmp, BridgeExePath);

            File.WriteAllText(VersionPath, latest.TagName);
            return BridgeExePath;
        }

        private static ReleaseInfo QueryLatestRelease()
        {
            var slug = ResolveRepoSlug();
            var url = $"https://api.github.com/repos/{slug}/releases/latest";
            var json = DownloadString(url, timeoutSeconds: 5);

            var response = JsonUtility.FromJson<GithubReleaseResponse>(json);
            if (response == null || string.IsNullOrWhiteSpace(response.tag_name))
                throw new InvalidDataException("GitHub release 响应缺少 tag_name");

            var tag = response.tag_name;
            var exeName = string.Format(AssetNamePattern, tag);
            string exeUrl = null, shaUrl = null;
            if (response.assets != null)
            {
                foreach (var asset in response.assets)
                {
                    if (asset == null) continue;
                    if (string.Equals(asset.name, exeName, StringComparison.Ordinal))
                        exeUrl = asset.browser_download_url;
                    else if (string.Equals(asset.name, exeName + ".sha256", StringComparison.Ordinal))
                        shaUrl = asset.browser_download_url;
                }
            }

            if (exeUrl == null || shaUrl == null)
                throw new InvalidDataException($"Release {tag} 不含期望的 asset（{exeName} 或 .sha256）");

            return new ReleaseInfo(tag, exeUrl, shaUrl);
        }

        private static void LaunchBridge(string exePath, string mappingPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--map \"{mappingPath}\"",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            };
            using var proc = Process.Start(psi);
            // 不保留引用，不 Wait，关 Unity 不影响桥。
        }

        private static string ResolveRepoSlug()
        {
            try
            {
                var packagePath = PackageJsonPath();
                if (packagePath != null && File.Exists(packagePath))
                {
                    var json = File.ReadAllText(packagePath);
                    var pkg = JsonUtility.FromJson<PackageJsonFile>(json);
                    if (pkg?.repository != null)
                    {
                        var slug = ParseSlug(pkg.repository.url);
                        if (!string.IsNullOrWhiteSpace(slug) && !slug.Contains("<owner>"))
                            return slug;
                    }
                }
            }
            catch { /* fall through */ }

            return DefaultRepoSlug;
        }

        private static string PackageJsonPath()
        {
            var candidates = new List<string>
            {
                Path.Combine("Packages", PackageFolder, "package.json"),
                Path.Combine("Packages", "package.json"),
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
        }

        private static string ParseSlug(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var trimmed = url.Trim();
            var sshPrefix = "git@github.com:";
            var httpsPrefix = "https://github.com/";
            string body;
            if (trimmed.StartsWith(sshPrefix, StringComparison.OrdinalIgnoreCase))
                body = trimmed.Substring(sshPrefix.Length);
            else if (trimmed.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
                body = trimmed.Substring(httpsPrefix.Length);
            else
                return null;
            if (body.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                body = body.Substring(0, body.Length - 4);
            var parts = body.Split('/');
            return parts.Length == 2 ? $"{parts[0]}/{parts[1]}" : null;
        }

        private static string ComputeSha256(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string DownloadString(string url, int timeoutSeconds = 30)
        {
            using var http = CreateHttpClient(timeoutSeconds);
            return http.GetStringAsync(url).GetAwaiter().GetResult();
        }

        private static void DownloadFile(string url, string destPath)
        {
            using var http = CreateHttpClient(timeoutSeconds: 120);
            using var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var src = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var dst = File.Create(destPath);
            src.CopyTo(dst);
        }

        private static HttpClient CreateHttpClient(int timeoutSeconds)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            // GitHub API 强制要求 User-Agent。
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LanstardAvatarVariantSwitcher", "0.1"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return http;
        }

        internal static void ClearCache()
        {
            try
            {
                if (File.Exists(BridgeExePath)) File.Delete(BridgeExePath);
                if (File.Exists(VersionPath)) File.Delete(VersionPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AvatarVariantSwitcher] 清除缓存失败：{ex.Message}");
            }
        }

        internal static string GetCacheDirectoryForDisplay() => CacheDirectory;

        private readonly struct ReleaseInfo
        {
            public readonly string TagName;
            public readonly string ExeUrl;
            public readonly string ShaUrl;
            public ReleaseInfo(string tag, string exe, string sha) { TagName = tag; ExeUrl = exe; ShaUrl = sha; }
        }

        // Unity JsonUtility 只处理 [Serializable] 的 public 字段（不是属性）。
        // GitHub API 返回的字段名必须精确匹配。
        [Serializable]
        private class GithubReleaseResponse
        {
            public string tag_name;
            public GithubReleaseAsset[] assets;
        }

        [Serializable]
        private class GithubReleaseAsset
        {
            public string name;
            public string browser_download_url;
        }

        [Serializable]
        private class PackageJsonFile
        {
            public PackageJsonRepository repository;
        }

        [Serializable]
        private class PackageJsonRepository
        {
            public string url;
        }
    }
}
