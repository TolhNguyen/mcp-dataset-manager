const Dashboard = {
    pollTimer: null,

    async init() {
        if (!AuthPage.requireAuth()) return;
        AuthPage.bindTopBar();

        $('#refreshBtn').addEventListener('click', () => this.refresh());
        $('#uploadForm').addEventListener('submit', (e) => this.handleUpload(e));

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
                        <span class="badge ${statusClass}">${escapeHtml(item.status)}</span>
                    </div>
                    <div class="dataset-actions">
                        <a class="btn-link" href="${item.actions.download_manifest_url}">manifest.md</a>
                        <a class="btn-link" href="${item.actions.download_original_url}">File gốc</a>
                        <button class="btn-danger" data-action="delete" data-id="${item.dataset_id}">Xoá</button>
                    </div>
                </div>
                ${errorBlock}
            </div>`;
        }).join('');

        wrap.querySelectorAll('button[data-action="delete"]').forEach(btn => {
            btn.addEventListener('click', () => this.handleDelete(btn.dataset.id));
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
    }
};
