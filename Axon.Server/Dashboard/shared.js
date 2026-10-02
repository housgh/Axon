// Shared between index.html and job.html - keep both in sync with this file rather than
// copy-pasting, since the two pages' copies previously drifted (job.html's STATE_NAMES/
// STATE_CLASSES were missing the "Awaiting parent"/"Skipped" states added to index.html's).

const STATE_NAMES = ['Enqueued', 'Scheduled', 'Processing', 'Succeeded', 'Failed', 'Awaiting parent', 'Skipped'];
const STATE_CLASSES = ['enqueued', 'scheduled', 'processing', 'succeeded', 'failed', 'awaiting-parent', 'skipped'];
// JobPriority's enum order is Medium=0, Low=1, High=2, Critical=3 (Medium first so
// default(JobPriority) is Medium server-side) - these arrays must stay index-aligned with it.
const PRIORITY_NAMES = ['Medium', 'Low', 'High', 'Critical'];
const PRIORITY_CLASSES = ['priority-medium', 'priority-low', 'priority-high', 'priority-critical'];

function escapeHtml(str) {
  return String(str).replace(/[&<>"']/g, function (c) {
    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
  });
}

function ticksToDate(ticks) {
  if (ticks === null || ticks === undefined) return null;
  var ticksSinceEpoch = ticks - 621355968000000000;
  return new Date(ticksSinceEpoch / 10000);
}

function fmtDate(ticks) {
  const d = ticksToDate(ticks);
  if (!d) return '—';
  return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function relativeTime(ticks) {
  const d = ticksToDate(ticks);
  if (!d) return '';
  const diffMs = d.getTime() - Date.now();
  const abs = Math.abs(diffMs);
  const mins = Math.round(abs / 60000);
  let label;
  if (mins < 1) label = 'moments';
  else if (mins < 60) label = mins + 'm';
  else if (mins < 1440) label = Math.round(mins / 60) + 'h';
  else label = Math.round(mins / 1440) + 'd';
  return diffMs < 0 ? label + ' ago' : 'in ' + label;
}

async function apiFetch(url, options) {
  const res = await fetch(url, options);
  if (res.status === 401) {
    window.location.href = '/axon/login?returnUrl=' + encodeURIComponent(window.location.pathname);
    throw new Error('Not authenticated');
  }
  if (!res.ok && res.status !== 204) {
    throw new Error('Request failed (' + res.status + ')');
  }
  return res;
}
