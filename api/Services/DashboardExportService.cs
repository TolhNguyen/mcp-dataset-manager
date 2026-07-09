using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExcelDatasetManager.Api.Models;

namespace ExcelDatasetManager.Api.Services;

/// <summary>AES-GCM with a PBKDF2-derived key. Cipher output = ciphertext||tag so the
/// browser can decrypt it directly with WebCrypto (which expects the tag appended).</summary>
public static class ExportCrypto
{
    public const int Iterations = 150_000;

    public static (string SaltB64, string IvB64, string CipherB64) Encrypt(string pin, string plaintextJson)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = Encoding.UTF8.GetBytes(plaintextJson);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(iv, plain, cipher, tag);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(iv),
                Convert.ToBase64String(cipher.Concat(tag).ToArray()));
    }

    public static string Decrypt(string pin, string saltB64, string ivB64, string cipherB64)
    {
        var salt = Convert.FromBase64String(saltB64);
        var iv = Convert.FromBase64String(ivB64);
        var all = Convert.FromBase64String(cipherB64);
        var cipher = all[..^16];
        var tag = all[^16..];
        var key = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(iv, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>Builds a fully self-contained snapshot HTML for a dashboard: Chart.js inlined,
/// widget data embedded (optionally AES-GCM-encrypted behind a PIN), timestamp banner.</summary>
public class DashboardExportService(DashboardService dashboards, IWebHostEnvironment env)
{
    public async Task<ApiResult<string>> BuildHtmlAsync(Guid userId, Guid dashboardId, string? pin, CancellationToken ct)
    {
        var view = await dashboards.GetShareViewAsync(userId, dashboardId, ct);
        if (!view.Success) return ApiResult<string>.Fail(view.Error!.Code, view.Error.Message);

        // Chạy frozen SQL từng widget đúng 1 lần, gom {widget_id -> data}.
        var viewJson = JsonSerializer.SerializeToElement(view.Data, JsonOpts);
        var widgetData = new Dictionary<string, JsonElement>();
        foreach (var w in viewJson.GetProperty("widgets").EnumerateArray())
        {
            var widgetId = w.GetProperty("widget_id").GetGuid();
            var data = await dashboards.GetWidgetDataAsync(userId, dashboardId, widgetId, ct);
            if (data.Success)
            {
                widgetData[widgetId.ToString()] = JsonSerializer.SerializeToElement(data.Data, JsonOpts);
            }
        }

        var payloadJson = JsonSerializer.Serialize(new { view = viewJson, data = widgetData }, JsonOpts);
        var chartJs = await File.ReadAllTextAsync(Path.Combine(env.WebRootPath, "js", "chart.umd.min.js"), ct);
        var stamp = DateTime.Now.ToString("HH:mm dd/MM/yyyy");
        var name = viewJson.GetProperty("dashboard_name").GetString() ?? "Dashboard";

        string body;
        if (string.IsNullOrEmpty(pin))
        {
            body = $"<script>window.__EDM_PAYLOAD__ = {payloadJson};</script>";
        }
        else
        {
            var (salt, iv, cipher) = ExportCrypto.Encrypt(pin, payloadJson);
            body = $$"""
                <script>
                window.__EDM_ENC__ = { salt: "{{salt}}", iv: "{{iv}}", cipher: "{{cipher}}", iterations: {{ExportCrypto.Iterations}} };
                </script>
                """;
        }

        var html = BuildShell(name, stamp, chartJs, body, encrypted: !string.IsNullOrEmpty(pin));
        return ApiResult<string>.Ok(html);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static string BuildShell(string name, string stamp, string chartJs, string payloadBlock, bool encrypted) => $$"""
        <!DOCTYPE html>
        <html lang="vi"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{System.Net.WebUtility.HtmlEncode(name)}} — snapshot</title>
        <style>
        body{font-family:system-ui,sans-serif;margin:0;background:#f4f5f7;color:#1c1e21}
        header{background:#fff;padding:12px 20px;border-bottom:1px solid #e0e2e6}
        header .stamp{color:#8a8f98;font-size:13px}
        .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(360px,1fr));gap:16px;padding:16px}
        .card{background:#fff;border:1px solid #e0e2e6;border-radius:8px;padding:12px}
        .card h3{margin:0 0 8px;font-size:15px}
        table{border-collapse:collapse;width:100%;font-size:13px}
        td,th{border:1px solid #e0e2e6;padding:4px 8px;text-align:left}
        #pin-gate{max-width:340px;margin:80px auto;background:#fff;padding:24px;border-radius:8px;border:1px solid #e0e2e6}
        </style>
        <script>{{chartJs}}</script>
        {{payloadBlock}}
        </head><body>
        <header><strong>{{System.Net.WebUtility.HtmlEncode(name)}}</strong>
          <span class="stamp">— Snapshot lúc {{stamp}} (số liệu đóng băng tại thời điểm xuất)</span></header>
        {{(encrypted ? """
        <div id="pin-gate"><h3>Nhập PIN để mở báo cáo</h3>
          <input id="pin" type="password" inputmode="numeric" style="width:100%;padding:8px">
          <button onclick="unlock()" style="margin-top:8px;padding:8px 16px">Mở</button>
          <p id="pin-err" style="color:#c0392b;display:none">PIN sai.</p></div>
        """ : "")}}
        <div class="grid" id="grid"></div>
        <script>
        async function unlock() {
          const enc = window.__EDM_ENC__;
          const pin = document.getElementById('pin').value;
          try {
            const dec = new TextDecoder();
            const b64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));
            const keyMaterial = await crypto.subtle.importKey('raw', new TextEncoder().encode(pin), 'PBKDF2', false, ['deriveKey']);
            const key = await crypto.subtle.deriveKey(
              { name: 'PBKDF2', salt: b64(enc.salt), iterations: enc.iterations, hash: 'SHA-256' },
              keyMaterial, { name: 'AES-GCM', length: 256 }, false, ['decrypt']);
            const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: b64(enc.iv) }, key, b64(enc.cipher));
            window.__EDM_PAYLOAD__ = JSON.parse(dec.decode(plain));
            document.getElementById('pin-gate').remove();
            render();
          } catch (e) { document.getElementById('pin-err').style.display = 'block'; }
        }
        function render() {
          const p = window.__EDM_PAYLOAD__;
          if (!p) return;
          const grid = document.getElementById('grid');
          for (const w of p.view.widgets) {
            const card = document.createElement('div'); card.className = 'card';
            card.innerHTML = '<h3></h3>'; card.querySelector('h3').textContent = w.title;
            const d = p.data[w.widget_id];
            if (!d) { card.append('Không có dữ liệu.'); grid.append(card); continue; }
            // GetWidgetDataAsync's compact_table shape: columns = [{name, type}], rows = value[][].
            const cols = (d.columns || []).map(c => c.name), rows = d.rows || [];
            if (w.chart_type === 'table' || !window.Chart) {
              const t = document.createElement('table');
              t.innerHTML = '<thead><tr></tr></thead><tbody></tbody>';
              for (const c of cols) { const th = document.createElement('th'); th.textContent = c; t.tHead.rows[0].append(th); }
              for (const r of rows.slice(0, 100)) {
                const tr = t.tBodies[0].insertRow();
                for (const v of r) tr.insertCell().textContent = v === null ? '' : String(v);
              }
              card.append(t);
            } else {
              const cv = document.createElement('canvas'); card.append(cv);
              const labels = rows.map(r => r[0]);
              const datasets = cols.slice(1).map((c, i) => ({ label: c, data: rows.map(r => r[i + 1]) }));
              new Chart(cv, { type: w.chart_type === 'stat' ? 'bar' : w.chart_type, data: { labels, datasets } });
            }
            grid.append(card);
          }
        }
        render();
        </script></body></html>
        """;
}
