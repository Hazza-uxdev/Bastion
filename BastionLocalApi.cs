using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SecureVault.Models;

namespace SecureVault;

/// <summary>
/// Localhost HTTP bridge for browser extension autofill.
/// A per-session token is written to %APPDATA%\Bastion\extension_token.txt
/// so the extension can connect without manual pasting.
/// </summary>
public class BastionLocalApi : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpListener _listener = new();
    private readonly Vault _vault;
    private readonly Action? _saveVault;
    private readonly string _token;
    private readonly CancellationTokenSource _cts = new();

    public static readonly string TokenFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Bastion", "extension_token.txt");

    public string Token => _token;
    public int Port => 59432;

    public BastionLocalApi(Vault vault, Action? saveVault = null)
    {
        _vault = vault;
        _saveVault = saveVault;

        var dir = Path.GetDirectoryName(TokenFilePath)!;
        Directory.CreateDirectory(dir);

        _token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
                        .Replace("+", "-").Replace("/", "_").Replace("=", "");
        File.WriteAllText(TokenFilePath, _token);

        _listener.Prefixes.Add($"http://localhost:{Port}/bastion/");
    }

    public void Start()
    {
        try { _listener.Start(); Task.Run(ListenLoop); }
        catch { /* Port in use — silently skip */ }
    }

    public void Stop()
    {
        _cts.Cancel();
        // Delete token file so stale token can't be used after vault is locked
        try { if (File.Exists(TokenFilePath)) File.Delete(TokenFilePath); } catch { }
        try { _listener.Stop(); } catch { }
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(ctx));
            }
            catch { break; }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        ctx.Response.ContentType = "application/json";

        try
        {
            if (!TryApplyCors(ctx))
            {
                ctx.Response.StatusCode = 403;
                Write(ctx, "{\"error\":\"Forbidden origin\"}");
                return;
            }

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "";

            // GET /bastion/token — returns token without auth (only reachable from localhost)
            // Used by extension on first install to auto-configure
            if (path.EndsWith("/token"))
            {
                // Only allow from localhost
                var remote = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "";
                if (remote != "127.0.0.1" && remote != "::1" && remote != "::ffff:127.0.0.1")
                {
                    ctx.Response.StatusCode = 403;
                    Write(ctx, "{\"error\":\"Forbidden\"}");
                    return;
                }
                Write(ctx, $"{{\"token\":\"{_token}\"}}");
                return;
            }

            var tok = ctx.Request.Headers["X-Bastion-Token"] ?? "";
            if (tok != _token)
            {
                ctx.Response.StatusCode = 401;
                Write(ctx, "{\"error\":\"Unauthorized\"}");
                return;
            }

            // GET /bastion/ping
            if (path.EndsWith("/ping"))
            {
                Write(ctx, "{\"status\":\"ok\",\"version\":\"1.0.1\"}");
                return;
            }

            // GET /bastion/search?url=example.com
            if (path.EndsWith("/search"))
            {
                if (!_vault.Settings.AutofillEnabled) { Write(ctx, "[]"); return; }
                var query = ctx.Request.QueryString["url"] ?? "";
                var matches = _vault.Entries
                    .Where(e => HostsMatch(e.Url, query))
                    .Select(e => new { e.Id, e.Title, e.Username, e.Url })
                    .ToList();
                Write(ctx, JsonSerializer.Serialize(matches, JsonOptions));
                return;
            }

            // GET /bastion/fill?id=xxx
            if (path.EndsWith("/fill"))
            {
                if (!_vault.Settings.AutofillEnabled) { ctx.Response.StatusCode = 403; Write(ctx, "{\"error\":\"Autofill disabled\"}"); return; }
                var id = ctx.Request.QueryString["id"] ?? "";
                var entry = _vault.Entries.FirstOrDefault(e => e.Id == id);
                if (entry == null) { ctx.Response.StatusCode = 404; Write(ctx, "{\"error\":\"Not found\"}"); return; }
                Write(ctx, JsonSerializer.Serialize(new { entry.Username, entry.Password }, JsonOptions));
                return;
            }

            if (path.EndsWith("/exists") && ctx.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                var body = reader.ReadToEnd();
                var request = JsonSerializer.Deserialize<SaveCredentialRequest>(body, JsonOptions);
                if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                {
                    ctx.Response.StatusCode = 400;
                    Write(ctx, "{\"error\":\"Missing credential fields\"}");
                    return;
                }

                var existing = FindCredential(request);
                var exactMatch = existing != null && existing.Password == request.Password;
                Write(ctx, JsonSerializer.Serialize(new { exists = exactMatch, usernameMatch = existing != null }, JsonOptions));
                return;
            }

            if (path.EndsWith("/save") && ctx.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                var body = reader.ReadToEnd();
                var request = JsonSerializer.Deserialize<SaveCredentialRequest>(body, JsonOptions);
                if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                {
                    ctx.Response.StatusCode = 400;
                    Write(ctx, "{\"error\":\"Missing credential fields\"}");
                    return;
                }

                var host = NormalizeHost(request.Url);
                var existing = FindCredential(request);
                if (existing == null)
                {
                    _vault.Entries.Add(new VaultEntry
                    {
                        Title = string.IsNullOrWhiteSpace(request.Title) ? host : request.Title,
                        Url = request.Url,
                        Username = request.Username,
                        Password = request.Password,
                        UpdatedAt = DateTime.Now
                    });
                }
                else
                {
                    existing.Password = request.Password;
                    existing.UpdatedAt = DateTime.Now;
                }
                _saveVault?.Invoke();
                Write(ctx, "{\"status\":\"saved\"}");
                return;
            }

            ctx.Response.StatusCode = 404;
            Write(ctx, "{\"error\":\"Unknown endpoint\"}");
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            Write(ctx, JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions));
        }
    }

    private VaultEntry? FindCredential(SaveCredentialRequest request)
    {
        var host = NormalizeHost(request.Url);
        return _vault.Entries.FirstOrDefault(e =>
            HostsMatch(e.Url, host) &&
            string.Equals(e.Username, request.Username, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryApplyCors(HttpListenerContext ctx)
    {
        var origin = ctx.Request.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin))
            return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return false;

        if (!IsExtensionOrigin(originUri))
            return false;

        ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "X-Bastion-Token,Content-Type";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        ctx.Response.Headers["Vary"] = "Origin";
        return true;
    }

    private static bool IsExtensionOrigin(Uri origin)
        => origin.Scheme.Equals("chrome-extension", StringComparison.OrdinalIgnoreCase)
        || origin.Scheme.Equals("moz-extension", StringComparison.OrdinalIgnoreCase)
        || origin.Scheme.Equals("ms-browser-extension", StringComparison.OrdinalIgnoreCase);

    private static void Write(HttpListenerContext ctx, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private static bool HostsMatch(string entryUrl, string queryUrl)
    {
        var entryHost = NormalizeHost(entryUrl);
        var queryHost = NormalizeHost(queryUrl);
        if (string.IsNullOrEmpty(entryHost) || string.IsNullOrEmpty(queryHost)) return false;

        return entryHost == queryHost ||
               entryHost.EndsWith("." + queryHost, StringComparison.OrdinalIgnoreCase) ||
               queryHost.EndsWith("." + entryHost, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHost(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value)) return "";

        if (!value.Contains("://")) value = "https://" + value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            value = uri.Host;
        else
            value = value.Replace("https://", "").Replace("http://", "").Split('/')[0];

        return value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? value[4..] : value;
    }

    public void Dispose() => Stop();

    private class SaveCredentialRequest
    {
        public string Url { get; set; } = "";
        public string Title { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
