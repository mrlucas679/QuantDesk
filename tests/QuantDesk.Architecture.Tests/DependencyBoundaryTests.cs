namespace QuantDesk.Architecture.Tests;

public sealed class DependencyBoundaryTests
{
    [Fact]
    public void ProductionProjects_DoNotReferenceHarness()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourceRoot = Path.Combine(repositoryRoot, "src");

        string[] projectFiles = Directory.GetFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories);
        Assert.NotEmpty(projectFiles);

        foreach (string projectFile in projectFiles)
        {
            string project = File.ReadAllText(projectFile);
            Assert.DoesNotContain("QuantDesk.Harness", project, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DomainProject_HasNoProjectDependencies()
    {
        string projectPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "QuantDesk.Domain",
            "QuantDesk.Domain.csproj");

        string project = File.ReadAllText(projectPath);

        Assert.DoesNotContain("ProjectReference", project, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

