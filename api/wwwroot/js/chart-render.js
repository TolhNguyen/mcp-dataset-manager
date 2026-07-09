// api/wwwroot/js/chart-render.js — shared widget-rendering pipeline used by both js/dashboards.js
// (owner view) and js/share.js (anonymous viewer). Extracted verbatim from dashboards.js's
// renderStat/renderTable/renderChartJs/destroyChart + CHART_SERIES_COLORS palette so the two pages
// stop carrying byte-for-byte copies that could drift.
//
// Functions that used to read/write page-local state (`this.charts`, a widget_id -> Chart.js
// instance map) now take that map as an explicit `charts` parameter — call sites in dashboards.js
// and share.js pass their own `this.charts` map through.
//
// Exposed as a single global namespace object (no bundler/module system in this codebase; plain
// <script> tags loaded via CSP-safe self-hosted files only, same pattern as chart.umd.min.js).

// Categorical series colors (fixed order, never cycled/re-picked per filter) come from the
// dataviz skill's validated default palette (references/palette.md), light-mode steps.
const CHART_SERIES_COLORS = [
    '#2a78d6', '#1baf7a', '#eda100', '#008300',
    '#4a3aa7', '#e34948', '#e87ba4', '#eb6834'
];

function renderStat(body, columns, rows) {
    const label = columns[0] ? escapeHtml(columns[0].name) : '';
    const value = (rows[0] && rows[0][0] !== null && rows[0][0] !== undefined)
        ? escapeHtml(String(rows[0][0]))
        : '—';
    body.innerHTML = `
        <div class="stat-value">${value}</div>
        <div class="stat-label">${label}</div>`;
}

function renderTable(body, columns, rows) {
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
}

function renderChartJs(charts, widget, body, columns, rows) {
    destroyChart(charts, widget.widget_id);

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

    charts[widget.widget_id] = new Chart(canvas, {
        type: widget.chart_type,
        data: { labels, datasets },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: { legend: { display: datasets.length > 1 || widget.chart_type === 'pie' } }
        }
    });
}

function destroyChart(charts, widgetId) {
    const existing = charts[widgetId];
    if (existing) {
        existing.destroy();
        delete charts[widgetId];
    }
}

window.EdmChartRender = { CHART_SERIES_COLORS, renderStat, renderTable, renderChartJs, destroyChart };
