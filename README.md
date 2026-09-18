# Soenneker.Google.DataManager.Conversions

Uploads sales and closed-lead conversions through the Google Data Manager API using Google's official .NET models. Includes sale-to-event mapping, SHA-256 identifier hashing, sequential batching, validation-only uploads, and asynchronous processing diagnostics.

Targets .NET 10. Depends on `Soenneker.Google.DataManager.Client`.

## Register

Registration includes the Data Manager client and shared Google service-account credential utility.

```csharp
using Soenneker.Google.DataManager.Client.Registrars;
using Soenneker.Google.DataManager.Conversions.Registrars;

services.AddGoogleDataManagerConversionsUtilAsSingleton();
```

The singleton registrar uses singleton client and credential caches; the scoped registrar registers all three per scope. Pass the service-account JSON filename relative to LocalResources on each upload or status request. Different credential files use separate cached clients. See the client README for credential setup and ownership.

## Upload a sale

```csharp
using Google.Apis.DataManager.v1.Data;
using Soenneker.Google.DataManager.Conversions;
using Soenneker.Google.DataManager.Conversions.Abstract;

public sealed class SaleReporter(IGoogleDataManagerConversionsUtil conversions)
{
    public async Task<string?> Report(
        string transactionId, DateTimeOffset closedAt, decimal revenue,
        string gclid, Consent? actualCustomerConsent, CancellationToken cancellationToken)
    {
        Event sale = conversions.CreateEvent(new SaleConversion
        {
            TransactionId = transactionId,
            Timestamp = closedAt,
            Value = revenue,
            Currency = "USD",
            EventSource = "WEB", // Use the actual conversion source.
            Gclid = gclid,
            Consent = actualCustomerConsent
            // Optional raw identifiers: Email, PhoneNumber (E.164).
        });

        var request = new IngestEventsRequest
        {
            Destinations = new[]
            {
                new Destination
                {
                    OperatingAccount = new ProductAccount
                    {
                        AccountType = "GOOGLE_ADS",
                        AccountId = "1234567890" // Account that owns the conversion action.
                    },
                    ProductDestinationId = "987654321" // Conversion action ID, not resource path.
                    // Optional LoginAccount: manager account used for access.
                }
            },
            Events = new[] { sale },
            Encoding = "HEX",
            ValidateOnly = false
        };

        IngestEventsResponse response = await conversions.Upload("google-sales.json", request, cancellationToken);
        return response.RequestId;
    }
}
```

Create/configure the conversion action separately. Google Ads offline conversions and enhanced conversions for leads require the appropriate upload conversion action and account setup. Supply actual consent at event or request level; the library never grants consent implicitly. Event-level consent overrides request-level consent.

`CreateEvent` requires a stable transaction ID, non-default timestamp, non-negative monetary value, three-letter currency code, actual event source, and at least one click identifier or customer identifier. It preserves GCLID/GBRAID/WBRAID and converts the timestamp to UTC. The decimal value is mapped to the API's double field in currency units, not micros.

Email is lowercased and stripped of whitespace. Gmail/Googlemail local parts also lose dots and plus suffixes. Phone numbers must already use E.164 format with a country code. Email/phone values are SHA-256 hashed and hex encoded. Do not pass pre-hashed values to `CreateEvent`; use SDK `Event` models directly for already prepared or advanced data. SDK events are sent unchanged; callers are responsible for valid fields, hashes, matching encoding, and consent. No customer data is logged by this library.

## Batches and diagnostics

```csharp
await foreach (ConversionUploadBatch batch in conversions.UploadBatches("google-sales.json", request, cancellationToken))
{
    // Persist this response and its input range before advancing enumeration.
    await SaveAcknowledgement(batch.StartIndex, batch.Count, batch.Response.RequestId);
}

RetrieveRequestStatusResponse diagnostics =
    await conversions.GetStatus("google-sales.json", savedRequestId, cancellationToken);

foreach (RequestStatusPerDestination destination in diagnostics.RequestStatusPerDestination)
{
    // Inspect RequestStatus, ErrorInfo, WarningInfo, and EventsIngestionStatus.
}
```

`Upload` accepts 1–2,000 events. `UploadBatches` splits larger requests into sequential batches of at most 2,000; empty event lists yield no batches. Every batch retains destinations, encoding, consent, encryption information, and validation mode. Do not mutate request objects during enumeration. Enumerating again resends data.

The wrapper propagates `GoogleApiException` and cancellation without automatically replaying requests. If a later batch fails, earlier batches may already be accepted: retain yielded request IDs/input ranges and stable transaction IDs. An ambiguous transport failure may occur after Google accepts a request, so do not blindly replay the whole dataset.

An upload response acknowledges receipt, not conversion attribution or successful processing. Check diagnostics later for `PROCESSING`, `SUCCESS`, `PARTIAL_SUCCESS`, or `FAILED` and inspect errors/warnings. `GetStatus` performs one request; callers control polling. `ValidateOnly=true` validates without uploading and may return no request ID; diagnostics are only available for successful non-validation uploads.

## Build before the client package is published

When both repositories are cloned next to each other, builds automatically reference the sibling client project so changes to its API are immediately available:

```powershell
dotnet build
dotnet test --project test/Soenneker.Google.DataManager.Conversions.Tests -- --treenode-filter "/*/*/GoogleDataManagerConversionsUtilTests/*"
dotnet pack src/Soenneker.Google.DataManager.Conversions
```

Without the sibling project, builds use the NuGet dependency. Set `-p:UseLocalDataManagerClient=false` to explicitly test the packaged dependency. Publish a client package containing the filename-based API before building/publishing conversions in isolation. Packing with the project reference still emits the client as a NuGet dependency; it does not bundle the client assembly.

Tests use a fake HTTP transport through the real Google SDK and never upload conversion data.

- [Send events and configure conversion destinations](https://developers.google.com/data-manager/api/devguides/events/send-events)
- [User-data normalization and hashing](https://developers.google.com/data-manager/api/devguides/concepts/formatting)
- [Upload request reference](https://developers.google.com/data-manager/api/reference/rest/v1/events/ingest)
- [Processing diagnostics](https://developers.google.com/data-manager/api/reference/rest/v1/requestStatus/retrieve)


