using SampleApp.Shared;

namespace SampleApp.Server.Services;

public interface IModsService
{
    Task<ModListResult> GetMods(int page, int pageSize, string? search, SortBy sort);
    Task<ModDto?> GetMod(int id);
    Task<ModDto> Upload(ModUploadDto dto);
    Task<bool> Delete(int id);
}

/// <summary>Trivial in-memory store, purely so the sample runs without a database.</summary>
public sealed class InMemoryModsService : IModsService
{
    private readonly List<ModDto> _mods =
    [
        new ModDto(1, "Better Torches", "alice", 1204),
        new ModDto(2, "Faster Loading", "bob", 8931),
        new ModDto(3, "Extra Biomes", "carol", 452),
    ];

    public Task<ModListResult> GetMods(int page, int pageSize, string? search, SortBy sort)
    {
        var query = _mods.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(m => m.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        query = sort == SortBy.Popular
            ? query.OrderByDescending(m => m.Downloads)
            : query.OrderByDescending(m => m.Id);

        var items = query.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new ModListResult(items, _mods.Count));
    }

    public Task<ModDto?> GetMod(int id) =>
        Task.FromResult(_mods.FirstOrDefault(m => m.Id == id));

    public Task<ModDto> Upload(ModUploadDto dto)
    {
        var mod = new ModDto(_mods.Count + 1, dto.Name, "you", 0);
        _mods.Add(mod);
        return Task.FromResult(mod);
    }

    public Task<bool> Delete(int id)
    {
        var mod = _mods.FirstOrDefault(m => m.Id == id);
        if (mod is null) return Task.FromResult(false);
        _mods.Remove(mod);
        return Task.FromResult(true);
    }
}
