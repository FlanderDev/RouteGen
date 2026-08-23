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
| `[Authorize]` / `[AllowAnonymous]` | Propagated to the generated controller |

`CancellationToken` is handled automatically and never becomes part of the URL.

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
