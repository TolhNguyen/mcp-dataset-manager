# Excel Dataset Manager + Remote MCP Bridge

App cho phép upload Excel/CSV, chuẩn hoá header tiếng Việt, sinh `manifest.md`, lưu dữ liệu dạng Parquet và mở API query SQL read-only để Claude/agent phân tích dữ liệu.

Bản này đã đổi sang mô hình **deploy một lần**:

```
Claude.ai Web
        │
        │  HTTPS /mcp/{user_id}
        ▼
Caddy reverse proxy
        ├── /mcp/{user_id} ──────► mcp-bridge
        │                              │
        │                              └── X-API-Key: user_id
        ▼
Excel Dataset Manager API ───────► PostgreSQL + storage volume
```

## Điểm chính

- `api/`: C# ASP.NET Core backend, `.csproj` nằm trực tiếp trong thư mục này.
- `mcp-bridge/`: Node.js MCP bridge chạy **remote HTTP**, không còn stdio/local bridge.
- `Caddyfile`: reverse proxy HTTPS, tự xin Let's Encrypt cert khi `EDM_DOMAIN` là domain thật.
- `docker-compose.yml`: chạy PostgreSQL + API + MCP bridge + Caddy bằng một lệnh.
- Bridge nhận user id trực tiếp trên URL `/mcp/{user_id}`, rồi forward xuống API bằng `X-API-Key: {user_id}` thông qua `${request.user_token}` trong `tools.md`.
- Rate limit bridge mặc định: `180 req/phút/IP`.

## Chạy nhanh bằng Docker

```bash
cp .env.example .env
# Sửa .env: POSTGRES_PASSWORD, EDM_DOMAIN nếu deploy domain thật.
# JWT_KEY có thể để trống nếu dùng script deploy, script sẽ tự sinh.

./scripts/deploy.sh
```

Hoặc chạy thủ công:

```bash
cp .env.example .env
# Set JWT_KEY >= 32 ký tự nếu không dùng scripts/deploy.sh
docker compose up -d --build
```

URL mặc định local:

- Web/API qua Caddy: `https://localhost/` hoặc `http://localhost/`
- API direct: `http://localhost:5847/`
- MCP endpoint: `https://localhost/mcp/<user-id>`

Với production, đặt trong `.env`:

```env
EDM_DOMAIN=data.your-domain.com
HTTP_PORT=80
HTTPS_PORT=443
MCP_RATE_LIMIT_PER_MINUTE=180
```

Sau đó trỏ DNS `data.your-domain.com` về server và chạy `docker compose up -d --build`. Caddy sẽ xử lý HTTPS.

## Cấu trúc thư mục

```text
excel-dataset-manager/
├── api/                         # C# backend, .csproj nằm trực tiếp ở đây
│   ├── Program.cs
│   ├── ExcelDatasetManager.Api.csproj
│   ├── Dockerfile
│   ├── Auth/
│   ├── Services/
│   ├── Models/
│   ├── BackgroundJobs/
│   └── wwwroot/
├── mcp-bridge/                  # Remote MCP Streamable HTTP bridge
│   ├── src/
│   ├── Dockerfile
│   ├── package.json
│   ├── tools.md                 # Production config mặc định cho EDM
│   └── tools.example.md         # Ví dụ thêm partner APIs
├── docs/
├── scripts/
├── Caddyfile
├── docker-compose.yml
└── .env.example
```

## Kết nối Claude.ai Web (Custom Connector)

1. Đăng nhập dashboard EDM.
2. Copy **Claude.ai Web Custom Connector URL**. URL có dạng `https://<EDM_DOMAIN>/mcp/<user-id>`.
3. Vào [Claude.ai](https://claude.ai) → Settings → Connectors → Add custom connector.
4. Điền:
   - **Name**: Excel Dataset Manager
   - **Remote MCP server URL**: URL vừa copy
5. Nhấn **Add**. Không cần OAuth, Client ID, Client Secret hay Bearer token.

Sau khi kết nối, thử hỏi: “Liệt kê dataset của tôi”.

## MCP bridge config

File `mcp-bridge/tools.md` mặc định chỉ khai báo các tool EDM để deploy không bị thiếu env partner.

Đoạn quan trọng:

```yaml
type: connection
id: edm
base_url: ${EDM_API_URL}
auth:
  type: header
  header: X-API-Key
  value: ${request.user_token}
```

`EDM_API_URL` là biến môi trường container (`http://api:8080`). `${request.user_token}` được resolve runtime từ phần `{user_id}` trong URL `/mcp/{user_id}`.

Nếu muốn thêm partner API để đối soát, copy ví dụ trong `mcp-bridge/tools.example.md` vào `tools.md`, set env tương ứng rồi restart bridge:

```bash
docker compose restart bridge
```

## Các loại API key

| Loại | Prefix | Scope | Dùng cho |
|---|---|---|---|
| User id shortcut | UUID user | Toàn bộ datasets của user | Claude.ai Web Custom Connector đơn giản |
| Personal Access Token | `edm_pat_` | Toàn bộ datasets của user | Tích hợp dài hạn có auth |
| Dataset-scoped key | `edm_` | Một dataset | Share/query giới hạn |

## Debug

```bash
# Xem log toàn stack
docker compose logs --tail 200

# Log bridge
docker compose logs -f bridge

# Log API
docker compose logs -f api

# Kiểm tra bridge health
curl http://localhost/mcp/<user-id> -i
curl http://localhost:5847/health
```

Các lỗi thường gặp:

- `Jwt:Key must be set...`: chạy `./scripts/deploy.sh` hoặc set `JWT_KEY` trong `.env`.
- `Bad Request: use /mcp/{userId}`: connector đang trỏ vào `/mcp` nhưng thiếu user id ở cuối URL.
- Caddy không cấp cert: kiểm tra `EDM_DOMAIN` có trỏ DNS về server và port 80/443 đã mở firewall chưa.
- `.env` đổi password DB nhưng volume cũ vẫn dùng password cũ: `docker compose down -v && docker compose up -d --build`.

## Local development API

```bash
cd api
dotnet restore
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=excel_dataset_manager;Username=app;Password=app_password"
dotnet run
```

Bridge hiện ưu tiên remote HTTP đơn giản cho Claude.ai Web qua URL `/mcp/{user_id}`.
