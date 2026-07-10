// api/wwwroot/js/page-embed.js — shell pump cho dashboard kind='custom'.
// Dùng bởi cả dashboards.js (owner, iframeSrc=/api/dashboards/{id}/page/raw, fetch qua Api.get)
// và share.js (viewer, iframeSrc=/api/share/{token}/page, fetch qua route share no-SQL).
//
// An ninh: HTML của AI được response tự sandbox qua header CSP `sandbox allow-scripts`
// (DashboardPageHeaders.cs). KHÔNG đặt sandbox attribute trên thẻ iframe ở đây — attribute tạo
// opaque origin ngay từ request nạp iframe nên cookie phiên (edm_token / edm_share_*) sẽ không
// được gửi và route trả 401/404. Sandbox đến từ response là đủ: trang bị opaque origin sau khi
// nạp, không đọc được cookie/localStorage, connect-src 'none' chặn mọi fetch từ bên trong.
// Data chỉ vào trang qua postMessage một chiều từ shell này.

const EdmPageEmbed = {
    mount(opts) {
        const { container, iframeSrc, widgets, fetchWidgetData, onWarning } = opts;

        const entries = new Map(); // widget_id -> {id, title, columns, rows, error?}
        widgets.forEach(w => entries.set(w.widget_id, {
            id: w.widget_id, title: w.title, columns: [], rows: [], error: 'Đang tải…'
        }));

        const iframe = document.createElement('iframe');
        iframe.src = iframeSrc;
        iframe.className = 'page-embed-frame';
        iframe.title = 'Dashboard';
        container.appendChild(iframe);

        let ready = false;
        let disposed = false;
        const intervals = [];
        const gens = new Map(); // widget_id -> generation, chặn response cũ ghi đè data mới hơn

        function post() {
            if (disposed || !iframe.contentWindow) return;
            // targetOrigin '*' là bắt buộc: document trong iframe có opaque origin (CSP sandbox)
            // nên không thể chỉ định origin cụ thể. Payload chỉ chứa data widget mà người xem
            // này vốn được phép thấy qua chính các route data — không có gì nhạy cảm hơn.
            iframe.contentWindow.postMessage(
                { type: 'edm:data', endpoints: Array.from(entries.values()) }, '*');
        }

        async function refreshWidget(w) {
            if (disposed) return;
            // Hai lần refresh cùng widget có thể chồng lấn (request chậm của tick trước còn
            // pending khi tick sau bắn). Đánh số generation: chỉ response của lần gọi mới nhất
            // được ghi vào entries — response cũ về muộn thành no-op, không ghi đè data mới.
            const gen = (gens.get(w.widget_id) || 0) + 1;
            gens.set(w.widget_id, gen);
            let entry;
            try {
                const table = await fetchWidgetData(w.widget_id) || {};
                entry = {
                    id: w.widget_id, title: w.title,
                    columns: table.columns || [], rows: table.rows || []
                };
            } catch (err) {
                entry = {
                    id: w.widget_id, title: w.title, columns: [], rows: [],
                    error: (err && err.message) || 'Không tải được dữ liệu.'
                };
            }
            if (disposed || gens.get(w.widget_id) !== gen) return;
            entries.set(w.widget_id, entry);
            if (ready) post();
        }

        const onMessage = (e) => {
            if (e.source !== iframe.contentWindow) return;
            if (!e.data || e.data.type !== 'edm:ready') return;
            ready = true;
            post();
        };
        window.addEventListener('message', onMessage);

        widgets.forEach(w => {
            refreshWidget(w);
            const sec = Math.max(30, w.refresh_interval_sec || 60);
            intervals.push(setInterval(() => refreshWidget(w), sec * 1000));
        });

        const readyTimeout = setTimeout(() => {
            if (ready || disposed) return;
            if (typeof onWarning === 'function') {
                onWarning('Trang không gửi edm:ready — HTML có thể chưa đúng contract postMessage. Nhờ Claude kiểm tra lại. Vẫn thử bơm data…');
            }
            ready = true; // trang thiếu handshake nhưng có listener thì vẫn nhận được data
            post();
        }, 5000);

        return {
            destroy() {
                disposed = true;
                window.removeEventListener('message', onMessage);
                intervals.forEach(clearInterval);
                clearTimeout(readyTimeout);
                iframe.remove();
            }
        };
    }
};
