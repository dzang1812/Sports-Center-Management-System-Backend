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
    // Tìm lớp theo bộ môn, trạng thái và tên.
    [HttpGet] public Task<PagedResponse<ClassDto>> List(int? sportId, ClassStatus? status, string? q, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        service.ListClassesAsync(sportId, status, q, page, pageSize, ct);
    // Lấy lớp nổi bật cho giao diện giới thiệu.
    [HttpGet("featured"), AllowAnonymous] public Task<List<PublicClassDto>> Featured(int count = 4, CancellationToken ct = default) => service.ListFeaturedClassesAsync(count, ct);
    // Xem danh sách lớp công khai theo môn/cấp độ.
    [HttpGet("public"), AllowAnonymous] public Task<List<PublicClassDto>> Public(int? sportId, Level? level, CancellationToken ct = default) => service.ListPublicClassesAsync(sportId, level, ct);
    // Tạo thông tin lớp theo đường dẫn xử lý cấu hình lớp.
    [HttpPost] public Task<ClassDto> Create(UpsertClassRequest request, CancellationToken ct) => service.CreateClassAsync(ActorId, request, ct);
    // Chỉnh sửa thông tin lớp hiện có.
    [HttpPut("{id:int}")] public Task<ClassDto> Update(int id, UpsertClassRequest request, CancellationToken ct) => service.UpdateClassAsync(ActorId, id, request, ct);
    // Sinh các buổi theo thứ và giờ trong khoảng lớp.
    [HttpPost("{id:int}/sessions/generate")] public Task<GenerateSessionsResponse> Generate(int id, GenerateSessionsRequest request, CancellationToken ct) => service.GenerateAsync(ActorId, id, request, ct);
    // Phân công huấn luyện viên cho lớp.
    [HttpPut("{id:int}/coach")] public Task<ClassDto> AssignCoach(int id, AssignCoachRequest request, CancellationToken ct) => service.AssignCoachAsync(ActorId, id, request, ct);
}

[ApiController, Route("api/sessions")]
public sealed class SessionsController(IClassScheduleService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    // Tìm buổi học theo ngày, lớp, môn hoặc huấn luyện viên.
    [HttpGet, AllowAnonymous]
    public Task<PagedResponse<SessionDto>> List(int? sportId, DateOnly? from, DateOnly? to, int? coachId, Level? level, int? classId,
        int page = 1, int pageSize = 20, CancellationToken ct = default) => service.ListSessionsAsync(sportId, from, to, coachId, level, classId, page, pageSize, ct);
    // Dời ngày, giờ hoặc phòng của một buổi lớp.
    [HttpPut("{id:int}"), Authorize(Roles = "Manager")] public Task<SessionDto> Update(int id, UpdateSessionRequest request, CancellationToken ct) => service.UpdateSessionAsync(ActorId, id, request, ct);
    // Hủy buổi lớp lẻ và lịch đặt liên quan.
    [HttpPatch("{id:int}/cancel"), Authorize(Roles = "Manager")] public async Task<IActionResult> Cancel(int id, CancelSessionRequest request, CancellationToken ct)
    { await service.CancelSessionAsync(ActorId, id, request, ct); return NoContent(); }
}
