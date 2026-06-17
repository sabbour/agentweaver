using System.ComponentModel.DataAnnotations;

namespace Scaffolder.Api.Memory;

public sealed class SubtaskDependency
{
    [Key] public int Id { get; set; }
    public int SubtaskId { get; set; }
    public int DependsOnSubtaskId { get; set; }
}
