const PROVIDER_LABELS = {
    postgresql: 'PostgreSQL',
    mysql: 'MySQL',
    mssql: 'SQL Server',
    bigquery: 'BigQuery'
};

const Connections = {
    items: [],
    editingId: null,      // null => creating a new connection
    editingProvider: null,
    wizardConnectionId: null,
    wizardTables: [],
    wizardTableFilter: '',
    wizardSelectedTables: new Set(),
    // last_test_error is persisted server-side, but the "write permission" warning on a successful
    // test is not — keep it in memory just long enough to render it once after a manual Test click.
    testWarnings: {},

    async init() {
        if (!AuthPage.requireAuth()) return;
        AuthPage.bindTopBar();

        $('#addConnectionBtn').addEventListener('click', () => this.openCreateModal());
        $('#closeConnectionModalBtn').addEventListener('click', () => this.closeConnectionModal());
        $('#cancelConnectionBtn').addEventListener('click', () => this.closeConnectionModal());
        $('#connectionForm').addEventListener('submit', (e) => this.handleSaveConnection(e));
        $$('input[name="provider"]').forEach(radio => {
            radio.addEventListener('change', () => this.updateProviderFields());
        });

        $('#closeWizardModalBtn').addEventListener('click', () => this.closeWizardModal());
        $('#cancelWizardBtn').addEventListener('click', () => this.closeWizardModal());
        document.querySelector('#wizardForm').addEventListener('submit', (e) => this.handleCreateDataset(e));
        document.querySelector('#wizardTableSearchInput').addEventListener('input', (e) => this.filterWizardTables(e.target.value));
        document.querySelector('#wizardSelectVisibleTablesInput').addEventListener('change', (e) => this.toggleVisibleWizardTables(e.target.checked));

        await this.refresh();
    },

    async refresh() {
        try {
            const result = await Api.get('/api/connections/');
            this.items = result.connections || [];
            this.renderList();
        } catch (err) {
            $('#connectionList').innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
        }
    },

    renderList() {
        const wrap = $('#connectionList');
        if (this.items.length === 0) {
            wrap.innerHTML = '<p class="muted">Chưa có kết nối nào. Hãy thêm kết nối đầu tiên!</p>';
            return;
        }

        wrap.innerHTML = this.items.map(item => {
            const statusClass =
                item.last_test_status === 'success' ? 'badge-ready'
                : item.last_test_status === 'failed' ? 'badge-failed'
                : 'badge-untested';
            const statusLabel =
                item.last_test_status === 'success' ? 'Kết nối OK'
                : item.last_test_status === 'failed' ? 'Lỗi kết nối'
                : 'Chưa kiểm tra';

            let errorBlock = '';
            if (item.last_test_status === 'failed' && item.last_test_error) {
                errorBlock = `<div class="error">${escapeHtml(item.last_test_error)}</div>`;
            } else if (this.testWarnings[item.id]) {
                errorBlock = `<div class="status-msg info">Cảnh báo: ${escapeHtml(this.testWarnings[item.id])}</div>`;
            }

            return `
            <div class="connection-item" data-id="${item.id}">
                <div class="connection-head">
                    <div>
                        <div class="connection-name">${escapeHtml(item.name)}</div>
                        <div class="connection-meta">
                            ${escapeHtml(PROVIDER_LABELS[item.provider] || item.provider)}
                            · ${escapeHtml(item.host_masked || '***')}
                            ${item.database ? '· ' + escapeHtml(item.database) : ''}
                        </div>
                        <span class="badge ${statusClass}">${statusLabel}</span>
                    </div>
                    <div class="dataset-actions">
                        <button class="btn-link" data-action="test" data-id="${item.id}">Test</button>
                        <button class="btn-link" data-action="wizard" data-id="${item.id}">Tạo dataset từ kết nối</button>
                        <button class="btn-link" data-action="edit" data-id="${item.id}">Sửa</button>
                        <button class="btn-danger" data-action="delete" data-id="${item.id}">Xoá</button>
                    </div>
                </div>
                ${errorBlock}
            </div>`;
        }).join('');

        wrap.querySelectorAll('button[data-action="test"]').forEach(btn => {
            btn.addEventListener('click', () => this.handleTest(btn.dataset.id, btn));
        });
        wrap.querySelectorAll('button[data-action="wizard"]').forEach(btn => {
            btn.addEventListener('click', () => this.openWizard(btn.dataset.id));
        });
        wrap.querySelectorAll('button[data-action="edit"]').forEach(btn => {
            btn.addEventListener('click', () => this.openEditModal(btn.dataset.id));
        });
        wrap.querySelectorAll('button[data-action="delete"]').forEach(btn => {
            btn.addEventListener('click', () => this.handleDelete(btn.dataset.id));
        });
    },

    // ============================================================
    // Add / edit connection modal
    // ============================================================

    openCreateModal() {
        this.editingId = null;
        this.editingProvider = null;
        $('#connectionModalTitle').textContent = 'Thêm kết nối';
        $('#connectionForm').reset();
        $('#connectionNameInput').value = '';
        $('#passwordHint').textContent = '';
        $('#serviceAccountHint').textContent = '';
        $$('input[name="provider"]').forEach(r => { r.disabled = false; });
        $('input[name="provider"][value="postgresql"]').checked = true;
        this.updateProviderFields();
        this.hideConnectionStatus();
        $('#connectionModal').hidden = false;
    },

    openEditModal(id) {
        const conn = this.items.find(c => c.id === id);
        if (!conn) return;

        this.editingId = id;
        this.editingProvider = conn.provider;

        $('#connectionModalTitle').textContent = `Sửa kết nối — ${conn.name}`;
        $('#connectionForm').reset();
        $('#connectionNameInput').value = conn.name;

        $$('input[name="provider"]').forEach(r => {
            r.checked = r.value === conn.provider;
            r.disabled = true; // provider cannot change once a connection is created
        });
        this.updateProviderFields();

        const form = $('#connectionForm');
        if (conn.provider === 'bigquery') {
            form.elements['dataset'].value = conn.database || '';
            $('#serviceAccountHint').textContent = '(để trống nếu giữ nguyên; nếu nhập, phải nhập lại đầy đủ project_id + dataset)';
        } else {
            form.elements['database'].value = conn.database || '';
            form.elements['username'].value = conn.username || '';
            $('#passwordHint').textContent = '(để trống nếu giữ nguyên; nếu nhập, phải nhập lại đầy đủ host/port/database/username)';
        }

        this.hideConnectionStatus();
        $('#connectionModal').hidden = false;
    },

    closeConnectionModal() {
        $('#connectionModal').hidden = true;
    },

    updateProviderFields() {
        const provider = $('input[name="provider"]:checked').value;
        $('#fieldsRelational').hidden = provider === 'bigquery';
        $('#fieldsBigQuery').hidden = provider !== 'bigquery';
    },

    hideConnectionStatus() {
        const status = $('#connectionFormStatus');
        status.hidden = true;
        status.textContent = '';
        status.className = 'status-msg';
    },

    buildConfig(provider, form) {
        if (provider === 'bigquery') {
            const config = {
                project_id: form.elements['project_id'].value.trim(),
                dataset: form.elements['dataset'].value.trim(),
                service_account_json: form.elements['service_account_json'].value.trim()
            };
            const maxBytes = form.elements['max_bytes_billed'].value.trim();
            if (maxBytes) config.max_bytes_billed = Number(maxBytes);
            return config;
        }

        const config = {
            host: form.elements['host'].value.trim(),
            database: form.elements['database'].value.trim(),
            username: form.elements['username'].value.trim(),
            password: form.elements['password'].value,
            ssl: form.elements['ssl'].checked
        };
        const port = form.elements['port'].value.trim();
        if (port) config.port = Number(port);
        return config;
    },

    async handleSaveConnection(e) {
        e.preventDefault();
        const form = e.currentTarget;
        const status = $('#connectionFormStatus');
        const btn = $('#saveConnectionBtn');
        const name = $('#connectionNameInput').value.trim();
        const provider = this.editingId
            ? this.editingProvider
            : $('input[name="provider"]:checked').value;

        btn.disabled = true;
        status.hidden = false;
        status.className = 'status-msg info';
        status.textContent = this.editingId ? 'Đang lưu…' : 'Đang tạo kết nối…';

        try {
            let connectionId = this.editingId;

            if (this.editingId) {
                const secretField = provider === 'bigquery' ? 'service_account_json' : 'password';
                const secretEntered = form.elements[secretField].value.trim().length > 0;
                const body = { name };
                if (secretEntered) {
                    body.config = this.buildConfig(provider, form);
                }
                const result = await Api.put(`/api/connections/${this.editingId}`, body);
                connectionId = result.connection.id;
            } else {
                const config = this.buildConfig(provider, form);
                const result = await Api.post('/api/connections/', { name, provider, config });
                connectionId = result.connection.id;
            }

            status.className = 'status-msg success';
            status.textContent = 'Đã lưu kết nối. Đang kiểm tra kết nối…';
            await this.refresh();

            const testResult = await Api.post(`/api/connections/${connectionId}/test`, {});
            this.applyTestResultToStatus(testResult, status);
            await this.refresh();
        } catch (err) {
            status.className = 'status-msg error';
            status.textContent = err.message || 'Không lưu được kết nối.';
        } finally {
            btn.disabled = false;
        }
    },

    applyTestResultToStatus(testResult, status) {
        const data = testResult.data || testResult;
        if (data.success) {
            status.className = 'status-msg success';
            status.textContent = data.warning
                ? `Kết nối thành công. Cảnh báo: ${data.warning}`
                : 'Kết nối thành công.';
        } else {
            status.className = 'status-msg error';
            status.textContent = `Kết nối thất bại: ${data.error || 'Không rõ lỗi.'}`;
        }
    },

    // ============================================================
    // Test / delete
    // ============================================================

    async handleTest(id, button) {
        button.disabled = true;
        delete this.testWarnings[id];
        try {
            const result = await Api.post(`/api/connections/${id}/test`, {});
            if (result.data && result.data.success && result.data.warning) {
                this.testWarnings[id] = result.data.warning;
            }
        } catch {
            // Error details are persisted server-side and shown via refresh(); ignore here.
        } finally {
            button.disabled = false;
            await this.refresh();
        }
    },

    async handleDelete(id) {
        if (!confirm('Xoá kết nối này? Hành động không thể hoàn tác.')) return;
        try {
            await Api.delete(`/api/connections/${id}`);
            await this.refresh();
        } catch (err) {
            alert(err.message);
        }
    },

    // ============================================================
    // Create-dataset-from-connection wizard
    // ============================================================

    async openWizard(connectionId) {
        this.wizardConnectionId = connectionId;
        const conn = this.items.find(c => c.id === connectionId);

        $('#wizardForm').reset();
        $('#includeSamplesInput').checked = true;
        $('#wizardNameInput').value = conn ? conn.name : '';
        document.querySelector('#wizardTableSearchInput').value = '';
        document.querySelector('#wizardSelectVisibleTablesInput').checked = false;
        document.querySelector('#wizardSelectVisibleTablesInput').indeterminate = false;
        this.wizardTables = [];
        this.wizardTableFilter = '';
        this.wizardSelectedTables = new Set();
        this.updateWizardTableSelectionSummary();
        this.hideWizardStatus();
        $('#wizardTables').innerHTML = '<p class="muted">Đang tải danh sách bảng…</p>';
        $('#wizardModal').hidden = false;

        try {
            const result = await Api.get(`/api/connections/${connectionId}/tables`);
            this.renderWizardTables(result.tables || []);
        } catch (err) {
            $('#wizardTables').innerHTML = `<p class="error">${escapeHtml(err.message)}</p>`;
        }
    },

    renderWizardTables(tables) {
        this.wizardTables = Array.isArray(tables) ? tables : [];
        this.wizardSelectedTables = new Set();
        this.wizardTableFilter = '';
        document.querySelector('#wizardTableSearchInput').value = '';
        this.renderWizardTableRows();
    },

    filterWizardTables(value) {
        this.wizardTableFilter = (value || '').trim().toLowerCase();
        this.renderWizardTableRows();
    },

    getFilteredWizardTables() {
        if (!this.wizardTableFilter) return this.wizardTables;
        return this.wizardTables.filter(t => {
            const label = ((t.source_label || '') + ' ' + (t.queryable_name || '')).toLowerCase();
            return label.includes(this.wizardTableFilter);
        });
    },

    renderWizardTableRows() {
        const wrap = document.querySelector('#wizardTables');
        const filtered = this.getFilteredWizardTables();

        if (this.wizardTables.length === 0) {
            wrap.innerHTML = '<p class=\'muted\'>Không tìm thấy bảng nào trong cơ sở dữ liệu này.</p>';
            this.updateWizardTableSelectionSummary();
            return;
        }

        if (filtered.length === 0) {
            wrap.innerHTML = '<p class=\'muted\'>Không có bảng nào khớp với từ khóa tìm kiếm.</p>';
            this.updateWizardTableSelectionSummary();
            return;
        }

        wrap.innerHTML = filtered.map(t => {
            const checked = this.wizardSelectedTables.has(t.queryable_name) ? ' checked' : '';
            return '<label class=\'table-pick-row\'>' +
                '<input type=\'checkbox\' name=\'table\' value=\'' + escapeHtml(t.queryable_name) + '\'' + checked + ' />' +
                '<span>' + escapeHtml(t.source_label || t.queryable_name) + '</span>' +
                '<span class=\'muted\'>(' + Number(t.column_count || 0) + ' cột)</span>' +
                '</label>';
        }).join('');

        wrap.querySelectorAll('input[name=table]').forEach(input => {
            input.addEventListener('change', () => {
                if (input.checked) {
                    this.wizardSelectedTables.add(input.value);
                } else {
                    this.wizardSelectedTables.delete(input.value);
                }
                this.updateWizardTableSelectionSummary();
            });
        });

        this.updateWizardTableSelectionSummary();
    },

    toggleVisibleWizardTables(checked) {
        this.getFilteredWizardTables().forEach(t => {
            if (checked) {
                this.wizardSelectedTables.add(t.queryable_name);
            } else {
                this.wizardSelectedTables.delete(t.queryable_name);
            }
        });
        this.renderWizardTableRows();
    },

    updateWizardTableSelectionSummary() {
        const selected = this.wizardSelectedTables.size;
        const total = this.wizardTables.length;
        const visible = this.getFilteredWizardTables().length;
        const selectVisible = document.querySelector('#wizardSelectVisibleTablesInput');
        const summary = document.querySelector('#wizardTableSelectionSummary');

        if (summary) {
            summary.textContent = selected + '/' + total + ' bảng được chọn' + (visible !== total ? ' - ' + visible + ' đang hiển thị' : '');
        }

        if (!selectVisible) return;
        const visibleSelected = this.getFilteredWizardTables().filter(t => this.wizardSelectedTables.has(t.queryable_name)).length;
        selectVisible.disabled = visible === 0;
        selectVisible.checked = visible > 0 && visibleSelected === visible;
        selectVisible.indeterminate = visibleSelected > 0 && visibleSelected < visible;
    },

    closeWizardModal() {
        $('#wizardModal').hidden = true;
        this.wizardConnectionId = null;
    },

    hideWizardStatus() {
        const status = $('#wizardStatus');
        status.hidden = true;
        status.textContent = '';
        status.className = 'status-msg';
    },

    async handleCreateDataset(e) {
        e.preventDefault();
        if (!this.wizardConnectionId) return;

        const name = $('#wizardNameInput').value.trim();
        const includeSamples = $('#includeSamplesInput').checked;
        const tables = Array.from(this.wizardSelectedTables);

        const status = $('#wizardStatus');
        const btn = $('#createWizardDatasetBtn');

        if (tables.length === 0) {
            status.hidden = false;
            status.className = 'status-msg error';
            status.textContent = 'Vui lòng chọn ít nhất một bảng.';
            return;
        }

        btn.disabled = true;
        status.hidden = false;
        status.className = 'status-msg info';
        status.textContent = 'Đang tạo dataset…';

        try {
            await Api.post(`/api/connections/${this.wizardConnectionId}/datasets`, {
                name,
                tables,
                include_samples: includeSamples
            });
            window.location.href = '/dashboard.html';
        } catch (err) {
            status.className = 'status-msg error';
            status.textContent = err.message || 'Không tạo được dataset.';
            btn.disabled = false;
        }
    }
};
