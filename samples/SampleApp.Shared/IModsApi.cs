using FlanderDev.RouteGen.Abstractions;

namespace SampleApp.Shared;

// This is the ONLY hand-written piece of the API surface. Everything else — the server's
// abstract controller base and the client's HttpClient implementation — is generated from it.
[ApiRoute("api/mods", HttpClientName = "App")]
public partial interface IModsApi
{
    [Get]
    Task<ModListResult> GetMods(
        [Query] int page = 1,
        [Query] int pageSize = 18,
        [Query] string? search = null,
        [Query] SortBy sort = SortBy.Newest);

    [Get("{id:int}")]
    Task<ModDto> GetMod(int id);

    [Post("upload")]
    [Authorize]
    Task<ModDto> Upload([Body] ModUploadDto dto);

    // [File] can be FormFile<TMetadata> instead of plain FormFile when the caller needs to
    // attach local data to the upload -- UploadClientContext here never reaches the server (see
    // FormFile<TMetadata>'s doc comment); it's purely for the caller's own bookkeeping around
    // the call (e.g. correlating this upload with UI state or a retry attempt).
    [Post("upload-with-screenshot")]
    [Authorize]
    Task<ModDto> UploadWithScreenshot(
        [Form] string name,
        [Form] string description,
        [File] FormFile<UploadClientContext> screenshot,
        CancellationToken ct = default);

    // [File] on an IReadOnlyList<FormFile>? parameter is the multi-file form: several files
    // under the same field name, here optional (the mod can be uploaded without a gallery).
    // [Form] tags is a complex type (not a primitive/string/enum/etc.) -- [Form] places no type
    // restriction, so this is JSON-serialized into its own field rather than sent as plain text.
    [Post("upload-with-gallery")]
    [Authorize]
    Task<ModDto> UploadWithGallery(
        [Form] string name,
        [Form] string description,
        [Form] ModTags tags,
        [File] IReadOnlyList<FormFile>? gallery,
        CancellationToken ct = default);

    [Delete("{id:int}")]
    [Authorize(Roles = "Admin")]
    Task Delete(int id, CancellationToken ct = default);
}

public enum SortBy
{
    Popular = 0,
    Newest = 1,
}

// Purely local to the client -- attached to a FormFile<TMetadata> upload but never sent to the
// server. Any type works here; this one just happens to be a record for convenience.
public sealed record UploadClientContext(string CorrelationId, int AttemptNumber);

public sealed record ModDto(int Id, string Name, string Author, int Downloads);

// A complex [Form] parameter: sent/bound as one JSON-serialized field rather than plain text,
// since there's no meaningful single-string form for a type like this.
public sealed record ModTags(string[] Categories, bool IsNsfw);

public sealed record ModListResult(IReadOnlyList<ModDto> Items, int TotalCount);

public sealed record ModUploadDto(string Name, string Description);
