const ApiKeys = {
    datasetId: null,

    async refresh(datasetId) {
        this.datasetId = datasetId;

        if (!this._bound) {
            $('#newKeyForm').addEventListener('submit', (e) => this.createKey(e));
            this._bound = true;
        }

        try {
            const result = await Api.get(`/api/datasets/${datasetId}/api-keys`);
            this.renderTable(result.data.api_keys || []);
        } catch (err) {
            // Probably the dataset isn't ready yet — silently no-op.
            this.renderTable([]);
        }
    },

    renderTable(keys) {
        const tbody = $('#apiKeyTable tbody');
        if (keys.length === 0) {
            tbody.innerHTML = `<tr><td colspan="5" class="muted">Chưa có API key nào.</td></tr>`;
            return;
        }

        tbody.innerHTML = keys.map(k => {
            const status = k.revoked_at
                ? `<span class="badge badge-failed">đã thu hồi</span>`
                : `<span class="badge badge-ready">đang hoạt động</span>`;

            const revokeBtn = k.revoked_at
                ? ''
                : `<button class="btn-danger" data-action="revoke" data-id="${k.api_key_id}">Thu hồi</button>`;

            return `
                <tr>
                    <td>${escapeHtml(k.name)}</td>
                    <td>${formatDate(k.created_at)}</td>
                    <td>${formatDate(k.last_used_at)}</td>
                    <td>${status}</td>
                    <td>${revokeBtn}</td>
                </tr>`;
        }).join('');

        tbody.querySelectorAll('button[data-action="revoke"]').forEach(btn => {
            btn.addEventListener('click', () => this.revokeKey(btn.dataset.id));
        });
    },

    async createKey(e) {
        e.preventDefault();
        const form = e.currentTarget;
        const fd = new FormData(form);
        const name = fd.get('name');
        const canWrite = fd.get('can_write') === 'on';
        const resultBox = $('#newKeyResult');

        try {
            const response = await Api.post(`/api/datasets/${this.datasetId}/api-keys`, { name, can_write: canWrite });
            const key = response.data.api_key;

            resultBox.hidden = false;
            resultBox.className = 'status-msg success';
            resultBox.innerHTML = `
                <strong>API key đã được tạo. Lưu ngay — sẽ không hiển thị lại:</strong>
                <div class="api-key-display">${escapeHtml(key)}</div>
                <div class="muted">
                    Cách dùng: gửi qua header <code>X-API-Key</code> đến endpoint query của dataset này.
                </div>
            `;
            form.reset();
            await this.refresh(this.datasetId);
        } catch (err) {
            resultBox.hidden = false;
            resultBox.className = 'status-msg error';
            resultBox.textContent = err.message || 'Tạo key thất bại.';
        }
    },

    async revokeKey(id) {
        if (!confirm('Thu hồi key này? Client đang dùng sẽ bị từ chối.')) return;
        try {
            await Api.delete(`/api/datasets/${this.datasetId}/api-keys/${id}`);
            await this.refresh(this.datasetId);
        } catch (err) {
            alert(err.message);
        }
    }
};
