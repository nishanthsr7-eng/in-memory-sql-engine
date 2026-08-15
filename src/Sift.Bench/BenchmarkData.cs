namespace Sift.Bench;

internal static class BenchmarkData
{
    public static string FmcgSalesCsv => Path.Combine(RepoRoot(), "data", "fmcg_sales.csv");
    public static string BrandsCsv => Path.Combine(RepoRoot(), "data", "brands.csv");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root (looked for sibling 'data' and 'src' directories)");
    }
}
