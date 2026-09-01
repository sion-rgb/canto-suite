use std::ffi::OsStr;
use std::io::{self, Read};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdout, Command, Stdio};

pub const SUPPORTED_EXTENSIONS: &[&str] = &["mp4", "mov", "mkv", "mp3", "m4a", "aac", "wav"];

pub fn is_supported(path: &Path) -> bool {
    path.extension()
        .and_then(OsStr::to_str)
        .map(|extension| SUPPORTED_EXTENSIONS.contains(&extension.to_ascii_lowercase().as_str()))
        .unwrap_or(false)
}

pub struct PcmDecoder {
    child: Child,
    stdout: ChildStdout,
    pending_byte: Option<u8>,
}

impl PcmDecoder {
    pub fn start(ffmpeg: &Path, source: &Path, seek_ms: i64) -> io::Result<Self> {
        let mut command = Command::new(ffmpeg);
        command.stdin(Stdio::null()).stderr(Stdio::piped()).stdout(Stdio::piped());
        if seek_ms > 0 {
            command.args(["-ss", &format!("{:.3}", seek_ms as f64 / 1000.0)]);
        }
        command
            .arg("-i").arg(source)
            .args(["-vn", "-ac", "1", "-ar", "16000", "-f", "s16le", "-c:a", "pcm_s16le", "pipe:1"]);
        let mut child = command.spawn()?;
        let stdout = child.stdout.take().ok_or_else(|| io::Error::other("FFmpeg stdout unavailable"))?;
        Ok(Self { child, stdout, pending_byte: None })
    }

    pub fn read_samples(&mut self, output: &mut [i16]) -> io::Result<usize> {
        let mut bytes = vec![0_u8; output.len() * 2];
        let mut offset = 0;
        if let Some(byte) = self.pending_byte.take() {
            bytes[0] = byte;
            offset = 1;
        }
        let read = self.stdout.read(&mut bytes[offset..])? + offset;
        let complete = read / 2;
        for index in 0..complete {
            output[index] = i16::from_le_bytes([bytes[index * 2], bytes[index * 2 + 1]]);
        }
        if read % 2 != 0 {
            self.pending_byte = Some(bytes[read - 1]);
        }
        Ok(complete)
    }

    pub fn cancel(&mut self) -> io::Result<()> { self.child.kill() }
}

impl Drop for PcmDecoder {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

pub fn find_packaged_ffmpeg(resource_dir: &Path) -> Option<PathBuf> {
    let executable = if cfg!(windows) { "ffmpeg.exe" } else { "ffmpeg" };
    let packaged = resource_dir.join("bin").join(executable);
    packaged.exists().then_some(packaged)
}
