// Lightweight fetch wrapper for the API.
// Backend serializes JSON with snake_case, so the client uses snake_case field names directly.

const TOKEN_KEY = 'edm_token';
const USER_KEY = 'edm_user';
const TOKEN_MAX_AGE_SECONDS = 60 * 60 * 24 * 7;

const Api = {
    get token() {
        const value = localStorage.getItem(TOKEN_KEY);
        syncTokenCookie(value);
        return value;
    },
    set token(value) {
        if (value) {
            localStorage.setItem(TOKEN_KEY, value);
            syncTokenCookie(value);
        } else {
            localStorage.removeItem(TOKEN_KEY);
            syncTokenCookie(null);
        }
    },

    get user() {
        const raw = localStorage.getItem(USER_KEY);
        return raw ? JSON.parse(raw) : null;
    },
    set user(value) {
        if (value) localStorage.setItem(USER_KEY, JSON.stringify(value));
        else localStorage.removeItem(USER_KEY);
    },

    clearSession() {
        this.token = null;
        this.user = null;
    },

    async request(method, path, { body, isForm = false, headers = {} } = {}) {
        const finalHeaders = { ...headers };
        if (this.token) finalHeaders['Authorization'] = `Bearer ${this.token}`;

        let payload = body;
        if (body && !isForm) {
            finalHeaders['Content-Type'] = 'application/json';
            payload = JSON.stringify(body);
        }

        const response = await fetch(path, { method, headers: finalHeaders, body: payload });

        if (response.status === 401) {
            // Stale token — push back to login.
            this.clearSession();
            if (!window.location.pathname.includes('login')) {
                window.location.replace('/login.html');
            }
            throw new ApiError('UNAUTHORIZED', 'Session expired.');
        }

        // Some endpoints return file downloads — let the caller handle those before parsing.
        const contentType = response.headers.get('content-type') || '';
        if (!contentType.includes('application/json')) {
            if (!response.ok) {
                throw new ApiError('HTTP_' + response.status, response.statusText);
            }
            return response;
        }

        const json = await response.json();

        if (!response.ok || json.success === false) {
            const err = json.error || {};
            throw new ApiError(err.code || 'HTTP_' + response.status,
                               err.message || response.statusText,
                               err.details);
        }

        return json;
    },

    get(path) { return this.request('GET', path); },
    post(path, body) { return this.request('POST', path, { body }); },
    delete(path) { return this.request('DELETE', path); },

    async downloadFile(path, fallbackFileName) {
        const response = await this.request('GET', path);
        const blob = await response.blob();
        const fileName = getDownloadFileName(response, fallbackFileName);
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');

        link.href = url;
        link.download = fileName;
        link.style.display = 'none';
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    },

    async uploadDataset(file, name) {
        const fd = new FormData();
        fd.append('file', file);
        if (name) fd.append('name', name);
        return this.request('POST', '/api/datasets', { body: fd, isForm: true });
    }
};

class ApiError extends Error {
    constructor(code, message, details) {
        super(message);
        this.code = code;
        this.details = details;
    }
}

// ============================================================
// Small DOM helpers
// ============================================================

function $(sel) { return document.querySelector(sel); }
function $$(sel) { return document.querySelectorAll(sel); }

function formatBytes(bytes) {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return (bytes / Math.pow(k, i)).toFixed(1) + ' ' + sizes[i];
}

function formatDate(value) {
    if (!value) return '—';
    return new Date(value).toLocaleString('vi-VN');
}

function escapeHtml(value) {
    if (value === null || value === undefined) return '';
    return String(value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

function syncTokenCookie(token) {
    if (token) {
        document.cookie = `${TOKEN_KEY}=${encodeURIComponent(token)}; Path=/; SameSite=Lax; Max-Age=${TOKEN_MAX_AGE_SECONDS}`;
    } else {
        document.cookie = `${TOKEN_KEY}=; Path=/; SameSite=Lax; Max-Age=0`;
    }
}

function getDownloadFileName(response, fallbackFileName) {
    const header = response.headers.get('content-disposition') || '';
    const encoded = header.match(/filename\*=UTF-8''([^;]+)/i);
    if (encoded) return decodeURIComponent(encoded[1]);

    const quoted = header.match(/filename="?([^";]+)"?/i);
    return quoted ? quoted[1] : fallbackFileName;
}
