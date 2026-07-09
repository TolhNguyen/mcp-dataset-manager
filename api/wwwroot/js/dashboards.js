// Phase D: dashboards.html — list dashboards, view a dashboard's widget grid, and
// create/edit/archive widgets. Chart rendering uses a self-hosted Chart.js (js/chart.umd.min.js,
// guarded behind `window.Chart` — if that file is ever missing, line/bar/pie widgets fall back to
// a "Chart.js chưa được tải" message while table/stat still render with plain HTML/CSS).
//
// EXACT response shapes this file was wired to (verified by reading DashboardService.cs /
// DashboardEndpoints.cs — see comments at each call site below):
//   GET  /api/dashboards               -> { success, data: { dashboards: [ dashboardDto, ... ] } }
//   GET  /api/dashboards/{id}          -> { success, data: { dashboard: dashboardDto, widgets: [ widgetDto, ... ] } }
//   POST /api/dashboards               -> { success, data: dashboardDto }
//   POST .../widgets, PUT .../widgets/{wid} -> { success, data: widgetDto }
//   DELETE .../widgets/{wid}           -> { success, data: widgetDto }  (soft-archive; sets archived_at)
//   GET  .../widgets/{wid}/data        -> { success, data: { format:"compact_table",
//                                             columns: [{name,type}], rows: [[...]], row_count,
//                                             truncated, next_cursor } }
// dashboardDto: { dashboard_id, name, description, created_by, created_at, updated_at }
// widgetDto: { widget_id, dashboard_id, dataset_id, title, sql, chart_type, chart_config,
//              refresh_interval_sec, position, source, created_by, archived_at, created_at, updated_at }
//
// Categorical series colors (fixed order, never cycled/re-picked per filter) come from the
// dataviz skill's validated default palette (references/palette.md), light-mode steps.
const CHART_SERIES_COLORS = [
    '#2a78d6', '#1baf7a', '#eda100', '#008300',
    '#4a3aa7', '#e34948', '#e87ba4', '#eb6834'
];

const DashboardsPage = {
    dashboards: [],
    currentDashboardId: null,
    currentDashboardName: '',
    widgets: [],
    timers: {},   // widget_id -> setInterval handle, cleared on every re-render/navigation
    charts: {},   // widget_id -> Chart.js instance, destroyed before re-creating
    datasetOptions: null, // cached [{dataset_id, name}] for the widget dataset picker
    editingWidgetId: null,

    async init() {
        if (!AuthPage.requireAuth()) return;
        AuthPage.bindTopBar();

        $('#newDashboardBtn').addEventListener('click', () => this.openDashboardModal());
        $('#dashboardForm').addEventListener('submit', (e) => this.handleCreateDashboard(e));
        $('#cancelDashboardBtn').addEventListener('click', () => this.closeDashboardModal());
        $('#closeDashboardModalBtn').addEventListener('click', () => this.closeDashboardModal());

        $('#addWidgetBtn').addEventListener('click', () => this.openWidgetModal());
        $('#widgetForm').addEventListener('submit', (e) => this.handleSaveWidget(e));
        $('#cancelWidgetBtn').addEventListener('click', () => this.closeWidgetModal());
        $('#closeWidgetModalBtn').addEventListener('click', () => this.closeWidgetModal());

        $('#shareBtn').addEventListener('click', () => this.openSharePanel());
        $('#closeSharePanelBtn').addEventListener('click', () => this.closeSharePanel());
        $('#shareForm').addEventListener('submit', (e) => this.handleCreateShare(e));
        $('#exportHtmlBtn').addEventListener('click', () => this.handleExportHtml());

        // Auto-refresh timers must not survive navigation away from this page.
        window.addEventListener('beforeunload', () => this.clearAllTimers());

        await this.loadDashboards();
    },

    // ============================================================
    // Dashboards list
    // ============================================================

    async loadDashboards() {
        const wrap = $('#dashboardList');
        try {
            const res = await Api.get('/api/dashboards');
            // Shape: { success, data: { dashboards: [...] } } — the array lives at data.dashboards,
            // NOT at data itself (verified in DashboardService.ListDashboardsAsync, which returns
            // ApiResult.Ok(new { dashboards = ... }), and DashboardEndpoints wraps that as `data`).
            this.dashboards = (res.data && res.data.dashboards) || [];
            this.renderDashboardList();
        } catch (err) {
            wrap.innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
        }
    },

    renderDashboardList() {
        const wrap = $('#dashboardList');
        if (this.dashboards.length === 0) {
            wrap.innerHTML = '<p class="muted">Chưa có dashboard nào. Hãy tạo dashboard đầu tiên!</p>';
            return;
        }

        wrap.innerHTML = this.dashboards.map(d => `
            <div class="dataset-item ${d.dashboard_id === this.currentDashboardId ? 'dataset-item-active' : ''}" data-id="${escapeHtml(d.dashboard_id)}">
                <div class="dataset-head">
                    <div>
                        <div class="dataset-name">
                            <a href="#" data-action="open" data-id="${escapeHtml(d.dashboard_id)}">${escapeHtml(d.name)}</a>
                        </div>
                        <div class="dataset-meta">
                            ${d.description ? escapeHtml(d.description) + ' · ' : ''}${formatDate(d.created_at)}
                        </div>
                    </div>
                    <div class="dataset-actions">
                        <button class="btn-danger" data-action="delete" data-id="${escapeHtml(d.dashboard_id)}">Xoá</button>
                    </div>
                </div>
            </div>`).join('');

        wrap.querySelectorAll('[data-action="open"]').forEach(el => {
            el.addEventListener('click', (e) => {
                e.preventDefault();
                this.selectDashboard(el.dataset.id);
            });
        });
        wrap.querySelectorAll('[data-action="delete"]').forEach(btn => {
            btn.addEventListener('click', () => this.handleDeleteDashboard(btn.dataset.id));
        });
    },

    openDashboardModal() {
        $('#dashboardForm').reset();
        $('#dashboardFormStatus').hidden = true;
        $('#dashboardModal').hidden = false;
    },

    closeDashboardModal() {
        $('#dashboardModal').hidden = true;
    },

    async handleCreateDashboard(e) {
        e.preventDefault();
        const fd = new FormData(e.currentTarget);
        const status = $('#dashboardFormStatus');
        status.hidden = true;

        try {
            await Api.post('/api/dashboards', {
                name: fd.get('name'),
                description: fd.get('description') || null
            });
            this.closeDashboardModal();
            await this.loadDashboards();
        } catch (err) {
            status.hidden = false;
            status.className = 'status-msg error';
            status.textContent = err.message || 'Không tạo được dashboard.';
        }
    },

    async handleDeleteDashboard(id) {
        if (!confirm('Xoá dashboard này? Toàn bộ widget bên trong sẽ bị xoá vĩnh viễn.')) return;
        try {
            await Api.delete(`/api/dashboards/${id}`);
            if (id === this.currentDashboardId) {
                this.clearAllTimers();
                this.currentDashboardId = null;
                $('#detailCard').hidden = true;
            }
            await this.loadDashboards();
        } catch (err) {
            alert(err.message);
        }
    },

    // ============================================================
    // Dashboard detail (widgets grid)
    // ============================================================

    async selectDashboard(id) {
        this.clearAllTimers();
        this.currentDashboardId = id;
        this.renderDashboardList();

        const grid = $('#widgetGrid');
        $('#detailCard').hidden = false;
        $('#detailTitle').textContent = 'Đang tải…';
        $('#detailDescription').textContent = '';
        grid.innerHTML = '';

        try {
            const res = await Api.get(`/api/dashboards/${id}`);
            // Shape: { success, data: { dashboard: {...}, widgets: [...] } } — verified in
            // DashboardService.GetDashboardAsync, which returns
            // ApiResult.Ok(new { dashboard = ..., widgets = ... }).
            const dashboard = res.data.dashboard;
            this.widgets = res.data.widgets || [];
            this.currentDashboardName = dashboard.name;

            $('#detailTitle').textContent = dashboard.name;
            $('#detailDescription').textContent = dashboard.description || '';

            this.renderWidgetGrid();
        } catch (err) {
            grid.innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
        }
    },

    renderWidgetGrid() {
        const grid = $('#widgetGrid');

        if (this.widgets.length === 0) {
            grid.innerHTML = '<p class="muted">Chưa có widget nào. Hãy thêm widget đầu tiên!</p>';
            return;
        }

        grid.innerHTML = this.widgets.map(w => this.widgetCardHtml(w)).join('');

        this.widgets.forEach(w => {
            const card = grid.querySelector(`[data-widget-id="${w.widget_id}"]`);
            if (!card) return;

            card.querySelector('[data-action="edit"]').addEventListener('click', () => this.openWidgetModal(w));
            card.querySelector('[data-action="archive"]').addEventListener('click', () => this.handleArchiveWidget(w.widget_id));

            this.loadWidgetData(w);
            // refresh_interval_sec is already clamped server-side (DashboardGuard.ClampRefresh,
            // floor 30s) at save time — trust the value on the widget DTO as-is.
            this.timers[w.widget_id] = setInterval(() => this.loadWidgetData(w), w.refresh_interval_sec * 1000);
        });
    },

    widgetCardHtml(w) {
        const sourceBadge = w.source === 'ai' ? '🤖 AI' : '👤 Bạn';
        return `
            <div class="widget-card" data-widget-id="${w.widget_id}">
                <div class="widget-card-head">
                    <div>
                        <div class="widget-title">${escapeHtml(w.title)}</div>
                        <span class="badge badge-source">${sourceBadge}</span>
                        <span class="badge badge-source">${escapeHtml(w.chart_type)}</span>
                    </div>
                    <div class="dataset-actions">
                        <button class="btn-link" data-action="edit">Sửa</button>
                        <button class="btn-danger" data-action="archive">Lưu trữ</button>
                    </div>
                </div>
                <div class="widget-body" id="widget-body-${w.widget_id}">
                    <p class="muted">Đang tải…</p>
                </div>
            </div>`;
    },

    async loadWidgetData(widget) {
        const body = $(`#widget-body-${widget.widget_id}`);
        if (!body) return; // widget no longer on screen (dashboard switched away)

        try {
            const res = await Api.get(`/api/dashboards/${this.currentDashboardId}/widgets/${widget.widget_id}/data`);
            // Shape: { success, data: { format:"compact_table", columns:[{name,type}], rows:[[...]],
            // row_count, truncated, next_cursor } } — verified in DuckDbQueryService/
            // ExternalQueryService's `result` object (both build the identical compact_table shape)
            // and DashboardService.GetWidgetDataAsync, which returns ApiResult.Ok(outcome.Result!)
            // where outcome.Result IS that `result` object directly (no extra nesting).
            const table = res.data;
            this.renderWidgetChart(widget, table);
        } catch (err) {
            this.renderWidgetError(widget, err.message || 'Không tải được dữ liệu widget.');
        }
    },

    renderWidgetError(widget, message) {
        this.destroyChart(widget.widget_id);
        const body = $(`#widget-body-${widget.widget_id}`);
        if (!body) return;

        body.innerHTML = `
            <div class="error">${escapeHtml(message)}</div>
            <button type="button" class="btn-link" data-action="ask-ai-fix">Nhờ AI sửa</button>
        `;
        body.querySelector('[data-action="ask-ai-fix"]').addEventListener('click', () =>
            this.copyAskAiFixPrompt(widget, message));
    },

    async copyAskAiFixPrompt(widget, errorMessage) {
        const prompt = `Widget "${widget.title}" (widget_id: ${widget.widget_id}) trong dashboard ` +
            `"${this.currentDashboardName}" đang lỗi khi chạy SQL:\n\n${errorMessage}\n\n` +
            `Hãy kiểm tra lại câu SQL của widget này và sửa cho đúng (dùng công cụ dashboard ` +
            `update_widget nếu bạn là Claude/MCP).`;
        try {
            await navigator.clipboard.writeText(prompt);
            alert('Đã copy gợi ý vào clipboard. Dán vào Claude để nhờ sửa widget.');
        } catch {
            alert('Không copy được. Nội dung gợi ý:\n\n' + prompt);
        }
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
            // Fallback path documented in the task report: chart.umd.min.js is vendored in this
            // repo (js/chart.umd.min.js) so this branch should not normally be hit — it only fires
            // if that file fails to load (e.g. blocked by CSP, missing from a deploy).
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

    clearAllTimers() {
        Object.values(this.timers).forEach(t => clearInterval(t));
        this.timers = {};
        Object.keys(this.charts).forEach(id => this.destroyChart(id));
    },

    // ============================================================
    // Widget create / edit modal
    // ============================================================

    async openWidgetModal(widget) {
        this.editingWidgetId = widget ? widget.widget_id : null;
        $('#widgetModalTitle').textContent = widget ? 'Sửa widget' : 'Thêm widget';
        $('#widgetFormStatus').hidden = true;

        const form = $('#widgetForm');
        form.reset();

        await this.ensureDatasetOptions();

        if (widget) {
            form.elements['dataset_id'].value = widget.dataset_id;
            form.elements['dataset_id'].disabled = true; // dataset never changes on update
            form.elements['title'].value = widget.title;
            form.elements['sql'].value = widget.sql;
            form.elements['chart_type'].value = widget.chart_type;
            form.elements['refresh_interval_sec'].value = widget.refresh_interval_sec;
        } else {
            form.elements['dataset_id'].disabled = false;
            form.elements['refresh_interval_sec'].value = 60;
        }

        $('#widgetModal').hidden = false;
    },

    closeWidgetModal() {
        $('#widgetModal').hidden = true;
        this.editingWidgetId = null;
    },

    async ensureDatasetOptions() {
        const select = $('#widgetDatasetSelect');
        if (this.datasetOptions) {
            this.renderDatasetOptions(select);
            return;
        }

        select.innerHTML = '<option value="">Đang tải...</option>';
        try {
            // GET /api/datasets/ returns { success, limit, datasets: [...] } (array at top level,
            // NOT wrapped in `data` — this endpoint predates the dashboards `data` envelope, see
            // DatasetEndpoints.cs's `Results.Ok(new { success = true, limit = ..., datasets = ... })`).
            const res = await Api.get('/api/datasets/');
            this.datasetOptions = (res.datasets || []).filter(d => d.status === 'ready');
            this.renderDatasetOptions(select);
        } catch (err) {
            select.innerHTML = `<option value="">${escapeHtml(err.message)}</option>`;
        }
    },

    renderDatasetOptions(select) {
        if (this.datasetOptions.length === 0) {
            select.innerHTML = '<option value="">Chưa có dataset sẵn sàng</option>';
            return;
        }
        select.innerHTML = this.datasetOptions
            .map(d => `<option value="${escapeHtml(d.dataset_id)}">${escapeHtml(d.name)}</option>`)
            .join('');
    },

    async handleSaveWidget(e) {
        e.preventDefault();
        const fd = new FormData(e.currentTarget);
        const status = $('#widgetFormStatus');
        status.hidden = true;

        const body = {
            title: fd.get('title'),
            sql: fd.get('sql'),
            chart_type: fd.get('chart_type'),
            refresh_interval_sec: parseInt(fd.get('refresh_interval_sec'), 10) || 60
        };

        try {
            if (this.editingWidgetId) {
                await Api.put(`/api/dashboards/${this.currentDashboardId}/widgets/${this.editingWidgetId}`, body);
            } else {
                body.dataset_id = fd.get('dataset_id');
                await Api.post(`/api/dashboards/${this.currentDashboardId}/widgets`, body);
            }
            this.closeWidgetModal();
            await this.selectDashboard(this.currentDashboardId);
        } catch (err) {
            status.hidden = false;
            status.className = 'status-msg error';
            status.textContent = err.message || 'Không lưu được widget.';
        }
    },

    async handleArchiveWidget(widgetId) {
        if (!confirm('Lưu trữ widget này? Widget sẽ ẩn khỏi dashboard.')) return;
        try {
            await Api.delete(`/api/dashboards/${this.currentDashboardId}/widgets/${widgetId}`);
            await this.selectDashboard(this.currentDashboardId);
        } catch (err) {
            alert(err.message);
        }
    },

    // ============================================================
    // Share management (anonymous PIN-gated viewer at /share/{token})
    // ============================================================
    //
    // Wire-verified against api/Endpoints/ShareAdminEndpoints.cs and ExportEndpoints.cs:
    //   POST   /api/dashboards/{id}/shares -> { success, data: { share_id, share_url, pin,
    //                                            expires_at, note } }  (pin shown ONCE, never
    //                                            retrievable again — server never returns it from
    //                                            GET /shares)
    //   GET    /api/dashboards/{id}/shares -> { success, data: { shares: [ { share_id,
    //                                            created_by, created_at, expires_at, view_count,
    //                                            last_viewed_at, revoked }, ... ] } }
    //   DELETE /api/shares/{shareId}       -> { success, data: { revoked: true, share_id } }
    //   POST   /api/dashboards/{id}/export -> { success, data: { download_url, expires_in_sec,
    //                                            one_time, encrypted } }

    async openSharePanel() {
        $('#shareForm').reset();
        $('#shareFormStatus').hidden = true;
        $('#shareResult').hidden = true;
        $('#exportStatus').hidden = true;
        $('#sharePanel').hidden = false;
        await this.loadShares();
    },

    closeSharePanel() {
        $('#sharePanel').hidden = true;
    },

    async loadShares() {
        const wrap = $('#shareList');
        wrap.innerHTML = '<p class="muted">Đang tải…</p>';
        try {
            const res = await Api.get(`/api/dashboards/${this.currentDashboardId}/shares`);
            const shares = (res.data && res.data.shares) || [];
            this.renderShareList(shares);
        } catch (err) {
            wrap.innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
        }
    },

    renderShareList(shares) {
        const wrap = $('#shareList');
        if (shares.length === 0) {
            wrap.innerHTML = '<p class="muted">Chưa có link chia sẻ nào đang hoạt động.</p>';
            return;
        }

        wrap.innerHTML = shares.map(s => `
            <div class="dataset-item" data-id="${escapeHtml(s.share_id)}">
                <div class="dataset-head">
                    <div>
                        <div class="dataset-name">${escapeHtml(s.created_by)}</div>
                        <div class="dataset-meta">
                            Hạn: ${formatDate(s.expires_at)}
                            · Lượt xem: ${s.view_count}
                            ${s.last_viewed_at ? '· Xem lần cuối: ' + formatDate(s.last_viewed_at) : ''}
                        </div>
                    </div>
                    <div class="dataset-actions">
                        <button class="btn-danger" data-action="revoke" data-id="${escapeHtml(s.share_id)}">Thu hồi</button>
                    </div>
                </div>
            </div>`).join('');

        wrap.querySelectorAll('[data-action="revoke"]').forEach(btn => {
            btn.addEventListener('click', () => this.handleRevokeShare(btn.dataset.id));
        });
    },

    async handleRevokeShare(shareId) {
        if (!confirm('Thu hồi link chia sẻ này? Người xem sẽ không thể truy cập nữa.')) return;
        try {
            await Api.delete(`/api/shares/${shareId}`);
            await this.loadShares();
        } catch (err) {
            alert(err.message);
        }
    },

    async handleCreateShare(e) {
        e.preventDefault();
        const fd = new FormData(e.currentTarget);
        const status = $('#shareFormStatus');
        status.hidden = true;

        const pin = fd.get('pin');
        const days = parseInt(fd.get('expires_in_days'), 10) || 30;
        const btn = $('#createShareBtn');
        btn.disabled = true;

        try {
            const res = await Api.post(`/api/dashboards/${this.currentDashboardId}/shares`, {
                pin: pin ? pin : null,
                expires_in_days: days
            });
            this.showShareResult(res.data);
            e.currentTarget.reset();
            await this.loadShares();
        } catch (err) {
            status.hidden = false;
            status.className = 'status-msg error';
            status.textContent = err.message || 'Không tạo được link chia sẻ.';
        } finally {
            btn.disabled = false;
        }
    },

    showShareResult(data) {
        const box = $('#shareResult');
        box.hidden = false;
        box.innerHTML = `
            <p style="font-weight:600;color:var(--danger);margin:0 0 8px">
                ⚠ Link và PIN chỉ hiển thị MỘT LẦN — hãy lưu lại ngay, hệ thống sẽ không hiện lại.
            </p>
            <label>
                <span>Link chia sẻ</span>
                <input type="text" readonly value="${escapeHtml(data.share_url)}" />
            </label>
            <button type="button" class="btn-link" data-copy="url">Copy link</button>
            <label style="margin-top:8px">
                <span>PIN</span>
                <input type="text" readonly value="${escapeHtml(data.pin)}" />
            </label>
            <button type="button" class="btn-link" data-copy="pin">Copy PIN</button>
            <p class="muted" style="margin-top:8px">Hết hạn: ${escapeHtml(formatDate(data.expires_at))}</p>`;

        box.querySelector('[data-copy="url"]').addEventListener('click', () => this.copyShareText(data.share_url, 'Đã copy link.'));
        box.querySelector('[data-copy="pin"]').addEventListener('click', () => this.copyShareText(data.pin, 'Đã copy PIN.'));
    },

    async copyShareText(text, message) {
        const status = $('#shareFormStatus');
        try {
            await navigator.clipboard.writeText(text);
            status.hidden = false;
            status.className = 'status-msg success';
            status.textContent = message;
        } catch {
            status.hidden = false;
            status.className = 'status-msg error';
            status.textContent = 'Không copy được. Hãy chọn nội dung và copy thủ công.';
        }
    },

    async handleExportHtml() {
        const status = $('#exportStatus');
        status.hidden = true;

        let pin = prompt('Đặt PIN để bảo vệ file xuất (tuỳ chọn — để trống nếu không cần):');
        if (pin === null) return; // user cancelled the prompt
        pin = pin.trim();

        const btn = $('#exportHtmlBtn');
        btn.disabled = true;
        btn.textContent = 'Đang xuất…';

        try {
            const res = await Api.post(`/api/dashboards/${this.currentDashboardId}/export`, { pin: pin || null });
            const data = res.data;
            window.open(data.download_url, '_blank', 'noopener');
            status.hidden = false;
            status.className = 'status-msg success';
            status.textContent = `Đã tạo file xuất. Link tải dùng được một lần, hết hạn sau ${Math.round(data.expires_in_sec / 60)} phút.`;
        } catch (err) {
            status.hidden = false;
            status.className = 'status-msg error';
            status.textContent = err.message || 'Không xuất được file.';
        } finally {
            btn.disabled = false;
            btn.textContent = 'Xuất file HTML';
        }
    }
};
