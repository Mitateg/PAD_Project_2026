using System.Collections.Concurrent;
using Pad.Common;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{Constants.ProxyPort}");
builder.Services.AddHttpClient("warehouse", c => c.Timeout = Constants.ProxyRequestTimeout);
var app = builder.Build();

var nodes = (Environment.GetEnvironmentVariable("PAD_WAREHOUSE_NODES") ?? "http://localhost:5001")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var sickUntil = new ConcurrentDictionary<string, DateTime>();
int counter = 0;

bool IsInCooldown(string n) => sickUntil.TryGetValue(n, out var until) && until > DateTime.UtcNow;
void MarkSick(string n) => sickUntil[n] = DateTime.UtcNow + Constants.NodeFailureCooldown;

// alege urmatorul nod, sarind peste cele deja incercate la aceasta cerere
string? NextNode(HashSet<string> tried)
{
    var candidates = nodes.Where(n => !tried.Contains(n) && !IsInCooldown(n)).ToList();
    if (candidates.Count == 0)
        candidates = nodes.Where(n => !tried.Contains(n)).ToList();   // toate par picate: incercam oricum
    if (candidates.Count == 0) return null;

    int i = Interlocked.Increment(ref counter);
    return candidates[(i & 0x7FFFFFFF) % candidates.Count];
}

app.Map("/{**path}", async (HttpContext ctx, IHttpClientFactory factory) =>
{
    // corpul trebuie tinut in memorie ca sa-l putem retrimite
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    var body = ms.ToArray();

    var tried = new HashSet<string>();
    string? node;

    while ((node = NextNode(tried)) is not null)
    {
        tried.Add(node);
        var target = node + ctx.Request.Path + ctx.Request.QueryString;

        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);

        if (ctx.Request.Headers.TryGetValue("Accept", out var accept))
            req.Headers.TryAddWithoutValidation("Accept", accept.ToString());

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
                continue;   // reincercam pe alt nod
            }

            ctx.Response.StatusCode = (int)resp.StatusCode;
            if (resp.Content.Headers.ContentType is not null)
                ctx.Response.ContentType = resp.Content.Headers.ContentType.ToString();
            ctx.Response.Headers["X-Served-By"] = node;
            await resp.Content.CopyToAsync(ctx.Response.Body);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ctx.RequestAborted.IsCancellationRequested) return;   // clientul a plecat
            MarkSick(node);
        }
    }

    // toate nodurile au esuat
    ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
});

app.Run();