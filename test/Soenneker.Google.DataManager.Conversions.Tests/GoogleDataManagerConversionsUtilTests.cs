using Google;
using Google.Apis.DataManager.v1;
using Google.Apis.DataManager.v1.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Soenneker.Google.DataManager.Client.Abstract;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Google.DataManager.Conversions.Tests;

public sealed class GoogleDataManagerConversionsUtilTests
{
    [Test]
    public void Sale_mapping_hashes_identifiers_and_preserves_value_timestamp_and_consent()
    {
        using var transport = new Transport();
        var util = new GoogleDataManagerConversionsUtil(transport);
        var consent = new Consent { AdUserData = "CONSENT_DENIED" };
        Event result = util.CreateEvent(Sale(" Cloudy.SanFrancisco+shopping@Gmail.com ", " +18005550100 ", consent));
        Check(result.UserData.UserIdentifiers[0].EmailAddress == Hash("cloudysanfrancisco@gmail.com"), "Email normalization differs from Google requirements.");
        Check(result.UserData.UserIdentifiers[1].PhoneNumber == Hash("+18005550100"), "Phone hash is incorrect.");
        Check(result.ConversionValue == 129.95 && result.Currency == "USD", "Sale value or currency changed.");
        Check(result.EventTimestampDateTimeOffset == new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero), "Timestamp offset was lost.");
        Check(ReferenceEquals(result.Consent, consent) && result.TransactionId == "order-1", "Consent or transaction ID changed.");
        Check(result.AdIdentifiers.Gclid == "click-1", "Click identifier changed.");
        Event custom = util.CreateEvent(Sale("User.Name+tag@Example.com", null, null));
        Check(custom.UserData.UserIdentifiers[0].EmailAddress == Hash("user.name+tag@example.com"), "Non-Gmail dots/plus were removed.");
        Check(custom.Consent == null, "Consent was invented.");
        Event clickOnly = util.CreateEvent(Sale(null, null, null));
        Check(clickOnly.UserData == null, "Click-only event included empty user data.");
        Throws<ArgumentException>(() => util.CreateEvent(Sale("a@example.com", "800-555-0100", null)));
    }

    [Test]
    public async Task Batches_use_real_sdk_serialization_and_preserve_request_options()
    {
        using var transport = new Transport();
        var util = new GoogleDataManagerConversionsUtil(transport);
        IngestEventsRequest request = Request(2001);
        request.Consent = new Consent { AdUserData = "CONSENT_GRANTED" };
        request.Encoding = "HEX";
        request.ValidateOnly = true;
        request.Events[0] = util.CreateEvent(Sale("customer@example.com", null, null));
        var batches = new List<ConversionUploadBatch>();
        await foreach (ConversionUploadBatch batch in util.UploadBatches("sales.json", request)) batches.Add(batch);
        Check(batches.Count == 2 && batches[0].Count == 2000 && batches[1].StartIndex == 2000 && batches[1].Count == 1, "Batch boundaries are incorrect.");
        Check(request.Events.Count == 2001, "Input list was changed.");
        Check(batches[0].Response.RequestId == "request-1" && batches[1].Response.RequestId == "request-2", "Request IDs were lost.");
        for (int index = 0; index < 2; index++)
        {
            using JsonDocument json = JsonDocument.Parse(transport.Bodies[index]);
            JsonElement root = json.RootElement;
            Check(root.GetProperty("events").GetArrayLength() == batches[index].Count, "Serialized count is incorrect.");
            Check(root.GetProperty("validateOnly").GetBoolean() && root.GetProperty("encoding").GetString() == "HEX", "Request options were lost.");
            Check(root.GetProperty("consent").GetProperty("adUserData").GetString() == "CONSENT_GRANTED", "Request consent was lost.");
            Check(root.GetProperty("destinations")[0].GetProperty("productDestinationId").GetString() == "456", "Destination was lost.");
            Check(transport.Uris[index].AbsolutePath == "/v1/events:ingest", "Incorrect endpoint.");
        }
        Check(!transport.Bodies[0].Contains("customer@example.com", StringComparison.Ordinal), "Raw email was sent.");
        using JsonDocument first = JsonDocument.Parse(transport.Bodies[0]);
        Check(first.RootElement.GetProperty("events")[0].GetProperty("conversionValue").GetDouble() == 129.95, "Value serialized incorrectly.");
    }

    [Test]
    public async Task Later_failure_preserves_first_batch_and_does_not_retry()
    {
        using var transport = new Transport { FailOn = 2 };
        var util = new GoogleDataManagerConversionsUtil(transport);
        var results = new List<ConversionUploadBatch>();
        try
        {
            await foreach (ConversionUploadBatch batch in util.UploadBatches("sales.json", Request(4001))) results.Add(batch);
            throw new Exception("Expected API failure.");
        }
        catch (GoogleApiException exception)
        {
            Check(exception.HttpStatusCode == HttpStatusCode.BadRequest, "HTTP error was lost.");
            Check(results.Count == 1 && results[0].Response.RequestId == "request-1", "Accepted batch was lost.");
            Check(transport.Bodies.Count == 2, "Failed batch was retried or later batch submitted.");
        }
    }

    [Test]
    public async Task Validation_and_cancellation_prevent_network_calls()
    {
        using var transport = new Transport();
        var util = new GoogleDataManagerConversionsUtil(transport);
        await ThrowsAsync<ArgumentException>(() => util.Upload("sales.json", Request(0)));
        await ThrowsAsync<ArgumentException>(() => util.Upload("sales.json", Request(2001)));
        IngestEventsRequest missingEncoding = Request(1);
        missingEncoding.Events[0].UserData = new UserData();
        await ThrowsAsync<ArgumentException>(() => util.Upload("sales.json", missingEncoding));
        await ThrowsAsync<OperationCanceledException>(() => util.Upload("sales.json", Request(1), new CancellationToken(true)));
        await foreach (ConversionUploadBatch _ in util.UploadBatches("sales.json", Request(0))) throw new Exception("Empty upload yielded a batch.");
        Check(transport.Bodies.Count == 0, "Invalid/cancelled input reached the network.");
    }

    [Test]
    public async Task Cancellation_between_batches_stops_later_uploads()
    {
        using var transport = new Transport();
        using var cancellation = new CancellationTokenSource();
        var util = new GoogleDataManagerConversionsUtil(transport);
        try
        {
            await foreach (ConversionUploadBatch _ in util.UploadBatches("sales.json", Request(2001), cancellation.Token)) cancellation.Cancel();
            throw new Exception("Expected cancellation.");
        }
        catch (OperationCanceledException) { Check(transport.Bodies.Count == 1, "Later batch was sent."); }
    }

    [Test]
    public async Task Diagnostics_preserve_partial_success_and_error_details()
    {
        using var transport = new Transport();
        var util = new GoogleDataManagerConversionsUtil(transport);
        RetrieveRequestStatusResponse response = await util.GetStatus("sales.json", "request-1");
        Check(transport.Uris[0].AbsolutePath == "/v1/requestStatus:retrieve" && transport.Uris[0].Query.Contains("requestId=request-1"), "Diagnostics request is incorrect.");
        var status = response.RequestStatusPerDestination[0];
        Check(status.RequestStatus == "PARTIAL_SUCCESS", "Partial failure was hidden.");
        Check(status.ErrorInfo.ErrorCounts[0].Reason == "PROCESSING_ERROR_REASON_INVALID_EVENT", "Processing error details were lost.");
    }

    private static SaleConversion Sale(string? email, string? phone, Consent? consent) => new()
    {
        TransactionId = "order-1", Timestamp = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.FromHours(-5)),
        Value = 129.95m, Currency = "usd", EventSource = "WEB", Gclid = "click-1", Email = email, PhoneNumber = phone, Consent = consent
    };

    private static IngestEventsRequest Request(int count) => new()
    {
        Destinations = new[] { new Destination { OperatingAccount = new ProductAccount { AccountType = "GOOGLE_ADS", AccountId = "123" }, ProductDestinationId = "456" } },
        Events = Enumerable.Range(0, count).Select(i => new Event { TransactionId = i.ToString(), AdIdentifiers = new AdIdentifiers { Gclid = "click" } }).ToArray()
    };

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class Transport : HttpMessageHandler, IGoogleDataManagerClientUtil, global::Google.Apis.Http.IHttpClientFactory
    {
        private readonly DataManagerService _service;
        public List<string> Bodies { get; } = new();
        public List<Uri> Uris { get; } = new();
        public int FailOn { get; init; }
        public Transport() => _service = new DataManagerService(new BaseClientService.Initializer { HttpClientFactory = this, ApplicationName = "OfflineTests" });
        public ValueTask<DataManagerService> Get(string fileName, CancellationToken cancellationToken = default) { Check(fileName == "sales.json", "Credential filename was lost."); return ValueTask.FromResult(_service); }
        public ValueTask<bool> Remove(string fileName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void RemoveSync(string fileName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) => new(new ConfigurableMessageHandler(this), false);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        protected override void Dispose(bool disposing) { if (disposing) _service.Dispose(); base.Dispose(disposing); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uris.Add(request.RequestUri!);
            string requestBody = "";
            if (request.Content != null)
            {
                if (request.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    using var compressed = new MemoryStream(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                    using var decompressed = new GZipStream(compressed, CompressionMode.Decompress);
                    using var reader = new StreamReader(decompressed);
                    requestBody = await reader.ReadToEndAsync(cancellationToken);
                }
                else requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            Bodies.Add(requestBody);
            bool fail = Bodies.Count == FailOn;
            string body = fail ? "{\"error\":{\"code\":400,\"message\":\"Invalid event\",\"status\":\"INVALID_ARGUMENT\"}}" :
                request.Method == HttpMethod.Get ? "{\"requestStatusPerDestination\":[{\"requestStatus\":\"PARTIAL_SUCCESS\",\"errorInfo\":{\"errorCounts\":[{\"reason\":\"PROCESSING_ERROR_REASON_INVALID_EVENT\",\"recordCount\":\"1\"}]}}]}" :
                "{\"requestId\":\"request-" + Bodies.Count + "\"}";
            return new HttpResponseMessage(fail ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}


