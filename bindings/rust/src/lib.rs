//! Safe ownership wrapper for the Canto native C ABI.

use std::ffi::{CStr, CString, c_char, c_void};
use std::fmt;
use std::ptr::NonNull;

const CANTO_OK: i32 = 0;
const CANTO_NO_RESULT: i32 = 5;

#[repr(C)]
struct EngineConfig {
    struct_size: u32,
    sample_rate_hz: u32,
    ring_capacity_samples: u32,
    result_queue_capacity: u32,
    chunk_samples: u32,
}

#[repr(C)]
struct NativeResult {
    struct_size: u32,
    kind: i32,
    start_ms: i64,
    end_ms: i64,
    text: *mut c_char,
    text_length: usize,
}

unsafe extern "C" {
    fn canto_engine_create(config: *const EngineConfig, output: *mut *mut c_void) -> i32;
    fn canto_engine_destroy(engine: *mut c_void);
    fn canto_model_load(engine: *mut c_void, model_path: *const c_char) -> i32;
    fn canto_model_unload(engine: *mut c_void) -> i32;
    fn canto_engine_reset(engine: *mut c_void) -> i32;
    fn canto_push_pcm16(
        engine: *mut c_void,
        samples: *const i16,
        sample_count: usize,
        end_of_stream: u8,
    ) -> i32;
    fn canto_poll_result(engine: *mut c_void, output: *mut NativeResult) -> i32;
    fn canto_result_free(result: *mut NativeResult);
    fn canto_status_message(status: i32) -> *const c_char;
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ResultKind {
    Partial,
    Final,
    Error,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TranscriptResult {
    pub kind: ResultKind,
    pub start_ms: i64,
    pub end_ms: i64,
    pub text: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CoreError {
    pub code: i32,
    pub message: String,
}

impl fmt::Display for CoreError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{} ({})", self.message, self.code)
    }
}

impl std::error::Error for CoreError {}

fn status_error(code: i32) -> CoreError {
    let message = unsafe {
        let pointer = canto_status_message(code);
        if pointer.is_null() {
            "unknown native error".to_owned()
        } else {
            CStr::from_ptr(pointer).to_string_lossy().into_owned()
        }
    };
    CoreError { code, message }
}

fn check(code: i32) -> Result<(), CoreError> {
    if code == CANTO_OK { Ok(()) } else { Err(status_error(code)) }
}

pub struct CoreEngine {
    handle: NonNull<c_void>,
}

// Ownership may move to a persistent Rust worker. Individual engines are not Sync.
unsafe impl Send for CoreEngine {}

impl CoreEngine {
    pub fn new(sample_rate_hz: u32, buffer_seconds: u32) -> Result<Self, CoreError> {
        let config = EngineConfig {
            struct_size: std::mem::size_of::<EngineConfig>() as u32,
            sample_rate_hz,
            ring_capacity_samples: sample_rate_hz.saturating_mul(buffer_seconds),
            result_queue_capacity: 128,
            chunk_samples: sample_rate_hz.saturating_mul(buffer_seconds.min(10)),
        };
        let mut pointer = std::ptr::null_mut();
        check(unsafe { canto_engine_create(&config, &mut pointer) })?;
        let handle = NonNull::new(pointer).ok_or_else(|| CoreError {
            code: 8,
            message: "native engine returned a null handle".into(),
        })?;
        Ok(Self { handle })
    }

    pub fn load_model(&mut self, path: &str) -> Result<(), CoreError> {
        let path = CString::new(path).map_err(|_| CoreError {
            code: 1,
            message: "model path contains a NUL byte".into(),
        })?;
        check(unsafe { canto_model_load(self.handle.as_ptr(), path.as_ptr()) })
    }

    pub fn unload_model(&mut self) -> Result<(), CoreError> {
        check(unsafe { canto_model_unload(self.handle.as_ptr()) })
    }

    pub fn reset(&mut self) -> Result<(), CoreError> {
        check(unsafe { canto_engine_reset(self.handle.as_ptr()) })
    }

    pub fn push_pcm16(&mut self, samples: &[i16], end_of_stream: bool) -> Result<(), CoreError> {
        check(unsafe {
            canto_push_pcm16(
                self.handle.as_ptr(),
                samples.as_ptr(),
                samples.len(),
                u8::from(end_of_stream),
            )
        })
    }

    pub fn poll(&mut self) -> Result<Option<TranscriptResult>, CoreError> {
        let mut native = NativeResult {
            struct_size: std::mem::size_of::<NativeResult>() as u32,
            kind: 0,
            start_ms: 0,
            end_ms: 0,
            text: std::ptr::null_mut(),
            text_length: 0,
        };
        let status = unsafe { canto_poll_result(self.handle.as_ptr(), &mut native) };
        if status == CANTO_NO_RESULT {
            return Ok(None);
        }
        check(status)?;
        let text = if native.text.is_null() {
            String::new()
        } else {
            unsafe { CStr::from_ptr(native.text) }.to_string_lossy().into_owned()
        };
        let result = TranscriptResult {
            kind: match native.kind {
                1 => ResultKind::Partial,
                2 => ResultKind::Final,
                _ => ResultKind::Error,
            },
            start_ms: native.start_ms,
            end_ms: native.end_ms,
            text,
        };
        unsafe { canto_result_free(&mut native) };
        Ok(Some(result))
    }
}

impl Drop for CoreEngine {
    fn drop(&mut self) {
        unsafe { canto_engine_destroy(self.handle.as_ptr()) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{Duration, Instant};

    #[test]
    fn transfers_results_and_native_ownership_safely() {
        let mut engine = CoreEngine::new(16_000, 2).unwrap();
        engine.load_model("test://deterministic").unwrap();
        engine.push_pcm16(&vec![2_000; 16_000], true).unwrap();
        let deadline = Instant::now() + Duration::from_secs(2);
        loop {
            if let Some(result) = engine.poll().unwrap() {
                assert!(!result.text.is_empty());
                break;
            }
            assert!(Instant::now() < deadline, "native worker produced no result");
            std::thread::sleep(Duration::from_millis(5));
        }
    }
}
