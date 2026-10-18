using System.Text.Json;
using Flagon.Internal;
using Flagon.Storage;

namespace Flagon.Testing;

/// <summary>
/// Synchronous, chainable flag setup for tests. Every write is an upsert that replaces a flag of any type and state, and
/// is visible to the very next evaluation: the snapshot is refreshed before the method returns. Writes go straight to
/// the store without an audit entry, so the audit history shows only the app's own admin writes.
/// </summary>
/// <remarks>Safe to call from parallel tasks and from inside a single-threaded <see cref="SynchronizationContext"/>.</remarks>
public sealed class TestFlags
{
    private readonly IFlagStore _store;
    private readonly FlagSnapshotManager _snapshots;

    internal TestFlags(IFlagStore store, FlagSnapshotManager snapshots)
    {
        _store = store;
        _snapshots = snapshots;
    }

    /// <summary>Sets <paramref name="key"/> to an enabled boolean flag (<c>on</c>/<c>off</c> variants) serving <paramref name="value"/>.</summary>
    /// <param name="key">The flag key.</param>
    /// <param name="value">The value every evaluation of the flag is served.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException"><paramref name="key"/> is not a valid flag key.</exception>
    public TestFlags SetFlag(string key, bool value) => Upsert(key, FlagDocument.Boolean(value));

    /// <summary>
    /// Sets <paramref name="key"/> to an enabled string flag with a single <c>default</c> variant serving
    /// <paramref name="value"/>, the same shape as a string flag defined in code or auto-created.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="value">The value every evaluation of the flag is served.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException"><paramref name="key"/> is not a valid flag key.</exception>
    public TestFlags SetFlag(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Upsert(key, FlagDocument.SingleValue(JsonSerializer.SerializeToElement(value)));
    }

    /// <summary>
    /// Sets <paramref name="key"/> to an enabled number flag with a single <c>default</c> variant serving
    /// <paramref name="value"/>. Otherwise as <see cref="SetFlag(string, string)"/>.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="value">The value every evaluation of the flag is served.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException"><paramref name="key"/> is not a valid flag key.</exception>
    public TestFlags SetFlag(string key, int value) =>
        Upsert(key, FlagDocument.SingleValue(JsonSerializer.SerializeToElement(value)));

    /// <summary>
    /// Sets <paramref name="key"/> to an enabled number flag with a single <c>default</c> variant serving
    /// <paramref name="value"/>. Otherwise as <see cref="SetFlag(string, string)"/>.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="value">The value every evaluation of the flag is served.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException">
    /// <paramref name="key"/> is not a valid flag key, or <paramref name="value"/> is NaN or infinite (JSON has no such numbers).
    /// </exception>
    public TestFlags SetFlag(string key, double value) => double.IsFinite(value)
        ? Upsert(key, FlagDocument.SingleValue(JsonSerializer.SerializeToElement(value)))
        : throw new FlagValidationException($"Flag '{key}' variant 'default' is {value}; number variants must be finite.");

    /// <summary>
    /// Sets <paramref name="key"/> to an enabled object flag with a single <c>default</c> variant serving the JSON object
    /// <paramref name="json"/>. Otherwise as <see cref="SetFlag(string, string)"/>.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="json">The value every evaluation of the flag is served: JSON text of an object, e.g. <c>{"columns":3}</c>.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException">
    /// <paramref name="key"/> is not a valid flag key, or <paramref name="json"/> is not valid JSON or not an object.
    /// </exception>
    public TestFlags SetObjectFlag(string key, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonElement value;
        try
        {
            using var document = JsonDocument.Parse(json);
            value = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new FlagValidationException($"Flag '{key}' object value is not valid JSON: {ex.Message}");
        }

        return Upsert(key, SingleObject(key, value, nameof(SetObjectFlag)));
    }

    /// <summary>
    /// Sets <paramref name="key"/> to an enabled object flag with a single <c>default</c> variant serving
    /// <paramref name="value"/> serialised to JSON, as <see cref="IFlagon.GetObject{T}"/> reads it back. Otherwise as
    /// <see cref="SetFlag(string, string)"/>.
    /// </summary>
    /// <typeparam name="T">The type of the value; it must serialise to a JSON object.</typeparam>
    /// <param name="key">The flag key.</param>
    /// <param name="value">The value every evaluation of the flag is served.</param>
    /// <param name="options">Serializer options for <paramref name="value"/>; pass the same options to the getter.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException">
    /// <paramref name="key"/> is not a valid flag key, or <paramref name="value"/> does not serialise to a JSON object.
    /// </exception>
    public TestFlags SetFlag<T>(string key, T value, JsonSerializerOptions? options = null) =>
        Upsert(key, SingleObject(key, JsonSerializer.SerializeToElement(value, options), nameof(SetFlag)));

    private static FlagDocument SingleObject(string key, JsonElement value, string method) =>
        value.ValueKind == JsonValueKind.Object
            ? FlagDocument.SingleValue(value)
            : throw new FlagValidationException(
                $"Flag '{key}' object value is a JSON {value.ValueKind.ToString().ToLowerInvariant()}; {method} takes a JSON object.");

    private TestFlags Upsert(string key, FlagDocument document)
    {
        FlagKey.Validate(key);
        // The in-memory store completes synchronously and every awaited path uses ConfigureAwait(false), so blocking here
        // cannot deadlock a test framework's synchronization context.
        UpsertAsync(key, document).GetAwaiter().GetResult();
        return this;
    }

    private async Task UpsertAsync(string key, FlagDocument document)
    {
        // A concurrent write to the same key (another task, or the app's own admin write) moves the version: re-read and retry.
        while (!await TryWriteAsync(key, document).ConfigureAwait(false))
        {
        }

        await _snapshots.RefreshAsync().ConfigureAwait(false);
    }

    private async Task<bool> TryWriteAsync(string key, FlagDocument document)
    {
        var current = await _store.GetAsync(key).ConfigureAwait(false);
        if (current is null)
        {
            return await _store.TryCreateAsync(key, document).ConfigureAwait(false) is not null;
        }

        try
        {
            if (current.Archived)
            {
                current = await _store.RestoreAsync(key, current.Version).ConfigureAwait(false);
            }

            await _store.UpdateAsync(key, document, current.Version).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is FlagVersionConflictException or FlagNotFoundException)
        {
            return false;
        }
    }
}
