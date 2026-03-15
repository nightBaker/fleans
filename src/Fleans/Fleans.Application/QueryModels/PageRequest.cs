namespace Fleans.Application.QueryModels;

public record PageRequest(
    int Page = 1,
    int PageSize = 20,
    string? Sorts = null,
    string? Filters = null)
{
    public PageRequest Normalize() => this with
    {
        Page = Math.Max(1, Page),
        PageSize = Math.Clamp(PageSize, 1, 100)
    };
}
