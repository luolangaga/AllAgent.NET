const sdk = AllAgentPlugin.connect();
const $ = id => document.getElementById(id);
const state = { workspace: sdk.context.workspace || '', operation: null, busy: false };
const labels = { running: '执行中', done: '完成', error: '失败', cancelled: '已取消' };

function bytes(value) {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '';
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  return `${(value / 1024 / 1024).toFixed(1)} MB`;
}

function date(value) {
  if (!value) return '';
  const parsed = new Date(value);
  return Number.isNaN(parsed.valueOf()) ? '' : parsed.toLocaleString();
}

function show(text, error) {
  const output = $('output');
  output.hidden = false;
  output.dataset.error = error ? 'true' : 'false';
  output.textContent = text || '';
  sdk.resize();
}

function setActions(enabled) {
  for (const id of ['init', 'rebuild']) $(id).disabled = !enabled;
}

async function refresh() {
  if (!state.workspace) {
    $('index').textContent = '没有可用的工作区';
    $('index').dataset.state = 'muted';
    $('stats').textContent = '';
    setActions(false);
    return;
  }
  try {
    const status = await sdk.invoke('status', { workspace: state.workspace });
    $('index').textContent = status.indexed ? '索引已构建' : '索引未构建';
    $('index').dataset.state = status.indexed ? 'ok' : 'muted';
    $('path').textContent = status.indexPath || '';
    $('path').title = status.indexPath || '';
    const parts = [];
    if (status.indexed) {
      parts.push(`${status.files} 文件`, `${status.symbols} 符号`, `${status.references} 引用`);
      const size = bytes(status.indexBytes);
      if (size) parts.push(size);
      const updated = date(status.indexUpdatedAt);
      if (updated) parts.push(updated);
    }
    $('stats').textContent = parts.join(' · ');
    setActions(!state.busy);
    if (status.detail) show(status.detail, Boolean(status.error));
    else if (status.error) show(status.error, true);
  } catch (error) {
    $('index').textContent = error?.message || String(error);
    $('index').dataset.state = 'error';
    setActions(false);
  }
  sdk.resize();
}

async function track(operation) {
  state.operation = operation;
  $('cancel').hidden = false;
  while (operation.state === 'running') {
    show(operation.output || '正在执行…');
    await new Promise(resolve => setTimeout(resolve, 1000));
    operation = await sdk.invoke('operation', { id: operation.id });
  }
  state.operation = null;
  $('cancel').hidden = true;
  show(operation.error || operation.output || labels[operation.state] || operation.state, operation.state === 'error');
  await refresh();
}

async function begin(method, params) {
  if (state.busy || state.operation) return;
  state.busy = true;
  setActions(false);
  try {
    await track(await sdk.invoke(method, params));
  } catch (error) {
    show(error?.message || String(error), true);
  } finally {
    state.busy = false;
    await refresh();
  }
}

$('init').addEventListener('click', () => void begin('index', { workspace: state.workspace, mode: 'init' }));
$('rebuild').addEventListener('click', () => void begin('index', { workspace: state.workspace, mode: 'rebuild' }));
$('cancel').addEventListener('click', () => {
  if (state.operation) void sdk.invoke('cancel', { id: state.operation.id }).catch(error => show(error?.message || String(error), true));
});

sdk.on('context', context => {
  if (context?.workspace && context.workspace !== state.workspace) {
    state.workspace = context.workspace;
    void refresh();
  }
});

sdk.ready.then(() => {
  // The host sends the initial context with `init`, after connect() has already run, so read it
  // here rather than at module load.
  state.workspace = sdk.context.workspace || '';
  return refresh();
}).catch(error => show(error?.message || String(error), true));
