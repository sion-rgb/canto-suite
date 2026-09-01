if(NOT ANDROID_ABI STREQUAL "arm64-v8a")
  add_library(canto_llm SHARED src/canto_llm_stub.cpp)
  return()
endif()

add_library(canto_llm SHARED src/canto_llm.cpp)

target_compile_features(canto_llm PRIVATE cxx_std_20)
target_include_directories(canto_llm PRIVATE
  "${CMAKE_SOURCE_DIR}/third_party/llama.cpp/include")

set(LLAMA_ANDROID_RUNTIME
  "${CMAKE_SOURCE_DIR}/third_party/llama.cpp/runtime/android-arm64-v8a")
add_library(llama_android SHARED IMPORTED GLOBAL)
set_target_properties(llama_android PROPERTIES
  IMPORTED_LOCATION "${LLAMA_ANDROID_RUNTIME}/libllama.so")
add_library(ggml_android SHARED IMPORTED GLOBAL)
set_target_properties(ggml_android PROPERTIES
  IMPORTED_LOCATION "${LLAMA_ANDROID_RUNTIME}/libggml.so")
add_library(ggml_base_android SHARED IMPORTED GLOBAL)
set_target_properties(ggml_base_android PROPERTIES
  IMPORTED_LOCATION "${LLAMA_ANDROID_RUNTIME}/libggml-base.so")

target_link_libraries(canto_llm PRIVATE llama_android ggml_android ggml_base_android log)
set_target_properties(canto_llm PROPERTIES
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN YES)
