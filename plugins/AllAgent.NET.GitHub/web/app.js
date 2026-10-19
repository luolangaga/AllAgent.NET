/* GitHub data is rendered as text only. No remote HTML, scripts, or arbitrary API URLs. */
const sdk = AllAgentPlugin.connect();
const $ = id => document.getElementById(id);
const state = { kind: 'issues', page: 1, more: false, selected: null, detail: null, version: 0, detailVersion: 0, task: null, operations: new Set(), loggedIn: false };
function node(tag, text, className) { const el = document.createElement(tag); if (text != null) el.textContent = text; if (className) el.className = className; return el; }
function button(text, handler, className) { const el = node(className === 'list-item' ? 'button' : 'ag-button', text, className); el.type = 'button'; el.addEventListener('click', handler); return el; }
function error(value, target = 'error') { $(target).textContent = value instanceof Error ? value.message : String(value); $(target).hidden = false; }
function clearError() { $('error').hidden = true; }
function external(url) { try { const parsed = new URL(url); if (parsed.protocol === 'https:' && parsed.hostname === 'github.com' && !parsed.username && !parsed.password) sdk.openExternal(parsed.href); } catch { /* invalid remote URL */ } }
function stamp(value) { const date = new Date(value); return Number.isNaN(date.valueOf()) ? '' : date.toLocaleDateString('zh-CN'); }
function repositoryOf(item) { const match = /^https:\/\/api\.github\.com\/repos\/([\w.-]+\/[\w.-]+)$/.exec(item.repository_url || ''); return item.full_name || item.repository?.full_name || match?.[1] || ''; }
function itemInfo(item) {
  const repository = repositoryOf(item);
  const subject = item.subject;
  if (subject) {
    const match = /^https:\/\/api\.github\.com\/repos\/([\w.-]+\/[\w.-]+)\/(issues|pulls)\/(\d+)$/.exec(subject.url || '');
    return { repository, number: match ? Number(match[3]) : null, kind: match?.[2] === 'pulls' ? 'pull' : 'issue', title: subject.title, url: `https://github.com/${repository}` };
  }
  return { repository, number: item.number, kind: item.pull_request || state.kind === 'pulls' ? 'pull' : 'issue', title: item.title || item.full_name, url: item.html_url };
}
async function checkLogin() {
  try {
    const status = await sdk.invoke('status');
    state.loggedIn = status.loggedIn;
    $('account').textContent = status.loggedIn ? `● ${status.account || 'GitHub'}` : '尚未连接';
    $('account-button').textContent = status.loggedIn ? '退出登录' : '登录 GitHub';
    $('login-panel').hidden = status.loggedIn;
    $('workbench').hidden = !status.loggedIn;
    if (status.loggedIn) { await loadRepositories(); await loadList(); }
  } catch (e) { error(e); $('account').textContent = '连接失败'; $('login-panel').hidden = false; }
}
async function loadRepositories() {
  try { const data = await sdk.invoke('list', { kind: 'repositories', page: 1 }); $('repos').replaceChildren(...data.items.map(repo => { const option = node('option'); option.value = repo.full_name; return option; })); }
  catch (e) { error(e); }
}
async function loadList() {
  const version = ++state.version;
  state.detailVersion++;
  state.selected = null; state.detail = null;
  $('detail').replaceChildren(node('div', '选择一项以查看详情。', 'empty'));
  clearError(); $('list').replaceChildren(node('p', '正在读取 GitHub…', 'list-empty'));
  $('next').disabled = true; $('previous').disabled = true;
  const canSearch = ['issues', 'pulls'].includes(state.kind);
  $('repository').disabled = !canSearch; $('query').disabled = !canSearch;
  $('quick-filters').hidden = !canSearch; $('review-filter').hidden = state.kind !== 'pulls';
  $('list-title').textContent = ({ issues: 'Issues', pulls: 'Pull requests', notifications: '消息通知', repositories: '仓库' })[state.kind];
  try {
    const data = await sdk.invoke('list', { kind: state.kind, repository: $('repository').value.trim(), query: $('query').value.trim(), page: state.page });
    if (version !== state.version) return;
    state.more = data.hasMore;
    $('count').textContent = data.total != null ? `${data.total} 项` : `本页 ${data.items.length} 项`;
    $('page').textContent = String(data.page);
    $('previous').disabled = state.page <= 1; $('next').disabled = !state.more;
    $('list').replaceChildren();
    if (!data.items.length) $('list').append(node('p', '没有匹配的内容。试试更换仓库或筛选条件。', 'list-empty'));
    for (const item of data.items) {
      const info = itemInfo(item);
      const row = button('', () => showItem(item, row), 'list-item');
      const meta = node('div', null, 'item-meta');
      meta.append(node('span', state.kind === 'repositories' ? '▤' : info.kind === 'pull' ? '⑂' : '◉', `status ${info.kind}`), node('span', info.repository), node('span', item.number ? `#${item.number}` : item.unread ? '未读' : ''));
      row.append(meta, node('div', info.title, 'item-title'), node('div', `${item.user?.login || item.reason || (item.private ? '私有仓库' : '公开仓库')} · ${stamp(item.updated_at)}`, 'muted'));
      if (item.labels?.length) { const labels = node('div', null, 'labels'); for (const label of item.labels.slice(0, 4)) labels.append(node('span', label.name, 'label')); row.append(labels); }
      $('list').append(row);
    }
  } catch (e) { if (version === state.version) { error(e); $('list').replaceChildren(node('p', '读取失败，请刷新重试。', 'list-empty')); } }
}
async function showItem(item, row) {
  const version = ++state.detailVersion;
  document.querySelectorAll('.list-item').forEach(el => el.classList.remove('active')); row.classList.add('active');
  const info = itemInfo(item); state.selected = info; state.detail = null;
  $('detail').replaceChildren(node('p', '正在读取上下文…', 'muted'));
  try {
    if (state.kind === 'repositories') {
      $('detail').replaceChildren(node('h2', item.full_name), node('p', item.description || '暂无仓库描述', 'body-text'));
      const actions = node('div', null, 'detail-actions');
      actions.append(button('查看 Issues', () => chooseRepo(item.full_name, 'issues')), button('查看 PR', () => chooseRepo(item.full_name, 'pulls')), button('在 GitHub 打开 ↗', () => external(item.html_url)));
      $('detail').append(actions); return;
    }
    if (!info.number) {
      $('detail').replaceChildren(node('h2', info.title), node('p', `${info.repository} · ${item.subject?.type || '通知'}`, 'muted'));
      $('detail').append(button('在 GitHub 打开仓库 ↗', () => external(info.url)));
      if (item.unread) $('detail').append(readButton(item));
      return;
    }
    const detail = await sdk.invoke('detail', { repository: info.repository, number: info.number, kind: info.kind });
    if (version !== state.detailVersion) return;
    state.detail = detail;
    const content = detail.item;
    const header = node('div', null, 'detail-header'); const heading = node('div');
    heading.append(node('span', `${info.repository} · #${info.number} · ${content.state}${content.draft ? ' · Draft' : ''}`, 'muted'), node('h2', content.title)); header.append(heading);
    $('detail').replaceChildren(header, node('p', `${content.user?.login || ''} · 更新于 ${stamp(content.updated_at)}`, 'muted'));
    const actions = node('div', null, 'detail-actions');
    actions.append(button(info.kind === 'pull' ? '✧ AI 审查 PR' : '✧ 交给 Agent 解决', () => openTask(info), 'primary'), button('在 GitHub 打开 ↗', () => external(content.html_url)));
    if (item.unread) actions.append(readButton(item));
    $('detail').append(actions);
    if (info.kind === 'pull') $('detail').append(node('p', `${content.head?.label || ''} → ${content.base?.label || ''} · ${content.changed_files} 个文件 · +${content.additions} −${content.deletions}`, 'muted'));
    $('detail').append(node('div', content.body || '暂无描述。', 'body-text'));
    if (detail.truncated) $('detail').append(node('p', '上下文较大，当前仅展示部分内容。Agent 会收到覆盖范围提示。', 'notice'));
    if (detail.files?.length) {
      $('detail').append(node('hr'), node('h3', '变更文件'));
      for (const file of detail.files) { const block = node('details'); block.append(node('summary', `${file.filename} · +${file.additions} −${file.deletions}`), node('pre', file.patch || '无文本补丁；可能为二进制或大型变更。')); $('detail').append(block); }
    }
    const comments = [...(detail.comments || []), ...(detail.reviews || [])];
    if (comments.length) $('detail').append(node('hr'), node('h3', `讨论与行内评论 · ${comments.length}`));
    for (const comment of comments) { const box = node('section', null, 'comment'); box.append(node('div', `${comment.user?.login || ''} · ${stamp(comment.created_at)}${comment.path ? ` · ${comment.path}:${comment.line || comment.original_line || ''}` : ''}`, 'muted'), node('div', comment.body || '', 'body-text')); $('detail').append(box); }
  } catch (e) { if (version === state.detailVersion) $('detail').replaceChildren(node('div', e.message || String(e), 'notice error')); }
}
function readButton(item) { const result = button('标记已读', async () => { result.disabled = true; try { await sdk.invoke('read', { id: item.id }); item.unread = false; result.textContent = '已读'; } catch (e) { error(e); result.disabled = false; } }); return result; }
function chooseRepo(repository, kind) { $('repository').value = repository; $('query').value = ''; chooseKind(kind); }
function chooseKind(kind) { state.kind = kind; state.page = 1; document.querySelectorAll('[data-kind]').forEach(el => { el.classList.toggle('selected', el.dataset.kind === kind); if (el.dataset.kind === kind) el.setAttribute('aria-current', 'page'); else el.removeAttribute('aria-current'); }); void loadList(); }
async function openTask(info) {
  state.task = info; $('task-error').hidden = true;
  $('task-title').textContent = info.kind === 'pull' ? '让 AI 审查 PR' : '让 Agent 解决 Issue';
  $('task-source').textContent = `${info.repository} #${info.number} · ${info.title}`;
  $('instructions').value = ''; $('start-task').disabled = false;
  $('task-dialog').showModal();
  try {
    const workspaces = await sdk.invoke('workspaces');
    const placeholder = node('option', '选择工作区，或在下方输入路径'); placeholder.value = '';
    $('workspace-choice').replaceChildren(placeholder, ...workspaces.map(workspace => { const option = node('option', `${workspace.name} · ${workspace.path}`); option.value = workspace.path; return option; }));
    $('workspace-choice').value = sdk.context.workspace || '';
    $('workspace-path').value = sdk.context.workspace || '';
    document.querySelector('[name=mode][value=existing]').checked = true; updateMode();
  } catch (e) { error(e, 'task-error'); }
}
function updateMode() { const clone = document.querySelector('[name=mode]:checked').value === 'clone'; $('workspace-choice').hidden = clone; $('workspace-label').hidden = clone; $('workspace-hint').textContent = clone ? '目标必须为空目录或尚不存在。PR 会克隆并检出本次读取的 head SHA；失败时保留文件。' : '校验 origin；保留现有分支及未提交修改。'; if (clone) $('workspace-path').value = ''; }
async function trackOperation(operation) {
  if (state.operations.has(operation.id)) return;
  state.operations.add(operation.id);
  $('activity').hidden = false; $('activity').textContent = '正在准备 GitHub 上下文与工作区…克隆可能需要几分钟，完成后可打开 Agent 会话。';
  try {
    while (operation.state === 'running') { await new Promise(resolve => setTimeout(resolve, 1500)); operation = await sdk.invoke('operation', { id: operation.id }); }
    if (operation.state === 'error') throw new Error(operation.error || '启动失败');
    const result = operation.result;
    $('activity').replaceChildren(node('span', 'Agent 流程已启动。结果保存在独立会话，切换页面后会继续运行。'), button('打开 Agent 会话 →', async () => { try { await sdk.invoke('host.openConversation', { id: result.conversationId, workspace: result.workspace }); } catch (e) { error(e); } }, 'operation-link'));
  } catch (e) { $('activity').textContent = 'GitHub 任务准备未完成。'; error(e); }
  finally { state.operations.delete(operation.id); $('start-task').disabled = false; }
}
$('search-form').addEventListener('submit', event => { event.preventDefault(); state.page = 1; void loadList(); });
$('refresh').addEventListener('click', () => void loadList());
$('previous').addEventListener('click', () => { if (state.page > 1) { state.page--; void loadList(); } });
$('next').addEventListener('click', () => { if (state.more) { state.page++; void loadList(); } });
document.querySelectorAll('[data-kind]').forEach(el => el.addEventListener('click', () => chooseKind(el.dataset.kind)));
document.querySelectorAll('[data-query]').forEach(el => el.addEventListener('click', () => { $('query').value = el.dataset.query; state.page = 1; void loadList(); }));
$('create-token').addEventListener('click', () => external('https://github.com/settings/tokens/new?scopes=repo,notifications,read:user&description=AllAgent.NET'));
$('account-button').addEventListener('click', async () => { if (state.loggedIn) { if (!window.confirm('退出 GitHub？本机保存的 Token 将被移除。')) return; try { await sdk.invoke('logout'); state.version++; state.detailVersion++; await checkLogin(); } catch (e) { error(e); } } else { $('login-panel').hidden = false; $('token').focus(); } });
$('login-form').addEventListener('submit', async event => { event.preventDefault(); $('login-submit').disabled = true; clearError(); const token = $('token').value; $('token').value = ''; try { await sdk.invoke('login', { token }); await checkLogin(); } catch (e) { error(e); } finally { $('login-submit').disabled = false; } });
$('browser-login').addEventListener('click', async () => { $('browser-login').disabled = true; try { await sdk.invoke('browserLogin'); $('device-code').textContent = '正在请求设备授权…'; let status; do { await new Promise(resolve => setTimeout(resolve, 1500)); status = await sdk.invoke('loginProgress'); $('device-code').textContent = status.instructions || '请在浏览器完成授权'; } while (status.pending); if (status.error) throw new Error(status.error); await checkLogin(); } catch (e) { error(e); } finally { $('browser-login').disabled = false; } });
for (const id of ['close-dialog', 'cancel-dialog']) $(id).addEventListener('click', () => $('task-dialog').close());
document.querySelectorAll('[name=mode]').forEach(el => el.addEventListener('change', updateMode));
$('workspace-choice').addEventListener('change', () => { $('workspace-path').value = $('workspace-choice').value; });
$('task-form').addEventListener('submit', async event => {
  event.preventDefault(); $('task-error').hidden = true; $('start-task').disabled = true;
  try {
    const info = state.task;
    const operation = await sdk.invoke('start', { repository: info.repository, number: info.number, kind: info.kind === 'pull' ? 'review' : 'solve', workspace: $('workspace-path').value.trim(), cloneRepository: document.querySelector('[name=mode]:checked').value === 'clone', instructions: $('instructions').value.trim() });
    $('task-dialog').close(); void trackOperation(operation);
  } catch (e) { error(e, 'task-error'); $('start-task').disabled = false; }
});
sdk.ready.then(async () => { await checkLogin(); const operations = await sdk.invoke('operations'); for (const operation of operations.filter(item => item.state === 'running' || item.state === 'done').slice(-1)) void trackOperation(operation); }).catch(error);

