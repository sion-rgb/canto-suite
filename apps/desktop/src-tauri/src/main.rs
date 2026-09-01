#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use canto_core::{CoreEngine, ResultKind};
use canto_transcribe::job::source_fingerprint;
use canto_transcribe::job::CompletedSegment;
use canto_transcribe::media::{PcmDecoder, find_packaged_ffmpeg, is_supported};
use canto_transcribe::model::{self, ModelStatus};
use canto_transcribe::srt::{SegmentationOptions, TimedText, render, segment};
use canto_transcribe::{Job, JobManager, JobState, OutputFormat, Quality};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};
use tauri::{Emitter, Manager};

struct AppState {
    jobs: Arc<Mutex<JobManager>>,
    resource_dir: PathBuf,
    model_root: PathBuf,
}

#[tauri::command]
fn model_status(state: tauri::State<'_, AppState>) -> Result<ModelStatus, String> {
    model::status(&state.model_root)
}

#[tauri::command]
async fn install_model(
    app: tauri::AppHandle,
    state: tauri::State<'_, AppState>,
) -> Result<ModelStatus, String> {
    let root = state.model_root.clone();
    tauri::async_runtime::spawn_blocking(move || {
        model::install(&root, |progress| {
            let _ = app.emit("model-progress", progress);
        })
    })
    .await
    .map_err(|error| error.to_string())?
}

#[tauri::command]
fn list_jobs(state: tauri::State<'_, AppState>) -> Result<Vec<Job>, String> {
    let manager = state.jobs.lock().map_err(|_| "job queue lock poisoned")?;
    Ok(manager.jobs().cloned().collect())
}

#[tauri::command]
fn enqueue_jobs(
    paths: Vec<String>,
    format: String,
    quality: String,
    state: tauri::State<'_, AppState>,
) -> Result<Vec<String>, String> {
    let format = match format.as_str() {
        "SRT" => OutputFormat::Srt,
        _ => OutputFormat::Txt,
    };
    let quality = match quality.as_str() {
        "快速" => Quality::Fast,
        "高準確度" => Quality::HighAccuracy,
        _ => Quality::Balanced,
    };
    let mut manager = state.jobs.lock().map_err(|_| "job queue lock poisoned")?;
    paths
        .into_iter()
        .map(|path| {
            let source = PathBuf::from(path);
            let output = source.with_extension(match format {
                OutputFormat::Txt => "txt",
                OutputFormat::Srt => "srt",
            });
            manager
                .enqueue(
                    source,
                    output,
                    format,
                    quality,
                    "sensevoice-yue-int8-2024-07-17".into(),
                    "2365baeacb507f821a0c8120fcee3d484dba7a07".into(),
                )
                .map_err(|error| error.to_string())
        })
        .collect()
}

fn persist_output(path: &Path, content: &str) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    let temporary = path.with_extension("canto.part");
    fs::write(&temporary, content.as_bytes()).map_err(|error| error.to_string())?;
    if path.exists() {
        fs::remove_file(path).map_err(|error| error.to_string())?;
    }
    fs::rename(temporary, path).map_err(|error| error.to_string())
}

fn drain_results(
    engine: &mut CoreEngine,
    job: &Job,
    jobs: &Arc<Mutex<JobManager>>,
    timed: &mut Vec<TimedText>,
) -> Result<bool, String> {
    let mut final_result = false;
    while let Some(result) = engine.poll().map_err(|error| error.to_string())? {
        if result.kind == ResultKind::Error {
            return Err(result.text);
        }
        let start_ms = job.processed_ms + result.start_ms;
        let end_ms = job.processed_ms + result.end_ms;
        if !result.text.trim().is_empty() {
            timed.push(TimedText { start_ms, end_ms, text: result.text.clone() });
            jobs.lock().map_err(|_| "job queue lock poisoned")?
                .update_segment(&job.id, CompletedSegment {
                    start_ms,
                    end_ms,
                    text: result.text,
                })
                .map_err(|error| error.to_string())?;
        }
        final_result |= result.kind == ResultKind::Final;
    }
    Ok(final_result)
}

fn run_job(
    job: Job,
    model_path: &Path,
    resource_dir: &Path,
    jobs: &Arc<Mutex<JobManager>>,
) -> Result<(), String> {
    if !is_supported(&job.source) {
        return Err("不支援嘅媒體格式".into());
    }
    if source_fingerprint(&job.source).map_err(|error| error.to_string())? != job.source_fingerprint {
        return Err("來源檔案喺加入佇列後已經改變".into());
    }
    let ffmpeg = find_packaged_ffmpeg(resource_dir)
        .ok_or_else(|| "找不到內置 FFmpeg".to_string())?;
    let mut engine = CoreEngine::new(16_000, 30).map_err(|error| error.to_string())?;
    engine.load_model(model_path.to_string_lossy().as_ref()).map_err(|error| error.to_string())?;
    let mut decoder = PcmDecoder::start(&ffmpeg, &job.source, job.processed_ms)
        .map_err(|error| error.to_string())?;
    jobs.lock().map_err(|_| "job queue lock poisoned")?
        .set_state(&job.id, JobState::Processing, None)
        .map_err(|error| error.to_string())?;

    let mut samples = vec![0_i16; 16_000];
    let mut fed_samples = 0_i64;
    let mut timed = job.completed_segments.iter().map(|item| TimedText {
        start_ms: item.start_ms,
        end_ms: item.end_ms,
        text: item.text.clone(),
    }).collect::<Vec<_>>();
    loop {
        let count = decoder.read_samples(&mut samples).map_err(|error| error.to_string())?;
        if count == 0 {
            engine.push_pcm16(&[], true).map_err(|error| error.to_string())?;
            break;
        }
        loop {
            match engine.push_pcm16(&samples[..count], false) {
                Ok(()) => break,
                Err(error) if error.code == 4 => {
                    let _ = drain_results(&mut engine, &job, jobs, &mut timed)?;
                    thread::sleep(Duration::from_millis(20));
                }
                Err(error) => return Err(error.to_string()),
            }
        }
        fed_samples += count as i64;
        let _ = drain_results(&mut engine, &job, jobs, &mut timed)?;
        if fed_samples % 160_000 < count as i64 {
            let progress = job.processed_ms + fed_samples * 1000 / 16_000;
            jobs.lock().map_err(|_| "job queue lock poisoned")?
                .update_progress(&job.id, progress, None)
                .map_err(|error| error.to_string())?;
        }
    }

    let deadline = Instant::now() + Duration::from_secs(180);
    loop {
        if drain_results(&mut engine, &job, jobs, &mut timed)? { break; }
        if Instant::now() >= deadline { return Err("等待 ASR 完成逾時".into()); }
        thread::sleep(Duration::from_millis(50));
    }
    let output = match job.format {
        OutputFormat::Txt => timed.iter().map(|item| item.text.as_str()).collect::<Vec<_>>().join("\n"),
        OutputFormat::Srt => render(&segment(&timed, SegmentationOptions::default())),
    };
    persist_output(&job.output, &output)?;
    jobs.lock().map_err(|_| "job queue lock poisoned")?
        .set_state(&job.id, JobState::Completed, None)
        .map_err(|error| error.to_string())
}

#[tauri::command]
async fn start_transcription(
    state: tauri::State<'_, AppState>,
) -> Result<(), String> {
    let jobs = Arc::clone(&state.jobs);
    let resource_dir = state.resource_dir.clone();
    let model_root = state.model_root.clone();
    tauri::async_runtime::spawn_blocking(move || {
        let model_path = model::resolve_installed(&model_root)?;
        let pending = jobs.lock().map_err(|_| "job queue lock poisoned")?.queued_jobs();
        for job in pending {
            if let Err(error) = run_job(job.clone(), &model_path, &resource_dir, &jobs) {
                jobs.lock().map_err(|_| "job queue lock poisoned")?
                    .set_state(&job.id, JobState::Failed, Some(error.clone()))
                    .map_err(|persist_error| persist_error.to_string())?;
                return Err(error);
            }
        }
        Ok(())
    }).await.map_err(|error| error.to_string())?
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            let state_dir = app.path().app_data_dir()?;
            let resource_dir = app.path().resource_dir()?;
            std::fs::create_dir_all(&state_dir)?;
            let model_root = state_dir.join("models");
            std::fs::create_dir_all(&model_root)?;
            let manager = JobManager::open(state_dir.join("jobs.v1.json"))?;
            app.manage(AppState {
                jobs: Arc::new(Mutex::new(manager)),
                resource_dir,
                model_root,
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            model_status,
            install_model,
            list_jobs,
            enqueue_jobs,
            start_transcription
        ])
        .run(tauri::generate_context!())
        .expect("failed to run CantoTranscribe");
}
