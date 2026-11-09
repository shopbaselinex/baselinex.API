using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BaselineX.Api.Common;
using BaselineX.Api.Data;
using BaselineX.Api.Features.Organizations;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Play;

public record ActivitySummaryDto(Guid Id, string Title, ActivityType Type, string Sport, Guid? HostUserId,
    Guid? OrganizationId, string? OrganizationName, decimal Price, string Currency, PriceUnit PriceUnit,
    string? SkillLevel, DateTime? NextSessionUtc);

public record SessionDto(Guid Id, Guid ActivityId, DateTime StartTimeUtc, DateTime EndTimeUtc, int Capacity, int BookedCount,
    int SpotsLeft, string? VenueName, string? Area, SessionStatus Status);

public record ActivityDto(Guid Id, string Title, string Description, ActivityType Type, int SportId, string Sport,
    Guid? HostUserId, Guid? OrganizationId, string? OrganizationName, string? SkillLevel, string? AgeGroup,
    decimal Price, string Currency, PriceUnit PriceUnit, int? DefaultCapacity, string? EquipmentRequirements,
    string? CancellationPolicy, bool IsActive, IEnumerable<SessionDto> UpcomingSessions);

public record SaveActivityRequest(
    Guid? OrganizationId,
    int SportId,
    ActivityType Type,
    [Required, MaxLength(220)] string Title,
    [Required] string Description,
    [MaxLength(100)] string? SkillLevel,
    [MaxLength(100)] string? AgeGroup,
    [Range(0, 100_000)] decimal Price,
    PriceUnit PriceUnit,
    [Range(1, 1000)] int? DefaultCapacity,
    string? EquipmentRequirements,
    string? CancellationPolicy,
    bool IsActive = true);

public record CreateSessionRequest(
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    [Range(1, 1000)] int? Capacity,
    [MaxLength(180)] string? VenueName,
    [MaxLength(160)] string? Area,
    string? AddressPrivate);

public record BookSessionRequest([Range(1, 50)] int ParticipantCount = 1);

public record BookingDto(Guid Id, Guid ActivitySessionId, Guid ActivityId, string ActivityTitle, DateTime StartTimeUtc,
    DateTime EndTimeUtc, string? VenueName, string? Area, string? Address, Guid UserId, int ParticipantCount,
    decimal Subtotal, decimal PlatformFee, decimal TaxAmount, decimal Total, string Currency, BookingStatus Status,
    DateTime CreatedAt, DateTime? CancelledAt);

public static class PlayEndpoints
{
    private static readonly OrganizationRole[] ActivityRoles =
        [OrganizationRole.Owner, OrganizationRole.Administrator, OrganizationRole.Coach];

    public static void MapPlayEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("Play");

        g.MapGet("/activities", async (AppDbContext db, int? sportId, ActivityType? type, Guid? organizationId,
            Guid? hostUserId, string? q, int? page, int? pageSize) =>
        {
            var now = DateTime.UtcNow;
            var query = db.Activities.AsNoTracking().Where(a => a.IsActive);
            if (sportId is not null) query = query.Where(a => a.SportId == sportId);
            if (type is not null) query = query.Where(a => a.Type == type);
            if (organizationId is not null) query = query.Where(a => a.OrganizationId == organizationId);
            if (hostUserId is not null) query = query.Where(a => a.HostUserId == hostUserId);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(a => EF.Functions.ILike(a.Title, $"%{q}%"));

            return await query.OrderByDescending(a => a.CreatedAt)
                .Select(a => new ActivitySummaryDto(a.Id, a.Title, a.Type, a.Sport.Name, a.HostUserId, a.OrganizationId,
                    a.Organization != null ? a.Organization.Name : null, a.Price, a.Currency, a.PriceUnit, a.SkillLevel,
                    a.Sessions.Where(s => s.Status == SessionStatus.Scheduled && s.StartTimeUtc >= now)
                        .OrderBy(s => s.StartTimeUtc).Select(s => (DateTime?)s.StartTimeUtc).FirstOrDefault()))
                .ToPagedAsync(page, pageSize);
        });

        g.MapGet("/activities/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var now = DateTime.UtcNow;
            return await db.Activities.AsNoTracking().Where(a => a.Id == id)
                .Select(a => new ActivityDto(a.Id, a.Title, a.Description, a.Type, a.SportId, a.Sport.Name, a.HostUserId,
                    a.OrganizationId, a.Organization != null ? a.Organization.Name : null, a.SkillLevel, a.AgeGroup,
                    a.Price, a.Currency, a.PriceUnit, a.DefaultCapacity, a.EquipmentRequirements, a.CancellationPolicy,
                    a.IsActive,
                    a.Sessions.Where(s => s.Status == SessionStatus.Scheduled && s.StartTimeUtc >= now)
                        .OrderBy(s => s.StartTimeUtc).Take(50)
                        .Select(s => new SessionDto(s.Id, s.ActivityId, s.StartTimeUtc, s.EndTimeUtc, s.Capacity,
                            s.BookedCount, s.Capacity - s.BookedCount, s.VenueName, s.Area, s.Status))))
                .FirstOrDefaultAsync() is { } a ? Results.Ok(a) : Results.NotFound();
        });

        g.MapPost("/activities", async (SaveActivityRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            if (await Validate(req, me, db) is { } error) return Results.BadRequest(error);
            var activity = new Activity { HostUserId = me };
            Apply(activity, req);
            db.Activities.Add(activity);
            await db.SaveChangesAsync();
            return Results.Created($"/activities/{activity.Id}", new { activity.Id });
        }).RequireAuthorization();

        g.MapPut("/activities/{id:guid}", async (Guid id, SaveActivityRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var me = user.UserId();
            var activity = await db.Activities.FirstOrDefaultAsync(a => a.Id == id);
            if (activity is null) return Results.NotFound();
            if (!await CanManage(db, activity, me)) return Results.Forbid();
            if (await Validate(req, me, db) is { } error) return Results.BadRequest(error);
            Apply(activity, req);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapGet("/activities/{id:guid}/sessions", async (Guid id, AppDbContext db, DateTimeOffset? from, DateTimeOffset? to) =>
        {
            var start = (from ?? DateTimeOffset.UtcNow).UtcDateTime;
            var query = db.ActivitySessions.AsNoTracking().Where(s => s.ActivityId == id && s.StartTimeUtc >= start);
            if (to is not null) query = query.Where(s => s.StartTimeUtc <= to.Value.UtcDateTime);
            return await query.OrderBy(s => s.StartTimeUtc).Take(200)
                .Select(s => new SessionDto(s.Id, s.ActivityId, s.StartTimeUtc, s.EndTimeUtc, s.Capacity, s.BookedCount,
                    s.Capacity - s.BookedCount, s.VenueName, s.Area, s.Status))
                .ToListAsync();
        });

        g.MapPost("/activities/{id:guid}/sessions", async (Guid id, CreateSessionRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            var activity = await db.Activities.FirstOrDefaultAsync(a => a.Id == id);
            if (activity is null) return Results.NotFound();
            if (!await CanManage(db, activity, user.UserId())) return Results.Forbid();
            if (req.EndTime <= req.StartTime) return Results.BadRequest("End time must be after start time.");
            if ((req.Capacity ?? activity.DefaultCapacity) is not { } capacity) return Results.BadRequest("Capacity is required.");

            var session = new ActivitySession
            {
                ActivityId = id, StartTimeUtc = req.StartTime.UtcDateTime, EndTimeUtc = req.EndTime.UtcDateTime,
                Capacity = capacity, VenueName = req.VenueName, Area = req.Area, AddressPrivate = req.AddressPrivate,
            };
            db.ActivitySessions.Add(session);
            await db.SaveChangesAsync();
            return Results.Created($"/sessions/{session.Id}", new { session.Id });
        }).RequireAuthorization();

        // Confirmed bookings on a cancelled session still need refunds (Stripe, not wired up yet).
        g.MapPost("/sessions/{id:guid}/cancel", (Guid id, ClaimsPrincipal user, AppDbContext db) =>
            ManageSession(id, user, db, async session =>
            {
                session.Cancel();
                var pending = await db.Bookings
                    .Where(b => b.ActivitySessionId == id && b.Status == BookingStatus.PendingPayment).ToListAsync();
                pending.ForEach(b => b.Cancel());
            })).RequireAuthorization();

        g.MapPost("/sessions/{id:guid}/complete", (Guid id, ClaimsPrincipal user, AppDbContext db) =>
            ManageSession(id, user, db, async session =>
            {
                session.Complete();
                var confirmed = await db.Bookings
                    .Where(b => b.ActivitySessionId == id && b.Status == BookingStatus.Confirmed).ToListAsync();
                confirmed.ForEach(b => b.Complete());
            })).RequireAuthorization();

        g.MapGet("/sessions/{id:guid}/bookings", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var session = await db.ActivitySessions.AsNoTracking().Include(s => s.Activity).FirstOrDefaultAsync(s => s.Id == id);
            if (session is null) return Results.NotFound();
            if (!await CanManage(db, session.Activity, user.UserId())) return Results.Forbid();
            return Results.Ok(await db.Bookings.AsNoTracking().Where(b => b.ActivitySessionId == id)
                .OrderBy(b => b.CreatedAt).Select(BookingDto(includeAddress: true)).ToListAsync());
        }).RequireAuthorization();

        // Seats are taken with one atomic UPDATE in the same transaction as the booking insert.
        g.MapPost("/sessions/{id:guid}/bookings", async (Guid id, BookSessionRequest req, ClaimsPrincipal user,
            AppDbContext db, IConfiguration config) =>
        {
            var session = await db.ActivitySessions.AsNoTracking().Include(s => s.Activity).FirstOrDefaultAsync(s => s.Id == id);
            if (session is null) return Results.NotFound();
            if (session.StartTimeUtc <= DateTime.UtcNow) throw new DomainException("This session has already started.");

            var a = session.Activity;
            var subtotal = a.PriceUnit switch
            {
                PriceUnit.PerPerson => a.Price * req.ParticipantCount,
                PriceUnit.PerHour => Math.Round(a.Price * (decimal)(session.EndTimeUtc - session.StartTimeUtc).TotalHours, 2),
                _ => a.Price,
            };
            var fee = Math.Round(subtotal * config.GetValue("Fees:PlayPercent", 0m) / 100m, 2);
            var booking = new Booking
            {
                ActivitySessionId = id, UserId = user.UserId(), ParticipantCount = req.ParticipantCount,
                Subtotal = subtotal, PlatformFee = fee, TaxAmount = 0m, Total = subtotal + fee, Currency = a.Currency,
            };

            await using var tx = await db.Database.BeginTransactionAsync();
            if (!await db.ActivitySessions.TryReserveAsync(id, req.ParticipantCount))
                throw new DomainException("Not enough spots left, or the session is no longer open.");
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            // TODO: create the Stripe PaymentIntent and store ProviderPaymentIntentId.
            return Results.Created($"/bookings/{booking.Id}", await GetBooking(db, booking.Id));
        }).RequireAuthorization();

        g.MapGet("/me/bookings", async (ClaimsPrincipal user, AppDbContext db, BookingStatus? status, int? page, int? pageSize) =>
        {
            var me = user.UserId();
            var query = db.Bookings.AsNoTracking().Where(b => b.UserId == me);
            if (status is not null) query = query.Where(b => b.Status == status);
            return await query.OrderByDescending(b => b.ActivitySession.StartTimeUtc)
                .Select(BookingDto(includeAddress: false)).ToPagedAsync(page, pageSize);
        }).RequireAuthorization();

        // Unpaid bookings only; cancelling a paid booking needs a Stripe refund.
        g.MapPost("/bookings/{id:guid}/cancel", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == id);
            if (booking is null || booking.UserId != user.UserId()) return Results.NotFound();
            if (booking.Status != BookingStatus.PendingPayment)
                throw new DomainException("Only unpaid bookings can be cancelled here.");

            await using var tx = await db.Database.BeginTransactionAsync();
            booking.Cancel();
            await db.SaveChangesAsync();
            await db.ActivitySessions.ReleaseAsync(booking.ActivitySessionId, booking.ParticipantCount);
            await tx.CommitAsync();
            return Results.Ok(await GetBooking(db, id));
        }).RequireAuthorization();
    }

    internal static Task<BookingDto?> GetBooking(AppDbContext db, Guid id) =>
        db.Bookings.AsNoTracking().Where(b => b.Id == id).Select(BookingDto(includeAddress: false)).FirstOrDefaultAsync();

    // The private address is only revealed to hosts and to players with a paid booking.
    private static System.Linq.Expressions.Expression<Func<Booking, BookingDto>> BookingDto(bool includeAddress) => b =>
        new BookingDto(b.Id, b.ActivitySessionId, b.ActivitySession.ActivityId, b.ActivitySession.Activity.Title,
            b.ActivitySession.StartTimeUtc, b.ActivitySession.EndTimeUtc, b.ActivitySession.VenueName, b.ActivitySession.Area,
            includeAddress || b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.Completed
                ? b.ActivitySession.AddressPrivate : null,
            b.UserId, b.ParticipantCount, b.Subtotal, b.PlatformFee, b.TaxAmount, b.Total, b.Currency, b.Status,
            b.CreatedAt, b.CancelledAt);

    private static async Task<IResult> ManageSession(Guid id, ClaimsPrincipal user, AppDbContext db, Func<ActivitySession, Task> action)
    {
        var session = await db.ActivitySessions.Include(s => s.Activity).FirstOrDefaultAsync(s => s.Id == id);
        if (session is null) return Results.NotFound();
        if (!await CanManage(db, session.Activity, user.UserId())) return Results.Forbid();
        await action(session);
        await db.SaveChangesAsync();
        return Results.Ok(new { session.Id, session.Status });
    }

    private static async Task<bool> CanManage(AppDbContext db, Activity activity, Guid userId) =>
        activity.HostUserId == userId ||
        (activity.OrganizationId is { } orgId && await db.HasRoleAsync(orgId, userId, ActivityRoles));

    private static async Task<string?> Validate(SaveActivityRequest req, Guid userId, AppDbContext db)
    {
        if (!await db.Sports.AnyAsync(s => s.Id == req.SportId)) return "Unknown sport.";
        if (req.OrganizationId is { } orgId && !await db.HasRoleAsync(orgId, userId, ActivityRoles))
            return "You cannot create activities for this organization.";
        return null;
    }

    private static void Apply(Activity a, SaveActivityRequest r)
    {
        (a.OrganizationId, a.SportId, a.Type, a.Title, a.Description) = (r.OrganizationId, r.SportId, r.Type, r.Title, r.Description);
        (a.SkillLevel, a.AgeGroup, a.Price, a.PriceUnit, a.DefaultCapacity) = (r.SkillLevel, r.AgeGroup, r.Price, r.PriceUnit, r.DefaultCapacity);
        (a.EquipmentRequirements, a.CancellationPolicy, a.IsActive) = (r.EquipmentRequirements, r.CancellationPolicy, r.IsActive);
    }
}
