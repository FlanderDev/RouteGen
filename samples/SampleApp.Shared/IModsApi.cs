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

    // [Form]/[File] demonstrate multipart/form-data: ordinary form fields alongside a single
    // required file. Not combinable with [Body] on the same method (a request has one content
    // type) -- see RG0009.
    [Post("upload-with-screenshot")]
    [Authorize]
    Task<ModDto> UploadWithScreenshot(
        [Form] string name,
        [Form] FileInfo fileInfo,
        [File] FormFile formFile,
        CancellationToken ct = default);

    // [File] on an IReadOnlyList<FormFile>? parameter is the multi-file form: several files
    // under the same field name, here optional (the mod can be uploaded without a gallery).
    [Post("upload-with-gallery")]
    [Authorize]
    Task<ModDto> UploadWithGallery(
        [Form] List<string> names,
        [Form] List<FileInfo> fileInfos,
        [File] List<FormFile> formFiles,
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

public sealed record ModDto(int Id, string Name, string Author, int Downloads);

public sealed record ModListResult(IReadOnlyList<ModDto> Items, int TotalCount);

public sealed record ModUploadDto(string Name, string Description);
