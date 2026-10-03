# FlanderDev.RouteGen.Abstractions

Attributes and runtime types for [RouteGen](https://github.com/FlanderDev/RouteGen).

This package contains everything your shared project (and application code) needs to reference:

- `[ApiRoute]`, `[Get]`, `[Post]`, `[Put]`, `[Delete]`, `[Patch]`
- `[Query]`, `[Body]`, `[Form]`, `[File]`, `[Route]`
- `[GenAuthorize]`, `[GenAllowAnonymous]` (prefixed to avoid colliding with ASP.NET Core's own `[Authorize]`/`[AllowAnonymous]`)
- `[GeneratedPathName]` (names a Razor page's generated `Paths` member)
- `FormFile`, `FileWithData<TData>` (client-side file/paired-data types for `[File]`)
- `ApiException` (thrown by the generated client on non-success responses)

## Quick start

```bash
dotnet add package FlanderDev.RouteGen.Abstractions
```

Then define your API contract once:

```csharp
[ApiRoute("api/mods", HttpClientName = "App")]
public partial interface IModsApi
{
    [Get]
    Task<ModListResult> GetMods([Query] int page = 1, [Query] string? search = null);

    [Get("{id:int}")]
    Task<ModDto> GetMod(int id);

    [Post("upload")]
    [GenAuthorize]
    Task<ModDto> Upload([Body] ModUploadDto dto);
}
```

The matching generator package (`FlanderDev.RouteGen.Generators`) turns this into a controller base + strongly typed `HttpClient` implementation.

## License

MIT
