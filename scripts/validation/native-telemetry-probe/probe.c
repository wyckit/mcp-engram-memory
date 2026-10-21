/* Test-only ONNX replacement. Capture the native environment at library LOAD,
 * then report it when the managed binding calls its first API. No inference. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <windows.h>
#define EXPORT __declspec(dllexport)
#define CALL __stdcall
#else
#define EXPORT __attribute__((visibility("default")))
#define CALL
#endif

static int disabled, windows_suppressed, ci_present;
static const char *ci_names[] = {
    "CI", "TF_BUILD", "GITHUB_ACTIONS", "GITLAB_CI", "CIRCLECI", "TRAVIS",
    "JENKINS_URL", "CODEBUILD_BUILD_ID", "BUILDKITE", "TEAMCITY_VERSION",
    "APPVEYOR", "BITBUCKET_BUILD_NUMBER", "SYSTEM_TEAMFOUNDATIONCOLLECTIONURI"
};
static int is_one(const char *name) {
#ifdef _WIN32
    char value[16];
    return GetEnvironmentVariableA(name, value, sizeof(value)) == 1 && value[0] == '1';
#else
    const char *value = getenv(name);
    return value && strcmp(value, "1") == 0;
#endif
}
static void capture(void) {
    disabled = is_one("ORT_DISABLE_TELEMETRY");
    windows_suppressed = is_one("ORT_RUNNING_UNIT_TESTS");
    for (size_t i = 0; i < sizeof(ci_names) / sizeof(ci_names[0]); i++) {
#ifdef _WIN32
        if (GetEnvironmentVariableA(ci_names[i], NULL, 0)) ci_present = 1;
#else
        if (getenv(ci_names[i])) ci_present = 1;
#endif
    }
}
#ifdef _WIN32
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    (void)instance; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) capture();
    return TRUE;
}
#else
__attribute__((constructor)) static void on_load(void) { capture(); }
#endif

EXPORT const void *CALL OrtGetApiBase(void) {
    printf("{\"probe\":\"native-load\",\"disabled\":%d,\"windowsSuppressed\":%d,\"ciPresent\":%d}\n",
           disabled, windows_suppressed, ci_present);
    fflush(stdout);
    /* Distinct sentinel: success must include the captured evidence, not just exit. */
    _Exit(86);
}
