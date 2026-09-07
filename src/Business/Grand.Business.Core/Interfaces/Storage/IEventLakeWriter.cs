namespace Grand.Business.Core.Interfaces.Storage;

/// <summary>
///     Appends raw domain events to the bronze layer of a data lake.
///
///     Bronze is append-only and never edited: events land exactly as they were captured, and every
///     later layer is derived from them. That is what makes reprocessing possible - a mapping bug
///     found in three months is fixed by rebuilding silver from bronze, which is only an option if
///     bronze was never interpreted on the way in.
/// </summary>
public interface IEventLakeWriter
{
    /// <summary>
    ///     True when a lake is configured. Callers check this instead of paying for a batch they
    ///     cannot write.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    ///     Appends one batch as a single object. Batching rather than writing per event is
    ///     deliberate: object stores charge per operation and lakehouse engines read many small
    ///     files badly.
    /// </summary>
    /// <param name="dataset">Logical stream, e.g. shipment_events. Becomes a folder under bronze.</param>
    /// <param name="eventDate">Event date the batch belongs to; drives the dt= partition.</param>
    /// <param name="jsonLines">One JSON document per element. Written newline-delimited.</param>
    Task AppendBatch(string dataset, DateTime eventDate, IReadOnlyCollection<string> jsonLines);
}
