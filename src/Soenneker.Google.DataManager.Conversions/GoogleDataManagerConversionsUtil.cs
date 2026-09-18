using Soenneker.Google.DataManager.Conversions.Abstract;
using Soenneker.Google.DataManager.Client.Abstract;
using Google.Apis.DataManager.v1;
using Google.Apis.DataManager.v1.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Google.DataManager.Conversions;

public sealed class GoogleDataManagerConversionsUtil : IGoogleDataManagerConversionsUtil
{
    private readonly IGoogleDataManagerClientUtil _client;
    private const int _maxEvents = 2000;

    public GoogleDataManagerConversionsUtil(IGoogleDataManagerClientUtil client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public Event CreateEvent(SaleConversion sale)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentException.ThrowIfNullOrWhiteSpace(sale.TransactionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sale.Currency);
        if (sale.Timestamp == default)
            throw new ArgumentException("A conversion timestamp is required.", nameof(sale));
        if (sale.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(sale), "Sale value cannot be negative.");
        string currency = sale.Currency.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(currency, "^[A-Z]{3}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Currency must be a three-letter ISO 4217 code.", nameof(sale));
        if (sale.EventSource is not ("WEB" or "APP" or "IN_STORE" or "PHONE" or "MESSAGE" or "OTHER"))
            throw new ArgumentException("Specify the actual conversion event source.", nameof(sale));

        var identifiers = new List<UserIdentifier>(2);
        if (sale.Email != null)
            identifiers.Add(new UserIdentifier { EmailAddress = HashEmail(sale.Email) });
        if (sale.PhoneNumber != null)
            identifiers.Add(new UserIdentifier { PhoneNumber = HashPhone(sale.PhoneNumber) });
        string? gclid = Clean(sale.Gclid), gbraid = Clean(sale.Gbraid), wbraid = Clean(sale.Wbraid);
        bool hasClick = gclid != null || gbraid != null || wbraid != null;
        if (!hasClick && identifiers.Count == 0)
            throw new ArgumentException("At least one click identifier, email, or phone number is required.",
                nameof(sale));

        return new Event
        {
            TransactionId = sale.TransactionId,
            EventTimestampDateTimeOffset = sale.Timestamp.ToUniversalTime(),
            ConversionValue = (double)sale.Value,
            Currency = currency,
            EventSource = sale.EventSource,
            AdIdentifiers = hasClick ? new AdIdentifiers { Gclid = gclid, Gbraid = gbraid, Wbraid = wbraid } : null,
            UserData = identifiers.Count > 0 ? new UserData { UserIdentifiers = identifiers } : null,
            Consent = sale.Consent
        };
    }

    public async Task<IngestEventsResponse> Upload(string fileName, IngestEventsRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        if (request.Events.Count is < 1 or > _maxEvents)
            throw new ArgumentException("A request must contain 1–2,000 events. Use UploadBatches for larger uploads.",
                nameof(request));
        DataManagerService client = await _client.Get(fileName, cancellationToken).ConfigureAwait(false);
        return await client.Events.Ingest(request).ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ConversionUploadBatch> UploadBatches(string fileName, IngestEventsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        // Snapshot list membership so input indices stay stable between awaits.
        Event[] events = request.Events.ToArray();
        Destination[] destinations = request.Destinations.ToArray();
        for (int start = 0; start < events.Length; start += _maxEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(_maxEvents, events.Length - start);
            var batch = new IngestEventsRequest
            {
                Destinations = destinations,
                Events = events.AsSpan(start, count).ToArray(),
                Consent = request.Consent,
                Encoding = request.Encoding,
                EncryptionInfo = request.EncryptionInfo,
                ValidateOnly = request.ValidateOnly
            };
            IngestEventsResponse response = await Upload(fileName, batch, cancellationToken).ConfigureAwait(false);
            yield return new ConversionUploadBatch(start, count, response);
        }
    }

    public async Task<RetrieveRequestStatusResponse> GetStatus(string fileName, string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        cancellationToken.ThrowIfCancellationRequested();
        DataManagerService client = await _client.Get(fileName, cancellationToken).ConfigureAwait(false);
        RequestStatusResource.RetrieveRequest request = client.RequestStatus.Retrieve();
        request.RequestId = requestId;
        return await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(IngestEventsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Destinations == null || request.Destinations.Count == 0 || request.Destinations.Any(d => d == null))
            throw new ArgumentException("At least one non-null destination is required.", nameof(request));
        if (request.Events == null || request.Events.Any(e => e == null))
            throw new ArgumentException("Events must be a non-null list with no null entries.", nameof(request));
        if (request.Events.Any(e => e.UserData != null || e.ThirdPartyUserData != null) &&
            request.Encoding is not ("HEX" or "BASE64"))
            throw new ArgumentException("User data requires explicit HEX or BASE64 encoding.", nameof(request));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string HashEmail(string value)
    {
        string email = string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        int at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@') || at == email.Length - 1)
            throw new ArgumentException("Email must be a raw email address.", nameof(value));
        string local = email[..at];
        string domain = email[(at + 1)..];
        if (domain is "gmail.com" or "googlemail.com")
        {
            int plus = local.IndexOf('+');
            if (plus >= 0)
                local = local[..plus];
            local = local.Replace(".", "", StringComparison.Ordinal);
        }

        if (local.Length == 0)
            throw new ArgumentException("Email local part is empty after normalization.", nameof(value));
        return Hash(local + "@" + domain);
    }

    private static string HashPhone(string value)
    {
        string phone = value.Trim();
        if (!Regex.IsMatch(phone, @"^\+[1-9][0-9]{1,14}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Phone number must use E.164 format, including + and country code.",
                nameof(value));
        return Hash(phone);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}