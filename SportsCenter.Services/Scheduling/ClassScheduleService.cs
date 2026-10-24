using System.ComponentModel.DataAnnotations;
using SportsCenter.Repository.Entities;
using SportsCenter.Repository.Repositories;

namespace SportsCenter.Services.Scheduling;

public record PagedResponse<T>(List<T> Items, int Total, int Page, int PageSize);
public record ClassDto(int Id, string Name, int SportId, string Sport, int RoomId, string Room, int? CoachId,
    string? Coach, int Capacity, Level Level, DateOnly StartDate, DateOnly EndDate, ClassStatus Status,
    string? Description, List<string> ImageUrls);
public record UpsertClassRequest(
    [param: Required, StringLength(150, MinimumLength = 2)] string Name,
    int SportId, int RoomId, int? CoachId,
    [param: Range(1, 10000)] int Capacity, Level Level, DateOnly StartDate, DateOnly EndDate,
    [param: StringLength(1000)] string? Description, ClassStatus Status = ClassStatus.Draft,
    [param: MaxLength(10)] List<string>? ImageUrls = null);
public record PublicClassDto(int Id, string Name, string Sport, Level Level, string? Description,
    DateOnly StartDate, DateOnly EndDate, List<string> ImageUrls);
public record AssignCoachRequest(int CoachId);
public record GenerateSessionsRequest(DayOfWeek[] DaysOfWeek, TimeOnly StartTime, TimeOnly EndTime);
public record GenerateSessionsResponse(int Created, int Existing, List<int> SessionIds);
public record SessionConflict(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, string Resource, int ResourceId);
public record SessionDto(int Id, int ClassId, string ClassName, int SportId, string Sport, Level Level,
    DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, int RoomId, string Room, int? CoachId, string? Coach,
    int Capacity, int RemainingSeats, SessionStatus Status);
public record UpdateSessionRequest(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, int RoomId,
    [param: StringLength(500)] string? Note);
public record CancelSessionRequest([param: Required, StringLength(500, MinimumLength = 2)] string Reason);

public interface IClassScheduleService
{
    Task<PagedResponse<ClassDto>> ListClassesAsync(int page, int pageSize, CancellationToken ct);
    Task<List<PublicClassDto>> ListFeaturedClassesAsync(int count, CancellationToken ct);
    Task<List<PublicClassDto>> ListPublicClassesAsync(int? sportId, Level? level, CancellationToken ct);
    Task<ClassDto> CreateClassAsync(int actorId, UpsertClassRequest request, CancellationToken ct);
    Task<ClassDto> UpdateClassAsync(int actorId, int id, UpsertClassRequest request, CancellationToken ct);
    Task<GenerateSessionsResponse> GenerateAsync(int actorId, int classId, GenerateSessionsRequest request, CancellationToken ct);
    Task<ClassDto> AssignCoachAsync(int actorId, int classId, AssignCoachRequest request, CancellationToken ct);
    Task<PagedResponse<SessionDto>> ListSessionsAsync(int? sportId, DateOnly? from, DateOnly? to, int? coachId, Level? level, int? classId, int page, int pageSize, CancellationToken ct);
    Task<SessionDto> UpdateSessionAsync(int actorId, int sessionId, UpdateSessionRequest request, CancellationToken ct);
    Task CancelSessionAsync(int actorId, int sessionId, CancelSessionRequest request, CancellationToken ct);
}

public sealed class ClassScheduleService(IClassRepository classes, ICatalogRepository catalog, IScheduleConflictRepository scheduleConflicts,
    IScheduleUserRepository users, ISchedulingSubscriptionRepository subscriptions, IAuditLogRepository audit, INotificationRepository notifications,
    ISchedulingTransaction transaction, IAppClock clock) : IClassScheduleService
{
    public async Task<PagedResponse<ClassDto>> ListClassesAsync(int page, int pageSize, CancellationToken ct)
    {
        (page, pageSize) = Page(page, pageSize);
        var (rows, total) = await classes.ListClassesAsync(page, pageSize, ct);
        return new(rows.Select(ToClassDto).ToList(), total, page, pageSize);
    }

    public async Task<List<PublicClassDto>> ListFeaturedClassesAsync(int count, CancellationToken ct)
    {
        count = Math.Clamp(count, 1, 12);
        var today = DateOnly.FromDateTime(clock.LocalNow);
        var rows = await classes.ListFeaturedClassesAsync(today, count, ct);
        return rows.Select(ToPublicClassDto).ToList();
    }

    public async Task<List<PublicClassDto>> ListPublicClassesAsync(int? sportId, Level? level, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.LocalNow);
        var rows = await classes.ListPublicOpenClassesAsync(sportId, level, today, ct);
        return rows.Select(ToPublicClassDto).ToList();
    }

    private static PublicClassDto ToPublicClassDto(Class x) =>
        new(x.Id, x.Name, x.Sport.Name, x.Level, x.Description, x.StartDate, x.EndDate, x.ImageUrls);

    public async Task<ClassDto> CreateClassAsync(int actorId, UpsertClassRequest request, CancellationToken ct)
    {
        ValidateClassDates(request);
        return await transaction.ExecuteAsync(async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Manager, token);
            var locks = new List<ScheduleResourceLock> { new(ScheduleResourceType.Sport, request.SportId), new(ScheduleResourceType.Room, request.RoomId) };
            if (request.CoachId is { } requestedCoach) locks.Add(new(ScheduleResourceType.Coach, requestedCoach));
            await transaction.LockAsync(locks, token);
            var (sport, room) = await ValidateSportRoomAsync(request.SportId, request.RoomId, request.Capacity, token);
            if (request.CoachId is { } coachId) await ValidateCoachAsync(coachId, sport.Id, token);
            var entity = new Class
            {
                Name = request.Name.Trim(), Description = request.Description?.Trim(), SportId = sport.Id, RoomId = room.Id,
                CoachId = request.CoachId, Capacity = request.Capacity, Level = request.Level, StartDate = request.StartDate,
                EndDate = request.EndDate, Status = request.Status, ImageUrls = request.ImageUrls ?? [],
            };
            classes.AddClass(entity);
            await transaction.SaveChangesAsync(token);
            audit.AddAudit(actorId, "Class.Create", nameof(Class), entity.Id, request);
            await transaction.SaveChangesAsync(token);
            return await GetClassAsync(entity.Id, token);
        }, ct);
    }

    public async Task<ClassDto> UpdateClassAsync(int actorId, int id, UpsertClassRequest request, CancellationToken ct)
    {
        ValidateClassDates(request);
        return await transaction.ExecuteAsync(async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Manager, token);
            await transaction.LockAsync([new(ScheduleResourceType.Sport, request.SportId), new(ScheduleResourceType.Room, request.RoomId), new(ScheduleResourceType.Class, id)], token);
            var entity = await classes.GetClassAsync(id, token)
                ?? throw SchedulingException.NotFound("CLASS_NOT_FOUND", "Không tìm thấy lớp.");
            var (sport, room) = await ValidateSportRoomAsync(request.SportId, request.RoomId, request.Capacity, token);
            var nameChanged = entity.Name != request.Name.Trim();
            var levelChanged = entity.Level != request.Level;
            var capacityChanged = entity.Capacity != request.Capacity;
            var roomChanged = entity.RoomId != request.RoomId;
            var localNow = clock.LocalNow;
            var today = DateOnly.FromDateTime(localNow);
            var timeNow = TimeOnly.FromDateTime(localNow);
            var bookingMax = await classes.GetMaxFutureBookingsPerSessionAsync(id, today, timeNow, token);
            if (request.Capacity < bookingMax)
                throw SchedulingException.Conflict("CAPACITY_BELOW_BOOKINGS", "Sức chứa mới nhỏ hơn số booking hiệu lực.");
            var hasSessions = await classes.ClassHasSessionsAsync(id, token);
            if (hasSessions && entity.SportId != request.SportId)
                throw SchedulingException.Conflict("CLASS_SPORT_IN_USE", "Không thể đổi bộ môn sau khi lớp đã có buổi học.");
            if (await classes.HasFutureSessionsOutsideRangeAsync(id, today, timeNow, request.StartDate, request.EndDate, token))
                throw SchedulingException.Conflict("CLASS_DATE_EXCLUDES_SESSION", "Khoảng ngày mới loại bỏ buổi học đã tồn tại.");
            var futureSessions = await classes.GetFutureScheduledSessionsAsync(id, today, timeNow, true, token);
            if (capacityChanged && futureSessions.Any(x => roomChanged ? room.Capacity < request.Capacity : x.Room.Capacity < request.Capacity))
                throw SchedulingException.Conflict("CAPACITY_EXCEEDS_SESSION_ROOM", "Sức chứa mới vượt quá phòng của một buổi tương lai.");
            if (request.CoachId is { } coachId && coachId != entity.CoachId)
                throw new SchedulingException("USE_ASSIGN_COACH", "Dùng endpoint /api/classes/{id}/coach để đổi huấn luyện viên.");
            if (roomChanged && entity.CoachId is { } assignedCoachId)
            {
                var conflicts = new List<SessionConflict>();
                foreach (var session in futureSessions)
                    conflicts.AddRange(await FindResourceConflictsAsync([session.Date], session.StartTime, session.EndTime,
                        request.RoomId, assignedCoachId, session.Id, token));
                if (conflicts.Count != 0)
                    throw SchedulingException.Conflict("CLASS_UPDATE_CONFLICT", "Phòng mới gây xung đột cho lịch tương lai.", conflicts);
            }
            foreach (var session in futureSessions)
            {
                if (nameChanged) session.ClassName = request.Name.Trim();
                if (levelChanged) session.Level = request.Level;
                if (capacityChanged) session.Capacity = request.Capacity;
                if (roomChanged) session.RoomId = request.RoomId;
            }
            entity.Name = request.Name.Trim(); entity.Description = request.Description?.Trim(); entity.SportId = sport.Id;
            entity.RoomId = room.Id; entity.Capacity = request.Capacity; entity.Level = request.Level;
            entity.StartDate = request.StartDate; entity.EndDate = request.EndDate; entity.Status = request.Status;
            entity.ImageUrls = request.ImageUrls ?? [];
            audit.AddAudit(actorId, "Class.Update", nameof(Class), id, request);
            await transaction.SaveChangesAsync(token);
            return await GetClassAsync(id, token);
        }, ct);
    }

    public async Task<GenerateSessionsResponse> GenerateAsync(int actorId, int classId, GenerateSessionsRequest request, CancellationToken ct)
    {
        if (request.DaysOfWeek.Length == 0 || request.DaysOfWeek.Distinct().Count() != request.DaysOfWeek.Length ||
            request.DaysOfWeek.Any(x => !Enum.IsDefined(x)))
            throw new SchedulingException("INVALID_WEEKDAYS", "Cần ít nhất một thứ và không được trùng.");
        if (request.StartTime >= request.EndTime) throw new SchedulingException("INVALID_TIME_RANGE", "Giờ kết thúc phải sau giờ bắt đầu; không hỗ trợ lịch qua đêm.");
        return await transaction.ExecuteAsync(async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Manager, token);
            var snapshot = await classes.GetClassResourcesAsync(classId, token)
                ?? throw SchedulingException.NotFound("CLASS_NOT_FOUND", "Không tìm thấy lớp.");
            if (snapshot.CoachId is not { } snapshotCoachId) throw SchedulingException.Conflict("COACH_REQUIRED", "Cần gán huấn luyện viên trước khi sinh lịch.");
            await transaction.LockAsync([new(ScheduleResourceType.Sport, snapshot.SportId), new(ScheduleResourceType.Room, snapshot.RoomId), new(ScheduleResourceType.Coach, snapshotCoachId), new(ScheduleResourceType.Class, classId)], token);
            var item = await classes.GetClassWithRoomAndCoachAsync(classId, token);
            if (item.EndDate.DayNumber - item.StartDate.DayNumber > 366)
                throw new SchedulingException("DATE_RANGE_TOO_LARGE", "Một lần sinh lịch không được vượt quá 366 ngày.");
            if (item.Status == ClassStatus.Cancelled) throw SchedulingException.Conflict("CLASS_CANCELLED", "Lớp đã bị hủy.");
            if (item.Room.Status != RoomStatus.Active) throw SchedulingException.Conflict("ROOM_UNAVAILABLE", "Phòng không hoạt động.");
            if (item.CoachId is not { } coachId) throw SchedulingException.Conflict("COACH_REQUIRED", "Cần gán huấn luyện viên trước khi sinh lịch.");
            await ValidateCoachAsync(coachId, item.SportId, token);
            if (item.RoomId != snapshot.RoomId || coachId != snapshotCoachId)
                throw SchedulingException.Conflict("SCHEDULE_CHANGED", "Lớp vừa được thay đổi. Vui lòng thử lại.");

            var dates = Enumerable.Range(0, item.EndDate.DayNumber - item.StartDate.DayNumber + 1)
                .Select(item.StartDate.AddDays).Where(x => request.DaysOfWeek.Contains(x.DayOfWeek) && clock.ToUtc(x, request.StartTime) > clock.UtcNow).ToList();
            var existing = await classes.GetExistingSessionDatesAsync(classId, dates, request.StartTime, token);
            var candidates = dates.Except(existing).ToList();
            var conflicts = await FindResourceConflictsAsync(candidates, request.StartTime, request.EndTime, item.RoomId, coachId, null, token);
            if (conflicts.Count != 0) throw SchedulingException.Conflict("SESSION_BATCH_CONFLICT", "Có buổi xung đột; toàn bộ lô chưa được ghi.", conflicts);
            var sessions = candidates.Select(date => new ClassSession
            {
                ClassId = classId, CoachId = coachId, RoomId = item.RoomId, Date = date, StartTime = request.StartTime, EndTime = request.EndTime,
                ClassName = item.Name, Level = item.Level, Capacity = item.Capacity,
                CreatedAt = clock.UtcNow,
            }).ToList();
            classes.AddSessions(sessions);
            audit.AddAudit(actorId, "ClassSession.Generate", nameof(Class), classId, new { request, Created = sessions.Count, Existing = existing.Count });
            await transaction.SaveChangesAsync(token);
            return new GenerateSessionsResponse(sessions.Count, existing.Count, sessions.Select(x => x.Id).ToList());
        }, ct);
    }

    public async Task<ClassDto> AssignCoachAsync(int actorId, int classId, AssignCoachRequest request, CancellationToken ct) =>
        await transaction.ExecuteAsync(async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Manager, token);
            var sportId = (await classes.GetClassResourcesAsync(classId, token))?.SportId
                ?? throw SchedulingException.NotFound("CLASS_NOT_FOUND", "Không tìm thấy lớp.");
            await transaction.LockAsync([new(ScheduleResourceType.Sport, sportId), new(ScheduleResourceType.Class, classId), new(ScheduleResourceType.Coach, request.CoachId)], token);
            var item = await classes.GetClassAsync(classId, token)
                ?? throw SchedulingException.NotFound("CLASS_NOT_FOUND", "Không tìm thấy lớp.");
            await ValidateCoachAsync(request.CoachId, item.SportId, token);
            if (item.CoachId == request.CoachId) return await GetClassAsync(classId, token);
            var localNow = clock.LocalNow;
            var today = DateOnly.FromDateTime(localNow);
            var timeNow = TimeOnly.FromDateTime(localNow);
            var sessions = await classes.GetFutureScheduledSessionsAsync(classId, today, timeNow, false, token);
            var conflicts = await FindCoachConflictsAsync(request.CoachId, sessions, token);
            if (conflicts.Count != 0) throw SchedulingException.Conflict("COACH_SCHEDULE_CONFLICT", "Huấn luyện viên bị trùng lịch.", conflicts);
            var previousCoachId = item.CoachId;
            item.CoachId = request.CoachId;
            item.NotificationRevision++;
            foreach (var session in sessions) session.CoachId = request.CoachId;
            audit.AddAudit(actorId, "Class.AssignCoach", nameof(Class), classId, request);
            notifications.AddNotification(request.CoachId, actorId, NotificationType.ScheduleChange, "Được phân công lớp",
                $"Bạn được phân công lớp {item.Name}.", $"class:{classId}:coach-revision:{item.NotificationRevision}:assigned:{request.CoachId}");
            if (previousCoachId is { } oldCoachId && oldCoachId != request.CoachId)
                notifications.AddNotification(oldCoachId, actorId, NotificationType.ScheduleChange, "Thay đổi phân công lớp",
                    $"Bạn không còn được phân công lớp {item.Name}.", $"class:{classId}:coach-revision:{item.NotificationRevision}:removed:{oldCoachId}");
            await transaction.SaveChangesAsync(token);
            return await GetClassAsync(classId, token);
        }, ct);

    public async Task<PagedResponse<SessionDto>> ListSessionsAsync(int? sportId, DateOnly? from, DateOnly? to, int? coachId, Level? level, int? classId, int page, int pageSize, CancellationToken ct)
    {
        (page, pageSize) = Page(page, pageSize);
        from ??= DateOnly.FromDateTime(clock.LocalNow);
        to ??= from.Value.AddDays(30);
        if (to < from || to.Value.DayNumber - from.Value.DayNumber > 180) throw new SchedulingException("INVALID_DATE_RANGE", "Khoảng lọc tối đa 180 ngày.");
        var (rows, total) = await classes.ListOpenSessionsAsync(sportId, from.Value, to.Value, coachId, level, classId, page, pageSize, ct);
        var items = rows.Select(x => ToSessionDto(x.Session, x.Class, x.Sport, x.Room, x.Coach, x.Booked)).ToList();
        return new(items, total, page, pageSize);
    }

    public async Task<SessionDto> UpdateSessionAsync(int actorId, int sessionId, UpdateSessionRequest request, CancellationToken ct)
    {
        if (request.StartTime >= request.EndTime) throw new SchedulingException("INVALID_TIME_RANGE", "Giờ kết thúc phải sau giờ bắt đầu; không hỗ trợ lịch qua đêm.");
        return await transaction.ExecuteAsync(async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Manager, token);
            var original = await classes.GetSessionResourcesAsync(sessionId, token)
                ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
            await transaction.LockAsync([new(ScheduleResourceType.Sport, original.SportId)], token);
            original = await classes.GetSessionResourcesAsync(sessionId, token)
                ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Session was not found.");
            var lockList = new List<ScheduleResourceLock> { new(ScheduleResourceType.Session, sessionId), new(ScheduleResourceType.Room, original.RoomId), new(ScheduleResourceType.Room, request.RoomId) };
            if (original.CoachId is { } c) lockList.Add(new(ScheduleResourceType.Coach, c));
            var memberIds = await classes.GetConfirmedMemberIdsAsync(sessionId, token);
            lockList.AddRange(memberIds.Select(x => new ScheduleResourceLock(ScheduleResourceType.Member, x)));
            await transaction.LockAsync(lockList, token);
            var session = await classes.GetSessionWithClassCoachAndRoomAsync(sessionId, token);
            if (session.Status != SessionStatus.Scheduled) throw SchedulingException.Conflict("SESSION_NOT_EDITABLE", "Chỉ sửa được buổi đang lên lịch.");
            if (clock.ToUtc(session.Date, session.StartTime) <= clock.UtcNow || clock.ToUtc(request.Date, request.StartTime) <= clock.UtcNow)
                throw SchedulingException.Conflict("SESSION_NOT_FUTURE", "Không thể sửa buổi đã diễn ra hoặc chuyển lịch về quá khứ.");
            if (request.Date < session.Class.StartDate || request.Date > session.Class.EndDate) throw new SchedulingException("DATE_OUTSIDE_CLASS", "Ngày nằm ngoài khoảng của lớp.");
            var room = await catalog.GetRoomAsync(request.RoomId, token) ?? throw SchedulingException.NotFound("ROOM_NOT_FOUND", "Không tìm thấy phòng.");
            if (room.Status != RoomStatus.Active || room.Capacity < session.Capacity) throw SchedulingException.Conflict("ROOM_UNAVAILABLE", "Phòng không hoạt động hoặc không đủ sức chứa.");
            if (session.CoachId is not { } coachId) throw SchedulingException.Conflict("COACH_REQUIRED", "Buổi học chưa có huấn luyện viên.");
            await ValidateCoachAsync(coachId, session.Class.SportId, token);
            if (session.Date == request.Date && session.StartTime == request.StartTime && session.EndTime == request.EndTime &&
                session.RoomId == request.RoomId && session.Note == request.Note?.Trim()) return await GetSessionAsync(sessionId, token);
            var conflicts = await FindResourceConflictsAsync([request.Date], request.StartTime, request.EndTime, request.RoomId, coachId, sessionId, token);
            conflicts.AddRange(await FindMemberConflictsAsync(memberIds, request.Date, request.StartTime, request.EndTime, sessionId, null, token));
            if (conflicts.Count != 0) throw SchedulingException.Conflict("SESSION_MOVE_CONFLICT", "Lịch mới gây xung đột.", conflicts);
            var bookingSubscriptions = await subscriptions.ListForConfirmedClassBookingsAsync(sessionId, token);
            var invalidSubscription = bookingSubscriptions.Any(x =>
                !SchedulingRules.SubscriptionCovers(x, session.Class.SportId, request.Date, needsClass: true));
            if (invalidSubscription) throw SchedulingException.Conflict("SESSION_MOVE_INVALIDATES_SUBSCRIPTION", "Ngày mới làm booking hiện hữu mất quyền gói.");
            session.Date = request.Date; session.StartTime = request.StartTime; session.EndTime = request.EndTime; session.RoomId = request.RoomId; session.Note = request.Note?.Trim();
            session.NotificationRevision++;
            audit.AddAudit(actorId, "ClassSession.Update", nameof(ClassSession), sessionId, request);
            foreach (var memberId in memberIds) notifications.AddNotification(memberId, actorId, NotificationType.ScheduleChange,
                "Lịch lớp đã thay đổi", $"Buổi {session.ClassName} đã được chuyển sang {request.Date:yyyy-MM-dd} {request.StartTime:HH:mm}.", $"class-session:{sessionId}:revision:{session.NotificationRevision}:member:{memberId}");
            notifications.AddNotification(coachId, actorId, NotificationType.ScheduleChange, "Lịch dạy đã thay đổi",
                $"Buổi {session.ClassName} đã chuyển sang {request.Date:yyyy-MM-dd} {request.StartTime:HH:mm}.", $"class-session:{sessionId}:revision:{session.NotificationRevision}:coach:{coachId}");
            await transaction.SaveChangesAsync(token);
            return await GetSessionAsync(sessionId, token);
        }, ct);
    }

    public async Task CancelSessionAsync(int actorId, int sessionId, CancelSessionRequest request, CancellationToken ct) =>
        await transaction.ExecuteAsync(async token =>
        {
            await users.EnsureActiveActorAsync(actorId, UserRole.Manager, token);
            var snapshot = await classes.GetSessionResourcesAsync(sessionId, token)
                ?? throw SchedulingException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy buổi học.");
            await transaction.LockAsync([new(ScheduleResourceType.Sport, snapshot.SportId)], token);
            var memberIds = await classes.GetConfirmedMemberIdsAsync(sessionId, token);
            var currentCoachId = await classes.GetSessionCoachIdAsync(sessionId, token);
            var locks = memberIds.Select(x => new ScheduleResourceLock(ScheduleResourceType.Member, x)).ToList();
            if (currentCoachId is { } lockedCoachId) locks.Add(new(ScheduleResourceType.Coach, lockedCoachId));
            locks.Add(new(ScheduleResourceType.Session, sessionId));
            await transaction.LockAsync(locks, token);
            var session = await classes.GetSessionWithClassAsync(sessionId, token);
            if (session.Status == SessionStatus.Cancelled) return 0;
            if (session.Status == SessionStatus.Completed) throw SchedulingException.Conflict("SESSION_COMPLETED", "Không thể hủy buổi đã hoàn tất.");
            if (clock.ToUtc(session.Date, session.StartTime) <= clock.UtcNow)
                throw SchedulingException.Conflict("SESSION_ALREADY_OCCURRED", "Không thể hủy buổi đã diễn ra.");
            var bookings = await classes.GetConfirmedBookingsAsync(sessionId, token);
            session.Status = SessionStatus.Cancelled; session.CancelledAt = clock.UtcNow; session.CancelledByUserId = actorId; session.CancellationReason = request.Reason.Trim();
            foreach (var booking in bookings)
            {
                booking.Status = BookingStatus.Cancelled; booking.CancelledAt = clock.UtcNow; booking.CancelledByUserId = actorId; booking.CancellationReason = request.Reason.Trim();
                notifications.AddNotification(booking.MemberId, actorId, NotificationType.ScheduleChange, "Buổi học đã hủy",
                    $"Buổi {session.ClassName} ngày {session.Date:yyyy-MM-dd} đã hủy: {request.Reason.Trim()}", $"class-session:{sessionId}:cancelled:{booking.MemberId}");
            }
            if (session.CoachId is { } coachId) notifications.AddNotification(coachId, actorId, NotificationType.ScheduleChange,
                "Buổi dạy đã hủy", $"Buổi {session.ClassName} ngày {session.Date:yyyy-MM-dd} đã hủy.", $"class-session:{sessionId}:cancelled:coach:{coachId}");
            audit.AddAudit(actorId, "ClassSession.Cancel", nameof(ClassSession), sessionId, new { request.Reason, BookingCount = bookings.Count });
            return 0;
        }, ct);

    private async Task<(Sport Sport, Room Room)> ValidateSportRoomAsync(int sportId, int roomId, int capacity, CancellationToken ct)
    {
        var sport = await catalog.GetSportAsync(sportId, ct) ?? throw SchedulingException.NotFound("SPORT_NOT_FOUND", "Không tìm thấy bộ môn.");
        if (sport.Status != SportStatus.Active || !sport.HasClasses) throw SchedulingException.Conflict("SPORT_NOT_CLASS_ELIGIBLE", "Bộ môn không hoạt động hoặc không hỗ trợ lớp.");
        var room = await catalog.GetRoomAsync(roomId, ct) ?? throw SchedulingException.NotFound("ROOM_NOT_FOUND", "Không tìm thấy phòng.");
        if (room.Status != RoomStatus.Active) throw SchedulingException.Conflict("ROOM_UNAVAILABLE", "Phòng không hoạt động.");
        if (capacity > room.Capacity) throw new SchedulingException("CAPACITY_EXCEEDS_ROOM", "Sức chứa lớp vượt quá sức chứa phòng.");
        return (sport, room);
    }

    private async Task ValidateCoachAsync(int coachId, int sportId, CancellationToken ct)
    {
        if (!await users.IsCoachForSportAsync(coachId, sportId, ct))
            throw SchedulingException.Conflict("COACH_NOT_ELIGIBLE", "Huấn luyện viên không hoạt động hoặc không đúng chuyên môn.");
    }

    private async Task<List<SessionConflict>> FindResourceConflictsAsync(List<DateOnly> dates, TimeOnly start, TimeOnly end,
        int roomId, int coachId, int? ignoredSessionId, CancellationToken ct) =>
        (await scheduleConflicts.FindResourceConflictsAsync(dates, start, end, roomId, coachId, ignoredSessionId, ct)).Select(ToConflict).ToList();

    private async Task<List<SessionConflict>> FindCoachConflictsAsync(int coachId, List<ClassSession> sessions, CancellationToken ct)
    {
        var result = new List<SessionConflict>();
        foreach (var s in sessions)
            result.AddRange(await FindResourceConflictsAsync([s.Date], s.StartTime, s.EndTime, s.RoomId, coachId, s.Id, ct));
        return result;
    }

    private async Task<List<SessionConflict>> FindMemberConflictsAsync(List<int> memberIds, DateOnly date, TimeOnly start, TimeOnly end,
        int? ignoredSessionId, int? ignoredPtId, CancellationToken ct) =>
        (await scheduleConflicts.FindMemberConflictsAsync(memberIds, date, start, end, ignoredSessionId, ignoredPtId, ct)).Select(ToConflict).ToList();

    private static void ValidateClassDates(UpsertClassRequest request)
    {
        if (request.EndDate <= request.StartDate) throw new SchedulingException("INVALID_CLASS_DATES", "Ngày kết thúc phải sau ngày bắt đầu.");
        if (request.EndDate.DayNumber - request.StartDate.DayNumber > 366) throw new SchedulingException("DATE_RANGE_TOO_LARGE", "Khoảng lớp tối đa 366 ngày.");
        if (request.Status == ClassStatus.Cancelled) throw new SchedulingException("USE_SESSION_CANCEL", "Không hủy lớp qua DTO chỉnh sửa.");
    }

    private async Task<ClassDto> GetClassAsync(int id, CancellationToken ct) => ToClassDto(await classes.GetClassDetailAsync(id, ct));

    private async Task<SessionDto> GetSessionAsync(int id, CancellationToken ct)
    {
        var x = await classes.GetSessionRowAsync(id, ct);
        return ToSessionDto(x.Session, x.Class, x.Sport, x.Room, x.Coach, x.Booked);
    }

    private static SessionConflict ToConflict(ResourceConflict x) => new(x.Date, x.StartTime, x.EndTime, x.Resource, x.ResourceId);

    private static ClassDto ToClassDto(Class x) => new(x.Id, x.Name, x.SportId, x.Sport.Name, x.RoomId, x.Room.Name, x.CoachId,
        x.Coach?.FullName, x.Capacity, x.Level, x.StartDate, x.EndDate, x.Status, x.Description, x.ImageUrls);

    private static SessionDto ToSessionDto(ClassSession x, Class c, string sport, string room, string? coach, int booked) =>
        new(x.Id, c.Id, x.ClassName, c.SportId, sport, x.Level, x.Date, x.StartTime, x.EndTime, x.RoomId, room, x.CoachId, coach,
            x.Capacity, Math.Max(0, x.Capacity - booked), x.Status);
    private static (int Page, int PageSize) Page(int page, int pageSize) => (Math.Max(1, page), pageSize is 10 or 20 or 50 ? pageSize : 20);
}
