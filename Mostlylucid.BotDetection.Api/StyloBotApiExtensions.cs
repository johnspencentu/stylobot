using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Endpoints;
using Mostlylucid.BotDetection.Api.Middleware;

namespace Mostlylucid.BotDetection.Api;

public static class StyloBotApiExtensions
{
    public static IServiceCollection AddStyloBotApi(
        this IServiceCollection services,
        Action<StyloBotApiOptions>? configure = null)
    {
        var options = new StyloBotApiOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, null);

        if (options.EnableOpenApi)
        {
            services.AddOpenApi();
        }

        return services;
    }

    public static IEndpointRouteBuilder MapStyloBotApi(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<StyloBotApiOptions>();

        endpoints.MapDetectEndpoints();
        endpoints.MapReadEndpoints();
        endpoints.MapMeEndpoints();

        if (options.EnableOpenApi)
        {
            endpoints.MapOpenApi("/api/v1/openapi.json");
        }

        return endpoints;
    }

    public static IApplicationBuilder UseStyloBotResponseHeaders(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ResponseHeaderInjectionMiddleware>();
    }
}
