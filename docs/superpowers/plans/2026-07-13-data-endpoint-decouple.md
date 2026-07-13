# Data-Endpoint Decouple Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Endpoint = nguồn dữ liệu thuần (1 query → 1 bảng JSON, không gắn chart), trang HTML vẽ tự do từ pool + được xin nạp lại chủ động qua `edm:refresh`; cap 5000 dòng; `chart_type` tuỳ chọn.

**Architecture:** Không đổi engine postMessage (đã là mô hình pool) — chỉ: (1) server default `chart_type='table'` + nâng row cap, (2) shell nhận message ngược `edm:refresh` có rate-limit, (3) viết lại ngữ nghĩa tool/guide theo mô hình 2 tầng DATA/TRÌNH BÀY.

**Tech Stack:** ASP.NET Core + Dapper (api/), vanilla JS (wwwroot/), YAML tool defs (mcp-bridge/tools.example.md), xUnit DB-less tests.

**Spec:** `docs/superpowers/specs/2026-07-13-data-endpoint-decouple-design.md`

## Global Constraints

- KHÔNG đổi shape `edm:data` / handshake `edm:ready`; chỉ THÊM message ngược `edm:refresh`.
- SQL vẫn đóng băng — không param server-side; bộ lọc là client-side trên data đã bơm.
- `DashboardGuard.ValidateCreate` giữ nguyên strict (vẫn chặn chart_type ngoài enum khi có gửi) — default `'table'` đặt ở tầng service TRƯỚC khi gọi guard.
- Rate-limit `edm:refresh`: tối thiểu 5000ms/endpoint, vượt hạn bỏ qua im lặng; `id` không khớp bỏ qua im lặng; sau teardown bỏ qua như mọi message.
- Server cache widget data (TTL = refresh_interval_sec, min 30s) GIỮ NGUYÊN — refresh chủ động trong TTL trả data cache; contract phải nói thật điều này.
- Unit tests không mở DB. `node --check` cho mọi file JS sửa. Bump version query `?v=20260713-refresh` cho page-embed.js ở cả dashboards.html và share.html.
- Commit `feat:`/`fix:`/`docs:` + dòng cuối `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- Test: `dotnet test tests/ExcelDatasetManager.Tests` từ repo root (hiện 393 pass).

---

### Task 1: Server — chart_type optional (default 'table') + row cap 5000

**Files:**
- Modify: `api/Services/DashboardService.cs` (CreateWidgetAsync ~dòng 250, 322; GetWidgetDataAsync ~dòng 581)
- Modify: `api/appsettings.json` (~dòng 43)

**Interfaces:**
- Produces: `POST /api/dashboards/{id}/widgets` và `POST /api/dashboards/widgets` chấp nhận body không có `chart_type` (server lưu `'table'`). `Dashboard:MaxRowsPerWidget` fallback + giá trị mẫu = 5000. Không đổi chữ ký method nào.

- [ ] **Step 1: Default chart_type ở CreateWidgetAsync**

Trong `CreateWidgetAsync`, thay dòng:

```csharp
        var fieldError = DashboardGuard.ValidateCreate(req.Title, req.Sql, req.ChartType);
```

bằng:

```csharp
        // Endpoint của dashboard custom là nguồn dữ liệu thuần — chart_type chỉ còn là nhãn
        // hiển thị ở tab Endpoints, nên cho phép bỏ trống và mặc định 'table'. Guard phía dưới
        // vẫn strict: giá trị CÓ gửi lên mà ngoài enum thì vẫn bị chặn như trước.
        var chartType = string.IsNullOrWhiteSpace(req.ChartType) ? "table" : req.ChartType;

        var fieldError = DashboardGuard.ValidateCreate(req.Title, req.Sql, chartType);
```

và trong object tham số INSERT (cùng method, ~dòng 322) thay `ChartType = req.ChartType,` bằng `ChartType = chartType,`.

(Update path đã tự merge `req.ChartType ?? existing.ChartType` — không đổi. Đường by-name dựng `CreateWidgetRequest` rồi gọi CreateWidgetAsync nên hưởng default này luôn.)

- [ ] **Step 2: Row cap 5000**

`GetWidgetDataAsync` (~dòng 581): `?? 1000` → `?? 5000`. `api/appsettings.json`: `"MaxRowsPerWidget": 1000` → `5000`. Cập nhật luôn câu doc đầu class nếu có nêu số cụ thể (kiểm tra comment dòng ~20).

- [ ] **Step 3: Build + full suite**

Run: `dotnet build api && dotnet test tests/ExcelDatasetManager.Tests`
Expected: build sạch, 393/393 PASS (defaulting nằm ở tầng service chạm DB — không unit-test được theo convention repo; ghi rõ trong report thay vì bịa test).

- [ ] **Step 4: Commit**

```bash
git add api/Services/DashboardService.cs api/appsettings.json
git commit -m "feat: chart_type tuy chon (mac dinh table) + row cap widget 5000"
```

---

### Task 2: Shell — message ngược `edm:refresh` (page-embed.js)

**Files:**
- Modify: `api/wwwroot/js/page-embed.js` (hàm `onMessage`, ~dòng 67-79; thêm state cạnh `gens` ~dòng 31)
- Modify: `api/wwwroot/dashboards.html`, `api/wwwroot/share.html` (bump `page-embed.js?v=20260713-refresh`)

**Interfaces:**
- Consumes: `refreshWidget(w)` + generation counter + các gate `disposed`/`e.source`/`e.origin==='null'` có sẵn — GIỮ NGUYÊN, không sửa.
- Produces: iframe → shell `{type:'edm:refresh'}` (tất cả) hoặc `{type:'edm:refresh', id:'<widget_id>'}` (một endpoint). Task 3 ghi contract này vào tool description.

- [ ] **Step 1: Thêm state rate-limit**

Ngay dưới dòng `const gens = new Map(); ...`:

```js
        const lastManualRefresh = new Map(); // widget_id -> timestamp lần edm:refresh gần nhất
```

- [ ] **Step 2: Mở rộng onMessage**

Thay phần thân sau các gate hiện có (giữ nguyên 4 gate: `disposed`, `e.source`, `e.origin !== 'null'`, `!e.data`) — cấu trúc mới:

```js
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
```

(`refreshWidget` sẵn có generation counter nên refresh chủ động chồng lấn với tick auto-refresh vẫn an toàn — không sửa gì thêm.)

- [ ] **Step 3: Cập nhật comment đầu file**

Câu cuối comment đầu file: "Data chỉ vào trang qua postMessage một chiều từ shell này." → sửa thành:

```js
// Data vào trang qua postMessage từ shell này; chiều ngược lại trang chỉ được gửi 2 loại
// message: edm:ready (handshake) và edm:refresh (xin nạp lại, rate-limit 5s/endpoint) — không
// có đường nào khác từ trang ra ngoài.
```

- [ ] **Step 4: Verify + bump version + commit**

Run: `node --check api/wwwroot/js/page-embed.js`
Bump trong `dashboards.html` và `share.html`: `page-embed.js?v=20260713-navguard2` → `?v=20260713-refresh`.

```bash
git add api/wwwroot/js/page-embed.js api/wwwroot/dashboards.html api/wwwroot/share.html
git commit -m "feat: page-embed nhan edm:refresh - trang xin nap lai data, rate-limit 5s/endpoint"
```

---

### Task 3: Ngữ nghĩa MCP + query guide (mô hình 2 tầng)

**Files:**
- Modify: `mcp-bridge/tools.example.md` (section `## create_dashboard_widget` ~dòng 510-590, `## set_dashboard_html` ~dòng 696-760)
- Modify: `api/Services/QueryGuideService.cs` (section "## Dashboards & reports" trong DefaultGuide, ~dòng 121-132)

**Interfaces:**
- Consumes: hành vi Task 1 (chart_type optional, cap 5000) + Task 2 (`edm:refresh`).
- Produces: text agent-facing; phải khớp 100% hành vi code.

- [ ] **Step 1: create_dashboard_widget — description mới + chart_type optional**

Thay toàn bộ `description` hiện tại bằng:

```yaml
description: |
  Tạo MỘT ENDPOINT DỮ LIỆU cho dashboard: 1 câu SQL đóng băng → 1 bảng dữ
  liệu (columns + rows, tối đa 5000 dòng). Endpoint KHÔNG gắn với một chart
  cụ thể. Với dashboard REALTIME kind='custom', trang HTML
  (set_dashboard_html) nhận CẢ POOL endpoint qua postMessage và tự do vẽ:
  một endpoint nuôi nhiều chart, nhiều endpoint gộp một chart, bộ lọc /
  drill-down làm client-side trên data đã bơm. Hãy thiết kế DATA trước:
  dashboard cần những BẢNG DỮ LIỆU nào (đủ chiều cho bộ lọc, ví dụ
  tháng × đơn vị × danh mục), mỗi bảng = 1 endpoint; thiếu data thì tạo
  thêm endpoint rồi cập nhật HTML. Với dashboard grid thường, widget vẫn
  hiển thị trực tiếp kiểu 1-widget-1-chart trên web app.
  SQL phải là SELECT/WITH read-only trên đúng dataset. Nếu dashboard_name
  chưa tồn tại, server tự tạo (kind theo dashboard_kind, mặc định grid);
  nếu tạo endpoint TRƯỚC cho dashboard realtime, truyền
  dashboard_kind:'custom' (hoặc gọi set_dashboard_html trước — cả hai thứ
  tự đều được, tên phải nhất quán).
```

Param `chart_type`: XOÁ dòng `required: true`, thay `description` bằng:

```yaml
    description: "Tuỳ chọn — bỏ trống server mặc định 'table'. Với dashboard custom đây chỉ là nhãn ở tab Endpoints (trang HTML tự quyết cách vẽ); với dashboard grid NÊN gửi vì widget render đúng theo type này."
```

`response_hint`: thay câu "Validate SQL with query_dataset first when creating analytical widgets." bằng "Validate SQL with query_dataset first. Row cap: 5000/endpoint — cần chi tiết hơn thì tách endpoint." (các dòng error codes giữ nguyên).

- [ ] **Step 2: set_dashboard_html — quy trình data-first + contract edm:refresh**

Trong `description`, thay bước 2 và 3 của QUY TRÌNH BẮT BUỘC bằng:

```
  2. REALTIME: thiết kế POOL DATA trước — dashboard cần những bảng dữ liệu
     nào (đủ chiều cho mọi bộ lọc dự kiến)? Mỗi bảng = 1 endpoint, tạo bằng
     create_dashboard_widget (cần schema_token), cùng dashboard_name —
     truyền dashboard_kind:'custom' (hoặc gọi set_dashboard_html trước rồi
     tạo endpoint sau, cả hai thứ tự đều được).
  3. Dựng trang HTML hoàn chỉnh VẼ TỰ DO từ pool (KHÔNG bó 1 endpoint = 1
     chart: một endpoint nuôi nhiều chart, nhiều endpoint gộp một chart;
     bộ lọc/drill-down chạy client-side trên data đã bơm; visual-first,
     chất lượng như artifact) rồi gọi tool này.
```

Trong CONTRACT, sau khối skeleton `edm:ready`, thêm:

```
  - Tuỳ chọn — trang được xin nạp lại data chủ động (nút "Làm mới", sau khi
    đổi bộ lọc):
      parent.postMessage({ type: 'edm:refresh' }, '*');                 // tất cả endpoint
      parent.postMessage({ type: 'edm:refresh', id: '<widget_id>' }, '*'); // một endpoint
    Shell rate-limit 5 giây/endpoint (xin quá hạn bị bỏ qua im lặng); server
    còn cache data theo refresh_interval_sec (tối thiểu 30s) nên data mới
    thật sự chỉ về sau khi cache hết hạn — nút Làm mới hữu ích khi trang mở lâu.
```

- [ ] **Step 3: Query guide — REALTIME data-first**

Trong `DefaultGuide` của `api/Services/QueryGuideService.cs`, thay dòng REALTIME hiện tại (từ "- REALTIME (data must be fresh..." đến "...returned view_url.") bằng:

```
        - REALTIME (data must be fresh every time it is opened): design the DATA POOL first —
          which tables of data does the dashboard need (detailed enough for every planned
          filter)? One DATA endpoint per table via create_dashboard_widget (same dashboard_name,
          dashboard_kind:'custom', cap 5000 rows each). Then build the page free-form from the
          pool (an endpoint is NOT one chart: one endpoint can feed many charts, several
          endpoints can merge into one; filters run client-side on the pumped data; the page may
          request a reload via edm:refresh) and call set_dashboard_html (its description contains
          the REQUIRED postMessage contract). Give the user the returned view_url. Missing data
          later -> add endpoints, then update the HTML.
```

(giữ nguyên các dòng SNAPSHOT + visual-first + owner-only SQL phía trên/dưới; giữ indent 8 space.)

- [ ] **Step 4: Verify + commit**

Run: `dotnet test tests/ExcelDatasetManager.Tests` (guide đổi → token đổi, QueryGuideServiceTests không hardcode nên vẫn pass; nếu fail thì sửa test bằng cách tính lại, không hardcode tay). Nếu bridge validator chạy được (`cd mcp-bridge && node dist/index.js validate`) thì chạy; không có env thì ghi nhận unavailable.

```bash
git add mcp-bridge/tools.example.md api/Services/QueryGuideService.cs
git commit -m "feat: ngu nghia 2 tang data/trinh bay + contract edm:refresh trong MCP tools va query guide"
```

---

### Task 4: Docs + verify tổng

**Files:**
- Modify: `docs/ARCHITECTURE.md` (section "### Custom page (kind='custom')" + bảng config `Dashboard:MaxRowsPerWidget`)
- Modify: `docs/API.md` (2 route tạo widget: chart_type optional; cap 5000)
- Modify: `README.md` (chỉ nếu có câu mô tả 1 widget = 1 chart cho custom — đọc trước, sai thì sửa 1 câu, không bloat)

**Interfaces:**
- Consumes: hành vi cuối cùng của Task 1-3.

- [ ] **Step 1: ARCHITECTURE.md**

Trong section Custom page: thêm 1 bullet sau bullet postMessage hiện có:

```markdown
- **Mô hình 2 tầng**: endpoint = nguồn dữ liệu thuần (1 SQL đóng băng → 1 bảng ≤5000 dòng, `chart_type` tuỳ chọn — chỉ là nhãn tab Endpoints); trang HTML vẽ tự do từ pool (1 endpoint nuôi nhiều chart, nhiều endpoint gộp 1 chart, bộ lọc client-side). Trang được xin nạp lại chủ động: iframe→shell `edm:refresh {id?}`, rate-limit 5s/endpoint phía shell, độ tươi vẫn chặn bởi cache TTL server.
```

Bảng config: dòng `Dashboard:MaxRowsPerWidget` — `1000` → `5000`.

- [ ] **Step 2: API.md**

Ở docs 2 route tạo widget: ghi `chart_type` tuỳ chọn (mặc định `table`), row cap 5000. Đọc format hiện có rồi sửa đúng chỗ, không thêm section mới.

- [ ] **Step 3: Verify tổng + commit**

```bash
dotnet build api && dotnet test tests/ExcelDatasetManager.Tests
node --check api/wwwroot/js/page-embed.js
```
Expected: sạch, 393/393.

```bash
git add docs/ARCHITECTURE.md docs/API.md README.md
git commit -m "docs: mo hinh 2 tang data/trinh bay, edm:refresh, cap 5000, chart_type tuy chon"
```
