// api/wwwroot/js/share.js — anonymous viewer for /share/{token}.
// No auth.js / api.js on this page: plain fetch() with same-origin cookies (the session cookie
// is HttpOnly and scoped to Path=/api/share — set by the server, never read/written here).
//
// Widget rendering (renderStat/renderTable/renderChartJs) is shared with js/dashboards.js via
// js/chart-render.js (window.EdmChartRender), applied here against the read-only share endpoints.
// Response shapes actually served (verified by reading
// ShareEndpoints.cs / DashboardService.GetShareViewAsync / GetWidgetDataAsync):
//   POST /api/share/{token}/session        -> 204 + Set-Cookie | 401 {success:false,error:{code:"SHARE_PIN_INVALID",...}}
//                                              | 429 {success:false,error:{code:"SHARE_LOCKED",message:"...minutes..."}}
//   GET  /api/share/{token}/dashboard      -> 404 (no/expired/revoked session) | {success:true,data:{dashboard_name, kind, has_page, widgets:[{widget_id,title,chart_type,chart_config,position}]}}
//   GET  /api/share/{token}/page           -> HTML (CSP sandbox, same-origin, requires session cookie) | bare 404 without session — dashboard kind='custom' only
//   GET  /api/share/{token}/widgets/{id}/data -> {success:true,data:{columns:[{name,type}],rows:[[...]], ...}} (compact_table shape, same as owner side)
//
// Widget DTOs here deliberately omit `sql`, `dataset_id`, `source`, `refresh_interval_sec`
// (server-side: DashboardService.ShareWidgetRow) — viewers must not learn schema/business logic,
// and there is no auto-refresh timer for the anonymous view.

const token = location.pathname.split('/').pop();

// Chart rendering pipeline (renderStat/renderTable/renderChartJs/destroyChart + the
// CHART_SERIES_COLORS palette) is shared with js/dashboards.js and lives in js/chart-render.js,
// loaded before this file — see window.EdmChartRender.

const SharePage = {
    charts: {}, // widget_id -> Chart.js instance, destroyed before re-creating
    pageEmbed: null, // EdmPageEmbed instance của dashboard custom đang mở (destroy trước khi tạo mới)

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
        if (this.pageEmbed) { this.pageEmbed.destroy(); this.pageEmbed = null; }
        $('#customView').hidden = true;
        $('#customWarning').hidden = true;
        $('#dash').hidden = true;
        $('#pin-gate').hidden = false;
        document.body.classList.remove('share-custom-full'); // rời trang custom -> bỏ full-bleed
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

        if (this.pageEmbed) { this.pageEmbed.destroy(); this.pageEmbed = null; }

        if (data.kind === 'custom' && data.has_page) {
            this.showCustomDashboard(data);
            return;
        }
        $('#customView').hidden = true;
        $('#customWarning').hidden = true;
        $('#widgetGrid').hidden = false;
        document.body.classList.remove('share-custom-full'); // dashboard dạng grid -> bỏ full-bleed

        const widgets = data.widgets || [];
        const grid = $('#widgetGrid');

        if (widgets.length === 0) {
            grid.innerHTML = '<p class="muted">Dashboard này chưa có widget nào.</p>';
            return;
        }

        grid.innerHTML = widgets.map(w => this.widgetCardHtml(w)).join('');
        widgets.forEach(w => this.loadWidgetData(w));
    },

    // Dashboard kind='custom': iframe sandbox nạp /api/share/{token}/page (cùng session cookie),
    // data bơm qua EdmPageEmbed. Share payload không có refresh_interval_sec (viewer không được
    // biết cấu hình) — refresh cố định 60s/widget ở phía shell.
    showCustomDashboard(data) {
        $('#widgetGrid').hidden = true;
        $('#widgetGrid').innerHTML = '';
        $('#customWarning').hidden = true; // reset cảnh báo cũ trước khi mount lại (giống dashboards.js)
        this.clearAllCharts();
        // Full-bleed layout cho trang custom: iframe chiếm trọn viewport dưới topbar,
        // bỏ khung card/padding của .container — xem style.css (body.share-custom-full).
        document.body.classList.add('share-custom-full');

        const view = $('#customView');
        view.hidden = false;
        view.innerHTML = '';

        this.pageEmbed = EdmPageEmbed.mount({
            container: view,
            iframeSrc: `/api/share/${token}/page`,
            widgets: (data.widgets || []).map(w => ({ widget_id: w.widget_id, title: w.title })),
            fetchWidgetData: async (wid) => {
                const res = await fetch(`/api/share/${token}/widgets/${wid}/data`);
                if (!res.ok) throw new Error('Không tải được dữ liệu widget.');
                const json = await res.json();
                return json.data || {};
            },
            onWarning: (msg) => {
                const box = $('#customWarning');
                box.hidden = false;
                box.textContent = msg;
            }
        });
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
    // Widget data + rendering (rendering delegated to EdmChartRender from js/chart-render.js,
    // endpoint swapped to the anonymous /api/share/{token}/widgets/{id}/data route)
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
        EdmChartRender.destroyChart(this.charts, widget.widget_id);
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
                EdmChartRender.destroyChart(this.charts, widget.widget_id);
                EdmChartRender.renderStat(body, columns, rows);
                break;
            case 'line':
            case 'bar':
            case 'pie':
                EdmChartRender.renderChartJs(this.charts, widget, body, columns, rows);
                break;
            case 'table':
            default:
                EdmChartRender.destroyChart(this.charts, widget.widget_id);
                EdmChartRender.renderTable(body, columns, rows);
                break;
        }
    },

    clearAllCharts() {
        Object.keys(this.charts).forEach(id => EdmChartRender.destroyChart(this.charts, id));
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
