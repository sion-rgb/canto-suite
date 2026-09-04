#include "canto_core.h"

#include <algorithm>
#include <cassert>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iostream>
#include <string>
#include <thread>
#include <vector>

namespace {

[[noreturn]] void fail_check(const char* expression, const char* file, int line) {
  std::cerr << file << ':' << line << ": check failed: " << expression << '\n';
  std::abort();
}

#undef assert
#define assert(expression) \
  ((expression) ? static_cast<void>(0) : fail_check(#expression, __FILE__, __LINE__))

template <typename Value>
Value read_value(std::ifstream& input) {
  Value value{};
  input.read(reinterpret_cast<char*>(&value), sizeof(value));
  return value;
}

struct WaveData {
  uint32_t sample_rate{};
  std::vector<int16_t> samples;
};

WaveData read_pcm16_wave(const char* path) {
  std::ifstream input(path, std::ios::binary);
  assert(input);
  char riff[4]{};
  input.read(riff, sizeof(riff));
  assert(std::memcmp(riff, "RIFF", 4) == 0);
  (void)read_value<uint32_t>(input);
  char wave[4]{};
  input.read(wave, sizeof(wave));
  assert(std::memcmp(wave, "WAVE", 4) == 0);

  uint16_t format = 0;
  uint16_t channels = 0;
  uint16_t bits = 0;
  WaveData output;
  while (input && output.samples.empty()) {
    char id[4]{};
    input.read(id, sizeof(id));
    if (!input) break;
    const uint32_t size = read_value<uint32_t>(input);
    if (std::memcmp(id, "fmt ", 4) == 0) {
      format = read_value<uint16_t>(input);
      channels = read_value<uint16_t>(input);
      output.sample_rate = read_value<uint32_t>(input);
      input.seekg(6, std::ios::cur);
      bits = read_value<uint16_t>(input);
      if (size > 16) input.seekg(size - 16, std::ios::cur);
    } else if (std::memcmp(id, "data", 4) == 0) {
      assert(format == 1 && channels == 1 && bits == 16);
      output.samples.resize(size / sizeof(int16_t));
      input.read(reinterpret_cast<char*>(output.samples.data()), size);
    } else {
      input.seekg(size + (size & 1U), std::ios::cur);
    }
  }
  assert(output.sample_rate > 0 && !output.samples.empty());
  return output;
}

void test_real_model_if_configured() {
  const char* model = std::getenv("CANTO_TEST_MODEL_DIR");
  const char* wave_path = std::getenv("CANTO_TEST_WAV");
  const char* expected_backend = std::getenv("CANTO_TEST_EXPECTED_BACKEND");
  const char* timestamp_mode = std::getenv("CANTO_TEST_TIMESTAMP_MODE");
  const char* cancel_after_ms = std::getenv("CANTO_TEST_CANCEL_AFTER_MS");
  if (model == nullptr || wave_path == nullptr) return;
  const auto wave = read_pcm16_wave(wave_path);
  canto_engine_config config{};
  config.struct_size = sizeof(config);
  config.sample_rate_hz = wave.sample_rate;
  const bool cancellation_test = cancel_after_ms != nullptr;
  config.ring_capacity_samples = cancellation_test
      ? wave.sample_rate * 30
      : static_cast<uint32_t>(wave.samples.size());
  config.result_queue_capacity = 8;
  config.chunk_samples = cancellation_test
      ? wave.sample_rate * 10
      : static_cast<uint32_t>(wave.samples.size());
  canto_engine* engine = nullptr;
  std::cerr << "real model: create engine\n";
  assert(canto_engine_create(&config, &engine) == CANTO_OK);
  if (timestamp_mode != nullptr) {
    assert(canto_engine_set_timestamp_mode(
               engine, std::strcmp(timestamp_mode, "0") == 0 ? 0 : 1) == CANTO_OK);
  }
  std::cerr << "real model: load model\n";
  assert(canto_model_load(engine, model) == CANTO_OK);
  std::cerr << "real model: inspect capabilities\n";
  canto_capabilities capabilities{};
  assert(canto_engine_get_capabilities(engine, &capabilities) == CANTO_OK);
  assert(std::strcmp(capabilities.backend,
                     expected_backend == nullptr ? "sherpa-sensevoice"
                                                 : expected_backend) == 0);
  if (expected_backend != nullptr &&
      (std::strcmp(expected_backend, "sherpa-whisper") == 0 ||
       std::strcmp(expected_backend, "whisper.cpp") == 0)) {
    assert(capabilities.supports_timestamps == 1);
  }
  assert(capabilities.supports_streaming == 0);
  const size_t pushed_samples = cancellation_test
      ? std::min<size_t>(wave.samples.size(), config.ring_capacity_samples)
      : wave.samples.size();
  std::cerr << "real model: push " << pushed_samples << " samples\n";
  assert(canto_push_pcm16(engine, wave.samples.data(), pushed_samples, 1) == CANTO_OK);
  if (cancel_after_ms != nullptr) {
    std::this_thread::sleep_for(
        std::chrono::milliseconds(std::max(1, std::atoi(cancel_after_ms))));
    const auto cancel_started = std::chrono::steady_clock::now();
    assert(canto_engine_cancel(engine) == CANTO_OK);
    const auto cancel_call_duration =
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - cancel_started);
    assert(cancel_call_duration < std::chrono::milliseconds(500));
    canto_engine_destroy(engine);
    const auto cleanup_duration = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - cancel_started);
    std::cerr << "real model: cancel call ms=" << cancel_call_duration.count()
              << " cleanup ms=" << cleanup_duration.count() << '\n';
    assert(cleanup_duration < std::chrono::seconds(20));
    return;
  }
  canto_result result{};
  canto_status status = CANTO_NO_RESULT;
  for (int i = 0; i < 600 && status == CANTO_NO_RESULT; ++i) {
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    status = canto_poll_result(engine, &result);
  }
  std::cerr << "real model: poll status=" << static_cast<int>(status)
            << " kind=" << static_cast<int>(result.kind)
            << " engine=" << static_cast<void*>(engine) << '\n';
  assert(status == CANTO_OK);
  assert(result.kind == CANTO_RESULT_FINAL);
  std::cerr << "real model: received result\n";
  assert(result.text != nullptr && result.text_length > 0);
  std::cerr << "real model: result ptr=" << static_cast<void*>(result.text)
            << " length=" << result.text_length << '\n';
  std::cerr << capabilities.backend << " Cantonese result: " << result.text << '\n';
  std::cerr << "real model: free result\n";
  canto_result_free(&result);
  std::cerr << "real model: destroy engine\n";
  canto_engine_destroy(engine);
  std::cerr << "real model: done\n";
}

}  // namespace

int main() {
  canto_engine_config config{};
  config.struct_size = sizeof(config);
  config.sample_rate_hz = 16000;
  config.ring_capacity_samples = 32000;
  config.result_queue_capacity = 8;
  config.chunk_samples = 1600;

  canto_engine* engine = nullptr;
  assert(canto_engine_create(&config, &engine) == CANTO_OK);
  assert(engine != nullptr);
  assert(canto_engine_set_timestamp_mode(engine, 0) == CANTO_OK);
  assert(canto_model_load(engine, "test://deterministic") == CANTO_OK);
  assert(canto_engine_set_timestamp_mode(engine, 1) == CANTO_INVALID_STATE);

  canto_capabilities capabilities{};
  assert(canto_engine_get_capabilities(engine, &capabilities) == CANTO_OK);
  assert(capabilities.abi_version == CANTO_ABI_VERSION);
  assert(std::strcmp(capabilities.backend, "deterministic-test") == 0);

  std::vector<int16_t> speech(1600, 1200);
  assert(canto_push_pcm16(engine, speech.data(), speech.size(), 0) == CANTO_OK);
  assert(canto_push_pcm16(engine, nullptr, 0, 1) == CANTO_OK);

  canto_result result{};
  canto_status status = CANTO_NO_RESULT;
  for (int i = 0; i < 100 && status == CANTO_NO_RESULT; ++i) {
    std::this_thread::sleep_for(std::chrono::milliseconds(5));
    status = canto_poll_result(engine, &result);
  }
  assert(status == CANTO_OK);
  assert(result.text != nullptr);
  assert(result.text_length > 0);
  assert(canto_engine_last_inference_ms(engine) >= 0);
  canto_result_free(&result);

  assert(canto_engine_cancel(engine) == CANTO_OK);
  assert(canto_push_pcm16(engine, speech.data(), speech.size(), 0) == CANTO_INVALID_STATE);
  assert(canto_engine_reset(engine) == CANTO_OK);
  assert(canto_push_pcm16(engine, speech.data(), speech.size(), 0) == CANTO_OK);
  assert(canto_engine_reset(engine) == CANTO_OK);
  assert(canto_model_unload(engine) == CANTO_OK);
  assert(canto_push_pcm16(engine, speech.data(), speech.size(), 0) == CANTO_INVALID_STATE);
  canto_engine_destroy(engine);
  test_real_model_if_configured();
  return 0;
}
