using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Grand.Business.Core.Interfaces.Storage;
using Grand.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Grand.Business.Storage.Services;

/// <summary>
///     Writes the bronze layer to ADLS Gen2 / Azure Blob storage.
///
///     Layout, which is the part that matters to whatever reads it:
///
///         bronze/{dataset}/dt=YYYY-MM-DD/{utc-timestamp}-{guid}.jsonl
///
///     The dt= prefix is Hive-style partitioning. Fabric, Databricks, Synapse and Spark all
///     recognise it and will prune by date instead of listing the whole dataset - the difference
///     between reading one day and reading every event ever captured.
///
///     Newline-delimited JSON rather than Parquet: bronze is written once and read rarely, the
///     payloads are already JSON, and every engine reads JSONL natively. Parquet would be the
///     better choice for silver, where data is read repeatedly and column pruning pays for itself -
///     but that conversion belongs to the transformation engine, not to this application.
/// </summary>
public class AzureEventLakeWriter : IEventLakeWriter
{
    private readonly string _connectionString;
    private readonly string _containerName;
    private readonly ILogger<AzureEventLakeWriter> _logger;

    public AzureEventLakeWriter(AzureConfig config, ILogger<AzureEventLakeWriter> logger)
    {
        _logger = logger;
        _containerName = config.LakeBronzeContainerName;

        //fall back to the media account, so one storage account needs no second setting
        _connectionString = string.IsNullOrEmpty(config.LakeStorageConnectionString)
            ? config.AzureBlobStorageConnectionString
            : config.LakeStorageConnectionString;
    }

    public bool Enabled =>
        !string.IsNullOrEmpty(_containerName) && !string.IsNullOrEmpty(_connectionString);

    public async Task AppendBatch(string dataset, DateTime eventDate, IReadOnlyCollection<string> jsonLines)
    {
        if (!Enabled || jsonLines is not { Count: > 0 }) return;

        ArgumentException.ThrowIfNullOrEmpty(dataset);

        //Unique per write. Blob storage has no append-to-object semantics for block blobs, and a
        //deterministic name would make a retry either overwrite a good batch or fail outright -
        //so every batch is its own immutable object.
        var blobName =
            $"{dataset}/dt={eventDate:yyyy-MM-dd}/{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.jsonl";

        var payload = string.Join('\n', jsonLines) + "\n";

        try
        {
            var container = new BlobContainerClient(_connectionString, _containerName);
            var blob = container.GetBlobClient(blobName);

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            await blob.UploadAsync(stream, new BlobUploadOptions {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/x-ndjson" }
            });

            _logger.LogInformation("Wrote {Count} events to bronze/{BlobName}", jsonLines.Count, blobName);
        }
        catch (Exception ex)
        {
            //Surfaced to the caller on purpose. The relay tracks lake publication separately from
            //applying an event to the shipment, so a lake outage must leave the batch unpublished
            //and retryable - never silently dropped, and never blocking the delivery update.
            _logger.LogError(ex, "Failed writing {Count} events to bronze/{BlobName}",
                jsonLines.Count, blobName);
            throw;
        }
    }
}
