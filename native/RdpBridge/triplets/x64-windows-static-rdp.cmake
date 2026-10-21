# x64, static libraries, static CRT: everything is linked into RdpBridge.dll,
# so the target machine needs neither FreeRDP/OpenSSL nor the VC++ redistributable.
set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE static)
set(VCPKG_LIBRARY_LINKAGE static)

# FreeRDP: enable the H.264 (AVC420/444) decoder via Windows Media Foundation.
if(PORT STREQUAL "freerdp")
    set(VCPKG_CMAKE_CONFIGURE_OPTIONS -DWITH_MEDIA_FOUNDATION=ON)
endif()
