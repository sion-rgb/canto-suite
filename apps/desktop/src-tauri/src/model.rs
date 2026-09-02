use reqwest::blocking::Client;
use reqwest::header::RANGE;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::time::Duration;
use sysinfo::System;

const CATALOG_JSON: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../../../shared/model-catalog/catalog.v1.json"
));

#[derive(Debug, Clone, Deserialize)]
struct Catalog {
    models: Vec<CatalogModel>,
}

#[derive(Debug, Clone, Deserialize)]
struct CatalogModel {
    id: String,
    revision: String,
    role: Vec<String>,
    enabled: bool,
    files: Vec<CatalogFile>,
}

#[derive(Debug, Clone, Deserialize)]
struct CatalogFile {
    path: String,
    size: u64,
    sha256: String,
    sources: Vec<CatalogSource>,
}

#[derive(Debug, Clone, Deserialize)]
struct CatalogSource {
    url: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ModelStatus {
    pub installed: bool,
    pub verified: bool,
    pub model_id: String,
    pub revision: String,
    pub total_bytes: u64,
    pub path: Option<PathBuf>,
    pub cpu_threads: usize,
    pub total_ram_mib: u64,
    pub recommended_profile: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DownloadProgress {
    pub received_bytes: u64,
    pub total_bytes: u64,
    pub current_file: String,
}

fn active_asr_model() -> Result<CatalogModel, String> {
    let catalog: Catalog = serde_json::from_str(CATALOG_JSON)
        .map_err(|error| format!("模型目錄格式錯誤：{error}"))?;
    catalog
        .models
        .into_iter()
        .find(|model| model.enabled && model.role.iter().any(|role| role == "QUALITY_ASR"))
        .ok_or_else(|| "模型目錄未有可用嘅 ASR 模型".to_string())
}

fn file_sha256(path: &Path) -> io::Result<String> {
    let mut input = File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = vec![0_u8; 1024 * 1024];
    loop {
        let read = input.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        digest.update(&buffer[..read]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

fn verify_file(root: &Path, file: &CatalogFile) -> bool {
    let path = root.join(&file.path);
    path.metadata().is_ok_and(|metadata| metadata.len() == file.size)
        && file_sha256(&path).is_ok_and(|digest| digest.eq_ignore_ascii_case(&file.sha256))
}

fn installed_path(root: &Path, model: &CatalogModel) -> PathBuf {
    root.join(&model.id).join(&model.revision)
}

pub fn status(root: &Path) -> Result<ModelStatus, String> {
    let model = active_asr_model()?;
    let path = installed_path(root, &model);
    let verified = path.is_dir() && model.files.iter().all(|file| verify_file(&path, file));
    let mut system = System::new();
    system.refresh_memory();
    let total_ram_mib = system.total_memory() / 1024 / 1024;
    let cpu_threads = std::thread::available_parallelism().map_or(1, usize::from);
    let recommended_profile = if total_ram_mib >= 16 * 1024 && cpu_threads >= 8 {
        "高準確度"
    } else if total_ram_mib >= 8 * 1024 && cpu_threads >= 4 {
        "平衡"
    } else {
        "快速"
    };
    Ok(ModelStatus {
        installed: verified,
        verified,
        model_id: model.id,
        revision: model.revision,
        total_bytes: model.files.iter().map(|file| file.size).sum(),
        path: verified.then_some(path),
        cpu_threads,
        total_ram_mib,
        recommended_profile: recommended_profile.to_string(),
    })
}

pub fn resolve_installed(root: &Path) -> Result<PathBuf, String> {
    status(root)?.path.ok_or_else(|| "請先喺應用程式內下載並驗證 ASR 模型".to_string())
}

fn atomic_write(path: &Path, contents: &[u8]) -> io::Result<()> {
    let temporary = path.with_extension("part");
    let mut output = File::create(&temporary)?;
    output.write_all(contents)?;
    output.sync_all()?;
    if path.exists() {
        fs::remove_file(path)?;
    }
    fs::rename(temporary, path)
}

pub fn install(
    root: &Path,
    mut on_progress: impl FnMut(DownloadProgress),
) -> Result<ModelStatus, String> {
    let model = active_asr_model()?;
    let installed = installed_path(root, &model);
    let staging = root.join(".staging").join(format!("{}-{}", model.id, model.revision));
    fs::create_dir_all(&staging).map_err(|error| error.to_string())?;
    let total_bytes = model.files.iter().map(|file| file.size).sum::<u64>();
    let client = Client::builder()
        .connect_timeout(Duration::from_secs(20))
        .timeout(Duration::from_secs(30 * 60))
        .user_agent("CantoTranscribe/0.1")
        .build()
        .map_err(|error| error.to_string())?;
    let mut completed = 0_u64;

    for catalog_file in &model.files {
        let destination = staging.join(&catalog_file.path);
        if verify_file(&staging, catalog_file) {
            completed += catalog_file.size;
            on_progress(DownloadProgress {
                received_bytes: completed,
                total_bytes,
                current_file: catalog_file.path.clone(),
            });
            continue;
        }
        if destination.exists() {
            fs::remove_file(&destination).map_err(|error| error.to_string())?;
        }
        if let Some(parent) = destination.parent() {
            fs::create_dir_all(parent).map_err(|error| error.to_string())?;
        }
        let part = PathBuf::from(format!("{}.part", destination.display()));
        if part.metadata().is_ok_and(|metadata| metadata.len() >= catalog_file.size) {
            fs::remove_file(&part).map_err(|error| error.to_string())?;
        }
        let existing = part.metadata().map_or(0, |metadata| metadata.len());
        let source = catalog_file
            .sources
            .first()
            .ok_or_else(|| format!("模型未有下載來源：{}", catalog_file.path))?;
        let mut request = client.get(&source.url);
        if existing > 0 {
            request = request.header(RANGE, format!("bytes={existing}-"));
        }
        let mut response = request.send().map_err(|error| format!("模型下載失敗：{error}"))?;
        if !response.status().is_success() {
            return Err(format!("模型下載失敗：HTTP {}", response.status()));
        }
        let resumed = existing > 0 && response.status().as_u16() == 206;
        let mut output = OpenOptions::new()
            .create(true)
            .write(true)
            .append(resumed)
            .truncate(!resumed)
            .open(&part)
            .map_err(|error| error.to_string())?;
        let mut received = if resumed { existing } else { 0 };
        let mut buffer = vec![0_u8; 1024 * 1024];
        loop {
            let read = response.read(&mut buffer).map_err(|error| error.to_string())?;
            if read == 0 {
                break;
            }
            output.write_all(&buffer[..read]).map_err(|error| error.to_string())?;
            received += read as u64;
            on_progress(DownloadProgress {
                received_bytes: completed + received,
                total_bytes,
                current_file: catalog_file.path.clone(),
            });
        }
        output.sync_all().map_err(|error| error.to_string())?;
        if received != catalog_file.size {
            return Err(format!("模型檔案大小錯誤：{}", catalog_file.path));
        }
        if !file_sha256(&part).is_ok_and(|digest| digest.eq_ignore_ascii_case(&catalog_file.sha256)) {
            let _ = fs::remove_file(&part);
            return Err(format!("模型校驗碼錯誤：{}", catalog_file.path));
        }
        fs::rename(&part, &destination).map_err(|error| error.to_string())?;
        completed += catalog_file.size;
    }

    atomic_write(
        &staging.join("install.json"),
        serde_json::to_string_pretty(&serde_json::json!({
            "modelId": model.id,
            "revision": model.revision,
            "verified": true
        }))
        .map_err(|error| error.to_string())?
        .as_bytes(),
    )
    .map_err(|error| error.to_string())?;
    if installed.exists() {
        fs::remove_dir_all(&installed).map_err(|error| error.to_string())?;
    }
    if let Some(parent) = installed.parent() {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    fs::rename(&staging, &installed).map_err(|error| error.to_string())?;
    status(root)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn embedded_catalog_selects_enabled_quality_asr() {
        let model = active_asr_model().unwrap();
        assert_eq!(model.id, "sensevoice-yue-int8-2024-07-17");
        assert!(model.files.iter().any(|file| file.path == "model.int8.onnx"));
        assert!(model.files.iter().all(|file| file.sha256.len() == 64));
        assert!(model.files.iter().all(|file| file.sources.len() >= 2));
    }
}
