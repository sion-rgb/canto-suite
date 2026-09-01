import './styles.css';
import './model.css';
import { invoke } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';
import { getCurrentWebview } from '@tauri-apps/api/webview';
import { open } from '@tauri-apps/plugin-dialog';

type Format = 'TXT' | 'SRT';
type Quality = '快速' | '平衡' | '高準確度';
type ModelStatus = {
  installed: boolean;
  verified: boolean;
  modelId: string;
  revision: string;
  totalBytes: number;
  cpuThreads: number;
  totalRamMib: number;
  recommendedProfile: Quality;
};
type ModelProgress = { receivedBytes: number; totalBytes: number; currentFile: string };

const app = document.querySelector<HTMLElement>('#app')!;
app.innerHTML = `
  <header><div class="brand">CantoTranscribe</div><div class="privacy">只在本機處理 · 網絡只用於下載模型</div></header>
  <section class="queue model-panel" id="model-panel">
    <div><h2>本機廣東話模型</h2><p id="model-status">正在檢查電腦同模型…</p></div>
    <progress id="model-progress" max="1" value="0" hidden></progress>
    <button id="install-model" hidden>下載並驗證模型</button>
  </section>
  <section class="panel">
    <div class="drop" id="drop" tabindex="0"><div class="drop-icon">＋</div><h1>將影片或音訊拖到這裡</h1><p>MP4、MOV、MKV、MP3、M4A、AAC、WAV</p><button id="pick">選擇檔案</button><input id="files" type="file" multiple hidden accept="audio/*,video/*"></div>
    <div id="selection" class="selection" hidden>
      <div><span class="label">檔案</span><strong id="filename"></strong></div>
      <fieldset><legend>輸出格式</legend><label><input type="radio" name="format" value="TXT" checked> TXT <small>純文字，沒有時間碼</small></label><label><input type="radio" name="format" value="SRT"> SRT <small>字幕時間碼</small></label></fieldset>
      <fieldset><legend>辨識品質</legend><div class="choices"><label><input type="radio" name="quality" value="快速"> 快速</label><label class="recommended"><input type="radio" name="quality" value="平衡" checked> 平衡 <small>建議</small></label><label><input type="radio" name="quality" value="高準確度"> 高準確度</label></div></fieldset>
      <div class="language"><span class="label">語言</span><strong>香港廣東話</strong></div>
      <button class="primary" id="start">開始轉錄</button>
    </div>
  </section>
  <section class="queue"><h2>轉錄佇列</h2><div id="empty">尚未加入檔案</div><div id="jobs"></div></section>`;

const input = document.querySelector<HTMLInputElement>('#files')!;
const drop = document.querySelector<HTMLElement>('#drop')!;
const selection = document.querySelector<HTMLElement>('#selection')!;
let selected: File[] = [];
let selectedPaths: string[] = [];
let currentModel: ModelStatus | null = null;

function formatBytes(value: number) {
  return `${(value / 1024 / 1024).toFixed(0)} MiB`;
}

function showWarning(error: unknown) {
  let warning = document.querySelector<HTMLElement>('.model-warning');
  if (!warning) {
    warning = document.createElement('p');
    warning.className = 'model-warning';
    document.querySelector('.queue')!.append(warning);
  }
  warning.textContent = String(error);
}

async function refreshModelStatus() {
  if (!('__TAURI_INTERNALS__' in window)) return;
  currentModel = await invoke<ModelStatus>('model_status');
  const message = document.querySelector<HTMLElement>('#model-status')!;
  const install = document.querySelector<HTMLButtonElement>('#install-model')!;
  const quality = document.querySelector<HTMLInputElement>(`input[name="quality"][value="${currentModel.recommendedProfile}"]`);
  if (quality) quality.checked = true;
  message.textContent = currentModel.installed
    ? `已驗證 · ${currentModel.modelId} · 建議「${currentModel.recommendedProfile}」`
    : `${currentModel.cpuThreads} 執行緒 · ${(currentModel.totalRamMib / 1024).toFixed(1)} GiB RAM · 建議「${currentModel.recommendedProfile}」 · 需要下載 ${formatBytes(currentModel.totalBytes)}`;
  install.hidden = currentModel.installed;
}

async function ensureModel() {
  if (currentModel?.installed) return;
  const install = document.querySelector<HTMLButtonElement>('#install-model')!;
  const progress = document.querySelector<HTMLProgressElement>('#model-progress')!;
  const message = document.querySelector<HTMLElement>('#model-status')!;
  install.disabled = true;
  progress.hidden = false;
  const unlisten = await listen<ModelProgress>('model-progress', event => {
    progress.max = event.payload.totalBytes;
    progress.value = event.payload.receivedBytes;
    message.textContent = `正在下載 ${event.payload.currentFile} · ${Math.floor(event.payload.receivedBytes * 100 / event.payload.totalBytes)}%`;
  });
  try {
    currentModel = await invoke<ModelStatus>('install_model');
    await refreshModelStatus();
  } finally {
    unlisten();
    progress.hidden = true;
    install.disabled = false;
  }
}

document.querySelector('#install-model')!.addEventListener('click', () => {
  ensureModel().catch(showWarning);
});

function choose(files: FileList | null) {
  selected = files ? Array.from(files) : [];
  if (!selected.length) return;
  document.querySelector('#filename')!.textContent = selected.length === 1 ? selected[0].name : `${selected.length} 個檔案`;
  selection.hidden = false;
}
document.querySelector('#pick')!.addEventListener('click', async event => {
  if (!('__TAURI_INTERNALS__' in window)) {
    input.click();
    return;
  }
  event.preventDefault();
  const paths = await open({ multiple: true, filters: [{ name: '音訊或影片', extensions: ['mp4', 'mov', 'mkv', 'mp3', 'm4a', 'aac', 'wav'] }] });
  selectedPaths = paths == null ? [] : Array.isArray(paths) ? paths : [paths];
  if (selectedPaths.length) {
    document.querySelector('#filename')!.textContent = selectedPaths.length === 1 ? selectedPaths[0].split(/[\\/]/).pop()! : `${selectedPaths.length} 個檔案`;
    selection.hidden = false;
  }
});
input.addEventListener('change', () => choose(input.files));
drop.addEventListener('dragover', event => { event.preventDefault(); drop.classList.add('active'); });
drop.addEventListener('dragleave', () => drop.classList.remove('active'));
drop.addEventListener('drop', event => { event.preventDefault(); drop.classList.remove('active'); choose(event.dataTransfer?.files ?? null); });
if ('__TAURI_INTERNALS__' in window) {
  getCurrentWebview().onDragDropEvent(event => {
    if (event.payload.type !== 'drop') return;
    selectedPaths = event.payload.paths;
    if (selectedPaths.length) {
      document.querySelector('#filename')!.textContent = selectedPaths.length === 1 ? selectedPaths[0].split(/[\\/]/).pop()! : `${selectedPaths.length} 個檔案`;
      selection.hidden = false;
    }
  });
}
document.querySelector('#start')!.addEventListener('click', async () => {
  const format = (document.querySelector<HTMLInputElement>('input[name="format"]:checked')?.value ?? 'TXT') as Format;
  const quality = (document.querySelector<HTMLInputElement>('input[name="quality"]:checked')?.value ?? '平衡') as Quality;
  if ('__TAURI_INTERNALS__' in window && selectedPaths.length) {
    await invoke('enqueue_jobs', { paths: selectedPaths, format, quality });
  }
  const jobs = document.querySelector('#jobs')!;
  document.querySelector<HTMLElement>('#empty')!.hidden = true;
  const names = selectedPaths.length ? selectedPaths.map(path => path.split(/[\\/]/).pop()!) : selected.map(file => file.name);
  for (const name of names) jobs.insertAdjacentHTML('beforeend', `<article class="job"><div><strong>${name}</strong><p>${format} · ${quality} · 等候中</p></div><div class="status">0%</div></article>`);
  if ('__TAURI_INTERNALS__' in window) {
    try {
      await ensureModel();
      await invoke('start_transcription');
      document.querySelectorAll<HTMLElement>('.status').forEach(status => { status.textContent = '完成'; });
    } catch (error) {
      showWarning(error);
    }
  }
});

refreshModelStatus().catch(showWarning);
