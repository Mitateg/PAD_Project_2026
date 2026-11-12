
using System.Collections.Concurrent;
using Pad.Common;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls($"http://0.0.0.0:{Constants.ProxyPort}");
builder.Services.AddHttpClient("warehouse", c =>
    c.Timeout = Constants.ProxyRequestTimeout);

var app = builder.Build();

// Configurarea nodurilor warehouse
var nodes = (Environment.GetEnvironmentVariable("PAD_WAREHOUSE_NODES")
    ?? "http://localhost:5001,http://localhost:5002,http://localhost:5003")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

// Conectarea la Redis
ConnectionMultiplexer? redis = null;
IDatabase? db = null;

var redisUrl = Environment.GetEnvironmentVariable("PAD_REDIS");

if (!string.IsNullOrWhiteSpace(redisUrl))
{
    try
    {
        var options = ConfigurationOptions.Parse(redisUrl);
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 1;
        options.ConnectTimeout = 2000;
        options.AsyncTimeout = 500;
        options.SyncTimeout = 500;
        options.BacklogPolicy = BacklogPolicy.FailFast;

        redis = ConnectionMultiplexer.Connect(options);
        db = redis.GetDatabase();

        app.Logger.LogInformation("Redis connection initialized.");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Redis unavailable. Client tracking disabled.");
    }
}
else
{
    app.Logger.LogWarning("PAD_REDIS is not configured. Client tracking disabled.");
}

// Endpoint: verificarea stării proxy-ului
app.MapGet("/health", () =>
{
    return Results.Json(new
    {
        status = "healthy",
        redis = db is not null ? "connected" : "unavailable",
        warehouseNodes = nodes
    });
});

// Structuri pentru proxy
var sickUntil = new ConcurrentDictionary<string, DateTime>();
var cache = new ConcurrentDictionary<string, CachedResponse>();
var requestsPerNode = new ConcurrentDictionary<string, long>();

int counter = 0;
long totalRequests = 0;
long cacheHits = 0;
long cacheMisses = 0;

bool IsInCooldown(string n) =>
    sickUntil.TryGetValue(n, out var until) && until > DateTime.UtcNow;

void MarkSick(string n) =>
    sickUntil[n] = DateTime.UtcNow + Constants.NodeFailureCooldown;

string NodeName(string n) =>
    $"warehouse-{Array.IndexOf(nodes, n) + 1}";

string? NextNode(HashSet<string> tried)
{
    var candidates = nodes
        .Where(n => !tried.Contains(n) && !IsInCooldown(n))
        .ToList();

    if (candidates.Count == 0)
        candidates = nodes.Where(n => !tried.Contains(n)).ToList();

    if (candidates.Count == 0)
        return null;

    int i = Interlocked.Increment(ref counter);
    return candidates[(i & 0x7FFFFFFF) % candidates.Count];
}

// Înregistrarea activității clienților
app.Use(async (ctx, next) =>
{
    Interlocked.Increment(ref totalRequests);

    if (db is not null)
    {
        try
        {
            var clientId = ctx.Request.Headers["X-Client-Id"].ToString();

            if (string.IsNullOrWhiteSpace(clientId))
                clientId = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var key = (RedisKey)$"client:{clientId}";
            var now = DateTime.UtcNow.ToString("O");

            await db.HashSetAsync(key, "firstSeen", now, When.NotExists);
            await db.HashSetAsync(key, "lastSeen", now);
            await db.HashIncrementAsync(key, "requests");
            await db.HashSetAsync(key, "lastPath", ctx.Request.Path.ToString());

            await db.KeyExpireAsync(key, Constants.ClientRecordTtl);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Redis client tracking failed.");
        }
    }

    await next();
});

// Endpoint: lista clienților activi
app.MapGet("/proxy/clients", async () =>
{
    if (redis is null || db is null)
        return Results.Json(new
        {
            error = "Redis unavailable",
            clients = Array.Empty<object>()
        });

    try
    {
        var server = redis.GetServer(redis.GetEndPoints().First());
        var clients = new List<object>();

        foreach (var key in server.Keys(pattern: "client:*"))
        {
            var fields = await db.HashGetAllAsync(key);
            var ttl = await db.KeyTimeToLiveAsync(key);

            var data = fields.ToDictionary(
                f => f.Name.ToString(),
                f => f.Value.ToString());

            clients.Add(new
            {
                id = key.ToString()["client:".Length..],
                data,
                ttlSeconds = ttl?.TotalSeconds
            });
        }

        return Results.Json(new
        {
            count = clients.Count,
            clients
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not retrieve Redis clients.");

        return Results.Json(new
        {
            error = "Could not retrieve clients",
            clients = Array.Empty<object>()
        });
    }
});

// Endpoint: statisticile proxy-ului
app.MapGet("/proxy/stats", () =>
{
    var sickNodes = sickUntil
        .Where(kv => kv.Value > DateTime.UtcNow)
        .Select(kv => NodeName(kv.Key))
        .ToArray();

    return Results.Json(new
    {
        totalRequests = Interlocked.Read(ref totalRequests),
        cacheHits = Interlocked.Read(ref cacheHits),
        cacheMisses = Interlocked.Read(ref cacheMisses),
        requestsPerNode = requestsPerNode,
        sickNodes = sickNodes
    });
});

// Endpoint principal: redirecționare către warehouse
app.Map("/{**path}", async (HttpContext ctx, IHttpClientFactory factory) =>
{
    var method = ctx.Request.Method;
    var isGet = HttpMethods.IsGet(method);
    var accept = ctx.Request.Headers.Accept.ToString();
    var cacheable = isGet && !ctx.Request.Path.StartsWithSegments("/update");

    string key =
        $"GET|{ctx.Request.Path}|{ctx.Request.QueryString}|{accept}";

    if (cacheable && cache.TryGetValue(key, out var hit))
    {
        if (hit.Expires > DateTime.UtcNow)
        {
            Interlocked.Increment(ref cacheHits);

            ctx.Response.StatusCode = 200;

            if (hit.ContentType is not null)
                ctx.Response.ContentType = hit.ContentType;

            ctx.Response.Headers["X-Cache"] = "HIT";
            ctx.Response.Headers["X-Served-By"] = hit.ServedBy;

            await ctx.Response.Body.WriteAsync(hit.Body);
            return;
        }

        cache.TryRemove(key, out _);
    }

    if (cacheable)
        Interlocked.Increment(ref cacheMisses);

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

        foreach (var h in new[] { "If-Modified-Since", "From" })
            if (ctx.Request.Headers.TryGetValue(h, out var hv))
                req.Headers.TryAddWithoutValidation(h, hv.ToString());

        if (body.Length > 0)
        {
            req.Content = new ByteArrayContent(body);

            if (ctx.Request.ContentType is not null)
                req.Content.Headers.TryAddWithoutValidation(
                    "Content-Type", ctx.Request.ContentType);
        }

        var servedBy = NodeName(node);

        requestsPerNode.AddOrUpdate(servedBy, 1, (_, old) => old + 1);

        try
        {
            using var resp = await factory.CreateClient("warehouse")
                .SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    ctx.RequestAborted);

            if ((int)resp.StatusCode >= 500)
            {
                MarkSick(node);
                continue;
            }

            var respBody =
                await resp.Content.ReadAsByteArrayAsync(ctx.RequestAborted);

            var contentType = resp.Content.Headers.ContentType?.ToString();

            // Cache doar pentru GET cu status 200
            if (cacheable && (int)resp.StatusCode == 200)
            {
                cache[key] = new CachedResponse(
                    respBody,
                    contentType,
                    servedBy,
                    DateTime.UtcNow + Constants.CacheTtl);
            }

            // Invalidarea cache-ului după PUT/POST reușit
            if ((HttpMethods.IsPut(method) || HttpMethods.IsPost(method))
                && resp.IsSuccessStatusCode)
            {
                foreach (var k in cache.Keys.Where(k =>
                    k.StartsWith("GET|/employee")))
                {
                    cache.TryRemove(k, out _);
                }
            }

            ctx.Response.StatusCode = (int)resp.StatusCode;

            if (contentType is not null)
                ctx.Response.ContentType = contentType;

            ctx.Response.Headers["X-Cache"] = "MISS";
            ctx.Response.Headers["X-Served-By"] = servedBy;

            await ctx.Response.Body.WriteAsync(respBody);
            return;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException)
        {
            if (ctx.RequestAborted.IsCancellationRequested)
                return;

            MarkSick(node);
        }
    }

    ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
});

app.Run();

record CachedResponse(
    byte[] Body,
    string? ContentType,
    string ServedBy,
    DateTime Expires);
