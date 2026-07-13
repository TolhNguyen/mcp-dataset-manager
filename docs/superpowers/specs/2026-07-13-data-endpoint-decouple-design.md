# Data-endpoint decouple — endpoint là nguồn dữ liệu thuần, trang tự vẽ, hỗ trợ nạp lại chủ động

Ngày: 2026-07-13
Trạng thái: đã user duyệt phương án A (brainstorm cùng Fable)

## Vấn đề

Flow custom dashboard hiện tạo cảm giác 1 endpoint = 1 chart/bảng (di sản từ dashboard grid):
`create_dashboard_widget` bắt buộc `chart_type`, mô tả tool viết theo giọng "widget hiển thị thế nào".
Hệ quả: Claude thiết kế dashboard bó theo từng query — bộ lọc, chart cần nhiều query, hoặc một
query nuôi nhiều chart đều bị gò. Engine postMessage thực chất đã là mô hình pool (bơm toàn bộ
endpoints vào trang) — vấn đề nằm ở ngữ nghĩa + 2 giới hạn nhỏ.

## Các quyết định đã chốt

| Quyết định | Lựa chọn |
|---|---|
| Mô hình | **2 tầng**: tầng DATA = endpoints (1 endpoint = 1 câu query = 1 bảng JSON, không gắn với cách hiển thị) — tầng TRÌNH BÀY = trang HTML vẽ tự do từ pool (1 endpoint nuôi nhiều chart, nhiều endpoint gộp 1 chart). Thiếu data → tạo thêm endpoint + cập nhật HTML |
| Bộ lọc | **Client-side** trên data đã bơm (SQL vẫn đóng băng, không param server-side). Claude thiết kế endpoint đủ chiều (vd tháng × BU) để trang tự lọc/gộp bằng JS |
| Row cap | Mặc định `Dashboard:MaxRowsPerWidget` **1000 → 5000** (vẫn override được qua config) |
| chart_type | **Tuỳ chọn** khi tạo endpoint — bỏ trống server mặc định `'table'`. Với custom dashboard nó chỉ là nhãn ở tab Endpoints; dashboard grid cũ không đổi (client grid vẫn gửi chart_type) |
| Nạp lại chủ động | Trang HTML được gửi message ngược **`edm:refresh`** lên shell để xin re-fetch (nút "Làm mới", sau khi đổi bộ lọc). Auto-refresh theo `refresh_interval_sec` giữ nguyên |
| Engine postMessage | Giữ nguyên `edm:ready` / `edm:data` — chỉ THÊM message ngược, không đổi shape data |

## Thành phần 1 — Server (2 thay đổi nhỏ)

1. `CreateWidgetAsync` + đường by-name: `chart_type` bỏ trống → mặc định `'table'` TRƯỚC khi vào
   `DashboardGuard.ValidateCreate` (guard giữ nguyên strict — vẫn chặn giá trị ngoài enum khi có gửi).
   Update path đã có sẵn `req.ChartType ?? existing.ChartType`, không đổi.
2. `GetWidgetDataAsync`: fallback `Dashboard:MaxRowsPerWidget` 1000 → **5000** (+ sync bảng config
   trong ARCHITECTURE.md; appsettings/compose nếu có khai key thì cập nhật giá trị mẫu).

Không migration, không đổi API shape.

## Thành phần 2 — Shell: message ngược `edm:refresh` (js/page-embed.js)

```
iframe → shell: { type:'edm:refresh' }                 // nạp lại TẤT CẢ endpoint
iframe → shell: { type:'edm:refresh', id:'<widget_id>' } // nạp lại 1 endpoint
```

- Shell nhận trong `onMessage` (cùng gate hiện có: `e.source === iframe.contentWindow` và
  `e.origin === 'null'`), re-fetch endpoint tương ứng qua `fetchWidgetData` rồi pump như thường
  (generation counter chống out-of-order đã có sẵn áp dụng luôn).
- **Rate-limit client-side**: tối thiểu 5s/endpoint giữa 2 lần refresh chủ động (chặn trang lỗi
  spam vòng lặp); request vượt hạn bị bỏ qua im lặng.
- Sự thật về độ tươi (ghi rõ trong contract cho Claude): server cache widget data theo TTL =
  `refresh_interval_sec` (tối thiểu 30s) — refresh chủ động trong TTL trả lại data cache; nút
  "Làm mới" hữu ích chủ yếu khi trang mở lâu. Cache giữ nguyên (bảo vệ DB nguồn external).
- `id` không khớp endpoint nào → bỏ qua im lặng. Sau teardown (navigation-guard/destroy) mọi
  `edm:refresh` bị bỏ qua như các message khác.

## Thành phần 3 — Ngữ nghĩa MCP + query guide (phần chính)

Viết lại theo mô hình 2 tầng:

- `create_dashboard_widget`: đổi giọng mô tả thành "tạo MỘT ENDPOINT DỮ LIỆU — 1 câu SQL trả 1
  bảng dữ liệu; KHÔNG gắn với một chart cụ thể". `chart_type` chuyển thành optional, mô tả rõ:
  chỉ là nhãn hiển thị ở tab Endpoints cho dashboard custom (grid dashboard vẫn nên gửi).
  Hướng dẫn thiết kế pool: nghĩ DATA trước (dashboard cần những bảng dữ liệu nào, đủ chiều cho
  bộ lọc), mỗi bảng = 1 endpoint, cap 5000 dòng/endpoint — cần chi tiết hơn thì tách endpoint.
- `set_dashboard_html`: bổ sung contract `edm:refresh` (kèm ghi chú rate-limit 5s + cache TTL
  server) và quy tắc trình bày: trang vẽ tự do từ `e.data.endpoints`; bộ lọc/drill-down làm
  client-side trên data đã bơm; `render()` idempotent như cũ.
- Query guide (`DefaultGuide` — "## Dashboards & reports"): cập nhật bước REALTIME thành
  "thiết kế pool data (endpoints) → dựng trang vẽ tự do → thiếu data thì thêm endpoint và cập
  nhật HTML".
- Nhất quán thuật ngữ trong 3 chỗ trên: "endpoint (dữ liệu)" cho custom flow; "widget" chỉ dùng
  cho dashboard grid.

## Thành phần 4 — Docs

- ARCHITECTURE.md: cập nhật section custom page (mô hình 2 tầng, edm:refresh, cap 5000,
  chart_type optional) + bảng config.
- API.md: `chart_type` optional ở 2 route tạo widget; cap 5000.
- README: 1 dòng nếu có chỗ đang nói 1 widget = 1 chart cho custom.
- Vận hành: tools.md production + storage/query-guide.md override phải sync lại (như đợt trước).

## Testing

- DashboardGuardTests giữ nguyên (guard vẫn strict); test mới ở tầng default: không unit-test
  được path DB → xác nhận qua build + tay/e2e.
- page-embed: `node --check`; kiểm logic rate-limit + id-không-khớp bằng đọc-lại-diff khi review.
- Full suite xanh; bump version query string các file JS/HTML đụng tới.

## Ngoài phạm vi

- Endpoint có tham số / lọc server-side (đã cân nhắc, từ chối — phá mô hình SQL đóng băng).
- Bypass cache server khi refresh chủ động.
- Đổi shape `edm:data`.
