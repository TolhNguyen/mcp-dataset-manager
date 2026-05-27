const AuthPage = {
    bindLogin() {
        const form = $('#loginForm');
        const errorBox = $('#error');
        const btn = $('#submitBtn');

        form.addEventListener('submit', async (e) => {
            e.preventDefault();
            errorBox.hidden = true;
            btn.disabled = true;
            btn.textContent = 'Đang đăng nhập…';

            try {
                const fd = new FormData(form);
                const result = await Api.post('/api/auth/login', {
                    email: fd.get('email'),
                    password: fd.get('password')
                });
                Api.token = result.token;
                Api.user = result.user;
                window.location.replace('/dashboard.html');
            } catch (err) {
                errorBox.textContent = err.message || 'Đăng nhập thất bại.';
                errorBox.hidden = false;
                btn.disabled = false;
                btn.textContent = 'Đăng nhập';
            }
        });
    },

    bindRegister() {
        const form = $('#registerForm');
        const errorBox = $('#error');
        const btn = $('#submitBtn');

        form.addEventListener('submit', async (e) => {
            e.preventDefault();
            errorBox.hidden = true;
            btn.disabled = true;
            btn.textContent = 'Đang tạo tài khoản…';

            try {
                const fd = new FormData(form);
                const result = await Api.post('/api/auth/register', {
                    email: fd.get('email'),
                    password: fd.get('password')
                });
                Api.token = result.token;
                Api.user = result.user;
                window.location.replace('/dashboard.html');
            } catch (err) {
                errorBox.textContent = err.message || 'Đăng ký thất bại.';
                errorBox.hidden = false;
                btn.disabled = false;
                btn.textContent = 'Đăng ký';
            }
        });
    },

    // Shared logout binding for pages that include the top bar.
    bindTopBar() {
        const emailEl = $('#userEmail');
        if (emailEl && Api.user) emailEl.textContent = Api.user.email;

        const logoutBtn = $('#logoutBtn');
        if (logoutBtn) {
            logoutBtn.addEventListener('click', () => {
                Api.clearSession();
                window.location.replace('/login.html');
            });
        }
    },

    requireAuth() {
        if (!Api.token) {
            window.location.replace('/login.html');
            return false;
        }
        return true;
    }
};
