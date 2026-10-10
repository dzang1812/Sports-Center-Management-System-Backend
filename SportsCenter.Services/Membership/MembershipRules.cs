using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.Services.Membership;

// SchedulingException is this project's shared business-exception type (statusCode + errorCode + details,
// wired to SchedulingExceptionMiddleware) despite its name — reused here instead of adding a second one.
internal static class MembershipRules
{
    public static async Task<User> EnsureActiveRoleAsync(IUserRepository users, int actorId, UserRole required)
    {
        var actor = await users.GetByIdAsync(actorId);
        if (actor is null || actor.Role != required)
            throw SchedulingException.Forbidden("ACTOR_NOT_ALLOWED", "Tài khoản không hợp lệ hoặc không có quyền.");
        return actor;
    }
}
