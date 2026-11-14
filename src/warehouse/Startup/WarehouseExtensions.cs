using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Pad.Warehouse.Services;

namespace Pad.Warehouse.Startup;

/// <summary>Transforma BadRequestException din serviciu in HTTP 400 cu un mesaj.</summary>
public class BadRequestFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not BadRequestException ex)
            return;

        context.Result = new BadRequestObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Bad request",
            Detail = ex.Message,
        });
        context.ExceptionHandled = true;
    }
}

public static class WarehouseExtensions
{
    /// <summary>
    /// Returneaza in raspuns X-Correlation-Id-ul din cerere (curatat). Folosita si de Program.cs, si de testele
    /// de integrare, ca testul sa verifice exact codul real.
    /// </summary>
    public static IApplicationBuilder UseCorrelationIdEcho(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            string? corr = CorrelationId.Clean(context.Request.Headers[CorrelationId.Header].FirstOrDefault());
            if (corr is not null)
                context.Response.Headers[CorrelationId.Header] = corr;

            await next();
        });

    /// <summary>
    /// Configurarea MVC a warehouse-ului, folosita si de Program.cs, si de testele de integrare,
    /// ca 406 si 400 sa se comporte la fel in ambele.
    ///
    ///  - ReturnHttpNotAcceptable = true: un Accept pe care nu-l putem produce (ex. text/plain)
    ///    primeste 406. Fara el, ASP.NET ar returna JSON in loc de 406.
    ///  - AddXmlSerializerFormatters: pe langa JSON, putem raspunde si cu XML (si citi XML in corp).
    /// </summary>
    public static IMvcBuilder AddWarehouseMvc(this IServiceCollection services) =>
        services
            .AddControllers(options =>
            {
                options.ReturnHttpNotAcceptable = true;
                options.Filters.Add<BadRequestFilter>();
            })
            .AddXmlSerializerFormatters();
}
