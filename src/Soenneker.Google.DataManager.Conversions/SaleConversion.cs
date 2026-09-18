using Google.Apis.DataManager.v1.Data;
using System;

namespace Soenneker.Google.DataManager.Conversions;

/// <summary>A Google Ads sale or closed lead. Identifiers are raw values; email and phone are hashed during mapping.</summary>
public sealed class SaleConversion
{
    /// <summary>Stable sale/order identifier, reused when retrying the same conversion.</summary>
    public required string TransactionId { get; init; }
    /// <summary>When the conversion occurred, including its time-zone offset.</summary>
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>Non-negative sale value in units of Currency, not micros.</summary>
    public required decimal Value { get; init; }
    /// <summary>Three-letter ISO 4217 currency code.</summary>
    public required string Currency { get; init; }
    /// <summary>Actual conversion source: WEB, APP, IN_STORE, PHONE, MESSAGE, or OTHER.</summary>
    public required string EventSource { get; init; }
    /// <summary>Google click identifier, when available. Never hashed.</summary>
    public string? Gclid { get; init; }
    /// <summary>Google app attribution identifier, when available. Never hashed.</summary>
    public string? Gbraid { get; init; }
    /// <summary>Google web attribution identifier, when available. Never hashed.</summary>
    public string? Wbraid { get; init; }
    /// <summary>Raw customer email. Do not pass an already hashed value.</summary>
    public string? Email { get; init; }
    /// <summary>Raw E.164 phone number including + and country code. Do not pass an already hashed value.</summary>
    public string? PhoneNumber { get; init; }
    /// <summary>Optional event-level consent, overriding request-level consent. Null leaves consent unspecified.</summary>
    public Consent? Consent { get; init; }
}
