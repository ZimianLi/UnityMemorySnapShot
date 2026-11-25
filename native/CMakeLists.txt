cmake_minimum_required(VERSION 3.20)
project(allochook CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# 只编一个共享库：liballochook.so
add_library(allochook SHARED src/allochook.cpp)

target_compile_options(allochook PRIVATE
    -O2
    -fPIC
    -fno-exceptions
    -fno-rtti
    -nostdlib++
    -funwind-tables
    -fno-omit-frame-pointer
    -Wall
    # 只导出 allochook_* 三个符号。
    # 否则我们定义的 malloc/free/mmap 会出现在 RTLD_DEFAULT 里，
    # dlsym(RTLD_DEFAULT,"malloc") 会拿到自己，L2 直接变成无限递归。
    -fvisibility=hidden
)

target_link_libraries(allochook PRIVATE
    unwind
    log
)

# 输出名固定为 liballochook.so（Unity / Android 约定）
set_target_properties(allochook PROPERTIES
    PREFIX "lib"
    OUTPUT_NAME "allochook"
)
