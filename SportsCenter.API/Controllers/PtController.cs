using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsCenter.Repository.Entities;
using SportsCenter.Services.Scheduling;

namespace SportsCenter.API.Controllers;

[ApiController, Route("api/coach/availability"), Authorize(Roles = "Coach")]
public sealed class CoachAvailabilityController(IPtService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    // Xem giờ rảnh trong tuần của huấn luyện viên.
    [HttpGet("week")] public Task<WeekAvailabilityResponse> Week(DateOnly weekStart, CancellationToken ct) => service.GetWeekAvailabilityAsync(ActorId, weekStart, ct);
    // Lưu giờ rảnh trong tuần đã chọn.
    [HttpPut("week")] public Task<WeekAvailabilityResponse> ReplaceWeek(ReplaceWeekAvailabilityRequest request, CancellationToken ct) => service.ReplaceWeekAvailabilityAsync(ActorId, request, ct);
    // Xem giờ rảnh tuần hiện tại bằng cách đọc cũ.
    [HttpGet] public Task<List<AvailabilityItem>> Get(CancellationToken ct) => service.GetAvailabilityAsync(ActorId, ct);
    // Thông báo chuyển sang lưu giờ rảnh theo tuần.
    [HttpPut] public Task<List<AvailabilityItem>> Put(ReplaceAvailabilityRequest request, CancellationToken ct) => service.ReplaceAvailabilityAsync(ActorId, request, ct);
}

[ApiController, Route("api/pt")]
public sealed class PtController(IPtService service) : ControllerBase
{
    private int ActorId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    // Chọn huấn luyện viên theo bộ môn.
    [HttpGet("coaches"), AllowAnonymous] public Task<List<PtCoachDto>> Coaches(int? sportId, CancellationToken ct) => service.ListCoachesAsync(sportId, ct);
    // Tìm giờ tập riêng còn trống.
    [HttpGet("coaches/{coachId:int}/slots"), AllowAnonymous]
    public Task<List<PtSlotDto>> Slots(int coachId, int sportId, DateOnly from, DateOnly to, CancellationToken ct) => service.ListSlotsAsync(coachId, sportId, from, to, ct);
    // Đặt buổi tập riêng bằng ví hoặc lượt trong gói.
    [HttpPost("bookings"), Authorize(Roles = "Member,Receptionist")]
    public Task<PtBookingDto> Book(CreatePtBookingRequest request, CancellationToken ct) => service.BookAsync(ActorId, request, ct, Request.Headers["Idempotency-Key"].FirstOrDefault());
    // Thu tiền mặt hoặc tạo mã thanh toán buổi tập riêng.
    [HttpPost("counter-checkouts"), Authorize(Roles = "Receptionist")]
    public Task<PtCounterCheckoutDto> CounterCheckout(PtCounterCheckoutRequest request, CancellationToken ct) =>
        service.CounterCheckoutAsync(ActorId, request, Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "", ct);
    // Hoàn tất đặt lịch sau khi xác nhận chuyển khoản.
    [HttpPost("counter-checkouts/{paymentId:int}/complete"), Authorize(Roles = "Receptionist")]
    public Task<PtBookingDto> CompleteCounterCheckout(int paymentId, CancellationToken ct) =>
        service.CompleteCounterCheckoutAsync(ActorId, paymentId, ct);
    // Xem lịch tập riêng toàn trung tâm.
    [HttpGet("bookings"), Authorize(Roles = "Manager")]
    public Task<PagedResponse<PtBookingListItem>> Bookings(DateOnly? from, DateOnly? to, int? memberId,
        int? coachId, int? sportId, BookingStatus? status, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        service.ListBookingsAsync(ActorId, from, to, memberId, coachId, sportId, status, page, pageSize, ct);
    // Quản lý hủy lịch tập riêng.
    [HttpPatch("bookings/{id:int}/manager-cancel"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> ManagerCancel(int id, ManagerCancelPtBookingRequest request, CancellationToken ct)
    { await service.ManagerCancelAsync(ActorId, id, request, ct); return NoContent(); }
    // Hủy lịch tập riêng của hội viên.
    [HttpDelete("bookings/{id:int}"), Authorize(Roles = "Member,Receptionist")]
    public async Task<IActionResult> Cancel(int id, [FromBody] CancelPtBookingRequest? request, CancellationToken ct)
    { await service.CancelAsync(ActorId, id, request ?? new(null), ct); return NoContent(); }
    // Đổi giờ tập riêng, giữ nguồn thanh toán.
    [HttpPut("bookings/{id:int}/reschedule"), Authorize(Roles = "Member,Receptionist")]
    public Task<PtBookingDto> Reschedule(int id, ReschedulePtBookingRequest request, CancellationToken ct) => service.RescheduleAsync(ActorId, id, request, ct);
    // Huấn luyện viên hủy lịch mình phụ trách.
    [HttpPatch("bookings/{id:int}/coach-cancel"), Authorize(Roles = "Coach")]
    public async Task<IActionResult> CoachCancel(int id, CancelPtBookingRequest? request, CancellationToken ct)
    { await service.CoachCancelAsync(ActorId, id, request ?? new(null), ct); return NoContent(); }
}
