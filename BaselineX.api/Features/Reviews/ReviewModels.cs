using BaselineX.Api.Common;
using Microsoft.EntityFrameworkCore;

namespace BaselineX.Api.Features.Reviews;

public enum ReviewSubjectType { Seller, Coach, Organization, Activity }

// Check constraint: Rating BETWEEN 1 AND 5.
// Polymorphic SubjectId has no FK; validate existence in the handler.
// Only allow reviews after a completed order/booking with the subject.
[Index(nameof(ReviewerId), nameof(SubjectType), nameof(SubjectId), IsUnique = true)]
[Index(nameof(SubjectType), nameof(SubjectId))]
[Index(nameof(CreatedAt))]
public class Review : IHasCreatedAt
{
    public Guid Id { get; set; }
    public Guid ReviewerId { get; set; }
    public ReviewSubjectType SubjectType { get; set; }
    public Guid SubjectId { get; set; }
    public int Rating { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }
}
