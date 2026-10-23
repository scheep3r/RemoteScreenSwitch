cmake_minimum_required(VERSION 3.25)

project(RdpBridge VERSION 1.0.0 LANGUAGES C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
# Static CRT, matching the x64-windows-static-rdp triplet.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

find_package(WinPR CONFIG REQUIRED)
find_package(FreeRDP CONFIG REQUIRED)
find_package(FreeRDP-Client CONFIG REQUIRED)

add_library(RdpBridge SHARED
    include/rdpbridge.h
    src/rb_internal.h
    src/rdpbridge.c
    src/rb_pointer.c
    src/rb_clipboard.c
    src/rb_focus.c
)

target_include_directories(RdpBridge PRIVATE include)
target_compile_definitions(RdpBridge PRIVATE RDPBRIDGE_EXPORTS WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(RdpBridge PRIVATE /W4 /wd4100 /wd4127 /utf-8)
target_link_libraries(RdpBridge PRIVATE freerdp-client freerdp winpr ws2_32 user32 gdi32)

install(TARGETS RdpBridge RUNTIME DESTINATION bin)
