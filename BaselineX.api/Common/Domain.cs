namespace BaselineX.Api.Common;

public interface IHasCreatedAt
{
    DateTime CreatedAt { get; set; }
}

// UpdatedAt is stamped by AppDbContext.SaveChanges, never by the DB.
public interface IHasTimestamps : IHasCreatedAt
{
    DateTime UpdatedAt { get; set; }
}

public class DomainException(string message) : InvalidOperationException(message);

internal static class Transition
{
    // Status = Transition.To(Status, Next, AllowedFrom1, AllowedFrom2, ...)
    public static T To<T>(T current, T next, params T[] allowedFrom) where T : struct, Enum =>
        allowedFrom.Contains(current)
            ? next
            : throw new DomainException($"Cannot change {typeof(T).Name} from {current} to {next}.");
}
