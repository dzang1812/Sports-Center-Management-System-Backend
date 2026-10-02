using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API.Controllers;

[ApiController, Route("api/sports")]
public sealed class SportsController(ICatalogService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet, AllowAnonymous] public Task<List<SportDto>> List(CancellationToken ct) => service.ListSportsAsync(ct);
    [HttpPost, Authorize(Roles = "Manager")] public Task<SportDto> Create(UpsertSportRequest request, CancellationToken ct) => service.CreateSportAsync(ActorId, request, ct);
    [HttpPut("{id:int}"), Authorize(Roles = "Manager")] public Task<SportDto> Update(int id, UpsertSportRequest request, CancellationToken ct) => service.UpdateSportAsync(ActorId, id, request, ct);
}

[ApiController, Route("api/rooms")]
public sealed class RoomsController(ICatalogService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet, Authorize] public Task<List<RoomDto>> List(CancellationToken ct) => service.ListRoomsAsync(ct);
    [HttpPost, Authorize(Roles = "Manager")] public Task<RoomDto> Create(UpsertRoomRequest request, CancellationToken ct) => service.CreateRoomAsync(ActorId, request, ct);
    [HttpPut("{id:int}"), Authorize(Roles = "Manager")] public Task<RoomDto> Update(int id, UpsertRoomRequest request, CancellationToken ct) => service.UpdateRoomAsync(ActorId, id, request, ct);
}
