# mcp-bridge

Remote MCP bridge cho Excel Dataset Manager. Bridge này expose endpoint Streamable HTTP `/mcp`, dùng được với Claude.ai Custom Connectors và Claude Desktop remote MCP.

Không còn stdio/local mode. Deploy chung với API bằng Docker Compose.

## Luồng hoạt động

```text
Claude.ai / Claude Desktop
  Authorization: Bearer edm_pat_xxx
          │
          ▼
POST/GET/DELETE https://<domain>/mcp
          │
          ▼
mcp-bridge
  - validate Bearer token prefix edm_pat_
  - rate limit theo IP
  - resolve ${request.user_token}
          │
          ▼
EDM API
  X-API-Key: edm_pat_xxx
```

## Chạy trong Docker Compose

Compose ở root repo đã cấu hình sẵn:

```yaml
bridge:
  build: ./mcp-bridge
  environment:
    MCP_CONFIG: /app/tools.md
    MCP_PORT: 5848
    MCP_RATE_LIMIT_PER_MINUTE: 180
    EDM_API_URL: http://api:8080
```

Caddy route `/mcp` sang bridge:

```caddy
@mcp path /mcp /mcp/*
reverse_proxy @mcp bridge:5848
```

## Validate config

```bash
cd mcp-bridge
npm install
npm run build
EDM_API_URL=http://localhost:5847 node dist/index.js validate ./tools.md
```

## Runtime variables

Các biến `${VAR}` được resolve khi bridge load config từ env.

Các biến `${request.*}` được resolve runtime theo từng request HTTP:

- `${request.user_token}`: phần token trong `Authorization: Bearer edm_pat_xxx`
- `${request.authorization}`: nguyên header Authorization
- `${request.client_ip}`: IP client sau proxy
- `${request.session_id}`: MCP session id nếu request có

Ví dụ EDM connection:

```yaml
type: connection
id: edm
base_url: ${EDM_API_URL}
auth:
  type: header
  header: X-API-Key
  value: ${request.user_token}
```

## Rate limit

Mặc định `180 req/phút/IP`:

```env
MCP_RATE_LIMIT_PER_MINUTE=180
```

## Claude.ai Custom Connector

- URL: `https://<domain>/mcp`
- Auth: Bearer token
- Token: `edm_pat_xxx`

## Claude Desktop remote MCP

```json
{
  "mcpServers": {
    "edm": {
      "url": "https://<domain>/mcp",
      "headers": {
        "Authorization": "Bearer edm_pat_xxx"
      }
    }
  }
}
```

## Thêm tool / partner API

Sửa `tools.md`, thêm các block YAML:

- `type: connection`
- `type: tool`

Sau đó restart bridge:

```bash
docker compose restart bridge
```

Nếu config bị sai trong lúc hot reload, bridge giữ config hợp lệ cũ và ghi lỗi ra log.
