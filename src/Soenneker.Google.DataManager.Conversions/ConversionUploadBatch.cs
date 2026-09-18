using Google.Apis.DataManager.v1.Data;

namespace Soenneker.Google.DataManager.Conversions;

/// <summary>The response for one uploaded input range. Validation-only responses may have no request ID.</summary>
/// <param name="StartIndex">Zero-based index of the first event in the original request.</param>
/// <param name="Count">Number of events submitted.</param>
/// <param name="Response">Google's acknowledgement, not final processing status.</param>
public sealed record ConversionUploadBatch(int StartIndex, int Count, IngestEventsResponse Response);
