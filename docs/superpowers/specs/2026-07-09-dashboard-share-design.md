# Dashboard Share Design — Link + PIN cho người xem ngoài, export HTML tĩnh

Ngày: 2026-07-09
Trạng thái: đã user duyệt (brainstorm cùng Fable)

## Vấn đề

Dashboard (Phase D) hiện chỉ chủ tài khoản đăng nhập web mới xem được (`/data` là JwtOnly). Nhu cầu thực tế: Claude (hoặc user) tạo dashboard rồi **gửi link cho sếp/nhân viên** — người xem không có tài khoản EDM. Cần một lớp chia sẻ an toàn, và một đường export file tĩnh khi muốn gửi qua email/chat không cần server.

## Các quyết định đã chốt

| Quyết định | Lựa chọn |
|---|---|
| Người xem xác thực | **Link + PIN** — không cần tài khoản; link lộ vẫn chưa xem được ngay |
| Data khi xem | **Live** — chạy lại frozen SQL của widget mỗi lần xem (qua cache TTL sẵn có) |
| Nơi host | **EDM server** (share view) là chủ lực; **export HTML tĩnh** (snapshot) là phụ |
| Phạm vi 1 link | **1 link = 1 dashboard**; một dashboard có nhiều share song song (mỗi người 1 link+PIN, thu hồi độc lập), cap 10 share sống/dashboard |
| Cơ chế link | **Share token lưu DB (hash) + PIN hash + viewer session cookie** — chọn thay vì signed-URL stateless vì cần thu hồi từng link + audit lượt xem |
| Quyền chạy SQL của viewer | **Không có.** Viewer chỉ gọi được 3 endpoint cố định; data chạy đúng frozen SQL của widget qua path `GetWidgetDataAsync` hiện có. Không endpoint nào của viewer nhận SQL |
| AI (PAT) được làm gì | Tạo share, list share (metadata), revoke share, export. Không bao giờ đọc lại được token/PIN sau khi tạo |

## Thành phần 1 — Data model (migration 0008)

```sql
CREATE TABLE dashboard_shares (
    id UUID PRIMARY KEY,
    dashboard_id UUID NOT NULL REFERENCES dashboards(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE, -- chủ dashboard
    token_hash TEXT NOT NULL UNIQUE,      -- SHA-256 của share token, không lưu token gốc
    pin_hash TEXT NOT NULL,               -- PBKDF2 (ASP.NET KeyDerivation)
    created_by TEXT NOT NULL,             -- 'user:<email>' | 'ai'
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ NULL,
    failed_pin_count INT NOT NULL DEFAULT 0,
    locked_until TIMESTAMPTZ NULL,
    view_count INT NOT NULL DEFAULT 0,
    last_viewed_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_dashboard_shares_dashboard ON dashboard_shares(dashboard_id);
```

- Token: `shr_` + 40 hex (~160 bit từ RandomNumberGenerator). So khớp: hash rồi lookup theo `token_hash` (constant-time bản chất vì so hash).
- PIN: mặc định auto-sinh 6 chữ số nếu không truyền; user/AI truyền được PIN tự chọn (4–32 ký tự). Hash bằng PBKDF2 ≥100k iterations, salt riêng từng share.
- Token + PIN **chỉ trả về đúng 1 lần** trong response tạo share.

## Thành phần 2 — Luồng người xem (đúng 3 endpoint, anonymous)

```
GET  /share/{token}                      → trang HTML: nhập PIN, hoặc dashboard shell nếu cookie còn hạn
POST /api/share/{token}/session {pin}    → đúng PIN: set cookie phiên-người-xem; sai: đếm + backoff
GET  /api/share/{token}/dashboard        → metadata + widgets (KHÔNG có SQL)
GET  /api/share/{token}/widgets/{wid}/data → chạy frozen SQL của widget
```

### Resolve share (mọi endpoint viewer đều chạy đầu tiên)

Hash token → lookup → share phải: tồn tại, `revoked_at IS NULL`, `expires_at > NOW()`. Fail bất kỳ điều kiện nào → **404 chung chung** (không phân biệt "không tồn tại" / "hết hạn" / "bị thu hồi" — không tạo oracle).

### PIN + lockout

- `POST session`: nếu `locked_until > NOW()` → 429 kèm thời gian còn lại (làm tròn phút). Sai PIN → `failed_pin_count++`; chạm mỗi bội số 5 → `locked_until = NOW() + 15min * 2^(failed_pin_count/5 - 1)`. Đúng PIN → reset count, set cookie.
- Rate limiter riêng cho route session (chặt hơn limiter "query"), khoá theo cặp share+IP.

### Viewer session cookie

- Giá trị: payload `{share_id, expires}` bảo vệ bằng **ASP.NET Data Protection** (ký + mã hoá, không phải JWT của user).
- Thuộc tính: `HttpOnly; Secure; SameSite=Lax`, path giới hạn `/share` + `/api/share`, TTL ~12h.
- **Mỗi lần gọi dashboard/data đều resolve lại share từ DB** — revoke/expire thắng cookie ngay lập tức.

### Data path

- Widget phải thuộc đúng `dashboard_id` của share.
- Thực thi qua `DashboardService.GetWidgetDataAsync` với `userId` = chủ share (data chạy như chủ): tái dùng nguyên re-validate frozen SQL mỗi lần chạy, cache TTL theo `updated_at` ticks, row caps. Viewer KHÔNG đi qua path nào nhận SQL từ request.
- Response viewer: không field `sql`; `view_count++` + `last_viewed_at` khi load trang dashboard (không phải mỗi widget call); log IP mức Information.
- Rate limit: dùng limiter "query" hiện có cho route data.

## Thành phần 3 — Endpoint quản lý + MCP tools

| Endpoint | Auth | Ghi chú |
|---|---|---|
| `POST /api/dashboards/{id}/shares` | JWT + PAT | body `{pin?, expires_in_days?}` (mặc định 30, max 90). Trả `{share_id, share_url, pin, expires_at}` một lần. Quá 10 share sống → `SHARE_LIMIT_REACHED` |
| `GET /api/dashboards/{id}/shares` | JWT + PAT | metadata: id, created_by, expires_at, view_count, last_viewed_at — không token/PIN |
| `DELETE /api/shares/{shareId}` | JWT + PAT | set `revoked_at`. AI được revoke (hướng an toàn). Mọi endpoint quản lý đều scope theo `user_id` của caller — không đụng được share của user khác |

- `share_url` build từ `EDM_PUBLIC_URL` (đã bắt buộc https).
- UI: tab "Chia sẻ" trong trang dashboard — tạo/list/revoke, hiển thị PIN 1 lần.
- MCP tools mới (tools.md + tools.example.md): `share_dashboard`, `list_dashboard_shares`, `revoke_dashboard_share`, `export_dashboard`. Description dặn AI: đưa link và PIN cho user, khuyên gửi qua 2 kênh tách nhau; PIN không xem lại được.

## Thành phần 4 — Export HTML tĩnh (snapshot)

- `POST /api/dashboards/{id}/export` (JWT + PAT), body `{pin?}`:
  1. Chạy frozen SQL từng widget đúng 1 lần (qua path GetWidgetDataAsync, cache được phép dùng).
  2. Sinh HTML tự chứa: Chart.js inline (bản self-host sẵn có), data nhúng JSON, banner "Snapshot lúc HH:mm dd/MM/yyyy" + tên dashboard.
  3. Có `pin`: payload JSON mã hoá **AES-GCM**, key = PBKDF2(PIN, salt, ≥100k iter); trang mở ra hỏi PIN, giải mã bằng WebCrypto trong browser — sai PIN thì AES-GCM tự fail authentication, không đọc được gì. Không `pin`: file trần, coi như gửi file Excel.
- Response: `{download_url, expires_in: 600}` — URL ký sẵn, sống 10 phút, dùng 1 lần (không nhét cả HTML qua MCP). File tạo trong thư mục tạm của storage, dọn sau khi tải hoặc hết hạn.

## Hardening checklist

- [ ] Token ≥160-bit random; DB chỉ lưu SHA-256.
- [ ] PIN hash PBKDF2 ≥100k iter, salt riêng; lockout backoff như Thành phần 2.
- [ ] 404 chung chung cho mọi token không hợp lệ — không oracle.
- [ ] Cookie: Data Protection, HttpOnly, Secure, SameSite=Lax, path giới hạn, TTL 12h; revoke thắng cookie.
- [ ] Headers trang share + data: `X-Robots-Tag: noindex`, `Referrer-Policy: no-referrer`, CSP self-only, `Cache-Control: no-store` cho JSON data.
- [ ] Viewer surface = đúng 3 endpoint; không endpoint viewer nào nhận SQL; response không chứa SQL.
- [ ] Rate limiter riêng cho POST PIN; limiter "query" cho data.
- [ ] Audit: created_by, view_count, last_viewed_at, log IP lượt xem, log PIN fail.
- [ ] Share URL từ `EDM_PUBLIC_URL`; qua IIS/ARR là HTTP thường (không dính buffer SSE).
- [ ] Export có PIN = AES-GCM thật; không bao giờ quảng cáo PIN-che-JS là bảo mật.

## Luồng đầy đủ

```
User: "share dashboard KPI cho sếp, hạn 2 tuần"
Claude → share_dashboard(dashboard_id, expires_in_days: 14)
       ← {share_url: "https://edm.../share/shr_ab..", pin: "482913", expires_at}
Claude → đưa user link + PIN (khuyên gửi 2 kênh riêng)

Sếp    → mở link → nhập PIN → xem dashboard live (frozen SQL, cache TTL)
Link lộ → user/Claude: revoke_dashboard_share(share_id) → cookie + link chết ngay

User: "xuất file gửi email"
Claude → export_dashboard(dashboard_id, pin: "1234")
       ← {download_url} → user tải file HTML mã hoá, gửi kèm PIN riêng
```

## Testing

- Unit: token gen/hash/resolve (revoked/expired/không tồn tại → null); PIN verify + lockout backoff (5 sai → khoá 15', 10 sai → 30'); cookie protector round-trip + tamper → reject; widget-thuộc-share check; export encrypt/decrypt round-trip + PIN sai fail; cap 10 share; expires_in_days clamp 1–90.
- E2e (curl): tạo share qua PAT → GET /share 200 → POST session sai PIN ×5 → 429 → đúng PIN → cookie → GET data 200 (không có sql trong response) → revoke → GET data 404 ngay với cookie cũ (404 chung chung, nhất quán nguyên tắc không-oracle) → token hết hạn → 404.
- UI: tab share tạo/hiển thị PIN 1 lần/revoke.

## Ngoài phạm vi

- Viewer accounts / role hệ thống.
- Gói nhiều dashboard trong 1 link (phase 2 nếu cần).
- Embed iframe vào site khác, custom domain, watermark.
- Chỉnh sửa từ phía viewer — share là read-only tuyệt đối.
- Notification cho chủ khi có lượt xem/PIN fail (có log, chưa có notify).
