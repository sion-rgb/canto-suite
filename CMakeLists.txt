cmake_minimum_required(VERSION 3.22)
project(canto_suite VERSION 0.1.0 LANGUAGES CXX)

option(CANTO_BUILD_TESTS "Build native tests" ON)
if(CANTO_BUILD_TESTS)
  enable_testing()
endif()

option(CANTO_ENABLE_TEST_BACKEND "Enable the deterministic test ASR backend" ON)
option(CANTO_ENABLE_SHERPA_ONNX "Enable the production sherpa-onnx SenseVoice adapter" ON)

if(WIN32 AND EXISTS "${CMAKE_SOURCE_DIR}/third_party/whisper.cpp/CMakeLists.txt")
  set(BUILD_SHARED_LIBS OFF CACHE BOOL "Build static third-party libraries" FORCE)
  set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
  set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
  set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
  set(WHISPER_BUILD_IS_DEV OFF CACHE BOOL "" FORCE)
  set(WHISPER_BUILD_COMMIT "371b5a7561823ab2bb32142d2751e35e7534727b" CACHE STRING "Pinned vendored whisper.cpp commit" FORCE)
  set(GGML_BUILD_COMMIT "whisper.cpp-v1.9.3" CACHE STRING "Pinned vendored ggml revision" FORCE)
  set(GGML_OPENMP OFF CACHE BOOL "Avoid external OpenMP runtime dependency" FORCE)
  add_subdirectory(third_party/whisper.cpp EXCLUDE_FROM_ALL)
endif()

add_subdirectory(native/canto-core)

if(ANDROID AND EXISTS "${CMAKE_SOURCE_DIR}/third_party/llama.cpp/include/llama.h")
  add_subdirectory(native/canto-llm)
endif()
