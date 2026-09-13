# FlanderDev.RouteGen.Migration

Optional, temporary migration tooling for [RouteGen](https://github.com/FlanderDev/RouteGen).

This package reverse-engineers an existing, attribute-routed ASP.NET Core controller into a
starting-point RouteGen `[ApiRoute]` interface — the same shape `RouteGen.Generators` expects to
generate a controller base and a client from going forward. It's meant to be added while
migrating an existing API onto RouteGen, and removed once migration is done. It never runs at
build time in a way that affects your app, and it has no runtime component at all.

## What it does, precisely

- Adds an **Info**-severity suggestion, `RGM0001`, to any controller that:
  - derives from `ControllerBase`,
  - uses **attribute routing** (`[Route]` + `[HttpGet]`/`[HttpPost]`/etc.) — convention-routed
    controllers (no such attributes at all) are never touched, since recovering their routes
    would require knowing your app's registered route templates, not just the controller itself,
  - has at least one action RouteGen can actually translate (see below).
- Applying the fix generates one new file, `I{Stem}Api.cs`, containing a draft interface —
  ordinary, editable source code, not build-time generated output. **It never modifies the
  controller.**
- Supports Fix-All (document/project/solution), so a real migration — usually dozens of
  controllers, not one — doesn't mean clicking through every one individually.
- Once a type named `I{Stem}Api` already exists anywhere in the solution, `RGM0001` stops firing
  for the matching controller, so re-running Fix-All after a partial migration won't regenerate
  files you've already reviewed and moved.

## Quick start

```bash
dotnet add package FlanderDev.RouteGen.Migration
```

Add it to your **Server** project (temporarily). Open the Error List, find an `RGM0001`
suggestion on a controller, and either apply the individual fix or right-click → **Fix all
occurrences in** → Solution.

When it's done, remove the package:

```bash
dotnet remove package FlanderDev.RouteGen.Migration
```

## What you still have to do by hand

Generating the interface is only the first of several steps — deliberately. Automatically
rewriting a live, working controller (removing its attributes, changing its base class) is a much
bigger blast radius than adding a new file, so this package stops well short of that:

1. **Move the file to your Shared project**, if it didn't land there automatically. The fix looks
   for exactly one other project in the solution whose name matches yours with a `.Shared` (or
   plain `Shared`) suffix in place of your project's own — e.g. `MyApp.Server` → `MyApp.Shared`.
   If it can't find exactly one confident match, the file is added next to the controller instead,
   with a comment telling you to move it.
2. **Read every `TODO` comment** in the generated file. There are three kinds:
   - Actions that couldn't be translated at all (see below), listed once at the top of the file.
   - Attributes that were recognized but dropped (`[Produces]`, `[ServiceFilter]`, API versioning
     attributes, etc.) — noted per-action, since they don't block generating the rest of that
     action but do change behavior you may still want.
   - Types referenced by the interface that are currently declared in your Server project — these
     need to move to Shared too (or wherever the interface ends up), or it won't compile there.
3. **Point the concrete controller at the generated abstract base** and remove its own
   `[Route]`/`[Http*]` attributes, the same manual step described in the main RouteGen README for
   any controller.

## What gets skipped, and why

Per-action, not per-controller: one action RouteGen can't translate never blocks migrating the
rest of the controller. A skipped action is *omitted* from the generated interface and listed in
a `TODO` comment — never guessed at with a maybe-wrong signature. Skipped for:

- More than one HTTP verb attribute on one action (RouteGen supports exactly one verb per method).
- A parameter using `[FromHeader]`, `[FromServices]`, or a custom `[ModelBinder]`.
- A return type with no recoverable response type: bare `IActionResult`, bare `ActionResult`
  (no type argument), or a concrete non-JSON result (`FileResult`, `ContentResult`,
  `RedirectResult`, and similar).

Everything else translates directly:

| Controller attribute / shape | Generated RouteGen attribute |
|---|---|
| `[FromRoute]`, or no attribute + name matches a route token | *(none — RouteGen infers it)* |
| `[FromQuery]`, or no attribute + a simple type | `[Query]` |
| `[FromBody]`, or no attribute + a complex type (MVC's own default) | `[Body]` |
| `[FromForm]` (non-file types) | `[Form]` |
| `IFormFile` / `IFormFileCollection` / `List<IFormFile>` / etc. | `[File] FormFile` / `[File] IReadOnlyList<FormFile>` |
| `CancellationToken` | carried over as-is |
| `[Authorize]`, `[AllowAnonymous]` | carried over as-is |
| `ActionResult<T>` / `Task<ActionResult<T>>` / `Task<T>` | `Task<T>` |
| A `[controller]` token in `[Route]` | resolved to the class name stem (`Controller` suffix stripped) |
| An `[action]` token in an `[Http*]` template | resolved to the action's own name (always unambiguous) |

## License

MIT
