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

    // For a single file, attaching extra data alongside it is simple: just add more [Form]
    // fields next to the [File] parameter -- no need for FileWithData<TData> here at all.
    [Post("upload-with-screenshot")]
    [Authorize]
    Task<ModDto> UploadWithScreenshot(
        [Form] string name,
        [Form] FileInfo fileInfo,
        [File] FormFile formFile,
        CancellationToken ct = default);

    // [File] on an IReadOnlyList<FileWithData<TData>>? parameter is where FileWithData<TData>
    // actually earns its keep: several files, each with its OWN caption, correlated by field
    // name (not by list position) -- no parallel "files" + "captions" lists to keep in sync.
    [Post("upload-with-gallery")]
    [Authorize]
    Task<ModDto> UploadWithGallery(
        [Form] string name,
        [Form] string description,
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

// The per-file data paired with each gallery photo via FileWithData<TData> -- genuinely sent to
// and readable by the server, unlike the old FormFile<TMetadata>'s client-local-only Metadata.
public sealed record PhotoCaption(string Caption, int SortOrder);

public sealed record ModDto(int Id, string Name, string Author, int Downloads);

public sealed record ModListResult(IReadOnlyList<ModDto> Items, int TotalCount);

public sealed record ModUploadDto(string Name, string Description);
