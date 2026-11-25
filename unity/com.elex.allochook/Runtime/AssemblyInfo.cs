// 关键：声明本程序集"始终参与链接"。
// 否则在 Managed Stripping Level = Medium/High 时，由于没有任何业务代码引用本程序集，
// IL2CPP 会把整个 Elex.AllocHook（含 [RuntimeInitializeOnLoadMethod] 入口）裁掉，
// 表现为：APK 里有 liballochook.so，但运行期一条 AllocHook 日志都没有。
using UnityEngine.Scripting;

[assembly: AlwaysLinkAssembly]
