using Jorrk.Flagon.Storage;

namespace Jorrk.Flagon.Internal;

/// <summary>Default <see cref="IFlagonActorAccessor"/>: the ambient <see cref="FlagonActor.Current"/>.</summary>
internal sealed class AmbientFlagonActorAccessor : IFlagonActorAccessor
{
    public string? Actor => FlagonActor.Current;
}

/// <summary>
/// Builds the <see cref="FlagChangeContext"/> (timestamp, actor, application) for a write and stamps flag metadata
/// with it. Every admin/seeding write should start with <see cref="Begin"/> and pass the result to the store.
/// </summary>
internal sealed class FlagChangeStamper(TimeProvider time, IFlagonActorAccessor actor, string applicationName)
{
    /// <summary>This app's resolved application name (<see cref="FlagonOptions.ApplicationName"/> or the host's).</summary>
    public string ApplicationName { get; } = applicationName;

    /// <summary>Captures who/when/where for one write.</summary>
    public FlagChangeContext Begin() => new(time.GetUtcNow(), actor.Actor, ApplicationName);
}

/// <summary>Metadata stamping helpers driven by a <see cref="FlagChangeContext"/>.</summary>
internal static class FlagChangeStamping
{
    /// <summary>Sets createdAt/By and updatedAt/By (a new flag).</summary>
    public static FlagMetadata StampCreated(this FlagMetadata metadata, FlagChangeContext change) => metadata with
    {
        CreatedAt = change.Timestamp,
        CreatedBy = change.Actor,
        UpdatedAt = change.Timestamp,
        UpdatedBy = change.Actor,
    };

    /// <summary>Sets updatedAt/By, leaving the created stamp alone.</summary>
    public static FlagMetadata StampUpdated(this FlagMetadata metadata, FlagChangeContext change) => metadata with
    {
        UpdatedAt = change.Timestamp,
        UpdatedBy = change.Actor,
    };

    /// <summary>Returns <paramref name="document"/> with updatedAt/By stamped.</summary>
    public static FlagDocument StampUpdated(this FlagDocument document, FlagChangeContext change) =>
        document with { Metadata = document.Metadata.StampUpdated(change) };
}
