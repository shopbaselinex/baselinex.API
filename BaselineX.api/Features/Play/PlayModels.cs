using System.ComponentModel.DataAnnotations;
using BaselineX.Api.Common;
using BaselineX.Api.Features.Catalog;
using BaselineX.Api.Features.Organizations;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Play;

public enum ActivityType
{
    PrivateLesson, SemiPrivateLesson, GroupClinic, LiveBall, CardioTennis, BeginnerClass,
    IntermediateClinic, AdvancedClinic, JuniorClinic, PickleballLesson, PickleballOpenPlay,
    PadelLesson, SquashLesson, HittingSession, Camp, SocialEvent,
}
public enum PriceUnit { PerPerson, PerSession, PerHour, Flat }
public enum SessionStatus { Scheduled, Cancelled, Completed }
public enum BookingStatus { PendingPayment, Confirmed, Cancelled, Refunded, Completed, NoShow }

// At least one of HostUserId / OrganizationId must be set (check constraint).
[Index(nameof(HostUserId))]
[Index(nameof(OrganizationId))]
[Index(nameof(SportId), nameof(Type), nameof(IsActive))]
public class Activity : IHasTimestamps
{
    public Guid Id { get; set; }
    public Guid? HostUserId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public int SportId { get; set; }
    public Sport Sport { get; set; } = null!;
    public ActivityType Type { get; set; }
    [MaxLength(220)] public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    [MaxLength(100)] public string? SkillLevel { get; set; }
    [MaxLength(100)] public string? AgeGroup { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public PriceUnit PriceUnit { get; set; } = PriceUnit.PerPerson;
    public int? DefaultCapacity { get; set; }
    public string? EquipmentRequirements { get; set; }
    public string? CancellationPolicy { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ActivitySession> Sessions { get; set; } = [];
}

// Check constraints: 0 <= BookedCount <= Capacity, EndTimeUtc > StartTimeUtc.
// BookedCount changes only through ActivitySessionSeats (atomic UPDATE).
[Index(nameof(ActivityId), nameof(StartTimeUtc))]
[Index(nameof(Status), nameof(StartTimeUtc))]
public class ActivitySession : IHasTimestamps
{
    public Guid Id { get; set; }
    public Guid ActivityId { get; set; }
    public Activity Activity { get; set; } = null!;
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }
    public int Capacity { get; set; }
    public int BookedCount { get; private set; }
    [MaxLength(180)] public string? VenueName { get; set; }
    [MaxLength(160)] public string? Area { get; set; }
    public string? AddressPrivate { get; set; }
    public SessionStatus Status { get; private set; } = SessionStatus.Scheduled;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public uint Version { get; set; }
    public List<Booking> Bookings { get; set; } = [];

    public void Cancel() =>
        Status = Transition.To(Status, SessionStatus.Cancelled, SessionStatus.Scheduled);

    public void Complete() =>
        Status = Transition.To(Status, SessionStatus.Completed, SessionStatus.Scheduled);
}

public static class ActivitySessionSeats
{
    // Atomic seat reservation. Returns false when the session is full or not scheduled.
    // Call inside the same transaction that inserts the Booking.
    public static async Task<bool> TryReserveAsync(this DbSet<ActivitySession> sessions, Guid sessionId, int count,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rows = await sessions
            .Where(s => s.Id == sessionId && s.Status == SessionStatus.Scheduled && s.BookedCount + count <= s.Capacity)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.BookedCount, s => s.BookedCount + count)
                .SetProperty(s => s.UpdatedAt, now), ct);
        return rows == 1;
    }

    // Call on booking cancel/refund, in the same transaction as the booking status change.
    public static async Task ReleaseAsync(this DbSet<ActivitySession> sessions, Guid sessionId, int count,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await sessions
            .Where(s => s.Id == sessionId && s.BookedCount >= count)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.BookedCount, s => s.BookedCount - count)
                .SetProperty(s => s.UpdatedAt, now), ct);
    }
}

[Index(nameof(ActivitySessionId), nameof(Status))]
[Index(nameof(UserId), nameof(CreatedAt))]
[Index(nameof(ProviderPaymentIntentId), IsUnique = true)]
public class Booking : IHasTimestamps
{
    public Guid Id { get; set; }
    public Guid ActivitySessionId { get; set; }
    public ActivitySession ActivitySession { get; set; } = null!;
    public Guid UserId { get; set; }
    public int ParticipantCount { get; set; } = 1;
    public decimal Subtotal { get; set; }
    public decimal PlatformFee { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "USD";
    public BookingStatus Status { get; private set; } = BookingStatus.PendingPayment;
    [MaxLength(255)] public string? ProviderPaymentIntentId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CancelledAt { get; private set; }
    public uint Version { get; set; }

    public void Confirm() =>
        Status = Transition.To(Status, BookingStatus.Confirmed, BookingStatus.PendingPayment);

    public void Cancel()
    {
        Status = Transition.To(Status, BookingStatus.Cancelled, BookingStatus.PendingPayment, BookingStatus.Confirmed);
        CancelledAt = DateTime.UtcNow;
    }

    public void MarkRefunded() =>
        Status = Transition.To(Status, BookingStatus.Refunded, BookingStatus.Confirmed, BookingStatus.Cancelled);

    public void Complete() =>
        Status = Transition.To(Status, BookingStatus.Completed, BookingStatus.Confirmed);

    public void MarkNoShow() =>
        Status = Transition.To(Status, BookingStatus.NoShow, BookingStatus.Confirmed);
}
