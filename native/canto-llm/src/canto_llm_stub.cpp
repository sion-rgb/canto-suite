#include <cstdint>

#if defined(__GNUC__)
#define CANTO_LLM_API __attribute__((visibility("default")))
#else
#define CANTO_LLM_API
#endif

extern "C" {
CANTO_LLM_API int32_t canto_llm_create(const char*, int32_t, void**) { return 8; }
CANTO_LLM_API int32_t canto_llm_generate(void*, const char*, int32_t, char**) { return 8; }
CANTO_LLM_API void canto_llm_free_text(char*) {}
CANTO_LLM_API void canto_llm_destroy(void*) {}
}
