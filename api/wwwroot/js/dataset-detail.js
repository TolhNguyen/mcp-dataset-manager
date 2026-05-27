const DatasetDetail = {
    datasetId: null,
    pollTimer: null,

    async init() {
        if (!AuthPage.requireAuth()) return;
        AuthPage.bindTopBar();

        this.datasetId = new URLSearchParams(window.location.search).get('id');
        if (!this.datasetId) {
            $('#datasetCard').innerHTML = '<p class="error">Thiếu tham số dataset.</p>';
            return;
        }

        $('#runQueryBtn').addEventListener('click', () => this.runQuery());

        await this.refresh();
    },

    async refresh() {
        try {
            const result = await Api.get(`/api/datasets/${this.datasetId}`);
            this.renderDataset(result.dataset);
            this.renderTables(result.tables);
            await ApiKeys.refresh(this.datasetId);

            if (result.dataset.status === 'processing') {
                this.pollTimer = setTimeout(() => this.refresh(), 3000);
            }
        } catch (err) {
            $('#datasetCard').innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
        }
    },

    renderDataset(d) {
        const statusClass =
            d.status === 'ready' ? 'badge-ready'
            : d.status === 'failed' ? 'badge-failed'
            : 'badge-processing';

        const errorBlock = d.error_message
            ? `<div class="error">${escapeHtml(d.error_message)}</div>`
            : '';

        $('#datasetCard').innerHTML = `
            <header class="card-head">
                <div>
                    <h2>${escapeHtml(d.name)}</h2>
                    <div class="muted">
                        ${escapeHtml(d.original_file_name)}
                        · ${formatBytes(d.file_size_bytes)}
                        · ${d.table_count} bảng
                        · ${d.total_rows.toLocaleString()} dòng
                    </div>
                </div>
                <span class="badge ${statusClass}">${escapeHtml(d.status)}</span>
            </header>

            <div class="muted" style="margin-top:8px">
                Tạo: ${formatDate(d.created_at)} · Xử lý xong: ${formatDate(d.processed_at)}
            </div>

            ${errorBlock}

            <div class="dataset-actions" style="margin-top:12px">
                <button class="btn-link" data-action="download" data-url="${escapeHtml(d.actions.download_manifest_url)}" data-filename="manifest.md">Tải manifest.md</button>
                <button class="btn-link" data-action="download" data-url="${escapeHtml(d.actions.download_original_url)}" data-filename="${escapeHtml(d.original_file_name)}">Tải file gốc</button>
            </div>
        `;

        $('#datasetCard').querySelectorAll('button[data-action="download"]').forEach(btn => {
            btn.addEventListener('click', () => this.handleDownload(btn.dataset.url, btn.dataset.filename, btn));
        });

        // Show subsequent cards only when the dataset is ready.
        const isReady = d.status === 'ready';
        $('#tablesCard').hidden = !isReady;
        $('#apiKeysCard').hidden = !isReady;
        $('#queryCard').hidden = !isReady;
    },

    renderTables(tables) {
        const wrap = $('#tables');
        if (!tables || tables.length === 0) {
            wrap.innerHTML = '<p class="muted">Chưa có bảng nào.</p>';
            return;
        }

        wrap.innerHTML = tables.map(t => `
            <div class="card" style="margin-top:12px">
                <h3>
                    <code>${escapeHtml(t.table_name)}</code>
                    <span class="muted">— từ ${escapeHtml(t.source_name)} (${escapeHtml(t.source_type)})</span>
                </h3>
                <div class="muted">
                    ${t.row_count.toLocaleString()} dòng · ${t.column_count} cột
                </div>
                <div class="table-scroll">
                    <table class="data-table">
                        <thead>
                            <tr>
                                <th>#</th>
                                <th>Header gốc</th>
                                <th>Tên SQL</th>
                                <th>Kiểu suy luận</th>
                                <th>Ngữ nghĩa</th>
                                <th>Null</th>
                                <th>Distinct</th>
                                <th>Ví dụ</th>
                            </tr>
                        </thead>
                        <tbody>
                        ${t.columns.map(c => `
                            <tr>
                                <td>${c.ordinal_position}</td>
                                <td>${escapeHtml(c.original_header || '')}</td>
                                <td class="mono">${escapeHtml(c.normalized_name)}</td>
                                <td>${escapeHtml(c.inferred_type || '')}</td>
                                <td>${escapeHtml(c.semantic_type || '')}</td>
                                <td>${c.null_count}</td>
                                <td>${c.distinct_count}</td>
                                <td>${escapeHtml((c.sample_values || []).slice(0, 3).join(', '))}</td>
                            </tr>
                        `).join('')}
                        </tbody>
                    </table>
                </div>
            </div>
        `).join('');
    },

    async runQuery() {
        const sql = $('#sqlInput').value.trim();
        const maxRows = parseInt($('#maxRows').value, 10) || 100;
        const status = $('#queryStatus');
        const resultBox = $('#queryResult');

        if (!sql) {
            status.hidden = false;
            status.className = 'status-msg error';
            status.textContent = 'Vui lòng nhập SQL.';
            return;
        }

        status.hidden = false;
        status.className = 'status-msg info';
        status.textContent = 'Đang chạy…';
        resultBox.hidden = true;

        try {
            const result = await Api.post(`/api/datasets/${this.datasetId}/query`, {
                query_type: 'sql',
                sql,
                options: { max_rows: maxRows, include_sql: true }
            });

            if (!result.success) {
                this.renderQueryError(result.error, result.retry_hint);
                return;
            }

            status.hidden = true;
            const r = result.result;
            $('#queryMeta').textContent =
                `${r.row_count} dòng · ${result.execution.elapsed_ms} ms`
                + (r.truncated ? ' · đã bị giới hạn' : '');

            $('#resultTable').innerHTML = this.renderResultTable(r.columns, r.rows);
            resultBox.hidden = false;
        } catch (err) {
            this.renderQueryError({ code: err.code, message: err.message, details: err.details });
        }
    },

    renderResultTable(columns, rows) {
        const headerHtml = '<thead><tr>'
            + columns.map(c => `<th>${escapeHtml(c.name)}<br><span class="muted">${escapeHtml(c.type)}</span></th>`).join('')
            + '</tr></thead>';

        const bodyHtml = '<tbody>'
            + rows.map(row =>
                '<tr>' + row.map(v => `<td class="mono">${v === null ? '<span class="muted">NULL</span>' : escapeHtml(v)}</td>`).join('') + '</tr>'
            ).join('')
            + '</tbody>';

        return headerHtml + bodyHtml;
    },

    renderQueryError(error, retryHint) {
        const status = $('#queryStatus');
        status.hidden = false;
        status.className = 'status-msg error';

        let html = `<strong>${escapeHtml(error.code)}</strong>: ${escapeHtml(error.message)}`;

        if (error.details && error.details.suggested_columns?.length) {
            html += `<br>Cột gợi ý: ${error.details.suggested_columns.map(c => `<code>${escapeHtml(c)}</code>`).join(', ')}`;
        }
        if (error.details && error.details.available_tables?.length) {
            html += `<br>Bảng có sẵn: ${error.details.available_tables.map(c => `<code>${escapeHtml(c)}</code>`).join(', ')}`;
        }
        if (retryHint?.message) {
            html += `<br>${escapeHtml(retryHint.message)}`;
        }

        status.innerHTML = html;
    },

    async handleDownload(url, filename, button) {
        button.disabled = true;
        try {
            await downloadFileWithAuth(url, filename);
        } catch (err) {
            alert(err.message || 'Không tải được file.');
        } finally {
            button.disabled = false;
        }
    }
};

async function downloadFileWithAuth(path, fallbackFileName) {
    if (typeof Api.downloadFile === 'function') {
        await Api.downloadFile(path, fallbackFileName);
        return;
    }

    const headers = {};
    if (Api.token) headers.Authorization = `Bearer ${Api.token}`;
    const response = await fetch(path, { headers });

    if (response.status === 401) {
        Api.clearSession();
        window.location.replace('/login.html');
        throw new Error('Phiên đăng nhập đã hết hạn.');
    }

    if (!response.ok) {
        throw new Error(response.statusText || `HTTP ${response.status}`);
    }

    const blob = await response.blob();
    const fileName = getDownloadFileNameFromResponse(response, fallbackFileName);
    const blobUrl = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = blobUrl;
    link.download = fileName;
    link.style.display = 'none';
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(blobUrl);
}

function getDownloadFileNameFromResponse(response, fallbackFileName) {
    const header = response.headers.get('content-disposition') || '';
    const encoded = header.match(/filename\*=UTF-8''([^;]+)/i);
    if (encoded) return decodeURIComponent(encoded[1]);

    const quoted = header.match(/filename="?([^";]+)"?/i);
    return quoted ? quoted[1] : fallbackFileName;
}
