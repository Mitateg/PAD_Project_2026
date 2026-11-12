using System.Collections.Concurrent;
using Pad.Common;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{Constants.ProxyPort}");
builder.Services.AddHttpClient("warehouse", c => c.Timeout = Constants.ProxyRequestTimeout);
var app = builder.Build();

var nodes = (Environment.GetEnvironmentVariable("PAD_WAREHOUSE_NODES") ?? "http://localhost:5001")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var sickUntil = new ConcurrentDictionary<string, DateTime>();
var cache = new ConcurrentDictionary<string, CachedResponse>();
int counter = 0;

bool IsInCooldown(string n) => sickUntil.TryGetValue(n, out var until) && until > DateTime.UtcNow;
void MarkSick(string n) => sickUntil[n] = DateTime.UtcNow + Constants.NodeFailureCooldown;
string NodeName(string n) => $"warehouse-{Array.IndexOf(nodes, n) + 1}";

string? NextNode(HashSet<string> tried)
{
    var candidates = nodes.Where(n => !tried.Contains(n) && !IsInCooldown(n)).ToList();
    if (candidates.Count == 0)
        candidates = nodes.Where(n => !tried.Contains(n)).ToList();
    if (candidates.Count == 0) return null;

    int i = Interlocked.Increment(ref counter);
    return candidates[(i & 0x7FFFFFFF) % candidates.Count];
}

app.Map("/{**path}", async (HttpContext ctx, IHttpClientFactory factory) =>
{
    var method = ctx.Request.Method;
    var isGet = HttpMethods.IsGet(method);
    var accept = ctx.Request.Headers.Accept.ToString();

    // Accept face parte din cheie, altfel JSON si XML se amesteca
    string key = $"GET|{ctx.Request.Path}|{ctx.Request.QueryString}|{accept}";

    if (isGet && cache.TryGetValue(key, out var hit) && hit.Expires > DateTime.UtcNow)
    {
        ctx.Response.StatusCode = 200;
        if (hit.ContentType is not null) ctx.Response.ContentType = hit.ContentType;
        ctx.Response.Headers["X-Cache"] = "HIT";
        ctx.Response.Headers["X-Served-By"] = hit.ServedBy;
        await ctx.Response.Body.WriteAsync(hit.Body);
        return;
    }

    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    var body = ms.ToArray();

    var tried = new HashSet<string>();
    string? node;

    while ((node = NextNode(tried)) is not null)
    {
        tried.Add(node);
        var target = node + ctx.Request.Path + ctx.Request.QueryString;

        using var req = new HttpRequestMessage(new HttpMethod(method), target);
        if (accept.Length > 0)
            req.Headers.TryAddWithoutValidation("Accept", accept);

        if (body.Length > 0)
        {
            req.Content = new ByteArrayContent(body);
            if (ctx.Request.ContentType is not null)
                req.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
        }

        try
        {
            using var resp = await factory.CreateClient("warehouse")
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

            if ((int)resp.StatusCode >= 500)
            {
                MarkSick(node);
                continue;
            }

            var respBody = await resp.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
            var contentType = resp.Content.Headers.ContentType?.ToString();
            var servedBy = NodeName(node);

            // se pune in cache doar GET cu status 200
            if (isGet && (int)resp.StatusCode == 200)
            {
                cache[key] = new CachedResponse(respBody, contentType, servedBy,
                    DateTime.UtcNow + Constants.CacheTtl);
            }

            // la scriere reusita, invalidare grosiera: tot ce tine de /employee*
            if ((HttpMethods.IsPut(method) || HttpMethods.IsPost(method)) && resp.IsSuccessStatusCode)
            {
                foreach (var k in cache.Keys.Where(k => k.StartsWith("GET|/employee")))
                    cache.TryRemove(k, out _);
            }

            ctx.Response.StatusCode = (int)resp.StatusCode;
            if (contentType is not null) ctx.Response.ContentType = contentType;
            ctx.Response.Headers["X-Cache"] = "MISS";
            ctx.Response.Headers["X-Served-By"] = servedBy;
            await ctx.Response.Body.WriteAsync(respBody);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ctx.RequestAborted.IsCancellationRequested) return;
            MarkSick(node);
        }
    }

    ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
});

app.Run();

record CachedResponse(byte[] Body, string? ContentType, string ServedBy, DateTime Expires);