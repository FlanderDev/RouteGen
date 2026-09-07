using System.IO;

namespace FlanderDev.RouteGen.Abstractions;

/// <summary>
/// The value type for a <see cref="FileAttribute"/>-marked client-side parameter: the file's
/// bytes plus the filename and (optional) content type multipart needs to describe the part
/// correctly. A bare <see cref="Stream"/> was considered and rejected as the parameter type for
/// <see cref="FileAttribute"/> precisely because it can't carry <see cref="FileName"/> --
/// multipart file parts are not meaningfully expressible without one.
/// Not <see langword="sealed"/> so <see cref="FormFile{TMetadata}"/> can extend it -- use this
/// non-generic form when there's no extra data to attach to the upload.
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
public record FormFile(Stream Content, string FileName, string? ContentType = null);

/// <summary>
/// <see cref="FormFile"/> plus an arbitrary <typeparamref name="TMetadata"/> value attached to
/// the upload. <see cref="Metadata"/> is a genuine runtime value (unlike an attribute argument,
/// which must be a compile-time constant) but is purely local to the client call: RouteGen never
/// serializes it or sends it to the server -- <c>[File]</c> binds server-side to <c>IFormFile</c>
/// regardless of whether the client declared <see cref="FormFile"/> or <see cref="FormFile{TMetadata}"/>,
/// so nothing about the wire contract or the generated controller changes based on
/// <typeparamref name="TMetadata"/>. Use this when the caller needs to correlate the upload with
/// local state -- UI context, progress tracking, retry bookkeeping -- and plain
/// <see cref="FormFile"/> otherwise. If the data genuinely needs to reach the server, send it as
/// an ordinary <c>[Form]</c> field (or several) alongside the file instead.
/// </summary>
/// <typeparam name="TMetadata">The type of the attached local data. Can be any type; RouteGen places no constraints on it.</typeparam>
/// <param name="Content"><inheritdoc cref="FormFile(Stream, string, string?)" path="/param[@name='Content']"/></param>
/// <param name="FileName"><inheritdoc cref="FormFile(Stream, string, string?)" path="/param[@name='FileName']"/></param>
/// <param name="Metadata">The attached local data.</param>
/// <param name="ContentType"><inheritdoc cref="FormFile(Stream, string, string?)" path="/param[@name='ContentType']"/></param>
public sealed record FormFile<TMetadata>(Stream Content, string FileName, TMetadata Metadata, string? ContentType = null)
    : FormFile(Content, FileName, ContentType);
