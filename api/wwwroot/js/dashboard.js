const Dashboard = {
    pollTimer: null,

    async init() {
        if (!AuthPage.requireAuth()) return;
        AuthPage.bindTopBar();

        $('#refreshBtn').addEventListener('click', () => this.refresh());
        $('#uploadForm').addEventListener('submit', (e) => this.handleUpload(e));
        $('#copyWebUrlBtn').addEventListener('click', () => this.copyText($('#claudeWebConnectorUrl').value, 'Đã copy URL connector.'));
        $('#claudeWebConnectorUrl').value = getClaudeWebConnectorUrl();

        await this.refresh();
    },

    async refresh() {
        try {
            const result = await Api.get('/api/datasets/');
            this.renderQuota(result.limit);
            this.renderList(result.datasets || []);
            this.schedulePollIfNeeded(result.datasets || []);
        } catch (err) {
            $('#datasetList').innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
        }
    },

    renderQuota(limit) {
        if (!limit) return;
        $('#quota').textContent = `Đã dùng ${limit.used}/${limit.max_datasets} dataset (còn ${limit.remaining}).`;
        $('#uploadBtn').disabled = !limit.can_upload;
    },

    renderList(items) {
        const wrap = $('#datasetList');
        if (items.length === 0) {
            wrap.innerHTML = '<p class="muted">Chưa có dataset nào. Hãy tải lên file đầu tiên!</p>';
            return;
        }

        wrap.innerHTML = items.map(item => {
            const statusClass =
                item.status === 'ready' ? 'badge-ready'
                : item.status === 'failed' ? 'badge-failed'
                : 'badge-processing';

            const errorBlock = item.error_message
                ? `<div class="error">${escapeHtml(item.error_message)}</div>`
                : '';

            const sourceBadge = item.source_kind === 'external_db'
                ? `🗄 ${escapeHtml(item.file_type)}`
                : `📄 ${escapeHtml(item.file_type)}`;

            return `
            <div class="dataset-item" data-id="${item.dataset_id}">
                <div class="dataset-head">
                    <div>
                        <div class="dataset-name">
                            <a href="/dataset-detail.html?id=${item.dataset_id}">${escapeHtml(item.name)}</a>
                        </div>
                        <div class="dataset-meta">
                            ${escapeHtml(item.original_file_name)}
                            · ${formatBytes(item.file_size_bytes)}
                            · ${item.table_count} bảng
                            · ${item.total_rows.toLocaleString()} dòng
                            · ${formatDate(item.created_at)}
                        </div>
                        <span class="badge badge-source">${sourceBadge}</span>
                        <span class="badge ${statusClass}">${escapeHtml(item.status)}</span>
                    </div>
                    <div class="dataset-actions">
                        <button class="btn-link" data-action="download" data-url="${escapeHtml(item.actions.download_manifest_url)}" data-filename="manifest.md">manifest.md</button>
                        <button class="btn-link" data-action="download" data-url="${escapeHtml(item.actions.download_original_url)}" data-filename="${escapeHtml(item.original_file_name)}">File gốc</button>
                        <button class="btn-danger" data-action="delete" data-id="${item.dataset_id}">Xoá</button>
                    </div>
                </div>
                ${errorBlock}
            </div>`;
        }).join('');

        wrap.querySelectorAll('button[data-action="delete"]').forEach(btn => {
            btn.addEventListener('click', () => this.handleDelete(btn.dataset.id));
        });
        wrap.querySelectorAll('button[data-action="download"]').forEach(btn => {
            btn.addEventListener('click', () => this.handleDownload(btn.dataset.url, btn.dataset.filename, btn));
        });
    },

    schedulePollIfNeeded(items) {
        if (this.pollTimer) {
            clearTimeout(this.pollTimer);
            this.pollTimer = null;
        }
        if (items.some(d => d.status === 'processing')) {
            this.pollTimer = setTimeout(() => this.refresh(), 3000);
        }
    },

    async handleUpload(e) {
        e.preventDefault();
        const form = e.currentTarget;
        const fd = new FormData(form);
        const file = fd.get('file');
        const name = fd.get('name');

        if (!file || file.size === 0) return;

        const status = $('#uploadStatus');
        const btn = $('#uploadBtn');

        status.hidden = false;
        status.className = 'status-msg info';
        status.textContent = 'Đang tải lên…';
        btn.disabled = true;

        try {
            const result = await Api.uploadDataset(file, name);
            status.className = 'status-msg success';
            status.textContent = `Đã upload "${result.dataset.name}". Đang xử lý nền…`;
            form.reset();
            await this.refresh();
        } catch (err) {
            status.className = 'status-msg error';
            status.textContent = err.message || 'Tải lên thất bại.';
        } finally {
            btn.disabled = false;
        }
    },

    async handleDelete(id) {
        if (!confirm('Xoá dataset này? Hành động không thể hoàn tác.')) return;
        try {
            await Api.delete(`/api/datasets/${id}`);
            await this.refresh();
        } catch (err) {
            alert(err.message);
        }
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
    },

    async copyText(text, message) {
        const status = $('#mcpStatus');
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
    }
};

function getMcpUrl() {
    const userId = Api.user?.id || 'missing-user-id';
    if (window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1') {
        return `http://${window.location.hostname}:5848/mcp/${userId}`;
    }

    return `${window.location.origin}/mcp/${userId}`;
}

function getClaudeWebConnectorUrl() {
    return getMcpUrl();
}

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
