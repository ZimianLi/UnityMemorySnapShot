const $ = (id) => document.getElementById(id);

const PALETTE = [
  '#3b82f6', '#10b981', '#f59e0b', '#ec4899', '#8b5cf6', '#06b6d4',
  '#84cc16', '#f97316', '#14b8a6', '#a855f7', '#38bdf8', '#eab308',
  '#6366f1', '#f43f5e', '#94a3b8'
];

const appState = {
  config: null,
  devices: [],
  llvmDir: '',
  symbolsDirOk: false,
  snapshots: [],
  serverPid: null,

  unit: localStorage.getItem('allochook.unit') || 'MB',
  hookTiming: localStorage.getItem('allochook.hookTiming') || 'standard',
  launchMode: localStorage.getItem('allochook.launchMode') || 'systemallocator',

  currentId: null,
  summary: null,

  layer: '1',
  page: 1,
  pageSize: 200,
  search: '',
  category: '',
  sort: 'bytes',
  hideZero: localStorage.getItem('allochook.hideZeroLive') !== '0',
  skipLow: localStorage.getItem('allochook.skipLowFrames') !== '0',
  scaleEst: false,

  diffA: '',
  diffB: '',
  diffPage: 1,
  diffPageSize: 200,
  diffFilter: 'all',
  diffKind: 'all',
  diffSearch: '',
  diffData: null,

  rawMode: 'md',
  rawReportMd: '',
  pieChart: null,
};

const UNIT_DIV = { KB: 1024, MB: 1024 * 1024, GB: 1024 * 1024 * 1024 };

function fmtBytes(b) {
  const n = Number(b || 0);
  const v = n / (UNIT_DIV[appState.unit] || 1024 * 1024);
  const digits = appState.unit === 'KB' ? 1 : 2;
  return `${v.toFixed(digits)} ${appState.unit}`;
}

function signedBytes(b) {
  const n = Number(b || 0);
  return (n < 0 ? '-' : '+') + fmtBytes(Math.abs(n));
}

function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}

function shortFrame(n) {
  let s = String(n || '-').replace(/\(.*?\)/g, '');
  if (s.length > 150) s = s.slice(0, 150) + '…';
  return s;
}

// ---------------- Toast & Modal ----------------
function toast(msg, kind = 'info', ttl = 4200) {
  const stack = $('toastStack');
  const el = document.createElement('div');
  el.className = `toast ${kind}`;
  el.innerHTML = `<div class="toast-body">${escapeHtml(msg)}</div><button class="toast-close">✕</button>`;
  const close = () => { if (el.parentNode) el.remove(); };
  el.querySelector('.toast-close').onclick = close;
  stack.appendChild(el);
  if (ttl > 0) setTimeout(close, ttl);
}

function confirmDialog({ title = '确认', message = '', confirmText = '确认', danger = false }) {
  return new Promise((resolve) => {
    const modal = $('confirmModal');
    $('confirmTitle').textContent = title;
    $('confirmMsg').textContent = message;
    const okBtn = $('confirmOk');
    okBtn.textContent = confirmText;
    okBtn.className = danger ? 'btn-danger' : 'btn-primary';
    modal.classList.remove('hidden');

    const cleanup = (res) => {
      modal.classList.add('hidden');
      okBtn.onclick = null;
      $('confirmCancel').onclick = null;
      $('confirmClose').onclick = null;
      resolve(res);
    };
    okBtn.onclick = () => cleanup(true);
    $('confirmCancel').onclick = () => cleanup(false);
    $('confirmClose').onclick = () => cleanup(false);
  });
}

// ---------------- API Helper ----------------
function isLongApi(path, method) {
  if (method !== 'POST') return false;
  return path.startsWith('/api/snapshot') || path.startsWith('/api/install') ||
    path.startsWith('/api/launch') || path.startsWith('/api/inject-launch') ||
    path.startsWith('/api/upload') || path.endsWith('/resymbolize');
}

async function api(path, opt = {}) {
  const opts = { ...opt };
  const method = (opts.method || 'GET').toUpperCase();
  const timeoutMs = opts.timeoutMs ?? (isLongApi(path, method) ? 130000 : 25000);
  delete opts.timeoutMs;

  const ctrl = new AbortController();
  opts.signal = ctrl.signal;
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const r = await fetch(path, opts);
    if (!r.ok) {
      let body = {};
      try { body = await r.json(); } catch { }
      const err = new Error(body.error || `HTTP ${r.status}`);
      err.body = body;
      throw err;
    }
    return await r.json();
  } catch (e) {
    if (e && e.name === 'AbortError') {
      throw new Error(`请求超时 (${Math.round(timeoutMs / 1000)}s)：服务端正忙或已停止`);
    }
    throw e;
  } finally {
    clearTimeout(timer);
  }
}

async function copyText(text, okMsg = '已复制到剪贴板') {
  try {
    await navigator.clipboard.writeText(text);
    toast(okMsg, 'ok', 2500);
  } catch {
    toast('复制失败，请手动选中复制', 'warn');
  }
}

// ---------------- Heartbeat & State ----------------
async function checkHeartbeat() {
  if (document.visibilityState !== 'visible') return;
  try {
    const v = await api('/api/version', { timeoutMs: 5000 });
    const dot = $('verDot');
    const txt = $('verText');
    if (appState.serverPid !== null && v.pid !== appState.serverPid) {
      dot.className = 'status-dot warn';
      txt.textContent = `服务端已重启 (${appState.serverPid}→${v.pid})`;
    } else {
      dot.className = 'status-dot ok';
      txt.textContent = `${v.build} · pid ${v.pid}`;
    }
    appState.serverPid = v.pid;
  } catch {
    $('verDot').className = 'status-dot err';
    $('verText').textContent = '服务端无响应';
  }
}

async function loadState(preserveSelection = true) {
  const [ver, st] = await Promise.all([
    api('/api/version', { timeoutMs: 5000 }).catch(() => null),
    api('/api/state')
  ]);

  if (ver) {
    appState.serverPid = ver.pid;
    $('verDot').className = 'status-dot ok';
    $('verText').textContent = `${ver.build} · pid ${ver.pid}`;
  }

  appState.config = st.config;
  appState.devices = st.devices || [];
  appState.llvmDir = st.llvmDir || '';
  appState.symbolsDirOk = !!st.symbolsDirOk;
  appState.snapshots = st.snapshots || [];

  renderHeader();
  renderEnvForm();
  renderRules(appState.config.rules || []);
  renderSnapshotList();
  populateDiffSelectors();
  populateCategoryDropdown();

  if (!preserveSelection || !appState.currentId) {
    if (appState.snapshots.length > 0 && !appState.currentId) {
      await openSnapshot(appState.snapshots[0].id);
    }
  }
}

function renderHeader() {
  const devSel = $('device');
  const devs = appState.devices;
  devSel.innerHTML = devs.length
    ? devs.map(d => `<option>${escapeHtml(d.serial)} (${escapeHtml(d.state)})</option>`).join('')
    : '<option>无 ADB 设备</option>';
  const online = devs.some(d => d.state === 'device');
  $('devDot').className = 'status-dot ' + (online ? 'ok' : (devs.length ? 'warn' : 'err'));

  const pkg = (appState.config && appState.config.packageName) || '';
  $('envPillPkg').textContent = pkg || '未配置包名';

  const badge = $('envPillBadge');
  const ok = !!pkg && appState.symbolsDirOk && !!appState.llvmDir;
  badge.className = 'env-badge ' + (ok ? 'ok' : 'warn');
  badge.textContent = ok ? '就绪' : '待完善';
}

// ---------------- Environment Modal ----------------
function renderEnvForm() {
  const c = appState.config || {};
  $('pkg').value = c.packageName || '';
  $('adbPath').value = c.adbPath || 'adb';
  $('apkDir').value = c.apkDir || '';
  $('symDir').value = c.symbolsDir || '';
  $('llvmDir').value = c.llvmDir || '';

  const okSym = appState.symbolsDirOk;
  const okLlvm = !!appState.llvmDir;
  $('envTip').innerHTML =
    `<span style="color:${okSym ? 'var(--pos)' : 'var(--neg)'}">● 符号目录：${okSym ? '有效' : '无效或未配置'}</span>` +
    `　｜　<span style="color:${okLlvm ? 'var(--pos)' : 'var(--neg)'}">● LLVM 工具链：${okLlvm ? escapeHtml(appState.llvmDir) : '未找到'}</span>`;
}

async function openEnvModal() {
  renderEnvForm();
  $('envModal').classList.remove('hidden');
  await loadApks();
}

function closeEnvModal() {
  $('envModal').classList.add('hidden');
}

async function loadApks() {
  const dir = $('apkDir').value.trim();
  if (!dir) {
    $('apkList').innerHTML = '<option value="">（未填写 APK 目录）</option>';
    $('apkHint').className = 'field-hint';
    $('apkHint').textContent = '';
    return;
  }
  try {
    const r = await api('/api/apks', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ dir })
    });
    $('apkList').innerHTML = r.files.length
      ? r.files.map(a => `<option value="${escapeHtml(a.path)}">${escapeHtml(a.name)} (${a.mb} MB)</option>`).join('')
      : '<option value="">（目录下无 APK）</option>';
    $('apkHint').className = 'field-hint ' + (r.files.length ? 'ok' : 'bad');
    $('apkHint').textContent = r.files.length ? `扫描到 ${r.files.length} 个 APK` : (r.error || '未找到 APK');
    if (r.files.length && !$('pkg').value.trim()) await fillPkgFromApk(r.files[0].path);
  } catch (e) {
    $('apkHint').className = 'field-hint bad';
    $('apkHint').textContent = '扫描失败：' + e.message;
  }
}

async function fillPkgFromApk(path) {
  if (!path) return;
  try {
    const r = await api('/api/apk-info', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path })
    });
    if (r.ok && r.package) $('pkg').value = r.package;
  } catch { }
}

async function saveConfig(showToast = true) {
  const c = appState.config || {};
  const body = {
    adbPath: $('adbPath').value.trim() || 'adb',
    packageName: $('pkg').value.trim(),
    apkDir: $('apkDir').value.trim(),
    symbolsDir: $('symDir').value.trim(),
    llvmDir: $('llvmDir').value.trim(),
    port: c.port || 7788,
    rules: collectRules(),
    ignoreFramePatterns: c.ignoreFramePatterns
  };
  await api('/api/config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  await loadState(true);
  if (showToast) toast('环境配置已保存', 'ok');
}

async function installSelectedApk() {
  const apk = $('apkList').value;
  if (!apk) { toast('请先选择 APK', 'warn'); return; }
  $('btnInstall').disabled = true;
  $('btnInstall').textContent = '安装中…';
  try {
    const r = await api('/api/install', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ apk })
    });
    if (r.package) $('pkg').value = r.package;
    if (r.ok) toast(`安装完成${r.package ? '：' + r.package : ''}`, 'ok');
    else toast('安装失败：\n' + r.output, 'err', 7000);
  } catch (e) {
    toast('安装异常：' + e.message, 'err');
  } finally {
    $('btnInstall').disabled = false;
    $('btnInstall').textContent = '安装选中 APK';
  }
}

function uploadAndInstallApk() {
  const f = $('apkFile').files[0];
  if (!f) { toast('请先选择一个本地 .apk 文件', 'warn'); return; }
  $('btnUpload').disabled = true;
  $('uploadTip').className = 'field-hint';
  $('uploadTip').textContent = '上传中 0%';

  const xhr = new XMLHttpRequest();
  xhr.open('POST', '/api/upload?name=' + encodeURIComponent(f.name));
  xhr.upload.onprogress = (e) => {
    if (e.lengthComputable) $('uploadTip').textContent = `上传中 ${(e.loaded / e.total * 100).toFixed(0)}%`;
  };
  xhr.onload = async () => {
    try {
      const r = JSON.parse(xhr.responseText || '{}');
      if (!r.ok) throw new Error(r.error || `HTTP ${xhr.status}`);
      const info = await api('/api/apk-info', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ path: r.path })
      });
      if (info.ok && info.package) $('pkg').value = info.package;
      $('uploadTip').textContent = `已上传 ${r.mb} MB，正在安装…`;
      const ir = await api('/api/install', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ apk: r.path })
      });
      if (ir.package) $('pkg').value = ir.package;
      if (ir.ok) {
        $('uploadTip').className = 'field-hint ok';
        $('uploadTip').textContent = `安装完成${ir.package ? '（' + ir.package + '）' : ''}`;
        toast('APK 上传并安装成功', 'ok');
      } else {
        $('uploadTip').className = 'field-hint bad';
        $('uploadTip').textContent = '安装失败：' + ir.output;
      }
    } catch (e) {
      $('uploadTip').className = 'field-hint bad';
      $('uploadTip').textContent = '失败：' + e.message;
      toast('上传/安装失败：' + e.message, 'err');
    } finally {
      $('btnUpload').disabled = false;
    }
  };
  xhr.onerror = () => {
    $('btnUpload').disabled = false;
    $('uploadTip').className = 'field-hint bad';
    $('uploadTip').textContent = '网络错误';
  };
  xhr.send(f);
}

// ---------------- Launch & Snapshot ----------------
function syncLaunchSegUI() {
  document.querySelectorAll('#timingSeg button').forEach(b => b.classList.toggle('active', b.dataset.val === appState.hookTiming));
  document.querySelectorAll('#modeSeg button').forEach(b => b.classList.toggle('active', b.dataset.val === appState.launchMode));
  $('cfgTimingTag').textContent = appState.hookTiming === 'jdwp' ? 'JDWP · 0ms' : 'Standard';
  $('l2ParamsBox').classList.toggle('disabled', appState.launchMode !== 'systemallocator');
  $('launchBtnText').textContent = appState.hookTiming === 'jdwp' ? 'JDWP 注入并启动' : '启动 / 重启应用';
}

function readL2Inputs() {
  const min = parseInt($('l2Min').value, 10);
  const frames = parseInt($('l2Frames').value, 10);
  const sample = parseInt($('l2Sample').value, 10);
  if (!Number.isFinite(min) || min < 0 || min > 64 * 1024 * 1024) {
    toast('L2 阈值请填写 0 ~ 67108864 之间的整数', 'warn'); return null;
  }
  if (!Number.isFinite(frames) || frames < 1 || frames > 32) {
    toast('栈深请填写 1 ~ 32 之间的整数', 'warn'); return null;
  }
  if (!Number.isFinite(sample) || sample < 1 || sample > 1024) {
    toast('采样比例请填写 1 ~ 1024 之间的整数', 'warn'); return null;
  }
  localStorage.setItem('allochook.l2Min', String(min));
  localStorage.setItem('allochook.l2Frames', String(frames));
  localStorage.setItem('allochook.l2Sample', String(sample));
  return { min, frames, sample };
}

function restoreL2Inputs() {
  const mn = localStorage.getItem('allochook.l2Min');
  const fr = localStorage.getItem('allochook.l2Frames');
  const sp = localStorage.getItem('allochook.l2Sample');
  if (mn !== null) $('l2Min').value = mn;
  if (fr !== null) $('l2Frames').value = fr;
  if (sp !== null) $('l2Sample').value = sp;
}

async function unifiedLaunch() {
  const pkg = ($('pkg').value || (appState.config && appState.config.packageName) || '').trim();
  if (!pkg) {
    toast('请先在顶部「⚙ 环境与安装包」中填写包名', 'warn');
    openEnvModal();
    return;
  }
  const l2 = readL2Inputs();
  if (!l2) return;

  const btn = $('btnUnifiedLaunch');
  btn.disabled = true;
  const oldTxt = $('launchBtnText').textContent;
  $('launchBtnText').textContent = appState.hookTiming === 'jdwp' ? '正在注入并启动…' : '正在重启应用…';
  $('launchStatusBadge').className = 'log-badge muted';
  $('launchStatusBadge').textContent = '执行中';

  try {
    const r = await api('/api/launch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        pkg,
        hookTiming: appState.hookTiming,
        mode: appState.launchMode,
        minBytes: l2.min,
        frames: l2.frames,
        sample: l2.sample,
        patchDelayMs: appState.hookTiming === 'jdwp' ? 0 : 500
      })
    });

    const lines = [
      `package: ${r.package}`,
      `timing: ${r.hookTiming} (patch_delay_ms=${r.patchDelayMs}, warmup_ms=${r.warmupMs})`,
      `mode: ${r.mode}${r.l2 ? ` (min=${r.l2Min}B, frames=${r.l2Frames}, sample=1/${r.l2Sample})` : ''}`,
      `unityOffsets: ${r.unityOffsets || '-'}`,
    ];
    if (r.activity) lines.push(`activity: ${r.activity}`);
    if (r.pid) lines.push(`pid: ${r.pid}  abi: ${r.abi || '-'}  lib: ${r.libPath || '-'}`);
    if (r.inject) lines.push(`inject: ${r.inject}`);
    if (r.debug) lines.push(`jdwp: ${r.debug}`);
    if (r.output && r.output.trim()) lines.push(`am output:\n${r.output.trim()}`);
    if (r.log && r.log.length) lines.push(`logcat(AllocHook):\n${r.log.join('\n')}`);

    $('launchLogPre').textContent = lines.join('\n');
    $('launchStatusBadge').className = 'log-badge ' + (r.ok ? 'ok' : 'bad');
    $('launchStatusBadge').textContent = r.ok ? '成功' : '异常';
    if (!r.ok) $('launchLogDetails').open = true;

    toast(r.ok ? `启动成功（${r.hookTiming === 'jdwp' ? 'JDWP 注入' : '标准时机'} · ${r.l2 ? 'L2 已开启' : '仅 L1'}）` : `启动异常：${r.inject || r.output || '查看启动日志'}`, r.ok ? 'ok' : 'err', 5000);
  } catch (e) {
    $('launchLogPre').textContent = '启动失败：' + e.message;
    $('launchStatusBadge').className = 'log-badge bad';
    $('launchStatusBadge').textContent = '失败';
    $('launchLogDetails').open = true;
    toast('启动失败：' + e.message, 'err', 6000);
  } finally {
    btn.disabled = false;
    $('launchBtnText').textContent = oldTxt;
  }
}

async function captureSnapshot() {
  const pkg = ($('pkg').value || (appState.config && appState.config.packageName) || '').trim();
  if (!pkg) {
    toast('请先配置包名', 'warn');
    openEnvModal();
    return;
  }
  const btn = $('btnSnap');
  btn.disabled = true;
  $('snapStageText').textContent = '触发 dump…';
  $('snapBtnText').textContent = '正在采集与符号化…';
  const t0 = performance.now();
  try {
    const r = await api('/api/snapshot', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ label: $('snapLabel').value.trim(), mode: 'snapshot', pkg })
    });
    $('snapLabel').value = '';
    $('snapStageText').textContent = `${Math.round(performance.now() - t0)} ms`;
    toast(`快照 ${r.id} 已完成（${r.stacks.toLocaleString()} 栈）`, 'ok');
    await loadState(true);
    await openSnapshot(r.id);
  } catch (e) {
    const b = e.body || {};
    let msg = e.message;
    if (b.logLines) msg += '\nlogcat:\n' + b.logLines;
    $('snapStageText').textContent = '失败';
    toast('抓取失败：' + msg, 'err', 8000);
  } finally {
    btn.disabled = false;
    $('snapBtnText').textContent = '抓取当前快照';
  }
}

// ---------------- Snapshot List & Selection ----------------
function renderSnapshotList() {
  const box = $('snapList');
  const list = appState.snapshots || [];
  $('snapCountBadge').textContent = String(list.length);
  box.innerHTML = '';

  if (!list.length) {
    box.innerHTML = '<div class="muted-xs" style="padding:12px;text-align:center;">暂无快照</div>';
    return;
  }

  list.forEach((s, idx) => {
    const isViewing = appState.currentId === s.id;
    const isA = appState.diffA === s.id;
    const card = document.createElement('div');
    card.className = 'snap-card' + (isViewing ? ' active' : '');
    const pssStr = s.totalPssKB ? fmtBytes(s.totalPssKB * 1024) : '-';
    const timeStr = new Date(s.time).toLocaleString('zh-CN', { hour12: false });

    card.innerHTML =
      `<div class="snap-card-top">
         <span class="snap-id">${escapeHtml(s.id)}${s.label ? ' · ' + escapeHtml(s.label) : ''}</span>
         ${isA ? '<span class="snap-tag-a">基准 A</span>' : ''}
       </div>
       <div class="snap-card-meta">${escapeHtml(timeStr)} · ${(s.stacks || 0).toLocaleString()} 栈 · PSS ${escapeHtml(pssStr)}</div>
       <div class="snap-card-ops">
         <button class="snap-op-btn ${isA ? 'is-a' : ''}" data-op="setA" title="设为 Diff 基准 A">设为 A</button>
         <button class="snap-op-btn" data-op="cmp" title="${isA ? '选择另一份对比' : (appState.diffA ? '与基准 A 对比' : '与上一份对比')}">对比</button>
         <span class="spacer"></span>
         <button class="snap-op-btn" data-op="resym" title="用当前符号表重新解析">↻</button>
         <button class="snap-op-btn danger" data-op="del" title="删除快照">✕</button>
       </div>`;

    card.onclick = () => openSnapshot(s.id);

    card.querySelector('[data-op="setA"]').onclick = (ev) => {
      ev.stopPropagation();
      appState.diffA = s.id;
      renderSnapshotList();
      populateDiffSelectors();
      toast(`已将 ${s.id} 设为 Diff 基准 A`, 'info', 2500);
    };

    card.querySelector('[data-op="cmp"]').onclick = async (ev) => {
      ev.stopPropagation();
      let a = appState.diffA && appState.diffA !== s.id ? appState.diffA : null;
      if (!a) {
        const prev = list[idx + 1] || list[0];
        if (!prev || prev.id === s.id) {
          toast('只有一份快照，无法对比', 'warn');
          return;
        }
        a = prev.id;
      }
      appState.diffA = a;
      appState.diffB = s.id;
      appState.diffPage = 1;
      renderSnapshotList();
      populateDiffSelectors();
      switchTab('diff');
      await runDiff();
    };

    card.querySelector('[data-op="resym"]').onclick = async (ev) => {
      ev.stopPropagation();
      const btn = ev.currentTarget;
      btn.disabled = true; btn.textContent = '…';
      try {
        const r = await api(`/api/snapshot/${encodeURIComponent(s.id)}/resymbolize`, { method: 'POST' });
        toast(`重解完成：${r.stacks.toLocaleString()} 栈，未解析 ${r.unresolved}`, 'ok');
        await loadState(true);
        if (appState.currentId === s.id) await openSnapshot(s.id);
      } catch (e) {
        toast('重解失败：' + e.message, 'err');
      } finally {
        btn.disabled = false; btn.textContent = '↻';
      }
    };

    card.querySelector('[data-op="del"]').onclick = async (ev) => {
      ev.stopPropagation();
      const ok = await confirmDialog({
        title: '删除快照',
        message: `确定永久删除快照 ${s.id}${s.label ? '（' + s.label + '）' : ''} 吗？`,
        confirmText: '删除',
        danger: true
      });
      if (!ok) return;
      await api('/api/snapshot/' + encodeURIComponent(s.id), { method: 'DELETE' });
      if (appState.diffA === s.id) appState.diffA = '';
      if (appState.diffB === s.id) appState.diffB = '';
      if (appState.currentId === s.id) {
        appState.currentId = null;
        appState.summary = null;
      }
      toast(`已删除快照 ${s.id}`, 'info');
      await loadState(false);
    };

    box.appendChild(card);
  });
}

// ---------------- Open Snapshot & Active Tab ----------------
async function openSnapshot(id) {
  appState.currentId = id;
  appState.page = 1;
  renderSnapshotList();
  try {
    const sum = await api(`/api/snapshot/${encodeURIComponent(id)}/summary`);
    appState.summary = sum;
    if (appState.layer === '2' && !sum.hasL2Hold) appState.layer = '1';
    updateLayerControls();
    await refreshActiveTab();
  } catch (e) {
    toast('加载快照失败：' + e.message, 'err');
  }
}

function updateLayerControls() {
  const sum = appState.summary;
  const hasL2 = !!(sum && sum.hasL2Hold);
  document.querySelectorAll('#layerSeg button').forEach(b => {
    const l = b.dataset.layer;
    b.classList.toggle('active', l === appState.layer);
    if (l === '2') b.disabled = !hasL2;
  });
  $('wrapHideZero').classList.toggle('hidden', appState.layer === '1');
  const sm = (sum && sum.l2Sample) || 1;
  const showScale = sm > 1 && (appState.layer === '2' || appState.layer === 'all');
  $('wrapScaleEst').classList.toggle('hidden', !showScale);
  $('lblScaleEst').textContent = `×${sm} 估算`;
}

function switchTab(name) {
  document.querySelectorAll('#mainTabs button').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.id === 'tab-' + name));
  if (name === 'pie' && appState.pieChart && !appState.pieChart.isDisposed()) {
    setTimeout(() => appState.pieChart.resize(), 30);
  }
}

async function refreshActiveTab() {
  const activeTab = document.querySelector('#mainTabs button.active')?.dataset.tab || 'stacks';
  if (activeTab === 'stacks') await renderStacksTab();
  else if (activeTab === 'pie') renderPieTab();
  else if (activeTab === 'pss') await renderPssTab();
  else if (activeTab === 'raw') await renderRawTab();
}

// ---------------- Tab 1: Stacks (Server Paginated) ----------------
async function renderStacksTab() {
  const sum = appState.summary;
  if (!sum) {
    $('stacksEmpty').classList.remove('hidden');
    $('stacksContent').classList.add('hidden');
    return;
  }
  $('stacksEmpty').classList.add('hidden');
  $('stacksContent').classList.remove('hidden');

  updateLayerControls();

  const qs = new URLSearchParams({
    layer: appState.layer,
    page: String(appState.page),
    pageSize: String(appState.pageSize),
    search: appState.search,
    category: appState.category,
    hideZero: String(appState.hideZero),
    skipLow: String(appState.skipLow),
    scaleEst: String(appState.scaleEst),
    sort: appState.sort
  });

  const t0 = performance.now();
  const data = await api(`/api/snapshot/${encodeURIComponent(sum.id)}/stacks?` + qs.toString());
  const costMs = Math.round(performance.now() - t0);

  renderStacksKpis(sum, data);

  const banner = $('stackFilterBanner');
  if (appState.category || appState.search) {
    banner.classList.remove('hidden');
    const parts = [];
    if (appState.category) parts.push(`分类：<b>${escapeHtml(appState.category)}</b>`);
    if (appState.search) parts.push(`关键词：<b>${escapeHtml(appState.search)}</b>`);
    banner.innerHTML =
      `<span>当前过滤：${parts.join(' · ')}（命中 ${data.totalRows.toLocaleString()} 条，合计 ${fmtBytes(data.filteredTotalBytes)}）</span>` +
      `<button id="btnResetStackFilters" class="btn-secondary btn-xs">清除过滤</button>`;
    $('btnResetStackFilters').onclick = () => {
      appState.category = '';
      appState.search = '';
      $('stackCatFilter').value = '';
      $('stackSearch').value = '';
      $('btnClearSearch').classList.add('hidden');
      appState.page = 1;
      renderStacksTab();
    };
  } else {
    banner.classList.add('hidden');
  }

  renderStacksTable(data, sum);
  renderPager($('stackPager'), data.page, data.totalPages, data.totalRows, appState.pageSize, costMs, (newPage, newSize) => {
    appState.page = newPage;
    if (newSize) appState.pageSize = newSize;
    renderStacksTab();
  });
}

function renderStacksKpis(sum, pageData) {
  const kpis = [];
  const totalPssB = (sum.pss && sum.pss['TOTAL PSS']) ? sum.pss['TOTAL PSS'] * 1024 : 0;

  if (pageData.layerMode === 'l2' || (appState.layer === 'all' && sum.hasL2Hold)) {
    const sm = Math.max(sum.l2Sample || 1, 1);
    const est = sum.l2EstLiveBytes;
    const targetNh = sum.mallocPssBytes > 0 ? sum.mallocPssBytes : ((sum.pss['Native Heap'] || 0) * 1024);
    const accounted = (sm > 1) ? (est + sum.l2FilteredLiveBytes) : (sum.l2RawLiveBytes + sum.l2FilteredLiveBytes + sum.l2SampledLiveBytes);
    const cov = targetNh > 0 ? (accounted * 100 / targetNh) : 0;

    kpis.push({ label: sm > 1 ? `L2 实采持有 (1/${sm})` : 'L2 大块持有', val: fmtBytes(sum.l2RawLiveBytes), cls: 'pos', sub: sm > 1 ? `估算全量 ≈ ${fmtBytes(est)}` : `${sum.l2NonZeroStacks.toLocaleString()} 活跃栈` });
    kpis.push({ label: `小对象净存 (<${sum.l2Min}B)`, val: fmtBytes(sum.l2FilteredLiveBytes), cls: sum.l2FilteredLiveBytes > 0 ? 'warn' : '', sub: `累计申请 ${fmtBytes(sum.l2FilteredBytes)} / ${sum.l2FilteredCount.toLocaleString()} 次` });
    kpis.push({ label: '已释放累计', val: fmtBytes(sum.l2FreedBytes), sub: `${sum.l2AllocCount.toLocaleString()} 次分配 · 隐藏0存活 ${pageData.zeroLiveCount.toLocaleString()}` });
    kpis.push({ label: sum.mallocPssBytes > 0 ? 'K_PSS(malloc) 对账' : 'Native Heap 对账', val: targetNh > 0 ? `${cov.toFixed(1)}%` : '-', cls: cov > 60 ? 'pos' : 'warn', sub: targetNh > 0 ? `归因 ${fmtBytes(accounted)} / ${fmtBytes(targetNh)}` : '无 PSS 数据' });
  } else if (pageData.layerMode === 'l1-live') {
    kpis.push({ label: 'L1 当前已提交', val: fmtBytes(sum.l1LiveBytes), cls: 'pos', sub: `${sum.l1LiveSegs.toLocaleString()} 段 · ${sum.l1LiveStackCount.toLocaleString()} 栈` });
    kpis.push({ label: 'L2 Native 堆持有', val: sum.hasL2Hold ? fmtBytes(sum.l2RawLiveBytes) : '未开启', cls: sum.hasL2Hold ? '' : 'muted', sub: sum.hasL2Hold ? `小对象净存 ${fmtBytes(sum.l2FilteredLiveBytes)}` : '仅线上口径 L1' });
    kpis.push({ label: '进程 TOTAL PSS', val: totalPssB > 0 ? fmtBytes(totalPssB) : '-', sub: `${sum.package} · pid ${sum.pid}` });
    kpis.push({ label: '当前筛选合计', val: fmtBytes(pageData.filteredTotalBytes), sub: `${pageData.totalRows.toLocaleString()} 条匹配栈` });
  } else {
    kpis.push({ label: '窗口净提交', val: signedBytes(sum.l1NetBytes), cls: sum.l1NetBytes < 0 ? 'neg' : 'pos', sub: `提交 ${fmtBytes(sum.l1CommitBytes)} − 归还 ${fmtBytes(sum.l1ReleaseBytes)}` });
    kpis.push({ label: '虚拟预留 (PROT_NONE)', val: fmtBytes(sum.l1ReserveBytes), sub: '仅地址空间，不占 PSS' });
    kpis.push({ label: '进程 TOTAL PSS', val: totalPssB > 0 ? fmtBytes(totalPssB) : '-', sub: `${sum.package} · pid ${sum.pid}` });
    kpis.push({ label: '当前筛选合计', val: fmtBytes(pageData.filteredTotalBytes), sub: `${pageData.totalRows.toLocaleString()} 条匹配栈` });
  }

  $('stacksKpis').innerHTML = kpis.map(k =>
    `<div class="kpi-card">
       <div class="kpi-label">${escapeHtml(k.label)}</div>
       <div class="kpi-val ${k.cls || ''}">${escapeHtml(k.val)}</div>
       <div class="kpi-sub">${escapeHtml(k.sub || '')}</div>
     </div>`).join('');
}

function renderStacksTable(data, sum) {
  const wrap = $('stackTableWrap');
  wrap.innerHTML = '';

  const mode = data.layerMode;
  const sm = Math.max(sum.l2Sample || 1, 1);
  const scaled = (appState.scaleEst && sm > 1);

  const head = document.createElement('div');
  if (mode === 'l2' || mode === 'all') {
    head.className = 'drow head cols-l2';
    head.innerHTML =
      `<span>#</span>
       <span class="c-num">${scaled ? `估算存活(×${sm})` : '当前存活'}</span>
       <span class="c-num">${scaled ? '估算释放' : '已释放'}</span>
       <span class="c-num">次数</span>
       <span>线程</span>
       <span>分配点（${appState.skipLow ? '业务归因' : '栈顶帧'}）</span>`;
  } else if (mode === 'l1-live') {
    head.className = 'drow head cols-l1-live';
    head.innerHTML =
      `<span>#</span>
       <span class="c-num">当前驻留</span>
       <span class="c-num">占比</span>
       <span class="c-num">段数</span>
       <span>线程</span>
       <span>提交点（${appState.skipLow ? '业务归因' : '栈顶帧'}）</span>`;
  } else {
    head.className = 'drow head cols-l1-win';
    head.innerHTML =
      `<span>#</span>
       <span class="c-num">净提交</span>
       <span class="c-num">提交</span>
       <span class="c-num">归还</span>
       <span class="c-num">预留</span>
       <span class="c-num">次数</span>
       <span>线程</span>
       <span>分配点</span>`;
  }
  wrap.appendChild(head);

  if (!data.rows.length) {
    const empty = document.createElement('div');
    empty.className = 'empty-state';
    empty.innerHTML = '<div class="empty-title">没有匹配的调用栈</div><div class="empty-sub">请尝试放宽搜索条件、清除分类筛选或取消「隐藏0存活」</div>';
    wrap.appendChild(empty);
    return;
  }

  const totalDenom = Math.max(data.filteredTotalBytes, 1);
  const frag = document.createDocumentFragment();

  data.rows.forEach((r) => {
    const row = document.createElement('div');
    const isSyn = r.rowType !== 'stack';

    const skipBadge = r.skippedCount > 0 ? `<span class="tag-skip" title="已穿透 ${r.skippedCount} 层底层帧">↳ +${r.skippedCount}</span>` : '';
    const suspBadge = r.suspect === 'pool'
      ? '<span class="tag-warn" title="已释放量远大于存活量，属复用池">池化</span>'
      : (r.suspect === 'churn' ? '<span class="tag-warn" title="反复提交与归还">抖动</span>' : '');
    const catBadge = r.category ? `<span class="tag-cat">${escapeHtml(r.category)}</span>` : '';
    const siteHtml = `<span class="c-site-txt" title="${escapeHtml(r.effectiveFrame)}">${escapeHtml(shortFrame(r.effectiveFrame))}</span>${skipBadge}${suspBadge}${catBadge}`;

    if (mode === 'l2' || mode === 'all') {
      row.className = 'drow cols-l2' + (isSyn ? ' synthetic' : '');
      row.title = r.tip || '';
      row.innerHTML =
        `<span class="c-idx">${r.index}</span>
         <span class="c-num ${r.bytes > 0 ? 'c-pos' : 'c-muted'}">${fmtBytes(r.bytes)}</span>
         <span class="c-num c-muted">${r.aux ? fmtBytes(r.aux) : '-'}</span>
         <span class="c-num c-muted">${(r.count || 0).toLocaleString()}</span>
         <span class="c-thread">${escapeHtml(r.thread)}</span>
         <span class="c-site">${siteHtml}</span>`;
    } else if (mode === 'l1-live') {
      row.className = 'drow cols-l1-live';
      row.innerHTML =
        `<span class="c-idx">${r.index}</span>
         <span class="c-num c-pos">${fmtBytes(r.bytes)}</span>
         <span class="c-num c-muted">${(r.bytes * 100 / totalDenom).toFixed(1)}%</span>
         <span class="c-num c-muted">${(r.count || 0).toLocaleString()}</span>
         <span class="c-thread">${escapeHtml(r.thread)}</span>
         <span class="c-site">${siteHtml}</span>`;
    } else {
      row.className = 'drow cols-l1-win';
      row.innerHTML =
        `<span class="c-idx">${r.index}</span>
         <span class="c-num ${r.bytes < 0 ? 'c-neg' : 'c-pos'}">${signedBytes(r.bytes)}</span>
         <span class="c-num c-muted">${r.commit ? fmtBytes(r.commit) : '-'}</span>
         <span class="c-num c-muted">${r.release ? fmtBytes(r.release) : '-'}</span>
         <span class="c-num c-muted">${r.reserve ? fmtBytes(r.reserve) : '-'}</span>
         <span class="c-num c-muted">${(r.count || 0).toLocaleString()}</span>
         <span class="c-thread">${escapeHtml(r.thread)}</span>
         <span class="c-site">${siteHtml}</span>`;
    }

    if (!isSyn && r.frames && r.frames.length) {
      attachLazyStackDetail(row, r.frames, r.effectiveIndex, r.skippedCount);
    }
    frag.appendChild(row);
  });

  if (data.legacyFilteredBytes > 0) {
    const info = document.createElement('div');
    info.className = 'drow cols-l2 synthetic';
    info.innerHTML =
      `<span class="c-idx">-</span><span class="c-num c-muted">-</span><span class="c-num c-muted">-</span>
       <span class="c-num c-muted">${data.legacyFilteredCount.toLocaleString()}</span>
       <span class="c-thread">参考</span>
       <span class="c-site"><span class="c-site-txt">旧版快照无净存量统计：累计过滤申请 ${fmtBytes(data.legacyFilteredBytes)}</span></span>`;
    frag.appendChild(info);
  }

  wrap.appendChild(frag);
}

function attachLazyStackDetail(row, frames, effIdx, skippedCount) {
  let detailEl = null;
  row.onclick = () => {
    if (!detailEl) {
      detailEl = document.createElement('div');
      detailEl.className = 'stack-detail';
      const frameLines = frames.map((f, i) => {
        const num = String(i).padStart(2, ' ');
        if (skippedCount > 0 && i === effIdx) {
          return `<span class="biz-frame">#${num} [业务归因] ${escapeHtml(f)}</span>`;
        }
        return `#${num}            ${escapeHtml(f)}`;
      }).join('\n');
      detailEl.innerHTML =
        `<div class="stack-detail-bar">
           <span>完整调用栈（共 ${frames.length} 帧）</span>
           <button class="btn-secondary btn-xs btn-copy-stack">📋 复制调用栈</button>
         </div>
         <pre class="stack-frames">${frameLines}</pre>`;
      detailEl.querySelector('.btn-copy-stack').onclick = (ev) => {
        ev.stopPropagation();
        copyText(frames.map((f, i) => `#${i} ${f}`).join('\n'));
      };
      row.after(detailEl);
    } else {
      detailEl.classList.toggle('hidden');
    }
  };
}

function renderPager(el, page, totalPages, totalRows, pageSize, costMs, onChange) {
  if (!totalRows) { el.innerHTML = ''; return; }
  el.innerHTML =
    `<div>共 <b>${totalRows.toLocaleString()}</b> 条 · 第 <b>${page}</b> / ${totalPages} 页 <span class="muted-xs">(${costMs} ms)</span></div>
     <div class="pager-btns">
       <select class="select-compact pager-size">
         <option value="100" ${pageSize === 100 ? 'selected' : ''}>100/页</option>
         <option value="200" ${pageSize === 200 ? 'selected' : ''}>200/页</option>
         <option value="500" ${pageSize === 500 ? 'selected' : ''}>500/页</option>
       </select>
       <button class="btn-secondary btn-sm p-first" ${page <= 1 ? 'disabled' : ''}>首页</button>
       <button class="btn-secondary btn-sm p-prev" ${page <= 1 ? 'disabled' : ''}>上一页</button>
       <button class="btn-secondary btn-sm p-next" ${page >= totalPages ? 'disabled' : ''}>下一页</button>
       <button class="btn-secondary btn-sm p-last" ${page >= totalPages ? 'disabled' : ''}>末页</button>
     </div>`;
  el.querySelector('.p-first').onclick = () => onChange(1);
  el.querySelector('.p-prev').onclick = () => onChange(Math.max(1, page - 1));
  el.querySelector('.p-next').onclick = () => onChange(Math.min(totalPages, page + 1));
  el.querySelector('.p-last').onclick = () => onChange(totalPages);
  el.querySelector('.pager-size').onchange = (e) => onChange(1, parseInt(e.target.value, 10));
}

// ---------------- Tab 2: Module Pie & Rules Drawer ----------------
function populateCategoryDropdown() {
  const sel = $('stackCatFilter');
  const rules = (appState.config && appState.config.rules) || [];
  const cur = appState.category;
  const opts = ['<option value="">全部分类</option>'];
  rules.forEach(r => opts.push(`<option value="${escapeHtml(r.name)}">${escapeHtml(r.name)}</option>`));
  opts.push('<option value="小对象/阈值以下(无栈)">小对象/阈值以下(无栈)</option>');
  opts.push('<option value="Other">Other</option>');
  sel.innerHTML = opts.join('');
  sel.value = cur;
}

function jumpToCategoryStacks(catName) {
  appState.category = catName;
  appState.layer = 'all';
  appState.page = 1;
  $('stackCatFilter').value = catName;
  switchTab('stacks');
  renderStacksTab();
}

function renderPieTab() {
  const sum = appState.summary;
  if (!sum) {
    $('pieEmpty').classList.remove('hidden');
    $('pieContent').classList.add('hidden');
    return;
  }
  $('pieEmpty').classList.add('hidden');
  $('pieContent').classList.remove('hidden');

  const data = (sum.categories || []).map(c => ({ name: c.name, value: c.bytes, percent: c.percent }));
  const total = data.reduce((a, d) => a + d.value, 0) || 1;
  const sm = Math.max(sum.l2Sample || 1, 1);

  $('pieMeta').textContent = (sum.hasL1Live || sum.hasL2Hold)
    ? `全量存活 ${fmtBytes(total)} · ${data.length} 个业务分类（L1 存量 + L2 持有${sm > 1 ? ` ×${sm}` : ''} + 小对象净存量；点击扇区/表格行可下钻堆栈）`
    : `净驻留合计 ${fmtBytes(total)} · ${data.length} 个业务分类`;

  const box = $('pieChart');
  if (window.echarts) {
    if (appState.pieChart && !appState.pieChart.isDisposed()) appState.pieChart.dispose();
    appState.pieChart = echarts.init(box);
    appState.pieChart.setOption({
      backgroundColor: 'transparent',
      color: PALETTE,
      tooltip: {
        trigger: 'item',
        backgroundColor: '#161d29',
        borderColor: '#33425b',
        textStyle: { color: '#e6edf7', fontSize: 12 },
        formatter: p => `${escapeHtml(p.name)}<br/><b>${fmtBytes(p.value)}</b> (${p.percent}%)<br/><span style="color:#64748b">点击下钻查看堆栈</span>`
      },
      series: [{
        type: 'pie',
        radius: ['42%', '72%'],
        center: ['50%', '50%'],
        avoidLabelOverlap: true,
        minAngle: 2,
        data,
        label: {
          color: '#cbd5e1', fontSize: 11,
          formatter: p => p.percent >= 4 ? `${p.name}\n${p.percent}%` : ''
        },
        labelLine: { length: 12, length2: 10, smooth: true },
        itemStyle: { borderColor: '#10151e', borderWidth: 2 }
      }]
    });
    appState.pieChart.on('click', (params) => {
      if (params && params.name) jumpToCategoryStacks(params.name);
    });
  } else {
    drawFallbackPie(box, data, total);
  }

  $('pieTableWrap').innerHTML =
    `<table class="cat-tbl">
       <thead><tr><th></th><th>业务模块</th><th style="text-align:right">占用</th><th style="text-align:right">占比</th><th>分布</th></tr></thead>
       <tbody>
         ${data.map((d, i) => {
           const color = PALETTE[i % PALETTE.length];
           const pct = (d.value * 100 / total).toFixed(1);
           return `<tr class="cat-row" data-cat="${escapeHtml(d.name)}" title="点击查看 ${escapeHtml(d.name)} 的分配堆栈">
             <td><span class="cat-swatch" style="background:${color}"></span></td>
             <td><b>${escapeHtml(d.name)}</b></td>
             <td class="num" style="text-align:right">${fmtBytes(d.value)}</td>
             <td class="num" style="text-align:right">${pct}%</td>
             <td><span class="cat-bar-bg"><span class="cat-bar-fill" style="width:${Math.max(2, Number(pct))}%;background:${color}"></span></span></td>
           </tr>`;
         }).join('')}
       </tbody>
     </table>`;

  $('pieTableWrap').querySelectorAll('tr.cat-row').forEach(tr => {
    tr.onclick = () => jumpToCategoryStacks(tr.dataset.cat);
  });
}

function drawFallbackPie(box, data, total) {
  box.innerHTML = '';
  const size = Math.min(box.clientWidth || 420, 420);
  const cv = document.createElement('canvas');
  const dpr = window.devicePixelRatio || 1;
  cv.width = size * dpr; cv.height = size * dpr;
  cv.style.width = size + 'px'; cv.style.height = size + 'px';
  box.appendChild(cv);
  const ctx = cv.getContext('2d');
  ctx.scale(dpr, dpr);
  const cx = size / 2, cy = size / 2, r = size * 0.42, r0 = size * 0.22;
  let a0 = -Math.PI / 2;
  data.forEach((d, i) => {
    const a1 = a0 + (d.value / total) * Math.PI * 2;
    ctx.beginPath(); ctx.arc(cx, cy, r, a0, a1); ctx.arc(cx, cy, r0, a1, a0, true); ctx.closePath();
    ctx.fillStyle = PALETTE[i % PALETTE.length]; ctx.fill();
    ctx.strokeStyle = '#10151e'; ctx.lineWidth = 2; ctx.stroke();
    a0 = a1;
  });
}

function attrEsc(s) { return String(s || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;'); }

function renderRules(list) {
  const box = $('rulesList');
  box.innerHTML = '';
  list.forEach((r, i) => {
    const div = document.createElement('div');
    div.className = 'rule-item';
    div.innerHTML =
      `<input class="rn" value="${attrEsc(r.name)}" placeholder="分类名" />
       <input class="rp" value="${attrEsc(r.pattern)}" placeholder="正则匹配" />
       <button class="btn-icon del" title="删除">✕</button>`;
    div.querySelector('.del').onclick = () => { list.splice(i, 1); renderRules(list); };
    box.appendChild(div);
  });
}

function collectRules() {
  return [...document.querySelectorAll('#rulesList .rule-item')].map(d => ({
    name: d.querySelector('.rn').value.trim(),
    pattern: d.querySelector('.rp').value.trim()
  })).filter(r => r.name && r.pattern);
}

async function saveRules() {
  const r = collectRules();
  if (!r.length) { toast('至少保留一条有效分类规则', 'warn'); return; }
  await saveConfig(false);
  if (appState.currentId) {
    appState.summary = await api(`/api/snapshot/${encodeURIComponent(appState.currentId)}/summary`);
    renderPieTab();
  }
  toast('规则已保存并重算完成', 'ok');
}

// ---------------- Tab 3: PSS Reconciliation ----------------
async function renderPssTab() {
  const sum = appState.summary;
  if (!sum) {
    $('pssEmpty').classList.remove('hidden');
    $('pssContent').classList.add('hidden');
    return;
  }
  $('pssEmpty').classList.add('hidden');
  $('pssContent').classList.remove('hidden');

  const diffAbsPct = Math.abs(sum.pssDiffPct || 0);
  $('pssKpis').innerHTML = [
    { label: 'Σ smaps PSS', val: fmtBytes(sum.smapsTotalBytes), cls: 'pos', sub: `${sum.totalVmas.toLocaleString()} 个 VMA` },
    { label: 'GPU mtrack (EGL+GL)', val: fmtBytes(sum.gpuMtrackBytes), sub: `EGL ${fmtBytes(sum.eglMtrackBytes)} + GL ${fmtBytes(sum.glMtrackBytes)}` },
    { label: 'smaps + GPU 合计', val: fmtBytes(sum.reconciledPssBytes), sub: '全进程物理内存还原口径' },
    { label: 'dumpsys TOTAL PSS', val: sum.dumpsysTotalBytes > 0 ? fmtBytes(sum.dumpsysTotalBytes) : '-', sub: sum.dumpsysTotalBytes > 0 ? `对账差额 ${signedBytes(sum.pssDiffBytes)} (${sum.pssDiffPct >= 0 ? '+' : ''}${sum.pssDiffPct}%)` : '无 dumpsys 记录', cls: diffAbsPct < 3 ? 'pos' : 'warn' }
  ].map(k => `<div class="kpi-card"><div class="kpi-label">${escapeHtml(k.label)}</div><div class="kpi-val ${k.cls || ''}">${escapeHtml(k.val)}</div><div class="kpi-sub">${escapeHtml(k.sub)}</div></div>`).join('');

  const groups = sum.pssGroups || [];
  const totalForBar = Math.max(sum.smapsTotalBytes + sum.gpuMtrackBytes, 1);
  const barItems = groups.map((g, i) => ({ name: g.cat, bytes: g.bytes, color: PALETTE[i % PALETTE.length] }));
  if (sum.gpuMtrackBytes > 0) barItems.push({ name: 'GPU mtrack', bytes: sum.gpuMtrackBytes, color: '#64748b' });

  $('pssDistBar').innerHTML =
    `<div class="pss-bar">
       ${barItems.map(it => {
         const w = (it.bytes * 100 / totalForBar).toFixed(2);
         return `<div class="pss-seg" style="width:${w}%;background:${it.color}" title="${escapeHtml(it.name)}: ${fmtBytes(it.bytes)} (${Number(w).toFixed(1)}%)"></div>`;
       }).join('')}
     </div>
     <div class="pss-legend">
       ${barItems.slice(0, 10).map(it => `<span class="pss-legend-item"><span class="cat-swatch" style="background:${it.color}"></span>${escapeHtml(it.name)} <b>${fmtBytes(it.bytes)}</b></span>`).join('')}
     </div>`;

  const wrap = $('pssTableWrap');
  wrap.innerHTML = '';
  const head = document.createElement('div');
  head.className = 'drow head cols-pss';
  head.innerHTML = '<span>#</span><span class="c-num">PSS</span><span class="c-num">占比</span><span class="c-num">VMA</span><span>明细来源</span><span>分区</span>';
  wrap.appendChild(head);

  if (!groups.length) {
    wrap.innerHTML += '<div class="empty-state"><div class="empty-title">该快照无 smaps PSS 数据</div></div>';
    return;
  }

  groups.forEach((g, idx) => {
    const parts = [];
    if (g.l2StackCount > 0) parts.push(`L2(${g.l2StackCount.toLocaleString()})`);
    if (g.l1StackCount > 0) parts.push(`L1(${g.l1StackCount.toLocaleString()})`);
    if (g.subBucketCount > 0) parts.push(`${g.subBucketCount}子桶`);
    const tag = parts.length ? parts.join(' + ') : '路径分桶';

    const row = document.createElement('div');
    row.className = 'drow cols-pss' + (g.expandable ? ' pss-group' : '');
    row.innerHTML =
      `<span class="c-idx">${idx + 1}</span>
       <span class="c-num c-pos">${fmtBytes(g.bytes)}</span>
       <span class="c-num c-muted">${g.percent.toFixed(1)}%</span>
       <span class="c-num c-muted">${g.vmas.toLocaleString()}</span>
       <span class="c-thread">${escapeHtml(tag)}</span>
       <span class="c-site"><span class="c-site-txt">${g.expandable ? '<span class="caret">▸</span> ' : ''}${escapeHtml(g.cat)}</span></span>`;
    wrap.appendChild(row);

    if (!g.expandable) return;

    const panel = document.createElement('div');
    panel.className = 'pss-sub-panel hidden';
    wrap.appendChild(panel);

    let subState = { page: 1, pageSize: 50, search: '' };
    async function loadPssDetail() {
      const qs = new URLSearchParams({
        page: String(subState.page),
        pageSize: String(subState.pageSize),
        search: subState.search,
        hideZero: String(appState.hideZero),
        skipLow: String(appState.skipLow),
        scaleEst: String(appState.scaleEst),
      });
      const res = await api(`/api/snapshot/${encodeURIComponent(sum.id)}/pss/${encodeURIComponent(g.cat)}?` + qs.toString());
      panel.innerHTML = '';

      const tb = document.createElement('div');
      tb.className = 'pss-sub-toolbar';
      tb.innerHTML =
        `<div class="search-box small"><span class="search-ico">⌕</span><input class="pss-q" value="${attrEsc(subState.search)}" placeholder="筛选 ${escapeHtml(g.cat)} 内明细…" /></div>
         <span class="muted-xs">共 ${res.totalRows.toLocaleString()} 项</span>`;
      const qInp = tb.querySelector('.pss-q');
      let tmr = null;
      qInp.oninput = () => {
        clearTimeout(tmr);
        tmr = setTimeout(() => { subState.search = qInp.value.trim(); subState.page = 1; loadPssDetail(); }, 250);
      };
      panel.appendChild(tb);

      res.notes.forEach(n => {
        const nr = document.createElement('div');
        nr.className = 'drow cols-pss pss-note';
        nr.innerHTML =
          `<span></span>
           <span class="c-num ${n.positive ? 'c-pos' : (n.negative ? 'c-neg' : 'c-muted')}">${n.bytes ? fmtBytes(n.bytes) : '-'}</span>
           <span class="c-num c-muted">${n.percent ? n.percent.toFixed(1) + '%' : '-'}</span>
           <span class="c-num c-muted">${escapeHtml(n.countText)}</span>
           <span class="c-thread">${escapeHtml(n.tag)}</span>
           <span class="c-site"><span class="c-site-txt">${escapeHtml(n.text)}</span></span>`;
        panel.appendChild(nr);
      });

      res.rows.forEach((item, i) => {
        const ir = document.createElement('div');
        ir.className = 'drow cols-pss pss-child';
        const skipBadge = item.skippedCount > 0 ? `<span class="tag-skip">↳ +${item.skippedCount}</span>` : '';
        const suspBadge = item.suspect === 'pool' ? '<span class="tag-warn">池化</span>' : '';
        const catBadge = item.category ? `<span class="tag-cat">${escapeHtml(item.category)}</span>` : '';
        ir.innerHTML =
          `<span class="c-idx">${(res.page - 1) * res.pageSize + i + 1}</span>
           <span class="c-num c-pos">${fmtBytes(item.bytes)}</span>
           <span class="c-num c-muted">${item.percent.toFixed(1)}%</span>
           <span class="c-num c-muted">${(item.count || 0).toLocaleString()}</span>
           <span class="c-thread">${escapeHtml(item.thread)}</span>
           <span class="c-site"><span class="c-site-txt">${escapeHtml(shortFrame(item.name))}</span>${skipBadge}${suspBadge}${catBadge}</span>`;
        if (item.frames && item.frames.length) {
          attachLazyStackDetail(ir, item.frames, item.effectiveIndex, item.skippedCount);
        }
        panel.appendChild(ir);
      });

      if (res.totalPages > 1) {
        const pg = document.createElement('div');
        pg.className = 'pager-bar';
        pg.style.margin = '6px 12px 2px 0';
        renderPager(pg, res.page, res.totalPages, res.totalRows, res.pageSize, 0, (np, ns) => {
          subState.page = np;
          if (ns) subState.pageSize = ns;
          loadPssDetail();
        });
        panel.appendChild(pg);
      }
    }

    row.onclick = async () => {
      const hidden = panel.classList.toggle('hidden');
      const caret = row.querySelector('.caret');
      if (caret) caret.textContent = hidden ? '▸' : '▾';
      if (!hidden && !panel.dataset.loaded) {
        panel.dataset.loaded = '1';
        await loadPssDetail();
      }
    };
  });
}

// ---------------- Tab 4: Diff Comparison ----------------
function populateDiffSelectors() {
  const list = appState.snapshots || [];
  if (list.length >= 2) {
    if (!appState.diffB || !list.some(x => x.id === appState.diffB)) appState.diffB = list[0].id;
    if (!appState.diffA || !list.some(x => x.id === appState.diffA)) appState.diffA = list[1].id;
  } else if (list.length === 1) {
    appState.diffA = list[0].id;
    appState.diffB = list[0].id;
  }

  const mkOpts = (selId, curVal) => {
    const sel = $(selId);
    sel.innerHTML = list.map(s => `<option value="${escapeHtml(s.id)}">${escapeHtml(s.id)}${s.label ? ' · ' + escapeHtml(s.label) : ''} (${(s.stacks || 0).toLocaleString()} 栈)</option>`).join('');
    if (curVal) sel.value = curVal;
  };
  mkOpts('diffSelA', appState.diffA);
  mkOpts('diffSelB', appState.diffB);
}

async function runDiff() {
  const a = $('diffSelA').value;
  const b = $('diffSelB').value;
  if (!a || !b) {
    $('diffEmpty').classList.remove('hidden');
    $('diffContent').classList.add('hidden');
    return;
  }
  appState.diffA = a;
  appState.diffB = b;

  try {
    const t0 = performance.now();
    const res = await api('/api/diff', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        a, b,
        page: appState.diffPage,
        pageSize: appState.diffPageSize,
        search: appState.diffSearch,
        filter: appState.diffFilter,
        kindFilter: appState.diffKind,
        skipLow: appState.skipLow
      })
    });
    const costMs = Math.round(performance.now() - t0);
    appState.diffData = res;
    renderDiffView(res, costMs);
  } catch (e) {
    toast('Diff 计算失败：' + e.message, 'err');
  }
}

function renderDiffView(res, costMs) {
  $('diffEmpty').classList.add('hidden');
  $('diffContent').classList.remove('hidden');

  const s = res.summary;
  $('diffKpis').innerHTML = [
    { label: 'TOTAL PSS Δ', val: signedBytes(s.pssDeltaBytes), cls: s.pssDeltaBytes > 0 ? 'neg' : 'pos', sub: `${fmtBytes(s.metaA.totalPssKB * 1024)} → ${fmtBytes(s.metaB.totalPssKB * 1024)}` },
    { label: 'L1 存量 Δ', val: signedBytes(s.l1DeltaBytes), cls: s.l1DeltaBytes > 0 ? 'neg' : 'pos', sub: `${fmtBytes(s.metaA.l1LiveBytes)} → ${fmtBytes(s.metaB.l1LiveBytes)}` },
    { label: 'L2 持有 Δ', val: signedBytes(s.l2DeltaBytes), cls: s.l2DeltaBytes > 0 ? 'neg' : 'pos', sub: `${fmtBytes(s.metaA.l2LiveBytes)} → ${fmtBytes(s.metaB.l2LiveBytes)}` },
    { label: '小对象净存 Δ', val: signedBytes(s.smallDeltaBytes), cls: s.smallDeltaBytes > 0 ? 'neg' : 'pos', sub: `${fmtBytes(s.metaA.l2SmallLiveBytes)} → ${fmtBytes(s.metaB.l2SmallLiveBytes)}` },
    { label: '栈变化净额', val: signedBytes(s.netSum), cls: s.netSum > 0 ? 'neg' : 'pos', sub: `增长 +${fmtBytes(s.posSum)} / 减少 ${fmtBytes(Math.abs(s.negSum))}` }
  ].map(k => `<div class="kpi-card"><div class="kpi-label">${escapeHtml(k.label)}</div><div class="kpi-val ${k.cls}">${escapeHtml(k.val)}</div><div class="kpi-sub">${escapeHtml(k.sub)}</div></div>`).join('');

  $('diffCatStrip').innerHTML = (res.categoryDeltas || []).map(c =>
    `<div class="diff-cat-pill">
       <span>${escapeHtml(c.name)}</span>
       <b class="num ${c.delta > 0 ? 'c-neg' : (c.delta < 0 ? 'c-pos' : 'c-muted')}">${signedBytes(c.delta)}</b>
     </div>`).join('');

  const wrap = $('diffTableWrap');
  wrap.innerHTML = '';
  const head = document.createElement('div');
  head.className = 'drow head cols-diff';
  head.innerHTML = '<span>#</span><span class="c-num">Δ 变化</span><span class="c-num">基准 A</span><span class="c-num">目标 B</span><span class="c-num">次数 A→B</span><span>线程</span><span>分配点</span>';
  wrap.appendChild(head);

  if (!res.rows.length) {
    wrap.innerHTML += '<div class="empty-state"><div class="empty-title">没有匹配的变化栈</div></div>';
  } else {
    const frag = document.createDocumentFragment();
    res.rows.forEach(r => {
      const row = document.createElement('div');
      row.className = 'drow cols-diff';
      const skipBadge = r.skippedCount > 0 ? `<span class="tag-skip">↳ +${r.skippedCount}</span>` : '';
      const suspBadge = r.suspect === 'pool'
        ? '<span class="tag-warn" title="已释放量远大于存活量，属复用池">池化</span>'
        : (r.suspect === 'churn' ? '<span class="tag-warn" title="反复提交与归还">抖动</span>' : '');
      const catBadge = r.category ? `<span class="tag-cat">${escapeHtml(r.category)}</span>` : '';
      const layerBadge = `<span class="tag-layer">L${r.layer}</span>`;
      row.innerHTML =
        `<span class="c-idx">${r.index}</span>
         <span class="c-num ${r.delta > 0 ? 'c-neg' : (r.delta < 0 ? 'c-pos' : 'c-muted')}">${signedBytes(r.delta)}</span>
         <span class="c-num c-muted">${r.bytesA ? fmtBytes(r.bytesA) : '-'}</span>
         <span class="c-num c-muted">${r.bytesB ? fmtBytes(r.bytesB) : '-'}</span>
         <span class="c-num c-muted">${r.countA}→${r.countB}</span>
         <span class="c-thread">${escapeHtml(r.thread)}</span>
         <span class="c-site"><span class="c-site-txt">${escapeHtml(shortFrame(r.effectiveFrame))}</span>${skipBadge}${suspBadge}${catBadge}${layerBadge}</span>`;
      if (r.frames && r.frames.length) attachLazyStackDetail(row, r.frames, r.effectiveIndex, r.skippedCount);
      frag.appendChild(row);
    });
    wrap.appendChild(frag);
  }

  renderPager($('diffPager'), res.page, res.totalPages, res.totalRows, res.pageSize, costMs, (np, ns) => {
    appState.diffPage = np;
    if (ns) appState.diffPageSize = ns;
    runDiff();
  });
}

// ---------------- Tab 5: Report & Export ----------------
function renderSimpleMarkdown(md) {
  const lines = String(md || '').replace(/\r/g, '').split('\n');
  let html = '';
  let inCode = false;
  let inTable = false;
  let inList = false;

  const closeList = () => { if (inList) { html += '</ul>'; inList = false; } };
  const closeTable = () => { if (inTable) { html += '</tbody></table>'; inTable = false; } };
  const inlineFmt = (s) => escapeHtml(s)
    .replace(/\*\*(.+?)\*\*/g, '<b>$1</b>')
    .replace(/`([^`]+)`/g, '<code>$1</code>');

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (line.startsWith('```')) {
      closeList(); closeTable();
      if (!inCode) { html += '<pre>'; inCode = true; }
      else { html += '</pre>'; inCode = false; }
      continue;
    }
    if (inCode) { html += escapeHtml(line) + '\n'; continue; }
    if (line.trim() === '') { closeList(); closeTable(); continue; }

    if (line.startsWith('|')) {
      closeList();
      const cells = line.split('|').slice(1, -1).map(c => c.trim());
      if (cells.every(c => /^:?-{2,}:?$/.test(c))) continue;
      if (!inTable) {
        inTable = true;
        html += '<table><thead><tr>' + cells.map(c => `<th>${inlineFmt(c)}</th>`).join('') + '</tr></thead><tbody>';
      } else {
        html += '<tr>' + cells.map(c => `<td>${inlineFmt(c)}</td>`).join('') + '</tr>';
      }
      continue;
    }
    closeTable();

    if (line.startsWith('### ')) { closeList(); html += `<h3>${inlineFmt(line.slice(4))}</h3>`; }
    else if (line.startsWith('## ')) { closeList(); html += `<h2>${inlineFmt(line.slice(3))}</h2>`; }
    else if (line.startsWith('# ')) { closeList(); html += `<h1>${inlineFmt(line.slice(2))}</h1>`; }
    else if (line.startsWith('- ')) {
      if (!inList) { html += '<ul>'; inList = true; }
      html += `<li>${inlineFmt(line.slice(2))}</li>`;
    } else {
      closeList();
      html += `<p>${inlineFmt(line)}</p>`;
    }
  }
  closeList(); closeTable();
  if (inCode) html += '</pre>';
  return html;
}

async function renderRawTab() {
  const sum = appState.summary;
  if (!sum) {
    $('rawEmpty').classList.remove('hidden');
    $('rawContent').classList.add('hidden');
    return;
  }
  $('rawEmpty').classList.add('hidden');
  $('rawContent').classList.remove('hidden');

  document.querySelectorAll('#rawModeSeg button').forEach(b => b.classList.toggle('active', b.dataset.raw === appState.rawMode));
  $('rawReportView').classList.toggle('hidden', appState.rawMode !== 'md');
  $('rawJsonView').classList.toggle('hidden', appState.rawMode !== 'json');

  if (appState.rawMode === 'md') {
    const r = await fetch(`/api/snapshot/${encodeURIComponent(sum.id)}/report`);
    const md = r.ok ? await r.text() : '报告不存在';
    appState.rawReportMd = md;
    $('rawReportView').innerHTML = renderSimpleMarkdown(md);
  } else {
    const preview = await api(`/api/snapshot/${encodeURIComponent(sum.id)}/raw-preview`);
    $('rawJsonView').textContent = JSON.stringify(preview, null, 2);
  }
}

// ---------------- Event Bindings ----------------
function initEvents() {
  document.querySelectorAll('#unitSeg button').forEach(b => {
    b.classList.toggle('active', b.dataset.unit === appState.unit);
    b.onclick = () => {
      appState.unit = b.dataset.unit;
      localStorage.setItem('allochook.unit', appState.unit);
      document.querySelectorAll('#unitSeg button').forEach(x => x.classList.toggle('active', x === b));
      renderSnapshotList();
      refreshActiveTab();
      if (document.querySelector('#mainTabs button[data-tab="diff"]').classList.contains('active') && appState.diffData) {
        renderDiffView(appState.diffData, 0);
      }
    };
  });

  $('btnRefreshDev').onclick = () => loadState(true).then(() => toast('设备列表已刷新', 'info', 2000));
  $('btnEnvModal').onclick = openEnvModal;
  $('btnCloseEnv').onclick = closeEnvModal;
  $('btnCancelEnv').onclick = closeEnvModal;
  $('btnSaveCfg').onclick = async () => {
    try { await saveConfig(true); closeEnvModal(); }
    catch (e) { toast('保存失败：' + e.message, 'err'); }
  };
  $('btnRefreshApk').onclick = loadApks;
  $('apkDir').onkeydown = (e) => { if (e.key === 'Enter') loadApks(); };
  $('apkList').onchange = () => fillPkgFromApk($('apkList').value);
  $('btnInstall').onclick = installSelectedApk;
  $('btnUpload').onclick = uploadAndInstallApk;

  document.querySelectorAll('#timingSeg button').forEach(b => b.onclick = () => {
    appState.hookTiming = b.dataset.val;
    localStorage.setItem('allochook.hookTiming', appState.hookTiming);
    syncLaunchSegUI();
  });
  document.querySelectorAll('#modeSeg button').forEach(b => b.onclick = () => {
    appState.launchMode = b.dataset.val;
    localStorage.setItem('allochook.launchMode', appState.launchMode);
    syncLaunchSegUI();
  });
  document.querySelectorAll('[data-l2min]').forEach(b => b.onclick = () => { $('l2Min').value = b.dataset.l2min; });
  document.querySelectorAll('[data-l2frames]').forEach(b => b.onclick = () => { $('l2Frames').value = b.dataset.l2frames; });
  document.querySelectorAll('[data-l2sample]').forEach(b => b.onclick = () => { $('l2Sample').value = b.dataset.l2sample; });

  restoreL2Inputs();
  syncLaunchSegUI();

  $('btnUnifiedLaunch').onclick = unifiedLaunch;
  $('btnSnap').onclick = captureSnapshot;

  $('btnOpenLatestDiff').onclick = () => { switchTab('diff'); runDiff(); };

  document.querySelectorAll('#mainTabs button').forEach(b => b.onclick = () => {
    switchTab(b.dataset.tab);
    if (b.dataset.tab === 'diff') runDiff();
    else refreshActiveTab();
  });

  document.querySelectorAll('#layerSeg button').forEach(b => b.onclick = () => {
    appState.layer = b.dataset.layer;
    appState.page = 1;
    renderStacksTab();
  });

  let searchTimer = null;
  $('stackSearch').oninput = () => {
    const v = $('stackSearch').value.trim();
    $('btnClearSearch').classList.toggle('hidden', !v);
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => {
      appState.search = v;
      appState.page = 1;
      renderStacksTab();
    }, 200);
  };
  $('btnClearSearch').onclick = () => {
    $('stackSearch').value = '';
    $('btnClearSearch').classList.add('hidden');
    appState.search = '';
    appState.page = 1;
    renderStacksTab();
  };
  $('stackCatFilter').onchange = () => {
    appState.category = $('stackCatFilter').value;
    appState.page = 1;
    renderStacksTab();
  };
  $('stackSort').onchange = () => {
    appState.sort = $('stackSort').value;
    appState.page = 1;
    renderStacksTab();
  };

  $('chkSkipLowFrames').checked = appState.skipLow;
  $('chkSkipLowFrames').onchange = () => {
    appState.skipLow = $('chkSkipLowFrames').checked;
    localStorage.setItem('allochook.skipLowFrames', appState.skipLow ? '1' : '0');
    refreshActiveTab();
    if (document.querySelector('#mainTabs button[data-tab="diff"]').classList.contains('active')) runDiff();
  };
  $('chkHideZeroLive').checked = appState.hideZero;
  $('chkHideZeroLive').onchange = () => {
    appState.hideZero = $('chkHideZeroLive').checked;
    localStorage.setItem('allochook.hideZeroLive', appState.hideZero ? '1' : '0');
    appState.page = 1;
    refreshActiveTab();
  };
  $('chkL2Est').onchange = () => {
    appState.scaleEst = $('chkL2Est').checked;
    appState.page = 1;
    refreshActiveTab();
  };

  // Pie rules drawer
  $('btnToggleRules').onclick = () => {
    $('rulesDrawer').classList.toggle('hidden');
    if (appState.pieChart && !appState.pieChart.isDisposed()) setTimeout(() => appState.pieChart.resize(), 30);
  };
  $('btnCloseRules').onclick = () => {
    $('rulesDrawer').classList.add('hidden');
    if (appState.pieChart && !appState.pieChart.isDisposed()) setTimeout(() => appState.pieChart.resize(), 30);
  };
  $('btnAddRule').onclick = () => {
    const list = collectRules();
    list.push({ name: '', pattern: '' });
    renderRules(list);
  };
  $('btnSaveRules').onclick = saveRules;

  // Diff controls
  $('diffSelA').onchange = () => { appState.diffPage = 1; runDiff(); };
  $('diffSelB').onchange = () => { appState.diffPage = 1; runDiff(); };
  $('btnDiffSwap').onclick = () => {
    const a = $('diffSelA').value, b = $('diffSelB').value;
    $('diffSelA').value = b; $('diffSelB').value = a;
    appState.diffPage = 1;
    runDiff();
  };
  $('diffKindFilter').onchange = () => { appState.diffKind = $('diffKindFilter').value; appState.diffPage = 1; runDiff(); };
  document.querySelectorAll('#diffDirSeg button').forEach(b => b.onclick = () => {
    appState.diffFilter = b.dataset.filter;
    document.querySelectorAll('#diffDirSeg button').forEach(x => x.classList.toggle('active', x === b));
    appState.diffPage = 1;
    runDiff();
  });
  let diffTmr = null;
  $('diffSearch').oninput = () => {
    clearTimeout(diffTmr);
    diffTmr = setTimeout(() => { appState.diffSearch = $('diffSearch').value.trim(); appState.diffPage = 1; runDiff(); }, 220);
  };

  // Raw & export
  document.querySelectorAll('#rawModeSeg button').forEach(b => b.onclick = () => {
    appState.rawMode = b.dataset.raw;
    renderRawTab();
  });
  $('btnCopyMd').onclick = () => {
    if (appState.rawReportMd) copyText(appState.rawReportMd, '已复制 Markdown 报告');
  };
  $('btnDlMd').onclick = () => {
    if (appState.currentId) window.location.href = `/api/snapshot/${encodeURIComponent(appState.currentId)}/download?format=md`;
  };
  $('btnDlJson').onclick = () => {
    if (appState.currentId) window.location.href = `/api/snapshot/${encodeURIComponent(appState.currentId)}/download?format=json`;
  };

  window.addEventListener('resize', () => {
    if (appState.pieChart && !appState.pieChart.isDisposed() && $('tab-pie').classList.contains('active')) {
      appState.pieChart.resize();
    }
  });
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') checkHeartbeat();
  });
  setInterval(checkHeartbeat, 15000);
}

initEvents();
loadState(false).catch(e => toast('初始化失败：' + e.message, 'err', 7000));
