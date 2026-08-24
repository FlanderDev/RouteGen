namespace FlanderDev.RouteGen.Abstractions;

/// <summary>
/// The value type for a <see cref="FileAttribute"/>-marked client-side parameter: the file's
/// bytes plus the filename and (optional) content type multipart needs to describe the part
/// correctly. A bare <see cref="Stream"/> was considered and rejected as the parameter type for
/// <see cref="FileAttribute"/> precisely because it can't carry <see cref="FileName"/> --
/// multipart file parts are not meaningfully expressible without one.
/// </summary>
/// <param name="Content">
/// The file's contents. The generated client wraps this directly in a <c>StreamContent</c> and
/// does not take ownership of it -- the caller is responsible for disposing it once the call
/// completes (e.g. via <c>using</c> around the call site), the same as with any other API that
/// accepts a caller-owned <see cref="Stream"/>.
/// </param>
/// <param name="FileName">The filename sent as part of the multipart <c>Content-Disposition</c> header.</param>
/// <param name="ContentType">
/// Optional MIME type sent as the part's <c>Content-Type</c> header. When omitted, no
/// <c>Content-Type</c> header is set on the part and the server infers/ignores it as usual.
/// </param>
public sealed record FormFile(Stream Content, string FileName, string? ContentType = null);
