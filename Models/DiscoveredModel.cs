namespace DeadlockVmdlCompiler.Models;

public class DiscoveredModel
{
    public string Display { get; set; } = string.Empty;
    public string Hero { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;

    public override string ToString() => Display;
}

public class DiscoveredAddon
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public List<DiscoveredModel> HeroModels { get; set; } = new();
    public string Display { get; set; } = string.Empty;
    
    public string Details => HeroModels.Count > 0
        ? $"{HeroModels.Count} hero model(s) detected"
        : "addon folder";

    public bool HasHero => HeroModels.Count > 0;

    public override string ToString() => Display;
}
