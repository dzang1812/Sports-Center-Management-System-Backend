using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API.Controllers;

[ApiController, Route("api/sports")]
public sealed class SportsController(ICatalogService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    // Đọc danh mục bộ môn và khả năng tổ chức lớp hoặc tập riêng.
    [HttpGet, AllowAnonymous] public Task<List<SportDto>> List(CancellationToken ct) => service.ListSportsAsync(ct);
    // Thêm bộ môn để cấu hình lớp hoặc tập riêng.
    [HttpPost, Authorize(Roles = "Manager")] public Task<SportDto> Create(UpsertSportRequest request, CancellationToken ct) => service.CreateSportAsync(ActorId, request, ct);
    // Sửa bộ môn và bật/tắt khả năng lớp hoặc tập riêng.
    [HttpPut("{id:int}"), Authorize(Roles = "Manager")] public Task<SportDto> Update(int id, UpsertSportRequest request, CancellationToken ct) => service.UpdateSportAsync(ActorId, id, request, ct);
}

[ApiController, Route("api/rooms")]
public sealed class RoomsController(ICatalogService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    // Đọc danh sách phòng, sức chứa và trạng thái.
    [HttpGet, Authorize] public Task<List<RoomDto>> List(CancellationToken ct) => service.ListRoomsAsync(ct);
    // Thêm phòng phục vụ sắp lịch.
    [HttpPost, Authorize(Roles = "Manager")] public Task<RoomDto> Create(UpsertRoomRequest request, CancellationToken ct) => service.CreateRoomAsync(ActorId, request, ct);
    // Sửa thông tin phòng và sức chứa.
    [HttpPut("{id:int}"), Authorize(Roles = "Manager")] public Task<RoomDto> Update(int id, UpsertRoomRequest request, CancellationToken ct) => service.UpdateRoomAsync(ActorId, id, request, ct);
}
