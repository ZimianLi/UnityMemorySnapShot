// 打包前检查：ABI / Development Build / 符号目录 / .so 是否齐全
// 只警告不阻断，避免影响正式出包。
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Elex.AllocHook.Editor
{
    public class AllocHookBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => 1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android) return;
            CheckInternal();
        }

        [MenuItem("Tools/AllocHook/输出接入检查")]
        public static void CheckMenu()
        {
            CheckInternal();
        }

        private static void CheckInternal()
        {
            Debug.Log("[AllocHook] ---- 接入检查 ----");

            var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android);
            var arch = PlayerSettings.Android.targetArchitectures;
            Debug.Log($"[AllocHook] backend={backend}, arch={arch}, development={EditorUserBuildSettings.development}");

            string pkgRoot = FindPackageRoot();
            if (pkgRoot == null)
            {
                Debug.LogWarning("[AllocHook] 找不到 com.elex.allochook 包，检查 manifest.json 里的本地包路径");
                return;
            }
            Debug.Log($"[AllocHook] package: {pkgRoot}");

            string plugins = Path.Combine(pkgRoot, "Plugins/Android");
            if (!Directory.Exists(plugins))
            {
                Debug.LogWarning("[AllocHook] 缺少 Plugins/Android 目录");
                return;
            }

            foreach (var dir in Directory.GetDirectories(plugins))
            {
                string so = Path.Combine(dir, "liballochook.so");
                if (!File.Exists(so)) { Debug.LogWarning($"[AllocHook] 缺失 {Path.GetFileName(dir)}\\liballochook.so"); continue; }

                // 关键：.so 必须勾选 Android 平台，否则会被打包流程完全忽略（只勾 Any Platform 是无效的）
                string abi = Path.GetFileName(dir);
                string assetPath = $"Packages/com.elex.allochook/Plugins/Android/{abi}/liballochook.so";
                var imp = AssetImporter.GetAtPath(assetPath) as PluginImporter;
                if (imp == null) { Debug.LogWarning($"[AllocHook] 无法读取导入设置: {assetPath}"); continue; }

                bool androidOn = imp.GetCompatibleWithPlatform(BuildTarget.Android);
                string cpu = imp.GetPlatformData(BuildTarget.Android, "CPU");
                if (androidOn)
                    Debug.Log($"[AllocHook] OK   {abi}\\liballochook.so  (Android=on, CPU={cpu})");
                else
                    Debug.LogError($"[AllocHook] ✗ {abi}\\liballochook.so 的 Android 平台未勾选（CPU={cpu}），打包会丢掉它！"
                                 + " 菜单 Tools/AllocHook/修复插件平台设置 可自动修正");
            }

            bool needArm64 = (arch & AndroidArchitecture.ARM64) != 0;
            bool needArmv7 = (arch & AndroidArchitecture.ARMv7) != 0;
            if (needArm64 && !File.Exists(Path.Combine(plugins, "arm64-v8a/liballochook.so")))
                Debug.LogWarning("[AllocHook] 目标是 ARM64 但缺少 arm64-v8a/liballochook.so，运行时会加载失败");
            if (needArmv7 && !File.Exists(Path.Combine(plugins, "armeabi-v7a/liballochook.so")))
                Debug.LogWarning("[AllocHook] 目标是 ARMv7 但缺少 armeabi-v7a/liballochook.so（32 位仅支持 LD_PRELOAD 方式）");

            string exportRoot = Path.Combine(Directory.GetCurrentDirectory(), "Build/export_project");
            if (Directory.Exists(exportRoot))
            {
                var hits = Directory.GetDirectories(exportRoot, "symbols", SearchOption.AllDirectories);
                if (hits.Length == 0)
                    Debug.LogWarning("[AllocHook] 未找到 unityLibrary/symbols，导出工程后请确认符号是否生成");
                else
                    foreach (var h in hits) Debug.Log($"[AllocHook] symbols: {h}");
            }

            if (!EditorUserBuildSettings.development)
                Debug.LogWarning("[AllocHook] 当前不是 Development Build：Profiler 连不上（AllocHook 本身不依赖）");
        }

        /// <summary>
        /// 一键把三个 ABI 的 .so 设成「仅 Android + 对应 CPU」。
        /// 现象：只勾 Any Platform、Android 未勾选时，打包会把 .so 静默丢掉。
        /// </summary>
        [MenuItem("Tools/AllocHook/修复插件平台设置")]
        public static void FixPluginSettings()
        {
            var map = new Dictionary<string, string>
            {
                { "arm64-v8a", "ARM64" },
                { "armeabi-v7a", "ARMv7" },
                { "x86_64", "x86_64" },
            };

            int fixedCount = 0;
            foreach (var kv in map)
            {
                string assetPath = $"Packages/com.elex.allochook/Plugins/Android/{kv.Key}/liballochook.so";
                var imp = AssetImporter.GetAtPath(assetPath) as PluginImporter;
                if (imp == null) { Debug.LogWarning("[AllocHook] 找不到 " + assetPath); continue; }

                imp.SetCompatibleWithAnyPlatform(false);
                imp.SetCompatibleWithPlatform(BuildTarget.Android, true);
                imp.SetPlatformData(BuildTarget.Android, "CPU", kv.Value);
                imp.SaveAndReimport();
                fixedCount++;
                Debug.Log($"[AllocHook] 已修正 {kv.Key} → Android=on, CPU={kv.Value}");
            }
            Debug.Log($"[AllocHook] 修复完成（{fixedCount} 个），请重新打包");
        }

        private static string FindPackageRoot()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.elex.allochook");
            if (info != null && Directory.Exists(info.resolvedPath)) return info.resolvedPath;
            string fallback = Path.GetFullPath("Packages/com.elex.allochook");
            return Directory.Exists(fallback) ? fallback : null;
        }
    }
}
