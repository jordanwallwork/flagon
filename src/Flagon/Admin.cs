namespace Flagon;

/// <summary>A request to create a boolean flag.</summary>
/// <param name="Key">The flag key; must satisfy <see cref="FlagKey.Pattern"/>.</param>
/// <param name="Enabled">Initial value served by the flag.</param>
/// <param name="Description">Optional description.</param>
/// <param name="Tags">Optional tags (no commas).</param>
public sealed record CreateFlagRequest(string Key, bool Enabled = false, string? Description = null, IReadOnlyList<string>? Tags = null);

/// <summary>
/// In-process flag management. Reads go to the store (not the evaluation snapshot); writes are applied to the store
/// and reflected in this app's <see cref="IFlagon"/> immediately.
/// Updates take the version the caller last saw and throw <see cref="FlagVersionConflictException"/> if it is stale.
/// </summary>
public interface IFlagonAdmin
{
    /// <summary>Lists all flags.</summary>
    Task<IReadOnlyList<FlagRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets a flag, or null if none exists with <paramref name="key"/>.</summary>
    Task<FlagRecord?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Creates a boolean flag with source <see cref="FlagSource.Manual"/>.</summary>
    /// <exception cref="FlagValidationException">The request is invalid.</exception>
    /// <exception cref="FlagAlreadyExistsException">A flag with the key already exists.</exception>
    Task<FlagRecord> CreateAsync(CreateFlagRequest request, CancellationToken cancellationToken = default);

    /// <summary>Toggles a boolean flag on or off (switches its default variant).</summary>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    Task<FlagRecord> SetEnabledAsync(string key, bool enabled, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives a flag: its document is kept, but evaluation returns the caller's default with reason
    /// <see cref="EvaluationReason.Archived"/> and the key is never re-created automatically.
    /// </summary>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The flag is already archived.</exception>
    Task<FlagRecord> ArchiveAsync(string key, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Restores an archived flag so it evaluates normally again.</summary>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The flag is not archived.</exception>
    Task<FlagRecord> RestoreAsync(string key, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes an archived flag (its audit history is kept). The key then evaluates as not found and may be created again.
    /// </summary>
    /// <exception cref="FlagNotFoundException">No flag exists with the key.</exception>
    /// <exception cref="FlagVersionConflictException">The flag changed since <paramref name="expectedVersion"/>.</exception>
    /// <exception cref="FlagValidationException">The flag is not archived (only archived flags can be purged).</exception>
    Task PurgeAsync(string key, long expectedVersion, CancellationToken cancellationToken = default);
}
