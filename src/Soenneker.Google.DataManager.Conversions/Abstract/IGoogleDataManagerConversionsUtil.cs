using Google.Apis.DataManager.v1.Data;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Google.DataManager.Conversions.Abstract;

/// <summary>
/// Uploads sales and offline conversion events through the Google Data Manager API.
/// Upload and status methods take a service-account credential filename relative to LocalResources.
/// </summary>
public interface IGoogleDataManagerConversionsUtil
{
    /// <summary>Maps a sale to a Google Ads event, normalizing and SHA-256 hex hashing raw email and phone identifiers.
    /// Consent is supplied by the caller and is never inferred. Use HEX encoding when uploading the returned event.</summary>
    Event CreateEvent(SaleConversion sale);

    /// <summary>Uploads one request containing 1–2,000 events. Google API errors propagate unchanged.
    /// A returned request ID acknowledges ingestion, not attribution or processing success. No automatic retry is performed by this wrapper.</summary>
    Task<IngestEventsResponse> Upload(string fileName, IngestEventsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Uploads a request as sequential batches of at most 2,000 events. Each yielded result identifies its input range.
    /// Persist each result before requesting the next. Errors or cancellation stop enumeration; earlier batches may already be accepted.
    /// Re-enumeration resends data. Do not modify the request during enumeration. Empty event lists yield no results.</summary>
    IAsyncEnumerable<ConversionUploadBatch> UploadBatches(string fileName, IngestEventsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Retrieves asynchronous processing status, including per-destination errors and warnings.
    /// Only successful uploads with validateOnly=false have diagnostics. This method does not poll or interpret status as attribution.</summary>
    Task<RetrieveRequestStatusResponse> GetStatus(string fileName, string requestId, CancellationToken cancellationToken = default);
}


