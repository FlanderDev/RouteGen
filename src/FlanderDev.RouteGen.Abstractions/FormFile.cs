using System.IO;

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

/// <summary>
/// Pairs a <see cref="File"/> with an arbitrary <typeparamref name="TData"/> value that
/// genuinely travels to the server alongside it -- the replacement for the old
/// <c>FormFile&lt;TMetadata&gt;</c>, which looked like it did this but didn't: that type's
/// attached value was local-only and never left the client. This one's <see cref="Data"/> is
/// JSON-serialized into its own multipart field, correlated with the file by field name (not by
/// list position), and reconstructed server-side into the generated controller base's own nested
/// <c>FileWithData&lt;TData&gt;</c> record via a generated model binder. Use this for exactly the
/// case <c>FormFile&lt;TMetadata&gt;</c> couldn't handle well: a caption, an album name, a sort
/// index -- any per-file data that needs to reach the controller, especially once there's more
/// than one file and a second parallel list would otherwise be the only alternative.
/// </summary>
/// <typeparam name="TData">The type of the attached data. Can be any type; it's JSON-serialized on the wire.</typeparam>
/// <param name="File">The file itself.</param>
/// <param name="Data">The data to send alongside it.</param>
public sealed record FileWithData<TData>(FormFile File, TData Data);
