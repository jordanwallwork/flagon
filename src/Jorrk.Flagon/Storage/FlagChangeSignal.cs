namespace Jorrk.Flagon.Storage;

/// <summary>
/// What an <see cref="IFlagChangeNotifier"/> reports with each signal.
/// </summary>
/// <param name="CommittedAt">
/// When the change committed, in the clock of the machine that recorded it (for a database store, the database clock), or
/// null when the store does not know. Used only to measure how long a change takes to reach the snapshot.
/// </param>
/// <param name="Resubscribed">
/// True for the signal raised after the notifier reconnects. Changes that committed while it was disconnected were not
/// signalled, so the app should refresh now. The signal carries no commit time.
/// </param>
public readonly record struct FlagChangeSignal(DateTimeOffset? CommittedAt = null, bool Resubscribed = false);
