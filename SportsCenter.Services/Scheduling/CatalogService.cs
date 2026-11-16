using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Scheduling;

public record SportDto(int Id, string Name, string? Description, bool HasClasses, bool HasPt, SportStatus Status);
public record UpsertSportRequest(
    [param: Required, StringLength(100, MinimumLength = 2)] string Name,
    [param: StringLength(500)] string? Description,
    bool HasClasses, bool HasPt, SportStatus Status = SportStatus.Active);
public record RoomDto(int Id, string Name, int Capacity, RoomStatus Status, List<string> ImageUrls);
public record UpsertRoomRequest(
    [param: Required, StringLength(100, MinimumLength = 2)] string Name,
    [param: Range(1, 10000)] int Capacity,
    RoomStatus Status = RoomStatus.Active, [param: MaxLength(10)] List<string>? ImageUrls = null);

public interface ICatalogService
{
    Task<List<SportDto>> ListSportsAsync(CancellationToken ct);
    Task<SportDto> CreateSportAsync(int actorId, UpsertSportRequest request, CancellationToken ct);
    Task<SportDto> UpdateSportAsync(int actorId, int id, UpsertSportRequest request, CancellationToken ct);
    Task<List<RoomDto>> ListRoomsAsync(CancellationToken ct);
    Task<RoomDto> CreateRoomAsync(int actorId, UpsertRoomRequest request, CancellationToken ct);
    Task<RoomDto> UpdateRoomAsync(int actorId, int id, UpsertRoomRequest request, CancellationToken ct);
}

public sealed class CatalogService(ICatalogRepository catalog, IScheduleUserRepository users, IAuditLogRepository audit,
    ISchedulingTransaction transaction) : ICatalogService
{
    // Đọc danh mục môn và khả năng tổ chức lớp hoặc tập riêng.
    // Nghiệp vụ: Trả thông tin cho màn hình chọn tài nguyên; quyền tổ chức lớp và tập riêng quyết định môn dùng được ở luồng nào.
    public async Task<List<SportDto>> ListSportsAsync(CancellationToken ct) => (await catalog.ListSportsAsync(ct)).Select(ToDto).ToList();

    // Tạo bộ môn.
    // Nghiệp vụ: Quản lý có tài khoản hoạt động, tên không trùng, hỗ trợ lớp hoặc tập riêng; lưu nhật ký cùng giao dịch dữ liệu.
    public async Task<SportDto> CreateSportAsync(int actorId, UpsertSportRequest request, CancellationToken ct)
    {
        ValidateCapability(request.HasClasses, request.HasPt);
        return await transaction.ExecuteAsync(async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Manager, token);
            var name = request.Name.Trim();
            if (await catalog.SportNameExistsAsync(name, null, token))
                throw SchedulingException.Conflict("SPORT_NAME_EXISTS", "Tên bộ môn đã tồn tại.");
            var entity = new Sport { Name = name, Description = request.Description?.Trim(), HasClasses = request.HasClasses, HasPt = request.HasPt, Status = request.Status };
            catalog.AddSport(entity);
            await transaction.SaveChangesAsync(token);
            audit.AddAudit(actorId, "Sport.Create", nameof(Sport), entity.Id, request);
            return ToDto(entity);
        }, ct);
    }

    // Sửa môn và khả năng tổ chức dịch vụ.
    // Nghiệp vụ: Khóa bộ môn; không bỏ quyền tổ chức lớp hay tập riêng đang có dữ liệu cần bảo vệ, kiểm tra tên không trùng.
    public async Task<SportDto> UpdateSportAsync(int actorId, int id, UpsertSportRequest request, CancellationToken ct)
    {
        ValidateCapability(request.HasClasses, request.HasPt);
        return await transaction.ExecuteAsync(async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Manager, token);
            await transaction.LockAsync([new(ScheduleResourceType.Sport, id)], token);
            var entity = await catalog.GetSportAsync(id, token) ?? throw SchedulingException.NotFound("SPORT_NOT_FOUND", "Không tìm thấy bộ môn.");
            var name = request.Name.Trim();
            if (await catalog.SportNameExistsAsync(name, id, token))
                throw SchedulingException.Conflict("SPORT_NAME_EXISTS", "Tên bộ môn đã tồn tại.");
            if (!request.HasClasses && await catalog.SportHasOpenClassesAsync(id, token))
                throw SchedulingException.Conflict("SPORT_HAS_CLASSES", "Không thể bỏ quyền lớp học khi bộ môn còn lớp được tham chiếu.");
            if (!request.HasPt && await catalog.SportHasConfirmedPtBookingsAsync(id, token))
                throw SchedulingException.Conflict("SPORT_HAS_PT_BOOKINGS", "Không thể bỏ quyền PT khi bộ môn còn lịch PT hiệu lực.");
            entity.Name = name; entity.Description = request.Description?.Trim(); entity.HasClasses = request.HasClasses;
            entity.HasPt = request.HasPt; entity.Status = request.Status;
            audit.AddAudit(actorId, "Sport.Update", nameof(Sport), id, request);
            await transaction.SaveChangesAsync(token);
            return ToDto(entity);
        }, ct);
    }

    // Đọc danh sách phòng.
    // Nghiệp vụ: Trả sức chứa, trạng thái và ảnh để giao diện tư vấn hoặc chọn phòng; lúc đặt lịch còn kiểm tra lại.
    public async Task<List<RoomDto>> ListRoomsAsync(CancellationToken ct) => (await catalog.ListRoomsAsync(ct)).Select(ToDto).ToList();

    // Tạo phòng phục vụ sắp lịch.
    // Nghiệp vụ: Quản lý đang hoạt động, tên phòng không trùng; lưu sức chứa, trạng thái, ảnh và nhật ký.
    public async Task<RoomDto> CreateRoomAsync(int actorId, UpsertRoomRequest request, CancellationToken ct)
    {
        return await transaction.ExecuteAsync(async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Manager, token);
            var name = request.Name.Trim();
            if (await catalog.RoomNameExistsAsync(name, null, token))
                throw SchedulingException.Conflict("ROOM_NAME_EXISTS", "Tên phòng đã tồn tại.");
            var entity = new Room { Name = name, Capacity = request.Capacity, Status = request.Status, IsActive = request.Status == RoomStatus.Active, ImageUrls = request.ImageUrls ?? [] };
            catalog.AddRoom(entity);
            await transaction.SaveChangesAsync(token);
            audit.AddAudit(actorId, "Room.Create", nameof(Room), entity.Id, request);
            return ToDto(entity);
        }, ct);
    }

    // Sửa phòng và sức chứa.
    // Nghiệp vụ: Khóa phòng; chặn tên trùng hoặc sức chứa mới nhỏ hơn lớp đang dùng phòng.
    public async Task<RoomDto> UpdateRoomAsync(int actorId, int id, UpsertRoomRequest request, CancellationToken ct)
    {
        return await transaction.ExecuteAsync(async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Manager, token);
            await transaction.LockAsync([new(ScheduleResourceType.Room, id)], token);
            var entity = await catalog.GetRoomAsync(id, token) ?? throw SchedulingException.NotFound("ROOM_NOT_FOUND", "Không tìm thấy phòng.");
            var name = request.Name.Trim();
            if (await catalog.RoomNameExistsAsync(name, id, token))
                throw SchedulingException.Conflict("ROOM_NAME_EXISTS", "Tên phòng đã tồn tại.");
            if (await catalog.RoomCapacityInUseAsync(id, request.Capacity, token))
                throw SchedulingException.Conflict("ROOM_CAPACITY_IN_USE", "Sức chứa mới nhỏ hơn sức chứa của lớp đang dùng phòng.");
            entity.Name = name; entity.Capacity = request.Capacity; entity.Status = request.Status;
            entity.IsActive = request.Status == RoomStatus.Active; entity.ImageUrls = request.ImageUrls ?? [];
            audit.AddAudit(actorId, "Room.Update", nameof(Room), id, request);
            await transaction.SaveChangesAsync(token);
            return ToDto(entity);
        }, ct);
    }

    private static void ValidateCapability(bool hasClasses, bool hasPt)
    {
        if (!hasClasses && !hasPt) throw new SchedulingException("SPORT_CAPABILITY_REQUIRED", "Bộ môn phải hỗ trợ lớp học hoặc PT.");
    }

    private static SportDto ToDto(Sport x) => new(x.Id, x.Name, x.Description, x.HasClasses, x.HasPt, x.Status);
    private static RoomDto ToDto(Room x) => new(x.Id, x.Name, x.Capacity, x.Status, x.ImageUrls);
}
