#include "canto_core.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <filesystem>
#include <memory>
#include <mutex>
#include <new>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#if defined(CANTO_ENABLE_SHERPA_ONNX)
#include "sherpa-onnx/c-api/c-api.h"
#if defined(_WIN32)
#define NOMINMAX
#include <windows.h>
#else
#include <dlfcn.h>
#endif
#endif

#if defined(CANTO_ENABLE_WHISPER_CPP)
#include "whisper.h"
#endif

namespace {

struct OwnedResult {
  canto_result_kind kind{CANTO_RESULT_PARTIAL};
  int64_t start_ms{0};
  int64_t end_ms{0};
  std::string text;
};

class SampleRing {
 public:
  explicit SampleRing(size_t capacity) : data_(capacity) {}

  size_t available_write() const noexcept { return data_.size() - size_; }
  size_t size() const noexcept { return size_; }

  bool push(const int16_t* samples, size_t count) noexcept {
    if (count > available_write()) return false;
    for (size_t i = 0; i < count; ++i) {
      data_[write_] = samples[i];
      write_ = (write_ + 1) % data_.size();
    }
    size_ += count;
    return true;
  }

  size_t pop(int16_t* destination, size_t count) noexcept {
    const size_t actual = std::min(count, size_);
    for (size_t i = 0; i < actual; ++i) {
      destination[i] = data_[read_];
      read_ = (read_ + 1) % data_.size();
    }
    size_ -= actual;
    return actual;
  }

  void clear() noexcept { read_ = write_ = size_ = 0; }

 private:
  std::vector<int16_t> data_;
  size_t read_{0};
  size_t write_{0};
  size_t size_{0};
};

#if defined(CANTO_ENABLE_SHERPA_ONNX)
class SherpaRuntime {
 public:
  using CreateRecognizer = decltype(&SherpaOnnxCreateOfflineRecognizer);
  using DestroyRecognizer = decltype(&SherpaOnnxDestroyOfflineRecognizer);
  using CreateStream = decltype(&SherpaOnnxCreateOfflineStream);
  using DestroyStream = decltype(&SherpaOnnxDestroyOfflineStream);
  using AcceptWaveform = decltype(&SherpaOnnxAcceptWaveformOffline);
  using DecodeStream = decltype(&SherpaOnnxDecodeOfflineStream);
  using GetResult = decltype(&SherpaOnnxGetOfflineStreamResult);
  using DestroyResult = decltype(&SherpaOnnxDestroyOfflineRecognizerResult);

  ~SherpaRuntime() {
#if defined(_WIN32)
    if (module_ != nullptr) FreeLibrary(module_);
    if (onnxruntime_ != nullptr) FreeLibrary(onnxruntime_);
#else
    if (module_ != nullptr) dlclose(module_);
#endif
  }

  bool load() {
#if defined(_WIN32)
    std::vector<std::filesystem::path> candidates;
    wchar_t executable[MAX_PATH]{};
    const DWORD length = GetModuleFileNameW(nullptr, executable, MAX_PATH);
    if (length > 0 && length < MAX_PATH) {
      const auto directory = std::filesystem::path(executable).parent_path();
      candidates.push_back(directory / L"sherpa-onnx-c-api.dll");
      candidates.push_back(directory / L"resources" / L"bin" /
                           L"sherpa-onnx-c-api.dll");
    }
#if defined(CANTO_SHERPA_RUNTIME_DIR)
    candidates.emplace_back(std::filesystem::path(CANTO_SHERPA_RUNTIME_DIR) /
                            L"sherpa-onnx-c-api.dll");
#endif
    for (const auto& candidate : candidates) {
      const auto onnxruntime_path = candidate.parent_path() / L"onnxruntime.dll";
      onnxruntime_ = LoadLibraryExW(onnxruntime_path.c_str(), nullptr,
                                    LOAD_WITH_ALTERED_SEARCH_PATH);
      if (onnxruntime_ == nullptr) continue;
      module_ = LoadLibraryExW(candidate.c_str(), nullptr,
                               LOAD_WITH_ALTERED_SEARCH_PATH);
      if (module_ != nullptr) break;
      FreeLibrary(onnxruntime_);
      onnxruntime_ = nullptr;
    }
#else
    module_ = dlopen("libsherpa-onnx-c-api.so", RTLD_NOW | RTLD_LOCAL);
#endif
    if (module_ == nullptr) return false;
    return bind(create_recognizer, "SherpaOnnxCreateOfflineRecognizer") &&
           bind(destroy_recognizer, "SherpaOnnxDestroyOfflineRecognizer") &&
           bind(create_stream, "SherpaOnnxCreateOfflineStream") &&
           bind(destroy_stream, "SherpaOnnxDestroyOfflineStream") &&
           bind(accept_waveform, "SherpaOnnxAcceptWaveformOffline") &&
           bind(decode_stream, "SherpaOnnxDecodeOfflineStream") &&
           bind(get_result, "SherpaOnnxGetOfflineStreamResult") &&
           bind(destroy_result, "SherpaOnnxDestroyOfflineRecognizerResult");
  }

  CreateRecognizer create_recognizer{};
  DestroyRecognizer destroy_recognizer{};
  CreateStream create_stream{};
  DestroyStream destroy_stream{};
  AcceptWaveform accept_waveform{};
  DecodeStream decode_stream{};
  GetResult get_result{};
  DestroyResult destroy_result{};

 private:
  template <typename Function>
  bool bind(Function& output, const char* name) {
#if defined(_WIN32)
    output = reinterpret_cast<Function>(GetProcAddress(module_, name));
#else
    output = reinterpret_cast<Function>(dlsym(module_, name));
#endif
    return output != nullptr;
  }

#if defined(_WIN32)
  HMODULE module_{nullptr};
  HMODULE onnxruntime_{nullptr};
#else
  void* module_{nullptr};
#endif
};

struct SherpaRecognizer {
  std::shared_ptr<SherpaRuntime> runtime;
  const SherpaOnnxOfflineRecognizer* handle{nullptr};

  ~SherpaRecognizer() {
    if (handle != nullptr) runtime->destroy_recognizer(handle);
  }
};
#endif

#if defined(CANTO_ENABLE_WHISPER_CPP)
struct WhisperRecognizer {
  whisper_context* handle{nullptr};
  ~WhisperRecognizer() {
    if (handle != nullptr) whisper_free(handle);
  }
};
#endif

}  // namespace

struct canto_engine {
  explicit canto_engine(const canto_engine_config& config)
      : config(config), ring(config.ring_capacity_samples),
        scratch(config.chunk_samples) {}

  canto_engine_config config{};
  SampleRing ring;
  std::vector<int16_t> scratch;
  std::mutex mutex;
  std::condition_variable wake;
  std::thread worker;
  std::deque<OwnedResult> results;
  bool stopping{false};
  bool loaded{false};
  bool eos_pending{false};
  std::atomic<bool> abort_requested{false};
  std::atomic<int64_t> last_inference_ms{-1};
  uint64_t samples_processed{0};
  uint64_t generation{0};
  bool test_backend{false};
  bool supports_timestamps{false};
  bool timestamp_mode{true};
  std::string backend{"none"};
#if defined(CANTO_ENABLE_SHERPA_ONNX)
  std::shared_ptr<SherpaRecognizer> sherpa;
#endif
#if defined(CANTO_ENABLE_WHISPER_CPP)
  std::shared_ptr<WhisperRecognizer> whisper;
#endif
};

namespace {

canto_engine_config normalized_config(const canto_engine_config* input) {
  canto_engine_config result{};
  result.struct_size = sizeof(canto_engine_config);
  result.sample_rate_hz = 16000;
  result.ring_capacity_samples = 16000 * 30;
  result.result_queue_capacity = 64;
  result.chunk_samples = 16000;
  if (input != nullptr) {
    if (input->sample_rate_hz != 0) result.sample_rate_hz = input->sample_rate_hz;
    if (input->ring_capacity_samples != 0) result.ring_capacity_samples = input->ring_capacity_samples;
    if (input->result_queue_capacity != 0) result.result_queue_capacity = input->result_queue_capacity;
    if (input->chunk_samples != 0) result.chunk_samples = input->chunk_samples;
  }
  return result;
}

bool config_valid(const canto_engine_config& config) {
  return config.sample_rate_hz >= 8000 && config.sample_rate_hz <= 48000 &&
         config.chunk_samples > 0 && config.ring_capacity_samples >= config.chunk_samples &&
         config.result_queue_capacity > 0;
}

void enqueue_result(canto_engine& engine, OwnedResult result) {
  if (engine.results.size() == engine.config.result_queue_capacity) {
    engine.results.pop_front();
  }
  engine.results.push_back(std::move(result));
}

std::optional<OwnedResult> process_test_chunk(const int16_t* samples, size_t count,
                                               bool final_chunk, int64_t start,
                                               int64_t end) {
  int64_t absolute_sum = 0;
  for (size_t i = 0; i < count; ++i) {
    absolute_sum += std::abs(static_cast<int>(samples[i]));
  }
  const bool speech = count > 0 && (absolute_sum / static_cast<int64_t>(count)) > 200;
  if (!speech && !final_chunk) return std::nullopt;
  return OwnedResult{final_chunk ? CANTO_RESULT_FINAL : CANTO_RESULT_PARTIAL,
                     start, end, speech ? "[測試語音片段]" : ""};
}

#if defined(CANTO_ENABLE_SHERPA_ONNX)
std::vector<OwnedResult> process_sherpa_chunk(
    const std::shared_ptr<SherpaRecognizer>& recognizer, const int16_t* samples,
    size_t count, uint32_t sample_rate, bool final_chunk, int64_t start,
    int64_t end) {
  if (count == 0) {
    if (!final_chunk) return {};
    return {{CANTO_RESULT_FINAL, start, end, ""}};
  }
  std::vector<float> normalized(count);
  std::transform(samples, samples + count, normalized.begin(),
                 [](int16_t sample) { return static_cast<float>(sample) / 32768.0F; });
  const auto* stream = recognizer->runtime->create_stream(recognizer->handle);
  if (stream == nullptr) {
    return {{CANTO_RESULT_ERROR, start, end,
             "sherpa-onnx failed to create an offline stream"}};
  }
  recognizer->runtime->accept_waveform(stream, static_cast<int32_t>(sample_rate),
                                       normalized.data(), static_cast<int32_t>(count));
  recognizer->runtime->decode_stream(recognizer->handle, stream);
  const auto* result = recognizer->runtime->get_result(stream);
  std::vector<OwnedResult> output;
  if (result == nullptr) {
    output.push_back({CANTO_RESULT_ERROR, start, end,
                      "sherpa-onnx returned no recognition result"});
  } else {
    if (result->segment_count > 0 && result->segment_texts_arr != nullptr &&
        result->segment_timestamps != nullptr) {
      for (int32_t i = 0; i < result->segment_count; ++i) {
        const int64_t segment_start = std::clamp<int64_t>(
            start + static_cast<int64_t>(result->segment_timestamps[i] * 1000),
            start, end);
        if (segment_start >= end) continue;
        int64_t segment_end = end;
        if (result->segment_durations != nullptr) {
          segment_end = segment_start + static_cast<int64_t>(
              result->segment_durations[i] * 1000);
        } else if (i + 1 < result->segment_count) {
          segment_end = start + static_cast<int64_t>(
              result->segment_timestamps[i + 1] * 1000);
        }
        segment_end = std::clamp(segment_end, segment_start + 1, end);
        const char* text = result->segment_texts_arr[i];
        output.push_back({CANTO_RESULT_PARTIAL, segment_start, segment_end,
                          text == nullptr ? "" : text});
      }
    } else {
      output.push_back({CANTO_RESULT_PARTIAL, start, end,
                        result->text == nullptr ? "" : result->text});
    }
    recognizer->runtime->destroy_result(result);
  }
  recognizer->runtime->destroy_stream(stream);
  if (final_chunk) {
    if (output.empty()) output.push_back({CANTO_RESULT_FINAL, start, end, ""});
    else output.back().kind = CANTO_RESULT_FINAL;
  }
  return output;
}
#endif

#if defined(CANTO_ENABLE_WHISPER_CPP)
std::vector<OwnedResult> process_whisper_chunk(
    const std::shared_ptr<WhisperRecognizer>& recognizer,
    const int16_t* samples, size_t count, bool final_chunk,
    int64_t start, int64_t end, bool timestamp_mode,
    std::atomic<bool>* abort_requested) {
  if (count == 0) {
    return final_chunk
        ? std::vector<OwnedResult>{{CANTO_RESULT_FINAL, start, end, ""}}
        : std::vector<OwnedResult>{};
  }
  std::vector<float> normalized(count);
  std::transform(samples, samples + count, normalized.begin(),
                 [](int16_t sample) { return static_cast<float>(sample) / 32768.0F; });
  auto params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
  // Leave two logical processors available for WinUI, FFmpeg, cancellation,
  // and the OS. Saturating every processor made lower-core-count PCs appear
  // frozen even though inference itself was already off the UI thread.
  const auto hardware_threads = std::max(1U, std::thread::hardware_concurrency());
  const auto inference_threads = hardware_threads > 2U ? hardware_threads - 2U : 1U;
  params.n_threads = static_cast<int>(
      std::clamp<unsigned int>(inference_threads, 1U, 6U));
  params.language = "zh";
  params.translate = false;
  params.no_context = true;
  params.no_timestamps = !timestamp_mode;
  params.token_timestamps = timestamp_mode;
  params.print_progress = false;
  params.print_realtime = false;
  params.print_timestamps = false;
  params.abort_callback = [](void* data) {
    return static_cast<std::atomic<bool>*>(data)->load(
        std::memory_order_relaxed);
  };
  params.abort_callback_user_data = abort_requested;
  if (whisper_full(recognizer->handle, params, normalized.data(),
                   static_cast<int>(normalized.size())) != 0) {
    return {{CANTO_RESULT_ERROR, start, end,
             "whisper.cpp failed to decode audio"}};
  }
  std::vector<OwnedResult> output;
  const int segment_count = whisper_full_n_segments(recognizer->handle);
  for (int i = 0; i < segment_count; ++i) {
    const int64_t segment_start = std::clamp<int64_t>(
        start + whisper_full_get_segment_t0(recognizer->handle, i) * 10,
        start, end);
    if (segment_start >= end) continue;
    const int64_t segment_end = std::clamp<int64_t>(
        start + whisper_full_get_segment_t1(recognizer->handle, i) * 10,
        std::min(end, segment_start + 1), end);
    const char* text = whisper_full_get_segment_text(recognizer->handle, i);
    output.push_back({CANTO_RESULT_PARTIAL, segment_start, segment_end,
                      text == nullptr ? "" : text});
  }
  if (final_chunk) {
    if (output.empty()) output.push_back({CANTO_RESULT_FINAL, start, end, ""});
    else output.back().kind = CANTO_RESULT_FINAL;
  }
  return output;
}
#endif

void worker_main(canto_engine* engine) {
  std::unique_lock lock(engine->mutex);
  while (!engine->stopping) {
    engine->wake.wait(lock, [&] {
      return engine->stopping || engine->ring.size() >= engine->config.chunk_samples ||
             (engine->eos_pending && engine->ring.size() > 0) ||
             (engine->eos_pending && engine->ring.size() == 0);
    });
    if (engine->stopping) break;

    const bool eos = engine->eos_pending;
    const size_t requested = std::min<size_t>(engine->config.chunk_samples, engine->ring.size());
    const size_t count = engine->ring.pop(engine->scratch.data(), requested);
    const bool final_chunk = eos && engine->ring.size() == 0;
    if (final_chunk) engine->eos_pending = false;
    const int64_t start = static_cast<int64_t>(engine->samples_processed * 1000 /
                                                engine->config.sample_rate_hz);
    engine->samples_processed += count;
    const int64_t end = static_cast<int64_t>(engine->samples_processed * 1000 /
                                              engine->config.sample_rate_hz);
    const uint64_t generation = engine->generation;
    const bool test_backend = engine->test_backend;
    const bool timestamp_mode = engine->timestamp_mode;
#if defined(CANTO_ENABLE_SHERPA_ONNX)
    auto sherpa = engine->sherpa;
#endif
#if defined(CANTO_ENABLE_WHISPER_CPP)
    auto whisper = engine->whisper;
#endif
    lock.unlock();
    std::vector<OwnedResult> results;
    if (count > 0 || final_chunk) {
      const auto inference_started = std::chrono::steady_clock::now();
      if (test_backend) {
        auto result = process_test_chunk(engine->scratch.data(), count,
                                         final_chunk, start, end);
        if (result.has_value()) results.push_back(std::move(*result));
      }
#if defined(CANTO_ENABLE_WHISPER_CPP)
      else if (whisper != nullptr) {
        results = process_whisper_chunk(whisper, engine->scratch.data(), count,
                                        final_chunk, start, end, timestamp_mode,
                                        &engine->abort_requested);
      }
#endif
#if defined(CANTO_ENABLE_SHERPA_ONNX)
      else if (sherpa != nullptr) {
        results = process_sherpa_chunk(sherpa, engine->scratch.data(), count,
                                       engine->config.sample_rate_hz,
                                       final_chunk, start, end);
      }
#endif
      engine->last_inference_ms.store(
          std::chrono::duration_cast<std::chrono::milliseconds>(
              std::chrono::steady_clock::now() - inference_started).count(),
          std::memory_order_relaxed);
    }
    lock.lock();
    if (generation == engine->generation) {
      for (auto& result : results) enqueue_result(*engine, std::move(result));
    }
  }
}

}  // namespace

extern "C" {

canto_status canto_engine_create(const canto_engine_config* config, canto_engine** out_engine) {
  if (out_engine == nullptr) return CANTO_INVALID_ARGUMENT;
  *out_engine = nullptr;
  try {
    const auto normalized = normalized_config(config);
    if (!config_valid(normalized)) return CANTO_INVALID_ARGUMENT;
    auto engine = std::make_unique<canto_engine>(normalized);
    engine->worker = std::thread(worker_main, engine.get());
    *out_engine = engine.release();
    return CANTO_OK;
  } catch (const std::bad_alloc&) {
    return CANTO_OUT_OF_MEMORY;
  } catch (...) {
    return CANTO_INTERNAL_ERROR;
  }
}

void canto_engine_destroy(canto_engine* engine) {
  if (engine == nullptr) return;
  engine->abort_requested.store(true, std::memory_order_relaxed);
  {
    std::lock_guard lock(engine->mutex);
    engine->stopping = true;
  }
  engine->wake.notify_one();
  if (engine->worker.joinable()) engine->worker.join();
  delete engine;
}

canto_status canto_model_load(canto_engine* engine, const char* model_path) {
  if (engine == nullptr || model_path == nullptr) return CANTO_INVALID_ARGUMENT;
  {
    std::lock_guard lock(engine->mutex);
    if (engine->loaded) return CANTO_INVALID_STATE;
    engine->abort_requested.store(false, std::memory_order_relaxed);
  }
#if defined(CANTO_ENABLE_TEST_BACKEND)
  if (std::strcmp(model_path, "test://deterministic") == 0) {
    std::lock_guard lock(engine->mutex);
    engine->loaded = true;
    engine->test_backend = true;
    engine->supports_timestamps = true;
    engine->backend = "deterministic-test";
    ++engine->generation;
    return CANTO_OK;
  }
#endif
#if defined(CANTO_ENABLE_WHISPER_CPP)
  try {
    const auto directory = std::filesystem::path(model_path);
    std::filesystem::path model;
    if (std::filesystem::is_directory(directory)) {
      for (const auto& entry : std::filesystem::directory_iterator(directory)) {
        const auto name = entry.path().filename().string();
        if (entry.is_regular_file() && entry.path().extension() == ".bin" &&
            name.rfind("ggml-", 0) == 0) {
          if (!model.empty()) return CANTO_MODEL_ERROR;
          model = entry.path();
        }
      }
    }
    if (!model.empty()) {
      auto params = whisper_context_default_params();
      params.use_gpu = false;
      auto holder = std::make_shared<WhisperRecognizer>();
      holder->handle = whisper_init_from_file_with_params(model.string().c_str(), params);
      if (holder->handle == nullptr) return CANTO_MODEL_ERROR;
      std::lock_guard lock(engine->mutex);
      if (engine->loaded) return CANTO_INVALID_STATE;
      engine->whisper = std::move(holder);
      engine->loaded = true;
      engine->test_backend = false;
      engine->supports_timestamps = true;
      engine->backend = "whisper.cpp";
      ++engine->generation;
      return CANTO_OK;
    }
  } catch (const std::bad_alloc&) {
    return CANTO_OUT_OF_MEMORY;
  } catch (...) {
    return CANTO_MODEL_ERROR;
  }
#endif
#if defined(CANTO_ENABLE_SHERPA_ONNX)
  try {
    const std::filesystem::path directory(model_path);
    const auto model = directory / "model.int8.onnx";
    const auto tokens = directory / "tokens.txt";
    const auto whisper_encoder = directory / "tiny-encoder.int8.onnx";
    const auto whisper_decoder = directory / "tiny-decoder.int8.onnx";
    const auto whisper_tokens = directory / "tiny-tokens.txt";
    const auto qwen_frontend = directory / "conv_frontend.onnx";
    const auto qwen_encoder = directory / "encoder.int8.onnx";
    const auto qwen_decoder = directory / "decoder.int8.onnx";
    const auto qwen_tokenizer = directory / "tokenizer";
    const bool is_qwen = std::filesystem::is_regular_file(qwen_frontend) &&
                         std::filesystem::is_regular_file(qwen_encoder) &&
                         std::filesystem::is_regular_file(qwen_decoder) &&
                         std::filesystem::is_regular_file(qwen_tokenizer / "vocab.json");
    const bool is_sense_voice = std::filesystem::is_regular_file(model) &&
                                std::filesystem::is_regular_file(tokens);
    const bool is_whisper = std::filesystem::is_regular_file(whisper_encoder) &&
                            std::filesystem::is_regular_file(whisper_decoder) &&
                            std::filesystem::is_regular_file(whisper_tokens);
    if (!is_sense_voice && !is_whisper && !is_qwen) {
      return CANTO_MODEL_ERROR;
    }
    auto runtime = std::make_shared<SherpaRuntime>();
    if (!runtime->load()) return CANTO_BACKEND_UNAVAILABLE;
    SherpaOnnxOfflineRecognizerConfig config{};
    const std::string model_string = model.string();
    const std::string tokens_string = (is_whisper ? whisper_tokens : tokens).string();
    const std::string encoder_string = whisper_encoder.string();
    const std::string decoder_string = whisper_decoder.string();
    const auto qwen_frontend_string = qwen_frontend.string();
    const auto qwen_encoder_string = qwen_encoder.string();
    const auto qwen_decoder_string = qwen_decoder.string();
    const auto qwen_tokenizer_string = qwen_tokenizer.string();
    config.feat_config.sample_rate = static_cast<int32_t>(engine->config.sample_rate_hz);
    config.feat_config.feature_dim = 80;
    config.model_config.tokens = is_qwen ? "" : tokens_string.c_str();
    config.model_config.num_threads = 2;
    config.model_config.provider = "cpu";
    if (is_qwen) {
      auto& qwen = config.model_config.qwen3_asr;
      qwen.conv_frontend = qwen_frontend_string.c_str();
      qwen.encoder = qwen_encoder_string.c_str();
      qwen.decoder = qwen_decoder_string.c_str();
      qwen.tokenizer = qwen_tokenizer_string.c_str();
      qwen.max_total_len = 512;
      qwen.max_new_tokens = 512;
      qwen.temperature = 0.000001f;
      qwen.top_p = 0.8f;
      qwen.seed = 42;
    } else if (is_whisper) {
      config.model_config.whisper.encoder = encoder_string.c_str();
      config.model_config.whisper.decoder = decoder_string.c_str();
      config.model_config.whisper.language = "zh";
      config.model_config.whisper.task = "transcribe";
      config.model_config.whisper.enable_token_timestamps = engine->timestamp_mode ? 1 : 0;
      config.model_config.whisper.enable_segment_timestamps = engine->timestamp_mode ? 1 : 0;
    } else {
      config.model_config.sense_voice.model = model_string.c_str();
      config.model_config.sense_voice.language = "yue";
      config.model_config.sense_voice.use_itn = 1;
    }
    config.decoding_method = "greedy_search";
    const auto* recognizer = runtime->create_recognizer(&config);
    if (recognizer == nullptr) return CANTO_MODEL_ERROR;
    auto holder = std::make_shared<SherpaRecognizer>();
    holder->runtime = std::move(runtime);
    holder->handle = recognizer;
    std::lock_guard lock(engine->mutex);
    if (engine->loaded) return CANTO_INVALID_STATE;
    engine->sherpa = std::move(holder);
    engine->loaded = true;
    engine->test_backend = false;
    engine->supports_timestamps = is_whisper;
    engine->backend = is_qwen ? "sherpa-qwen3-asr" : (is_whisper ? "sherpa-whisper" : "sherpa-sensevoice");
    ++engine->generation;
    return CANTO_OK;
  } catch (const std::bad_alloc&) {
    return CANTO_OUT_OF_MEMORY;
  } catch (...) {
    return CANTO_MODEL_ERROR;
  }
#endif
  return CANTO_BACKEND_UNAVAILABLE;
}

canto_status canto_model_unload(canto_engine* engine) {
  if (engine == nullptr) return CANTO_INVALID_ARGUMENT;
  std::lock_guard lock(engine->mutex);
  engine->loaded = false;
  engine->test_backend = false;
  engine->supports_timestamps = false;
  engine->backend = "none";
#if defined(CANTO_ENABLE_SHERPA_ONNX)
  engine->sherpa.reset();
#endif
#if defined(CANTO_ENABLE_WHISPER_CPP)
  engine->whisper.reset();
#endif
  engine->ring.clear();
  engine->results.clear();
  engine->eos_pending = false;
  engine->abort_requested.store(false, std::memory_order_relaxed);
  engine->samples_processed = 0;
  ++engine->generation;
  return CANTO_OK;
}

canto_status canto_engine_set_timestamp_mode(canto_engine* engine,
                                              uint8_t enabled) {
  if (engine == nullptr) return CANTO_INVALID_ARGUMENT;
  std::lock_guard lock(engine->mutex);
  if (engine->loaded) return CANTO_INVALID_STATE;
  engine->timestamp_mode = enabled != 0;
  return CANTO_OK;
}

canto_status canto_engine_cancel(canto_engine* engine) {
  if (engine == nullptr) return CANTO_INVALID_ARGUMENT;
  engine->abort_requested.store(true, std::memory_order_relaxed);
  {
    std::lock_guard lock(engine->mutex);
    engine->ring.clear();
    engine->results.clear();
    engine->eos_pending = false;
    ++engine->generation;
  }
  engine->wake.notify_one();
  return CANTO_OK;
}

canto_status canto_engine_reset(canto_engine* engine) {
  if (engine == nullptr) return CANTO_INVALID_ARGUMENT;
  std::lock_guard lock(engine->mutex);
  engine->ring.clear();
  engine->results.clear();
  engine->eos_pending = false;
  engine->abort_requested.store(false, std::memory_order_relaxed);
  engine->samples_processed = 0;
  ++engine->generation;
  return CANTO_OK;
}

canto_status canto_push_pcm16(canto_engine* engine, const int16_t* samples,
                              size_t sample_count, uint8_t end_of_stream) {
  if (engine == nullptr || (samples == nullptr && sample_count != 0)) {
    return CANTO_INVALID_ARGUMENT;
  }
  {
    std::lock_guard lock(engine->mutex);
    if (!engine->loaded) return CANTO_INVALID_STATE;
    if (engine->abort_requested.load(std::memory_order_relaxed)) {
      return CANTO_INVALID_STATE;
    }
    if (!engine->ring.push(samples, sample_count)) return CANTO_QUEUE_FULL;
    if (end_of_stream != 0) engine->eos_pending = true;
  }
  engine->wake.notify_one();
  return CANTO_OK;
}

canto_status canto_poll_result(canto_engine* engine, canto_result* out_result) {
  if (engine == nullptr || out_result == nullptr) return CANTO_INVALID_ARGUMENT;
  std::lock_guard lock(engine->mutex);
  if (engine->results.empty()) return CANTO_NO_RESULT;
  OwnedResult owned = std::move(engine->results.front());
  engine->results.pop_front();
  char* text = new (std::nothrow) char[owned.text.size() + 1];
  if (text == nullptr) return CANTO_OUT_OF_MEMORY;
  std::memcpy(text, owned.text.c_str(), owned.text.size() + 1);
  out_result->struct_size = sizeof(canto_result);
  out_result->kind = owned.kind;
  out_result->start_ms = owned.start_ms;
  out_result->end_ms = owned.end_ms;
  out_result->text = text;
  out_result->text_length = owned.text.size();
  return CANTO_OK;
}

void canto_result_free(canto_result* result) {
  if (result == nullptr) return;
  delete[] result->text;
  result->text = nullptr;
  result->text_length = 0;
}

int64_t canto_engine_last_inference_ms(const canto_engine* engine) {
  return engine == nullptr
      ? -1
      : engine->last_inference_ms.load(std::memory_order_relaxed);
}

canto_status canto_engine_get_capabilities(const canto_engine* engine,
                                            canto_capabilities* output) {
  if (engine == nullptr || output == nullptr) return CANTO_INVALID_ARGUMENT;
  std::lock_guard lock(const_cast<canto_engine*>(engine)->mutex);
  std::memset(output, 0, sizeof(*output));
  output->struct_size = sizeof(*output);
  output->abi_version = CANTO_ABI_VERSION;
  output->supports_streaming = engine->test_backend ? 1 : 0;
  output->supports_timestamps = engine->supports_timestamps ? 1 : 0;
  output->supports_hotwords = 0;
  output->supports_vad = engine->test_backend ? 1 : 0;
  output->max_sample_rate_hz = 48000;
  const size_t backend_length = std::min(engine->backend.size(), sizeof(output->backend) - 1);
  std::memcpy(output->backend, engine->backend.data(), backend_length);
  return CANTO_OK;
}

const char* canto_status_message(canto_status status) {
  switch (status) {
    case CANTO_OK: return "ok";
    case CANTO_INVALID_ARGUMENT: return "invalid argument";
    case CANTO_OUT_OF_MEMORY: return "out of memory";
    case CANTO_INVALID_STATE: return "invalid state";
    case CANTO_QUEUE_FULL: return "audio queue full";
    case CANTO_NO_RESULT: return "no result";
    case CANTO_BACKEND_UNAVAILABLE: return "backend unavailable";
    case CANTO_MODEL_ERROR: return "model error";
    case CANTO_INTERNAL_ERROR: return "internal error";
    default: return "unknown status";
  }
}

}  // extern "C"
