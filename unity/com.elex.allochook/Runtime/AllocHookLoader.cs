// AllocHook 加载器：工程里只要有这个包 + Plugins/Android 下的 .so，启动时会自动加载。
// 注意：仅用于开发/排查。正式包不包含 liballochook.so 时，这里会静默跳过。
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Elex.AllocHook
{
    public static class AllocHookLoader
    {
        public static bool IsLoaded { get; private set; }
        public static int Version { get; private set; }
        public static string DataDir { get; private set; }

        [DllImport("allochook")]
        private static extern int allochook_version();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoLoadEarly()
        {
            EnsureLoaded();
        }

        // 双保险：即使 BeforeSceneLoad 被裁掉/被异常打断，进入首个场景后还会再试一次（幂等）
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoLoadLate()
        {
            EnsureLoaded();
        }

        /// <summary> 幂等加载；失败不抛异常（正式包里没有 .so 时属正常情况） </summary>
        public static bool EnsureLoaded()
        {
            if (IsLoaded) return true;
#if UNITY_ANDROID && !UNITY_EDITOR
            bool alreadyMapped = IsNativeLibraryMapped();
            if (!alreadyMapped)
            {
                try
                {
                    using (var sys = new AndroidJavaClass("java.lang.System"))
                    {
                        sys.CallStatic("loadLibrary", "allochook");
                    }
                }
                catch (Exception loadEx)
                {
                    // JDWP 早期注入通过 ClassLoader 0x0(null) 加载时，PathClassLoader 再调 System.loadLibrary
                    // 可能抛 UnsatisfiedLinkError（已在另一 classloader 打开），此处直接探测 native 符号是否已就绪
                    try
                    {
                        Version = allochook_version();
                        IsLoaded = true;
                        DataDir = TryResolveDataDir();
                        Debug.Log($"[AllocHook] already loaded (JDWP/early fallback) v{Version}, dataDir={DataDir}");
                        return true;
                    }
                    catch
                    {
                        Debug.LogWarning("[AllocHook] load failed: " + loadEx.Message);
                        return false;
                    }
                }
            }

            try
            {
                Version = allochook_version();
                IsLoaded = true;
                DataDir = TryResolveDataDir();
                Debug.Log($"[AllocHook] {(alreadyMapped ? "already mapped (JDWP/early)" : "loaded")} v{Version}, dataDir={DataDir}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AllocHook] load failed: " + e.Message);
                return false;
            }
#else
            return false;
#endif
        }

        private static bool IsNativeLibraryMapped()
        {
            try
            {
                using (var sr = new StreamReader("/proc/self/maps"))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.IndexOf("liballochook.so", StringComparison.Ordinal) >= 0)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static string TryResolveDataDir()
        {
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var filesDir = activity.Call<AndroidJavaObject>("getFilesDir"))
                {
                    string files = filesDir.Call<string>("getAbsolutePath");   // .../files
                    return Path.GetDirectoryName(files);                       // /data/data/<pkg>
                }
            }
            catch { return null; }
        }

        /// <summary> 写入/覆盖配置（min_bytes / max_frames / dump_interval / dump_binary） </summary>
        public static bool WriteConfig(string content)
        {
            if (!EnsureLoaded() || string.IsNullOrEmpty(DataDir)) return false;
            try
            {
                File.WriteAllText(Path.Combine(DataDir, "allochook.cfg"), content ?? "");
                Debug.Log("[AllocHook] cfg written, 重启应用后生效");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AllocHook] write cfg failed: " + e.Message);
                return false;
            }
        }
    }
}
