#include "llama.h"

#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <vector>

#if defined(__GNUC__)
#define CANTO_LLM_API __attribute__((visibility("default")))
#else
#define CANTO_LLM_API
#endif

struct canto_llm {
  llama_model* model{};
  int32_t threads{2};
  std::mutex mutex;
};

namespace {
std::once_flag backend_once;

// Constrain every generation to the persisted MeetingReport wire format.  The
// model still supplies all values, while the sampler guarantees parseable JSON
// and prevents small models from inventing alternate nesting or field names.
constexpr const char* meeting_report_grammar = R"GBNF(
root ::= "{" ws "\"summary\"" ws ":" ws string "," ws "\"topics\"" ws ":" ws strings "," ws "\"decisions\"" ws ":" ws strings "," ws "\"actionItems\"" ws ":" ws actions "," ws "\"followUps\"" ws ":" ws strings "," ws "\"unresolvedQuestions\"" ws ":" ws strings "," ws "\"risks\"" ws ":" ws strings "}" ws
strings ::= "[" ws (string ("," ws string)*)? "]" ws
actions ::= "[" ws (action ("," ws action)*)? "]" ws
action ::= "{" ws "\"text\"" ws ":" ws string "," ws "\"owner\"" ws ":" ws nullable "," ws "\"dueDate\"" ws ":" ws nullable "}" ws
nullable ::= string | "null" ws
string ::= "\"" ([^"\\\x7F\x00-\x1F] | "\\" (["\\bfnrt] | "u" [0-9a-fA-F]{4}))* "\"" ws
ws ::= | " " | "\n" [ \t]{0,20}
)GBNF";

char* copy_string(const std::string& value) {
  auto* result = static_cast<char*>(std::malloc(value.size() + 1));
  if (result == nullptr) return nullptr;
  std::memcpy(result, value.data(), value.size());
  result[value.size()] = '\0';
  return result;
}

std::string token_piece(const llama_vocab* vocab, llama_token token) {
  std::vector<char> buffer(256);
  auto length = llama_token_to_piece(vocab, token, buffer.data(),
                                     static_cast<int32_t>(buffer.size()), 0, true);
  if (length < 0) {
    buffer.resize(static_cast<size_t>(-length));
    length = llama_token_to_piece(vocab, token, buffer.data(),
                                  static_cast<int32_t>(buffer.size()), 0, true);
  }
  return length > 0 ? std::string(buffer.data(), static_cast<size_t>(length))
                    : std::string();
}
}  // namespace

extern "C" {

CANTO_LLM_API int32_t canto_llm_create(const char* model_path, int32_t threads,
                                        canto_llm** output) {
  if (model_path == nullptr || output == nullptr) return 1;
  *output = nullptr;
  try {
    std::call_once(backend_once, [] { ggml_backend_load_all(); });
    llama_model_params params = llama_model_default_params();
    params.n_gpu_layers = 0;
    auto model = llama_model_load_from_file(model_path, params);
    if (model == nullptr) return 2;
    auto handle = std::make_unique<canto_llm>();
    handle->model = model;
    handle->threads = std::clamp<int32_t>(threads, 1, 8);
    *output = handle.release();
    return 0;
  } catch (...) {
    return 3;
  }
}

CANTO_LLM_API int32_t canto_llm_generate(canto_llm* handle, const char* prompt,
                                          int32_t max_tokens, char** output) {
  if (handle == nullptr || prompt == nullptr || output == nullptr) return 1;
  *output = nullptr;
  std::lock_guard lock(handle->mutex);
  try {
    const auto* vocab = llama_model_get_vocab(handle->model);
    const size_t prompt_bytes = std::strlen(prompt);
    int32_t count = llama_tokenize(vocab, prompt, prompt_bytes, nullptr, 0, true, true);
    if (count >= 0) return 4;
    count = -count;
    std::vector<llama_token> tokens(static_cast<size_t>(count));
    const int32_t written = llama_tokenize(vocab, prompt, prompt_bytes, tokens.data(),
                                           static_cast<int32_t>(tokens.size()), true, true);
    if (written < 0) return 4;
    tokens.resize(static_cast<size_t>(written));

    const int32_t prediction_limit = std::clamp<int32_t>(max_tokens, 32, 2048);
    const uint32_t context_size = std::max<uint32_t>(4096,
        static_cast<uint32_t>(tokens.size() + prediction_limit + 32));
    if (context_size > 8192) return 5;
    llama_context_params context_params = llama_context_default_params();
    context_params.n_ctx = context_size;
    context_params.n_batch = std::min<uint32_t>(context_size, 1024);
    context_params.n_threads = handle->threads;
    context_params.n_threads_batch = handle->threads;
    context_params.no_perf = true;
    auto* context = llama_init_from_model(handle->model, context_params);
    if (context == nullptr) return 6;

    auto sampler_params = llama_sampler_chain_default_params();
    sampler_params.no_perf = true;
    auto* sampler = llama_sampler_chain_init(sampler_params);
    auto* grammar = llama_sampler_init_grammar(
        vocab, meeting_report_grammar, "root");
    if (grammar == nullptr) {
      llama_sampler_free(sampler);
      llama_free(context);
      return 8;
    }
    llama_sampler_chain_add(sampler, grammar);
    llama_sampler_chain_add(sampler, llama_sampler_init_greedy());

    std::string generated;
    llama_batch batch = llama_batch_get_one(tokens.data(), static_cast<int32_t>(tokens.size()));
    for (int32_t produced = 0; produced < prediction_limit; ++produced) {
      if (llama_decode(context, batch) != 0) {
        llama_sampler_free(sampler);
        llama_free(context);
        return 7;
      }
      llama_token token = llama_sampler_sample(sampler, context, -1);
      if (llama_vocab_is_eog(vocab, token)) break;
      generated += token_piece(vocab, token);
      batch = llama_batch_get_one(&token, 1);
    }
    llama_sampler_free(sampler);
    llama_free(context);
    *output = copy_string(generated);
    return *output == nullptr ? 3 : 0;
  } catch (...) {
    return 3;
  }
}

CANTO_LLM_API void canto_llm_free_text(char* text) { std::free(text); }

CANTO_LLM_API void canto_llm_destroy(canto_llm* handle) {
  if (handle == nullptr) return;
  llama_model_free(handle->model);
  delete handle;
}

}  // extern "C"
