using System.Net;

namespace FlanderDev.RouteGen.Abstractions;

/// <summary>
/// Thrown by generated client implementations when an API call returns a non-success status
/// code, instead of letting a bare <see cref="System.Net.Http.HttpRequestException"/> escape
/// from <c>EnsureSuccessStatusCode()</c>. Carries the status code and the raw response body so
/// callers can inspect a structured, package-defined exception.
/// </summary>
public sealed class ApiException : Exception
{
    /// <summary>The HTTP status code returned by the server.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>The raw response body, if any was returned.</summary>
    public string? ResponseBody { get; }

    /// <summary>Creates an <see cref="ApiException"/> for a failed call with no inner exception.</summary>
    /// <param name="statusCode">The response status code.</param>
    /// <param name="responseBody">The raw response body, if any.</param>
    public ApiException(HttpStatusCode statusCode, string? responseBody)
        : base($"API call failed with status {(int)statusCode} ({statusCode}).")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <inheritdoc cref="ApiException(HttpStatusCode, string?)"/>
    /// <param name="statusCode">The response status code.</param>
    /// <param name="responseBody">The raw response body, if any.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public ApiException(HttpStatusCode statusCode, string? responseBody, Exception innerException)
        : base($"API call failed with status {(int)statusCode} ({statusCode}).", innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
