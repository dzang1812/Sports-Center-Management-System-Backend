using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace SportsCenter.API;

public sealed class SchedulingOpenApiOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath ?? "";
        if (!path.StartsWith("api/sports") && !path.StartsWith("api/rooms") && !path.StartsWith("api/classes") &&
            !path.StartsWith("api/sessions") && !path.StartsWith("api/bookings") && !path.StartsWith("api/pt") &&
            !path.StartsWith("api/coach") && !path.StartsWith("api/me/schedule") && !path.StartsWith("api/me/attendance")) return;

        var attributes = context.MethodInfo.DeclaringType!.GetCustomAttributes(true).Concat(context.MethodInfo.GetCustomAttributes(true));
        var roles = attributes.OfType<AuthorizeAttribute>().SelectMany(x => (x.Roles ?? "Authenticated").Split(','))
            .Select(x => x.Trim()).Distinct().ToArray();
        var access = attributes.OfType<AllowAnonymousAttribute>().Any() ? "Public" : string.Join(", ", roles);
        operation.Description = $"Access: {access}. Calendar/time and the 12-hour cutoff use Asia/Ho_Chi_Minh; timestamps are stored/returned as UTC. " +
            (operation.Description ?? "");
        operation.Responses.TryAdd("400", new OpenApiResponse { Description = "VALIDATION_ERROR or invalid input" });
        if (access != "Public") operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing or invalid JWT" });
        if (access != "Public") operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Role or ownership denied" });
        operation.Responses.TryAdd("404", new OpenApiResponse { Description = "Resource not found" });
        operation.Responses.TryAdd("409", new OpenApiResponse { Description = "Business/concurrency conflict; body has errorCode and message" });
        if (context.ApiDescription.HttpMethod == "DELETE")
            operation.Description += " DELETE is a business cancellation and retains history; repeated cancellation is idempotent.";
    }
}
