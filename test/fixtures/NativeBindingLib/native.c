// Trivial native library used by the NativeBindingLib fixture.
//
// native_get_version returns a compile-time version constant. The constant is
// baked in via the NATIVE_VER macro at build time (see BuildUtility), so the
// 1.0.0 native build returns 1, the 2.0.0 build returns 2, etc. This lets the
// NativeBindingApp sample assert that each routed managed version binds its
// own renamed native library (distinct native version) rather than sharing a
// single native lib.

#ifndef NATIVE_VER
#define NATIVE_VER 0
#endif

#if defined(_WIN32) || defined(_WIN32_) || defined(__CYGWIN__)
#define NATIVE_EXPORT __declspec(dllexport)
#else
#define NATIVE_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

NATIVE_EXPORT int native_get_version(void)
{
    return NATIVE_VER;
}

#ifdef __cplusplus
}
#endif