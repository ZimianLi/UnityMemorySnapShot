using System.Text.Json.Serialization;

namespace AllocHookStudio;

public class AppConfig
{
    public string AdbPath { get; set; } = "adb";
    public string PackageName { get; set; } = "";
    public string ApkDir { get; set; } = "";
    public string SymbolsDir { get; set; } = "";
    public string LlvmDir { get; set; } = "";
    public int Port { get; set; } = 7788;
    public List<CategoryRule> Rules { get; set; } = CategoryRule.Defaults();
    public List<string> IgnoreFramePatterns { get; set; } = DefaultIgnoreFramePatterns();

    public static List<string> DefaultIgnoreFramePatterns() => new()
    {
        @"MemoryManager::",
        @"UnityDefaultAllocator<",
        @"DynamicHeapAllocator",
        @"DualThreadAllocator<",
        @"TLSAllocator<",
        @"BucketAllocator",
        @"StackAllocator<",
        @"ThreadsafeLinearAllocator<",
        @"LocalLowLevelAllocator",
        @"baselib::UnityClassic::detail::",
        @"MemoryPool",
        @"il2cpp::utils::Memory::",
        @"il2cpp::utils::MemoryPool::",
        @"GC_(unix_mmap_get_mem|unix_get_mem|collect_or_expand|allocobj|alloc_large|generic_malloc|malloc\w*|gcj_malloc)",
        @"il2cpp::vm::(Object::(Allocate|New)\w*|Array::New\w*|String::New\w*|GarbageCollector::Allocate\w*)\(",
        @"il2cpp::metadata::",
        @"il2cpp::vm::GlobalMetadata::",
        @"il2cpp_codegen_",
        @"il2cpp::vm::MetadataCache::GetGenericMethod\(",
        @"il2cpp::vm::GenericClass::",
        @"il2cpp::vm::ClassInlines::",
        @"il2cpp::vm::(?:SetupMethodsLocked|SetupFieldsLocked|SetupPropertiesLocked)\(",
        @"il2cpp::vm::Class::(?:InitLocked|Init|SetupMethods|SetupFields|GetMethods|GetMethodFromNameFlagsAndSig)\(",
        @"il2cpp::vm::Runtime::(?:Invoke|InvokeWithThrow|ClassInit)\(",
        @"il2cpp::vm::Type::GetUnderlyingType\(",
        @"\bAllocGenericMethodInfo\(",
        @"\bGetGenericMethodFromIndex\(",
        @"\bGetMethodInfoFromEncodedIndex\(",
        @"\bFromTypeDefinition\(",
        @"\bUNITY_ft_mem_",
        @"std::__ndk1::",
        @"core::vector_detail::",
        @"core::(vector|hash_set|base_hash_map|hash_map|list|StringStorageDefault|basic_string|function_ref|pair)<",
        @"core::pair<core::hash_set<",
        @"dynamic_array<",
        @"dynamic_block_array<",
        @"vector_map<",
        @"stl_allocator<",
        @"google::(libc_allocator_with_realloc|sparsegroup|dense_hash|sparse_hash)",
        @"il2cpp::utils::collections::",
        @"StreamedBinaryRead::TransferSTLStyleArray<",
        @"TransferPPtr<",
        @"\boperator new\b",
        @"(?:^|!)(?:malloc|calloc|realloc|free|memalign|posix_memalign|aligned_alloc)(?:\(|$)",
        @"Baselib_Memory_",
        @"unwindstack::",
        @"StackUnwinder::",
        @"ResolveUsingLibUnwind",
        @"liballochook\.so",
        @"libc\.so",
    };
}

public class CategoryRule
{
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";

    public static List<CategoryRule> Defaults() => new()
    {
        new() { Name = "Meta配置", Pattern = "MetaManager|MetaBase|MetaRuntimeGroup|ConfigFormatter|MetaSplitManifest|GameLoadMetaStep|MetaTable|GlobalTable|SettingDictionary|TableManager" },
        new() { Name = "邮件/聊天/社交", Pattern = "Mail|ECMail|Chat|IMManager|Friend|Alliance|Guild|Social|Sobot|ZCSobot" },
        new() { Name = "UI/字体/文本", Pattern = "UIPanel|UIWidget|UILabel|UISprite|UITexture|UIDrawCall|UIRect|UIAtlas|UIFont|NGUI|GUILayout|GUIManager|TMP_|TextMeshPro|TextRendering|FontImpl|UNITY_FT_|cff_|FT_" },
        new() { Name = "地图/城建/战斗", Pattern = "MapNode|MapProxy|MapScene|Building|Army|Soldier|Troop|Battle|Arena|Skill|Hero|TowerDefense|LordSurvival|CampWorldBoss|Rumble|City|WorldEvent|WorldCastle|SeasonMap|Explore|ShootingGame|NovaBattle" },
        new() { Name = "音频/视频", Pattern = "Audio|FMOD|AVPro|MediaPlayer|Video" },
        new() { Name = "登录/支付/热更", Pattern = "Login|Account|HotUpdate|Patch|PayProxy|ServerList|ArchiveCenter|PrivacyAgreement" },
        new() { Name = "第三方SDK/存储", Pattern = "Firebase|Crashlytics|Adjust|Bugly|UWA|Analytics|UniWebView|sqlite3|libsqlite3" },
        new() { Name = "渲染/Shader/特效", Pattern = "Camera::|RenderManager|CullResults|RenderMesh|DrawMesh|GraphicsScripting|GfxDevice|ThreadedStreamBuffer|ShaderLab|Shader::|SRPBatcher|BuiltInCompatibilityChecker|ConstantBuffers|Material|Texture|Mesh::|VertexData::|ParticleSystem|EasyGPUInstancing" },
        new() { Name = "动画/物理/寻路", Pattern = "mecanim::|AnimationClip|Animator|Playable|b2World|b2Contact|physx::|PhysX|AstarPath|NavMesh" },
        new() { Name = "网络/协议/压缩", Pattern = "CommunicationManager|NetFacade|ProtocolFactory|WebRequest|Http|Socket|Curl_|mbedtls_|ZlibBaseStream|InflateBlocks|InflateManager|ZlibCodec|LZ4|JObject|JContainer|JsonTextReader|Newtonsoft" },
        new() { Name = "资源/场景反序列化", Pattern = "Remapper::|PersistentManager|SerializedFile|ResourceManager::|AssetBundle|LoadSceneOperation|PreloadManager|AwakeFromLoad|AsyncUpload|FileCacher|Object::Produce|CloneObject|Object::InsertObjectInMap|TypeTree|TextAsset::|BuiltinResourceManager|SerializationCache" },
        new() { Name = "IL2CPP元数据/反射", Pattern = "RuntimeType_|RuntimeMethodInfo|AllocGenericMethodInfo|MetadataCache|GenericMethod|GenericClass|SetupMethods|SetupFields|SetupProperties|SetupArray|ConstructGenericArray|ConstructArray|il2cpp_init|il2cpp_codegen|CollectTypeMethods|FromTypeDefinition|Thread::AllocateStaticData" },
        new() { Name = "引擎Job/基础堆", Pattern = "ujob_|JobQueue|Thread::RunThreadWrapper|BatchAllocator|MemoryProfiler|UnityInitApplication|InitializeEngine" },
        new() { Name = "托管GC(字符串/容器)", Pattern = "String_FastAllocateString|List_1_|Dictionary|GC_|Boehm|GarbageCollector|alloc_mark_stack" },
    };
}

/// <summary> 单条聚合记录（一次快照内按栈聚合的结果） </summary>
public class StackRecord
{
    public int Kind { get; set; }          // 0 mmap, 1 mprotect, 2 munmap
    public string Thread { get; set; } = "-";
    public long Bytes { get; set; }
    public long Count { get; set; }

    /// <summary> 1 = 页级（mmap/mprotect/munmap）；2 = 分配级（malloc/free） </summary>
    public int Layer { get; set; } = 1;
    /// <summary> L2：该调用栈已释放的字节；K_PSS(Kind=5)：VMA 分类编号（0=anon,1=malloc,...） </summary>
    public long Aux { get; set; }
    /// <summary> 疑似标记："" | "churn"（L1 反复提交归还） | "pool"（L2 释放远大于存活） </summary>
    public string Suspect { get; set; } = "";
    /// <summary> PSS 分桶里"不可归因"的标题（code/graphics/java/shared/anon/malloc/file-mmap...） </summary>
    public string Label { get; set; } = "";

    /// <summary> 原始地址（用于"重新解析"：换符号表后可对已有快照重新符号化） </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ulong> Pcs { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string> Frames { get; set; } = new();   // 已符号化

    /// <summary> 紧凑磁盘存储用字典索引（内存中水合后置 null） </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? PcIds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? FrameIds { get; set; }
}

public class Snapshot
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime Time { get; set; }
    public int Pid { get; set; }
    public string Package { get; set; } = "";
    public string Device { get; set; } = "";
    public string Mode { get; set; } = "snapshot";      // snapshot | record
    public string Format { get; set; } = "bin";         // bin | txt（txt = 设备上还是老 .so）
    public long TotalDeltaKB { get; set; }               // dumpsys PSS 差值（record 时才有意义）
    /// <summary> L2 被阈值过滤掉的累计字节（没有栈，用户据此决定是否调低阈值） </summary>
    public long L2FilteredBytes { get; set; }
    public long L2FilteredCount { get; set; }
    /// <summary> L2 因采样累计跳过的字节 </summary>
    public long L2SampledBytes { get; set; }
    /// <summary> L2 阈值以下的净存量（申请 - 释放，基于 malloc_usable_size） </summary>
    public long L2FilteredLiveBytes { get; set; }
    public long L2FilteredLiveCount { get; set; }
    /// <summary> L2 因采样跳过的净存量（申请 - 释放，基于 malloc_usable_size） </summary>
    public long L2SampledLiveBytes { get; set; }
    public long L2SampledLiveCount { get; set; }
    /// <summary> 进程内 mallinfo().uordblks </summary>
    public long NativeHeapAllocBytes { get; set; }
    public int L2Min { get; set; }
    public int L2Sample { get; set; }

    /// <summary> 紧凑磁盘存储用去重表（内存中水合后置 null） </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FrameTable { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ulong>? PcTable { get; set; }

    public List<StackRecord> Stacks { get; set; } = new();
    public Dictionary<string, long> Pss { get; set; } = new();   // dumpsys 分项（KB）
}

public class DiffRow
{
    public string Key { get; set; } = "";
    public int Kind { get; set; }
    public string Thread { get; set; } = "-";
    public long BytesA { get; set; }
    public long BytesB { get; set; }
    public long Delta => BytesB - BytesA;
    public long CountA { get; set; }
    public long CountB { get; set; }
    public string Suspect { get; set; } = "";
    public List<string> Frames { get; set; } = new();
}
