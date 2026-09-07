using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SampleApp.Server.Services;
using SampleApp.Shared;

namespace SampleApp.Server.Controllers;

// Thin concrete controller: no route attributes, no route strings, anywhere. All routing,
// binding, and [Authorize] behavior comes from the RouteGen-generated ModsApiControllerBase
// (see obj/**/generated/FlanderDev.RouteGen.Generators/.../Server_IModsApi.g.cs after build).
public sealed class ModsController(IModsService service) : ModsApiControllerBase
{
    public override async Task<ActionResult<ModListResult>> GetMods(int page, int pageSize, string? search, SortBy sort)
        => Ok(await service.GetMods(page, pageSize, search, sort));

    public override async Task<ActionResult<ModDto>> GetMod(int id)
        => await service.GetMod(id) is { } mod ? Ok(mod) : NotFound();

    public override async Task<ActionResult<ModDto>> Upload(ModUploadDto dto)
        => Ok(await service.Upload(dto));

    public override async Task<ActionResult<ModDto>> UploadWithScreenshot(string name, string description, IFormFile screenshot, CancellationToken ct)
        => Ok(await service.UploadWithScreenshot(name, description, screenshot));

    public override async Task<ActionResult<ModDto>> UploadWithGallery(string name, string description, ModTags tags, IReadOnlyList<IFormFile>? gallery, CancellationToken ct)
        => Ok(await service.UploadWithGallery(name, description, tags, gallery));

    public override async Task<IActionResult> Delete(int id, CancellationToken ct)
        => await service.Delete(id) ? NoContent() : NotFound();
}
