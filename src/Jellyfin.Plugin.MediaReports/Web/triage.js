// Media Reports page in the Dashboard: the Reports tab, one issue, and the Settings tab.
// The tab, the issue and the list's view, grouping, filters, sort and page live in the
// page's URL, so Back returns to the list as it was.

const PAGE = 'mediareports';
const API = 'MediaReports/Admin/';

const STATUSES = [
    { code: 'New', label: 'New', bg: 'rgba(0,164,220,.18)', fg: '#4fc3ec' },
    { code: 'Investigating', label: 'Investigating', bg: 'rgba(242,176,30,.18)', fg: '#f5c451' },
    { code: 'ReplacedVerify', label: 'Replaced – verify', bg: 'rgba(179,136,255,.18)', fg: '#c7a8ff' },
    { code: 'Fixed', label: 'Fixed', bg: 'rgba(102,187,106,.18)', fg: '#8fd193' },
    { code: 'WontFix', label: "Won't fix", bg: 'rgba(255,255,255,.1)', fg: 'rgba(255,255,255,.75)' },
    { code: 'Duplicate', label: 'Duplicate', bg: 'rgba(255,255,255,.1)', fg: 'rgba(255,255,255,.75)' },
    { code: 'CantReproduce', label: "Can't reproduce", bg: 'rgba(255,255,255,.1)', fg: 'rgba(255,255,255,.75)' }
];
const MIXED = { code: 'Mixed', label: 'Mixed', bg: 'rgba(255,255,255,.1)', fg: 'rgba(255,255,255,.75)' };

const LIST_KEYS = ['view', 'groupBy', 'reason', 'client', 'q', 'sort', 'page'];
const DEFAULTS = { view: 'open', groupBy: 'title', sort: 'last', page: '1' };

const STYLES = `
.mediaReportsTriage{--mr-text:rgba(255,255,255,.87);--mr-muted:rgba(255,255,255,.7);--mr-faint:rgba(255,255,255,.6);--mr-card:#181818;--mr-accent:#00a4dc;
  color:var(--mr-text);font-size:15px;display:flex;flex-direction:column;padding-bottom:48px}
.mediaReportsTriage>*+*{margin-top:20px}
.mediaReportsTriage a{color:var(--mr-accent)}
.mediaReportsTriage a:hover{color:#4fc3ec}
.mrHead{display:flex;flex-wrap:wrap;align-items:baseline}
.mrHead h1{margin:0 24px 0 0;font-size:30px;font-weight:400;color:#fff}
.mrMuted{color:var(--mr-muted)}
.mrFaint{color:var(--mr-faint);font-size:13px}
.mrTabs{display:flex;flex-wrap:wrap;border-bottom:1px solid rgba(255,255,255,.12)}
.mrTab{height:48px;display:inline-flex;align-items:center;padding:0 16px;color:var(--mr-muted) !important;text-decoration:none;font-weight:600;font-size:14px;border-bottom:2px solid transparent}
.mrTab:hover{color:#fff !important}
.mrTab[aria-current=page]{color:#fff !important;border-bottom-color:var(--mr-accent)}
.mrTab .mrCount{margin-left:8px;color:var(--mr-muted);font-weight:400}
.mrTabs+.mrControls{margin-top:18px}
.mrControls{display:flex;flex-wrap:wrap;align-items:center;margin:-6px -10px}
.mrControls>*{margin:6px 10px}
.mrSegs{display:flex}
.mrSeg{height:36px;padding:0 14px;border:1px solid rgba(255,255,255,.24);background:transparent;color:var(--mr-text);font:inherit;font-size:14px;cursor:pointer;margin-left:-1px}
.mrSeg:first-child{border-radius:4px 0 0 4px;margin-left:0}
.mrSeg:last-child{border-radius:0 4px 4px 0}
.mrSeg[aria-pressed=true]{background:rgba(0,164,220,.2);border-color:var(--mr-accent);color:#fff;font-weight:600;position:relative}
.mrLabel{display:flex;align-items:center;font-size:14px;color:var(--mr-muted)}
.mrLabel select{margin-left:8px}
.mrSelect{height:36px;background:#2a2a2a;color:#fff;border:1px solid rgba(255,255,255,.18);border-radius:4px;font:inherit;font-size:14px;padding:0 8px}
.mrSearch{flex:1 1 200px;display:flex;align-items:center;height:36px;padding:0 10px;background:rgba(255,255,255,.09);border-radius:4px}
.mrSearch input{flex:1;min-width:0;background:none;border:0;color:#fff;font:inherit;font-size:14px;outline:none;margin-left:8px}
.mrSearch:focus-within{outline:2px solid var(--mr-accent)}
.mrIcon{font-family:'Material Icons';font-weight:normal;font-style:normal;font-size:20px;line-height:1;display:inline-block;-webkit-font-smoothing:antialiased;vertical-align:middle}
.mrTableBox{overflow-x:auto;background:var(--mr-card);border-radius:8px}
.mrTable{width:100%;border-collapse:collapse;min-width:860px}
.mrTable th{font-size:12px;font-weight:600;color:var(--mr-faint);text-align:left;padding:10px 12px;border-bottom:1px solid rgba(255,255,255,.12);white-space:nowrap}
.mrTable th button{background:none;border:0;padding:0;font:inherit;color:inherit;cursor:pointer}
.mrTable th button:hover,.mrTable th[aria-sort] button{color:#fff}
.mrTable td{padding:12px;border-bottom:1px solid rgba(255,255,255,.07);vertical-align:middle;font-size:14px}
.mrTable tbody tr:hover{background:rgba(255,255,255,.04)}
.mrTable tr.mrChild td:first-child{padding-left:44px}
.mrTitleCell{display:flex;align-items:center}
.mrTitleCell>*+*{margin-left:10px}
.mrDot{display:block;width:8px;height:8px;border-radius:4px;background:var(--mr-accent)}
.mrDotSlot{width:8px;flex-shrink:0}
.mrPoster{width:32px;height:46px;border-radius:3px;background:#2b3a44 center/cover no-repeat;flex-shrink:0}
.mrTitleText{display:flex;flex-direction:column}
.mrTitleLink{color:#fff !important;text-decoration:none;font-weight:600;background:none;border:0;padding:0;font:inherit;font-weight:600;text-align:left;cursor:pointer}
.mrTitleLink:hover{color:#4fc3ec !important;text-decoration:underline}
.mrLine{color:var(--mr-faint);font-size:12px}
.mrPill{display:inline-flex;align-items:center;height:24px;padding:0 10px;border-radius:12px;font-size:12px;font-weight:600;white-space:nowrap}
.mrPaging{display:flex;align-items:center;justify-content:flex-end;flex-wrap:wrap}
.mrPaging>*+*{margin-left:12px}
.mrBtn{height:36px;border-radius:4px;border:0;font:inherit;font-size:14px;font-weight:600;cursor:pointer;padding:0 14px;display:inline-flex;align-items:center;text-decoration:none;background:#424242;color:#fff !important}
.mrBtn:hover{background:#505050}
.mrBtn:disabled{opacity:.4;cursor:default}
.mrBtn .mrIcon{margin-right:8px;font-size:18px}
.mrPrimary{background:var(--mr-accent);color:rgba(0,0,0,.87) !important}
.mrPrimary:hover{background:#4fc3ec}
.mrPanel{background:var(--mr-card);border-radius:8px;padding:32px}
.mrPanel h2{margin:0 0 12px;font-size:24px;font-weight:400;color:#fff}
.mrPanel p{margin:0 0 14px;color:var(--mr-muted);line-height:1.6}
.mrAlert{display:flex;align-items:flex-start}
.mrAlert>.mrIcon{color:#ffb4ab;margin-right:16px;font-size:24px}
.mrAlert h2{font-size:18px;font-weight:600}
.mrButtonStopped>.mrIcon{color:#f5c451}
.mrButtonStopped p{margin:2px 0 0}
.mrEmpty{text-align:center}
.mrSkeleton{display:block;background:#242424;border-radius:4px;height:14px}
.mrActions{display:flex;flex-wrap:wrap;margin:-4px}
.mrActions>*{margin:4px}
.mrBack{display:inline-flex;align-items:center;font-size:14px;text-decoration:none;align-self:flex-start}
.mediaReportsIssue>*+*{margin-top:24px}
.mediaReportsIssue>.mrIssueHead{margin-top:6px}
.mrIssueHead{display:flex;flex-wrap:wrap;align-items:flex-start;margin:-10px}
.mrIssueHead>*{margin:10px}
.mrIssueHead .mrPoster{width:96px;height:144px;border-radius:4px}
.mrIssueTitle{flex:1 1 360px;min-width:0;display:flex;flex-direction:column}
.mrIssueTitle>*+*{margin-top:10px}
.mrIssueTitle h1{margin:0;font-size:30px;font-weight:400;color:#fff}
.mrIssueTitle p{margin:0}
.mrOk{color:#8fd193;font-size:13px}
.mrBad{color:#ffb4ab;font-size:13px}
.mrSection{display:flex;flex-direction:column;min-width:0}
.mrSection>*+*{margin-top:12px}
.mrTriage{display:grid;grid-template-columns:minmax(220px,1fr) 2fr;grid-gap:20px;align-items:start}
.mrCard.mrTriage>*+*{margin-top:0}
.mrField{display:flex;flex-direction:column}
.mrField>*+*{margin-top:6px}
.mrField select{height:44px;font-size:15px;width:100%}
.mrField .mrBtn{align-self:flex-start}
.mrColumns{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);grid-gap:20px;align-items:start}
@media (max-width:900px){.mrColumns,.mrTriage{grid-template-columns:minmax(0,1fr)}}
.mrCard{background:var(--mr-card);border-radius:8px;padding:20px}
.mrCard>*+*{margin-top:12px}
.mrH3{margin:0;font-size:13px;font-weight:600;letter-spacing:.06em;text-transform:uppercase;color:var(--mr-muted)}
.mrSpread{display:flex;flex-wrap:wrap;justify-content:space-between}
.mrTags{display:flex;flex-wrap:wrap;margin:-3px}
.mrTag{display:inline-flex;align-items:center;height:24px;padding:0 8px;border-radius:4px;background:#2a2a2a;font-size:12px;margin:3px}
.mrLinkBtn{align-self:flex-start;background:none;border:0;color:var(--mr-accent);font:inherit;font-size:14px;font-weight:600;cursor:pointer;padding:6px 0;display:inline-flex;align-items:center}
.mrDl{margin:0;display:grid;grid-template-columns:140px minmax(0,1fr);grid-gap:8px 16px;font-size:14px}
.mrDl dt{color:var(--mr-faint)}
.mrDl dd{margin:0;overflow-wrap:anywhere}
.mrDetails{padding:12px;background:#141414;border-radius:4px}
.mrMono{font-family:monospace;font-size:13px}
.mrHistory{list-style:none;margin:0;padding:0;font-size:14px}
.mrHistory li{display:flex}
.mrHistory li+li{margin-top:12px}
.mrHistory li>span:first-child{width:8px;height:8px;border-radius:4px;background:rgba(255,255,255,.4);margin:6px 12px 0 0;flex-shrink:0}
.mrHistory li>span:nth-child(2){flex:1}
.mrHistory time{color:var(--mr-faint);font-size:12px;white-space:nowrap;margin-left:12px}
.mrSettings>*+*{margin-top:20px}
.mrReasons{list-style:none;margin:0;padding:0}
.mrReason{display:flex;flex-wrap:wrap;align-items:center;padding:8px 0;border-bottom:1px solid rgba(255,255,255,.07)}
.mrReason>*{margin:4px 12px 4px 0}
.mrReason input{flex:1 1 220px;min-width:0}
.mrReason .mrUses{flex:0 0 150px;font-size:13px;color:var(--mr-faint)}
.mrReason .mrBad{flex-basis:100%;margin-top:0}
.mrRetired input{color:var(--mr-faint)}
.mrIconBtn{width:36px;height:36px;border-radius:18px;border:0;background:none;color:var(--mr-text);cursor:pointer;display:inline-flex;align-items:center;justify-content:center;padding:0}
.mrIconBtn:hover{background:rgba(255,255,255,.1)}
.mrIconBtn:disabled{opacity:.3;cursor:default;background:none}
.mrInput{height:40px;box-sizing:border-box;background:rgba(255,255,255,.08);color:#fff;border:1px solid rgba(255,255,255,.3);border-radius:4px;padding:0 10px;font:inherit;font-size:15px}
.mrInput:focus{outline:2px solid var(--mr-accent);outline-offset:-1px}
.mrInput[aria-invalid=true]{border-color:#ffb4ab}
.mrDays{width:90px;margin:0 8px}
.mrHook{display:flex;flex-direction:column}
.mrHook>*+*{margin-top:6px}
.mrHook label{font-size:14px;color:var(--mr-muted)}
.mrHook .mrInput{width:100%}
.mrHook .mrOk,.mrHook .mrBad{margin:0}
.mrMetrics{display:flex;flex-direction:column;border-top:1px solid rgba(255,255,255,.12);padding-top:16px}
.mrMetrics>*+*{margin-top:8px}
.mrCopyRow{display:flex;flex-wrap:wrap;align-items:center;margin:-4px}
.mrCopyRow>*{margin:4px}
.mrCopyRow .mrInput{flex:1 1 260px;min-width:0}
.mrScrape{margin:0;padding:12px;background:#141414;border-radius:4px;font-family:monospace;font-size:13px;white-space:pre;overflow-x:auto;max-width:100%;box-sizing:border-box}
.mrCheck{display:flex;align-items:flex-start;font-size:15px;color:#fff;cursor:pointer}
.mrCheck input{width:20px;height:20px;margin:1px 12px 0 0;flex-shrink:0;accent-color:var(--mr-accent)}
.mrSave{display:flex;flex-wrap:wrap;align-items:center}
.mrSave>*+*{margin-left:16px}
.mrNotes{width:100%;box-sizing:border-box;min-height:96px;background:rgba(255,255,255,.08);color:#fff;border:1px solid rgba(255,255,255,.3);border-radius:4px;padding:8px;font:inherit;font-size:14px;resize:vertical}
@media (max-width:600px){.mrDl{grid-template-columns:110px minmax(0,1fr)}.mrHead h1,.mrIssueTitle h1{font-size:24px}}
`;

// ---- Small helpers -------------------------------------------------------------------

function h(tag, attrs, ...children) {
    const element = document.createElement(tag);
    for (const [key, value] of Object.entries(attrs || {})) {
        if (value === null || value === undefined || value === false) {
            continue;
        }
        if (key.startsWith('on')) {
            element.addEventListener(key.slice(2), value);
        } else if (key === 'className') {
            element.className = value;
        } else if (key === 'style') {
            element.setAttribute('style', value);
        } else {
            element.setAttribute(key, value === true ? '' : String(value));
        }
    }
    for (const child of children.flat(Infinity)) {
        if (child === null || child === undefined || child === false) {
            continue;
        }
        element.appendChild(typeof child === 'object' ? child : document.createTextNode(String(child)));
    }
    return element;
}

function icon(name) {
    return h('span', { className: 'mrIcon', 'aria-hidden': 'true' }, name);
}

function status(code) {
    return STATUSES.find((s) => s.code === code) || (code === 'Mixed' ? MIXED : { code, label: code, bg: MIXED.bg, fg: MIXED.fg });
}

function pill(code) {
    const s = status(code);
    return h('span', { className: 'mrPill', style: `background:${s.bg};color:${s.fg}` }, s.label);
}

function plural(count, one, many) {
    return `${count} ${count === 1 ? one : many}`;
}

function relative(when) {
    const date = new Date(when);
    const minutes = Math.round((Date.now() - date.getTime()) / 60000);
    if (minutes < 1) {
        return 'Just now';
    }
    if (minutes < 60) {
        return `${minutes} min ago`;
    }
    if (minutes < 24 * 60) {
        return `${Math.round(minutes / 60)} h ago`;
    }
    if (minutes < 6 * 24 * 60) {
        return date.toLocaleDateString(undefined, { weekday: 'short' });
    }
    return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

function day(when) {
    return new Date(when).toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
}

function stamp(when) {
    return new Date(when).toLocaleString(undefined, { weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
}

function position(ticks) {
    if (ticks === null || ticks === undefined) {
        return null;
    }
    const total = Math.floor(ticks / 10000000);
    const two = (n) => String(n).padStart(2, '0');
    return `${Math.floor(total / 3600)}:${two(Math.floor(total / 60) % 60)}:${two(total % 60)}`;
}

function size(bytes) {
    if (!bytes) {
        return null;
    }
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let value = bytes;
    let unit = 0;
    while (value >= 1000 && unit < units.length - 1) {
        value /= 1000;
        unit++;
    }
    return `${value.toFixed(unit < 2 ? 0 : 1)} ${units[unit]}`;
}

function mbps(bits) {
    return bits ? `${(bits / 1000000).toFixed(bits < 10000000 ? 1 : 0)} Mbps` : null;
}

const PLAY_METHODS = { DirectPlay: 'Direct play', DirectStream: 'Direct stream', Transcode: 'Transcode' };

function playMethod(method) {
    return PLAY_METHODS[method] || method;
}

function methods(list) {
    return (list || []).map((m) => (m.Count > 1 ? `${playMethod(m.Method)} ×${m.Count}` : playMethod(m.Method))).join(' · ');
}

function poster(itemId) {
    const url = itemId ? ApiClient.getUrl(`Items/${itemId}/Images/Primary`, { fillHeight: 138, quality: 90 }) : null;
    return h('span', { className: 'mrPoster', style: url ? `background-image:url("${url}")` : null });
}

function call(method, path, query, body) {
    const request = { type: method, url: ApiClient.getUrl(API + path, query || {}), dataType: 'json' };
    if (body) {
        request.data = JSON.stringify(body);
        request.contentType = 'application/json';
    }
    return ApiClient.ajax(request);
}

// A refused call rejects with the fetch Response; its body carries the error code.
function refusal(failure) {
    if (failure && typeof failure.json === 'function') {
        return failure.json().then((body) => body || {}, () => ({}))
            .then((body) => ({ status: failure.status, code: body.Code || body.code, issueId: body.IssueId || body.issueId }));
    }
    return Promise.resolve({ status: 0 });
}

function pageUrl(params) {
    const query = new URLSearchParams({ name: PAGE });
    for (const [key, value] of Object.entries(params)) {
        if (value !== null && value !== undefined && value !== '' && DEFAULTS[key] !== String(value)) {
            query.set(key, value);
        }
    }
    return `#/configurationpage?${query.toString()}`;
}

function listParams(params) {
    const list = {};
    for (const key of LIST_KEYS) {
        if (params[key]) {
            list[key] = params[key];
        }
    }
    return list;
}

function addStyles() {
    if (!document.getElementById('mediaReportsTriageStyles')) {
        document.head.appendChild(h('style', { id: 'mediaReportsTriageStyles' }, STYLES));
    }
}

// ---- The Reports tab -------------------------------------------------------------------

// The page's title and summary line, and above the tabs, the warning that the Report button
// has stopped loading in every app.
function header(summary) {
    const head = h('div', { className: 'mrHead' },
        h('h1', null, 'Media Reports'),
        summary ? h('span', { className: 'mrMuted mrSummary', style: 'font-size:14px' },
            `${plural(summary.OpenIssues, 'open issue', 'open issues')} from ${plural(summary.Reports, 'report', 'reports')} · ${summary.New} new since you last looked`) : null);
    if (!summary || !summary.ButtonStopped) {
        return head;
    }
    const both = document.createDocumentFragment();
    both.append(head, h('div', { className: 'mrPanel mrAlert mrButtonStopped', role: 'status' },
        icon('warning'),
        h('p', null, "The Report button hasn't loaded in any app for 7 days. A Jellyfin update may have broken it.")));
    return both;
}

function tabs(summary, active) {
    return h('nav', { className: 'mrTabs', 'aria-label': 'Media Reports' },
        h('a', { className: 'mrTab', href: pageUrl({}), 'aria-current': active === 'settings' ? null : 'page' }, 'Reports',
            summary ? h('span', { className: 'mrCount' }, summary.OpenIssues) : null),
        h('a', { className: 'mrTab mrSettingsTab', href: pageUrl({ tab: 'settings' }), 'aria-current': active === 'settings' ? 'page' : null }, 'Settings'));
}

function skeleton() {
    const row = () => h('tr', null, ...[60, 50, 20, 60, 70, 40, 30].map((w) => h('td', null, h('span', { className: 'mrSkeleton', style: `width:${w}%` }))));
    return h('div', { className: 'mrTableBox', 'aria-busy': 'true', 'aria-label': 'Loading reports' },
        h('table', { className: 'mrTable' }, h('tbody', null, row(), row(), row())));
}

function couldNotLoad(retry) {
    return h('div', { className: 'mrPanel mrAlert mrCouldNotLoad', role: 'alert' },
        icon('error'),
        h('div', null,
            h('h2', null, "Reports couldn't be loaded"),
            h('p', null, "The Media Reports database didn't open. Reports already filed are safe; yesterday's copy is in the plugin's data folder as media-reports.backup.db. The server log has the details."),
            h('div', { className: 'mrActions' },
                h('button', { type: 'button', className: 'mrBtn mrPrimary', onclick: retry }, 'Try again'),
                h('a', { className: 'mrBtn', href: '#/dashboard/logs' }, 'Open the server log'))));
}

function justInstalled() {
    return h('div', { className: 'mrPanel mrJustInstalled' },
        h('h2', null, 'No reports yet'),
        h('p', null, "Users report with the flag button on a title's page or in the player, in Jellyfin Web, the Android app and the webOS TV app."));
}

function noMatch(params, navigate) {
    const parts = [];
    if (params.reason) {
        parts.push(`for “${params.reasonLabel || params.reason}”`);
    }
    if (params.client) {
        parts.push(`on ${params.client}`);
    }
    if (params.q) {
        parts.push(`matching “${params.q}”`);
    }
    const view = params.view === 'closed' ? 'closed issues' : params.view === 'all' ? 'issues' : 'open issues';
    return h('div', { className: 'mrPanel mrEmpty mrNoMatch' },
        h('p', null, `No ${view} ${parts.join(' ')}.`.replace(/ +\./, '.')),
        h('button', { type: 'button', className: 'mrBtn', onclick: () => navigate({ view: params.view, groupBy: params.groupBy, sort: params.sort }) }, 'Clear filters'));
}

function controls(params, list, navigate) {
    const set = (changes) => navigate(Object.assign({}, listParams(params), changes, { page: null }));
    const segs = (label, key, options) => h('div', { className: 'mrSegs', role: 'group', 'aria-label': label },
        options.map(([value, text]) => h('button', {
            type: 'button',
            className: 'mrSeg',
            'aria-pressed': String((params[key] || DEFAULTS[key]) === value),
            onclick: () => set({ [key]: value })
        }, text)));
    const select = (label, key, any, options) => h('label', { className: 'mrLabel' }, label,
        h('select', { className: 'mrSelect', 'data-filter': key, onchange: (e) => set({ [key]: e.target.value || null }) },
            h('option', { value: '' }, any),
            options.map(([value, text]) => h('option', { value, selected: params[key] === value }, text))));
    const search = h('input', { type: 'search', 'aria-label': 'Search titles', placeholder: 'Search titles', value: params.q || '' });
    let timer = 0;
    search.addEventListener('input', () => {
        clearTimeout(timer);
        timer = setTimeout(() => set({ q: search.value.trim() || null }), 400);
    });
    search.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            clearTimeout(timer);
            set({ q: search.value.trim() || null });
        }
    });
    return h('div', { className: 'mrControls' },
        segs('Status', 'view', [['open', 'Open'], ['closed', 'Closed'], ['all', 'All']]),
        segs('Group reports', 'groupBy', [['title', 'By title'], ['titleClient', 'By title and client']]),
        select('Reason', 'reason', 'Any reason', (list ? list.Reasons : []).map((r) => [r.Code, r.Label])),
        select('Client', 'client', 'Any client', (list ? list.Clients : []).map((c) => [c, c])),
        h('div', { className: 'mrSearch' }, icon('search'), search));
}

function table(params, list, seenNew, navigate) {
    const sortHeader = (key, text) => {
        const active = (params.sort || DEFAULTS.sort) === key;
        return h('th', { scope: 'col', 'aria-sort': active ? (key === 'title' || key === 'status' ? 'ascending' : 'descending') : null },
            h('button', { type: 'button', onclick: () => navigate(Object.assign({}, listParams(params), { sort: key, page: null })) }, text));
    };
    const body = h('tbody');
    const expanded = new Set();
    const render = () => {
        body.textContent = '';
        for (const row of list.Rows) {
            body.appendChild(rowElement(row, false));
            if (row.Kind === 'season' && expanded.has(row.SeriesId + ':' + row.SeasonNumber + ':' + row.ReasonCode)) {
                body.appendChild(setAllRow(row));
                for (const child of row.Children) {
                    body.appendChild(rowElement(child, true));
                }
            }
        }
    };
    const issueLink = (row) => h('a', { className: 'mrTitleLink', href: pageUrl(Object.assign({}, listParams(params), { issue: row.IssueId })) }, row.Title);
    const rowElement = (row, child) => {
        const key = row.SeriesId + ':' + row.SeasonNumber + ':' + row.ReasonCode;
        const isNew = row.IsNew || seenNew.has(row.IssueId) || (row.Children || []).some((c) => seenNew.has(c.IssueId));
        const title = row.Kind === 'season'
            ? h('button', {
                type: 'button',
                className: 'mrTitleLink',
                'aria-expanded': String(expanded.has(key)),
                onclick: () => {
                    if (expanded.has(key)) {
                        expanded.delete(key);
                    } else {
                        expanded.add(key);
                    }
                    render();
                }
            }, row.Title)
            : issueLink(row);
        const line = row.Kind === 'season' ? `${row.Line} · ${plural(row.EpisodeCount, 'episode', 'episodes')}` : row.Line;
        return h('tr', { className: child ? 'mrChild' : (row.Kind === 'season' ? 'mrSeasonRow' : 'mrIssueRow'), 'data-issue': row.IssueId },
            h('td', null, h('div', { className: 'mrTitleCell' },
                h('span', { className: 'mrDotSlot' }, isNew ? h('span', { className: 'mrDot', role: 'img', 'aria-label': 'New since you last looked' }) : null),
                child ? null : poster(row.Kind === 'season' ? row.SeriesId : row.ItemId),
                h('span', { className: 'mrTitleText' }, child ? issueLink(Object.assign({}, row, { Title: row.Line })) : title, child ? null : h('span', { className: 'mrLine' }, line || '')))),
            h('td', null, row.ReasonLabel),
            h('td', null, h('span', { style: 'font-weight:600;color:#fff' }, row.Reports), h('span', { className: 'mrLine', style: 'display:block' }, plural(row.People, 'person', 'people'))),
            h('td', { style: 'color:rgba(255,255,255,.8)' }, params.groupBy === 'titleClient' ? (row.Client || 'No playback attached') : (row.Clients || []).join(', ')),
            h('td', { style: 'color:rgba(255,255,255,.8);font-size:13px' }, methods(row.PlayMethods)),
            h('td', null, pill(row.Status)),
            h('td', { style: 'color:rgba(255,255,255,.7);white-space:nowrap' }, h('time', { datetime: row.LastReport }, relative(row.LastReport))));
    };
    const setAllRow = (row) => {
        const message = h('span', { role: 'status', className: 'mrOk', style: 'margin-left:12px' });
        const select = h('select', { className: 'mrSelect', 'aria-label': 'Set status for all' },
            h('option', { value: '' }, 'Set status for all…'),
            STATUSES.filter((s) => s.code !== 'ReplacedVerify').map((s) => h('option', { value: s.code }, s.label)));
        select.addEventListener('change', () => {
            if (!select.value) {
                return;
            }
            const ids = row.Children.map((c) => c.IssueId);
            call('POST', 'Issues/Status', null, { issueIds: ids, status: select.value })
                .then(() => navigate(listParams(params), true), (failure) => refusal(failure).then((r) => {
                    message.className = 'mrBad';
                    message.textContent = r.code === 'newer-open-issue'
                        ? 'A newer open issue exists for some of these episodes, so none was changed.'
                        : "The statuses couldn't be changed.";
                }));
        });
        return h('tr', { className: 'mrChild mrSetAll' }, h('td', { colspan: '7' }, select, message));
    };
    render();
    return h('div', { className: 'mrTableBox' },
        h('table', { className: 'mrTable' },
            h('thead', null, h('tr', null,
                sortHeader('title', 'Title'),
                h('th', { scope: 'col' }, 'Reason'),
                sortHeader('reports', 'Reports'),
                h('th', { scope: 'col' }, 'Clients'),
                h('th', { scope: 'col' }, 'How it played'),
                sortHeader('status', 'Status'),
                sortHeader('last', 'Last report'))),
            body));
}

function paging(params, list, navigate) {
    const pages = Math.max(1, Math.ceil(list.Total / list.PageSize));
    if (pages === 1) {
        return null;
    }
    const go = (page) => navigate(Object.assign({}, listParams(params), { page: page === 1 ? null : page }));
    return h('nav', { className: 'mrPaging', 'aria-label': 'Pages' },
        h('span', { className: 'mrMuted mrPageOf' }, `Page ${list.Page} of ${pages}`),
        h('button', { type: 'button', className: 'mrBtn', disabled: list.Page <= 1, onclick: () => go(list.Page - 1) }, 'Previous'),
        h('button', { type: 'button', className: 'mrBtn', disabled: list.Page >= pages, onclick: () => go(list.Page + 1) }, 'Next'));
}

// ---- One issue -------------------------------------------------------------------------

function historyLine(event) {
    const who = event.ActorName;
    switch (event.Kind) {
        case 'filed':
            return `Reported by ${who || 'a deleted user'}`;
        case 'withdrawn':
            return `Report withdrawn by ${who || 'a deleted user'}`;
        case 'status changed':
            return `${event.Detail || 'Status changed'}, by ${who || 'Media Reports'}`;
        case 'note added':
            return `Notes changed by ${who || 'a deleted user'}`;
        case 'action run':
            return `${event.Detail || 'Action run'}, by ${who || 'a deleted user'}`;
        case 'anonymised':
            return 'Reporter deleted from the server';
        default:
            return event.Detail || event.Kind;
    }
}

function reportCard(report) {
    const snapshot = report.Snapshot;
    const tags = [report.Client, report.PlayMethod && playMethod(report.PlayMethod), (report.TranscodeReasons || []).join(', '), position(report.PositionTicks) && `at ${position(report.PositionTicks)}`].filter(Boolean);
    const card = h('article', { className: 'mrCard mrReport' },
        h('div', { className: 'mrSpread' },
            h('span', null, h('strong', { style: 'color:#fff;font-weight:600' }, report.ReporterName || 'A deleted user'),
                report.Device ? h('span', { className: 'mrFaint', style: 'font-size:15px' }, ` on ${report.Device}`) : null),
            h('time', { className: 'mrFaint', datetime: report.CreatedAt }, stamp(report.CreatedAt))),
        report.Note ? h('p', { style: 'margin:0;line-height:1.5' }, `“${report.Note}”`) : null,
        tags.length ? h('div', { className: 'mrTags' }, tags.map((t) => h('span', { className: 'mrTag' }, t))) : null);
    if (!snapshot) {
        card.appendChild(h('p', { className: 'mrFaint', style: 'margin:0' }, 'No playback was attached to this report.'));
        return card;
    }
    const details = h('dl', { className: 'mrDl mrDetails', hidden: true });
    const add = (term, value, mono) => {
        if (value) {
            details.appendChild(h('dt', null, term));
            details.appendChild(h('dd', { className: mono ? 'mrMono' : null }, value));
        }
    };
    const t = snapshot.transcoding;
    const streams = snapshot.sourceStreams || [];
    const stream = (index) => streams.find((s) => s.index === index);
    const describe = (s) => s ? [s.codec && s.codec.toUpperCase(), s.channels && `${s.channels} ch`, s.language].filter(Boolean).join(' ') + ` (${s.index})` : 'none';
    add('Captured', snapshot.captured ? `${stamp(snapshot.captured)} · ${(snapshot.state || '').toLowerCase()}` : snapshot.state);
    add('Client', [snapshot.client, snapshot.clientVersion].filter(Boolean).join(' ') + (snapshot.deviceName ? ` · ${snapshot.deviceName}` : ''));
    add('Play method', snapshot.playMethod && playMethod(snapshot.playMethod));
    add('Transcode reasons', t && t.transcodeReasons && t.transcodeReasons.length ? t.transcodeReasons.join(', ') : 'none', true);
    add('Output', t ? [t.videoCodec && t.videoCodec.toUpperCase(), t.width && t.height && `${t.width}×${t.height}`, mbps(t.bitrate), t.audioCodec && t.audioCodec.toUpperCase(), `hardware ${t.hardwareAccelerationType || 'none'}`].filter(Boolean).join(' · ') : 'Original file');
    add('Audio / subtitle', `${describe(stream(snapshot.audioStreamIndex))} / ${describe(stream(snapshot.subtitleStreamIndex))}`);
    const toggle = h('button', { type: 'button', className: 'mrLinkBtn', 'aria-expanded': 'false' }, icon('expand_more'), 'Playback details');
    toggle.addEventListener('click', () => {
        const open = toggle.getAttribute('aria-expanded') !== 'true';
        toggle.setAttribute('aria-expanded', String(open));
        toggle.firstChild.textContent = open ? 'expand_less' : 'expand_more';
        details.hidden = !open;
    });
    card.appendChild(toggle);
    card.appendChild(details);
    return card;
}

function fileCard(issue) {
    const file = issue.File;
    const dl = h('dl', { className: 'mrDl' });
    const add = (term, value, mono) => {
        if (value) {
            dl.appendChild(h('dt', null, term));
            dl.appendChild(h('dd', { className: mono ? 'mrMono' : null }, value));
        }
    };
    if (!file.InLibrary) {
        add('Path', file.Path, true);
        return h('div', { className: 'mrCard mrFile' },
            h('p', { className: 'mrBad', style: 'margin:0;font-size:14px' }, 'This title is no longer in the library.'), dl);
    }
    if (issue.ItemType === 'Season' || issue.ItemType === 'Series') {
        add('Folder', file.Path, true);
        add('Episodes', String(file.EpisodeCount));
    } else {
        const streams = file.Streams || [];
        const video = streams.find((s) => s.Type === 'Video');
        const list = (type) => streams.filter((s) => s.Type === type).map((s, i) => `${i + 1}. ${s.Title || [s.Codec, s.Language].filter(Boolean).join(' ')}`);
        add('Path', file.Path, true);
        add('Size', [size(file.Size), file.DateAdded && `added ${day(file.DateAdded)}`].filter(Boolean).join(' · '));
        add('Container', [file.Container && file.Container.toUpperCase(), mbps(file.Bitrate)].filter(Boolean).join(' · '));
        add('Video', video ? [video.Codec && video.Codec.toUpperCase(), video.Profile, video.Width && `${video.Width}×${video.Height}`, video.VideoRange, video.FrameRate && `${Math.round(video.FrameRate * 1000) / 1000} fps`].filter(Boolean).join(' · ') : null);
        const audio = list('Audio');
        const subtitles = list('Subtitle');
        if (audio.length) {
            add('Audio', h('span', null, audio.map((a, i) => [i ? h('br') : null, a])));
        }
        add('Subtitles', subtitles.length ? subtitles.join(', ') : 'none');
    }
    return h('div', { className: 'mrCard mrFile' }, dl);
}

// jellyfin-web keeps earlier views in the page, so ids are made unique per view.
let ids = 0;
// A message to show once the issue has been loaded again, such as "Scan started".
let pending = null;

function issuePage(params, issue, navigate) {
    const statusId = `mediaReportsStatus${++ids}`;
    const notesId = `mediaReportsNotes${ids}`;
    const back = h('a', { className: 'mrBack', href: pageUrl(listParams(params)) }, icon('arrow_back'), 'All reports');
    const message = h('p', { role: 'status', className: 'mrOk mrMessage', style: 'margin:0' });
    const say = (text, bad) => {
        message.className = (bad ? 'mrBad' : 'mrOk') + ' mrMessage';
        message.textContent = '';
        message.appendChild(typeof text === 'string' ? document.createTextNode(text) : text);
    };
    const reload = () => navigate(Object.assign({}, listParams(params), { issue: issue.Id }), true);
    const action = (path, started) => () => call('POST', `Issues/${issue.Id}/${path}`).then(() => {
        pending = started;
        reload();
    }, (failure) => refusal(failure).then((r) => say(r.code === 'not-in-library' ? 'This title is no longer in the library.' : "That couldn't be started.", true)));
    const inLibrary = issue.File.InLibrary;
    const reports = issue.Reports;
    const first = reports.length ? reports[reports.length - 1].CreatedAt : issue.CreatedAt;
    const last = reports.length ? reports[0].CreatedAt : issue.CreatedAt;

    const statusSelect = h('select', { id: statusId, className: 'mrSelect mrStatusSelect' },
        STATUSES.filter((s) => s.code !== 'ReplacedVerify' || issue.Status === 'ReplacedVerify')
            .map((s) => h('option', { value: s.code, selected: s.code === issue.Status, disabled: s.code === 'ReplacedVerify' }, s.label)));
    statusSelect.addEventListener('change', () => {
        call('PATCH', `Issues/${issue.Id}`, null, { status: statusSelect.value }).then(reload, (failure) => refusal(failure).then((r) => {
            statusSelect.value = issue.Status;
            if (r.code === 'newer-open-issue') {
                say(h('span', null, 'A newer open issue exists for this title and reason, so this one stays closed. ',
                    h('a', { href: pageUrl(Object.assign({}, listParams(params), { issue: r.issueId })) }, 'Open the newer issue')), true);
            } else {
                say("The status couldn't be changed.", true);
            }
        }));
    });

    const notes = h('textarea', { id: notesId, className: 'mrNotes', maxlength: '10000' });
    notes.value = issue.Notes || '';
    const saveNotes = h('button', { type: 'button', className: 'mrBtn' }, 'Save notes');
    saveNotes.addEventListener('click', () => call('PATCH', `Issues/${issue.Id}`, null, { notes: notes.value }).then(reload,
        (failure) => refusal(failure).then((r) => say(r.code === 'notes-too-long' ? 'Notes are limited to 10,000 characters.' : "The notes couldn't be saved.", true))));

    if (pending) {
        say(pending);
        pending = null;
    }

    const jellyfinUrl = `${ApiClient.serverAddress()}/web/#/details?id=${issue.ItemId}&serverId=${issue.ServerId}`;
    // Laid out in the order an issue is worked: decide (status, notes), read the reports,
    // check the file, then the history.
    const section = (title, className, ...content) => h('section', { className: `mrSection ${className}` }, h('h2', { className: 'mrH3' }, title), content);
    return h('div', { className: 'mediaReportsIssue', 'data-issue': issue.Id, style: 'display:flex;flex-direction:column' },
        back,
        h('div', { className: 'mrIssueHead' },
            poster(issue.ItemId),
            h('div', { className: 'mrIssueTitle' },
                h('h1', null, issue.Title),
                issue.Line ? h('p', { className: 'mrMuted' }, issue.Line) : null,
                h('p', { className: 'mrMuted mrIssueSummary' }, `${issue.ReasonLabel} · ${plural(reports.length, 'report', 'reports')} from ${plural(issue.People, 'person', 'people')} · first ${day(first)}, last ${day(last)}`),
                inLibrary ? h('div', { className: 'mrActions' },
                    h('a', { className: 'mrBtn', href: jellyfinUrl, target: '_blank', rel: 'noopener' }, icon('open_in_new'), 'Open in Jellyfin'),
                    h('button', { type: 'button', className: 'mrBtn', onclick: action('Refresh', 'Refresh started') }, icon('refresh'), 'Refresh metadata'),
                    h('button', { type: 'button', className: 'mrBtn', title: "Look for new or changed files in this title's folder", onclick: action('Scan', 'Rescan started') }, icon('sync'), 'Rescan')) : null,
                message)),
        section('Triage', 'mrTriageSection', h('div', { className: 'mrCard mrTriage' },
            h('div', { className: 'mrField' },
                h('label', { for: statusId, className: 'mrFaint' }, 'Status'),
                statusSelect,
                h('span', { className: 'mrFaint', style: 'font-size:12px' }, `Closing notifies the ${plural(issue.People, 'person', 'people')} who reported it.`)),
            h('div', { className: 'mrField mrNotesCard' },
                h('label', { for: notesId, className: 'mrFaint' }, 'Notes (only admins see them)'),
                notes,
                saveNotes))),
        h('div', { className: 'mrColumns' },
            section(`Reports (${reports.length})`, 'mrReports', reports.map(reportCard)),
            section('File', 'mrFileSection', fileCard(issue))),
        section('History', 'mrHistorySection', h('div', { className: 'mrCard mrHistoryCard' },
            h('ol', { className: 'mrHistory' }, issue.History.map((event) =>
                h('li', null, h('span'), h('span', null, historyLine(event)), h('time', { datetime: event.At }, stamp(event.At))))))));
}

// ---- The Settings tab -----------------------------------------------------------------

// The scrape config Prometheus needs for the metrics address, with the API key left to fill in.
function scrapeConfig(metricsUrl) {
    let url;
    try {
        url = new URL(metricsUrl);
    } catch (e) {
        return '';
    }
    return ['scrape_configs:',
        '  - job_name: media-reports',
        `    scheme: ${url.protocol.replace(':', '')}`,
        `    metrics_path: ${url.pathname}`,
        '    authorization:',
        '      type: MediaBrowser',
        "      credentials: 'Token=\"<API key>\"'",
        '    static_configs:',
        `      - targets: ['${url.host}']`].join('\n');
}

// Copies text; the Dashboard is often served over plain http, where the Clipboard API is missing.
function copyText(text, from) {
    if (navigator.clipboard && window.isSecureContext) {
        return navigator.clipboard.writeText(text);
    }
    from.removeAttribute('readonly');
    from.select();
    const done = document.execCommand('copy');
    from.setAttribute('readonly', '');
    return done ? Promise.resolve() : Promise.reject(new Error('copy'));
}

function metricsBlock(metricsUrl) {
    const address = h('input', { className: 'mrInput mrMetricsUrl', id: 'mrMetricsUrl', type: 'text', readonly: true, value: metricsUrl });
    const scrape = scrapeConfig(metricsUrl);
    const config = h('textarea', { className: 'mrScrape', id: 'mrScrape', readonly: true, rows: scrape.split('\n').length, 'aria-label': 'Prometheus scrape config' }, scrape);
    const note = h('span', { className: 'mrOk', role: 'status' });
    const copy = (text, from) => () => copyText(text, from).then(() => {
        note.className = 'mrOk';
        note.textContent = 'Copied';
    }, () => {
        note.className = 'mrBad';
        note.textContent = 'Select it and copy it by hand.';
    });
    return h('div', { className: 'mrMetrics' },
        h('label', { for: 'mrMetricsUrl', style: 'font-size:14px;color:var(--mr-muted)' }, 'Prometheus metrics address'),
        h('div', { className: 'mrCopyRow' }, address,
            h('button', { type: 'button', className: 'mrBtn mrCopyUrl', onclick: copy(metricsUrl, address) }, icon('content_copy'), 'Copy'),
            h('button', { type: 'button', className: 'mrBtn mrCopyScrape', onclick: copy(scrape, config) }, icon('content_copy'), 'Copy scrape config'),
            note),
        h('p', { className: 'mrFaint', style: 'margin:0' },
            'Create an API key for it in Dashboard → API Keys and put it in place of <API key>. Counts carry reasons and client names only, never titles, paths or user names.'),
        config);
}

// A refused save answers 400 with the field at fault, and the reason's position for a reason.
function settingsRefusal(failure) {
    if (failure && failure.status === 400 && typeof failure.json === 'function') {
        return failure.json().then((body) => ({
            code: body.Code, message: body.Message, field: body.Field, index: body.ReasonIndex === undefined ? null : body.ReasonIndex
        }), () => null);
    }
    return Promise.resolve(null);
}

function exportReports(button, note) {
    button.disabled = true;
    note.textContent = '';
    ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(API + 'Export') })
        .then((response) => response.blob().then((blob) => {
            const match = /filename="?([^";]+)"?/.exec(response.headers.get('Content-Disposition') || '');
            const link = h('a', { href: URL.createObjectURL(blob), download: match ? match[1] : 'media-reports-export.json', style: 'display:none' });
            document.body.appendChild(link);
            link.click();
            setTimeout(() => {
                URL.revokeObjectURL(link.href);
                link.remove();
            }, 1000);
        }))
        .catch(() => {
            note.textContent = "The export couldn't be made. The server log has the details.";
        })
        .then(() => {
            button.disabled = false;
        });
}

function settingsPage(settings) {
    const model = {
        reasons: settings.Reasons.map((r) => ({ code: r.Code, label: r.Label, retired: r.Retired, reports: r.Reports })),
        reportingEnabled: settings.ReportingEnabled,
        days: String(settings.ReportRetentionDays)
    };
    let refused = null;
    let focusAfter = null;

    const list = h('ul', { className: 'mrReasons' });
    const reasonsNote = h('p', { className: 'mrBad', role: 'alert' });
    const days = h('input', { className: 'mrInput mrDays', id: 'mrDays', type: 'text', inputmode: 'numeric', value: model.days, 'aria-describedby': 'mrDaysNote' });
    const daysNote = h('p', { className: 'mrBad', role: 'alert' });
    const reporting = h('input', { type: 'checkbox', id: 'mrReporting', checked: model.reportingEnabled });
    const hooks = [
        { field: 'discordWebhookUrl', id: 'mrDiscord', label: 'Discord webhook', value: settings.DiscordWebhookUrl, placeholder: 'https://discord.com/api/webhooks/…' },
        { field: 'webhookUrl', id: 'mrWebhook', label: 'Other webhook (JSON POST)', value: settings.WebhookUrl, placeholder: 'https://' }
    ].map((hook) => Object.assign(hook, {
        input: h('input', { className: 'mrInput', id: hook.id, type: 'url', value: hook.value, placeholder: hook.placeholder, 'aria-describedby': hook.id + 'Note' }),
        note: h('p', { id: hook.id + 'Note', role: 'status' })
    }));
    const testButton = h('button', { type: 'button', className: 'mrBtn mrTestAlert' }, icon('send'), 'Send a test alert');
    const testNote = h('p', { className: 'mrFaint', role: 'status' });
    const saveButton = h('button', { type: 'button', className: 'mrBtn mrPrimary mrSaveBtn' }, 'Save');
    const saveNote = h('span', { className: 'mrSaveNote', role: 'status' });
    const exportButton = h('button', { type: 'button', className: 'mrBtn mrExport' }, icon('download'), 'Export all reports');
    const exportNote = h('p', { className: 'mrBad', role: 'alert' });

    function edited() {
        saveNote.textContent = '';
        saveNote.className = 'mrSaveNote';
    }

    function uses(reason) {
        const count = plural(reason.reports, 'report', 'reports');
        if (reason.retired) {
            return `Retired · ${count}`;
        }
        return reason.code ? count : 'New';
    }

    function move(index, by) {
        const [reason] = model.reasons.splice(index, 1);
        model.reasons.splice(index + by, 0, reason);
        focusAfter = `.mrReason:nth-child(${index + by + 1}) .${by < 0 ? 'mrUp' : 'mrDown'}`;
        refused = null;
        edited();
        renderReasons();
    }

    function renderReasons() {
        list.textContent = '';
        model.reasons.forEach((reason, index) => {
            const label = reason.label || 'this reason';
            const error = refused && refused.field === 'reasons' && refused.index === index ? refused.message : null;
            const input = h('input', {
                className: 'mrInput', type: 'text', maxlength: 40, value: reason.label, 'aria-label': `Reason ${index + 1} name`,
                'aria-invalid': error ? 'true' : null, oninput: (e) => {
                    reason.label = e.target.value;
                    edited();
                }
            });
            list.appendChild(h('li', { className: `mrReason${reason.retired ? ' mrRetired' : ''}` },
                input,
                h('span', { className: 'mrUses' }, uses(reason)),
                h('button', { type: 'button', className: 'mrIconBtn mrUp', 'aria-label': `Move ${label} up`, disabled: index === 0, onclick: () => move(index, -1) }, icon('arrow_upward')),
                h('button', { type: 'button', className: 'mrIconBtn mrDown', 'aria-label': `Move ${label} down`, disabled: index === model.reasons.length - 1, onclick: () => move(index, 1) }, icon('arrow_downward')),
                h('button', {
                    type: 'button', className: `mrLinkBtn ${reason.retired ? 'mrRestore' : 'mrRetire'}`, onclick: () => {
                        reason.retired = !reason.retired;
                        focusAfter = `.mrReason:nth-child(${index + 1}) .${reason.retired ? 'mrRestore' : 'mrRetire'}`;
                        edited();
                        renderReasons();
                    }
                }, reason.retired ? 'Restore' : 'Retire'),
                error ? h('p', { className: 'mrBad', role: 'alert' }, error) : null));
        });
        reasonsNote.textContent = refused && refused.field === 'reasons' && refused.index === null ? refused.message : '';
        if (focusAfter) {
            const target = list.querySelector(focusAfter);
            focusAfter = null;
            if (target && !target.disabled) {
                target.focus();
            }
        }
    }

    function hookNote(hook, text, good) {
        hook.note.className = good ? 'mrOk' : 'mrBad';
        hook.note.textContent = text || '';
        hook.input.setAttribute('aria-invalid', text && !good ? 'true' : 'false');
    }

    function sendTest() {
        testNote.textContent = '';
        hooks.forEach((hook) => hookNote(hook, ''));
        if (hooks.every((hook) => !hook.input.value.trim())) {
            testNote.textContent = 'Enter a webhook address first.';
            return;
        }
        testButton.disabled = true;
        testNote.textContent = 'Sending…';
        call('POST', 'Settings/TestAlert', null, { DiscordWebhookUrl: hooks[0].input.value, WebhookUrl: hooks[1].input.value })
            .then((results) => {
                testNote.textContent = '';
                for (const result of results) {
                    const hook = hooks.find((x) => x.field === result.Field);
                    if (hook) {
                        hookNote(hook, result.Accepted ? 'Test alert accepted' : `Test alert failed: ${result.Message}`, result.Accepted);
                    }
                }
            }, () => {
                testNote.textContent = "The test alert couldn't be sent. Try again.";
            })
            .then(() => {
                testButton.disabled = false;
            });
    }

    function showRefusal() {
        hooks.forEach((hook) => hookNote(hook, refused && refused.field === hook.field ? refused.message : ''));
        renderReasons();
        const daysWrong = refused && refused.field === 'reportRetentionDays';
        daysNote.textContent = daysWrong ? refused.message : '';
        days.setAttribute('aria-invalid', daysWrong ? 'true' : 'false');
    }

    function save() {
        saveButton.disabled = true;
        saveNote.className = 'mrSaveNote mrFaint';
        saveNote.textContent = 'Saving…';
        const body = {
            ReportingEnabled: reporting.checked,
            Reasons: model.reasons.map((r) => ({ Code: r.code, Label: r.label, Retired: r.retired })),
            ReportRetentionDays: days.value,
            DiscordWebhookUrl: hooks[0].input.value,
            WebhookUrl: hooks[1].input.value
        };
        call('PUT', 'Settings', null, body).then((saved) => {
            refused = null;
            model.reasons = saved.Reasons.map((r) => ({ code: r.Code, label: r.Label, retired: r.Retired, reports: r.Reports }));
            days.value = String(saved.ReportRetentionDays);
            reporting.checked = saved.ReportingEnabled;
            hooks[0].input.value = saved.DiscordWebhookUrl;
            hooks[1].input.value = saved.WebhookUrl;
            showRefusal();
            saveNote.className = 'mrSaveNote mrOk';
            saveNote.textContent = 'Saved';
        }, (failure) => settingsRefusal(failure).then((r) => {
            refused = r;
            showRefusal();
            saveNote.className = 'mrSaveNote mrBad';
            saveNote.textContent = r ? "Not saved. Fix what's marked and save again." : "Settings couldn't be saved. Try again.";
            const first = r && r.field === 'reasons' && r.index !== null ? list.children[r.index] && list.children[r.index].querySelector('input')
                : r && r.field === 'reportRetentionDays' ? days
                    : r && hooks.find((hook) => hook.field === r.field) ? hooks.find((hook) => hook.field === r.field).input : null;
            if (first) {
                first.focus();
            }
        })).then(() => {
            saveButton.disabled = false;
        });
    }

    saveButton.addEventListener('click', save);
    exportButton.addEventListener('click', () => exportReports(exportButton, exportNote));
    days.addEventListener('input', edited);
    hooks.forEach((hook) => hook.input.addEventListener('input', edited));
    testButton.addEventListener('click', sendTest);
    reporting.addEventListener('change', edited);
    renderReasons();

    return h('div', { className: 'mrSettings' },
        h('section', { className: 'mrCard', 'aria-labelledby': 'mrReasonsHead' },
            h('h2', { className: 'mrH3', id: 'mrReasonsHead' }, 'Reasons'),
            h('p', { className: 'mrFaint' }, 'What users pick from when they report. Renaming keeps the reason on its reports; a retired reason is no longer offered but stays on the reports that used it.'),
            list,
            reasonsNote,
            h('button', {
                type: 'button', className: 'mrLinkBtn mrAddReason', onclick: () => {
                    model.reasons.push({ code: null, label: '', retired: false, reports: 0 });
                    focusAfter = '.mrReason:last-child input';
                    edited();
                    renderReasons();
                }
            }, icon('add'), 'Add a reason')),
        h('section', { className: 'mrCard', 'aria-labelledby': 'mrReportingHead' },
            h('h2', { className: 'mrH3', id: 'mrReportingHead' }, 'Reporting'),
            h('label', { className: 'mrCheck', for: 'mrReporting' }, reporting,
                h('span', null, 'Let users report problems',
                    h('span', { className: 'mrFaint', style: 'display:block;margin-top:4px' },
                        'Off removes the Report button and My Reports from every app, and new reports are refused. Reports already filed stay here.')))),
        h('section', { className: 'mrCard mrAlerts', 'aria-labelledby': 'mrAlertsHead' },
            h('h2', { className: 'mrH3', id: 'mrAlertsHead' }, 'Alerts'),
            h('p', { className: 'mrFaint' }, "Each new report is posted here within a minute; reports filed in the same minute come as one message. Every report is also in Jellyfin's activity log."),
            hooks.map((hook) => h('div', { className: 'mrHook' }, h('label', { for: hook.id }, hook.label), hook.input, hook.note)),
            h('div', null, testButton),
            testNote,
            metricsBlock(settings.MetricsUrl)),
        h('section', { className: 'mrCard', 'aria-labelledby': 'mrKeepingHead' },
            h('h2', { className: 'mrH3', id: 'mrKeepingHead' }, 'Keeping reports'),
            h('p', { style: 'margin:0;display:flex;flex-wrap:wrap;align-items:center' },
                h('label', { for: 'mrDays' }, 'Delete closed reports after'), days, h('span', null, 'days')),
            daysNote,
            h('p', { className: 'mrFaint', id: 'mrDaysNote' }, '0 keeps them forever. Open reports are never deleted.'),
            h('div', null, exportButton),
            exportNote,
            h('p', { className: 'mrFaint' }, 'Every report, issue and history entry, as one JSON file.')),
        h('div', { className: 'mrSave' }, saveButton, saveNote));
}

// ---- The page --------------------------------------------------------------------------

// One visit to the page spans its own navigation, which jellyfin-web loads as new views.
// Issues new when the visit started keep their dot after the visit is recorded; the visit
// ends once the page has been off screen for a moment.
const visit = { marked: false, seenNew: new Set(), lastOnScreen: 0 };
const roots = new Set();
setInterval(() => {
    for (const root of roots) {
        if (root.isConnected && root.offsetParent !== null) {
            visit.lastOnScreen = Date.now();
        }
    }
}, 500);

export default function (view) {
    const root = view.querySelector('.mediaReportsTriage');
    const seenNew = visit.seenNew;
    let generation = 0;
    roots.add(root);

    function current() {
        const query = new URLSearchParams((window.location.hash.split('?')[1] || ''));
        const params = {};
        query.forEach((value, key) => {
            params[key] = value;
        });
        return params;
    }

    function navigate(params, replace) {
        const url = pageUrl(params);
        if (replace || url === window.location.hash) {
            show();
            return;
        }
        window.location.hash = url.slice(1);
    }

    function show() {
        if (Date.now() - visit.lastOnScreen > 1500) {
            visit.marked = false;
            seenNew.clear();
        }
        visit.lastOnScreen = Date.now();
        const params = current();
        const mine = ++generation;
        addStyles();
        root.textContent = '';
        if (params.issue) {
            root.appendChild(skeleton());
            call('GET', `Issues/${encodeURIComponent(params.issue)}`).then((issue) => {
                if (mine === generation) {
                    root.textContent = '';
                    root.appendChild(issuePage(params, issue, navigate));
                }
            }, (failure) => refusal(failure).then((r) => {
                if (mine === generation) {
                    root.textContent = '';
                    root.appendChild(r.status === 404
                        ? h('div', { className: 'mrPanel mrEmpty' }, h('p', null, 'That issue no longer exists.'), h('a', { className: 'mrBtn', href: pageUrl(listParams(params)) }, 'All reports'))
                        : couldNotLoad(show));
                }
            }));
            return;
        }

        if (params.tab === 'settings') {
            root.appendChild(header(null));
            root.appendChild(tabs(null, 'settings'));
            root.appendChild(skeleton());
            Promise.all([call('GET', 'Settings'), call('GET', 'Summary', { markSeen: false })]).then(([settings, summary]) => {
                if (mine === generation) {
                    root.textContent = '';
                    root.appendChild(header(summary));
                    root.appendChild(tabs(summary, 'settings'));
                    root.appendChild(settingsPage(settings));
                }
            }, () => {
                if (mine === generation) {
                    root.textContent = '';
                    root.appendChild(header(null));
                    root.appendChild(tabs(null, 'settings'));
                    root.appendChild(couldNotLoad(show));
                }
            });
            return;
        }

        root.appendChild(header(null));
        root.appendChild(tabs(null));
        root.appendChild(controls(params, null, navigate));
        root.appendChild(skeleton());
        const query = { view: params.view || DEFAULTS.view, groupBy: params.groupBy || DEFAULTS.groupBy, sort: params.sort || DEFAULTS.sort, page: params.page || 1 };
        for (const key of ['reason', 'client', 'q']) {
            if (params[key]) {
                query[key] = params[key];
            }
        }
        call('GET', 'Issues', query)
            .then((list) => call('GET', 'Summary', { markSeen: !visit.marked }).then((summary) => [list, summary]))
            .then(([list, summary]) => {
                if (mine !== generation) {
                    return;
                }
                if (!visit.marked) {
                    visit.marked = true;
                    list.Rows.flatMap((r) => [r].concat(r.Children || [])).filter((r) => r.IsNew).forEach((r) => seenNew.add(r.IssueId));
                    document.dispatchEvent(new CustomEvent('mediareports:seen'));
                }
                const filtered = params.reason || params.client || params.q;
                root.textContent = '';
                root.appendChild(header(summary));
                root.appendChild(tabs(summary));
                if (list.Total === 0 && !filtered && summary.OpenIssues === 0 && (params.view || 'open') !== 'closed') {
                    const waiting = root.appendChild(skeleton());
                    return call('GET', 'Issues', { view: 'all' }).then((all) => {
                        if (mine !== generation) {
                            return;
                        }
                        root.removeChild(waiting);
                        if (all.Total === 0) {
                            root.appendChild(justInstalled());
                        } else {
                            root.appendChild(controls(params, list, navigate));
                            root.appendChild(noMatch(params, navigate));
                        }
                    });
                }
                root.appendChild(controls(params, list, navigate));
                if (list.Total === 0) {
                    const reason = list.Reasons.find((r) => r.Code === params.reason);
                    root.appendChild(noMatch(Object.assign({}, params, { reasonLabel: reason && reason.Label }), navigate));
                    return null;
                }
                root.appendChild(table(params, list, seenNew, navigate));
                const pages = paging(params, list, navigate);
                if (pages) {
                    root.appendChild(pages);
                }
                root.appendChild(h('p', { className: 'mrFaint', style: 'margin-top:20px' },
                    'A report joins the open issue for the same title and reason; after that issue closes, a new report starts a new one. Episodes roll up into their season when three or more episodes share a reason.'));
                return null;
            })
            .catch(() => {
                if (mine === generation) {
                    root.textContent = '';
                    root.appendChild(header(null));
                    root.appendChild(couldNotLoad(show));
                }
            });
    }

    // jellyfin-web loads a new view for each URL and shows a kept one again on Back.
    view.addEventListener('viewshow', show);
    view.addEventListener('viewdestroy', () => roots.delete(root));
}
