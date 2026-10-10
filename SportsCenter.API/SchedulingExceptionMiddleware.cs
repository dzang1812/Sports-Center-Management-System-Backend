using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API;

public sealed class SchedulingExceptionMiddleware(RequestDelegate next, ILogger<SchedulingExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (SchedulingException ex)
        {
            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(new { errorCode = ex.ErrorCode, message = ex.Message, details = ex.Details });
        }
        catch (ScheduleConflictException ex)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { errorCode = "SCHEDULE_CONFLICT", message = ex.Message, details = (object?)null });
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Database rejected a scheduling operation");
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                errorCode = "DATA_CONFLICT",
                message = "Dữ liệu vừa thay đổi hoặc vi phạm ràng buộc. Vui lòng tải lại và thử lại.",
            });
        }
    }
}
