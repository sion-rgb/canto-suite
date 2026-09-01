use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::VecDeque;
use std::fs;
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum OutputFormat { Txt, Srt }

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Quality { Fast, Balanced, HighAccuracy }

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum JobState { Queued, Processing, Paused, Completed, Failed, Cancelled }

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CompletedSegment {
    pub start_ms: i64,
    pub end_ms: i64,
    pub text: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Job {
    pub id: String,
    pub source: PathBuf,
    pub source_fingerprint: String,
    pub output: PathBuf,
    pub format: OutputFormat,
    pub quality: Quality,
    pub model_id: String,
    pub model_version: String,
    pub state: JobState,
    pub processed_ms: i64,
    pub total_ms: Option<i64>,
    pub completed_results: Vec<String>,
    #[serde(default)]
    pub completed_segments: Vec<CompletedSegment>,
    pub error: Option<String>,
}

#[derive(Debug, Serialize, Deserialize, Default)]
struct PersistedQueue { version: u32, jobs: Vec<Job> }

pub struct JobManager {
    state_path: PathBuf,
    jobs: VecDeque<Job>,
}

fn atomic_write(path: &Path, contents: &[u8]) -> io::Result<()> {
    if let Some(parent) = path.parent() { fs::create_dir_all(parent)?; }
    let temporary = path.with_extension("tmp");
    {
        let mut file = fs::File::create(&temporary)?;
        file.write_all(contents)?;
        file.sync_all()?;
    }
    if path.exists() { fs::remove_file(path)?; }
    fs::rename(temporary, path)
}

pub fn source_fingerprint(path: &Path) -> io::Result<String> {
    let metadata = fs::metadata(path)?;
    let mut file = fs::File::open(path)?;
    let mut first_megabyte = vec![0_u8; 1024 * 1024];
    let read = file.read(&mut first_megabyte)?;
    let mut digest = Sha256::new();
    digest.update(metadata.len().to_le_bytes());
    digest.update(&first_megabyte[..read]);
    Ok(format!("{:x}", digest.finalize()))
}

impl JobManager {
    pub fn open(state_path: impl Into<PathBuf>) -> io::Result<Self> {
        let state_path = state_path.into();
        let jobs = if state_path.exists() {
            let persisted: PersistedQueue = serde_json::from_slice(&fs::read(&state_path)?)
                .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
            persisted.jobs.into()
        } else { VecDeque::new() };
        Ok(Self { state_path, jobs })
    }

    pub fn jobs(&self) -> impl Iterator<Item = &Job> { self.jobs.iter() }

    pub fn queued_jobs(&self) -> Vec<Job> {
        self.jobs.iter().filter(|job| matches!(job.state, JobState::Queued | JobState::Paused)).cloned().collect()
    }

    pub fn enqueue(
        &mut self,
        source: PathBuf,
        output: PathBuf,
        format: OutputFormat,
        quality: Quality,
        model_id: String,
        model_version: String,
    ) -> io::Result<String> {
        let fingerprint = source_fingerprint(&source)?;
        let timestamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_millis();
        let id = format!("{timestamp}-{}", &fingerprint[..12]);
        self.jobs.push_back(Job {
            id: id.clone(), source, source_fingerprint: fingerprint, output,
            format, quality, model_id, model_version, state: JobState::Queued,
            processed_ms: 0, total_ms: None, completed_results: Vec::new(),
            completed_segments: Vec::new(), error: None,
        });
        self.persist()?;
        Ok(id)
    }

    pub fn update_progress(&mut self, id: &str, processed_ms: i64, result: Option<String>) -> io::Result<()> {
        let job = self.jobs.iter_mut().find(|job| job.id == id)
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "job not found"))?;
        job.state = JobState::Processing;
        job.processed_ms = processed_ms.max(job.processed_ms);
        if let Some(result) = result { job.completed_results.push(result); }
        self.persist()
    }

    pub fn update_segment(&mut self, id: &str, segment: CompletedSegment) -> io::Result<()> {
        let job = self.jobs.iter_mut().find(|job| job.id == id)
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "job not found"))?;
        job.state = JobState::Processing;
        job.processed_ms = segment.end_ms.max(job.processed_ms);
        job.completed_results.push(segment.text.clone());
        job.completed_segments.push(segment);
        self.persist()
    }

    pub fn set_state(&mut self, id: &str, state: JobState, error: Option<String>) -> io::Result<()> {
        let job = self.jobs.iter_mut().find(|job| job.id == id)
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "job not found"))?;
        job.state = state;
        job.error = error;
        self.persist()
    }

    pub fn persist(&self) -> io::Result<()> {
        let persisted = PersistedQueue { version: 1, jobs: self.jobs.iter().cloned().collect() };
        let data = serde_json::to_vec_pretty(&persisted).map_err(io::Error::other)?;
        atomic_write(&self.state_path, &data)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn persists_resume_position_and_completed_results() {
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("meeting.wav");
        fs::write(&source, b"RIFF test audio fingerprint").unwrap();
        let state = directory.path().join("jobs.json");
        let mut manager = JobManager::open(&state).unwrap();
        let id = manager.enqueue(
            source, directory.path().join("meeting.txt"), OutputFormat::Txt,
            Quality::Balanced, "asr.test".into(), "1".into(),
        ).unwrap();
        manager.update_progress(&id, 42_000, Some("我哋開始開會".into())).unwrap();
        drop(manager);
        let restored = JobManager::open(&state).unwrap();
        let job = restored.jobs().next().unwrap();
        assert_eq!(job.processed_ms, 42_000);
        assert_eq!(job.completed_results, ["我哋開始開會"]);
    }
}
