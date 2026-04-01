namespace ArrowThing.Server;

public static class AdminDashboardHtml
{
    public const string Page = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Arrow Thing — Admin Dashboard</title>
<style>
  :root {
    --bg: #0f1117;
    --surface: #1a1d27;
    --border: #2a2d3a;
    --text: #e0e0e8;
    --muted: #8888a0;
    --accent: #00e5cc;
    --accent-dim: #00b3a0;
    --danger: #ff5555;
    --warn: #ffaa33;
  }
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body {
    font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
    background: var(--bg);
    color: var(--text);
    font-size: 14px;
    line-height: 1.5;
  }
  #auth-gate {
    display: flex;
    align-items: center;
    justify-content: center;
    min-height: 100vh;
    flex-direction: column;
    gap: 16px;
  }
  #auth-gate h1 { color: var(--accent); font-size: 18px; }
  #auth-gate input {
    background: var(--surface);
    border: 1px solid var(--border);
    color: var(--text);
    padding: 10px 16px;
    border-radius: 6px;
    font-family: inherit;
    font-size: 14px;
    width: 320px;
  }
  #auth-gate button {
    background: var(--accent);
    color: var(--bg);
    border: none;
    padding: 10px 24px;
    border-radius: 6px;
    cursor: pointer;
    font-family: inherit;
    font-weight: 600;
    font-size: 14px;
  }
  #auth-gate button:hover { background: var(--accent-dim); }
  #auth-error { color: var(--danger); font-size: 13px; min-height: 20px; }

  #app { display: none; }
  header {
    background: var(--surface);
    border-bottom: 1px solid var(--border);
    padding: 12px 24px;
    display: flex;
    align-items: center;
    justify-content: space-between;
  }
  header h1 { font-size: 16px; color: var(--accent); }
  header .server-time { color: var(--muted); font-size: 12px; }

  nav {
    display: flex;
    gap: 0;
    background: var(--surface);
    border-bottom: 1px solid var(--border);
  }
  nav button {
    background: none;
    border: none;
    color: var(--muted);
    padding: 10px 20px;
    cursor: pointer;
    font-family: inherit;
    font-size: 13px;
    border-bottom: 2px solid transparent;
  }
  nav button.active {
    color: var(--accent);
    border-bottom-color: var(--accent);
  }
  nav button:hover { color: var(--text); }

  .content { padding: 24px; max-width: 1200px; margin: 0 auto; }

  .stats-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
    gap: 16px;
    margin-bottom: 24px;
  }
  .stat-card {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 16px;
  }
  .stat-card .label { color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: 0.5px; }
  .stat-card .value { font-size: 28px; font-weight: 700; color: var(--accent); margin-top: 4px; }
  .stat-card .sub { color: var(--muted); font-size: 12px; margin-top: 2px; }
  .stat-card.warn .value { color: var(--warn); }
  .stat-card.danger .value { color: var(--danger); }

  .panel {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 8px;
    overflow: hidden;
  }
  .panel-header {
    padding: 12px 16px;
    border-bottom: 1px solid var(--border);
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    flex-wrap: wrap;
  }
  .panel-header h2 { font-size: 14px; white-space: nowrap; }
  .controls { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  .controls input, .controls select {
    background: var(--bg);
    border: 1px solid var(--border);
    color: var(--text);
    padding: 6px 10px;
    border-radius: 4px;
    font-family: inherit;
    font-size: 13px;
  }
  .controls select { cursor: pointer; }
  .controls button {
    background: var(--border);
    border: none;
    color: var(--text);
    padding: 6px 12px;
    border-radius: 4px;
    cursor: pointer;
    font-family: inherit;
    font-size: 13px;
  }
  .controls button:hover { background: var(--muted); }

  table { width: 100%; border-collapse: collapse; }
  th {
    text-align: left;
    padding: 8px 12px;
    background: var(--bg);
    color: var(--muted);
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.5px;
    font-weight: 600;
    position: sticky;
    top: 0;
  }
  td {
    padding: 8px 12px;
    border-top: 1px solid var(--border);
    font-size: 13px;
    white-space: nowrap;
  }
  tr:hover td { background: rgba(0, 229, 204, 0.03); }
  .clickable { cursor: pointer; }
  .clickable:hover td { background: rgba(0, 229, 204, 0.08); }

  .badge {
    display: inline-block;
    padding: 2px 8px;
    border-radius: 10px;
    font-size: 11px;
    font-weight: 600;
  }
  .badge-ok { background: rgba(0, 229, 204, 0.15); color: var(--accent); }
  .badge-warn { background: rgba(255, 170, 51, 0.15); color: var(--warn); }
  .badge-danger { background: rgba(255, 85, 85, 0.15); color: var(--danger); }
  .badge-muted { background: rgba(136, 136, 160, 0.15); color: var(--muted); }

  .pagination {
    padding: 12px 16px;
    border-top: 1px solid var(--border);
    display: flex;
    align-items: center;
    justify-content: space-between;
    font-size: 12px;
    color: var(--muted);
  }
  .pagination button {
    background: var(--border);
    border: none;
    color: var(--text);
    padding: 4px 12px;
    border-radius: 4px;
    cursor: pointer;
    font-family: inherit;
    font-size: 12px;
  }
  .pagination button:disabled { opacity: 0.3; cursor: not-allowed; }

  .table-scroll { max-height: 600px; overflow-y: auto; }

  .user-detail { display: none; }
  .user-detail .back {
    color: var(--accent);
    cursor: pointer;
    font-size: 13px;
    margin-bottom: 16px;
    display: inline-block;
  }
  .user-detail .back:hover { text-decoration: underline; }
  .user-info {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
    gap: 12px;
    margin-bottom: 24px;
  }
  .user-info .field { background: var(--surface); border: 1px solid var(--border); border-radius: 6px; padding: 12px; }
  .user-info .field .label { color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: 0.5px; }
  .user-info .field .val { margin-top: 4px; word-break: break-all; }

  .loading { color: var(--muted); padding: 24px; text-align: center; }
  .empty { color: var(--muted); padding: 24px; text-align: center; font-style: italic; }

  @media (max-width: 600px) {
    .content { padding: 12px; }
    .stats-grid { grid-template-columns: repeat(2, 1fr); }
    td, th { padding: 6px 8px; }
  }
</style>
</head>
<body>

<div id="auth-gate">
  <h1>Arrow Thing Admin</h1>
  <input type="password" id="key-input" placeholder="Admin API key" autofocus>
  <button onclick="authenticate()">Authenticate</button>
  <div id="auth-error"></div>
</div>

<div id="app">
  <header>
    <h1>Arrow Thing Admin</h1>
    <span class="server-time" id="server-time"></span>
  </header>
  <nav>
    <button class="active" onclick="showTab('overview')">Overview</button>
    <button onclick="showTab('users')">Users</button>
    <button onclick="showTab('audit')">Audit Log</button>
  </nav>
  <div class="content">

    <!-- Overview tab -->
    <div id="tab-overview">
      <div class="stats-grid" id="stats-grid"></div>
      <div class="panel">
        <div class="panel-header"><h2>Recent Activity</h2></div>
        <div class="table-scroll">
          <table><thead><tr>
            <th>Time</th><th>Event</th><th>Email</th><th>IP</th><th>Detail</th>
          </tr></thead><tbody id="recent-logs"></tbody></table>
        </div>
      </div>
    </div>

    <!-- Users tab -->
    <div id="tab-users" style="display:none">
      <div id="users-list">
        <div class="panel">
          <div class="panel-header">
            <h2>Users</h2>
            <div class="controls">
              <input type="text" id="user-search" placeholder="Search email or name...">
              <button onclick="loadUsers(1)">Search</button>
            </div>
          </div>
          <div class="table-scroll">
            <table><thead><tr>
              <th>Display Name</th><th>Email</th><th>Created</th><th>Status</th>
            </tr></thead><tbody id="users-tbody"></tbody></table>
          </div>
          <div class="pagination" id="users-pagination"></div>
        </div>
      </div>
      <div class="user-detail" id="user-detail">
        <span class="back" onclick="hideUserDetail()">&larr; Back to Users</span>
        <div class="user-info" id="user-info"></div>
        <div class="panel">
          <div class="panel-header"><h2>User Audit Log</h2></div>
          <div class="table-scroll">
            <table><thead><tr>
              <th>Time</th><th>Event</th><th>IP</th><th>Detail</th>
            </tr></thead><tbody id="user-logs"></tbody></table>
          </div>
        </div>
      </div>
    </div>

    <!-- Audit Log tab -->
    <div id="tab-audit" style="display:none">
      <div class="panel">
        <div class="panel-header">
          <h2>Audit Log</h2>
          <div class="controls">
            <input type="text" id="audit-search" placeholder="Search email...">
            <select id="audit-event-filter">
              <option value="">All Events</option>
              <option value="register">register</option>
              <option value="login">login</option>
              <option value="login_failed">login_failed</option>
              <option value="verify_email">verify_email</option>
              <option value="change_password">change_password</option>
              <option value="forgot_password">forgot_password</option>
              <option value="reset_password">reset_password</option>
              <option value="change_email">change_email</option>
              <option value="confirm_email_change">confirm_email_change</option>
              <option value="update_display_name">update_display_name</option>
              <option value="lock_account">lock_account</option>
              <option value="unlock_account">unlock_account</option>
              <option value="session_invalidated">session_invalidated</option>
              <option value="resend_verification">resend_verification</option>
            </select>
            <button onclick="loadAuditLog(1)">Filter</button>
          </div>
        </div>
        <div class="table-scroll">
          <table><thead><tr>
            <th>Time</th><th>Event</th><th>Email</th><th>IP</th><th>Detail</th>
          </tr></thead><tbody id="audit-tbody"></tbody></table>
        </div>
        <div class="pagination" id="audit-pagination"></div>
      </div>
    </div>

  </div>
</div>

<script>
let apiKey = '';
const baseUrl = window.location.origin;

function authenticate() {
  apiKey = document.getElementById('key-input').value.trim();
  if (!apiKey) return;
  apiFetch('/api/admin/stats').then(data => {
    if (data.error) {
      document.getElementById('auth-error').textContent = 'Invalid key.';
      apiKey = '';
      return;
    }
    document.getElementById('auth-gate').style.display = 'none';
    document.getElementById('app').style.display = 'block';
    renderStats(data);
    loadRecentActivity();
  }).catch(() => {
    document.getElementById('auth-error').textContent = 'Connection failed.';
    apiKey = '';
  });
}

document.getElementById('key-input').addEventListener('keydown', e => {
  if (e.key === 'Enter') authenticate();
});

async function apiFetch(path, params) {
  const url = new URL(baseUrl + path);
  if (params) Object.entries(params).forEach(([k, v]) => { if (v != null && v !== '') url.searchParams.set(k, v); });
  const res = await fetch(url, { headers: { 'X-Admin-Key': apiKey } });
  return res.json();
}

function fmt(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  return d.toISOString().replace('T', ' ').replace(/\.\d+Z/, ' UTC');
}

function fmtShort(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  const now = new Date();
  const diff = now - d;
  if (diff < 60000) return Math.floor(diff/1000) + 's ago';
  if (diff < 3600000) return Math.floor(diff/60000) + 'm ago';
  if (diff < 86400000) return Math.floor(diff/3600000) + 'h ago';
  return Math.floor(diff/86400000) + 'd ago';
}

function eventBadge(event) {
  const cls = event === 'login' ? 'badge-ok'
    : event === 'login_failed' ? 'badge-danger'
    : event === 'lock_account' ? 'badge-danger'
    : event === 'register' ? 'badge-ok'
    : event === 'session_invalidated' ? 'badge-warn'
    : 'badge-muted';
  return `<span class="badge ${cls}">${event}</span>`;
}

function statusBadge(user) {
  if (user.lockedAt) return '<span class="badge badge-danger">Locked</span>';
  if (user.emailVerifiedAt) return '<span class="badge badge-ok">Verified</span>';
  return '<span class="badge badge-warn">Unverified</span>';
}

function renderStats(data) {
  document.getElementById('server-time').textContent = 'Server: ' + fmt(data.serverTime);
  const g = document.getElementById('stats-grid');
  g.innerHTML = `
    <div class="stat-card"><div class="label">Total Users</div><div class="value">${data.totalUsers}</div><div class="sub">${data.verifiedUsers} verified</div></div>
    <div class="stat-card"><div class="label">Registrations</div><div class="value">${data.registrations24h}</div><div class="sub">${data.registrations7d} last 7d</div></div>
    <div class="stat-card"><div class="label">Logins (24h)</div><div class="value">${data.logins24h}</div><div class="sub">${data.logins7d} last 7d</div></div>
    <div class="stat-card ${data.failedLogins24h > 10 ? 'danger' : data.failedLogins24h > 0 ? 'warn' : ''}"><div class="label">Failed Logins (24h)</div><div class="value">${data.failedLogins24h}</div></div>
    <div class="stat-card ${data.lockedUsers > 0 ? 'danger' : ''}"><div class="label">Locked Accounts</div><div class="value">${data.lockedUsers}</div></div>
  `;
}

async function loadRecentActivity() {
  const data = await apiFetch('/api/admin/audit-log', { page: 1 });
  const tbody = document.getElementById('recent-logs');
  if (!data.logs || data.logs.length === 0) {
    tbody.innerHTML = '<tr><td colspan="5" class="empty">No activity yet</td></tr>';
    return;
  }
  tbody.innerHTML = data.logs.slice(0, 25).map(l => `<tr>
    <td title="${fmt(l.timestamp)}">${fmtShort(l.timestamp)}</td>
    <td>${eventBadge(l.event)}</td>
    <td>${l.email || '—'}</td>
    <td>${l.ipAddress || '—'}</td>
    <td>${l.detail || '—'}</td>
  </tr>`).join('');
}

let usersPage = 1;
async function loadUsers(page) {
  usersPage = page || 1;
  const search = document.getElementById('user-search').value;
  const data = await apiFetch('/api/admin/users', { page: usersPage, search });
  const tbody = document.getElementById('users-tbody');
  if (!data.users || data.users.length === 0) {
    tbody.innerHTML = '<tr><td colspan="4" class="empty">No users found</td></tr>';
  } else {
    tbody.innerHTML = data.users.map(u => `<tr class="clickable" onclick="showUser('${u.id}')">
      <td>${esc(u.displayName)}</td>
      <td>${esc(u.email)}</td>
      <td title="${fmt(u.createdAt)}">${fmtShort(u.createdAt)}</td>
      <td>${statusBadge(u)}</td>
    </tr>`).join('');
  }
  const totalPages = Math.ceil(data.total / data.pageSize);
  document.getElementById('users-pagination').innerHTML = `
    <span>${data.total} users (page ${data.page}/${totalPages || 1})</span>
    <div>
      <button ${data.page <= 1 ? 'disabled' : ''} onclick="loadUsers(${data.page - 1})">Prev</button>
      <button ${data.page >= totalPages ? 'disabled' : ''} onclick="loadUsers(${data.page + 1})">Next</button>
    </div>
  `;
}

async function showUser(id) {
  const data = await apiFetch('/api/admin/users/' + id);
  if (data.error) return;
  document.getElementById('users-list').style.display = 'none';
  const det = document.getElementById('user-detail');
  det.style.display = 'block';

  const u = data.user;
  document.getElementById('user-info').innerHTML = `
    <div class="field"><div class="label">ID</div><div class="val">${u.id}</div></div>
    <div class="field"><div class="label">Email</div><div class="val">${esc(u.email)}</div></div>
    <div class="field"><div class="label">Display Name</div><div class="val">${esc(u.displayName)}</div></div>
    <div class="field"><div class="label">Created</div><div class="val">${fmt(u.createdAt)}</div></div>
    <div class="field"><div class="label">Verified</div><div class="val">${u.emailVerifiedAt ? fmt(u.emailVerifiedAt) : '<span class="badge badge-warn">No</span>'}</div></div>
    <div class="field"><div class="label">Status</div><div class="val">${u.lockedAt ? '<span class="badge badge-danger">Locked</span> ' + fmt(u.lockedAt) : '<span class="badge badge-ok">Active</span>'}</div></div>
    ${u.hasPendingEmailChange ? '<div class="field"><div class="label">Pending</div><div class="val"><span class="badge badge-warn">Email change pending</span></div></div>' : ''}
  `;

  const tbody = document.getElementById('user-logs');
  if (!data.recentLogs || data.recentLogs.length === 0) {
    tbody.innerHTML = '<tr><td colspan="4" class="empty">No activity</td></tr>';
  } else {
    tbody.innerHTML = data.recentLogs.map(l => `<tr>
      <td title="${fmt(l.timestamp)}">${fmtShort(l.timestamp)}</td>
      <td>${eventBadge(l.event)}</td>
      <td>${l.ipAddress || '—'}</td>
      <td>${l.detail || '—'}</td>
    </tr>`).join('');
  }
}

function hideUserDetail() {
  document.getElementById('user-detail').style.display = 'none';
  document.getElementById('users-list').style.display = 'block';
}

let auditPage = 1;
async function loadAuditLog(page) {
  auditPage = page || 1;
  const eventType = document.getElementById('audit-event-filter').value;
  const search = document.getElementById('audit-search').value;
  const data = await apiFetch('/api/admin/audit-log', { page: auditPage, eventType, search });
  const tbody = document.getElementById('audit-tbody');
  if (!data.logs || data.logs.length === 0) {
    tbody.innerHTML = '<tr><td colspan="5" class="empty">No logs found</td></tr>';
  } else {
    tbody.innerHTML = data.logs.map(l => `<tr>
      <td title="${fmt(l.timestamp)}">${fmtShort(l.timestamp)}</td>
      <td>${eventBadge(l.event)}</td>
      <td>${l.email || '—'}</td>
      <td>${l.ipAddress || '—'}</td>
      <td>${l.detail || '—'}</td>
    </tr>`).join('');
  }
  const totalPages = Math.ceil(data.total / data.pageSize);
  document.getElementById('audit-pagination').innerHTML = `
    <span>${data.total} entries (page ${data.page}/${totalPages || 1})</span>
    <div>
      <button ${data.page <= 1 ? 'disabled' : ''} onclick="loadAuditLog(${data.page - 1})">Prev</button>
      <button ${data.page >= totalPages ? 'disabled' : ''} onclick="loadAuditLog(${data.page + 1})">Next</button>
    </div>
  `;
}

function showTab(name) {
  document.querySelectorAll('nav button').forEach(b => b.classList.remove('active'));
  event.target.classList.add('active');
  document.getElementById('tab-overview').style.display = name === 'overview' ? '' : 'none';
  document.getElementById('tab-users').style.display = name === 'users' ? '' : 'none';
  document.getElementById('tab-audit').style.display = name === 'audit' ? '' : 'none';

  if (name === 'overview') {
    apiFetch('/api/admin/stats').then(renderStats);
    loadRecentActivity();
  }
  if (name === 'users') loadUsers(1);
  if (name === 'audit') loadAuditLog(1);
}

document.getElementById('user-search').addEventListener('keydown', e => {
  if (e.key === 'Enter') loadUsers(1);
});
document.getElementById('audit-search').addEventListener('keydown', e => {
  if (e.key === 'Enter') loadAuditLog(1);
});

function esc(s) {
  const d = document.createElement('div');
  d.textContent = s || '';
  return d.innerHTML;
}
</script>
</body>
</html>
""";
}
