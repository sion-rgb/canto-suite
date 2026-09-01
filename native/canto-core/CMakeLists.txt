set(CANTO_CORE_SOURCES
  src/canto_core.cpp
)

add_library(canto_core SHARED ${CANTO_CORE_SOURCES})
add_library(canto::core ALIAS canto_core)
target_compile_features(canto_core PUBLIC cxx_std_20)
target_include_directories(canto_core
  PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
    $<INSTALL_INTERFACE:include>
)
target_compile_definitions(canto_core PRIVATE CANTO_CORE_BUILD)
if(CANTO_ENABLE_TEST_BACKEND)
  target_compile_definitions(canto_core PRIVATE CANTO_ENABLE_TEST_BACKEND=1)
endif()
if(CANTO_ENABLE_SHERPA_ONNX)
  set(CANTO_SHERPA_ROOT "${CMAKE_SOURCE_DIR}/third_party/sherpa-onnx")
  target_include_directories(canto_core PRIVATE "${CANTO_SHERPA_ROOT}/include")
  target_compile_definitions(canto_core PRIVATE
    CANTO_ENABLE_SHERPA_ONNX=1
    CANTO_SHERPA_RUNTIME_DIR="${CANTO_SHERPA_ROOT}/runtime/windows-x64"
  )
  if(ANDROID)
    target_link_libraries(canto_core PRIVATE dl)
  endif()
endif()
if(WIN32 AND TARGET whisper)
  target_compile_definitions(canto_core PRIVATE CANTO_ENABLE_WHISPER_CPP=1)
  target_link_libraries(canto_core PRIVATE whisper)
endif()

if(MSVC)
  target_compile_options(canto_core PRIVATE /W4 /permissive- /EHsc /utf-8)
else()
  target_compile_options(canto_core PRIVATE -Wall -Wextra -Wpedantic)
endif()

set_target_properties(canto_core PROPERTIES
  OUTPUT_NAME "canto_core"
  WINDOWS_EXPORT_ALL_SYMBOLS OFF
)

if(CANTO_BUILD_TESTS)
  add_executable(canto_core_tests tests/canto_core_tests.cpp)
  if(MSVC)
    target_compile_options(canto_core_tests PRIVATE /utf-8)
  endif()
  target_compile_features(canto_core_tests PRIVATE cxx_std_20)
  target_link_libraries(canto_core_tests PRIVATE canto::core)
  add_test(NAME canto_core_tests COMMAND canto_core_tests)
endif()
