(() => {
  'use strict';

  const host = window.chrome && window.chrome.webview;
  const $ = (selector) => document.querySelector(selector);
  const listEl = $('#list');
  const menuEl = $('#menu');
  const toastWrap = $('#toastWrap');
  const settingsPanel = $('#settingsPanel');
  const settingsBody = $('#settingsBody');
  const modalBackdrop = $('#modalBackdrop');

  const view = {
    data: null,
    filter: 'all',
    menuKey: null,
    settingsOpen: false,
    initialized: false,
  };

  const ICONS = {
    check: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m5 12.5 4.2 4.2L19 7"/></svg>',
    arrow: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 17 17 7"/><path d="M9 7h8v8"/></svg>',
    inbox: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 13h4l2 3h4l2-3h4"/><path d="M5.5 5h13l2 8v5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-5l2-8Z"/></svg>',
    search: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="6.5"/><path d="m16 16 4 4"/></svg>',
  };

  const SOURCE_NAMES = { dsh: 'DSH', codex: 'Codex', claude: 'Claude' };
  const SOURCE_COLORS = { dsh: 'var(--dsh)', codex: 'var(--codex)', claude: 'var(--claude)' };

  function post(type, payload = {}) {
    if (host) host.postMessage({ type, ...payload });
  }

  function escapeHtml(value) {
    return String(value ?? '')
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#39;');
  }

  function relTime(iso) {
    if (!iso) return '';
    const time = new Date(iso).getTime();
    if (!Number.isFinite(time)) return '';
    const diff = Math.max(0, Date.now() - time);
    const seconds = Math.floor(diff / 1000);
    if (seconds < 45) return '刚刚';
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes} 分钟前`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours} 小时前`;
    const days = Math.floor(hours / 24);
    if (days < 7) return `${days} 天前`;
    const date = new Date(time);
    return `${date.getMonth() + 1}月${date.getDate()}日`;
  }

  function clockTime(iso) {
    if (!iso) return '';
    const date = new Date(iso);
    if (!Number.isFinite(date.getTime())) return '';
    return date.toLocaleTimeString('zh-CN', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }

  function folderLabel(cwd) {
    if (!cwd) return '未知目录';
    const parts = cwd.replaceAll('\\', '/').split('/').filter(Boolean);
    if (parts.length === 0) return cwd;
    if (parts.length === 1) return parts[0];
    return `${parts[parts.length - 2]} / ${parts[parts.length - 1]}`;
  }

  function stateChip(row) {
    if (row.state === 'running') return { cls: 'running', text: '进行中' };
    if (row.state === 'interrupted') return { cls: 'interrupted', text: '已中断' };
    if (row.done) return { cls: 'done', text: '已完成' };
    if (row.isNew) return { cls: 'unread', text: '待看' };
    return { cls: 'done', text: '待完成' };
  }

  function currentRow(key) {
    return view.data?.rows?.find((row) => row.key === key) ?? null;
  }

  function filteredRows() {
    if (!view.data) return [];
    const rows = view.data.rows ?? [];
    switch (view.filter) {
      case 'unread':
        return rows.filter((row) => row.isNew);
      case 'running':
        return rows.filter((row) => row.state === 'running');
      case 'done':
        return rows.filter((row) => row.done);
      default:
        return rows;
    }
  }

  function rowHtml(row) {
    const chip = stateChip(row);
    const source = SOURCE_NAMES[row.sourceId] ?? row.sourceName ?? row.sourceId;
    const time = relTime(row.completedAt ?? row.lastActivityAt);
    const title = escapeHtml(row.title || '未命名会话');
    const classes = ['row'];
    if (row.done) classes.push('done');
    if (row.isNew) classes.push('new');
    return `
      <article class="${classes.join(' ')}" data-key="${escapeHtml(row.key)}" data-source="${escapeHtml(row.sourceId)}"
        tabindex="0" role="button" aria-label="${title}">
        <button class="check ${row.done ? 'checked' : ''}" role="checkbox" aria-checked="${row.done}"
          aria-label="${row.done ? '取消完成' : '标记完成'}" data-action="toggle">${ICONS.check}</button>
        <div class="row-main">
          <div class="row-head">
            <span class="source-chip">${escapeHtml(source)}</span>
            <span class="row-time" data-time="${escapeHtml(row.completedAt ?? row.lastActivityAt ?? '')}">${time}</span>
          </div>
          <div class="row-title">${title}</div>
          <div class="row-meta">
            <span class="folder">${folderLabel(row.cwd)}</span>
            <span class="meta-sep"></span>
            <span class="state ${chip.cls}">${chip.text}</span>
          </div>
        </div>
        <span class="row-arrow" aria-hidden="true">${ICONS.arrow}</span>
      </article>`;
  }

  function emptyHtml() {
    const messages = {
      all: ['收件箱是空的', '打开 DSH、Codex 或 Claude Code 开始一段对话，完成后会自动出现在这里。'],
      unread: ['没有待看的对话', 'Agent 完成一轮对话后，会在这里亮起。'],
      running: ['没有正在运行的对话', '去开一个新的 Agent 任务吧。'],
      done: ['还没有完成的记录', '勾选记录左侧的方框后，它们会留在这里。'],
    };
    const [title, text] = messages[view.filter] ?? messages.all;
    return `<div class="empty">
      <div class="empty-art">${ICONS.inbox}</div>
      <div class="empty-title">${title}</div>
      <div class="empty-text">${text}</div>
    </div>`;
  }

  function summaryText() {
    if (!view.data) return '正在加载…';
    if (view.data.scanning) return '正在扫描本地会话…';
    const { unread, running, total } = view.data.counts ?? {};
    if (unread > 0) return `${unread} 条待看 · ${running ?? 0} 个进行中`;
    if (running > 0) return `${running} 个进行中 · 共 ${total ?? 0} 条记录`;
    return `${total ?? 0} 条记录 · 最近扫描 ${clockTime(view.data.lastScanAt) || '—'}`;
  }

  function render() {
    if (!view.data) return;
    const data = view.data;
    const counts = data.counts ?? { unread: 0, running: 0, done: 0, total: 0 };
    const scrollTop = listEl.scrollTop;
    const rows = filteredRows();

    $('#brandSub').textContent = summaryText();
    $('#countAll').textContent = counts.total ?? 0;
    $('#countUnread').textContent = counts.unread ?? 0;
    $('#countRunning').textContent = counts.running ?? 0;
    $('#countDone').textContent = counts.done ?? 0;
    listEl.innerHTML = rows.length > 0 ? rows.map(rowHtml).join('') : emptyHtml();
    listEl.scrollTop = scrollTop;

    document.querySelectorAll('.filter').forEach((button) => {
      button.classList.toggle('active', button.dataset.filter === view.filter);
    });

    const scanning = Boolean(data.scanning);
    const pulse = $('#scanPulse');
    pulse.classList.toggle('live', scanning || (counts.running ?? 0) > 0);
    $('#scanText').textContent = scanning
      ? '正在扫描本地会话…'
      : data.lastScanAt
        ? `最近扫描 ${clockTime(data.lastScanAt)}`
        : '尚未扫描';

    $('#clearDoneBtn').disabled = (counts.done ?? 0) === 0;
    $('#pinBtn').classList.toggle('active', Boolean(data.settings?.alwaysOnTop));
    $('#versionText').textContent = data.version ? `v${data.version}` : '';
  }

  function showToast(message, kind = 'info') {
    if (!message) return;
    const toast = document.createElement('div');
    toast.className = `toast ${kind === 'error' ? 'error' : ''}`;
    toast.textContent = message;
    toastWrap.appendChild(toast);
    while (toastWrap.children.length > 3) toastWrap.firstElementChild.remove();
    setTimeout(() => {
      toast.classList.add('out');
      setTimeout(() => toast.remove(), 200);
    }, 3200);
  }

  function openSettings() {
    view.settingsOpen = true;
    settingsPanel.classList.add('open');
    settingsPanel.setAttribute('aria-hidden', 'false');
    renderSettings();
  }

  function closeSettings() {
    view.settingsOpen = false;
    settingsPanel.classList.remove('open');
    settingsPanel.setAttribute('aria-hidden', 'true');
    closeMenu();
  }

  function switchHtml(id, checked, label) {
    return `<label class="switch" title="${escapeHtml(label)}">
      <input type="checkbox" id="${id}" ${checked ? 'checked' : ''} aria-label="${escapeHtml(label)}">
      <span class="track"></span>
    </label>`;
  }

  function sourceCard(source, settings, defaults) {
    const enabledKey = `enable${source.id[0].toUpperCase()}${source.id.slice(1)}`;
    const pathKey = `${source.id}Root`;
    const enabled = Boolean(settings?.[enabledKey]);
    const rootValue = settings?.[pathKey] ?? '';
    const placeholder = defaults?.[pathKey] ?? '';
    const status = !enabled
      ? '已关闭'
      : !source.available
        ? '未找到目录'
        : source.error
          ? source.error
          : `已发现 ${source.count ?? 0} 条 · ${source.lastScanMs ?? 0} ms`;
    return `<div class="source-card" style="--source-color:${SOURCE_COLORS[source.id] ?? 'var(--muted)'}">
      <div class="source-head">
        <span class="source-dot"></span>
        <span class="source-name">${escapeHtml(source.name)}</span>
        ${switchHtml(enabledKey, enabled, `启用 ${source.name}`)}
      </div>
      <input class="path-input" id="${source.id}Path" value="${escapeHtml(rootValue)}"
        placeholder="${escapeHtml(placeholder)}" spellcheck="false" aria-label="${escapeHtml(source.name)} 数据目录">
      <div class="source-status ${source.error ? 'error' : ''}">${escapeHtml(status)}</div>
    </div>`;
  }

  function renderSettings() {
    const data = view.data;
    if (!data) {
      settingsBody.innerHTML = '<div class="settings-note">正在加载设置…</div>';
      return;
    }
    const settings = data.settings ?? {};
    const defaults = data.defaults ?? {};
    const sources = data.sources ?? [];
    const sourceCards = sources.length
      ? sources.map((source) => sourceCard(source, settings, defaults)).join('')
      : '<div class="card-hint">没有启用的数据源。</div>';

    settingsBody.innerHTML = `
      <div class="section-title">数据源</div>
      ${sourceCards}
      <div class="card-hint" style="margin:-2px 2px 10px">只读扫描本机会话文件，不会修改 Agent 数据，也不会上传任何内容。</div>

      <div class="section-title">收件箱</div>
      <div class="card">
        <div class="card-row">
          <div>
            <div class="card-label">回看天数</div>
            <div class="card-hint">登记最近几天内有活动的会话</div>
          </div>
          <input class="number-input" id="lookbackDays" type="number" min="1" max="90" value="${Number(settings.lookbackDays) || 3}">
        </div>
        <div class="card-row">
          <div>
            <div class="card-label">扫描间隔</div>
            <div class="card-hint">文件变化时仍会立即触发刷新</div>
          </div>
          <input class="number-input" id="scanInterval" type="number" min="2" max="120" value="${Number(settings.scanIntervalSeconds) || 4}">
        </div>
        <div class="card-row">
          <div>
            <div class="card-label">完成时通知</div>
            <div class="card-hint">窗口不在前台时弹出托盘气泡</div>
          </div>
          ${switchHtml('notifyOnComplete', Boolean(settings.notifyOnComplete), '完成时通知')}
        </div>
      </div>

      <div class="section-title">窗口</div>
      <div class="card">
        <div class="card-row">
          <div class="card-label">始终置顶</div>
          ${switchHtml('alwaysOnTop', Boolean(settings.alwaysOnTop), '始终置顶')}
        </div>
        <div class="card-row">
          <div class="card-label">开机自动启动</div>
          ${switchHtml('startWithWindows', Boolean(settings.startWithWindows), '开机自动启动')}
        </div>
      </div>

      <div class="section-title">数据</div>
      <div class="card">
        <div class="card-row">
          <button class="ghost-btn" id="openLogsBtn">打开日志目录</button>
          <button class="ghost-btn" id="clearAllBtn" style="color:#ff9f9f">清理全部记录</button>
        </div>
        <div class="card-hint" style="margin-top:8px">清理只影响这个收件箱的登记与已读状态，不会删除 Agent 会话。</div>
      </div>

      <div class="settings-actions">
        <button class="ghost-btn" id="cancelSettingsBtn">取消</button>
        <button class="primary-btn" id="saveSettingsBtn">保存设置</button>
      </div>
      <div class="settings-note">Agent 收件箱 ${escapeHtml(data.version ? `v${data.version}` : '')} · 本地运行 · 无网络请求</div>`;

    $('#cancelSettingsBtn').addEventListener('click', closeSettings);
    $('#saveSettingsBtn').addEventListener('click', saveSettings);
    $('#openLogsBtn').addEventListener('click', () => post('openLogs'));
    $('#clearAllBtn').addEventListener('click', () => {
      confirmAction('清理全部记录？', '所有记录都会从这个收件箱消失，已经打开的 Agent 会话不会被关闭；只有会话产生新的完成时才会重新出现。', '清理', () => {
        post('clearAll');
        closeSettings();
      });
    });
  }

  function saveSettings() {
    const sources = view.data?.sources ?? [];
    const payload = {
      lookbackDays: Number($('#lookbackDays')?.value) || 3,
      scanIntervalSeconds: Number($('#scanInterval')?.value) || 4,
      notifyOnComplete: Boolean($('#notifyOnComplete')?.checked),
      alwaysOnTop: Boolean($('#alwaysOnTop')?.checked),
      startWithWindows: Boolean($('#startWithWindows')?.checked),
    };
    for (const source of sources) {
      const enabledKey = `enable${source.id[0].toUpperCase()}${source.id.slice(1)}`;
      payload[enabledKey] = Boolean($(`#${enabledKey}`)?.checked);
      payload[`${source.id}Root`] = $(`#${source.id}Path`)?.value?.trim() ?? '';
    }
    post('saveSettings', payload);
    closeSettings();
  }

  function openMenu(x, y, key) {
    const row = currentRow(key);
    if (!row) return;
    view.menuKey = key;
    const doneLabel = row.done ? '取消完成' : '标记完成';
    menuEl.innerHTML = `
      <button class="menu-item" data-menu="focus">${ICONS.arrow} 切换到应用窗口</button>
      <button class="menu-item" data-menu="done">${ICONS.check} ${doneLabel}</button>
      <div class="menu-sep"></div>
      <button class="menu-item" data-menu="copy-title">复制标题</button>
      <button class="menu-item" data-menu="copy-id">复制会话 ID</button>
      <button class="menu-item" data-menu="copy-path">复制项目路径</button>`;
    menuEl.classList.add('open');
    const rect = menuEl.getBoundingClientRect();
    menuEl.style.left = `${Math.min(x, window.innerWidth - rect.width - 8)}px`;
    menuEl.style.top = `${Math.min(y, window.innerHeight - rect.height - 8)}px`;
  }

  function closeMenu() {
    view.menuKey = null;
    menuEl.classList.remove('open');
  }

  function copyText(text) {
    if (host) {
      post('copy', { text });
    } else if (navigator.clipboard) {
      navigator.clipboard.writeText(text).then(() => showToast('已复制到剪贴板')).catch(() => {});
    }
  }

  function handleMenu(action) {
    const key = view.menuKey;
    const row = key ? currentRow(key) : null;
    if (!row) {
      closeMenu();
      return;
    }
    switch (action) {
      case 'focus':
        post('setSeen', { key });
        post('focus', { key });
        break;
      case 'done':
        post('setDone', { key, done: !row.done });
        break;
      case 'copy-title':
        copyText(row.title);
        break;
      case 'copy-id':
        copyText(row.sessionId || row.key);
        break;
      case 'copy-path':
        copyText(row.cwd);
        break;
    }
    closeMenu();
  }

  function confirmAction(title, text, okLabel, onOk) {
    $('#modalTitle').textContent = title;
    $('#modalText').textContent = text;
    $('#modalOk').textContent = okLabel;
    modalBackdrop.classList.add('open');
    modalBackdrop.setAttribute('aria-hidden', 'false');
    const cleanup = () => {
      modalBackdrop.classList.remove('open');
      modalBackdrop.setAttribute('aria-hidden', 'true');
      $('#modalOk').onclick = null;
      $('#modalCancel').onclick = null;
    };
    $('#modalOk').onclick = () => {
      cleanup();
      onOk();
    };
    $('#modalCancel').onclick = cleanup;
  }

  function handleListClick(event) {
    const row = event.target.closest('.row');
    if (!row) return;
    const key = row.dataset.key;
    const check = event.target.closest('.check');
    if (check) {
      event.stopPropagation();
      const current = currentRow(key);
      if (!current) return;
      post('setDone', { key, done: !current.done });
      return;
    }
    post('setSeen', { key });
    post('focus', { key });
  }

  function handleKeydown(event) {
    if (event.key === 'Escape') {
      if (modalBackdrop.classList.contains('open')) {
        modalBackdrop.classList.remove('open');
        return;
      }
      if (view.settingsOpen) {
        closeSettings();
        return;
      }
      closeMenu();
      return;
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'r') {
      event.preventDefault();
      post('refresh');
      return;
    }
    if (event.key === 'Enter' || event.key === ' ') {
      const row = event.target.closest?.('.row');
      if (row) {
        event.preventDefault();
        post('setSeen', { key: row.dataset.key });
        post('focus', { key: row.dataset.key });
      }
    }
  }

  function bindUi() {
    let drag = null;
    const titlebar = $('#titlebar');
    titlebar.addEventListener('pointerdown', (event) => {
      if (event.button !== 0 || event.target.closest('button')) return;
      drag = { id: event.pointerId, x: event.screenX, y: event.screenY };
      titlebar.setPointerCapture(event.pointerId);
      post('dragStart');
      event.preventDefault();
    });
    titlebar.addEventListener('pointermove', (event) => {
      if (!drag || event.pointerId !== drag.id) return;
      post('dragMove', { dx: event.screenX - drag.x, dy: event.screenY - drag.y });
    });
    const finishDrag = (event) => {
      if (!drag || (event && event.pointerId !== drag.id)) return;
      drag = null;
      post('dragEnd');
    };
    titlebar.addEventListener('pointerup', finishDrag);
    titlebar.addEventListener('pointercancel', finishDrag);
    window.addEventListener('blur', finishDrag);

    $('#pinBtn').addEventListener('click', () => post('toggleTopmost'));
    $('#minBtn').addEventListener('click', () => post('minimize'));
    $('#closeBtn').addEventListener('click', () => post('close'));
    $('#settingsBtn').addEventListener('click', openSettings);
    $('#settingsBack').addEventListener('click', closeSettings);

    $('#refreshBtn').addEventListener('click', () => {
      const button = $('#refreshBtn');
      button.animate(
        [{ transform: 'rotate(0deg)' }, { transform: 'rotate(360deg)' }],
        { duration: 550, easing: 'ease-out' },
      );
      post('refresh');
      if (!host) showToast('正在扫描本地会话…');
    });

    $('#filters').addEventListener('click', (event) => {
      const button = event.target.closest('.filter');
      if (!button) return;
      view.filter = button.dataset.filter;
      render();
    });

    $('#clearDoneBtn').addEventListener('click', () => {
      const count = view.data?.counts?.done ?? 0;
      if (count <= 0) return;
      confirmAction('清理已完成记录？', `将移除 ${count} 条已完成的记录。它们只从这个收件箱消失，Agent 会话本身不受影响。`, '清理', () => {
        post('clearDone');
      });
    });

    listEl.addEventListener('click', handleListClick);
    listEl.addEventListener('keydown', handleKeydown);
    listEl.addEventListener('contextmenu', (event) => {
      const row = event.target.closest('.row');
      if (!row) return;
      event.preventDefault();
      openMenu(event.clientX, event.clientY, row.dataset.key);
    });

    menuEl.addEventListener('click', (event) => {
      const item = event.target.closest('.menu-item');
      if (item) handleMenu(item.dataset.menu);
    });

    document.addEventListener('click', (event) => {
      if (!event.target.closest('#menu')) closeMenu();
    });

    document.addEventListener('keydown', handleKeydown);

    if (host) {
      host.addEventListener('message', (event) => {
        const message = event.data;
        if (!message || typeof message !== 'object') return;
        if (message.type === 'state') {
          view.data = message;
          view.initialized = true;
          render();
        } else if (message.type === 'toast') {
          showToast(message.message, message.kind);
        }
      });
    }

    render();
    post('ready');
  }

  function mockData() {
    const now = Date.now();
    const rows = [
      {
        key: 'dsh:session-demo-0001',
        sourceId: 'dsh',
        sourceName: 'DSH',
        title: '多 Agent 会话监控与收件箱',
        cwd: 'D:\\Workspace\\sample-app',
        state: 'running',
        done: false,
        seen: true,
        isNew: false,
        lastActivityAt: new Date(now - 18000).toISOString(),
        completedAt: null,
        turn: 1,
      },
      {
        key: 'codex:demo-0002',
        sourceId: 'codex',
        sourceName: 'Codex',
        title: '整理日志上报方案',
        cwd: 'D:\\Workspace\\service',
        state: 'completed',
        done: false,
        seen: false,
        isNew: true,
        lastActivityAt: new Date(now - 120000).toISOString(),
        completedAt: new Date(now - 120000).toISOString(),
      },
      {
        key: 'claude:demo-0003',
        sourceId: 'claude',
        sourceName: 'Claude Code',
        title: '修复登录回调的竞态',
        cwd: 'D:\\Workspace\\service',
        state: 'completed',
        done: false,
        seen: true,
        isNew: false,
        lastActivityAt: new Date(now - 1500000).toISOString(),
        completedAt: new Date(now - 1500000).toISOString(),
      },
      {
        key: 'codex:demo-0004',
        sourceId: 'codex',
        sourceName: 'Codex',
        title: '验证本地回归检查',
        cwd: 'D:\\Workspace\\sample-app',
        state: 'completed',
        done: true,
        seen: true,
        isNew: false,
        lastActivityAt: new Date(now - 3600000).toISOString(),
        completedAt: new Date(now - 3600000).toISOString(),
      },
      {
        key: 'dsh:session-demo-0005',
        sourceId: 'dsh',
        sourceName: 'DSH',
        title: '优化角色操作输入',
        cwd: 'D:\\Workspace\\service',
        state: 'completed',
        done: false,
        seen: true,
        isNew: false,
        lastActivityAt: new Date(now - 7200000).toISOString(),
        completedAt: new Date(now - 7200000).toISOString(),
        turn: 20,
      },
      {
        key: 'claude:demo-0006',
        sourceId: 'claude',
        sourceName: 'Claude Code',
        title: '整理 Claude Code 清理功能',
        cwd: 'D:\\Workspace\\sample-app',
        state: 'interrupted',
        done: false,
        seen: true,
        isNew: false,
        lastActivityAt: new Date(now - 86400000).toISOString(),
        completedAt: null,
      },
    ];
    return {
      type: 'state',
      rows,
      counts: { unread: 1, running: 1, done: 1, total: rows.length },
      sources: [
        { id: 'dsh', name: 'DSH', enabled: true, available: true, root: 'C:\\Users\\demo\\.dsh', count: 42, lastScanMs: 19, error: null },
        { id: 'codex', name: 'Codex', enabled: true, available: true, root: 'C:\\Users\\demo\\.codex', count: 18, lastScanMs: 18, error: null },
        { id: 'claude', name: 'Claude Code', enabled: true, available: true, root: 'C:\\Users\\demo\\.claude', count: 6, lastScanMs: 1, error: null },
      ],
      settings: {
        enableDsh: true,
        enableCodex: true,
        enableClaude: true,
        dshRoot: '',
        codexRoot: '',
        claudeRoot: '',
        lookbackDays: 3,
        scanIntervalSeconds: 4,
        notifyOnComplete: true,
        alwaysOnTop: true,
        startWithWindows: false,
      },
      defaults: {
        dshRoot: 'C:\\Users\\demo\\.dsh',
        codexRoot: 'C:\\Users\\demo\\.codex',
        claudeRoot: 'C:\\Users\\demo\\.claude',
      },
      scanning: false,
      lastScanAt: new Date(now - 6000).toISOString(),
      version: '0.1.0',
      appDataPath: 'C:\\Users\\demo\\AppData\\Roaming\\AgentInbox',
    };
  }

  if (!host) {
    view.data = mockData();
    view.initialized = true;
    // Preview-only interactions so the UI can be reviewed in a plain browser.
    const mockMutate = (type, payload) => {
      const data = view.data;
      const row = data.rows.find((item) => item.key === payload?.key);
      if (type === 'setDone' && row) {
        row.done = payload.done;
        row.seen = true;
        data.counts.done = data.rows.filter((item) => item.done).length;
      } else if (type === 'clearDone') {
        data.rows = data.rows.filter((item) => !item.done);
        data.counts.done = 0;
        data.counts.total = data.rows.length;
      } else if (type === 'clearAll') {
        data.rows = [];
        data.counts = { unread: 0, running: 0, done: 0, total: 0 };
      } else if (type === 'toggleTopmost') {
        data.settings.alwaysOnTop = !data.settings.alwaysOnTop;
      } else if (type === 'focus') {
        showToast(`已切换到 ${row?.sourceName ?? '应用'}：${(row?.title ?? '').slice(0, 24)}…`);
      }
      data.counts.unread = data.rows.filter((item) => item.isNew).length;
      data.counts.running = data.rows.filter((item) => item.state === 'running').length;
      render();
    };
    post = mockMutate;
    setTimeout(() => showToast('预览模式：未连接到桌面宿主'), 700);
  }

  bindUi();
})();
