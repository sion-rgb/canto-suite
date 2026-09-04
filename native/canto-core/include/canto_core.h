#ifndef CANTO_CORE_H
#define CANTO_CORE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
  #if defined(CANTO_CORE_BUILD)
    #define CANTO_API __declspec(dllexport)
  #else
    #define CANTO_API __declspec(dllimport)
  #endif
#else
  #define CANTO_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define CANTO_ABI_VERSION 1u

typedef struct canto_engine canto_engine;

typedef enum canto_status {
  CANTO_OK = 0,
  CANTO_INVALID_ARGUMENT = 1,
  CANTO_OUT_OF_MEMORY = 2,
  CANTO_INVALID_STATE = 3,
  CANTO_QUEUE_FULL = 4,
  CANTO_NO_RESULT = 5,
  CANTO_BACKEND_UNAVAILABLE = 6,
  CANTO_MODEL_ERROR = 7,
  CANTO_INTERNAL_ERROR = 8
} canto_status;

typedef enum canto_result_kind {
  CANTO_RESULT_PARTIAL = 1,
  CANTO_RESULT_FINAL = 2,
  CANTO_RESULT_ERROR = 3
} canto_result_kind;

typedef struct canto_engine_config {
  uint32_t struct_size;
  uint32_t sample_rate_hz;
  uint32_t ring_capacity_samples;
  uint32_t result_queue_capacity;
  uint32_t chunk_samples;
} canto_engine_config;

typedef struct canto_capabilities {
  uint32_t struct_size;
  uint32_t abi_version;
  uint8_t supports_streaming;
  uint8_t supports_timestamps;
  uint8_t supports_hotwords;
  uint8_t supports_vad;
  uint32_t max_sample_rate_hz;
  char backend[32];
} canto_capabilities;

typedef struct canto_result {
  uint32_t struct_size;
  canto_result_kind kind;
  int64_t start_ms;
  int64_t end_ms;
  char* text;
  size_t text_length;
} canto_result;

/** Creates one independent engine. The returned handle is owned by the caller. */
CANTO_API canto_status canto_engine_create(
    const canto_engine_config* config,
    canto_engine** out_engine);

/** Stops the worker and frees the handle. Null is accepted. */
CANTO_API void canto_engine_destroy(canto_engine* engine);

/** Loads a model directory or the reserved test model id "test://deterministic". */
CANTO_API canto_status canto_model_load(canto_engine* engine, const char* model_path);
CANTO_API canto_status canto_model_unload(canto_engine* engine);

/** Selects content mode (0) or timestamp mode (1). Call before model load. */
CANTO_API canto_status canto_engine_set_timestamp_mode(
    canto_engine* engine, uint8_t enabled);

/** Requests prompt cancellation of queued/current inference. Thread-safe. */
CANTO_API canto_status canto_engine_cancel(canto_engine* engine);

/** Clears buffered audio and queued results without unloading the model. */
CANTO_API canto_status canto_engine_reset(canto_engine* engine);

/** Copies PCM16 samples into bounded native-owned storage. Input is never retained. */
CANTO_API canto_status canto_push_pcm16(
    canto_engine* engine,
    const int16_t* samples,
    size_t sample_count,
    uint8_t end_of_stream);

/** Transfers ownership of result->text to the caller. Use canto_result_free. */
CANTO_API canto_status canto_poll_result(canto_engine* engine, canto_result* out_result);
CANTO_API void canto_result_free(canto_result* result);

CANTO_API canto_status canto_engine_get_capabilities(
    const canto_engine* engine,
    canto_capabilities* out_capabilities);

/** Returns the most recently completed native ASR call duration, or -1. */
CANTO_API int64_t canto_engine_last_inference_ms(const canto_engine* engine);

CANTO_API const char* canto_status_message(canto_status status);

#ifdef __cplusplus
}
#endif

#endif
