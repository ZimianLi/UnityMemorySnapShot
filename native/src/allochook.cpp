// allochook.cpp —— UnityMemorySnapShot 的原生采集层
//
// 原理：Unity 的 DynamicHeapAllocator / IL2CPP 的 Boehm GC / libc malloc 的 arena
//       都是通过 mmap 预留、mprotect 提交页面的，而这些系统调用发生在"发起分配的那条调用栈"上。
//       所以在 mmap/mprotect/munmap 上挂钩子并抓栈，就等价于拿到了"每次内存增长时的调用栈"。
//
// 注意：本文件内不允许调用 malloc/new（会递归），只用 syscall + 静态内存。
//
// 控制文件（放在应用私有目录 /data/data/<pkg>/ 下）：
//   allochook.snap   出现即立刻 dump 一次（bin + txt），dump 后自动删除
//   allochook.reset  出现即清空计数，自动删除
//   allochook.cfg    可选，key=value 配置（min_bytes / max_frames / dump_interval / dump_binary）

#include <fcntl.h>
#include <unistd.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <malloc.h>
#include <errno.h>
#include <time.h>
#include <pthread.h>
#include <sys/mman.h>
#include <sys/prctl.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <elf.h>
#include <link.h>
#include <dlfcn.h>
#include <android/log.h>
#include <unwind.h>
#include <stdarg.h>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "AllocHook", __VA_ARGS__)

// 32 位 ARM 没有 __NR_mmap，只有 __NR_mmap2（offset 以页为单位）
#if defined(__LP64__)
#define MY_NR_MMAP  __NR_mmap
#define MY_MMAP_OFF(o) (o)
#define MY_R_SYM(i) ELF64_R_SYM(i)
#else
#define MY_NR_MMAP  __NR_mmap2
#define MY_MMAP_OFF(o) ((o) >> 12)
#define MY_R_SYM(i) ELF32_R_SYM(i)
#endif

#ifndef ElfW
#if defined(__LP64__)
#define ElfW(type) Elf64_##type
#else
#define ElfW(type) Elf32_##type
#endif
#endif

// ---------------- 配置 ----------------
static int  g_minBytes     = 4096;     // 忽略小于该值的提交（L1）
static int  g_maxFrames    = 32;       // 栈深度（L1）
static int  g_dumpInterval = 15;       // 文本快照周期（秒）
static int  g_dumpBinary   = 1;        // 是否写二进制
static int  g_dumpTxt      = 0;        // dumpBinary==1 时默认跳过慢速文本 dump；dump_txt=1 显式开启
static int  g_regEnabled   = 1;        // 是否维护"已提交区间表"（排查用开关）
// 库加载后延迟多久才打 GOT 补丁。
// 实测：在 libunity 的 JNI_OnLoad 期间改它的 GOT，进程会直接消失（无 tombstone）。
// 等它初始化完成再补丁就稳定了（C# 那条路一直没问题就是因为那时引擎早就起来了）。
// 实测（Huawei + 该 Unity 项目）：6s 崩溃、10s 正常，15s 正常。
// 引擎初始化完成前改 GOT 会让进程无征兆消失，所以默认取一个偏保守的值；
// 如果你的项目初始化更久，把 allochook.cfg 里的 patch_delay_ms 调大即可。
static int  g_patchDelayMs = 500;
static uint64_t g_warmupMs = 500ULL;
// 持有者追踪（inline hook Unity 分配器）：
//   分配时抓栈并登记 ptr→(size, 栈)，释放时只按 ptr 扣减（不抓栈）。
//   这与 L1 页级口径互补：L1 只知道"谁最后提交了页"，这里知道"现在谁持有"。
static int  g_holdEnabled  = 0;        // 是否 hook Unity 分配器（实验特性，默认关；cfg 里 hold=1 打开）
static int  g_holdMin      = 256;      // 只统计 >= 该值的分配（越大越省）
static int  g_holdFrames   = 10;       // 栈深度

// 轻量 L2：只在 -systemallocator 包上打开。
// 分配抓栈、释放只按 ptr 扣减；阈值以下的分配只累加字节数（不抓栈、不建记录），
// 这样界面能明确显示"被过滤掉多少"，方便决定是否调低阈值。
static int  g_l2Enabled     = 0;
static int  g_l2Min         = 4096;
static int  g_l2Frames      = 16;
static int  g_l2Sample      = 8;        // 1/N 采样；1 = 全采
static int  g_btDiag        = 0;        // 调试对比开关（默认关）

static size_t g_pageSize = 4096;

static inline size_t alignPage(size_t sz) {
    size_t ps = g_pageSize ? g_pageSize : 4096;
    return (sz + ps - 1) & ~(ps - 1);
}

static void* rawMmapAnon(size_t bytes) {
    size_t sz = alignPage(bytes);
    if (sz == 0) return 0;
    long r = syscall(MY_NR_MMAP, (void*)0, sz, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if ((unsigned long)r >= (unsigned long)-4095UL) return 0;
    return (void*)r;
}

static void rawMunmap(void* ptr, size_t bytes) {
    if (!ptr || bytes == 0) return;
    size_t sz = alignPage(bytes);
    syscall(__NR_munmap, ptr, sz);
}

// 64 分片缓存行对齐统计：消除 < l2Min 与采样跳过热路径上的全局原子锁争用
struct alignas(64) StatShard {
    int64_t filteredBytes;
    int64_t filteredCount;
    int64_t sampledBytes;
    int64_t filteredLiveBytes;
    int64_t filteredLiveCount;
    int64_t sampledLiveBytes;
    int64_t sampledLiveCount;
};
static StatShard g_statShards[64];
static uint32_t  g_nextStatShard = 0;
static uint32_t  g_statGen = 0;
static size_t    g_l2MaxFilteredUsz = 0;
static size_t    g_l2MaxSampledUsz  = 0;

struct alignas(64) TlsStatNode {
    int64_t  filteredBytes;
    int64_t  sampledBytes;
    int64_t  filteredLiveBytes;
    int64_t  sampledLiveBytes;
    int32_t  filteredCount;
    int32_t  filteredLiveCount;
    int32_t  sampledLiveCount;
    uint32_t statGen;
    uint16_t ops;
    uint8_t  statShardPlus1;
    uint8_t  pad;
};
#define STAT_NODE_SLAB_CAP 512
static TlsStatNode* g_statNodeSlab = 0;
static uint32_t     g_statNodeSlabUsed = STAT_NODE_SLAB_CAP;
static volatile int g_statNodeLock = 0;
static pthread_key_t g_tlsKey;
static volatile int  g_tlsKeyReady = 0;

static unsigned long long g_offAlloc   = 0;   // MemoryManager::Allocate 在 libunity 内的偏移
static unsigned long long g_offFree    = 0;   // 对应的释放入口偏移
static unsigned long long g_offAlloc1  = 0;   // TLSAllocator<1>::Allocate
static unsigned long long g_offFree1   = 0;   // TLSAllocator<1>::Deallocate

// 预热期：进程启动后的头 g_warmupMs 毫秒内跳过 _Unwind_Backtrace 回退，
// 仅保留无锁 fastUnwindCore（g_ehMods 已在 patchModule 时安全注册）。
static uint64_t g_t0ms = 0;

static inline uint64_t nowMs() {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000ULL + (uint64_t)ts.tv_nsec / 1000000ULL;
}

static inline int inWarmup() {
    return g_t0ms != 0 && (nowMs() - g_t0ms) < g_warmupMs;
}

#define MAXF 32
#define TBL_SHARDS 64
#define REC_NONE 0xFFFFFFFFu

// 三种语义（不是三种系统调用）：
//   K_RESERVE 只预留地址空间（mmap PROT_NONE），不占物理内存
//   K_COMMIT  真正的提交（mmap 带读写权限，或 mprotect 把预留页变成可读写）
//   K_RELEASE 归还（munmap 释放的**已提交**字节，或 mprotect 改回 PROT_NONE）
// 这样"净 = commit − release"才等于真实增长。
// 3 = 已废弃的 L2（libc malloc 钩子），编号保留，避免旧快照被误读
// K_LIVE = 4：当前仍处于"已提交"状态的页，按提交它的调用栈归属（存量视图）
// K_PSS = 5：来自 /proc/self/smaps 的 Pss，按"命中区间表 → 提交栈；否则按路径分桶"汇总，
//            所有 K_PSS 之和 == 进程 PSS
// K_HOLD = 6：当前真正被持有的内存（inline hook Unity 分配器的结果）
//            bytes = 当前持有字节，count = 分配次数，aux = 已释放字节
enum { K_RESERVE = 0, K_COMMIT = 1, K_RELEASE = 2, K_OLD_L2 = 3, K_LIVE = 4, K_PSS = 5, K_HOLD = 6 };

// ---------------- 已提交区间表（mmap 动态扩容） ----------------
// 作用有两个：
//   1) 算 mprotect/munmap 的真实增量（去重：只有"预留→提交"的部分才计入）
//   2) 记下"这块页是谁提交的"（rec = stackId），从而支持"当前存量按栈归因"
// 注意：归因的是"最后一次提交这块页的调用栈"，不是"当前谁在使用"（分配器复用不会重新提交）。
#define REG_INIT_CAP 32768
#define REG_MAX_CAP  (1 << 20)
struct Region { uintptr_t s, e; uint32_t rec; };
static Region* g_reg = 0;
static int g_regN = 0;
static int g_regCap = 0;

static bool ensureRegCap(int need) {
    if (need <= g_regCap) return true;
    int newCap = g_regCap ? g_regCap * 2 : REG_INIT_CAP;
    while (newCap < need && newCap < REG_MAX_CAP) newCap *= 2;
    if (need > newCap) return false;
    Region* nr = (Region*)rawMmapAnon((size_t)newCap * sizeof(Region));
    if (!nr) return false;
    if (g_reg) {
        if (g_regN > 0) memcpy(nr, g_reg, (size_t)g_regN * sizeof(Region));
        rawMunmap(g_reg, (size_t)g_regCap * sizeof(Region));
    }
    g_reg = nr;
    g_regCap = newCap;
    return true;
}

static int regLowerBound(uintptr_t a) {
    int lo = 0, hi = g_regN;
    while (lo < hi) {
        int mid = (lo + hi) >> 1;
        if (g_reg[mid].e <= a) lo = mid + 1;
        else hi = mid;
    }
    return lo;
}

static size_t CoveredBytes(uintptr_t a, uintptr_t b) {
    size_t sum = 0;
    for (int i = regLowerBound(a); i < g_regN && g_reg[i].s < b; ++i) {
        uintptr_t lo = g_reg[i].s > a ? g_reg[i].s : a;
        uintptr_t hi = g_reg[i].e < b ? g_reg[i].e : b;
        if (hi > lo) sum += (size_t)(hi - lo);
    }
    return sum;
}

// 从已提交集合里挖掉 [a,b)，返回真正被归还的字节数（保留分段各自的归属）
static size_t RemoveRange(uintptr_t a, uintptr_t b) {
    size_t removed = 0;
    for (int i = regLowerBound(a); i < g_regN && g_reg[i].s < b; ) {
        uintptr_t rs = g_reg[i].s, re = g_reg[i].e;
        uint32_t rec = g_reg[i].rec;
        uintptr_t lo = rs > a ? rs : a;
        uintptr_t hi = re < b ? re : b;
        if (hi > lo) removed += (size_t)(hi - lo);

        if (rs >= a && re <= b) {                    // 整段删除
            if (i < g_regN - 1)
                memmove(&g_reg[i], &g_reg[i + 1], (size_t)(g_regN - 1 - i) * sizeof(Region));
            g_regN--;
            continue;
        }
        if (rs < a && re > b) {                      // 中间挖空，拆两段（归属相同）
            if (!ensureRegCap(g_regN + 1)) { i++; continue; }
            memmove(&g_reg[i + 2], &g_reg[i + 1], (size_t)(g_regN - 1 - i) * sizeof(Region));
            g_regN++;
            g_reg[i + 1].s = b; g_reg[i + 1].e = re; g_reg[i + 1].rec = rec;
            g_reg[i].e = a;
        } else if (rs < a) {
            g_reg[i].e = a;
        } else {
            g_reg[i].s = b;
        }
        i++;
    }
    return removed;
}

// 把 [a,b) 标记为已提交，归属记为 rec
static void InsertRange(uintptr_t a, uintptr_t b, uint32_t rec) {
    if (b <= a) return;
    RemoveRange(a, b);                               // 先清掉重叠部分（各自归属不同则先断开）

    if (!ensureRegCap(g_regN + 1)) return;
    int lo = 0, hi = g_regN;
    while (lo < hi) {
        int mid = (lo + hi) >> 1;
        if (g_reg[mid].s < a) lo = mid + 1;
        else hi = mid;
    }
    int pos = lo;
    if (pos < g_regN)
        memmove(&g_reg[pos + 1], &g_reg[pos], (size_t)(g_regN - pos) * sizeof(Region));
    g_reg[pos].s = a; g_reg[pos].e = b; g_reg[pos].rec = rec;
    g_regN++;
}

// ---------------- 线程字符串表（append-only，用于 StackSlot 紧凑存储与 v3 字典） ----------------
#define MAX_THREADS 4096
#define THREAD_HASH_CAP 8192
struct ThreadEnt {
    uint64_t hash;
    uint8_t  len;
    char     name[23];
};
static ThreadEnt g_threads[MAX_THREADS];
static uint16_t  g_threadBuckets[THREAD_HASH_CAP]; // 0xFFFF = empty
static uint32_t  g_threadCount = 0;
static volatile int g_threadLock = 0;
static int       g_threadHashInited = 0;

static inline void THREAD_LOCK()   { while (__sync_lock_test_and_set(&g_threadLock, 1)) { } }
static inline void THREAD_UNLOCK() { __sync_lock_release(&g_threadLock); }

static uint16_t internThreadName(const char* tn) {
    if (!tn || !*tn) tn = "-";
    uint64_t h = 1469598103934665603ULL;
    size_t len = 0;
    for (; tn[len] && len < 22; ++len) {
        h ^= (uint64_t)(unsigned char)tn[len];
        h *= 1099511628211ULL;
    }
    THREAD_LOCK();
    if (!g_threadHashInited) {
        memset(g_threadBuckets, 0xFF, sizeof(g_threadBuckets));
        g_threadHashInited = 1;
    }
    uint32_t mask = THREAD_HASH_CAP - 1;
    uint32_t pos = (uint32_t)(h & mask);
    uint16_t found = 0;
    bool hit = false;
    for (uint32_t k = 0; k < THREAD_HASH_CAP; ++k) {
        uint32_t b = (pos + k) & mask;
        uint16_t idx = g_threadBuckets[b];
        if (idx == 0xFFFFu) {
            if (g_threadCount < MAX_THREADS) {
                idx = (uint16_t)g_threadCount++;
                g_threads[idx].hash = h;
                g_threads[idx].len = (uint8_t)len;
                memcpy(g_threads[idx].name, tn, len);
                g_threads[idx].name[len] = 0;
                g_threadBuckets[b] = idx;
                found = idx;
            } else {
                found = 0;
            }
            hit = true;
            break;
        }
        if (g_threads[idx].hash == h && g_threads[idx].len == (uint8_t)len &&
            memcmp(g_threads[idx].name, tn, len) == 0) {
            found = idx;
            hit = true;
            break;
        }
    }
    THREAD_UNLOCK();
    return hit ? found : 0;
}

// ---------------- 64 分片 PC Intern 表 + TLS 直映缓存 ----------------
#define PC_SHARDS 64
#define PC_SLAB_SHIFT 12
#define PC_SLAB_CAP (1u << PC_SLAB_SHIFT) // 4096 * 8 = 32KB per slab
#define PC_NONE 0xFFFFFFFFu
#define TLS_PC_CACHE_SIZE 256

struct TlsPcEnt {
    uint64_t pc;
    uint32_t pcId;
    uint32_t pad;
};

struct PcShard {
    volatile int lock;
    uint32_t*    buckets;
    uint32_t     bucketCap;
    uint32_t     count;
    uint64_t**   slabs;
    uint32_t     slabPtrCap;
};
static PcShard g_pcShards[PC_SHARDS];

static inline void PC_LOCK(uint32_t sh)   { while (__sync_lock_test_and_set(&g_pcShards[sh].lock, 1)) { } }
static inline void PC_UNLOCK(uint32_t sh) { __sync_lock_release(&g_pcShards[sh].lock); }

static inline uint64_t pcHash(uint64_t pc) {
    uint64_t x = (pc >> 2) * 11400714819323198485ULL;
    x ^= x >> 33;
    x *= 0xff51afd7ed558ccdULL;
    x ^= x >> 33;
    return x;
}

static inline uint64_t pcFromId(uint32_t pcId) {
    uint32_t sh = pcId >> 24;
    uint32_t idx = pcId & 0xFFFFFFu;
    return g_pcShards[sh].slabs[idx >> PC_SLAB_SHIFT][idx & (PC_SLAB_CAP - 1)];
}

static bool pcGrowBuckets(PcShard* s) {
    uint32_t newCap = s->bucketCap ? (s->bucketCap * 2) : 4096u;
    uint32_t* nb = (uint32_t*)rawMmapAnon((size_t)newCap * sizeof(uint32_t));
    if (!nb) return false;
    memset(nb, 0xFF, (size_t)newCap * sizeof(uint32_t));
    uint32_t mask = newCap - 1;
    for (uint32_t i = 0; i < s->count; ++i) {
        uint64_t pc = s->slabs[i >> PC_SLAB_SHIFT][i & (PC_SLAB_CAP - 1)];
        uint32_t pos = (uint32_t)(pcHash(pc) >> 6) & mask;
        while (nb[pos] != 0xFFFFFFFFu) pos = (pos + 1) & mask;
        nb[pos] = i;
    }
    if (s->buckets) rawMunmap(s->buckets, (size_t)s->bucketCap * sizeof(uint32_t));
    s->buckets = nb;
    s->bucketCap = newCap;
    return true;
}

static bool pcEnsureSlot(PcShard* s, uint32_t idx) {
    uint32_t slabIdx = idx >> PC_SLAB_SHIFT;
    if (slabIdx >= 4096u) return false; // 最多 16M PC / 分片（受 24-bit localIdx 约束）
    if (slabIdx >= s->slabPtrCap) {
        uint32_t newPtrCap = s->slabPtrCap ? (s->slabPtrCap * 2) : 64u;
        while (newPtrCap <= slabIdx) newPtrCap *= 2;
        uint64_t** np = (uint64_t**)rawMmapAnon((size_t)newPtrCap * sizeof(uint64_t*));
        if (!np) return false;
        if (s->slabs) {
            memcpy(np, s->slabs, (size_t)s->slabPtrCap * sizeof(uint64_t*));
            rawMunmap(s->slabs, (size_t)s->slabPtrCap * sizeof(uint64_t*));
        }
        s->slabs = np;
        s->slabPtrCap = newPtrCap;
    }
    if (!s->slabs[slabIdx]) {
        uint64_t* ns = (uint64_t*)rawMmapAnon((size_t)PC_SLAB_CAP * sizeof(uint64_t));
        if (!ns) return false;
        s->slabs[slabIdx] = ns;
    }
    return true;
}

static uint32_t pcInternSlow(uint64_t pc) {
    uint64_t h = pcHash(pc);
    uint32_t sh = (uint32_t)(h & (PC_SHARDS - 1));
    PcShard* s = &g_pcShards[sh];
    uint32_t res = PC_NONE;

    PC_LOCK(sh);
    if (!s->buckets || (s->count + 1) * 10 >= s->bucketCap * 7) {
        if (!pcGrowBuckets(s) && !s->buckets) { PC_UNLOCK(sh); return PC_NONE; }
    }
    uint32_t mask = s->bucketCap - 1;
    uint32_t pos = (uint32_t)(h >> 6) & mask;
    for (uint32_t k = 0; k < s->bucketCap; ++k) {
        uint32_t b = (pos + k) & mask;
        uint32_t idx = s->buckets[b];
        if (idx == 0xFFFFFFFFu) {
            if (s->count < 0x00FFFFFFu && pcEnsureSlot(s, s->count)) {
                uint32_t localIdx = s->count;
                s->slabs[localIdx >> PC_SLAB_SHIFT][localIdx & (PC_SLAB_CAP - 1)] = pc;
                s->buckets[b] = localIdx;
                s->count++;
                res = (sh << 24) | localIdx;
            }
            break;
        }
        if (s->slabs[idx >> PC_SLAB_SHIFT][idx & (PC_SLAB_CAP - 1)] == pc) {
            res = (sh << 24) | idx;
            break;
        }
    }
    PC_UNLOCK(sh);
    return res;
}

// ---------------- 64 分片 mmap 动态扩容栈表 + Arena ----------------
#define STACK_SLAB_SHIFT 10
#define STACK_SLAB_CAP   (1u << STACK_SLAB_SHIFT) // 1024 slots/slab
#define ARENA_SLAB_SHIFT 14
#define ARENA_SLAB_CAP   (1u << ARENA_SLAB_SHIFT) // 16384 uint32_t = 64KB/slab

struct StackSlot {
    uint64_t hash;
    uint64_t bytes;      // L1: 提交/归还字节   L2: 当前存活字节
    uint64_t count;      // L1: 次数            L2: 分配次数
    uint64_t aux;        // L2: 已释放字节（用于判定"池化/复用"）
    uint32_t arenaOff;   // uint32_t 偏移 = (slabIdx << ARENA_SLAB_SHIFT) | inSlab
    uint16_t threadId;   // g_threads 下标
    uint8_t  kind;
    uint8_t  nf;
};

struct StackShard {
    volatile int lock;
    uint32_t*    buckets;
    uint32_t     bucketCap;
    uint32_t     count;
    StackSlot**  slabs;
    uint32_t     slabPtrCap;
    uint32_t**   arenaSlabs;
    uint32_t     arenaSlabPtrCap;
    uint32_t     arenaCurSlab;
    uint32_t     arenaCurUsed;
};
static StackShard g_tblShards[TBL_SHARDS];
static volatile int g_ready;
static char g_dir[160];

struct TlsState {
    uint8_t      inHook;
    uint8_t      hasName;
    uint8_t      tlsKeyReg;
    uint8_t      pad0;
    uint16_t     nameTick;
    uint16_t     threadId;
    uint32_t     sampleSeq;
    TlsStatNode* statNode;
    char         tname[24];
    TlsPcEnt     pcCache[TLS_PC_CACHE_SIZE];
};
static __thread TlsState g_tls;

static inline void TBL_LOCK(uint32_t sh)   { while (__sync_lock_test_and_set(&g_tblShards[sh].lock, 1)) { } }
static inline void TBL_UNLOCK(uint32_t sh) { __sync_lock_release(&g_tblShards[sh].lock); }

static inline uint32_t pcIntern(TlsState* tls, uint64_t pc) {
    if (pc == 0) return PC_NONE;
    uint64_t h = pcHash(pc);
    uint32_t slot = (uint32_t)((h >> 6) & (TLS_PC_CACHE_SIZE - 1));
    if (tls->pcCache[slot].pc == pc) return tls->pcCache[slot].pcId;
    uint32_t id = pcInternSlow(pc);
    if (id != PC_NONE) {
        tls->pcCache[slot].pc = pc;
        tls->pcCache[slot].pcId = id;
    }
    return id;
}

static inline StackSlot* stackSlotAt(const StackShard* s, uint32_t localIdx) {
    return &s->slabs[localIdx >> STACK_SLAB_SHIFT][localIdx & (STACK_SLAB_CAP - 1)];
}

static inline const uint32_t* arenaPtr(const StackShard* s, uint32_t arenaOff) {
    return &s->arenaSlabs[arenaOff >> ARENA_SLAB_SHIFT][arenaOff & (ARENA_SLAB_CAP - 1)];
}

static inline StackSlot* tblGetSlot(uint32_t stackId) {
    if (stackId == REC_NONE) return 0;
    uint32_t sh = stackId >> 24;
    uint32_t localIdx = stackId & 0xFFFFFFu;
    if (sh >= TBL_SHARDS) return 0;
    const StackShard* s = &g_tblShards[sh];
    if (localIdx >= s->count) return 0;
    return &s->slabs[localIdx >> STACK_SLAB_SHIFT][localIdx & (STACK_SLAB_CAP - 1)];
}

static bool stackGrowBuckets(StackShard* s) {
    uint32_t newCap = s->bucketCap ? (s->bucketCap * 2) : 4096u;
    uint32_t* nb = (uint32_t*)rawMmapAnon((size_t)newCap * sizeof(uint32_t));
    if (!nb) return false;
    memset(nb, 0xFF, (size_t)newCap * sizeof(uint32_t));
    uint32_t mask = newCap - 1;
    for (uint32_t i = 0; i < s->count; ++i) {
        const StackSlot* r = stackSlotAt(s, i);
        uint32_t pos = (uint32_t)(r->hash >> 6) & mask;
        while (nb[pos] != 0xFFFFFFFFu) pos = (pos + 1) & mask;
        nb[pos] = i;
    }
    if (s->buckets) rawMunmap(s->buckets, (size_t)s->bucketCap * sizeof(uint32_t));
    s->buckets = nb;
    s->bucketCap = newCap;
    return true;
}

static bool stackEnsureSlot(StackShard* s, uint32_t idx) {
    uint32_t slabIdx = idx >> STACK_SLAB_SHIFT;
    if (slabIdx >= 16384u) return false;
    if (slabIdx >= s->slabPtrCap) {
        uint32_t newPtrCap = s->slabPtrCap ? (s->slabPtrCap * 2) : 64u;
        while (newPtrCap <= slabIdx) newPtrCap *= 2;
        StackSlot** np = (StackSlot**)rawMmapAnon((size_t)newPtrCap * sizeof(StackSlot*));
        if (!np) return false;
        if (s->slabs) {
            memcpy(np, s->slabs, (size_t)s->slabPtrCap * sizeof(StackSlot*));
            rawMunmap(s->slabs, (size_t)s->slabPtrCap * sizeof(StackSlot*));
        }
        s->slabs = np;
        s->slabPtrCap = newPtrCap;
    }
    if (!s->slabs[slabIdx]) {
        StackSlot* ns = (StackSlot*)rawMmapAnon((size_t)STACK_SLAB_CAP * sizeof(StackSlot));
        if (!ns) return false;
        s->slabs[slabIdx] = ns;
    }
    return true;
}

static bool arenaAlloc(StackShard* s, uint32_t nf, uint32_t* outOff, uint32_t** outPtr) {
    if (s->arenaSlabPtrCap == 0 || s->arenaCurUsed + nf > ARENA_SLAB_CAP) {
        uint32_t nextSlab = (s->arenaSlabPtrCap == 0) ? 0u : (s->arenaCurSlab + 1u);
        if (nextSlab >= 262144u) return false;
        if (nextSlab >= s->arenaSlabPtrCap) {
            uint32_t newPtrCap = s->arenaSlabPtrCap ? (s->arenaSlabPtrCap * 2) : 32u;
            while (newPtrCap <= nextSlab) newPtrCap *= 2;
            uint32_t** np = (uint32_t**)rawMmapAnon((size_t)newPtrCap * sizeof(uint32_t*));
            if (!np) return false;
            if (s->arenaSlabs) {
                memcpy(np, s->arenaSlabs, (size_t)s->arenaSlabPtrCap * sizeof(uint32_t*));
                rawMunmap(s->arenaSlabs, (size_t)s->arenaSlabPtrCap * sizeof(uint32_t*));
            }
            s->arenaSlabs = np;
            s->arenaSlabPtrCap = newPtrCap;
        }
        if (!s->arenaSlabs[nextSlab]) {
            uint32_t* ns = (uint32_t*)rawMmapAnon((size_t)ARENA_SLAB_CAP * sizeof(uint32_t));
            if (!ns) return false;
            s->arenaSlabs[nextSlab] = ns;
        }
        s->arenaCurSlab = nextSlab;
        s->arenaCurUsed = 0;
    }
    *outOff = (s->arenaCurSlab << ARENA_SLAB_SHIFT) | s->arenaCurUsed;
    *outPtr = s->arenaSlabs[s->arenaCurSlab] + s->arenaCurUsed;
    s->arenaCurUsed += nf;
    return true;
}

// ---------------- TLS 统计累加 & 64 分片刷新 ----------------
static TlsStatNode* allocStatNode() {
    while (__sync_lock_test_and_set(&g_statNodeLock, 1)) { }
    if (!g_statNodeSlab || g_statNodeSlabUsed >= STAT_NODE_SLAB_CAP) {
        TlsStatNode* slab = (TlsStatNode*)rawMmapAnon((size_t)STAT_NODE_SLAB_CAP * sizeof(TlsStatNode));
        if (!slab) { __sync_lock_release(&g_statNodeLock); return 0; }
        g_statNodeSlab = slab;
        g_statNodeSlabUsed = 0;
    }
    TlsStatNode* node = &g_statNodeSlab[g_statNodeSlabUsed++];
    __sync_lock_release(&g_statNodeLock);
    return node;
}

static void tlsStatFlush(TlsStatNode* node) {
    if (!node) return;
    uint32_t gen = __atomic_load_n(&g_statGen, __ATOMIC_RELAXED);
    if (node->statGen != gen) {
        node->filteredBytes = 0;
        node->sampledBytes = 0;
        node->filteredLiveBytes = 0;
        node->sampledLiveBytes = 0;
        node->filteredCount = 0;
        node->filteredLiveCount = 0;
        node->sampledLiveCount = 0;
        node->ops = 0;
        node->statGen = gen;
        return;
    }
    if (node->ops == 0 &&
        node->filteredBytes == 0 && node->sampledBytes == 0 &&
        node->filteredLiveBytes == 0 && node->sampledLiveBytes == 0 &&
        node->filteredCount == 0 && node->filteredLiveCount == 0 && node->sampledLiveCount == 0) {
        return;
    }
    if (node->statShardPlus1 == 0) {
        uint32_t sh = __atomic_fetch_add(&g_nextStatShard, 1u, __ATOMIC_RELAXED) & 63u;
        node->statShardPlus1 = (uint8_t)(sh + 1u);
    }
    StatShard* s = &g_statShards[node->statShardPlus1 - 1u];
    if (node->filteredBytes)     __atomic_fetch_add(&s->filteredBytes,     node->filteredBytes,            __ATOMIC_RELAXED);
    if (node->filteredCount)     __atomic_fetch_add(&s->filteredCount,     (int64_t)node->filteredCount,   __ATOMIC_RELAXED);
    if (node->sampledBytes)      __atomic_fetch_add(&s->sampledBytes,      node->sampledBytes,             __ATOMIC_RELAXED);
    if (node->filteredLiveBytes) __atomic_fetch_add(&s->filteredLiveBytes, node->filteredLiveBytes,        __ATOMIC_RELAXED);
    if (node->filteredLiveCount) __atomic_fetch_add(&s->filteredLiveCount, (int64_t)node->filteredLiveCount, __ATOMIC_RELAXED);
    if (node->sampledLiveBytes)  __atomic_fetch_add(&s->sampledLiveBytes,  node->sampledLiveBytes,         __ATOMIC_RELAXED);
    if (node->sampledLiveCount)  __atomic_fetch_add(&s->sampledLiveCount,  (int64_t)node->sampledLiveCount, __ATOMIC_RELAXED);
    node->filteredBytes = 0;
    node->sampledBytes = 0;
    node->filteredLiveBytes = 0;
    node->sampledLiveBytes = 0;
    node->filteredCount = 0;
    node->filteredLiveCount = 0;
    node->sampledLiveCount = 0;
    node->ops = 0;
}

static void tlsOnExit(void* arg) {
    TlsStatNode* node = (TlsStatNode*)arg;
    if (!node) return;
    tlsStatFlush(node);
}

static inline TlsStatNode* ensureStatNode(TlsState* tls) {
    TlsStatNode* node = tls->statNode;
    if (__builtin_expect(node != 0, 1)) {
        uint32_t gen = __atomic_load_n(&g_statGen, __ATOMIC_RELAXED);
        if (__builtin_expect(node->statGen != gen, 0)) tlsStatFlush(node);
        return node;
    }
    node = allocStatNode();
    if (!node) return 0;
    node->statGen = __atomic_load_n(&g_statGen, __ATOMIC_RELAXED);
    tls->statNode = node;
    if (tls->tlsKeyReg == 0 && __atomic_load_n(&g_tlsKeyReady, __ATOMIC_ACQUIRE)) {
        tls->tlsKeyReg = 1;
        uint8_t prev = tls->inHook;
        tls->inHook = 1;
        pthread_setspecific(g_tlsKey, node);
        tls->inHook = prev;
    }
    return node;
}

static inline void tlsAddFilteredAlloc(TlsState* tls, int64_t reqN, int64_t usz) {
    TlsStatNode* node = ensureStatNode(tls);
    if (!node) return;
    node->filteredBytes += reqN;
    node->filteredCount += 1;
    node->filteredLiveBytes += usz;
    node->filteredLiveCount += 1;
    if (++node->ops >= 64) tlsStatFlush(node);
}

static inline void tlsAddFilteredReqAndSampledLive(TlsState* tls, int64_t reqN, int64_t usz) {
    TlsStatNode* node = ensureStatNode(tls);
    if (!node) return;
    node->filteredBytes += reqN;
    node->filteredCount += 1;
    node->sampledLiveBytes += usz;
    node->sampledLiveCount += 1;
    if (++node->ops >= 64) tlsStatFlush(node);
}

static inline void tlsAddSampledSkip(TlsState* tls, int64_t reqN, int64_t usz) {
    TlsStatNode* node = ensureStatNode(tls);
    if (!node) return;
    node->sampledBytes += reqN;
    node->sampledLiveBytes += usz;
    node->sampledLiveCount += 1;
    if (++node->ops >= 64) tlsStatFlush(node);
}

static inline void tlsAddFilteredFree(TlsState* tls, int64_t usz) {
    TlsStatNode* node = ensureStatNode(tls);
    if (!node) return;
    node->filteredLiveBytes -= usz;
    node->filteredLiveCount -= 1;
    if (++node->ops >= 64) tlsStatFlush(node);
}

static inline void tlsAddSampledFree(TlsState* tls, int64_t usz) {
    TlsStatNode* node = ensureStatNode(tls);
    if (!node) return;
    node->sampledLiveBytes -= usz;
    node->sampledLiveCount -= 1;
    if (++node->ops >= 64) tlsStatFlush(node);
}

// ---------------- 线程名（prctl + 每 512 次采样刷新一次） ----------------
static inline void getThreadNameFast(TlsState* tls) {
    if (!tls->hasName || (tls->nameTick++ & 511u) == 0) {
        char tmp[16];
        char clean[20];
        memset(tmp, 0, sizeof(tmp));
        if (prctl(PR_GET_NAME, (unsigned long)tmp, 0, 0, 0) == 0 && tmp[0]) {
            int i = 0;
            for (; i < 15 && tmp[i]; ++i) {
                char c = tmp[i];
                clean[i] = (c == '\n' || c == ' ' || c == '\t') ? '_' : c;
            }
            clean[i] = 0;
        } else if (!tls->hasName) {
            clean[0] = '-';
            clean[1] = 0;
        } else {
            return;
        }
        if (!tls->hasName || strcmp(tls->tname, clean) != 0) {
            int k = 0;
            for (; clean[k] && k < (int)sizeof(tls->tname) - 1; ++k) tls->tname[k] = clean[k];
            tls->tname[k] = 0;
            tls->threadId = internThreadName(tls->tname);
        }
        tls->hasName = 1;
    }
}

static uint32_t tblInsertOrAdd(uint32_t kind, uint16_t threadId, const uintptr_t* pcs, int nf, uint64_t bytes) {
    if (nf <= 0 || nf > MAXF) return REC_NONE;
    uint32_t pcIds[MAXF];
    for (int i = 0; i < nf; ++i) {
        pcIds[i] = pcIntern(&g_tls, (uint64_t)pcs[i]);
        if (pcIds[i] == PC_NONE) return REC_NONE;
    }

    uint64_t h = 1469598103934665603ULL ^ (uint64_t)kind;
    h ^= (uint64_t)threadId; h *= 1099511628211ULL;
    for (int i = 0; i < nf; ++i) {
        h ^= (uint64_t)pcIds[i];
        h *= 1099511628211ULL;
    }
    h ^= h >> 33;
    h *= 0xff51afd7ed558ccdULL;
    h ^= h >> 33;

    uint32_t sh = (uint32_t)(h & (TBL_SHARDS - 1));
    StackShard* s = &g_tblShards[sh];
    uint32_t found = REC_NONE;

    TBL_LOCK(sh);
    if (!s->buckets || (s->count + 1) * 10 >= s->bucketCap * 7) {
        if (!stackGrowBuckets(s) && !s->buckets) { TBL_UNLOCK(sh); return REC_NONE; }
    }
    uint32_t mask = s->bucketCap - 1;
    uint32_t pos = (uint32_t)(h >> 6) & mask;
    for (uint32_t k = 0; k < s->bucketCap; ++k) {
        uint32_t b = (pos + k) & mask;
        uint32_t idx = s->buckets[b];
        if (idx == 0xFFFFFFFFu) {
            if (s->count < 0x00FFFFFFu && stackEnsureSlot(s, s->count)) {
                uint32_t off = 0;
                uint32_t* ap = 0;
                if (arenaAlloc(s, (uint32_t)nf, &off, &ap)) {
                    memcpy(ap, pcIds, (size_t)nf * sizeof(uint32_t));
                    uint32_t localIdx = s->count;
                    StackSlot* r = stackSlotAt(s, localIdx);
                    r->hash = h;
                    r->bytes = bytes;
                    r->count = 1;
                    r->aux = 0;
                    r->arenaOff = off;
                    r->threadId = threadId;
                    r->kind = (uint8_t)kind;
                    r->nf = (uint8_t)nf;
                    s->buckets[b] = localIdx;
                    s->count++;
                    found = (sh << 24) | localIdx;
                }
            }
            break;
        }
        StackSlot* r = stackSlotAt(s, idx);
        if (r->hash == h && r->kind == (uint8_t)kind && r->threadId == threadId && r->nf == (uint8_t)nf &&
            memcmp(arenaPtr(s, r->arenaOff), pcIds, (size_t)nf * sizeof(uint32_t)) == 0) {
            if (r->count == 0) {
                r->bytes = bytes;
                r->count = 1;
                r->aux = 0;
            } else {
                r->bytes += bytes;
                r->count++;
            }
            found = (sh << 24) | idx;
            break;
        }
    }
    TBL_UNLOCK(sh);
    return found;
}

// ---------------- 栈回溯 ----------------
struct Bt { uintptr_t* out; int max; int n; int skip; };

// 动态桩所在的匿名页没有模块信息，unwinder 走到这里会在 step() 里崩
// （实测 SIGSEGV @ libunwind::UnwindCursor::setInfoBasedOnIPRegister）。
// 一旦发现 PC 落进这些页，就地终止回溯。
static uintptr_t g_stubPages[8];
static volatile int g_stubPageN = 0;

static bool inStubPage(uintptr_t pc) {
    int n = g_stubPageN;
    for (int i = 0; i < n; ++i)
        if (pc >= g_stubPages[i] && pc < g_stubPages[i] + 4096) return true;
    return false;
}

static _Unwind_Reason_Code btcb(struct _Unwind_Context* ctx, void* arg) {
    Bt* s = (Bt*)arg;
    uintptr_t pc = (uintptr_t)_Unwind_GetIP(ctx);
    if (!pc) return _URC_END_OF_STACK;
    if (inStubPage(pc)) return _URC_END_OF_STACK;   // 桩页不可展开，直接收尾
    if (s->skip > 0) { s->skip--; return _URC_NO_REASON; }
    if (s->n < s->max) s->out[s->n++] = pc;
    return (s->n >= s->max) ? _URC_END_OF_STACK : _URC_NO_REASON;
}

// ---------------- 崩溃面包屑 ----------------
// 早期注入时如果进程被莫名杀掉，logcat 可能来不及刷出来；这里直接写文件，崩溃后仍可读。
static void trace(const char* s);
static void tlog(const char* fmt, ...) {
    char b[220];
    va_list ap; va_start(ap, fmt); vsnprintf(b, sizeof(b), fmt, ap); va_end(ap);
    trace(b);
}

static void trace(const char* s) {
    if (!g_dir[0]) return;
    char p[220];
    int n = 0;
    for (const char* q = g_dir; *q && n < (int)sizeof(p) - 40; ++q) p[n++] = *q;
    const char* sfx = "/allochook_trace.txt";
    for (const char* q = sfx; *q && n < (int)sizeof(p) - 1; ++q) p[n++] = *q;
    p[n] = 0;
    int fd = open(p, O_WRONLY | O_CREAT | O_APPEND, 0644);
    if (fd < 0) return;
    write(fd, s, strlen(s));
    write(fd, "\n", 1);
    close(fd);
}

// ---------------- 路径 ----------------
static int makePath(char* dst, int cap, const char* name) {
    int n = 0;
    for (const char* p = g_dir; *p && n < cap - 32; ++p) dst[n++] = *p;
    for (const char* p = name; *p && n < cap - 1; ++p) dst[n++] = *p;
    dst[n] = 0;
    return n;
}

// ---------------- 模块表（写进 bin 头，便于宿主符号化） ----------------
static int addModule(struct dl_phdr_info* info, size_t, void* data) {
    if (!info || !info->dlpi_name || !*info->dlpi_name) return 0;
    const char* p = info->dlpi_name;
    if (!strstr(p, ".so")) return 0;

    char* buf = (char*)data;          // 16KB
    size_t used = strlen(buf);
    if (used > 14000) return 0;

    char enc[260]; int k = 0;
    for (const char* s = p; *s && k < 250; ++s) {
        if (*s == '"' || *s == '\\') { enc[k++] = '\\'; }
        enc[k++] = *s;
    }
    enc[k] = 0;

    snprintf(buf + used, 16384 - used,
             "%s{\"p\":\"%s\",\"b\":\"0x%llx\"}",
             used > 18 ? "," : "", enc, (unsigned long long)info->dlpi_addr);
    return 0;
}

// 区间表锁（mprotect/munmap 与 dump 之间互斥）
static volatile int g_regLock;
static inline void RLOCK()   { while (__sync_lock_test_and_set(&g_regLock, 1)) { } }
static inline void RUNLOCK() { __sync_lock_release(&g_regLock); }

// dump 时用的动态聚合表（仅记录区间表实际引用的 L1 stackId，不扫描全表）
struct DumpAccEnt {
    uint32_t stackId; // REC_NONE = empty
    uint32_t cnt;
    uint64_t bytes;
    uint64_t topBytes;
    uint8_t  cat;
};

struct DumpAccTable {
    DumpAccEnt* ents;
    uint32_t    cap;
    uint32_t    count;
};

static inline uint32_t accHash(uint32_t stackId) {
    uint32_t x = stackId * 2654435761u;
    return x ^ (x >> 16);
}

static void accInit(DumpAccTable* t, uint32_t initCap) {
    t->cap = initCap;
    t->count = 0;
    t->ents = (DumpAccEnt*)rawMmapAnon((size_t)initCap * sizeof(DumpAccEnt));
    if (t->ents) {
        for (uint32_t i = 0; i < initCap; ++i) t->ents[i].stackId = REC_NONE;
    } else {
        t->cap = 0;
    }
}

static void accDestroy(DumpAccTable* t) {
    if (t->ents) {
        rawMunmap(t->ents, (size_t)t->cap * sizeof(DumpAccEnt));
        t->ents = 0;
    }
    t->cap = 0;
    t->count = 0;
}

static void accGrow(DumpAccTable* t) {
    uint32_t newCap = t->cap ? (t->cap * 2) : 8192u;
    DumpAccEnt* ne = (DumpAccEnt*)rawMmapAnon((size_t)newCap * sizeof(DumpAccEnt));
    if (!ne) return;
    for (uint32_t i = 0; i < newCap; ++i) ne[i].stackId = REC_NONE;
    uint32_t mask = newCap - 1;
    for (uint32_t i = 0; i < t->cap; ++i) {
        if (t->ents[i].stackId == REC_NONE) continue;
        uint32_t pos = accHash(t->ents[i].stackId) & mask;
        while (ne[pos].stackId != REC_NONE) pos = (pos + 1) & mask;
        ne[pos] = t->ents[i];
    }
    rawMunmap(t->ents, (size_t)t->cap * sizeof(DumpAccEnt));
    t->ents = ne;
    t->cap = newCap;
}

static void accAdd(DumpAccTable* t, uint32_t stackId, uint64_t bytes, uint32_t cntInc, uint8_t cat, bool trackTopCat) {
    if (stackId == REC_NONE || !t->ents) return;
    if ((t->count + 1) * 10 >= t->cap * 7) accGrow(t);
    uint32_t mask = t->cap - 1;
    uint32_t pos = accHash(stackId) & mask;
    for (uint32_t k = 0; k < t->cap; ++k) {
        uint32_t b = (pos + k) & mask;
        DumpAccEnt* e = &t->ents[b];
        if (e->stackId == REC_NONE) {
            e->stackId = stackId;
            e->bytes = bytes;
            e->cnt = cntInc;
            e->topBytes = bytes;
            e->cat = cat;
            t->count++;
            return;
        }
        if (e->stackId == stackId) {
            e->bytes += bytes;
            e->cnt += cntInc;
            if (trackTopCat && bytes >= e->topBytes) {
                e->topBytes = bytes;
                e->cat = cat;
            }
            return;
        }
    }
}

// 汇总"当前已提交"的字节，按提交该区间时的调用栈归属（存量视图）
static void buildLive(DumpAccTable* acc) {
    accInit(acc, 8192);
    RLOCK();
    for (int i = 0; i < g_regN; ++i) {
        uint32_t r = g_reg[i].rec;
        if (r == REC_NONE) continue;
        accAdd(acc, r, (uint64_t)(g_reg[i].e - g_reg[i].s), 1u, 0, false);
    }
    RUNLOCK();
}

// ---------------- PSS 分桶（进程内解析 smaps） ----------------
// 总量取 smaps 的 Pss + SwapPss（按定义等于进程 PSS），归因用区间表：
// 对 VMA [start, end) 与 g_reg 的所有重叠段按重叠字节占比拆分归因到各自提交栈；
// 剩余未命中部分按路径细粒度分桶（malloc/stack/bss/jit-cache/ashmem/memfd/graphics/java/shared/code/file-mmap/anon）。
enum {
    CAT_ANON = 0, CAT_MALLOC, CAT_CODE, CAT_GFX, CAT_JAVA, CAT_SHARED,
    CAT_STACK, CAT_BSS, CAT_JIT, CAT_ASHMEM, CAT_MEMFD, CAT_FILE, CAT_MAX
};
static const char* kCatName[CAT_MAX] = {
    "anon", "malloc", "code", "graphics", "java", "shared",
    "stack", "bss", "jit-cache", "ashmem", "memfd", "file-mmap"
};

struct SubBucket {
    char     name[64];
    uint64_t bytes;
    uint32_t vmas;
    uint8_t  cat;
};
#define SUB_BUCKET_CAP 128
static SubBucket g_pssSub[SUB_BUCKET_CAP];
static int       g_pssSubN = 0;

static uint64_t g_pssCat[CAT_MAX];
static uint32_t g_pssVmaCat[CAT_MAX];

static void formatSubBucketName(int cat, const char* path, const char* perms, char* out, int cap) {
    out[0] = 0;
    if (!path) path = "";
    char clean[128];
    const char* slash = strrchr(path, '/');
    const char* base = slash ? (slash + 1) : path;
    int cn = 0;
    for (int i = 0; base[i] && cn < (int)sizeof(clean) - 1; ++i) {
        if (strncmp(base + i, " (deleted)", 10) == 0) break;
        clean[cn++] = base[i];
    }
    clean[cn] = 0;

    if (cat == CAT_CODE) {
        const char* pstr = (perms && perms[0]) ? perms : "----";
        if (strstr(clean, "libil2cpp.so"))      snprintf(out, cap, "code:libil2cpp.so (%s)", pstr);
        else if (strstr(clean, "libunity.so"))  snprintf(out, cap, "code:libunity.so (%s)", pstr);
        else if (strstr(clean, ".so"))          snprintf(out, cap, "code:%.55s", clean);
        else if (strstr(clean, ".ttf") || strstr(clean, ".ttc")) snprintf(out, cap, "code:font (.ttf/.ttc)");
        else if (strstr(clean, ".apk") || strstr(clean, ".jar")) snprintf(out, cap, "code:%.55s", clean);
        else                                    snprintf(out, cap, "code:oat/dex/vdex");
        return;
    }
    if (cat == CAT_JAVA) {
        if (strstr(path, "dalvik-main space") || strstr(path, "allocspace") ||
            strstr(path, "zygote space") || strstr(path, "non moving space") || strstr(path, "large object"))
            snprintf(out, cap, "java:Dalvik堆");
        else if (strstr(path, "LinearAlloc"))
            snprintf(out, cap, "java:LinearAlloc");
        else if (strstr(path, ".dex"))
            snprintf(out, cap, "java:dex");
        else if (strstr(path, ".art"))
            snprintf(out, cap, "java:art");
        else
            snprintf(out, cap, "java:其他Java/ART");
        return;
    }
    if (cat == CAT_FILE) {
        if (clean[0]) snprintf(out, cap, "file-mmap:%.52s", clean);
        else          snprintf(out, cap, "file-mmap");
        return;
    }
}

static void addSubBucketOrCat(int cat, const char* path, const char* perms, uint64_t bytes) {
    if (cat == CAT_CODE || cat == CAT_JAVA || cat == CAT_FILE) {
        char subName[64];
        formatSubBucketName(cat, path, perms, subName, sizeof(subName));
        if (subName[0]) {
            for (int i = 0; i < g_pssSubN; ++i) {
                if (g_pssSub[i].cat == (uint8_t)cat && strcmp(g_pssSub[i].name, subName) == 0) {
                    g_pssSub[i].bytes += bytes;
                    g_pssSub[i].vmas++;
                    return;
                }
            }
            if (g_pssSubN < SUB_BUCKET_CAP) {
                SubBucket* sb = &g_pssSub[g_pssSubN++];
                strncpy(sb->name, subName, sizeof(sb->name) - 1);
                sb->name[sizeof(sb->name) - 1] = 0;
                sb->bytes = bytes;
                sb->vmas = 1;
                sb->cat = (uint8_t)cat;
                return;
            }
        }
    }
    g_pssCat[cat] += bytes;
    g_pssVmaCat[cat]++;
}

static int categorizePath(const char* p) {
    if (!p || !*p) return CAT_ANON;
    if (strstr(p, "libc_malloc") || strstr(p, "scudo") || strstr(p, "[heap]")) return CAT_MALLOC;
    if (strstr(p, "[stack") || strstr(p, "stack_and_tls") || strstr(p, "signal stack")) return CAT_STACK;
    if (strstr(p, "[anon:.bss]") || strstr(p, ":.bss]")) return CAT_BSS;
    if (strstr(p, "jit-cache") || strstr(p, "jit-zygote-cache") || strstr(p, "jit-code-cache")) return CAT_JIT;
    if (strstr(p, "ashmem")) return CAT_ASHMEM;
    if (strstr(p, "/memfd:")) return CAT_MEMFD;
    if (strstr(p, "kgsl") || strstr(p, "dma_heap") || strstr(p, "dmabuf") || strstr(p, "mali") || strstr(p, "/dev/ion") || strstr(p, "/dev/dri"))
        return CAT_GFX;
    if (strstr(p, "/dev/binder") || strstr(p, "/dev/__properties__")) return CAT_SHARED;
    if (strstr(p, "/dev/")) return CAT_GFX;
    if (strstr(p, "dalvik") || strstr(p, "art]") || strstr(p, "dalvik-")) return CAT_JAVA;
    if (strstr(p, "/system/") || strstr(p, "/apex/") || strstr(p, "/vendor/") || strstr(p, "/product/") || strstr(p, "/odm/"))
        return CAT_SHARED;
    if (strstr(p, ".so") || strstr(p, ".oat") || strstr(p, ".odex") || strstr(p, ".dex") ||
        strstr(p, ".vdex") || strstr(p, ".apk") || strstr(p, ".jar") || strstr(p, ".ttf") || strstr(p, ".ttc"))
        return CAT_CODE;
    if (strstr(p, ".dat") || strstr(p, "-shm") || strstr(p, ".resS") || strstr(p, ".obb") ||
        strstr(p, ".pak") || strstr(p, ".bundle") || strstr(p, "/storage/") || strstr(p, "/data/"))
        return CAT_FILE;
    return CAT_ANON;
}

static inline int hexDigit(char ch) {
    if (ch >= '0' && ch <= '9') return ch - '0';
    if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
    if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
    return -1;
}

static inline uint64_t parseKbVal(const char* s) {
    while (*s == ' ' || *s == '\t') s++;
    uint64_t kb = 0;
    while (*s >= '0' && *s <= '9') { kb = kb * 10 + (uint64_t)(*s - '0'); s++; }
    return kb;
}

static void flushVmaPss(DumpAccTable* pssAcc, uintptr_t curStart, uintptr_t curEnd, int curCat,
                        const char* curPath, const char* curPerms, uint64_t totalKb) {
    if (!totalKb) return;
    uint64_t totalBytes = totalKb * 1024ULL;
    uint64_t vmaLen = (curEnd > curStart) ? (uint64_t)(curEnd - curStart) : 0;
    if (vmaLen == 0) {
        addSubBucketOrCat(curCat, curPath, curPerms, totalBytes);
        return;
    }

    uint64_t attributed = 0;
    RLOCK();
    for (int i = regLowerBound(curStart); i < g_regN && g_reg[i].s < curEnd; ++i) {
        uint32_t rec = g_reg[i].rec;
        if (rec == REC_NONE) continue;
        uintptr_t lo = g_reg[i].s > curStart ? g_reg[i].s : curStart;
        uintptr_t hi = g_reg[i].e < curEnd ? g_reg[i].e : curEnd;
        if (hi <= lo) continue;
        uint64_t ov = (uint64_t)(hi - lo);
        uint64_t share = (ov == vmaLen)
            ? totalBytes
            : (uint64_t)(((double)totalBytes * ((double)ov / (double)vmaLen)) + 0.5);
        if (attributed + share > totalBytes) share = totalBytes - attributed;
        if (share > 0) {
            accAdd(pssAcc, rec, share, 1u, (uint8_t)curCat, true);
            attributed += share;
        }
        if (attributed >= totalBytes) break;
    }
    RUNLOCK();

    if (attributed < totalBytes) {
        addSubBucketOrCat(curCat, curPath, curPerms, totalBytes - attributed);
    }
}

static void buildPss(DumpAccTable* pssAcc) {
    accInit(pssAcc, 8192);
    memset(g_pssCat, 0, sizeof(g_pssCat));
    memset(g_pssVmaCat, 0, sizeof(g_pssVmaCat));
    g_pssSubN = 0;

    int fd = open("/proc/self/smaps", O_RDONLY);
    if (fd < 0) return;

    static char buf[65536];
    static char line[1024];
    static char curPath[512];
    static char curPerms[8];
    int ll = 0;
    uintptr_t curStart = 0, curEnd = 0;
    uint64_t curPssKb = 0, curSwapPssKb = 0;
    int curCat = CAT_ANON, haveVma = 0;
    curPath[0] = 0;
    curPerms[0] = 0;

    for (;;) {
        ssize_t n = read(fd, buf, sizeof(buf));
        if (n <= 0) break;
        for (ssize_t i = 0; i < n; ++i) {
            char c = buf[i];
            if (c != '\n') {
                if (ll < (int)sizeof(line) - 1) line[ll++] = c;
                continue;
            }
            line[ll] = 0;
            ll = 0;

            if (hexDigit(line[0]) >= 0 && strchr(line, '-')) {
                // VMA 头：start-end perms offset dev inode path
                const char* s = line;
                uintptr_t st = 0, ed = 0;
                int d;
                while ((d = hexDigit(*s)) >= 0) { st = st * 16 + (uintptr_t)d; s++; }
                if (*s == '-') {
                    s++;
                    int hasEnd = 0;
                    while ((d = hexDigit(*s)) >= 0) { ed = ed * 16 + (uintptr_t)d; s++; hasEnd = 1; }
                    if (hasEnd && (*s == ' ' || *s == '\t')) {
                        if (haveVma) flushVmaPss(pssAcc, curStart, curEnd, curCat, curPath, curPerms, curPssKb + curSwapPssKb);

                        while (*s == ' ' || *s == '\t') s++;
                        int pi = 0;
                        while (*s && *s != ' ' && *s != '\t' && pi < 4) curPerms[pi++] = *s++;
                        curPerms[pi] = 0;

                        const char* path = 0;
                        for (const char* q = s; *q; ++q) {
                            if (*q == '/' || *q == '[') { path = q; break; }
                        }
                        if (!path) {
                            // 如 dmabuf: 等无 '/' 无 '[' 路径，取第 6 列（此时已过 perms，从 col=1 开始）
                            const char* q = s;
                            int col = 1;
                            while (*q) {
                                while (*q == ' ' || *q == '\t') q++;
                                if (!*q) break;
                                col++;
                                if (col >= 5) { path = q; break; }
                                while (*q && *q != ' ' && *q != '\t') q++;
                            }
                        }
                        if (path) {
                            strncpy(curPath, path, sizeof(curPath) - 1);
                            curPath[sizeof(curPath) - 1] = 0;
                        } else {
                            curPath[0] = 0;
                        }
                        curStart = st;
                        curEnd = ed;
                        curCat = categorizePath(curPath);
                        curPssKb = 0;
                        curSwapPssKb = 0;
                        haveVma = 1;
                    }
                }
            } else if (haveVma) {
                if (strncmp(line, "Pss:", 4) == 0) {
                    curPssKb = parseKbVal(line + 4);
                } else if (strncmp(line, "SwapPss:", 8) == 0) {
                    curSwapPssKb = parseKbVal(line + 8);
                }
            }
        }
    }
    if (haveVma) flushVmaPss(pssAcc, curStart, curEnd, curCat, curPath, curPerms, curPssKb + curSwapPssKb);
    close(fd);
}

// ---------------- 64KB 缓冲写器 ----------------
static char g_dumpIoBuf[65536];
static volatile int g_dumpLock = 0;
static inline void DUMP_LOCK()   { while (__sync_lock_test_and_set(&g_dumpLock, 1)) { } }
static inline void DUMP_UNLOCK() { __sync_lock_release(&g_dumpLock); }

struct BufWriter {
    int    fd;
    char*  buf;
    size_t cap;
    size_t pos;
    int    err;
};

static void bwInit(BufWriter* bw, int fd, char* buf, size_t cap) {
    bw->fd = fd; bw->buf = buf; bw->cap = cap; bw->pos = 0; bw->err = 0;
}

static void bwFlushRaw(BufWriter* bw, const char* data, size_t len) {
    if (bw->err || len == 0) return;
    size_t off = 0;
    while (off < len) {
        ssize_t w = write(bw->fd, data + off, len - off);
        if (w < 0) {
            if (errno == EINTR) continue;
            bw->err = 1;
            return;
        }
        if (w == 0) { bw->err = 1; return; }
        off += (size_t)w;
    }
}

static void bwFlush(BufWriter* bw) {
    if (bw->pos > 0) {
        bwFlushRaw(bw, bw->buf, bw->pos);
        bw->pos = 0;
    }
}

static void bwWrite(BufWriter* bw, const void* data, size_t len) {
    if (bw->err || len == 0) return;
    if (len >= bw->cap) {
        bwFlush(bw);
        bwFlushRaw(bw, (const char*)data, len);
        return;
    }
    if (bw->pos + len > bw->cap) bwFlush(bw);
    memcpy(bw->buf + bw->pos, data, len);
    bw->pos += len;
}

static inline void bwPutU8(BufWriter* bw, uint8_t v)   { bwWrite(bw, &v, 1); }
static inline void bwPutU16(BufWriter* bw, uint16_t v) { bwWrite(bw, &v, 2); }
static inline void bwPutU32(BufWriter* bw, uint32_t v) { bwWrite(bw, &v, 4); }
static inline void bwPutU64(BufWriter* bw, uint64_t v) { bwWrite(bw, &v, 8); }

static inline const char* threadNameFromId(uint16_t tid, uint32_t snapThreadCnt) {
    if (tid < snapThreadCnt && g_threads[tid].name[0]) return g_threads[tid].name;
    return "-";
}

// ---------------- 文本 dump（64KB 缓冲 + 原子 rename） ----------------
static int dumpTextCore() {
    char path[256], tmpPath[256];
    makePath(path, sizeof(path), "/allochook.txt");
    makePath(tmpPath, sizeof(tmpPath), "/allochook.txt.tmp");
    int fd = open(tmpPath, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (fd < 0) return 0;

    BufWriter bw;
    bwInit(&bw, fd, g_dumpIoBuf, sizeof(g_dumpIoBuf));

    uint32_t snapStackCnt[TBL_SHARDS];
    for (uint32_t sh = 0; sh < TBL_SHARDS; ++sh) {
        TBL_LOCK(sh);
        snapStackCnt[sh] = g_tblShards[sh].count;
        TBL_UNLOCK(sh);
    }
    THREAD_LOCK();
    uint32_t snapThreadCnt = g_threadCount;
    THREAD_UNLOCK();

    char line[1024];
    int recs = 0;
    StackSlot batch[256];
    for (uint32_t sh = 0; sh < TBL_SHARDS; ++sh) {
        StackShard* s = &g_tblShards[sh];
        uint32_t total = snapStackCnt[sh];
        for (uint32_t off = 0; off < total; off += 256) {
            uint32_t n = total - off;
            if (n > 256) n = 256;
            TBL_LOCK(sh);
            for (uint32_t j = 0; j < n; ++j) batch[j] = *stackSlotAt(s, off + j);
            TBL_UNLOCK(sh);

            for (uint32_t j = 0; j < n; ++j) {
                const StackSlot& r = batch[j];
                if (!r.count) continue;
                recs++;
                const char* tn = threadNameFromId(r.threadId, snapThreadCnt);
                const uint32_t* pcIds = arenaPtr(s, r.arenaOff);
                int k = snprintf(line, sizeof(line), "%u %s %u %llu %llu",
                                 (unsigned)r.kind, tn, (unsigned)r.nf,
                                 (unsigned long long)r.bytes, (unsigned long long)r.count);
                for (uint32_t f = 0; f < r.nf && k < (int)sizeof(line) - 24; ++f)
                    k += snprintf(line + k, sizeof(line) - k, " %llx", (unsigned long long)pcFromId(pcIds[f]));
                if (k > 0 && k < (int)sizeof(line) - 2) { line[k++] = '\n'; bwWrite(&bw, line, (size_t)k); }
            }
        }
    }

    DumpAccTable liveAcc;
    buildLive(&liveAcc);
    if (liveAcc.ents) {
        for (uint32_t i = 0; i < liveAcc.cap; ++i) {
            const DumpAccEnt& e = liveAcc.ents[i];
            if (e.stackId == REC_NONE || !e.bytes) continue;
            const StackSlot* r = tblGetSlot(e.stackId);
            if (!r || r->nf == 0) continue;
            const StackShard* s = &g_tblShards[e.stackId >> 24];
            const uint32_t* pcIds = arenaPtr(s, r->arenaOff);
            const char* tn = threadNameFromId(r->threadId, snapThreadCnt);
            int k2 = snprintf(line, sizeof(line), "%u %s %u %llu %llu",
                              (unsigned)K_LIVE, tn, (unsigned)r->nf,
                              (unsigned long long)e.bytes, (unsigned long long)e.cnt);
            for (uint32_t j = 0; j < r->nf && k2 < (int)sizeof(line) - 24; ++j)
                k2 += snprintf(line + k2, sizeof(line) - k2, " %llx", (unsigned long long)pcFromId(pcIds[j]));
            if (k2 > 0 && k2 < (int)sizeof(line) - 2) { line[k2++] = '\n'; bwWrite(&bw, line, (size_t)k2); recs++; }
        }
    }
    accDestroy(&liveAcc);

    DumpAccTable pssAcc;
    buildPss(&pssAcc);
    if (pssAcc.ents) {
        for (uint32_t i = 0; i < pssAcc.cap; ++i) {
            const DumpAccEnt& e = pssAcc.ents[i];
            if (e.stackId == REC_NONE || !e.bytes) continue;
            const StackSlot* r = tblGetSlot(e.stackId);
            if (!r || r->nf == 0) continue;
            const StackShard* s = &g_tblShards[e.stackId >> 24];
            const uint32_t* pcIds = arenaPtr(s, r->arenaOff);
            const char* tn = threadNameFromId(r->threadId, snapThreadCnt);
            int k3 = snprintf(line, sizeof(line), "%u %s %u %llu %llu",
                              (unsigned)K_PSS, tn, (unsigned)r->nf,
                              (unsigned long long)e.bytes, (unsigned long long)e.cnt);
            for (uint32_t j = 0; j < r->nf && k3 < (int)sizeof(line) - 24; ++j)
                k3 += snprintf(line + k3, sizeof(line) - k3, " %llx", (unsigned long long)pcFromId(pcIds[j]));
            if (k3 > 0 && k3 < (int)sizeof(line) - 2) { line[k3++] = '\n'; bwWrite(&bw, line, (size_t)k3); recs++; }
        }
    }
    accDestroy(&pssAcc);

    for (int c = 0; c < CAT_MAX; ++c) {
        if (!g_pssCat[c]) continue;
        int k4 = snprintf(line, sizeof(line), "%u %s 0 %llu %llu",
                          (unsigned)K_PSS, kCatName[c],
                          (unsigned long long)g_pssCat[c], (unsigned long long)g_pssVmaCat[c]);
        if (k4 > 0 && k4 < (int)sizeof(line) - 2) { line[k4++] = '\n'; bwWrite(&bw, line, (size_t)k4); recs++; }
    }
    for (int i = 0; i < g_pssSubN; ++i) {
        if (!g_pssSub[i].bytes) continue;
        int k5 = snprintf(line, sizeof(line), "%u %s 0 %llu %llu",
                          (unsigned)K_PSS, g_pssSub[i].name,
                          (unsigned long long)g_pssSub[i].bytes, (unsigned long long)g_pssSub[i].vmas);
        if (k5 > 0 && k5 < (int)sizeof(line) - 2) { line[k5++] = '\n'; bwWrite(&bw, line, (size_t)k5); recs++; }
    }

    bwFlush(&bw);
    close(fd);
    if (!bw.err) rename(tmpPath, path);
    else unlink(tmpPath);
    return recs;
}

static int dumpText() {
    DUMP_LOCK();
    int r = dumpTextCore();
    DUMP_UNLOCK();
    return r;
}

static void sumStats(long long* outFb, long long* outFc, long long* outSb,
                     long long* outFlb, long long* outFlc, long long* outSlb, long long* outSlc) {
    long long fb = 0, fc = 0, sb = 0, flb = 0, flc = 0, slb = 0, slc = 0;
    for (int i = 0; i < 64; ++i) {
        fb  += __atomic_load_n(&g_statShards[i].filteredBytes,     __ATOMIC_RELAXED);
        fc  += __atomic_load_n(&g_statShards[i].filteredCount,     __ATOMIC_RELAXED);
        sb  += __atomic_load_n(&g_statShards[i].sampledBytes,      __ATOMIC_RELAXED);
        flb += __atomic_load_n(&g_statShards[i].filteredLiveBytes, __ATOMIC_RELAXED);
        flc += __atomic_load_n(&g_statShards[i].filteredLiveCount, __ATOMIC_RELAXED);
        slb += __atomic_load_n(&g_statShards[i].sampledLiveBytes,  __ATOMIC_RELAXED);
        slc += __atomic_load_n(&g_statShards[i].sampledLiveCount,  __ATOMIC_RELAXED);
    }
    if (fb  < 0) fb  = 0;
    if (fc  < 0) fc  = 0;
    if (sb  < 0) sb  = 0;
    if (flc <= 0) { flc = 0; flb = 0; }
    else if (flb < 0) flb = 0;
    if (slc <= 0) { slc = 0; slb = 0; }
    else if (slb < 0) slb = 0;
    *outFb = fb; *outFc = fc; *outSb = sb;
    *outFlb = flb; *outFlc = flc; *outSlb = slb; *outSlc = slc;
}

// ---------------- 二进制 dump (AHK1 v3：64KB 缓冲 + 线程表 + PC 字典 + 原子 rename) ----------------
// 须在持有 g_dumpLock 时调用
static int dumpBinaryCore() {
    static char mods[16384];
    snprintf(mods, sizeof(mods), "{\"mods\":[");
    dl_iterate_phdr(addModule, mods);            // addModule 内部按需补逗号
    snprintf(mods + strlen(mods), sizeof(mods) - strlen(mods), "]}");
    char* buf = mods;

    char path[256], tmpPath[256];
    makePath(path, sizeof(path), "/allochook.bin");
    makePath(tmpPath, sizeof(tmpPath), "/allochook.bin.tmp");
    int fd = open(tmpPath, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (fd < 0) return 0;

    BufWriter bw;
    bwInit(&bw, fd, g_dumpIoBuf, sizeof(g_dumpIoBuf));

    // 头部
    bwWrite(&bw, "AHK1", 4);
    bwPutU32(&bw, 3);              // version 3：线程字符串表 + 去重 PC 字典 + uint16 threadIdx + uint32 pcIdx[]

    long long fb = 0, fc = 0, sb = 0, flb = 0, flc = 0, slb = 0, slc = 0;
    sumStats(&fb, &fc, &sb, &flb, &flc, &slb, &slc);
    struct mallinfo mi = mallinfo();
    unsigned long long nativeHeapAllocBytes = (unsigned long long)mi.uordblks;

    char hdr[20000];
    int hn = snprintf(hdr, sizeof(hdr),
        "{\"pid\":%d,\"ts\":%lld,\"level\":\"%s\",\"maxFrames\":%d,\"minBytes\":%d,"
        "\"holdMin\":%d,\"holdFrames\":%d,"
        "\"l2Enabled\":%d,\"l2Min\":%d,\"l2Sample\":%d,"
        "\"l2FilteredBytes\":%lld,\"l2FilteredCount\":%lld,\"l2SampledBytes\":%lld,"
        "\"l2FilteredLiveBytes\":%lld,\"l2FilteredLiveCount\":%lld,"
        "\"l2SampledLiveBytes\":%lld,\"l2SampledLiveCount\":%lld,"
        "\"nativeHeapAllocBytes\":%llu,%s",
        (int)getpid(), (long long)time(0),
        g_holdEnabled ? "pages+hold" : (g_l2Enabled ? "pages+l2" : "pages"),
        g_maxFrames, g_minBytes, g_holdMin, g_holdFrames,
        g_l2Enabled, g_l2Min, g_l2Sample,
        fb, fc, sb,
        flb, flc, slb, slc, nativeHeapAllocBytes,
        buf + 1); // buf+1 跳过 '{'

    bwPutU32(&bw, (uint32_t)hn);
    bwWrite(&bw, hdr, (size_t)hn);

    // 先算 L1 存量与 PSS 归属，再按反依赖顺序（栈 → PC → 线程）拍快照，保证记录引用永不越界
    DumpAccTable liveAcc;
    buildLive(&liveAcc);
    DumpAccTable pssAcc;
    buildPss(&pssAcc);

    uint32_t snapStackCnt[TBL_SHARDS];
    for (uint32_t sh = 0; sh < TBL_SHARDS; ++sh) {
        TBL_LOCK(sh);
        snapStackCnt[sh] = g_tblShards[sh].count;
        TBL_UNLOCK(sh);
    }
    uint32_t snapPcCnt[PC_SHARDS];
    for (uint32_t s = 0; s < PC_SHARDS; ++s) {
        PC_LOCK(s);
        snapPcCnt[s] = g_pcShards[s].count;
        PC_UNLOCK(s);
    }
    THREAD_LOCK();
    uint32_t snapThreadCnt = g_threadCount;
    THREAD_UNLOCK();

    // 1. 线程字符串表（包含运行时线程 + PSS 主分类/子分桶名）
    uint16_t pssCatTidx[CAT_MAX];
    uint16_t pssSubTidx[SUB_BUCKET_CAP];
    uint32_t extraThreads = 0;
    for (int c = 0; c < CAT_MAX; ++c) {
        if (g_pssCat[c]) { pssCatTidx[c] = (uint16_t)(snapThreadCnt + extraThreads++); }
        else pssCatTidx[c] = 0;
    }
    for (int i = 0; i < g_pssSubN; ++i) {
        if (g_pssSub[i].bytes) { pssSubTidx[i] = (uint16_t)(snapThreadCnt + extraThreads++); }
        else pssSubTidx[i] = 0;
    }
    uint32_t totalThreads = snapThreadCnt + extraThreads;
    bwPutU32(&bw, totalThreads);
    for (uint32_t i = 0; i < snapThreadCnt; ++i) {
        uint8_t len = g_threads[i].len;
        bwPutU8(&bw, len);
        if (len) bwWrite(&bw, g_threads[i].name, len);
    }
    for (int c = 0; c < CAT_MAX; ++c) {
        if (!g_pssCat[c]) continue;
        const char* nm = kCatName[c];
        uint8_t len = (uint8_t)strlen(nm);
        bwPutU8(&bw, len);
        if (len) bwWrite(&bw, nm, len);
    }
    for (int i = 0; i < g_pssSubN; ++i) {
        if (!g_pssSub[i].bytes) continue;
        const char* nm = g_pssSub[i].name;
        uint8_t len = (uint8_t)strlen(nm);
        bwPutU8(&bw, len);
        if (len) bwWrite(&bw, nm, len);
    }

    // 2. 去重 PC 字典（按分片连续编号）
    uint32_t pcShardOff[PC_SHARDS];
    uint32_t totalPcs = 0;
    for (uint32_t s = 0; s < PC_SHARDS; ++s) {
        pcShardOff[s] = totalPcs;
        totalPcs += snapPcCnt[s];
    }
    bwPutU32(&bw, totalPcs);
    for (uint32_t s = 0; s < PC_SHARDS; ++s) {
        uint32_t cnt = snapPcCnt[s];
        const PcShard* ps = &g_pcShards[s];
        for (uint32_t i = 0; i < cnt; ) {
            uint32_t slabIdx = i >> PC_SLAB_SHIFT;
            uint32_t inSlab = i & (PC_SLAB_CAP - 1);
            uint32_t take = PC_SLAB_CAP - inSlab;
            if (take > cnt - i) take = cnt - i;
            bwWrite(&bw, ps->slabs[slabIdx] + inSlab, (size_t)take * sizeof(uint64_t));
            i += take;
        }
    }

    auto pcIdToDict = [&](uint32_t pcId) -> uint32_t {
        return pcShardOff[pcId >> 24] + (pcId & 0xFFFFFFu);
    };

    // 3. 记录载荷（批拷贝释放分片锁后再写 64KB 缓冲，避免阻塞应用线程）
    int recs = 0;
    StackSlot batch[256];
    uint32_t pcIdxBuf[MAXF];

    for (uint32_t sh = 0; sh < TBL_SHARDS; ++sh) {
        StackShard* s = &g_tblShards[sh];
        uint32_t total = snapStackCnt[sh];
        for (uint32_t off = 0; off < total; off += 256) {
            uint32_t n = total - off;
            if (n > 256) n = 256;
            TBL_LOCK(sh);
            for (uint32_t k = 0; k < n; ++k) batch[k] = *stackSlotAt(s, off + k);
            TBL_UNLOCK(sh);

            for (uint32_t k = 0; k < n; ++k) {
                const StackSlot& r = batch[k];
                if (!r.count) continue;
                recs++;
                bwPutU8(&bw, r.kind);
                bwPutU16(&bw, r.threadId);
                bwPutU16(&bw, (uint16_t)r.nf);
                bwPutU64(&bw, r.bytes);
                bwPutU64(&bw, r.count);
                bwPutU64(&bw, r.aux);
                const uint32_t* ap = arenaPtr(s, r.arenaOff);
                for (uint32_t j = 0; j < r.nf; ++j) pcIdxBuf[j] = pcIdToDict(ap[j]);
                if (r.nf) bwWrite(&bw, pcIdxBuf, (size_t)r.nf * sizeof(uint32_t));
            }
        }
    }

    // 存量视图（K_LIVE）
    if (liveAcc.ents) {
        for (uint32_t i = 0; i < liveAcc.cap; ++i) {
            const DumpAccEnt& e = liveAcc.ents[i];
            if (e.stackId == REC_NONE || !e.bytes) continue;
            const StackSlot* r = tblGetSlot(e.stackId);
            if (!r || r->nf == 0 || r->nf > MAXF) continue;
            const StackShard* s = &g_tblShards[e.stackId >> 24];
            bwPutU8(&bw, (uint8_t)K_LIVE);
            bwPutU16(&bw, r->threadId);
            bwPutU16(&bw, (uint16_t)r->nf);
            bwPutU64(&bw, e.bytes);
            bwPutU64(&bw, (uint64_t)e.cnt);
            bwPutU64(&bw, 0);
            const uint32_t* ap = arenaPtr(s, r->arenaOff);
            for (uint32_t j = 0; j < r->nf; ++j) pcIdxBuf[j] = pcIdToDict(ap[j]);
            bwWrite(&bw, pcIdxBuf, (size_t)r->nf * sizeof(uint32_t));
            recs++;
        }
    }
    accDestroy(&liveAcc);

    // PSS 分桶（kind=5）
    if (pssAcc.ents) {
        for (uint32_t i = 0; i < pssAcc.cap; ++i) {
            const DumpAccEnt& e = pssAcc.ents[i];
            if (e.stackId == REC_NONE || !e.bytes) continue;
            const StackSlot* r = tblGetSlot(e.stackId);
            if (!r || r->nf == 0 || r->nf > MAXF) continue;
            const StackShard* s = &g_tblShards[e.stackId >> 24];
            bwPutU8(&bw, (uint8_t)K_PSS);
            bwPutU16(&bw, r->threadId);
            bwPutU16(&bw, (uint16_t)r->nf);
            bwPutU64(&bw, e.bytes);
            bwPutU64(&bw, (uint64_t)e.cnt);
            bwPutU64(&bw, (uint64_t)e.cat);
            const uint32_t* ap = arenaPtr(s, r->arenaOff);
            for (uint32_t j = 0; j < r->nf; ++j) pcIdxBuf[j] = pcIdToDict(ap[j]);
            bwWrite(&bw, pcIdxBuf, (size_t)r->nf * sizeof(uint32_t));
            recs++;
        }
    }
    accDestroy(&pssAcc);

    for (int c = 0; c < CAT_MAX; ++c) {
        if (!g_pssCat[c]) continue;
        bwPutU8(&bw, (uint8_t)K_PSS);
        bwPutU16(&bw, pssCatTidx[c]);
        bwPutU16(&bw, 0);
        bwPutU64(&bw, g_pssCat[c]);
        bwPutU64(&bw, (uint64_t)g_pssVmaCat[c]);
        bwPutU64(&bw, (uint64_t)c);
        recs++;
    }
    for (int i = 0; i < g_pssSubN; ++i) {
        if (!g_pssSub[i].bytes) continue;
        bwPutU8(&bw, (uint8_t)K_PSS);
        bwPutU16(&bw, pssSubTidx[i]);
        bwPutU16(&bw, 0);
        bwPutU64(&bw, g_pssSub[i].bytes);
        bwPutU64(&bw, (uint64_t)g_pssSub[i].vmas);
        bwPutU64(&bw, (uint64_t)g_pssSub[i].cat);
        recs++;
    }

    bwFlush(&bw);
    close(fd);
    if (!bw.err) rename(tmpPath, path);
    else unlink(tmpPath);
    return recs;
}

static void dumpMaps() {
    int in = open("/proc/self/maps", O_RDONLY);
    if (in < 0) return;
    char path[256], tmpPath[256];
    makePath(path, sizeof(path), "/allochook_maps.txt");
    makePath(tmpPath, sizeof(tmpPath), "/allochook_maps.txt.tmp");
    int out = open(tmpPath, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (out < 0) { close(in); return; }

    // 首行写 pid：宿主侧用它校验 maps 与 dump 是否来自同一个进程。
    // 进程重启会让两边对不上，符号化就会全是 (anon)。
    {
        char hdr[96];
        int hn = snprintf(hdr, sizeof(hdr), "# allochook pid=%d ts=%lld\n",
                          (int)getpid(), (long long)time(0));
        if (hn > 0) write(out, hdr, (size_t)hn);
    }

    static char buf[65536];
    int err = 0;
    for (;;) {
        ssize_t n = read(in, buf, sizeof(buf));
        if (n <= 0) break;
        ssize_t off = 0;
        while (off < n) {
            ssize_t w = write(out, buf + off, (size_t)(n - off));
            if (w < 0) { if (errno == EINTR) continue; err = 1; break; }
            if (w == 0) { err = 1; break; }
            off += w;
        }
        if (err) break;
    }
    close(out); close(in);
    if (!err) rename(tmpPath, path);
    else unlink(tmpPath);
}

static void dumpAll(const char* why) {
    // 必须每次快照都重 dump maps：
    // 进程启动时 dump 的那份只有 libc/zygote，没有 libunity/libil2cpp，
    // 用它符号化会全是 (anon)。快照时刻的 maps 才是完整的。
    DUMP_LOCK();
    dumpMaps();
    int brecs = g_dumpBinary ? dumpBinaryCore() : 0;
    int recs  = (!g_dumpBinary || g_dumpTxt) ? dumpTextCore() : 0;
    DUMP_UNLOCK();
    LOGI("dump(%s) text=%d bin=%d -> %s", why, recs, brecs, g_dir);
}

// ---------------- 持有者追踪（Unity 分配器 / L2 malloc 家族，64 分片 mmap 动态扩容 + 零墓碑） ----------------
// 只统计"存活"，并与 L1 的页级口径严格分开。
// 初始 4096 槽/分片（64 分片共 4MB），负载 >= 70% 时单分片倍增，最高可支持 16M 存活指针；
// 线性探测 + 后移删除（零墓碑），外加 262,144 项 64KB 页原子计数表 g_pageCnt。
#define HOLD_SHARDS      64
#define HOLD_INIT_CAP    4096u
#define HOLD_MAX_CAP     262144u
#define PAGE_CNT_BITS    18
#define PAGE_CNT_CAP     (1u << PAGE_CNT_BITS)

struct HoldEnt { uintptr_t ptr; uint32_t size; uint32_t idx; };
struct HoldShard {
    volatile int lock;
    uint32_t     cap;
    uint32_t     count;
    HoldEnt*     arr;
};
static HoldShard g_hold[HOLD_SHARDS];
static uint32_t  g_pageCnt[PAGE_CNT_CAP];

static void (*real_free)(void*) = 0;

static inline void HOLD_LOCK(uint32_t sh)   { while (__sync_lock_test_and_set(&g_hold[sh].lock, 1)) { } }
static inline void HOLD_UNLOCK(uint32_t sh) { __sync_lock_release(&g_hold[sh].lock); }

static inline uint32_t pageIdx(uintptr_t p) {
    uint64_t pg = (uint64_t)p >> 16;
    return (uint32_t)((pg ^ (pg >> 18)) & (PAGE_CNT_CAP - 1));
}

static inline uint64_t holdMix(uintptr_t p) {
    return ((uint64_t)p >> 4) * 11400714819323198485ULL;
}
static inline uint32_t holdShard(uint64_t m) { return (uint32_t)(m >> 58); }
static inline uint32_t holdSlot(uint64_t m, uint32_t mask) { return (uint32_t)((m >> 26) & mask); }

static bool holdGrow(HoldShard* s) {
    uint32_t newCap = s->arr ? (s->cap * 2) : HOLD_INIT_CAP;
    if (newCap > HOLD_MAX_CAP) return false;
    HoldEnt* na = (HoldEnt*)rawMmapAnon((size_t)newCap * sizeof(HoldEnt));
    if (!na) return false;
    uint32_t newMask = newCap - 1;
    if (s->arr) {
        for (uint32_t i = 0; i < s->cap; ++i) {
            if (s->arr[i].ptr == 0) continue;
            uint32_t pos = holdSlot(holdMix(s->arr[i].ptr), newMask);
            while (na[pos].ptr != 0) pos = (pos + 1) & newMask;
            na[pos] = s->arr[i];
        }
        rawMunmap(s->arr, (size_t)s->cap * sizeof(HoldEnt));
    }
    s->arr = na;
    s->cap = newCap;
    return true;
}

// 单次加锁完成插入/更新；若同地址原本已存在，带回 oldSize/oldIdx 供调用方扣减旧值
static bool holdUpsert(uintptr_t p, uint32_t size, uint32_t idx, uint32_t* oldSize, uint32_t* oldIdx) {
    *oldIdx = REC_NONE;
    *oldSize = 0;
    if (p <= 1) return false;
    uint64_t m = holdMix(p);
    uint32_t sh = holdShard(m);
    HoldShard* hs = &g_hold[sh];
    bool ok = false;
    HOLD_LOCK(sh);
    if (!hs->arr || ((hs->count + 1) * 10 >= hs->cap * 7 && hs->cap < HOLD_MAX_CAP)) {
        holdGrow(hs);
    }
    if (hs->arr && hs->count + 1 < hs->cap) {
        uint32_t mask = hs->cap - 1;
        uint32_t h0 = holdSlot(m, mask);
        for (uint32_t i = 0; i < hs->cap; ++i) {
            uint32_t s = (h0 + i) & mask;
            uintptr_t cur = hs->arr[s].ptr;
            if (cur == p) {
                *oldSize = hs->arr[s].size;
                *oldIdx = hs->arr[s].idx;
                hs->arr[s].size = size;
                hs->arr[s].idx = idx;
                ok = true;
                break;
            }
            if (cur == 0) {
                hs->arr[s].ptr = p;
                hs->arr[s].size = size;
                hs->arr[s].idx = idx;
                hs->count++;
                __atomic_fetch_add(&g_pageCnt[pageIdx(p)], 1u, __ATOMIC_RELEASE);
                ok = true;
                break;
            }
        }
    }
    HOLD_UNLOCK(sh);
    return ok;
}

static uint32_t holdFind(uintptr_t p, uint32_t* size) {
    if (p <= 1) return REC_NONE;
    uint32_t pg = pageIdx(p);
    if (__atomic_load_n(&g_pageCnt[pg], __ATOMIC_ACQUIRE) == 0) return REC_NONE;
    uint64_t m = holdMix(p);
    uint32_t sh = holdShard(m);
    HoldShard* hs = &g_hold[sh];
    uint32_t foundIdx = REC_NONE;
    HOLD_LOCK(sh);
    if (hs->arr && hs->count > 0) {
        uint32_t mask = hs->cap - 1;
        uint32_t h0 = holdSlot(m, mask);
        for (uint32_t i = 0; i < hs->cap; ++i) {
            uint32_t s = (h0 + i) & mask;
            uintptr_t curp = hs->arr[s].ptr;
            if (curp == 0) break;
            if (curp == p) {
                *size = hs->arr[s].size;
                foundIdx = hs->arr[s].idx;
                // 后移删除（backward-shift deletion）：零墓碑
                uint32_t hole = s;
                for (uint32_t j = 1; j < hs->cap; ++j) {
                    uint32_t cur = (s + j) & mask;
                    uintptr_t cp = hs->arr[cur].ptr;
                    if (cp == 0) break;
                    uint32_t chome = holdSlot(holdMix(cp), mask);
                    uint32_t dCur  = (cur  - chome) & mask;
                    uint32_t dHole = (hole - chome) & mask;
                    if (dHole < dCur) {
                        hs->arr[hole] = hs->arr[cur];
                        hole = cur;
                    }
                }
                hs->arr[hole].ptr = 0;
                hs->count--;
                __atomic_fetch_sub(&g_pageCnt[pg], 1u, __ATOMIC_RELEASE);
                break;
            }
        }
    }
    HOLD_UNLOCK(sh);
    return foundIdx;
}

// ---------------- 无锁 .eh_frame_hdr + Seqlock CFI 规则缓存回溯器 ----------------
#define MAX_EH_MODS 256
#define MAX_EH_SEGS 8
struct EhSeg { uintptr_t lo, hi; };
struct EhMod {
    uintptr_t base;
    uintptr_t ehHdr;
    uintptr_t ehHdrEnd;
    uintptr_t textBase;
    uintptr_t minLo, maxHi;
    int       nSeg;
    EhSeg     segs[MAX_EH_SEGS];
    uint8_t   hdrVer;
    uint8_t   ehFramePtrEnc;
    uint8_t   fdeCountEnc;
    uint8_t   tableEnc;
    uintptr_t ehFramePtr;
    uint32_t  fdeCount;
    uintptr_t tableAddr;
    uint32_t  entrySize;
};
static EhMod g_ehMods[MAX_EH_MODS];
static int   g_ehModN = 0;
static int   g_ehUnityIdx = -1;
static int   g_ehIl2cppIdx = -1;
static volatile int g_ehRegLock = 0;

static uintptr_t g_selfLo = 0, g_selfHi = 0;

static inline bool ehModContains(const EhMod* m, uintptr_t pc) {
    if (pc < m->minLo || pc >= m->maxHi) return false;
    for (int i = 0; i < m->nSeg; ++i)
        if (pc >= m->segs[i].lo && pc < m->segs[i].hi) return true;
    return false;
}

static const EhMod* findEhMod(uintptr_t pc) {
    int n = __atomic_load_n(&g_ehModN, __ATOMIC_ACQUIRE);
    int ui = __atomic_load_n(&g_ehUnityIdx, __ATOMIC_ACQUIRE);
    if (ui >= 0 && ui < n && ehModContains(&g_ehMods[ui], pc)) return &g_ehMods[ui];
    int ci = __atomic_load_n(&g_ehIl2cppIdx, __ATOMIC_ACQUIRE);
    if (ci >= 0 && ci < n && ehModContains(&g_ehMods[ci], pc)) return &g_ehMods[ci];
    for (int i = 0; i < n; ++i) {
        if (i == ui || i == ci) continue;
        if (ehModContains(&g_ehMods[i], pc)) return &g_ehMods[i];
    }
    return 0;
}

static bool isCode(uintptr_t a) {
    return findEhMod(a) != 0;
}

#if defined(__aarch64__)

extern "C" void ach_capture_ctx(uintptr_t* regs3);

#define CFI_CACHE_SETS 65536
#define CFI_CACHE_WAYS 2
struct CfiCacheEnt {
    volatile uint64_t seq;
    uint64_t pc;
    uint64_t rule;
};
static CfiCacheEnt g_cfiCache[CFI_CACHE_SETS][CFI_CACHE_WAYS];

struct CfiRule {
    int32_t cfaOff;
    int32_t raOff;
    int32_t fpOff;
    uint8_t valid;
    uint8_t endOfStack;
    uint8_t useFp;
    uint8_t raInReg;
    uint8_t hasFpOff;
    uint8_t hasRaOff;
    uint8_t isSignal;
};

static inline bool packCfiRule(const CfiRule& r, uint64_t* out) {
    if (r.cfaOff < -(1 << 20) || r.cfaOff >= (1 << 20)) return false;
    if (r.raOff  < -(1 << 17) || r.raOff  >= (1 << 17)) return false;
    if (r.fpOff  < -(1 << 17) || r.fpOff  >= (1 << 17)) return false;
    uint64_t v = 0;
    v |= (uint64_t)(r.valid      & 1u) << 0;
    v |= (uint64_t)(r.endOfStack & 1u) << 1;
    v |= (uint64_t)(r.useFp      & 1u) << 2;
    v |= (uint64_t)(r.raInReg    & 1u) << 3;
    v |= (uint64_t)(r.hasFpOff   & 1u) << 4;
    v |= (uint64_t)(r.hasRaOff   & 1u) << 5;
    v |= (uint64_t)(r.isSignal   & 1u) << 6;
    v |= ((uint64_t)(uint32_t)r.cfaOff & 0x1FFFFFull) << 7;
    v |= ((uint64_t)(uint32_t)r.raOff  & 0x3FFFFull)  << 28;
    v |= ((uint64_t)(uint32_t)r.fpOff  & 0x3FFFFull)  << 46;
    *out = v;
    return true;
}

static inline CfiRule unpackCfiRule(uint64_t v) {
    CfiRule r;
    r.valid      = (uint8_t)((v >> 0) & 1u);
    r.endOfStack = (uint8_t)((v >> 1) & 1u);
    r.useFp      = (uint8_t)((v >> 2) & 1u);
    r.raInReg    = (uint8_t)((v >> 3) & 1u);
    r.hasFpOff   = (uint8_t)((v >> 4) & 1u);
    r.hasRaOff   = (uint8_t)((v >> 5) & 1u);
    r.isSignal   = (uint8_t)((v >> 6) & 1u);
    int32_t cfa  = (int32_t)((v >> 7)  & 0x1FFFFFull); if (cfa & (1 << 20)) cfa -= (1 << 21);
    int32_t ra   = (int32_t)((v >> 28) & 0x3FFFFull);  if (ra  & (1 << 17)) ra  -= (1 << 18);
    int32_t fp   = (int32_t)((v >> 46) & 0x3FFFFull);  if (fp  & (1 << 17)) fp  -= (1 << 18);
    r.cfaOff = cfa;
    r.raOff  = ra;
    r.fpOff  = fp;
    return r;
}

static inline uint32_t cfiHash(uint64_t pc) {
    uint64_t x = (pc >> 2) * 11400714819323198485ULL;
    return (uint32_t)((x ^ (x >> 32)) & (CFI_CACHE_SETS - 1));
}

static inline uint16_t ehReadU16(const uint8_t* p) { uint16_t v; memcpy(&v, p, 2); return v; }
static inline int16_t  ehReadI16(const uint8_t* p) { int16_t v;  memcpy(&v, p, 2); return v; }
static inline uint32_t ehReadU32(const uint8_t* p) { uint32_t v; memcpy(&v, p, 4); return v; }
static inline int32_t  ehReadI32(const uint8_t* p) { int32_t v;  memcpy(&v, p, 4); return v; }
static inline uint64_t ehReadU64(const uint8_t* p) { uint64_t v; memcpy(&v, p, 8); return v; }
static inline int64_t  ehReadI64(const uint8_t* p) { int64_t v;  memcpy(&v, p, 8); return v; }

static inline uint64_t ehReadUleb(const uint8_t** pp, const uint8_t* end) {
    const uint8_t* p = *pp;
    uint64_t res = 0;
    int shift = 0;
    while (p < end) {
        uint8_t b = *p++;
        res |= (uint64_t)(b & 0x7Fu) << shift;
        if ((b & 0x80u) == 0) break;
        shift += 7;
        if (shift >= 64) break;
    }
    *pp = p;
    return res;
}

static inline int64_t ehReadSleb(const uint8_t** pp, const uint8_t* end) {
    const uint8_t* p = *pp;
    uint64_t res = 0;
    int shift = 0;
    uint8_t b = 0;
    while (p < end) {
        b = *p++;
        res |= (uint64_t)(b & 0x7Fu) << shift;
        shift += 7;
        if ((b & 0x80u) == 0) break;
        if (shift >= 64) break;
    }
    if (shift < 64 && (b & 0x40u)) res |= (~0ULL << shift);
    *pp = p;
    return (int64_t)res;
}

static uintptr_t ehReadEncodedVal(const uint8_t** pp, const uint8_t* end, uint8_t enc,
                                  uintptr_t ehHdr, uintptr_t textBase, uintptr_t funcStart,
                                  bool applyBase, bool derefIndirect, bool* ok) {
    if (enc == 0xFFu || (enc & 0x0Fu) == 0x0Fu) { *ok = false; return 0; }
    uint8_t app = enc & 0x70u;
    uint8_t fmt = enc & 0x0Fu;
    const uint8_t* p = *pp;
    if (app == 0x50u) {
        uintptr_t a = ((uintptr_t)p + sizeof(uintptr_t) - 1) & ~(uintptr_t)(sizeof(uintptr_t) - 1);
        p = (const uint8_t*)a;
    }
    uintptr_t fieldAddr = (uintptr_t)p;
    uint64_t raw = 0;
    switch (fmt) {
        case 0x00: if (p + 8 > end) { *ok = false; return 0; } raw = ehReadU64(p); p += 8; break;
        case 0x01: raw = ehReadUleb(&p, end); break;
        case 0x02: if (p + 2 > end) { *ok = false; return 0; } raw = ehReadU16(p); p += 2; break;
        case 0x03: if (p + 4 > end) { *ok = false; return 0; } raw = ehReadU32(p); p += 4; break;
        case 0x04: if (p + 8 > end) { *ok = false; return 0; } raw = ehReadU64(p); p += 8; break;
        case 0x09: raw = (uint64_t)ehReadSleb(&p, end); break;
        case 0x0A: if (p + 2 > end) { *ok = false; return 0; } raw = (uint64_t)(int64_t)ehReadI16(p); p += 2; break;
        case 0x0B: if (p + 4 > end) { *ok = false; return 0; } raw = (uint64_t)(int64_t)ehReadI32(p); p += 4; break;
        case 0x0C: if (p + 8 > end) { *ok = false; return 0; } raw = (uint64_t)ehReadI64(p); p += 8; break;
        default: *ok = false; return 0;
    }
    *pp = p;
    if (!applyBase) return (uintptr_t)raw;

    uintptr_t val = (uintptr_t)raw;
    switch (app) {
        case 0x00: break;
        case 0x10: val = fieldAddr + (uintptr_t)raw; break;
        case 0x20: val = textBase  + (uintptr_t)raw; break;
        case 0x30: val = ehHdr     + (uintptr_t)raw; break;
        case 0x40: val = funcStart + (uintptr_t)raw; break;
        case 0x50: break;
        default: *ok = false; return 0;
    }
    if ((enc & 0x80u) && derefIndirect && val != 0)
        val = *(const uintptr_t*)val;
    return val;
}

enum { CREG_SAME = 0, CREG_OFFSET = 1, CREG_UNDEF = 2, CREG_INREG30 = 3, CREG_UNSUP = 4 };
struct CfiExecState {
    uint8_t cfaReg;
    int64_t cfaOff;
    uint8_t fpRule;
    int64_t fpOff;
    uint8_t raRule;
    int64_t raOff;
};

static bool runCfaProgram(const uint8_t* p, const uint8_t* end,
                          uintptr_t startPc, uintptr_t targetPc,
                          uint64_t codeAlign, int64_t dataAlign, uint64_t raCol,
                          uint8_t fdeEnc, const EhMod* mod,
                          const CfiExecState* cieInit, CfiExecState* st) {
    CfiExecState stack[8];
    int spIdx = 0;
    uintptr_t curPc = startPc;
    bool checkPc = (targetPc != ~(uintptr_t)0);

    while (p < end) {
        if (checkPc && curPc > targetPc) break;
        uint8_t op = *p++;
        uint8_t hi = op & 0xC0u;
        if (hi == 0x40u) {
            curPc += (uintptr_t)((op & 0x3Fu) * codeAlign);
        } else if (hi == 0x80u) {
            uint64_t reg = op & 0x3Fu;
            int64_t off = (int64_t)ehReadUleb(&p, end) * dataAlign;
            if (reg == 29) { st->fpRule = CREG_OFFSET; st->fpOff = off; }
            else if (reg == raCol) { st->raRule = CREG_OFFSET; st->raOff = off; }
        } else if (hi == 0xC0u) {
            uint64_t reg = op & 0x3Fu;
            if (cieInit) {
                if (reg == 29) { st->fpRule = cieInit->fpRule; st->fpOff = cieInit->fpOff; }
                else if (reg == raCol) { st->raRule = cieInit->raRule; st->raOff = cieInit->raOff; }
            }
        } else {
            switch (op) {
                case 0x00: break; // DW_CFA_nop
                case 0x01: {      // DW_CFA_set_loc
                    bool ok = true;
                    curPc = ehReadEncodedVal(&p, end, fdeEnc, mod->ehHdr, mod->textBase, startPc, true, true, &ok);
                    if (!ok) return false;
                    break;
                }
                case 0x02: if (p + 1 > end) return false; curPc += (uintptr_t)((*p++) * codeAlign); break;
                case 0x03: if (p + 2 > end) return false; curPc += (uintptr_t)(ehReadU16(p) * codeAlign); p += 2; break;
                case 0x04: if (p + 4 > end) return false; curPc += (uintptr_t)(ehReadU32(p) * codeAlign); p += 4; break;
                case 0x05: {      // DW_CFA_offset_extended
                    uint64_t reg = ehReadUleb(&p, end);
                    int64_t off = (int64_t)ehReadUleb(&p, end) * dataAlign;
                    if (reg == 29) { st->fpRule = CREG_OFFSET; st->fpOff = off; }
                    else if (reg == raCol) { st->raRule = CREG_OFFSET; st->raOff = off; }
                    break;
                }
                case 0x06: {      // DW_CFA_restore_extended
                    uint64_t reg = ehReadUleb(&p, end);
                    if (cieInit) {
                        if (reg == 29) { st->fpRule = cieInit->fpRule; st->fpOff = cieInit->fpOff; }
                        else if (reg == raCol) { st->raRule = cieInit->raRule; st->raOff = cieInit->raOff; }
                    }
                    break;
                }
                case 0x07: {      // DW_CFA_undefined
                    uint64_t reg = ehReadUleb(&p, end);
                    if (reg == 29) st->fpRule = CREG_UNDEF;
                    else if (reg == raCol) st->raRule = CREG_UNDEF;
                    break;
                }
                case 0x08: {      // DW_CFA_same_value
                    uint64_t reg = ehReadUleb(&p, end);
                    if (reg == 29) st->fpRule = CREG_SAME;
                    else if (reg == raCol) st->raRule = CREG_INREG30;
                    break;
                }
                case 0x09: {      // DW_CFA_register
                    uint64_t r1 = ehReadUleb(&p, end);
                    uint64_t r2 = ehReadUleb(&p, end);
                    if (r1 == raCol) st->raRule = (r2 == 30) ? CREG_INREG30 : CREG_UNSUP;
                    else if (r1 == 29) st->fpRule = (r2 == 29) ? CREG_SAME : CREG_UNSUP;
                    break;
                }
                case 0x0A:        // DW_CFA_remember_state
                    if (spIdx < 8) stack[spIdx++] = *st;
                    else return false;
                    break;
                case 0x0B:        // DW_CFA_restore_state
                    if (spIdx > 0) *st = stack[--spIdx];
                    else return false;
                    break;
                case 0x0C: {      // DW_CFA_def_cfa
                    uint64_t reg = ehReadUleb(&p, end);
                    uint64_t off = ehReadUleb(&p, end);
                    st->cfaReg = (reg == 29 || reg == 31) ? (uint8_t)reg : 0xFFu;
                    st->cfaOff = (int64_t)off;
                    break;
                }
                case 0x0D: {      // DW_CFA_def_cfa_register
                    uint64_t reg = ehReadUleb(&p, end);
                    st->cfaReg = (reg == 29 || reg == 31) ? (uint8_t)reg : 0xFFu;
                    break;
                }
                case 0x0E: {      // DW_CFA_def_cfa_offset
                    st->cfaOff = (int64_t)ehReadUleb(&p, end);
                    break;
                }
                case 0x0F: {      // DW_CFA_def_cfa_expression
                    uint64_t len = ehReadUleb(&p, end);
                    if (p + len > end) return false;
                    p += len;
                    st->cfaReg = 0xFFu;
                    break;
                }
                case 0x10:        // DW_CFA_expression
                case 0x16: {      // DW_CFA_val_expression
                    uint64_t reg = ehReadUleb(&p, end);
                    uint64_t len = ehReadUleb(&p, end);
                    if (p + len > end) return false;
                    p += len;
                    if (reg == 29) st->fpRule = CREG_UNSUP;
                    else if (reg == raCol) st->raRule = CREG_UNSUP;
                    break;
                }
                case 0x11: {      // DW_CFA_offset_extended_sf
                    uint64_t reg = ehReadUleb(&p, end);
                    int64_t off = ehReadSleb(&p, end) * dataAlign;
                    if (reg == 29) { st->fpRule = CREG_OFFSET; st->fpOff = off; }
                    else if (reg == raCol) { st->raRule = CREG_OFFSET; st->raOff = off; }
                    break;
                }
                case 0x12: {      // DW_CFA_def_cfa_sf
                    uint64_t reg = ehReadUleb(&p, end);
                    int64_t off = ehReadSleb(&p, end) * dataAlign;
                    st->cfaReg = (reg == 29 || reg == 31) ? (uint8_t)reg : 0xFFu;
                    st->cfaOff = off;
                    break;
                }
                case 0x13: {      // DW_CFA_def_cfa_offset_sf
                    st->cfaOff = ehReadSleb(&p, end) * dataAlign;
                    break;
                }
                case 0x14: {      // DW_CFA_val_offset
                    uint64_t reg = ehReadUleb(&p, end);
                    (void)ehReadUleb(&p, end);
                    if (reg == 29) st->fpRule = CREG_UNSUP;
                    else if (reg == raCol) st->raRule = CREG_UNSUP;
                    break;
                }
                case 0x15: {      // DW_CFA_val_offset_sf
                    uint64_t reg = ehReadUleb(&p, end);
                    (void)ehReadSleb(&p, end);
                    if (reg == 29) st->fpRule = CREG_UNSUP;
                    else if (reg == raCol) st->raRule = CREG_UNSUP;
                    break;
                }
                case 0x2D: break; // DW_CFA_AARCH64_negate_ra_state
                case 0x2E: (void)ehReadUleb(&p, end); break; // DW_CFA_GNU_args_size
                case 0x2F: {      // DW_CFA_GNU_negative_offset_extended
                    uint64_t reg = ehReadUleb(&p, end);
                    int64_t off = -(int64_t)ehReadUleb(&p, end) * dataAlign;
                    if (reg == 29) { st->fpRule = CREG_OFFSET; st->fpOff = off; }
                    else if (reg == raCol) { st->raRule = CREG_OFFSET; st->raOff = off; }
                    break;
                }
                default: return false;
            }
        }
    }
    return true;
}

static bool decodeCfiFromModule(const EhMod* mod, uintptr_t lookupPc, CfiRule* outRule) {
    if (!mod || !mod->ehHdr || mod->hdrVer != 1 || mod->fdeCount == 0 || mod->entrySize == 0)
        return false;

    uint32_t elemSz = mod->entrySize >> 1;
    uint32_t lo = 0, hi = mod->fdeCount;
    while (lo < hi) {
        uint32_t mid = lo + ((hi - lo) >> 1);
        const uint8_t* ep = (const uint8_t*)(mod->tableAddr + (uintptr_t)mid * mod->entrySize);
        bool ok = true;
        uintptr_t loc = ehReadEncodedVal(&ep, ep + elemSz, mod->tableEnc, mod->ehHdr, mod->textBase, 0, true, false, &ok);
        if (!ok) return false;
        if (loc <= lookupPc) lo = mid + 1;
        else hi = mid;
    }
    if (lo == 0) return false;

    const uint8_t* bestFdeField = (const uint8_t*)(mod->tableAddr + (uintptr_t)(lo - 1) * mod->entrySize + elemSz);
    bool ok = true;
    uintptr_t fdeAddr = ehReadEncodedVal(&bestFdeField, bestFdeField + elemSz, mod->tableEnc, mod->ehHdr, mod->textBase, 0, true, true, &ok);
    if (!ok || !fdeAddr) return false;

    const uint8_t* fdeHdr = (const uint8_t*)fdeAddr;
    uint32_t fdeLen = ehReadU32(fdeHdr);
    if (fdeLen == 0 || fdeLen == 0xFFFFFFFFu) return false;
    const uint8_t* fdeBody = fdeHdr + 4;
    const uint8_t* fdeEnd  = fdeBody + fdeLen;
    if (fdeBody + 4 > fdeEnd) return false;

    uint32_t cieBack = ehReadU32(fdeBody);
    if (cieBack == 0) return false;
    const uint8_t* cieHdr = fdeBody - cieBack;
    uint32_t cieLen = ehReadU32(cieHdr);
    if (cieLen == 0 || cieLen == 0xFFFFFFFFu) return false;
    const uint8_t* cieBody = cieHdr + 4;
    const uint8_t* cieEnd  = cieBody + cieLen;
    if (cieBody + 8 > cieEnd || ehReadU32(cieBody) != 0) return false;

    const uint8_t* cp = cieBody + 4;
    uint8_t ver = *cp++;
    const char* aug = (const char*)cp;
    while (cp < cieEnd && *cp) cp++;
    if (cp >= cieEnd) return false;
    cp++;
    if (ver >= 4) {
        if (cp + 2 > cieEnd) return false;
        cp += 2; // address_size, segment_size
    }
    uint64_t codeAlign = ehReadUleb(&cp, cieEnd);
    int64_t  dataAlign = ehReadSleb(&cp, cieEnd);
    uint64_t raCol     = (ver == 1) ? (cp < cieEnd ? *cp++ : 255) : ehReadUleb(&cp, cieEnd);
    if (raCol != 30) return false;

    uint8_t fdeEnc = 0x00;
    uint8_t isSignal = 0;
    bool hasZ = (aug[0] == 'z');
    if (hasZ) {
        uint64_t augLen = ehReadUleb(&cp, cieEnd);
        const uint8_t* augEnd = cp + augLen;
        if (augEnd > cieEnd) return false;
        for (const char* a = aug + 1; *a && cp < augEnd; ++a) {
            if (*a == 'L') {
                cp++;
            } else if (*a == 'P') {
                uint8_t pEnc = *cp++;
                bool pok = true;
                (void)ehReadEncodedVal(&cp, augEnd, pEnc, mod->ehHdr, mod->textBase, 0, true, false, &pok);
                if (!pok) return false;
            } else if (*a == 'R') {
                fdeEnc = *cp++;
            } else if (*a == 'S') {
                isSignal = 1;
            } else {
                break;
            }
        }
        cp = augEnd;
    }

    const uint8_t* fp = fdeBody + 4;
    uintptr_t fdePcStart = ehReadEncodedVal(&fp, fdeEnd, fdeEnc, mod->ehHdr, mod->textBase, 0, true, true, &ok);
    if (!ok) return false;
    uintptr_t fdePcRange = ehReadEncodedVal(&fp, fdeEnd, (uint8_t)(fdeEnc & 0x0Fu), mod->ehHdr, mod->textBase, 0, false, false, &ok);
    if (!ok) return false;
    if (lookupPc < fdePcStart || lookupPc >= fdePcStart + fdePcRange) return false;
    if (hasZ) {
        uint64_t fdeAugLen = ehReadUleb(&fp, fdeEnd);
        if (fp + fdeAugLen > fdeEnd) return false;
        fp += fdeAugLen;
    }

    CfiExecState cieState = { 31, 0, CREG_SAME, 0, CREG_INREG30, 0 };
    if (!runCfaProgram(cp, cieEnd, fdePcStart, ~(uintptr_t)0, codeAlign, dataAlign, raCol, fdeEnc, mod, 0, &cieState))
        return false;

    CfiExecState fdeState = cieState;
    if (!runCfaProgram(fp, fdeEnd, fdePcStart, lookupPc, codeAlign, dataAlign, raCol, fdeEnc, mod, &cieState, &fdeState))
        return false;

    if (fdeState.cfaReg != 31 && fdeState.cfaReg != 29) return false;
    if (fdeState.raRule == CREG_UNSUP || fdeState.fpRule == CREG_UNSUP) return false;

    outRule->valid      = 1;
    outRule->endOfStack = (fdeState.raRule == CREG_UNDEF) ? 1 : 0;
    outRule->useFp      = (fdeState.cfaReg == 29) ? 1 : 0;
    outRule->raInReg    = (fdeState.raRule == CREG_INREG30) ? 1 : 0;
    outRule->hasFpOff   = (fdeState.fpRule == CREG_OFFSET) ? 1 : 0;
    outRule->hasRaOff   = (fdeState.raRule == CREG_OFFSET) ? 1 : 0;
    outRule->isSignal   = isSignal;
    outRule->cfaOff     = (int32_t)fdeState.cfaOff;
    outRule->raOff      = (int32_t)fdeState.raOff;
    outRule->fpOff      = (int32_t)fdeState.fpOff;
    return true;
}

static bool lookupCfiRule(const EhMod* mod, uintptr_t lookupPc, CfiRule* outRule) {
    uint32_t setIdx = cfiHash((uint64_t)lookupPc);
    CfiCacheEnt* ways = g_cfiCache[setIdx];

    uint64_t waySeq[CFI_CACHE_WAYS] = { 0, 0 };
    for (int w = 0; w < CFI_CACHE_WAYS; ++w) {
        CfiCacheEnt* ent = &ways[w];
        uint64_t s1 = __atomic_load_n(&ent->seq, __ATOMIC_ACQUIRE);
        waySeq[w] = s1;
        if ((s1 & 1u) == 0 && s1 != 0) {
            uint64_t cpc = __atomic_load_n(&ent->pc, __ATOMIC_RELAXED);
            uint64_t crl = __atomic_load_n(&ent->rule, __ATOMIC_RELAXED);
            __atomic_thread_fence(__ATOMIC_ACQUIRE);
            uint64_t s2 = __atomic_load_n(&ent->seq, __ATOMIC_RELAXED);
            if (s1 == s2 && cpc == (uint64_t)lookupPc) {
                *outRule = unpackCfiRule(crl);
                return outRule->valid != 0;
            }
        }
    }

    bool ok = decodeCfiFromModule(mod, lookupPc, outRule);
    if (!ok) {
        memset(outRule, 0, sizeof(*outRule));
        outRule->valid = 0;
    }

    uint64_t packed = 0;
    if (!packCfiRule(*outRule, &packed)) {
        CfiRule neg;
        memset(&neg, 0, sizeof(neg));
        neg.valid = 0;
        packCfiRule(neg, &packed);
        ok = false;
    }

    int pick = 0;
    if (waySeq[0] == 0) pick = 0;
    else if (waySeq[1] == 0) pick = 1;
    else if ((waySeq[0] & 1u) == 0 && ((waySeq[1] & 1u) != 0 || waySeq[0] <= waySeq[1])) pick = 0;
    else pick = 1;

    CfiCacheEnt* target = &ways[pick];
    uint64_t curSeq = __atomic_load_n(&target->seq, __ATOMIC_RELAXED);
    if ((curSeq & 1u) == 0 &&
        __atomic_compare_exchange_n(&target->seq, &curSeq, curSeq + 1, false, __ATOMIC_ACQ_REL, __ATOMIC_RELAXED)) {
        __atomic_store_n(&target->pc,   (uint64_t)lookupPc, __ATOMIC_RELAXED);
        __atomic_store_n(&target->rule, packed,             __ATOMIC_RELAXED);
        __atomic_store_n(&target->seq,  curSeq + 2,         __ATOMIC_RELEASE);
    }
    return ok;
}

// 返回 >=0 为帧数；返回 -1 表示首个外部帧未找到/未知模块，调用方应回退到 _Unwind_Backtrace
static int fastUnwindCore(uintptr_t pc, uintptr_t sp, uintptr_t fp, uintptr_t lr,
                          uintptr_t* out, int max, bool skipSelf) {
    int nf = 0;
    uint8_t prevWasSignal = 0;
    uintptr_t selfLo = g_selfLo, selfHi = g_selfHi;

    for (int step = 0; step < 64 && nf < max; ++step) {
        if (pc == 0) break;
        uintptr_t lookupPc = (step == 0 || prevWasSignal) ? pc : (pc - 1);
        const EhMod* mod = findEhMod(lookupPc);
        bool inSelf = skipSelf && selfHi > selfLo && (pc >= selfLo && pc < selfHi);

        if (!inSelf) {
            if (!mod) return (nf == 0) ? -1 : nf;
            out[nf++] = pc;
            if (nf >= max) break;
        } else if (!mod) {
            return (nf == 0) ? -1 : nf;
        }

        CfiRule rule;
        if (!lookupCfiRule(mod, lookupPc, &rule))
            return (nf == 0) ? -1 : nf;
        if (rule.endOfStack) break;

        uintptr_t baseReg = rule.useFp ? fp : sp;
        uintptr_t cfa = (uintptr_t)((int64_t)baseReg + (int64_t)rule.cfaOff);
        if (cfa < sp || (cfa - sp) >= 0x100000ULL || (cfa & 7u) != 0)
            return (nf == 0) ? -1 : nf;

        uintptr_t nextPc = 0;
        if (rule.raInReg) {
            if (step > 0) return (nf == 0) ? -1 : nf;
            nextPc = lr;
        } else if (rule.hasRaOff) {
            uintptr_t raAddr = (uintptr_t)((int64_t)cfa + (int64_t)rule.raOff);
            if (raAddr < sp || raAddr >= sp + 0x100000ULL || (raAddr & 7u) != 0)
                return (nf == 0) ? -1 : nf;
            nextPc = *(const uintptr_t*)raAddr;
        } else {
            return (nf == 0) ? -1 : nf;
        }

        if (nextPc == 0) break;
        if (!findEhMod(rule.isSignal ? nextPc : (nextPc - 1)))
            return (nf == 0) ? -1 : nf;

        uintptr_t nextFp = fp;
        if (rule.hasFpOff) {
            uintptr_t fpAddr = (uintptr_t)((int64_t)cfa + (int64_t)rule.fpOff);
            if (fpAddr >= sp && fpAddr < sp + 0x100000ULL && (fpAddr & 7u) == 0)
                nextFp = *(const uintptr_t*)fpAddr;
        }

        pc = nextPc;
        sp = cfa;
        fp = nextFp;
        lr = 0;
        prevWasSignal = rule.isSignal;
    }
    return nf;
}

#endif // __aarch64__

// 返回地址校验：PC 的前一条指令必须是 bl / blr。
// 这比 CFI 是否存在可靠得多——libunity 里有些 FDE 覆盖多个函数，
// 但只写了一套 CFA，有 FDE 也会走错。
static bool isCallReturn(uintptr_t pc) {
    if (pc < 4 || !isCode(pc)) return false;
    uint32_t ins = *(const volatile uint32_t*)(pc - 4);
    bool bl  = (ins & 0xFC000000u) == 0x94000000u;   // BL  <imm26>
    bool blr = (ins & 0xFFFFFC1Fu) == 0xD63F0000u;   // BLR <Xn>
    return bl || blr;
}

// 设备端栈扫描：从当前 SP 往上扫，收集"前一条是 bl/blr、且落在可执行段"的地址。
// 这正是 Android libunwindstack 在 CFI 缺失时的兜底思路；这里先做诊断版本。
static int scanStackForCalls(uintptr_t* out, int max,
                             uintptr_t skipLo, uintptr_t skipHi,
                             uintptr_t mem1Lo, uintptr_t mem1Hi,
                             uintptr_t mem2Lo, uintptr_t mem2Hi,
                             uintptr_t mem3Lo, uintptr_t mem3Hi) {
    int n = 0;
    pthread_attr_t attr;
    void* stackBase = 0;
    size_t stackSize = 0;
    if (pthread_getattr_np(pthread_self(), &attr) != 0) return 0;
    pthread_attr_getstack(&attr, &stackBase, &stackSize);
    pthread_attr_destroy(&attr);

    uintptr_t lo = (uintptr_t)stackBase;
    uintptr_t hi = lo + stackSize;
    uintptr_t sp = (uintptr_t)__builtin_frame_address(0);
    uintptr_t end = (sp + 0x40000 < hi) ? (sp + 0x40000) : hi;  // 只看当前帧往上 256KB

    for (uintptr_t a = (sp + 7u) & ~(uintptr_t)7u; a + sizeof(uintptr_t) <= end; a += sizeof(uintptr_t)) {
        // 关键：不要扫到我们自己放在栈上的 PC 数组，否则会把 A 的结果当成"栈上的返回地址"
        if ((a >= mem1Lo && a < mem1Hi) || (a >= mem2Lo && a < mem2Hi) ||
            (a >= mem3Lo && a < mem3Hi)) continue;
        uintptr_t w = *(const volatile uintptr_t*)a;
        if (w >= skipLo && w < skipHi) continue;
        if (!isCallReturn(w)) continue;
        bool dup = false;
        for (int i = 0; i < n; ++i) if (out[i] == w) { dup = true; break; }
        if (!dup) out[n++] = w;
        if (n >= max) break;
    }
    return n;
}

static int fpWalk(const uint64_t* f, uintptr_t* out, int max) {
    uintptr_t base = (uintptr_t)f;
    uintptr_t fp = (uintptr_t)f[0];
    uintptr_t hi = base + 8u * 1024 * 1024;
    int n = 0;
    while (fp > base && fp < hi && n < max) {
        uintptr_t lr  = *(const uintptr_t*)(fp + 8);
        uintptr_t prv = *(const uintptr_t*)fp;
        if (lr > 0x1000 && isCode(lr)) out[n++] = lr;
        if (prv <= fp) break;
        fp = prv;
    }
    return n;
}

static volatile int g_holdUnwind = 1;   // 1 = 用 unwinder 展开；0 = 只记调用者（安全模式）

// —— Android 系统 libunwindstack 实验（LocalUnwinder）——
// 通过 dlopen/dlsym 调 C++ mangled 符号，避免把系统 C++ ABI 引入本 .so。
// LocalUnwinder() = default、std::vector 默认构造都是零初始化，这里用够大的对齐 buffer。
typedef bool (*LUInitFn)(void*);
typedef bool (*LUUnwindFn)(void*, void*, size_t);
static void*  g_luHandle = 0;
static LUInitFn  g_luInit = 0;
static LUUnwindFn g_luUnwind = 0;
static int    g_luReady = -1;
static unsigned char g_luObj[512] __attribute__((aligned(16)));
static unsigned char g_luVec[64]  __attribute__((aligned(8)));

static int luCapture(uintptr_t* out, int max) {
    if (g_luReady == -1) {
        g_luReady = 0;
        char e1[256] = {0}, e2[256] = {0}, e3[256] = {0};
        g_luHandle = dlopen("libmystack1234.so", RTLD_NOW);
        if (!g_luHandle) { const char* p = dlerror(); snprintf(e1, sizeof(e1), "%s", p ? p : "null"); }
        if (!g_luHandle) {
            g_luHandle = dlopen("libunwindstack.so", RTLD_NOW);
            if (!g_luHandle) { const char* p = dlerror(); snprintf(e2, sizeof(e2), "%s", p ? p : "null"); }
        }
        if (!g_luHandle) {
            g_luHandle = dlopen("/system/lib64/libunwindstack.so", RTLD_NOW);
            if (!g_luHandle) { const char* p = dlerror(); snprintf(e3, sizeof(e3), "%s", p ? p : "null"); }
        }
        if (!g_luHandle) {
            char e[800];
            snprintf(e, sizeof(e), "localunwind dlopen failed: [rename]%s | [normal]%s | [abs]%s", e1, e2, e3);
            trace(e);
        }
        if (g_luHandle) {
            g_luInit = (LUInitFn)dlsym(g_luHandle, "_ZN11unwindstack13LocalUnwinder4InitEv");
            g_luUnwind = (LUUnwindFn)dlsym(g_luHandle,
                "_ZN11unwindstack13LocalUnwinder6UnwindEPNSt3__16vectorINS_14LocalFrameDataENS1_9allocatorIS3_EEEEm");
            if (g_luInit && g_luUnwind && g_luInit(g_luObj)) g_luReady = 1;
        }
        char b[160];
        snprintf(b, sizeof(b), "localunwind handle=%p init=%p unwind=%p ready=%d",
                 g_luHandle, (void*)g_luInit, (void*)g_luUnwind, g_luReady);
        trace(b);
    }
    if (!g_luReady) return 0;

    // libc++ std::vector: begin/end/cap 三个指针；每次用零初始化的空 vector。
    memset(g_luVec, 0, sizeof(g_luVec));
    if (!g_luUnwind(g_luObj, g_luVec, (size_t)max)) return 0;

    uintptr_t begin = *(uintptr_t*)(g_luVec + 0);
    uintptr_t end   = *(uintptr_t*)(g_luVec + 8);
    if (!begin || end <= begin) return 0;

    // LocalFrameData: map_info(8), pc(8), rel_pc(8), std::string(24), function_offset(8) = 56
    const size_t FRAME_SZ = 56;
    int n = (int)((end - begin) / FRAME_SZ);
    if (n > max) n = max;
    for (int i = 0; i < n; ++i)
        out[i] = *(uintptr_t*)(begin + (size_t)i * FRAME_SZ + 8);
    return n;
}

// 栈采集方式对比实验：libc backtrace()（走系统 libunwindstack） vs 我们静态链接的 libunwind
typedef int (*BacktraceFn)(void**, int);
static BacktraceFn g_backtraceFn = 0;
static volatile int g_btCmp = 0;
static volatile int g_holdSmall = 0;
static volatile int g_holdNoFrame = 0;

static void holdRecordAlloc(uintptr_t p, size_t n, const uint64_t* f, uintptr_t callerPc = 0) {
    if ((!g_holdEnabled && !g_l2Enabled) || !g_ready || g_tls.inHook) return;
    int minBytes = g_holdEnabled ? g_holdMin : 0;
    if ((int)n < minBytes) { __sync_add_and_fetch(&g_holdSmall, 1); return; }
    g_tls.inHook = 1;

    getThreadNameFast(&g_tls);
    uint16_t tid = g_tls.threadId;

    uintptr_t pcs[MAXF];
    uintptr_t raw[MAXF + 4];              // unwinder 临时数组；栈扫描必须排除，否则会自扫
    int max = (g_holdEnabled ? g_holdFrames : (g_l2Enabled ? g_l2Frames : g_holdFrames));
    if (max > MAXF) max = MAXF;

    uintptr_t selfC = (uintptr_t)(void*)&holdRecordAlloc;
    uintptr_t selfLo = (g_selfHi > g_selfLo) ? g_selfLo : (selfC - 0x100000);
    uintptr_t selfHi = (g_selfHi > g_selfLo) ? g_selfHi : (selfC + 0x100000);
    int nf = 0;

    if (g_holdUnwind) {
#if defined(__aarch64__)
        uintptr_t regs[3];
        ach_capture_ctx(regs);
        int fnf = fastUnwindCore(regs[2], regs[0], regs[1], regs[2], pcs, max, true);
        if (fnf >= 0) {
            nf = fnf;
        } else
#endif
        if (!inWarmup()) {
            Bt s = { raw, max + 4, 0, 2 };
            _Unwind_Backtrace(btcb, &s);
            for (int i = 0; i < s.n && nf < max; ++i) {
                uintptr_t pc = raw[i];
                if (pc >= selfLo && pc < selfHi) continue;
                pcs[nf++] = pc;
            }
        }
    }
    // 仅在开启 bt_diag=1 时跑栈回溯对比实验
    if (g_btDiag && g_l2Enabled && g_btCmp < 20 && nf > 0 && (nowMs() - g_t0ms) > 15000) {
        g_btCmp++;
        uintptr_t scanned[16];
        int sn = scanStackForCalls(scanned, 12, selfLo, selfHi,
                                   (uintptr_t)pcs, (uintptr_t)(pcs + MAXF),
                                   (uintptr_t)scanned, (uintptr_t)(scanned + 16),
                                   (uintptr_t)raw, (uintptr_t)(raw + MAXF + 4));
        uintptr_t lu[16];
        int un = luCapture(lu, 12);
        char b[1100];
        int m = snprintf(b, sizeof(b), "SCMP A(n=%d):", nf);
        for (int i = 0; i < nf && m < (int)sizeof(b) - 16; ++i)
            m += snprintf(b + m, sizeof(b) - m, " %llx", (unsigned long long)pcs[i]);
        m += snprintf(b + m, sizeof(b) - m, " | C(n=%d):", sn);
        for (int i = 0; i < sn && m < (int)sizeof(b) - 16; ++i)
            m += snprintf(b + m, sizeof(b) - m, " %llx", (unsigned long long)scanned[i]);
        m += snprintf(b + m, sizeof(b) - m, " | U(n=%d):", un);
        for (int i = 0; i < un && m < (int)sizeof(b) - 16; ++i)
            m += snprintf(b + m, sizeof(b) - m, " %llx", (unsigned long long)lu[i]);
        trace(b);
    }

    if (nf <= 0 && f) {
        uintptr_t lr = (uintptr_t)f[1];                 // 入口保存的 x30 = 调用者返回地址
        if (lr > 0x1000) { pcs[nf++] = lr; }
        else { nf = fpWalk(f, pcs, max); }
    }
    if (nf <= 0 && callerPc > 0x1000 && !(callerPc >= selfLo && callerPc < selfHi)) {
        pcs[nf++] = callerPc;
    }
    if (nf <= 0) __sync_add_and_fetch(&g_holdNoFrame, 1);

    if (nf > 0) {
        uint32_t insertedSlot = tblInsertOrAdd(K_HOLD, tid, pcs, nf, (uint64_t)n);
        if (insertedSlot != REC_NONE) {
            uint32_t recSize = (n > 0xFFFFFFFFULL) ? 0xFFFFFFFFu : (uint32_t)n;
            uint32_t oldSize = 0;
            uint32_t oldIdx = REC_NONE;
            holdUpsert(p, recSize, insertedSlot, &oldSize, &oldIdx);
            if (oldIdx != REC_NONE) {
                uint32_t oldSh = oldIdx >> 24;
                TBL_LOCK(oldSh);
                StackSlot* oldR = tblGetSlot(oldIdx);
                if (oldR && oldR->count) {
                    oldR->bytes = (oldR->bytes >= oldSize) ? (oldR->bytes - oldSize) : 0;
                    oldR->aux += oldSize;
                }
                TBL_UNLOCK(oldSh);
            }
        }
    }
    g_tls.inHook = 0;
}

static void holdRecordFree(uintptr_t p) {
    if ((!g_holdEnabled && !g_l2Enabled) || !g_ready || g_tls.inHook || p <= 1) return;
    uint32_t size = 0;
    uint32_t idx = holdFind(p, &size);
    if (idx != REC_NONE) {
        uint32_t sh = idx >> 24;
        TBL_LOCK(sh);
        StackSlot* r = tblGetSlot(idx);
        if (r && r->count) {
            r->bytes = (r->bytes >= size) ? (r->bytes - size) : 0;
            r->aux += size;
        }
        TBL_UNLOCK(sh);
        return;
    }

    if (g_l2Enabled) {
        size_t usz = malloc_usable_size((const void*)p);
        if (usz > 0) {
            if (g_l2Sample == 1) {
                size_t maxF = __atomic_load_n(&g_l2MaxFilteredUsz, __ATOMIC_RELAXED);
                if (maxF > 0 && usz <= maxF) {
                    tlsAddFilteredFree(&g_tls, (int64_t)usz);
                }
            } else {
                if (usz < (size_t)g_l2Min) {
                    size_t maxF = __atomic_load_n(&g_l2MaxFilteredUsz, __ATOMIC_RELAXED);
                    if (maxF > 0 && usz <= maxF) {
                        tlsAddFilteredFree(&g_tls, (int64_t)usz);
                    }
                } else {
                    size_t maxS = __atomic_load_n(&g_l2MaxSampledUsz, __ATOMIC_RELAXED);
                    if (maxS > 0 && usz <= maxS) {
                        tlsAddSampledFree(&g_tls, (int64_t)usz);
                    }
                }
            }
        }
    }
}

static void resetTable() {
    DUMP_LOCK();
    for (uint32_t sh = 0; sh < TBL_SHARDS; ++sh) {
        TBL_LOCK(sh);
        StackShard* s = &g_tblShards[sh];
        for (uint32_t i = 0; i < s->count; ++i) {
            StackSlot* r = stackSlotAt(s, i);
            if (r->count) {
                // 只清计数，保留 hash/栈帧：区间表里仍引用这些下标，存量视图还要用它们出调用栈
                r->count = 0; r->bytes = 0; r->aux = 0;
            }
        }
        TBL_UNLOCK(sh);
    }

    // L2 指针表必须一起清，否则残留的旧指针会在 free 时减到别的记录上
    for (uint32_t sh = 0; sh < HOLD_SHARDS; ++sh) {
        HOLD_LOCK(sh);
        if (g_hold[sh].arr) memset(g_hold[sh].arr, 0, (size_t)g_hold[sh].cap * sizeof(HoldEnt));
        g_hold[sh].count = 0;
        HOLD_UNLOCK(sh);
    }
    memset(g_pageCnt, 0, sizeof(g_pageCnt));

    __atomic_fetch_add(&g_statGen, 1u, __ATOMIC_RELAXED);
    memset(g_statShards, 0, sizeof(g_statShards));
    g_holdSmall = 0;
    g_holdNoFrame = 0;
    DUMP_UNLOCK();

    LOGI("reset");
}

// ---------------- 记录 ----------------
// 返回 g_tbl 中的 stackId（供区间表归属使用），失败返回 REC_NONE
static uint32_t recordEx(uint32_t kind, size_t len) {
    if (!g_ready || g_tls.inHook) return REC_NONE;
    if ((int)len < g_minBytes) return REC_NONE;
    g_tls.inHook = 1;

    uint32_t found = REC_NONE;
    getThreadNameFast(&g_tls);
    uint16_t tid = g_tls.threadId;

    uintptr_t pcs[MAXF];
    int max = g_maxFrames > MAXF ? MAXF : g_maxFrames;
    int nf = 0;
#if defined(__aarch64__)
    uintptr_t regs[3];
    ach_capture_ctx(regs);
    int fnf = fastUnwindCore(regs[2], regs[0], regs[1], regs[2], pcs, max, true);
    if (fnf >= 0) {
        nf = fnf;
    } else
#endif
    if (!inWarmup()) {
        uintptr_t raw[MAXF + 4];
        Bt s = { raw, max + 4, 0, 2 };
        _Unwind_Backtrace(btcb, &s);
        uintptr_t selfC = (uintptr_t)(void*)&recordEx;
        uintptr_t selfLo = (g_selfHi > g_selfLo) ? g_selfLo : (selfC - 0x100000);
        uintptr_t selfHi = (g_selfHi > g_selfLo) ? g_selfHi : (selfC + 0x100000);
        for (int i = 0; i < s.n && nf < max; ++i) {
            if (raw[i] >= selfLo && raw[i] < selfHi) continue;
            pcs[nf++] = raw[i];
        }
    }

    if (nf > 0)
        found = tblInsertOrAdd(kind, tid, pcs, nf, (uint64_t)len);

    g_tls.inHook = 0;
    return found;
}

static void record(uint32_t kind, size_t len) { (void)recordEx(kind, len); }

// ---------------- 钩子 ----------------
// 关键：**不能**用 libc 的同名符号（mmap/mprotect/malloc/...）。
// 只要本 DSO 里存在同名定义，DSO 自身的调用（包括编译器生成的 __emutls_get_address）
// 也会被绑定到我们的函数上 —— 于是 emutls 分配 TLS 控制块时调到我们的 malloc，
// 轻则递归、重则拿到 NULL 直接崩（实测崩溃栈：__emutls_get_address ← recordEx ← mmap）。
// 因此统一改名 hook_*，只通过 patchModule 把函数地址写进 libunity/libil2cpp 的 GOT。

// 前几次调用写面包屑，定位崩溃点（之后不再写，避免开销）
static int g_tr = 0;
static inline void T(const char* s) { if (g_tr < 8) { g_tr++; if (g_dir[0]) trace(s); } }

static void* hook_mmap(void* addr, size_t len, int prot, int flags, int fd, off_t off) {
    T("mmap enter");
    long r = syscall(MY_NR_MMAP, addr, len, prot, flags, fd, MY_MMAP_OFF(off));
    T("mmap syscall ok");
    if ((unsigned long)r < (unsigned long)-4095UL && r != 0) {
        // 过滤只读文件映射（如 unwindstack::MemoryFileAtOffset 映射 258MB .so，真实 PSS 仅 ~3.9MB）
        bool readOnlyFile = (fd >= 0) && !(flags & MAP_ANONYMOUS) && !(prot & PROT_WRITE);
        if (prot == PROT_NONE) {
            record(K_RESERVE, len);          // 预留不占物理内存，也不进区间表
        } else if (!readOnlyFile) {
            uint32_t rec = recordEx(K_COMMIT, len);
            T("mmap record ok");
            if (g_regEnabled) {
                RLOCK();
                InsertRange((uintptr_t)r, (uintptr_t)r + len, rec);
                RUNLOCK();
            }
            T("mmap insert ok");
        }
    }
    return (void*)r;
}

static int hook_mprotect(void* addr, size_t len, int prot) {
    int r = (int)syscall(__NR_mprotect, addr, len, prot);
    if (r == 0 && g_regEnabled) {
        uintptr_t a = (uintptr_t)addr, b = a + len;
        if (prot != PROT_NONE) {
            size_t already;
            RLOCK(); already = CoveredBytes(a, b); RUNLOCK();
            size_t added = len > already ? (len - already) : 0;
            if (added > 0) {
                uint32_t rec = recordEx(K_COMMIT, added);   // 只记"新提交"的那部分，避免重复 mprotect 抹掉已有归属
                RLOCK();
                InsertRange(a, b, rec);
                RUNLOCK();
            }
        } else {
            size_t removed;
            RLOCK(); removed = RemoveRange(a, b); RUNLOCK();
            if (removed) record(K_RELEASE, removed); // 改回 PROT_NONE = 归还
        }
    }
    return r;
}

static int hook_munmap(void* addr, size_t len) {
    int r = (int)syscall(__NR_munmap, addr, len);
    if (r == 0 && g_regEnabled) {
        size_t removed;
        RLOCK(); removed = RemoveRange((uintptr_t)addr, (uintptr_t)addr + len); RUNLOCK();
        if (removed) record(K_RELEASE, removed);     // 只把"已提交"的部分算归还
    }
    return r;
}

// ---------------- Unity 分配器的 inline hook（CFI 版） ----------------
// 入口打 16 字节补丁：ldr x16,#8 ; br x16 ; .quad <动态桩>
// 动态桩只放"被覆盖的 4 条指令 + 跳回 target+16"；
// 真正做记录的代码在下面这些**编译期汇编**里 —— 它们带 .cfi，
// _Unwind_Backtrace 才能穿过桩继续回溯到业务代码。
// （手写页没有 CFI，unwinder 到那一帧就停，这是 FP 链方案失败后的结论。）

static volatile int g_unityHooked = 0;
static uintptr_t    g_unityBase = 0;
static volatile int g_holdAllocHits = 0;
static volatile int g_holdFreeHits = 0;
static volatile int g_holdRecs = 0;

// 帧布局（112 字节）：
//   0     x29
//   8     x30（原始调用者的返回地址）
//   16..79  x0..x7
//   80    resume（动态桩里"4 条指令 + 跳回"的地址）
//   88    post stub 地址
//   96    返回值
//   104   参数下标（f[2+i] 里哪个是 size/ptr：静态函数 x0→2，成员函数 x1→3）
#define ACH_FRAME 112

// 桥接：f[0]=x29 f[1]=x30 f[2+i]=xi，f[13]=参数下标
extern "C" __attribute__((used, noinline)) void unity_alloc_entry(uint64_t* f) {
    int ai = (int)f[13];
    int n = __sync_add_and_fetch(&g_holdAllocHits, 1);
    if (n <= 3) tlog("hold alloc entry size=%llu align=%llu ai=%d",
                     (unsigned long long)f[ai], (unsigned long long)f[ai + 1], ai);
}

extern "C" __attribute__((used, noinline)) void unity_alloc_post(uint64_t* f, uint64_t ret) {
    static int dbg = 0;
    int ai = (int)f[13];
    if (dbg < 3) { dbg++; tlog("hold alloc post size=%llu ptr=%p ai=%d", (unsigned long long)f[ai], (void*)ret, ai); }
    if (!ret) return;
    size_t size = (size_t)f[ai];
    if (size < (size_t)g_holdMin) return;
    if (size > (size_t)512 * 1024 * 1024) return;
    holdRecordAlloc((uintptr_t)ret, size, f);
    __sync_add_and_fetch(&g_holdRecs, 1);
}

extern "C" __attribute__((used, noinline)) void unity_free_entry(uint64_t* f) {
    int n = __sync_add_and_fetch(&g_holdFreeHits, 1);
    int ai = (int)f[13];
    if (n <= 3) tlog("hold free entry ptr=%p ai=%d", (void*)f[ai], ai);
    if (f[ai]) holdRecordFree((uintptr_t)f[ai]);
}

extern "C" __attribute__((used, noinline)) void unity_free_post(uint64_t* f, uint64_t ret) {
    (void)f; (void)ret;
}

#if defined(__aarch64__)

extern "C" void ach_preamble_alloc();
extern "C" void ach_preamble_free();
extern "C" void ach_post_alloc();
extern "C" void ach_post_free();

// 桩入口由动态页进（已经 stp x29,x30,[sp,#-112]!），所以这里 CFA = sp+112。
__asm__(
    ".text\n"
    ".p2align 2\n"

    ".global ach_preamble_alloc\n"
    ".hidden ach_preamble_alloc\n"
    ".type   ach_preamble_alloc, %function\n"
    "ach_preamble_alloc:\n"
    "  .cfi_startproc\n"
    "  .cfi_def_cfa 29, 112\n"
    "  .cfi_offset 29, -112\n"
    "  .cfi_offset 30, -104\n"
    "  mov x0, sp\n"
    "  bl  unity_alloc_entry\n"
    "  ldp x0, x1, [sp, #16]\n"
    "  ldp x2, x3, [sp, #32]\n"
    "  ldp x4, x5, [sp, #48]\n"
    "  ldp x6, x7, [sp, #64]\n"
    "  ldr x30, [sp, #88]\n"           // LR = post 桩：原函数 return 时落到那里记账
    "  ldr x9,  [sp, #80]\n"
    "  br  x9\n"
    "  .cfi_endproc\n"
    ".size ach_preamble_alloc, .-ach_preamble_alloc\n"

    ".global ach_preamble_free\n"
    ".hidden ach_preamble_free\n"
    ".type   ach_preamble_free, %function\n"
    "ach_preamble_free:\n"
    "  .cfi_startproc\n"
    "  .cfi_def_cfa 29, 112\n"
    "  .cfi_offset 29, -112\n"
    "  .cfi_offset 30, -104\n"
    "  mov x0, sp\n"
    "  bl  unity_free_entry\n"
    "  ldp x0, x1, [sp, #16]\n"
    "  ldp x2, x3, [sp, #32]\n"
    "  ldp x4, x5, [sp, #48]\n"
    "  ldp x6, x7, [sp, #64]\n"
    "  ldr x30, [sp, #88]\n"
    "  ldr x9,  [sp, #80]\n"
    "  br  x9\n"
    "  .cfi_endproc\n"
    ".size ach_preamble_free, .-ach_preamble_free\n"

    // 原函数 return 落到这里：x0 = 返回值，帧还在栈上
    ".global ach_post_alloc\n"
    ".hidden ach_post_alloc\n"
    ".type   ach_post_alloc, %function\n"
    "ach_post_alloc:\n"
    "  .cfi_startproc\n"
    "  .cfi_def_cfa 29, 112\n"
    "  .cfi_offset 29, -112\n"
    "  .cfi_offset 30, -104\n"
    "  str x0, [sp, #96]\n"
    "  mov x0, sp\n"
    "  ldr x1, [sp, #96]\n"
    "  bl  unity_alloc_post\n"
    "  ldr x0, [sp, #96]\n"
    "  ldp x29, x30, [sp], #112\n"
    "  .cfi_def_cfa 31, 0\n"
    "  ret\n"
    "  .cfi_endproc\n"
    ".size ach_post_alloc, .-ach_post_alloc\n"

    ".global ach_post_free\n"
    ".hidden ach_post_free\n"
    ".type   ach_post_free, %function\n"
    "ach_post_free:\n"
    "  .cfi_startproc\n"
    "  .cfi_def_cfa 29, 112\n"
    "  .cfi_offset 29, -112\n"
    "  .cfi_offset 30, -104\n"
    "  ldp x29, x30, [sp], #112\n"
    "  .cfi_def_cfa 31, 0\n"
    "  ret\n"
    "  .cfi_endproc\n"
    ".size ach_post_free, .-ach_post_free\n"
    "\n"
    "// 无栈叶函数：采集调用者当前 sp / fp(x29) / pc(x30)，供 fastUnwindCore 起步\n"
    ".global ach_capture_ctx\n"
    ".hidden ach_capture_ctx\n"
    ".type   ach_capture_ctx, %function\n"
    "ach_capture_ctx:\n"
    "  .cfi_startproc\n"
    "  mov x9, sp\n"
    "  stp x9, x29, [x0]\n"
    "  str x30, [x0, #16]\n"
    "  ret\n"
    "  .cfi_endproc\n"
    ".size ach_capture_ctx, .-ach_capture_ctx\n"
    "\n"
    "// 桩槽位：运行时被写入\"4 条原指令 + 跳回\"和桩入口。\n"
    "// 放在 .text 里是为了让 unwinder 能查到模块（否则匿名页会让它崩）。\n"
    ".p2align 6\n"
    ".global ach_slots\n"
    ".hidden ach_slots\n"
    "ach_slots:\n"
    "  .rept 64\n"
    "  .space 128\n"
    "  .endr\n"
    ".size ach_slots, .-ach_slots\n"
);

// ---- 指令编码 ----
static inline uint32_t encStpPre(int rt, int rt2, int rn, int off) {
    return 0xA9800000u | ((uint32_t)((off / 8) & 0x7F) << 15) | ((uint32_t)rt2 << 10) | ((uint32_t)rn << 5) | (uint32_t)rt;
}
static inline uint32_t encStpOff(int rt, int rt2, int rn, int off) {
    return 0xA9000000u | ((uint32_t)((off / 8) & 0x7F) << 15) | ((uint32_t)rt2 << 10) | ((uint32_t)rn << 5) | (uint32_t)rt;
}
static inline uint32_t encStrOff(int rt, int rn, int off) {
    return 0xF9000000u | ((uint32_t)((off / 8) & 0xFFF) << 10) | ((uint32_t)rn << 5) | (uint32_t)rt;
}
static inline uint32_t encLdrLit(int rt, int words) {
    return 0x58000000u | ((uint32_t)(words & 0x7FFFF) << 5) | (uint32_t)rt;
}

// 桩槽位放在本 .so 的 .text 里（见下面 ach_slots）。
// 之前放 mmap 的匿名页，unwinder 解析到这块地址时查不到模块，
// 直接崩在 libunwind::UnwindCursor::setInfoBasedOnIPRegister。
// 放进 .text 后模块可查，最多是"没有 FDE"，unwinder 会正常收尾而不是崩。
#define ACH_SLOT_SIZE 128
#define ACH_SLOT_MAX  64

extern "C" uint8_t ach_slots[];

static uint8_t* allocStubSpace(int need) {
    if (need > ACH_SLOT_SIZE) { tlog("stub: need %d > slot", need); return 0; }
    static int used = 0;
    static int writable = 0;
    if (used >= ACH_SLOT_MAX) { tlog("stub: slots exhausted"); return 0; }

    // .text 是只读的，写代码前先把槽位区改成可写（只做一次）
    if (!writable) {
        uintptr_t ps = (uintptr_t)(g_pageSize ? g_pageSize : 4096);
        uintptr_t base = (uintptr_t)ach_slots & ~(ps - 1);
        uintptr_t endp = ((uintptr_t)ach_slots + (size_t)ACH_SLOT_MAX * ACH_SLOT_SIZE + ps - 1) & ~(ps - 1);
        size_t span = (size_t)(endp - base);
        if (syscall(__NR_mprotect, (void*)base, span, PROT_READ | PROT_WRITE | PROT_EXEC) != 0) {
            tlog("stub: mprotect text failed");
            return 0;
        }
        writable = 1;
    }

    uint8_t* r = ach_slots + (size_t)used * ACH_SLOT_SIZE;
    used++;
    return r;
}

// 入口前 4 条指令能否原样搬走（不含 PC 相对寻址）
static bool fourInsnSafe(const uint32_t* ins) {
    for (int i = 0; i < 4; ++i) {
        uint32_t in = ins[i];
        if ((in & 0x7C000000u) == 0x14000000u) return false;   // B / BL
        if ((in & 0x1F000000u) == 0x10000000u) return false;   // ADR / ADRP
        if ((in & 0x3B000000u) == 0x18000000u) return false;   // LDR (literal)
        if ((in & 0x7E000000u) == 0x34000000u) return false;   // CBZ / CBNZ
        if ((in & 0x7E000000u) == 0x36000000u) return false;   // TBZ / TBNZ
    }
    return true;
}

static bool writeCode(void* addr, const void* data, size_t len) {
    uintptr_t ps = (uintptr_t)(g_pageSize ? g_pageSize : 4096);
    uintptr_t page = (uintptr_t)addr & ~(ps - 1);
    size_t span = ((uintptr_t)addr + len) - page;
    if (syscall(__NR_mprotect, (void*)page, span, PROT_READ | PROT_WRITE | PROT_EXEC) != 0) return false;
    memcpy(addr, data, len);
    __builtin___clear_cache((char*)addr, (char*)addr + len);
    syscall(__NR_mprotect, (void*)page, span, PROT_READ | PROT_EXEC);
    return true;
}

static void* installInlineHook(void* target, int isAlloc, int argIdx, const char* tag) {
    const uint32_t* ti = (const uint32_t*)target;
    if (!fourInsnSafe(ti)) {
        tlog("hook %s: prologue %08x %08x %08x %08x not relocatable",
             tag, ti[0], ti[1], ti[2], ti[3]);
        return 0;
    }

    uint8_t* mem = allocStubSpace(128);
    if (!mem) { tlog("hook %s: no exec page", tag); return 0; }
    uint32_t* w = (uint32_t*)mem;
    int i = 0;

    // [0..3] 被覆盖的 4 条指令；[4] ldr x16,#8；[5] br x16；[6,7] target+16
    w[i++] = ti[0]; w[i++] = ti[1]; w[i++] = ti[2]; w[i++] = ti[3];
    w[i++] = encLdrLit(16, 2);
    w[i++] = 0xD61F0200u;                                  // br x16
    *(uint64_t*)&w[i] = (uint64_t)((uintptr_t)target + 16); i += 2;

    // 桩入口
    w[i++] = encStpPre(29, 30, 31, -ACH_FRAME);            // stp x29,x30,[sp,#-112]!
    w[i++] = 0x910003FD;                                   // mov x29, sp
    w[i++] = encStpOff(0, 1, 31, 16);
    w[i++] = encStpOff(2, 3, 31, 32);
    w[i++] = encStpOff(4, 5, 31, 48);
    w[i++] = encStpOff(6, 7, 31, 64);

    int adrIdx = i;                                        // adr x9, <resume>
    {
        int32_t imm = -(int32_t)(adrIdx * 4);
        w[i++] = 0x10000000u | ((uint32_t)(imm & 3) << 29)
               | (((uint32_t)((imm >> 2) & 0x7FFFF)) << 5) | 9;
    }
    w[i++] = encStrOff(9, 31, 80);                         // str x9,[sp,#80]
    w[i++] = 0xD2800000u | ((uint32_t)(argIdx & 0xFFFF) << 5) | 9;   // movz x9, #argIdx
    w[i++] = encStrOff(9, 31, 104);                        // str x9,[sp,#104]
    int ldrPost = i; w[i++] = 0;                           // ldr x16,<post>
    w[i++] = encStrOff(16, 31, 88);                        // str x16,[sp,#88]
    int ldrPre = i;  w[i++] = 0;                           // ldr x16,<preamble>
    w[i++] = 0xD61F0200u;                                  // br x16

    int litPost = i;
    *(uint64_t*)&w[i] = (uint64_t)(uintptr_t)(isAlloc ? ach_post_alloc : ach_post_free); i += 2;
    int litPre = i;
    *(uint64_t*)&w[i] = (uint64_t)(uintptr_t)(isAlloc ? ach_preamble_alloc : ach_preamble_free); i += 2;

    w[ldrPost] = encLdrLit(16, litPost - ldrPost);
    w[ldrPre]  = encLdrLit(16, litPre  - ldrPre);

    __builtin___clear_cache((char*)mem, (char*)mem + (size_t)i * 4);

    uint32_t patch[4];
    patch[0] = 0x58000050u;                                // ldr x16, #8
    patch[1] = 0xD61F0200u;                                // br x16
    // 注意：要跳到"桩入口"（mem + 32），不是 mem 本身 ——
    // mem 开头是给 resume 用的 4 条原指令，跳那儿等于什么都不做（踩过）。
    *(uint64_t*)&patch[2] = (uint64_t)(uintptr_t)(mem + 32);
    if (!writeCode(target, patch, 16)) { tlog("hook %s: patch failed", tag); return 0; }

    tlog("hook %s: ok target=%p stub=%p", tag, target, mem);
    return mem;
}

#else

static void* installInlineHook(void*, int, int, const char*) { return 0; }

#endif

// ---------------- 轻量 L2（malloc 家族；只在 -systemallocator 包打开） ----------------
// 关键设计（参考 loli_profiler）：
//   1. 分配抓栈、释放只按 ptr 扣减 —— 永远不会为 free 付栈回溯的开销；
//   2. 阈值以下只累加"被过滤字节数"，不建记录、不抓栈 —— 报表能显示漏掉多少；
//   3. 1/N 采样，被采样跳过的字节也单独累加；
//   4. g_inHook 守卫：unwinder 内部的 malloc 不再递归进我们的钩子。
static void* (*real_malloc )(size_t) = 0;
static void* (*real_calloc )(size_t, size_t) = 0;
static void* (*real_realloc)(void*, size_t) = 0;
static void* (*real_memalign)(size_t, size_t) = 0;
static int   (*real_posix_memalign)(void**, size_t, size_t) = 0;
static void* (*real_aligned_alloc)(size_t, size_t) = 0;

static inline void updateMaxUsz(size_t* target, size_t usz) {
    size_t curMax = __atomic_load_n(target, __ATOMIC_RELAXED);
    if (__builtin_expect(usz > curMax, 0)) {
        while (usz > curMax &&
               !__atomic_compare_exchange_n(target, &curMax, usz, true, __ATOMIC_RELAXED, __ATOMIC_RELAXED)) { }
    }
}

static __attribute__((always_inline)) inline void l2OnAlloc(void* p, size_t n, uintptr_t callerPc) {
    if (p && g_l2Enabled && !g_tls.inHook) {
        if (n < (size_t)g_l2Min) {
            size_t usz = malloc_usable_size(p);
            updateMaxUsz(&g_l2MaxFilteredUsz, usz);
            if (g_l2Sample > 1 && usz >= (size_t)g_l2Min) {
                updateMaxUsz(&g_l2MaxSampledUsz, usz);
                tlsAddFilteredReqAndSampledLive(&g_tls, (int64_t)n, (int64_t)usz);
            } else {
                tlsAddFilteredAlloc(&g_tls, (int64_t)n, (int64_t)usz);
            }
        } else if (g_l2Sample > 1 && ((g_tls.sampleSeq++ % (unsigned int)g_l2Sample) != 0)) {
            size_t usz = malloc_usable_size(p);
            updateMaxUsz(&g_l2MaxSampledUsz, usz);
            tlsAddSampledSkip(&g_tls, (int64_t)n, (int64_t)usz);
        } else {
            holdRecordAlloc((uintptr_t)p, n, 0, callerPc);   // 内部自己设 g_tls.inHook 防递归
        }
    }
}

static void* hook_malloc(size_t n) {
    void* p = real_malloc ? real_malloc(n) : 0;
    l2OnAlloc(p, n, (uintptr_t)__builtin_return_address(0));
    return p;
}

static void* hook_calloc(size_t a, size_t b) {
    void* p = real_calloc ? real_calloc(a, b) : 0;
    l2OnAlloc(p, a * b, (uintptr_t)__builtin_return_address(0));
    return p;
}

static void* hook_realloc(void* old, size_t n) {
    if (g_l2Enabled && old && !g_tls.inHook) holdRecordFree((uintptr_t)old);
    void* p = real_realloc ? real_realloc(old, n) : 0;
    l2OnAlloc(p, n, (uintptr_t)__builtin_return_address(0));
    return p;
}

static void* hook_memalign(size_t alignment, size_t n) {
    void* p = real_memalign ? real_memalign(alignment, n) : 0;
    l2OnAlloc(p, n, (uintptr_t)__builtin_return_address(0));
    return p;
}

static void* hook_aligned_alloc(size_t alignment, size_t n) {
    void* p = real_aligned_alloc ? real_aligned_alloc(alignment, n) : 0;
    l2OnAlloc(p, n, (uintptr_t)__builtin_return_address(0));
    return p;
}

static int hook_posix_memalign(void** memptr, size_t alignment, size_t n) {
    int rc = real_posix_memalign ? real_posix_memalign(memptr, alignment, n) : EINVAL;
    if (rc == 0 && memptr) l2OnAlloc(*memptr, n, (uintptr_t)__builtin_return_address(0));
    return rc;
}

static void hook_free(void* p) {
    if (p && g_l2Enabled && !g_tls.inHook) holdRecordFree((uintptr_t)p);
    if (real_free) real_free(p);
}

// ---------------- GOT/PLT 注入 ----------------
// dlopen 进来的库不参与全局符号解析，必须把应用与插件 .so 的 .got/.got.plt 槽位改写。
// 注意：系统库（/system /apex /vendor /product /odm）、linker 与 unwinder 库必须排除，
// 特别是不 hook libc.so 的 mmap/mprotect/munmap（pthread_exit/dlopen/scudo 内部持锁或 TLS
// 销毁阶段调用 mmap/munmap，进 _Unwind_Backtrace -> dl_iterate_phdr 极易死锁或崩溃；
// libc 堆分配已通过应用与插件层 GOT/PLT 的 malloc 家族完全覆盖）。
static ElfW(Addr) dynAddr(ElfW(Addr) base, ElfW(Addr) v) {
    return (v < base) ? (base + v) : v;
}

static bool isBasenameAllochook(const char* mod) {
    if (!mod || !*mod) return false;
    const char* slash = strrchr(mod, '/');
    const char* base = slash ? (slash + 1) : mod;
    return strcmp(base, "liballochook.so") == 0;
}

static uint32_t elfGnuHash(const char* name) {
    uint32_t h = 5381u;
    for (const unsigned char* s = (const unsigned char*)name; *s; ++s) {
        h = (h << 5) + h + *s;
    }
    return h;
}

static void* lookupDynSym(const struct dl_phdr_info* info, const char* targetName) {
    if (!info || !info->dlpi_phdr || !targetName || !*targetName) return 0;
    ElfW(Addr) base = info->dlpi_addr;
    const ElfW(Phdr)* ph = info->dlpi_phdr;
    const ElfW(Dyn)* dyn = 0;
    for (int i = 0; i < info->dlpi_phnum; ++i) {
        if (ph[i].p_type == PT_DYNAMIC) {
            dyn = (const ElfW(Dyn)*)((uintptr_t)base + (uintptr_t)ph[i].p_vaddr);
            break;
        }
    }
    if (!dyn) return 0;

    const ElfW(Sym)* symtab = 0;
    const char* strtab = 0;
    size_t strsz = 0;
    const uint32_t* hash = 0;
    const uint32_t* gnuHash = 0;

    for (const ElfW(Dyn)* d = dyn; d->d_tag != DT_NULL; ++d) {
        switch (d->d_tag) {
            case DT_SYMTAB:   symtab  = (const ElfW(Sym)*)dynAddr(base, d->d_un.d_ptr); break;
            case DT_STRTAB:   strtab  = (const char*)dynAddr(base, d->d_un.d_ptr); break;
            case DT_STRSZ:    strsz   = (size_t)d->d_un.d_val; break;
            case DT_HASH:     hash    = (const uint32_t*)dynAddr(base, d->d_un.d_ptr); break;
            case DT_GNU_HASH: gnuHash = (const uint32_t*)dynAddr(base, d->d_un.d_ptr); break;
            default: break;
        }
    }
    if (!symtab || !strtab) return 0;

    if (hash && hash[1] > 1) {
        uint32_t nchain = hash[1];
        for (uint32_t i = 1; i < nchain; ++i) {
            uint32_t nmOff = symtab[i].st_name;
            if (strsz && nmOff >= strsz) continue;
            if (symtab[i].st_shndx == SHN_UNDEF) continue;
            if (strcmp(strtab + nmOff, targetName) == 0) {
                return (void*)((uintptr_t)base + (uintptr_t)symtab[i].st_value);
            }
        }
        return 0;
    }

    if (gnuHash) {
        uint32_t nbuckets  = gnuHash[0];
        uint32_t symoffset = gnuHash[1];
        uint32_t bloomSize = gnuHash[2];
        if (nbuckets == 0) return 0;
        const uint32_t* buckets = (const uint32_t*)((uintptr_t)(gnuHash + 4) + (size_t)bloomSize * sizeof(ElfW(Addr)));
        const uint32_t* chain   = buckets + nbuckets;
        uint32_t h = elfGnuHash(targetName);
        uint32_t symIdx = buckets[h % nbuckets];
        if (symIdx < symoffset) return 0;
        for (uint32_t guard = 0; guard < 1024; ++guard) {
            uint32_t c = chain[symIdx - symoffset];
            if (((c ^ h) & ~1u) == 0) {
                uint32_t nmOff = symtab[symIdx].st_name;
                if ((!strsz || nmOff < strsz) && symtab[symIdx].st_shndx != SHN_UNDEF) {
                    if (strcmp(strtab + nmOff, targetName) == 0) {
                        return (void*)((uintptr_t)base + (uintptr_t)symtab[symIdx].st_value);
                    }
                }
            }
            if (c & 1u) break;
            symIdx++;
        }
    }
    return 0;
}

#if defined(__LP64__)
#if defined(__aarch64__)
#ifndef R_AARCH64_ABS64
#define R_AARCH64_ABS64 257
#endif
#ifndef R_AARCH64_GLOB_DAT
#define R_AARCH64_GLOB_DAT 1025
#endif
#ifndef R_AARCH64_JUMP_SLOT
#define R_AARCH64_JUMP_SLOT 1026
#endif
#define MY_R_TYPE(i) ELF64_R_TYPE(i)
#define IS_GLOB_DAT_TYPE(t) ((t) == R_AARCH64_JUMP_SLOT || (t) == R_AARCH64_GLOB_DAT)
#define IS_ABS64_TYPE(t, addend) ((t) == R_AARCH64_ABS64 && (addend) == 0)
#elif defined(__x86_64__)
#define MY_R_TYPE(i) ELF64_R_TYPE(i)
#define IS_GLOB_DAT_TYPE(t) ((t) == R_X86_64_JUMP_SLOT || (t) == R_X86_64_GLOB_DAT)
#define IS_ABS64_TYPE(t, addend) ((t) == R_X86_64_64 && (addend) == 0)
#else
#define MY_R_TYPE(i) ELF64_R_TYPE(i)
#define IS_GLOB_DAT_TYPE(t) (false)
#define IS_ABS64_TYPE(t, addend) ((addend) == 0)
#endif
#endif

#if defined(__LP64__)
struct PatchedMod { uintptr_t base; uint64_t seenMs; int patched; };
#define MAX_PATCH_MODS 128
static PatchedMod g_patchedMods[MAX_PATCH_MODS];
static int g_patchedModN = 0;

static void* resolveHookFn(const char* nm) {
    if (!strcmp(nm, "mmap") || !strcmp(nm, "mmap64")) return (void*)&hook_mmap;
    if (!strcmp(nm, "mprotect")) return (void*)&hook_mprotect;
    if (!strcmp(nm, "munmap"))   return (void*)&hook_munmap;
    if (g_l2Enabled && real_malloc) {
        if (!strcmp(nm, "malloc"))  return (void*)&hook_malloc;
        if (!strcmp(nm, "calloc"))  return (void*)&hook_calloc;
        if (!strcmp(nm, "realloc")) return (void*)&hook_realloc;
        if (!strcmp(nm, "free"))    return (void*)&hook_free;
        if (!strcmp(nm, "memalign") && real_memalign)             return (void*)&hook_memalign;
        if (!strcmp(nm, "posix_memalign") && real_posix_memalign) return (void*)&hook_posix_memalign;
        if (!strcmp(nm, "aligned_alloc") && real_aligned_alloc)   return (void*)&hook_aligned_alloc;
    }
    return 0;
}

static bool shouldPatchModule(const char* mod) {
    if (!mod || !*mod) return false;
    if (!strstr(mod, ".so")) return false;
    if (strstr(mod, "liballochook.so")) return false;
    if (strstr(mod, "/system/") || strstr(mod, "/apex/") || strstr(mod, "/vendor/") ||
        strstr(mod, "/product/") || strstr(mod, "/odm/") || strstr(mod, "/system_ext/")) return false;
    if (strstr(mod, "libunwind") || strstr(mod, "libbacktrace") || strstr(mod, "liblzma") || strstr(mod, "linker"))
        return false;
    return true;
}
#endif

static void registerEhMod(struct dl_phdr_info* info) {
    if (!info || !info->dlpi_phdr || info->dlpi_phnum == 0) return;
    uintptr_t base = (uintptr_t)info->dlpi_addr;
    const char* mod = info->dlpi_name;

    EhMod e;
    memset(&e, 0, sizeof(e));
    e.base = base;
    e.minLo = ~(uintptr_t)0;

    for (int i = 0; i < info->dlpi_phnum; ++i) {
        const ElfW(Phdr)& ph = info->dlpi_phdr[i];
        if (ph.p_type == PT_LOAD && (ph.p_flags & PF_X)) {
            uintptr_t s = base + (uintptr_t)ph.p_vaddr;
            uintptr_t hi = s + (uintptr_t)ph.p_memsz;
            if (hi > s && e.nSeg < MAX_EH_SEGS) {
                if (e.nSeg == 0) e.textBase = s;
                e.segs[e.nSeg].lo = s;
                e.segs[e.nSeg].hi = hi;
                e.nSeg++;
                if (s < e.minLo) e.minLo = s;
                if (hi > e.maxHi) e.maxHi = hi;
            }
        } else if (ph.p_type == PT_GNU_EH_FRAME) {
            e.ehHdr = base + (uintptr_t)ph.p_vaddr;
            e.ehHdrEnd = e.ehHdr + (uintptr_t)ph.p_memsz;
        }
    }
    if (e.nSeg == 0) return;

    uintptr_t selfC = (uintptr_t)(void*)&holdRecordAlloc;
    if (selfC >= e.minLo && selfC < e.maxHi) {
        if (g_selfHi == 0) { g_selfLo = e.minLo; g_selfHi = e.maxHi; }
    }

#if defined(__aarch64__)
    if (e.ehHdr != 0 && e.ehHdr + 4 <= e.ehHdrEnd) {
        const uint8_t* p = (const uint8_t*)e.ehHdr;
        const uint8_t* end = (const uint8_t*)e.ehHdrEnd;
        e.hdrVer = p[0];
        if (e.hdrVer == 1) {
            e.ehFramePtrEnc = p[1];
            e.fdeCountEnc   = p[2];
            e.tableEnc      = p[3];
            p += 4;
            bool ok = true;
            e.ehFramePtr = ehReadEncodedVal(&p, end, e.ehFramePtrEnc, e.ehHdr, e.textBase, 0, true, true, &ok);
            if (!ok) { e.ehFramePtr = 0; ok = true; }
            if (e.fdeCountEnc != 0x0Fu && e.fdeCountEnc != 0xFFu) {
                e.fdeCount = (uint32_t)ehReadEncodedVal(&p, end, e.fdeCountEnc, e.ehHdr, e.textBase, 0, false, false, &ok);
                if (!ok) e.fdeCount = 0;
            }
            uint8_t tfmt = e.tableEnc & 0x0Fu;
            uint32_t elemSz = 0;
            if (tfmt == 0x00 || tfmt == 0x04 || tfmt == 0x0C) elemSz = 8;
            else if (tfmt == 0x03 || tfmt == 0x0B) elemSz = 4;
            else if (tfmt == 0x02 || tfmt == 0x0A) elemSz = 2;
            e.entrySize = elemSz * 2;
            e.tableAddr = (uintptr_t)p;
            if (e.entrySize == 0 || (uintptr_t)p + (uintptr_t)e.fdeCount * e.entrySize > e.ehHdrEnd)
                e.fdeCount = 0;
        }
    }
#endif

    while (__sync_lock_test_and_set(&g_ehRegLock, 1)) { }
    int n = g_ehModN;
    bool dup = false;
    for (int i = 0; i < n; ++i) {
        if (g_ehMods[i].base == e.base && g_ehMods[i].minLo == e.minLo) { dup = true; break; }
    }
    if (!dup && n < MAX_EH_MODS) {
        g_ehMods[n] = e;
        __atomic_store_n(&g_ehModN, n + 1, __ATOMIC_RELEASE);
        if (mod && strstr(mod, "libunity.so"))        __atomic_store_n(&g_ehUnityIdx, n, __ATOMIC_RELEASE);
        else if (mod && strstr(mod, "libil2cpp.so")) __atomic_store_n(&g_ehIl2cppIdx, n, __ATOMIC_RELEASE);
    }
    __sync_lock_release(&g_ehRegLock);
}

static int patchModule(struct dl_phdr_info* info, size_t, void*) {
    registerEhMod(info);

#if !defined(__LP64__)
    (void)info;
    return 0;
#else
    const char* mod = info->dlpi_name;
    if (!shouldPatchModule(mod)) return 0;

    ElfW(Addr) base = info->dlpi_addr;
    PatchedMod* pm = 0;
    for (int i = 0; i < g_patchedModN; ++i) {
        if (g_patchedMods[i].base == (uintptr_t)base) { pm = &g_patchedMods[i]; break; }
    }
    if (pm && pm->patched) return 0;
    uint64_t now = nowMs();
    if (!pm) {
        if (g_patchedModN >= MAX_PATCH_MODS) return 0;
        pm = &g_patchedMods[g_patchedModN++];
        pm->base = (uintptr_t)base;
        pm->seenMs = now;
        pm->patched = 0;
        if (g_patchDelayMs > 0) return 0;
    } else if (now - pm->seenMs < (uint64_t)g_patchDelayMs) {
        return 0;
    }

    bool isUnity = strstr(mod, "libunity.so") != 0;

    // 顺带装 Unity 分配器的 inline hook（只做一次；32 位 ARM 不支持）
#if defined(__aarch64__)
    if (isUnity && g_holdEnabled && !g_unityHooked && g_offAlloc && g_offFree) {
        g_unityBase = (uintptr_t)base;
        tlog("inline: base=%p alloc=%p free=%p a1=%p f1=%p",
             (void*)base, (void*)(base + g_offAlloc), (void*)(base + g_offFree),
             (void*)(g_offAlloc1 ? base + g_offAlloc1 : 0),
             (void*)(g_offFree1 ? base + g_offFree1 : 0));

        void* a = installInlineHook((void*)(base + g_offAlloc), 1, 3, "DynamicHeap::Allocate");
        void* f = installInlineHook((void*)(base + g_offFree),  0, 3, "DualThread::TryDeallocate");

        if (a && f) {
            g_unityHooked = 1;
            trace("inline unity hooked");
        } else {
            tlog("inline: partial alloc=%p free=%p", a, f);
        }
    }
#endif

    const ElfW(Phdr)* ph = info->dlpi_phdr;
    const ElfW(Dyn)* dyn = 0;
    uintptr_t relroStart = 0, relroEnd = 0;
    for (int i = 0; i < info->dlpi_phnum; ++i) {
        if (ph[i].p_type == PT_DYNAMIC) {
            dyn = (const ElfW(Dyn)*)(base + ph[i].p_vaddr);
        } else if (ph[i].p_type == PT_GNU_RELRO && ph[i].p_memsz > 0) {
            relroStart = (uintptr_t)base + (uintptr_t)ph[i].p_vaddr;
            relroEnd   = relroStart + (uintptr_t)ph[i].p_memsz;
        }
    }
    if (!dyn) { pm->patched = 1; return 0; }

    const ElfW(Sym)* symtab = 0;
    const char* strtab = 0;
    size_t strsz = 0;
    const ElfW(Rela)* jmprel = 0;
    size_t pltrelsz = 0;
    const uint8_t* rela = 0;
    size_t relasz = 0;
    size_t relaent = sizeof(ElfW(Rela));

    for (const ElfW(Dyn)* d = dyn; d->d_tag != DT_NULL; ++d) {
        switch (d->d_tag) {
            case DT_SYMTAB:   symtab   = (const ElfW(Sym)*)dynAddr(base, d->d_un.d_ptr); break;
            case DT_STRTAB:   strtab   = (const char*)dynAddr(base, d->d_un.d_ptr);     break;
            case DT_STRSZ:    strsz    = (size_t)d->d_un.d_val; break;
            case DT_JMPREL:   jmprel   = (const ElfW(Rela)*)dynAddr(base, d->d_un.d_ptr); break;
            case DT_PLTRELSZ: pltrelsz = (size_t)d->d_un.d_val; break;
            case DT_RELA:     rela     = (const uint8_t*)dynAddr(base, d->d_un.d_ptr); break;
            case DT_RELASZ:   relasz   = (size_t)d->d_un.d_val; break;
            case DT_RELAENT:  relaent  = (size_t)d->d_un.d_val; break;
            default: break;
        }
    }
    if (!symtab || !strtab) { pm->patched = 1; return 0; }

    int patched = 0;
    // 1) .rela.plt (DT_JMPREL)
    uintptr_t ps = (uintptr_t)(g_pageSize ? g_pageSize : 4096);
    if (jmprel && pltrelsz) {
        size_t cnt = pltrelsz / sizeof(ElfW(Rela));
        for (size_t i = 0; i < cnt; ++i) {
            const ElfW(Rela)& r = jmprel[i];
            unsigned si = (unsigned)MY_R_SYM(r.r_info);
            if (si == 0 || (strsz && symtab[si].st_name >= strsz)) continue;
            const char* nm = strtab + symtab[si].st_name;
            void* fn = resolveHookFn(nm);
            if (!fn) continue;

            uintptr_t* slot = (uintptr_t*)(base + r.r_offset);
            if (*slot == (uintptr_t)fn) continue;
            uintptr_t page = (uintptr_t)slot & ~(ps - 1);
            syscall(__NR_mprotect, (void*)page, (size_t)ps, PROT_READ | PROT_WRITE);
            __atomic_store_n(slot, (uintptr_t)fn, __ATOMIC_RELEASE);
            patched++;
        }
    }

    // 2) .rela.dyn (DT_RELA: GLOB_DAT / RELRO 内 ABS64)
    if (rela && relasz && relaent >= sizeof(ElfW(Rela))) {
        for (size_t off = 0; off + sizeof(ElfW(Rela)) <= relasz; off += relaent) {
            const ElfW(Rela)& r = *(const ElfW(Rela)*)(rela + off);
            unsigned si = (unsigned)MY_R_SYM(r.r_info);
            if (si == 0) continue;
            unsigned rtype = (unsigned)MY_R_TYPE(r.r_info);
            bool isGlob = IS_GLOB_DAT_TYPE(rtype);
            bool isAbs  = IS_ABS64_TYPE(rtype, r.r_addend);
            if (!isGlob && !isAbs) continue;
            if (strsz && symtab[si].st_name >= strsz) continue;
            const char* nm = strtab + symtab[si].st_name;
            void* fn = resolveHookFn(nm);
            if (!fn) continue;

            uintptr_t slotAddr = (uintptr_t)base + (uintptr_t)r.r_offset;
            // ABS64 只允许改写 PT_GNU_RELRO 之内的只读 GOT 表项，绝不碰可写 .data 回调结构体
            if (isAbs) {
                if (relroEnd <= relroStart || slotAddr < relroStart || slotAddr + sizeof(uintptr_t) > relroEnd)
                    continue;
            }

            uintptr_t* slot = (uintptr_t*)slotAddr;
            if (*slot == (uintptr_t)fn) continue;
            uintptr_t page = slotAddr & ~(ps - 1);
            syscall(__NR_mprotect, (void*)page, (size_t)ps, PROT_READ | PROT_WRITE);
            __atomic_store_n(slot, (uintptr_t)fn, __ATOMIC_RELEASE);
            patched++;
        }
    }

    pm->patched = 1;
    if (patched > 0) {
        if (isUnity) trace("patched libunity");
        else if (strstr(mod, "libil2cpp.so")) trace("patched libil2cpp");
    }
    LOGI("module %s: plt=%d dyn=%d patched=%d",
         mod,
         jmprel ? (int)(pltrelsz / sizeof(ElfW(Rela))) : 0,
         (rela && relaent) ? (int)(relasz / relaent) : 0,
         patched);
    return 0;
#endif
}

static volatile int g_patchLock = 0;
static void patchAll() {
    uint8_t prev = g_tls.inHook;
    g_tls.inHook = 1;
    while (__sync_lock_test_and_set(&g_patchLock, 1)) { }
    dl_iterate_phdr(patchModule, 0);
    __sync_lock_release(&g_patchLock);
    g_tls.inHook = prev;
}

// ---------------- 控制文件轮询 ----------------
static int exists(const char* name) {
    char p[256];
    makePath(p, sizeof(p), name);
    return access(p, F_OK) == 0;
}
static void removeFile(const char* name) {
    char p[256];
    makePath(p, sizeof(p), name);
    unlink(p);
}

static void* watchdog(void*) {
    uint64_t fastUntilMs = (g_patchDelayMs == 0) ? (nowMs() + 5000ULL) : 0ULL;
    uint64_t lastPatchMs = 0;
    uint64_t lastVerifyMs = 0;
    uint64_t lastTextDumpMs = nowMs();

    for (;;) {
        uint64_t now = nowMs();
        bool fast = (fastUntilMs != 0 && now < fastUntilMs);
        usleep(fast ? 2000 : 200 * 1000);
        now = nowMs();

        // 早期模式前 5 秒每 2ms 轮询（不挂钩 dlopen 保留原始 linker namespace）；标准模式前 10 秒每 200ms 轮询；之后每 1 秒检查一次
        if (fast || (now - g_t0ms) <= 10000ULL || (now - lastPatchMs) >= 1000ULL) {
            patchAll();
            lastPatchMs = now;
        }

        // 每 10 秒记账一次：入口字节（判断补丁是否还在）+ 各项计数
        {
            static int cnt = 0;
            if (g_unityHooked && g_unityBase && (now - lastVerifyMs) >= 10000ULL) {
                lastVerifyMs = now;
                cnt++;
                const uint32_t* ins = (const uint32_t*)(g_unityBase + g_offAlloc);
                char b[200];
                snprintf(b, sizeof(b), "verify#%d entry=%08x alloc=%d free=%d recs=%d small=%d noframe=%d",
                         cnt, ins[0], g_holdAllocHits, g_holdFreeHits, g_holdRecs,
                         g_holdSmall, g_holdNoFrame);
                trace(b);
            }
        }

        // 先完成 dump / reset，再删除信号文件，让 Studio 可精确轮询信号消失，告别盲等与半截读取
        if (exists("/allochook.snap"))  { dumpAll("snap");  removeFile("/allochook.snap"); }
        if (exists("/allochook.reset")) { resetTable();     removeFile("/allochook.reset"); }
        if ((!g_dumpBinary || g_dumpTxt) && (now - lastTextDumpMs) >= (uint64_t)g_dumpInterval * 1000ULL) {
            lastTextDumpMs = now;
            dumpText();
        }
    }
    return 0;
}

// ---------------- 配置读取 ----------------
// 读取宿主侧算好的 libunity 符号偏移（十六进制）：
//   alloc=0x8c17c
//   free=0x8cf00
static void loadUnityOffsets() {
    char path[256];
    makePath(path, sizeof(path), "/allochook_unity.txt");
    int fd = open(path, O_RDONLY);
    if (fd < 0) return;
    char buf[256];
    int n = (int)read(fd, buf, sizeof(buf) - 1);
    close(fd);
    if (n <= 0) return;
    buf[n] = 0;

    char* p = buf;
    while (*p) {
        char* e = strchr(p, '\n');
        if (e) *e = 0;
        char* eq = strchr(p, '=');
        if (eq) {
            *eq = 0;
            char* k = p; char* v = eq + 1;
            while (*k == ' ') ++k;
            while (*v == ' ') ++v;
            unsigned long long hv = 0;
            bool hex = (v[0] == '0' && (v[1] == 'x' || v[1] == 'X'));
            if (hex) v += 2;
            for (char* q = v; *q; ++q) {
                int d;
                if (*q >= '0' && *q <= '9') d = *q - '0';
                else if (*q >= 'a' && *q <= 'f') d = *q - 'a' + 10;
                else if (*q >= 'A' && *q <= 'F') d = *q - 'A' + 10;
                else break;
                hv = hv * (hex ? 16 : 10) + (unsigned)d;
            }
            if (!strcmp(k, "alloc")) g_offAlloc = hv;
            else if (!strcmp(k, "free")) g_offFree = hv;
            else if (!strcmp(k, "alloc1")) g_offAlloc1 = hv;
            else if (!strcmp(k, "free1")) g_offFree1 = hv;
        }
        if (!e) break;
        p = e + 1;
    }
}

static void loadConfig() {
    char path[256];
    makePath(path, sizeof(path), "/allochook.cfg");
    int fd = open(path, O_RDONLY);
    if (fd < 0) return;
    char buf[512];
    int n = (int)read(fd, buf, sizeof(buf) - 1);
    close(fd);
    if (n <= 0) return;
    buf[n] = 0;

    char* p = buf;
    while (*p) {
        char* e = strchr(p, '\n');
        if (e) *e = 0;
        char* eq = strchr(p, '=');
        if (eq) {
            *eq = 0;
            char* k = p; char* v = eq + 1;
            while (*k == ' ') ++k;
            while (*v == ' ') ++v;
            int iv = atoi(v);
            if (!strcmp(k, "min_bytes") && iv > 0) g_minBytes = iv;
            else if (!strcmp(k, "max_frames") && iv > 0) g_maxFrames = iv > MAXF ? MAXF : iv;
            else if (!strcmp(k, "dump_interval") && iv > 0) g_dumpInterval = iv;
            else if (!strcmp(k, "dump_binary")) g_dumpBinary = iv;
            else if (!strcmp(k, "dump_txt")) g_dumpTxt = iv;
            else if (!strcmp(k, "hold")) g_holdEnabled = iv;
            else if (!strcmp(k, "reg")) g_regEnabled = iv;
            else if (!strcmp(k, "patch_delay_ms") && iv >= 0) g_patchDelayMs = iv;
            else if (!strcmp(k, "warmup_ms") && iv >= 0) g_warmupMs = (uint64_t)iv;
            else if (!strcmp(k, "bt_diag")) g_btDiag = iv;
            else if (!strcmp(k, "hold_min_bytes") && iv > 0) g_holdMin = iv;
            else if (!strcmp(k, "hold_frames") && iv > 0) g_holdFrames = iv > MAXF ? MAXF : iv;
            else if (!strcmp(k, "hold_unwind")) g_holdUnwind = iv;
            else if (!strcmp(k, "l2")) g_l2Enabled = iv;
            else if (!strcmp(k, "l2_min_bytes") && iv > 0) g_l2Min = iv;
            else if (!strcmp(k, "l2_frames") && iv > 0) g_l2Frames = iv > MAXF ? MAXF : iv;
            else if (!strcmp(k, "l2_sample") && iv > 0) g_l2Sample = iv;
        }
        if (!e) break;
        p = e + 1;
    }
    LOGI("cfg min=%d frames=%d interval=%d bin=%d", g_minBytes, g_maxFrames, g_dumpInterval, g_dumpBinary);
}

// ---------------- 初始化 ----------------
static int tryDir(const char* dir) {
    char path[256];
    int n = 0;
    for (const char* p = dir; *p && n < 200; ++p) path[n++] = *p;
    const char* f = "/allochook_init.txt";
    for (const char* p = f; *p && n < 250; ++p) path[n++] = *p;
    path[n] = 0;

    int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (fd < 0) return 0;
    char msg[64];
    int m = snprintf(msg, sizeof(msg), "pid=%d dir=%s\n", (int)getpid(), dir);
    write(fd, msg, m);
    close(fd);
    return 1;
}

extern "C" __attribute__((visibility("default"))) volatile int allochook_active_flag = 0;
extern "C" __attribute__((visibility("default"))) int  allochook_version();
extern "C" __attribute__((visibility("default"))) void allochook_snapshot();
extern "C" __attribute__((visibility("default"))) void allochook_reset();

static volatile int g_isProxy = 0;
static int  (*g_primaryVersion)() = 0;
static void (*g_primarySnapshot)() = 0;
static void (*g_primaryReset)() = 0;

struct SelfBaseSearch {
    uintptr_t addr;
    uintptr_t foundBase;
    bool      found;
};

static int findSelfBaseCb(struct dl_phdr_info* info, size_t, void* data) {
    SelfBaseSearch* s = (SelfBaseSearch*)data;
    if (!info || !info->dlpi_phdr) return 0;
    uintptr_t base = (uintptr_t)info->dlpi_addr;
    for (int i = 0; i < info->dlpi_phnum; ++i) {
        const ElfW(Phdr)& ph = info->dlpi_phdr[i];
        if (ph.p_type == PT_LOAD && (ph.p_flags & PF_X)) {
            uintptr_t lo = base + (uintptr_t)ph.p_vaddr;
            uintptr_t hi = lo + (uintptr_t)ph.p_memsz;
            if (s->addr >= lo && s->addr < hi) {
                s->foundBase = base;
                s->found = true;
                return 1;
            }
        }
    }
    return 0;
}

struct PrimarySearch {
    uintptr_t selfBase;
    uintptr_t primaryBase;
    int  (*versionFn)();
    void (*snapshotFn)();
    void (*resetFn)();
    bool found;
};

static int findPrimaryCb(struct dl_phdr_info* info, size_t, void* data) {
    PrimarySearch* ps = (PrimarySearch*)data;
    if (!info || !info->dlpi_phdr || !info->dlpi_name) return 0;
    if ((uintptr_t)info->dlpi_addr == ps->selfBase) return 0;
    if (!isBasenameAllochook(info->dlpi_name)) return 0;

    volatile int* flagPtr = (volatile int*)lookupDynSym(info, "allochook_active_flag");
    if (!flagPtr) return 0;
    if (__atomic_load_n(flagPtr, __ATOMIC_ACQUIRE) != 1) return 0;

    ps->primaryBase = (uintptr_t)info->dlpi_addr;
    ps->versionFn   = (int  (*)())lookupDynSym(info, "allochook_version");
    ps->snapshotFn  = (void (*)())lookupDynSym(info, "allochook_snapshot");
    ps->resetFn     = (void (*)())lookupDynSym(info, "allochook_reset");
    ps->found = true;
    return 1;
}

__attribute__((constructor)) static void initHook() {
    // 跨 Bionic linker namespace 双重加载防护：若已有主副本激活，当前次副本进入零开销代理模式
    SelfBaseSearch sbs = { (uintptr_t)(void*)&initHook, 0, false };
    dl_iterate_phdr(findSelfBaseCb, &sbs);
    if (sbs.found) {
        PrimarySearch ps = { sbs.foundBase, 0, 0, 0, 0, false };
        dl_iterate_phdr(findPrimaryCb, &ps);
        if (ps.found) {
            g_primaryVersion  = ps.versionFn;
            g_primarySnapshot = ps.snapshotFn;
            g_primaryReset    = ps.resetFn;
            __atomic_store_n(&g_isProxy, 1, __ATOMIC_RELEASE);
            LOGI("proxy mode: secondary liballochook (%p) delegating to primary (%p)",
                 (void*)sbs.foundBase, (void*)ps.primaryBase);
            return;
        }
    }

    long ps = sysconf(_SC_PAGESIZE);
    if (ps >= 4096 && (ps & (ps - 1)) == 0) g_pageSize = (size_t)ps;

    char buf[256];
    int fd = open("/proc/self/cmdline", O_RDONLY);
    int n = (fd >= 0) ? (int)read(fd, buf, sizeof(buf) - 1) : -1;
    if (fd >= 0) close(fd);
    if (n <= 0) { LOGI("cmdline read failed"); return; }
    buf[n] = 0;

    const char* prefixes[2] = { "/data/data/", "/data/user/0/" };
    g_dir[0] = 0;
    for (int c = 0; c < 2 && !g_dir[0]; ++c) {
        char cand[160];
        int k = 0;
        for (const char* p = prefixes[c]; *p && k < 100; ++p) cand[k++] = *p;
        for (int i = 0; i < n && buf[i] && k < 140; ++i) cand[k++] = buf[i];
        cand[k] = 0;
        if (tryDir(cand)) {
            for (int i = 0; i <= k; ++i) g_dir[i] = cand[i];
            LOGI("dir=%s pid=%d", g_dir, (int)getpid());
        }
    }
    if (!g_dir[0]) {
        const char* envDir = getenv("ALLOCHOOK_DIR");
        if (envDir && *envDir && strlen(envDir) < 140 && tryDir(envDir)) {
            strncpy(g_dir, envDir, sizeof(g_dir) - 1);
            g_dir[sizeof(g_dir) - 1] = 0;
            LOGI("dir=%s pid=%d (env)", g_dir, (int)getpid());
        }
    }
    if (!g_dir[0]) { LOGI("no writable dir for pkg=%s", buf); return; }

    if (pthread_key_create(&g_tlsKey, tlsOnExit) == 0) {
        __atomic_store_n(&g_tlsKeyReady, 1, __ATOMIC_RELEASE);
    }

    loadConfig();

    // 持有者追踪需要的符号偏移由宿主侧（Studio）从 libunity 符号表算好后写入
    // allochook_unity.txt；这里只负责读取，读不到就跳过（不影响 L1）
    loadUnityOffsets();
    if (g_holdEnabled) {
        if (g_offAlloc && g_offFree)
            LOGI("hold: enabled min=%d frames=%d alloc=0x%llx free=0x%llx",
                 g_holdMin, g_holdFrames, g_offAlloc, g_offFree);
        else
            LOGI("hold: no offsets (allochook_unity.txt), will skip inline hooks");
    }

    // L2 只在 -systemallocator 包打开；先解析 libc 真身，再改 GOT（顺序反了会 dlsym 到自己）
    if (g_l2Enabled) {
        real_malloc         = (void* (*)(size_t))               dlsym(RTLD_DEFAULT, "malloc");
        real_calloc         = (void* (*)(size_t, size_t))       dlsym(RTLD_DEFAULT, "calloc");
        real_realloc        = (void* (*)(void*, size_t))        dlsym(RTLD_DEFAULT, "realloc");
        real_memalign       = (void* (*)(size_t, size_t))       dlsym(RTLD_DEFAULT, "memalign");
        real_posix_memalign = (int   (*)(void**, size_t, size_t))dlsym(RTLD_DEFAULT, "posix_memalign");
        real_aligned_alloc  = (void* (*)(size_t, size_t))       dlsym(RTLD_DEFAULT, "aligned_alloc");
        real_free           = (void  (*)(void*))                dlsym(RTLD_DEFAULT, "free");
        if (!real_malloc || !real_free || (void*)real_malloc == (void*)&hook_malloc) {
            LOGI("l2: dlsym self/null, disabled");
            g_l2Enabled = 0;
        } else {
            LOGI("l2: enabled min=%d frames=%d sample=1/%d", g_l2Min, g_l2Frames, g_l2Sample);
        }
    }

    // 对比实验：系统 libc 是否提供 backtrace()（Android 12 起通常有）
    g_backtraceFn = (BacktraceFn)dlsym(RTLD_DEFAULT, "backtrace");
    tlog("backtrace sym=%p", (void*)g_backtraceFn);

    trace("cfg ok");
    dumpMaps();
    trace("maps ok");
    g_t0ms = nowMs();
    g_ready = 1;
    __atomic_store_n(&allochook_active_flag, 1, __ATOMIC_RELEASE);
    patchAll();
    trace("patchAll ok");

    // 自检：从本函数抓一次栈，把结果和本函数地址一起写进 trace。
    // 如果这些 PC 不在 liballochook 的代码段附近，说明 _Unwind_Backtrace 本身不可用。
    {
        uintptr_t tp[6];
        Bt t = { tp, 6, 0, 0 };
        _Unwind_Backtrace(btcb, &t);
        char b[220];
        int n = snprintf(b, sizeof(b), "selftest here=%llx n=%d:",
                         (unsigned long long)(uintptr_t)&initHook, t.n);
        for (int i = 0; i < t.n && i < 5 && n < (int)sizeof(b) - 24; ++i)
            n += snprintf(b + n, sizeof(b) - n, " %llx", (unsigned long long)tp[i]);
        trace(b);
    }

    LOGI("init done, warmup=%llums (no unwinding until then)", (unsigned long long)g_warmupMs);

    pthread_t t;
    if (pthread_create(&t, 0, watchdog, 0) == 0) pthread_detach(t);
}

__attribute__((destructor)) static void finiHook() {
    if (__atomic_load_n(&g_isProxy, __ATOMIC_ACQUIRE)) return;
    g_ready = 0;
    __atomic_store_n(&allochook_active_flag, 0, __ATOMIC_RELEASE);
    dumpAll("exit");
}

// 供 C# 侧 DllImport 使用（导出 allochook_active_flag + allochook_* 接口，其余全部 hidden，
// 避免我们的 malloc/mmap 被当成全局符号插入到别的库里导致无限递归）
extern "C" __attribute__((visibility("default"))) int  allochook_version()  {
    if (__atomic_load_n(&g_isProxy, __ATOMIC_ACQUIRE) && g_primaryVersion) return g_primaryVersion();
    return 100;
}
extern "C" __attribute__((visibility("default"))) void allochook_snapshot() {
    if (__atomic_load_n(&g_isProxy, __ATOMIC_ACQUIRE)) {
        if (g_primarySnapshot) g_primarySnapshot();
        return;
    }
    if (g_ready) dumpAll("api");
}
extern "C" __attribute__((visibility("default"))) void allochook_reset()    {
    if (__atomic_load_n(&g_isProxy, __ATOMIC_ACQUIRE)) {
        if (g_primaryReset) g_primaryReset();
        return;
    }
    if (g_ready) resetTable();
}
