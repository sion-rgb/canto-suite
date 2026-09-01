# Memory ownership

| Object | Owner | Transfer/release |
|---|---|---|
| PCM passed to C | Dart/Rust during call | Native makes one bounded copy before returning; pointer is never retained |
| Native ring/scratch | `canto_engine` | Preallocated at create; freed by `canto_engine_destroy` |
| Result text | Native then caller | Ownership transfers on successful poll; caller invokes `canto_result_free` exactly once |
| Engine handle | FFI wrapper | Rust `Drop` / Dart `dispose` invokes destroy; no global singleton |
| FFmpeg PCM chunk | Rust decoder | Reused bounded buffer; not persisted as full-media PCM |
| Meeting AAC segment | Filesystem | `.part` becomes committed file via same-volume atomic rename |

The native worker is joined before the engine is deleted. Queue operations are mutex-protected and bounded. No exception is allowed through `extern "C"`; allocation and unexpected failures become status codes.
