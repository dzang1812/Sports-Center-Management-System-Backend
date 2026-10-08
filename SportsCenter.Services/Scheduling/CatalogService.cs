using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Services.Scheduling;

public record SportDto(int Id, string Name, string? Description, bool HasClasses, bool HasPt, SportStatus Status);
public record UpsertSportRequest(
    [param: Required, StringLength(100, MinimumLength = 2)] string Name,
    [param: StringLength(500)] string? Description,
    bool HasClasses, bool HasPt, SportStatus Status = SportStatus.Active);
public record RoomDto(int Id, string Name, int Capacity, RoomStatus Status);
public record UpsertRoomRequest(
    [param: Required, StringLength(100, MinimumLength = 2)] string Name,
    [param: Range(1, 10000)] int Capacity,
    RoomStatus Status = RoomStatus.Active);

public interface ICatalogService
{
    Task<List<SportDto>> ListSportsAsync(CancellationToken ct);
    Task<SportDto> CreateSportAsync(int actorId, UpsertSportRequest request, CancellationToken ct);
    Task<SportDto> UpdateSportAsync(int actorId, int id, UpsertSportRequest request, CancellationToken ct);
    Task<List<RoomDto>> ListRoomsAsync(CancellationToken ct);
    Task<RoomDto> CreateRoomAsync(int actorId, UpsertRoomRequest request, CancellationToken ct);
    Task<RoomDto> UpdateRoomAsync(int actorId, int id, UpsertRoomRequest request, CancellationToken ct);
}

public sealed class CatalogService(AppDbContext db, ISchedulingTransaction transaction) : ICatalogService
{
    public Task<List<SportDto>> ListSportsAsync(CancellationToken ct) => db.Sports.AsNoTracking().OrderBy(x => x.Name)
        .Select(x => new SportDto(x.Id, x.Name, x.Description, x.HasClasses, x.HasPt, x.Status)).ToListAsync(ct);

    public async Task<SportDto> CreateSportAsync(int actorId, UpsertSportRequest request, CancellationToken ct)
    {
        ValidateCapability(request.HasClasses, request.HasPt);
        return await transaction.ExecuteAsync(async token =>
        {
            await SchedulingRules.EnsureActiveActorAsync(db, actorId, UserRole.Manager, token);
            var name = request.Name.Trim();
            if (await db.Sports.AnyAsync(x => x.Name.ToLower() == name.ToLower(), token))
                throw SchedulingException.Conflict("SPORT_NAME_EXISTS", "Tên bộ môn đã tồn tại.");
            var entity = new Sport { Name = name, Description = request.Description?.Trim(), HasClasses = request.HasClasses, HasPt = request.HasPt, Status = request.Status };
            db.Sports.Add(entity);
            await db.SaveChangesAsync(token);
            SchedulingRules.AddAudit(db, actorId, "Sport.Create", nameof(Sport), entity.Id, request);
            return ToDto(entity);
        }, ct);
    }

    public async Task<SportDto> UpdateSportAsync(int actorId, int id, UpsertSportRequest request, CancellationToken ct)
    {
        ValidateCapability(request.HasClasses, request.HasPt);
        return await transaction.ExecuteAsync(async token =>
        {
            await SchedulingRules.EnsureActiveActorAsync(db, actorId, UserRole.Manager, token);
            await transaction.LockAsync([new(ScheduleResourceType.Sport, id)], token);
            var entity = await db.Sports.FindAsync([id], token) ?? throw SchedulingException.NotFound("SPORT_NOT_FOUND", "Không tìm thấy bộ môn.");
            var name = request.Name.Trim();
            if (await db.Sports.AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower(), token))
                throw SchedulingException.Conflict("SPORT_NAME_EXISTS", "Tên bộ môn đã tồn tại.");
            if (!request.HasClasses && await db.Classes.AnyAsync(x => x.SportId == id && x.Status != ClassStatus.Cancelled, token))
                throw SchedulingException.Conflict("SPORT_HAS_CLASSES", "Không thể bỏ quyền lớp học khi bộ môn còn lớp được tham chiếu.");
            if (!request.HasPt && await db.PtBookings.AnyAsync(x => x.SportId == id && x.Status == BookingStatus.Confirmed, token))
                throw SchedulingException.Conflict("SPORT_HAS_PT_BOOKINGS", "Không thể bỏ quyền PT khi bộ môn còn lịch PT hiệu lực.");
            entity.Name = name; entity.Description = request.Description?.Trim(); entity.HasClasses = request.HasClasses;
            entity.HasPt = request.HasPt; entity.Status = request.Status;
            SchedulingRules.AddAudit(db, actorId, "Sport.Update", nameof(Sport), id, request);
            await db.SaveChangesAsync(token);
            return ToDto(entity);
        }, ct);
    }

    public Task<List<RoomDto>> ListRoomsAsync(CancellationToken ct) => db.Rooms.AsNoTracking().OrderBy(x => x.Name)
        .Select(x => new RoomDto(x.Id, x.Name, x.Capacity, x.Status)).ToListAsync(ct);

    public async Task<RoomDto> CreateRoomAsync(int actorId, UpsertRoomRequest request, CancellationToken ct)
    {
        return await transaction.ExecuteAsync(async token =>
        {
            await SchedulingRules.EnsureActiveActorAsync(db, actorId, UserRole.Manager, token);
            var name = request.Name.Trim();
            if (await db.Rooms.AnyAsync(x => x.Name.ToLower() == name.ToLower(), token))
                throw SchedulingException.Conflict("ROOM_NAME_EXISTS", "Tên phòng đã tồn tại.");
            var entity = new Room { Name = name, Capacity = request.Capacity, Status = request.Status, IsActive = request.Status == RoomStatus.Active };
            db.Rooms.Add(entity);
            await db.SaveChangesAsync(token);
            SchedulingRules.AddAudit(db, actorId, "Room.Create", nameof(Room), entity.Id, request);
            return ToDto(entity);
        }, ct);
    }

    public async Task<RoomDto> UpdateRoomAsync(int actorId, int id, UpsertRoomRequest request, CancellationToken ct)
    {
        return await transaction.ExecuteAsync(async token =>
        {
            await SchedulingRules.EnsureActiveActorAsync(db, actorId, UserRole.Manager, token);
            await transaction.LockAsync([new(ScheduleResourceType.Room, id)], token);
            var entity = await db.Rooms.FindAsync([id], token) ?? throw SchedulingException.NotFound("ROOM_NOT_FOUND", "Không tìm thấy phòng.");
            var name = request.Name.Trim();
            if (await db.Rooms.AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower(), token))
                throw SchedulingException.Conflict("ROOM_NAME_EXISTS", "Tên phòng đã tồn tại.");
            if (await db.Classes.AnyAsync(x => x.RoomId == id && x.Capacity > request.Capacity && x.Status != ClassStatus.Cancelled, token) ||
                await db.ClassSessions.AnyAsync(x => x.RoomId == id && x.Capacity > request.Capacity && x.Status != SessionStatus.Cancelled, token))
                throw SchedulingException.Conflict("ROOM_CAPACITY_IN_USE", "Sức chứa mới nhỏ hơn sức chứa của lớp đang dùng phòng.");
            entity.Name = name; entity.Capacity = request.Capacity; entity.Status = request.Status;
            entity.IsActive = request.Status == RoomStatus.Active;
            SchedulingRules.AddAudit(db, actorId, "Room.Update", nameof(Room), id, request);
            await db.SaveChangesAsync(token);
            return ToDto(entity);
        }, ct);
    }

    private static void ValidateCapability(bool hasClasses, bool hasPt)
    {
        if (!hasClasses && !hasPt) throw new SchedulingException("SPORT_CAPABILITY_REQUIRED", "Bộ môn phải hỗ trợ lớp học hoặc PT.");
    }

    private static SportDto ToDto(Sport x) => new(x.Id, x.Name, x.Description, x.HasClasses, x.HasPt, x.Status);
    private static RoomDto ToDto(Room x) => new(x.Id, x.Name, x.Capacity, x.Status);
}
