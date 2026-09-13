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

**Migrating an existing API onto RouteGen?** There's a third, optional package,
`FlanderDev.RouteGen.Migration`, that reverse-engineers an attribute-routed controller into a
starting-point interface — add it temporarily, run it, remove it. See its own
[README](src/FlanderDev.RouteGen.Migration/README.md) for what it does and doesn't do.

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

// [File] on a collection-of-FileWithData<TData>-typed parameter (optionally nullable) accepts
// several files under the same field name, each carrying its own data -- no separate parallel
// list to keep in sync by index. IReadOnlyList<> here; List<>, an array, and several other
// shapes work too -- see the exact accepted list below.
[Post("upload-with-gallery")]
[Authorize]
Task<ModDto> UploadWithGallery(
    [Form] string name,
    [Form] string description,
    [File] IReadOnlyList<FileWithData<PhotoCaption>>? gallery,
    CancellationToken ct = default);

public sealed record PhotoCaption(string Caption, int SortOrder);
```

- `[Form]` parameters must be simple types (same rule as `[Query]`) and become `[FromForm]`
  server-side / a `StringContent` part client-side.
- `[File]` parameters must be `FormFile` or `FileWithData<TData>` (single file), or — for
  multiple files — one of: an array, `List<>`, `IEnumerable<>`, `ICollection<>`, `IList<>`,
  `IReadOnlyList<>`, `IReadOnlyCollection<>` (of either), or another concrete generic collection
  type with a public parameterless constructor that implements `ICollection<>` — optionally
  nullable either way — never anything else (RG0010). **The server generates the exact same
  collection type the client declared** (with `IFormFile` substituted for `FormFile`, or the
  generated controller base's own nested `FileWithData<TData>` substituted for
  `FileWithData<TData>`) — this list isn't an arbitrary restriction, it's precisely the set of
  shapes [ASP.NET Core's own model binder](https://github.com/dotnet/aspnetcore/blob/main/src/Mvc/Mvc.Core/src/ModelBinding/ModelBindingHelper.cs)
  is verified able to construct without throwing at request time (types like
  `ImmutableList<>` satisfy the interface check but have no public constructor, which throws
  `MissingMethodException` from ASP.NET Core's own binder at runtime if used directly — RouteGen
  checks for the constructor explicitly so this is rejected at compile time instead, as RG0010).
  A non-generic custom collection class hardcoded to hold `FormFile` specifically can never be
  mirrored this way, since there's no way to construct an analogous type over `IFormFile` — those
  are always rejected too. A custom **generic** collection type closed over `FormFile` is also
  checked for a subtler problem: if it constrains its element type parameter in a way `IFormFile`
  can't satisfy (a plausible `where T : FormFile`, for instance — a class constraint, which an
  interface can never meet), mirroring it to `IFormFile` would produce an invalid closed generic
  type. RouteGen checks this too and rejects it at compile time (RG0011) rather than letting it
  surface as a confusing generic-constraint error in the generated server file. (This check
  doesn't extend to a custom collection closed over `FileWithData<TData>` — a custom collection
  *and* paired-data files *and* an incompatible constraint, all at once, was judged too narrow an
  edge case to justify the added complexity for now.) The constraint check only runs in the
  server project's own build (it needs `IFormFile` itself to be resolvable to check against) —
  the same interface method is independently parsed there regardless of where else it's
  referenced from, so the check still fires at the point it's actually decidable.
  Client-side, `FormFile` (from `RouteGen.Abstractions`) is a small
  `record` — `FormFile(Stream Content, string FileName, string? ContentType)` — that carries
  what a multipart file part needs to be built correctly. The caller owns the `Stream` and is
  responsible for disposing it once the call completes, same as any other API taking a
  caller-supplied stream.
- `FileWithData<TData>` pairs a `FormFile` with an arbitrary `TData` value that's genuinely sent
  to and readable by the server — the tool for exactly the case a second, parallel list of data
  next to a list of files gets messy and error-prone (nothing guarantees the two lists stay the
  same length or order, so a dropped/reordered item silently pairs the wrong data with the wrong
  file). `TData` can be any type; it's JSON-serialized into its own multipart field, correlated
  with the file by field name — `photos[0].file`/`photos[0].data`, `photos[1].file`/
  `photos[1].data`, and so on for the multi-file case, or `photo.file`/`photo.data` for a single
  one — never by list position. The generated controller base gets its own nested
  `FileWithData<TData>` record (reconstructed by a generated model binder from the matching file
  and data parts); reference it from a concrete controller/service by simple name if you inherit
  from the base, or `{Stem}ApiControllerBase.FileWithData<TData>` if you don't. For a *single*
  file, you don't need this at all — just add more `[Form]` fields alongside the `[File]`
  parameter, as `UploadWithScreenshot` above does.
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

### Components with more than one `@page` route

A single component can declare more than one `@page` directive — Blazor supports this natively.
The first route on a component behaves exactly as described above (filename-derived name, or the
single-argument `[GeneratedPathName("Name")]` override). Every route *after* the first requires
its own explicit two-argument override, naming that specific route by its literal text:

```razor
@page "/mods"
@page "/mods/list"
@attribute [GeneratedPathName("/mods/list", "ModsList")]
```

```csharp
public static class Paths
{
    public const string Mods = "/mods";
    public const string ModsList = "/mods/list";
}
```

This is required, not optional — RouteGen won't guess a name for you (e.g. by numbering routes),
since that would silently rename a generated member the moment a route were reordered or removed
in the source file. A route with no matching override is RG0012, a compile error naming exactly
which route needs one.

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
| RG0001 | Duplicate verb + route (parameter *names* don't count — `{id:int}` and `{value:int}` at the same position collide just the same) |
| RG0002 | `[Body]` on GET/DELETE (warning) |
| RG0003 | Route token with no matching parameter |
| RG0004 | Parameter not marked as route / query / body / form / file |
| RG0005 | More than one `[Body]` |
| RG0006 | Unsupported parameter type |
| RG0007 | Duplicate `Paths` member name |
| RG0008 | Unparseable route template |
| RG0009 | `[Body]` combined with `[Form]`/`[File]` on the same method |
| RG0010 | `[File]` parameter's collection type isn't verified-bindable — see the README |
| RG0011 | Custom `[File]` collection type's generic constraint can't be satisfied by `IFormFile` |
| RG0012 | A component's `@page` route beyond the first has no matching `[GeneratedPathName]` |
| RG0013 | Two routes have the same shape but differ in constraint tightness at one position (warning) |

These turn what would be runtime URL bugs into build-time errors.

### A note on RG0001 vs RG0013

RG0001 only fires when two routes are **guaranteed** to resolve identically at runtime — same
literal text, same parameter positions, same constraint at each of those positions (parameter
*names* are ignored, since ASP.NET Core's routing never uses them to disambiguate). RG0013 is
the softer, non-guaranteed sibling: same shape, but one route leaves a position unconstrained
(or differently constrained) where the other doesn't. ASP.NET Core's constraint precedence can
correctly resolve many such pairs, so this is a warning worth double-checking, not an assertion
that the code is broken. Deeper route-ambiguity analysis (e.g. accounting for optional
parameters creating variable-length effective routes) is intentionally out of scope — it would
mean re-implementing a meaningful slice of ASP.NET Core's own routing precedence rules, with
real risk of false positives on a public analyzer. `AmbiguousMatchException` remains the runtime
backstop for anything past what RG0001/RG0013 catch, and it surfaces on the very first request
that actually hits the ambiguity, in any environment including local dev.

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
