namespace Kasi.ArchitectureTests;

public class ProjectLayeringTests
{
    public static TheoryData<string> AllProjects => new(SolutionLayout.AllowedReferences.Keys);

    [Theory]
    [MemberData(nameof(AllProjects))]
    [Trait("Requirement", "KASI-NFR-019")]
    public void Project_references_only_projects_allowed_by_TRD_5_2(string project)
    {
        var actual = SolutionLayout.ProjectReferences(project);

        actual.Should().BeSubsetOf(SolutionLayout.AllowedReferences[project]);
    }

    [Fact]
    [Trait("Requirement", "KASI-NFR-019")]
    public void Every_project_under_src_is_covered_by_the_layering_map()
    {
        SolutionLayout.SourceProjects().Should().BeEquivalentTo(SolutionLayout.AllowedReferences.Keys);
    }
}
