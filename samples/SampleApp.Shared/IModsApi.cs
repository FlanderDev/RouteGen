using FlanderDev.RouteGen.Abstractions;
using System.ComponentModel.DataAnnotations;

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

    // For a single file, attaching extra data alongside it is simple: just add more [Form]
    // fields next to the [File] parameter, no need for FileWithData<TData> here at all.
    [Post("upload-with-screenshot")]
    [Authorize]
    Task<ModDto> UploadWithScreenshot(
        [Form] string name,
        [Form] string description,
        [File] FormFile screenshot,
        CancellationToken ct = default);

    // [Form] on a complex type (GalleryUploadMetadata here, not just primitives) is JSON-serialized
    // into its own field, this is the case [Form]-any-type exists for: a whole metadata object
    // travels alongside the files in one call, the same way it would as a single [Body] parameter,
    // except [Body] can't be combined with [File] on the same method (RG0009).
    [Post("upload-with-gallery")]
    [Authorize]
    Task<ModDto> UploadWithGallery(
        [Form] GalleryUploadMetadata metadata,
        [File] IReadOnlyList<FileWithData<PhotoCaption>>? gallery,
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

// The per-file data paired with each gallery photo via FileWithData<TData>, genuinely sent to
// and readable by the server, unlike the old FormFile<TMetadata>'s client-local-only Metadata.
public sealed record PhotoCaption(string Caption, int SortOrder);

// A complex [Form] parameter: sent as one JSON-serialized field, reconstructed server-side by a
// generated model binder. [Required]/other DataAnnotations on it still run as normal, since
// ASP.NET Core validates ModelState after binding regardless of which binder produced the value.
public sealed record GalleryUploadMetadata(
    [property: Required, MaxLength(120)] string Name,
    [property: MaxLength(2000)] string Description);

public sealed record ModDto(int Id, string Name, string Author, int Downloads);

public sealed record ModListResult(IReadOnlyList<ModDto> Items, int TotalCount);

public sealed record ModUploadDto(string Name, string Description);
