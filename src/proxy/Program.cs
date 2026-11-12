using Pad.Common;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{Constants.ProxyPort}");
builder.Services.AddHttpClient("warehouse", c => c.Timeout = Constants.ProxyRequestTimeout);
var app = builder.Build();

var nodes = (Environment.GetEnvironmentVariable("PAD_WAREHOUSE_NODES") ?? "http://localhost:5001")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

int counter = 0;

string NextNode()
{
    int i = Interlocked.Increment(ref counter);
    return nodes[(i & 0x7FFFFFFF) % nodes.Length];
}

app.Map("/{**path}", async (HttpContext ctx, IHttpClientFactory factory) =>
{
    var node = NextNode();
    var target = node + ctx.Request.Path + ctx.Request.QueryString;

    using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);

    // cele DOUA antete care trebuie duse mai departe
    if (ctx.Request.Headers.TryGetValue("Accept", out var accept))
        req.Headers.TryAddWithoutValidation("Accept", accept.ToString());

    if (ctx.Request.ContentLength > 0)
    {
        req.Content = new StreamContent(ctx.Request.Body);
        if (ctx.Request.ContentType is not null)
            req.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
    }

    try
    {
        using var resp = await factory.CreateClient("warehouse")
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

        ctx.Response.StatusCode = (int)resp.StatusCode;
        if (resp.Content.Headers.ContentType is not null)
            ctx.Response.ContentType = resp.Content.Headers.ContentType.ToString();
        ctx.Response.Headers["X-Served-By"] = node;
        await resp.Content.CopyToAsync(ctx.Response.Body);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
    }
});

app.Run();