// 给游戏内 GM / 调试面板调用的接口。
// 与宿主工具的文件触发方式（allochook.snap / allochook.reset）等价。
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Elex.AllocHook
{
    public static class AllocHookApi
    {
        [DllImport("allochook")]
        private static extern void allochook_snapshot();

        [DllImport("allochook")]
        private static extern void allochook_reset();

        /// <summary> 立刻 dump 一次（allochook.bin / allochook.txt） </summary>
        public static void Snapshot()
        {
            if (!AllocHookLoader.EnsureLoaded()) return;
            try { allochook_snapshot(); }
            catch (Exception e) { Debug.LogWarning("[AllocHook] snapshot failed: " + e.Message); }
        }

        /// <summary> 清空计数（录制模式：先 Reset，做操作，再 Snapshot 得到增量） </summary>
        public static void Reset()
        {
            if (!AllocHookLoader.EnsureLoaded()) return;
            try { allochook_reset(); }
            catch (Exception e) { Debug.LogWarning("[AllocHook] reset failed: " + e.Message); }
        }

        /// <summary> 不依赖 native API 的兜底方式：靠文件触发，Native 侧 200ms 轮询 </summary>
        public static bool TouchControlFile(string fileName)
        {
            if (!AllocHookLoader.EnsureLoaded() || string.IsNullOrEmpty(AllocHookLoader.DataDir)) return false;
            try
            {
                File.WriteAllText(Path.Combine(AllocHookLoader.DataDir, fileName), "1");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AllocHook] touch " + fileName + " failed: " + e.Message);
                return false;
            }
        }

        public static void SnapshotByFile() => TouchControlFile("allochook.snap");
        public static void ResetByFile() => TouchControlFile("allochook.reset");
    }
}
