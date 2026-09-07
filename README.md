# RouteGen

RouteGen is a Roslyn source generator for Blazor WebAssembly Hosted apps.

Define your API once as a C# interface in a shared project. RouteGen then generates:

- An abstract ASP.NET Core MVC controller base (server)
- A strongly typed `HttpClient` implementation (client)
- Strongly typed page-route helpers from `@page` directives

One source of truth for routes. No more duplicated URL strings that drift out of sync.

## Requirements

- .NET 10
- ASP.NET Core MVC + Blazor WebAssembly Hosted
- C# with nullable reference types

## Packages

```bash
dotnet add package FlanderDev.RouteGen.Abstractions
dotnet add package FlanderDev.RouteGen.Generators
```

`Abstractions` goes in the shared project. It's also needed directly in the client project, since the generated client implementation throws `Abstractions`' `ApiException`. `Generators` (the analyzer) goes in the server and client projects — wherever you want generated code to actually appear (mark it with `PrivateAssets="all"`, which `dotnet add package` does automatically for analyzer packages). The server's generated controller base doesn't use any `Abstractions` types itself, but referencing `Abstractions` from the server too doesn't hurt and keeps all three projects symmetric — that's what the sample does.

For page-route generation, add your `.razor` files as `AdditionalFiles` to whichever project you want the generated `Paths` class to live in — see [Page routes](#page-routes) below for the recommended pattern (put it in the shared project so both server and client see the same `Paths` type).

## Define the API once

```csharp
using FlanderDev.RouteGen.Abstractions;

[ApiRoute("api/mods", HttpClientName = "App")]
public partial interface IModsApi
{
    [Get]
    Task<ModListResult> GetMods(
        [Query] int page = 1,
        [Query] int pageSize = 18,
        [Query] string? search = null);

    [Get("{id:int}")]
    Task<ModDto> GetMod(int id);

    [Post("upload")]
    [Authorize]
    Task<ModDto> Upload([Body] ModUploadDto dto);

    [Delete("{id:int}")]
    [Authorize(Roles = "Admin")]
    Task Delete(int id, CancellationToken ct = default);
}
```

That’s the only place you write the routes.

### Attributes at a glance

| Attribute | Purpose |
|-----------|---------|
| `[ApiRoute("...")]` | Base route + optional named `HttpClient` |
| `[Get]`, `[Post]`, `[Put]`, `[Delete]`, `[Patch]` | HTTP verb (+ optional route suffix) |
| `[Route("name")]` | When the parameter name ≠ route token |
| `[Query]` | Query-string parameter |
| `[Body]` | JSON request body (max one per method) |
| `[Form]` | One `multipart/form-data` field (see below) |
| `[File]` | One or more uploaded files, `multipart/form-data` (see below) |
| `[Authorize]` / `[AllowAnonymous]` | Propagated to the generated controller |

`CancellationToken` is handled automatically and never becomes part of the URL.

### File uploads

`[Body]` produces JSON, which can't express `IFormFile`. For file uploads, use `[Form]` for
ordinary fields and `[File]` for the file(s) instead — RouteGen generates `multipart/form-data`
binding/request-building for both ends from the same declaration `[Body]` gets for JSON:

```csharp
[Post("upload-with-screenshot")]
[Authorize]
Task<ModDto> UploadWithScreenshot(
    [Form] string name,
    [Form] string description,
    [File] FormFile screenshot,
    CancellationToken ct = default);

// [File] on a collection-of-FormFile-typed parameter (optionally nullable) accepts several
// files under the same field name. IReadOnlyList<FormFile> here; List<>, an array, and several
// other shapes work too -- see the exact accepted list below.
[Post("upload-with-gallery")]
[Authorize]
Task<ModDto> UploadWithGallery(
    [Form] string name,
    [Form] string description,
    [File] IReadOnlyList<FormFile>? gallery,
    CancellationToken ct = default);
```

- `[Form]` parameters can be any type. A simple type (same rule `[Query]` uses — primitives,
  string, enum, Guid, DateTime, etc.) becomes `[FromForm]` server-side / a plain `StringContent`
  part client-side, unchanged. Anything else is JSON-serialized into the one field instead —
  server-side via a small generated model binder (nested in the controller base, emitted only
  when a method actually needs it) that reads the raw field and deserializes it, since a complex
  object has no meaningful single-string form the way a primitive does. This is a deliberate
  choice over mirroring ASP.NET Core's per-property complex-form binding (`field.PropertyName`
  for each property): that shape doesn't have an equivalent single declared parameter type to
  generate a matching client for, the way `[File]`'s mirrored collection type does.
- `[File]` parameters must be `FormFile`/`FormFile<TMetadata>` (single file), or — for multiple
  files — one of: an array, `List<>`, `IEnumerable<>`, `ICollection<>`, `IList<>`,
  `IReadOnlyList<>`, `IReadOnlyCollection<>` (of either), or another concrete generic collection
  type with a public parameterless constructor that implements `ICollection<>` — optionally
  nullable either way — never anything else (RG0010). **The server generates the exact same
  collection type the client declared** (with `IFormFile` substituted for `FormFile`) — this
  list isn't an arbitrary restriction, it's precisely the set of shapes
  [ASP.NET Core's own model binder](https://github.com/dotnet/aspnetcore/blob/main/src/Mvc/Mvc.Core/src/ModelBinding/ModelBindingHelper.cs)
  is verified able to construct without throwing at request time (types like
  `ImmutableList<>` satisfy the interface check but have no public constructor, which throws
  `MissingMethodException` from ASP.NET Core's own binder at runtime if used directly — RouteGen
  checks for the constructor explicitly so this is rejected at compile time instead, as RG0010).
  A non-generic custom collection class hardcoded to hold `FormFile` specifically can never be
  mirrored this way, since there's no way to construct an analogous type over `IFormFile` — those
  are always rejected too. Client-side, `FormFile` (from `RouteGen.Abstractions`) is a small
  `record` — `FormFile(Stream Content, string FileName, string? ContentType)` — that carries
  what a multipart file part needs to be built correctly. The caller owns the `Stream` and is
  responsible for disposing it once the call completes, same as any other API taking a
  caller-supplied stream.
- `FormFile<TMetadata>` attaches an arbitrary runtime value to the upload, for when the caller
  needs to correlate it with local state (UI context, progress tracking, retry bookkeeping):

  ```csharp
  [File] FormFile<UploadContext> screenshot
  // call site: new FormFile<UploadContext>(stream, "pic.png", Metadata: new UploadContext(...))
  ```

  `TMetadata` never reaches the server or changes the wire contract — `[File]` binds to
  `IFormFile` server-side either way, exactly as it does for plain `FormFile`. If the data
  genuinely needs to reach the server, send it as an ordinary `[Form]` field instead.
- `[Body]` and `[Form]`/`[File]` can't be combined on the same method — an HTTP request only has
  one content type (RG0009).
- There's no attribute for request size limits; that stays a hosting/infrastructure concern
  configured on the concrete controller action, same as it would be for a hand-written multipart
  endpoint.

## Server side

RouteGen generates `ModsApiControllerBase`. Your controller just implements the logic:

```csharp
public sealed class ModsController(IModsService service) : ModsApiControllerBase
{
    public override async Task<ActionResult<ModListResult>> GetMods(...)
        => Ok(await service.GetMods(...));

    // etc.
}
```

No route attributes needed on the concrete controller. Normal `AddControllers()` / `MapControllers()` is enough.

## Client side

Register the named `HttpClient` and the generated implementation:

```csharp
builder.Services.AddHttpClient("App", c => c.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress));
builder.Services.AddScoped<IModsApi, HttpModsApi>();
```

Then inject the interface:

```razor
@inject IModsApi ModsApi

result = await ModsApi.GetMods();
```

Non-success responses throw `ApiException` (with `StatusCode` and `ResponseBody`).

## Page routes

From `@page "/mod/{id:int}"` RouteGen generates:

```csharp
public static class Paths
{
    public static string ModDetail(int id) => $"/mod/{id}";
}
```

Use it instead of hard-coded URLs. Override the generated member name with `@attribute [GeneratedPathName("Whatever")]` if the default (derived from the `.razor` filename) would collide with another page.

`PageRouteGenerator` generates `Paths` into whichever project has the `.razor` files listed as `AdditionalFiles` — it doesn't care where those files physically live, only that they're visible to the compilation it's running in.

The recommended setup, and what the sample under `samples/` does, is to run **both** generators from the Shared project rather than from Client:

```xml
<!-- In Shared's .csproj -->
<ItemGroup>
  <ProjectReference Include="..\..\src\RouteGen.Abstractions\RouteGen.Abstractions.csproj" />
  <!-- Required at compile time: the generated HttpClient implementation's constructor takes an
       IHttpClientFactory, and that type lives in this package, not the base framework. -->
  <PackageReference Include="Microsoft.Extensions.Http" Version="..." />
  <ProjectReference Include="..\..\src\RouteGen.Generators\RouteGen.Generators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
<ItemGroup>
  <AdditionalFiles Include="..\YourApp.Client\**\*.razor" />
</ItemGroup>
```

Because Shared has no `Microsoft.AspNetCore.Mvc.ControllerBase` reference, `ApiContractGenerator` takes its **client**-emission branch there, and compiles the `HttpClient` implementation directly into `Shared.dll`. Combined with `PageRouteGenerator` also running there (via the `AdditionalFiles` glob above), Shared ends up holding the interface, the client implementation, *and* `Paths` — a single, unambiguous copy of each, reachable from both Server and Client purely through the `ProjectReference` to Shared they already have. Neither Server nor Client needs `RouteGen.Generators` for these; Server only needs it for its own `ApiContractGenerator`-driven controller base, and Client doesn't need it at all.

If you'd rather keep Shared a plain contracts-only library with no generator dependency, the alternative is to run `PageRouteGenerator` directly from Server instead, pointing its own `AdditionalFiles` at Client's `.razor` files. That produces a second, independent `Paths` class scoped to Server's own namespace — differently namespaced from Client's copy, identically shaped. More duplication, but zero change to Shared.

## Diagnostics

| ID | Meaning |
|----|---------|
| RG0001 | Duplicate verb + route |
| RG0002 | `[Body]` on GET/DELETE (warning) |
| RG0003 | Route token with no matching parameter |
| RG0004 | Parameter not marked as route / query / body |
| RG0005 | More than one `[Body]` |
| RG0006 | Unsupported parameter type |
| RG0007 | Duplicate `Paths` member name |
| RG0008 | Unparseable route template |
| RG0009 | `[Body]` combined with `[Form]`/`[File]` on the same method |
| RG0010 | `[File]` parameter's collection type isn't verified-bindable — see the README |

These turn what would be runtime URL bugs into build-time errors.

## Sample

```bash
dotnet run --project samples/SampleApp.Server
```

Look under `obj/**/generated/FlanderDev.RouteGen.Generators/` after a build to see the generated code.

## Limitations (current)

- No Minimal API generation (yet)
- No OpenAPI / Swagger support
- No static-asset URL helpers
- Page member names come from the `.razor` filename (override with `[GeneratedPathName]`)

## License

MIT
