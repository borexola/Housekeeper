namespace HearthSense.Core;

public enum OperationStatus
{
    Ok = 0,
    NotFound = 1,
    /// <summary>The target exists but is not in a state where the operation makes sense.</summary>
    Conflict = 2,
    /// <summary>The operation was attempted and something downstream refused it.</summary>
    Failed = 3,
}

/// <summary>A result the API layer can map straight onto a status code without throwing for control flow.</summary>
public sealed record Operation<T>(OperationStatus Status, T? Value, string? Error)
{
    public static Operation<T> Ok(T value) => new(OperationStatus.Ok, value, null);
    public static Operation<T> NotFound(string error) => new(OperationStatus.NotFound, default, error);
    public static Operation<T> Conflict(string error) => new(OperationStatus.Conflict, default, error);
    public static Operation<T> Failed(string error) => new(OperationStatus.Failed, default, error);
}
