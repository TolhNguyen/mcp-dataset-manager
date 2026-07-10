# Realtime Custom-HTML Dashboard — Case dashboard với endpoint xem được query

Ngày: 2026-07-10
Trạng thái: đã user duyệt (brainstorm cùng Fable)

## Vấn đề

Flow tạo dashboard hiện tại chưa phân biệt hai nhu cầu khác nhau:

1. **Báo cáo snapshot** — xem một lần, data đóng băng tại thời điểm tạo. Claude query qua MCP rồi dựng HTML artifact ngay trong chat. Đã hoạt động tốt.
2. **Dashboard realtime** — mở lại lúc nào cũng thấy data mới. Dashboard grid hiện có (widget SQL đóng băng, chạy lại mỗi lần xem) đáp ứng về data nhưng giao diện đồng khuôn, không đạt chất lượng thiết kế "như artifact" mà user muốn.

Thiếu sót thứ hai: khi Claude dựng dashboard, các câu SQL đứng sau từng biểu đồ khó soi lại — user muốn "vào xem câu query của endpoint đó luôn".

Ghi chú thuật ngữ: artifact trên claude.ai **không thể** tự fetch data (CSP chặn toàn bộ request ra ngoài, chỉ whitelist cdnjs cho thư viện JS) — đã kiểm chứng 2026-07-10. Vì vậy dashboard realtime phải host trên EDM server, không thể là artifact.

## Các quyết định đã chốt

| Quyết định | Lựa chọn |
|---|---|
| Phân loại khi tạo | Claude **phải xác định snapshot hay realtime** trước khi làm; không rõ thì hỏi user 1 câu |
| Phong cách báo cáo | **Visual-first**: ưu tiên chart/KPI tile hơn bảng số liệu; chất lượng thiết kế như artifact — áp dụng cho cả 2 loại |
| Snapshot | Giữ nguyên hiện tại: artifact trong chat, data nhúng cứng |
| Realtime — kiến trúc | **Phương án A — mở rộng dashboards hiện có**: Case dashboard = dashboard `kind='custom'` + 1 trang HTML; endpoint = `dashboard_widgets` hiện có (tái dụng validate/cache/row cap/share/archive) |
| Nơi render realtime | HTML do Claude dựng, **host trên EDM server**, xem qua link |
| Cách ly an ninh | HTML của AI chạy trong **iframe `sandbox="allow-scripts"`** (opaque origin — không cookie, không localStorage, không gọi API); data bơm một chiều qua `postMessage` từ shell tin cậy |
| Truy cập | Owner đăng nhập web app; người ngoài qua **share link+PIN có sẵn** (0008) |
| Quyền sửa query/HTML | **Chỉ owner** (JWT/PAT). Share session và dataset-scoped key: read-only, không bao giờ thấy SQL |
| Xem query ở đâu | Web app (tab Endpoints, owner-only) **và** qua MCP (`get_dashboard` trả SQL cho owner) |

## Thành phần 1 — Data model (migration 0009)

```sql
ALTER TABLE dashboards ADD COLUMN kind VARCHAR(10) NOT NULL DEFAULT 'grid';
-- 'grid' = dashboard widget-grid hiện tại (không đổi hành vi)
-- 'custom' = Case dashboard có trang HTML riêng

CREATE TABLE dashboard_pages (
    dashboard_id UUID PRIMARY KEY REFERENCES dashboards(id) ON DELETE CASCADE,
    html TEXT NOT NULL,                 -- cap 2MB, kiểm ở service
    created_by TEXT NOT NULL,           -- 'user:<email>' | 'ai'
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

- Endpoint = `dashboard_widgets`, **không đổi schema**. "Case dashboard có 10 endpoint" = dashboard `kind='custom'` có 10 widget active.
- `chart_type` của widget trong Case dashboard vẫn lưu (dùng cho hiển thị fallback/chạy thử ở tab Endpoints) nhưng trang HTML tự quyết cách vẽ.
- Không sanitize HTML server-side — ranh giới an ninh là iframe sandbox, không phải regex. Chỉ enforce cap 2MB.

## Thành phần 2 — Shell + iframe sandbox (viewer)

Shell là trang do mình viết (tin cậy), chịu trách nhiệm xác thực + fetch data. HTML của Claude là trang trình bày câm trong iframe.

```
Owner:  dashboard.html?id=…  ──JWT──>  /api/dashboards/{id}          (kind, widgets)
                                        /api/dashboards/{id}/pages    (html — owner route)
Share:  share viewer hiện có ──session──> route share no-SQL hiện có + html qua share route

Shell dựng <iframe sandbox="allow-scripts" srcdoc="…html…">
  ├─ KHÔNG allow-same-origin → opaque origin: không cookie/localStorage/API
  ├─ inject <meta CSP>: chặn hết trừ inline script/style + cdnjs.cloudflare.com
  └─ postMessage một chiều:
       iframe → shell:  { type:'edm:ready' }   (khi trang sẵn sàng)
       shell  → iframe: { type:'edm:data', endpoints:[{id,title,columns,rows,error?}] }
```

- Shell fetch data từng endpoint qua route `/data` có sẵn (owner) hoặc route share no-SQL có sẵn (viewer), rồi post vào iframe.
- Shell tự re-fetch theo `refresh_interval_sec` từng widget và post lại → trang tự cập nhật, không reload. Cache TTL server-side có sẵn vẫn áp dụng.
- Endpoint lỗi (dataset xoá, schema đổi, external DB rớt): entry mang `error` thay vì `rows` — các chart khác vẫn sống, HTML tự hiện trạng thái lỗi cho chart đó.
- Shell timeout 5s không nhận `edm:ready` → hiện cảnh báo cho owner "trang không nhận data — nhờ Claude sửa lại theo contract".

### Contract cho HTML của Claude (ghi trong tool description, kèm skeleton)

```js
window.addEventListener('message', (e) => {
  if (e.data?.type !== 'edm:data') return;
  render(e.data.endpoints); // [{id, title, columns, rows, error?}]
});
parent.postMessage({ type: 'edm:ready' }, '*');
```

## Thành phần 3 — API + MCP tools

| Route/Tool | Thay đổi |
|---|---|
| `PUT /api/dashboards/{id}/page` (+ biến thể by-name cho bridge) | **Mới.** Upsert HTML. Auth: JWT/PAT owner. Từ chối: >2MB, dashboard `kind='grid'` (lỗi rõ, không tự đổi kind), không phải owner. Tự tạo dashboard `kind='custom'` nếu chưa tồn tại (convention giống create widget by-name) |
| `GET /api/dashboards/{id}/page` | **Mới.** Owner route trả HTML; share route tương đương cho viewer session |
| `set_dashboard_html` (MCP) | **Mới.** Nhận `dashboard_name` + `html`. Trả link xem + danh sách endpoint hiện có (id, title) để Claude đối chiếu. Không cần `schema_token` (không chứa SQL). Description chứa contract + skeleton + quy tắc visual-first |
| `create_dashboard_widget` (MCP) | Giữ nguyên; sửa description: đây là cách tạo endpoint cho dashboard realtime |
| `get_dashboard` (MCP) | Bổ sung `kind`, link xem; với owner trả cả SQL từng endpoint |
| Query guide | Thêm cây quyết định snapshot/realtime + visual-first |

## Thành phần 4 — UI web app

- `dashboards.html`: badge `grid`/`custom`.
- `dashboard.html` khi `kind='custom'`:
  - **Tab "Xem"** (mặc định): shell + iframe.
  - **Tab "Endpoints"** (owner-only): bảng endpoint — tên, dataset, SQL đầy đủ, refresh interval, nút **Chạy thử** (gọi `/data`). Read-only: sửa qua Claude/MCP hoặc API PUT widget có sẵn.
- Share: luồng link+PIN hiện có, share viewer nhận diện `kind='custom'` → render shell+iframe; response share không bao giờ chứa SQL.
- Dashboard `grid` cũ: không đổi hành vi.

## Testing

- Service: cap 2MB, ownership, kind guard ('grid' bị từ chối set html), create-or-ensure custom dashboard.
- Endpoint: auth matrix cho page routes (JWT/PAT ok; dataset-key/share viewer bị chặn ghi), `get_dashboard` trả SQL đúng theo quyền.
- Share flow: viewer của Case dashboard nhận HTML + data, không có SQL trong bất kỳ response nào.
- E2E: tạo 2 endpoint → set HTML → owner mở link thấy data → tạo share link+PIN → viewer thấy data → viewer sửa query bị 403.

## Ngoài phạm vi

- Editor HTML trong web app (sửa HTML chỉ qua Claude/MCP).
- Nâng cấp giao diện dashboard grid cũ.
- Artifact tự fetch data (không khả thi vì CSP claude.ai).
