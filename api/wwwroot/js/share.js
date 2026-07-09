// api/wwwroot/js/share.js — anonymous viewer for /share/{token}.
// No auth.js / api.js on this page: plain fetch() with same-origin cookies (the session cookie
// is HttpOnly and scoped to Path=/api/share — set by the server, never read/written here).
//
// Mirrors js/dashboards.js's widget-rendering pattern (renderStat/renderTable/renderChartJs)
// against the read-only share endpoints. Response shapes actually served (verified by reading
// ShareEndpoints.cs / DashboardService.GetShareViewAsync / GetWidgetDataAsync):
//   POST /api/share/{token}/session        -> 204 + Set-Cookie | 401 {success:false,error:{code:"SHARE_PIN_INVALID",...}}
//                                              | 429 {success:false,error:{code:"SHARE_LOCKED",message:"...minutes..."}}
//   GET  /api/share/{token}/dashboard      -> 404 (no/expired/revoked session) | {success:true,data:{dashboard_name, widgets:[{widget_id,title,chart_type,chart_config,position}]}}
//   GET  /api/share/{token}/widgets/{id}/data -> {success:true,data:{columns:[{name,type}],rows:[[...]], ...}} (compact_table shape, same as owner side)
//
// Widget DTOs here deliberately omit `sql`, `dataset_id`, `source`, `refresh_interval_sec`
// (server-side: DashboardService.ShareWidgetRow) — viewers must not learn schema/business logic,
// and there is no auto-refresh timer for the anonymous view.

const token = location.pathname.split('/').pop();

// Same validated default palette as js/dashboards.js (dataviz skill, light-mode steps) — fixed
// order, never cycled/re-picked per filter.
const CHART_SERIES_COLORS = [
    '#2a78d6', '#1baf7a', '#eda100', '#008300',
    '#4a3aa7', '#e34948', '#e87ba4', '#eb6834'
];

const SharePage = {
    charts: {}, // widget_id -> Chart.js instance, destroyed before re-creating

    init() {
        $('#pinForm').addEventListener('submit', (e) => this.submitPin(e));
        this.loadDashboard(); // try straight away: an existing session cookie skips the PIN gate
    },

    // ============================================================
    // PIN gate
    // ============================================================

    async submitPin(e) {
        e.preventDefault();
        const pin = $('#pin').value;
        const btn = $('#pinSubmitBtn');

        this.hidePinStatus();
        btn.disabled = true;
        btn.textContent = 'Đang mở…';

        try {
            const res = await fetch(`/api/share/${token}/session`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ pin })
            });

            if (res.status === 204) {
                await this.loadDashboard();
                return;
            }

            const json = await res.json().catch(() => null);
            const serverMessage = json && json.error && json.error.message;

            if (res.status === 429) {
                this.showPinError(serverMessage || 'Tạm khoá vì sai PIN nhiều lần. Thử lại sau.');
            } else if (res.status === 404) {
                // Token expired/revoked between page load and submit — not a wrong-PIN case.
                this.showPinError('Link chia sẻ đã hết hạn hoặc bị thu hồi.');
            } else {
                this.showPinError('Sai PIN.');
            }
        } catch {
            this.showPinError('Không kết nối được máy chủ. Vui lòng thử lại.');
        } finally {
            btn.disabled = false;
            btn.textContent = 'Mở';
        }
    },

    showPinError(message) {
        const box = $('#pinStatus');
        box.hidden = false;
        box.className = 'status-msg error';
        box.textContent = message;
    },

    hidePinStatus() {
        $('#pinStatus').hidden = true;
    },

    showGate() {
        this.clearAllCharts();
        $('#dash').hidden = true;
        $('#pin-gate').hidden = false;
    },

    // ============================================================
    // Dashboard load
    // ============================================================

    async loadDashboard() {
        let res;
        try {
            res = await fetch(`/api/share/${token}/dashboard`);
        } catch {
            this.showGate();
            return;
        }

        if (!res.ok) {
            this.showGate(); // no/expired/revoked session -> back to PIN gate
            return;
        }

        const json = await res.json().catch(() => null);
        if (!json || !json.data) {
            this.showGate();
            return;
        }

        this.showDashboard(json.data);
    },

    showDashboard(data) {
        $('#pin-gate').hidden = true;
        const dash = $('#dash');
        dash.hidden = false;
        dash.querySelector('h1').textContent = data.dashboard_name || '';

        const widgets = data.widgets || [];
        const grid = $('#widgetGrid');

        if (widgets.length === 0) {
            grid.innerHTML = '<p class="muted">Dashboard này chưa có widget nào.</p>';
            return;
        }

        grid.innerHTML = widgets.map(w => this.widgetCardHtml(w)).join('');
        widgets.forEach(w => this.loadWidgetData(w));
    },

    widgetCardHtml(w) {
        // widget_id is a server-generated UUID (DB primary key), never user input — same
        // unescaped-in-id/selector convention as js/dashboards.js's widgetCardHtml.
        return `
            <div class="widget-card" data-widget-id="${w.widget_id}">
                <div class="widget-card-head">
                    <div>
                        <div class="widget-title">${escapeHtml(w.title)}</div>
                        <span class="badge badge-source">${escapeHtml(w.chart_type)}</span>
                    </div>
                </div>
                <div class="widget-body" id="widget-body-${w.widget_id}">
                    <p class="muted">Đang tải…</p>
                </div>
            </div>`;
    },

    // ============================================================
    // Widget data + rendering (pattern copied from js/dashboards.js, endpoint swapped to the
    // anonymous /api/share/{token}/widgets/{id}/data route)
    // ============================================================

    async loadWidgetData(widget) {
        const body = $(`#widget-body-${widget.widget_id}`);
        if (!body) return;

        try {
            const res = await fetch(`/api/share/${token}/widgets/${widget.widget_id}/data`);
            if (!res.ok) {
                this.renderWidgetError(widget, 'Không tải được dữ liệu widget.');
                return;
            }
            const json = await res.json();
            this.renderWidgetChart(widget, json.data || {});
        } catch {
            this.renderWidgetError(widget, 'Không tải được dữ liệu widget.');
        }
    },

    renderWidgetError(widget, message) {
        this.destroyChart(widget.widget_id);
        const body = $(`#widget-body-${widget.widget_id}`);
        if (!body) return;
        body.innerHTML = `<div class="error">${escapeHtml(message)}</div>`;
    },

    renderWidgetChart(widget, table) {
        const body = $(`#widget-body-${widget.widget_id}`);
        if (!body) return;

        const columns = table.columns || [];
        const rows = table.rows || [];

        switch (widget.chart_type) {
            case 'stat':
                this.destroyChart(widget.widget_id);
                this.renderStat(body, columns, rows);
                break;
            case 'line':
            case 'bar':
            case 'pie':
                this.renderChartJs(widget, body, columns, rows);
                break;
            case 'table':
            default:
                this.destroyChart(widget.widget_id);
                this.renderTable(body, columns, rows);
                break;
        }
    },

    renderStat(body, columns, rows) {
        const label = columns[0] ? escapeHtml(columns[0].name) : '';
        const value = (rows[0] && rows[0][0] !== null && rows[0][0] !== undefined)
            ? escapeHtml(String(rows[0][0]))
            : '—';
        body.innerHTML = `
            <div class="stat-value">${value}</div>
            <div class="stat-label">${label}</div>`;
    },

    renderTable(body, columns, rows) {
        if (rows.length === 0) {
            body.innerHTML = '<p class="muted">Không có dữ liệu.</p>';
            return;
        }

        const head = columns.map(c => `<th>${escapeHtml(c.name)}</th>`).join('');
        const bodyRows = rows.map(r => `<tr>${r.map(cell =>
            `<td>${cell === null || cell === undefined ? '' : escapeHtml(String(cell))}</td>`).join('')}</tr>`).join('');

        body.innerHTML = `
            <div class="table-scroll">
                <table class="data-table">
                    <thead><tr>${head}</tr></thead>
                    <tbody>${bodyRows}</tbody>
                </table>
            </div>`;
    },

    renderChartJs(widget, body, columns, rows) {
        this.destroyChart(widget.widget_id);

        if (!window.Chart) {
            body.innerHTML = '<p class="muted">Chart.js chưa được tải — không thể vẽ biểu đồ này.</p>';
            return;
        }

        if (rows.length === 0 || columns.length < 2) {
            body.innerHTML = '<p class="muted">Không đủ dữ liệu để vẽ biểu đồ (cần ít nhất 2 cột).</p>';
            return;
        }

        body.innerHTML = '<div class="chart-canvas-wrap"><canvas></canvas></div>';
        const canvas = body.querySelector('canvas');

        const labels = rows.map(r => String(r[0] ?? ''));
        const seriesColumns = widget.chart_type === 'pie' ? columns.slice(1, 2) : columns.slice(1);

        const datasets = seriesColumns.map((col, idx) => {
            const color = CHART_SERIES_COLORS[idx % CHART_SERIES_COLORS.length];
            const colIndex = idx + 1;
            const data = rows.map(r => {
                const v = Number(r[colIndex]);
                return Number.isFinite(v) ? v : 0;
            });

            if (widget.chart_type === 'pie') {
                return {
                    label: col.name,
                    data,
                    backgroundColor: CHART_SERIES_COLORS
                };
            }

            return {
                label: col.name,
                data,
                borderColor: color,
                backgroundColor: widget.chart_type === 'bar' ? color : 'transparent',
                borderWidth: 2
            };
        });

        this.charts[widget.widget_id] = new Chart(canvas, {
            type: widget.chart_type,
            data: { labels, datasets },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: { legend: { display: datasets.length > 1 || widget.chart_type === 'pie' } }
            }
        });
    },

    destroyChart(widgetId) {
        const existing = this.charts[widgetId];
        if (existing) {
            existing.destroy();
            delete this.charts[widgetId];
        }
    },

    clearAllCharts() {
        Object.keys(this.charts).forEach(id => this.destroyChart(id));
    }
};

// ============================================================
// Small DOM helpers (this page loads neither api.js nor auth.js, so these are self-contained)
// ============================================================

function $(sel) { return document.querySelector(sel); }

function escapeHtml(value) {
    if (value === null || value === undefined) return '';
    return String(value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

// Page CSP is `script-src 'self'` (no 'unsafe-inline') — an inline <script>SharePage.init()</script>
// tag in share.html would be blocked, so the entry point lives here at the bottom of the file
// instead.
SharePage.init();
