using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Repository.Entities;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API.Controllers;

[ApiController, Authorize(Roles = "Manager"), Route("api/classes")]
public sealed class ClassesController(IClassScheduleService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet] public Task<PagedResponse<ClassDto>> List(int page = 1, int pageSize = 20, CancellationToken ct = default) => service.ListClassesAsync(page, pageSize, ct);
    [HttpGet("featured"), AllowAnonymous] public Task<List<PublicClassDto>> Featured(int count = 4, CancellationToken ct = default) => service.ListFeaturedClassesAsync(count, ct);
    [HttpGet("public"), AllowAnonymous] public Task<List<PublicClassDto>> Public(int? sportId, Level? level, CancellationToken ct = default) => service.ListPublicClassesAsync(sportId, level, ct);
    [HttpPost] public Task<ClassDto> Create(UpsertClassRequest request, CancellationToken ct) => service.CreateClassAsync(ActorId, request, ct);
    [HttpPut("{id:int}")] public Task<ClassDto> Update(int id, UpsertClassRequest request, CancellationToken ct) => service.UpdateClassAsync(ActorId, id, request, ct);
    [HttpPost("{id:int}/sessions/generate")] public Task<GenerateSessionsResponse> Generate(int id, GenerateSessionsRequest request, CancellationToken ct) => service.GenerateAsync(ActorId, id, request, ct);
    [HttpPut("{id:int}/coach")] public Task<ClassDto> AssignCoach(int id, AssignCoachRequest request, CancellationToken ct) => service.AssignCoachAsync(ActorId, id, request, ct);
}

[ApiController, Route("api/sessions")]
public sealed class SessionsController(IClassScheduleService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet, AllowAnonymous]
    public Task<PagedResponse<SessionDto>> List(int? sportId, DateOnly? from, DateOnly? to, int? coachId, Level? level, int? classId,
        int page = 1, int pageSize = 20, CancellationToken ct = default) => service.ListSessionsAsync(sportId, from, to, coachId, level, classId, page, pageSize, ct);
    [HttpPut("{id:int}"), Authorize(Roles = "Manager")] public Task<SessionDto> Update(int id, UpdateSessionRequest request, CancellationToken ct) => service.UpdateSessionAsync(ActorId, id, request, ct);
    [HttpPatch("{id:int}/cancel"), Authorize(Roles = "Manager")] public async Task<IActionResult> Cancel(int id, CancelSessionRequest request, CancellationToken ct)
    { await service.CancelSessionAsync(ActorId, id, request, ct); return NoContent(); }
}
