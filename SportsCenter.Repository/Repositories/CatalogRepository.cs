using Microsoft.EntityFrameworkCore;
using SportsCenter.Repository.Data;
using SportsCenter.Repository.Entities;

namespace SportsCenter.Repository.Repositories;

public interface ICatalogRepository
{
    Task<List<Sport>> ListSportsAsync(CancellationToken ct);
    Task<Sport?> GetSportAsync(int id, CancellationToken ct);
    Task<bool> SportNameExistsAsync(string name, int? excludeId, CancellationToken ct);
    void AddSport(Sport sport);
    Task<bool> SportHasOpenClassesAsync(int sportId, CancellationToken ct);
    Task<bool> SportHasConfirmedPtBookingsAsync(int sportId, CancellationToken ct);
    Task<List<Room>> ListRoomsAsync(CancellationToken ct);
    Task<Room?> GetRoomAsync(int id, CancellationToken ct);
    Task<bool> RoomNameExistsAsync(string name, int? excludeId, CancellationToken ct);
    void AddRoom(Room room);
    Task<bool> RoomCapacityInUseAsync(int roomId, int capacity, CancellationToken ct);
}

public class CatalogRepository(AppDbContext db) : ICatalogRepository
{
    public Task<List<Sport>> ListSportsAsync(CancellationToken ct) => db.Sports.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<Sport?> GetSportAsync(int id, CancellationToken ct) => await db.Sports.FindAsync([id], ct);

    public Task<bool> SportNameExistsAsync(string name, int? excludeId, CancellationToken ct) =>
        db.Sports.AnyAsync(x => (excludeId == null || x.Id != excludeId) && x.Name.ToLower() == name.ToLower(), ct);

    public void AddSport(Sport sport) => db.Sports.Add(sport);

    public Task<bool> SportHasOpenClassesAsync(int sportId, CancellationToken ct) =>
        db.Classes.AnyAsync(x => x.SportId == sportId && x.Status != ClassStatus.Cancelled, ct);

    public Task<bool> SportHasConfirmedPtBookingsAsync(int sportId, CancellationToken ct) =>
        db.PtBookings.AnyAsync(x => x.SportId == sportId && x.Status == BookingStatus.Confirmed, ct);

    public Task<List<Room>> ListRoomsAsync(CancellationToken ct) => db.Rooms.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<Room?> GetRoomAsync(int id, CancellationToken ct) => await db.Rooms.FindAsync([id], ct);

    public Task<bool> RoomNameExistsAsync(string name, int? excludeId, CancellationToken ct) =>
        db.Rooms.AnyAsync(x => (excludeId == null || x.Id != excludeId) && x.Name.ToLower() == name.ToLower(), ct);

    public void AddRoom(Room room) => db.Rooms.Add(room);

    // True when a non-cancelled class or session in the room is larger than the proposed capacity.
    public async Task<bool> RoomCapacityInUseAsync(int roomId, int capacity, CancellationToken ct) =>
        await db.Classes.AnyAsync(x => x.RoomId == roomId && x.Capacity > capacity && x.Status != ClassStatus.Cancelled, ct) ||
        await db.ClassSessions.AnyAsync(x => x.RoomId == roomId && x.Capacity > capacity && x.Status != SessionStatus.Cancelled, ct);
}
