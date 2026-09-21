using Viegard.Application.Classifiers;
using Viegard.Application.Configuration;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class LocalModelAdvisorPromptTemplatesTests
{
    [Fact]
    public void Validator_accepts_valid_template()
    {
        Assert.True(LocalModelAdvisorPromptTemplateValidator.TryValidate(
            AdvisoryIncidentClassifier.PromptTemplateId,
            "System {output_schema}",
            "Application {base_category} {base_severity} {base_confidence} {max_severity_delta} {max_confidence_delta}",
            "note",
            out var error));
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("System {output_schema}", "Application {unknown}", "Local-model advisor prompt placeholder {unknown} is not allowed.")]
    [InlineData("System only", "Application {base_category}", LocalModelAdvisorPromptTemplateValidator.OutputSchemaError)]
    [InlineData("", "Application {output_schema}", LocalModelAdvisorPromptTemplateValidator.InstructionsError)]
    [InlineData("System {output_schema}\u0001", "Application", LocalModelAdvisorPromptTemplateValidator.InstructionsError)]
    public void Validator_rejects_invalid_templates(string system, string application, string expectedError)
    {
        Assert.False(LocalModelAdvisorPromptTemplateValidator.TryValidate(
            AdvisoryIncidentClassifier.PromptTemplateId,
            system,
            application,
            null,
            out var error));
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void Validator_rejects_over_length_fields()
    {
        Assert.False(LocalModelAdvisorPromptTemplateValidator.TryValidate(
            AdvisoryIncidentClassifier.PromptTemplateId,
            new string('a', LocalModelAdvisorPromptTemplateRevision.MaxInstructionsLength + 1),
            "Application {output_schema}",
            null,
            out var error));
        Assert.Equal(LocalModelAdvisorPromptTemplateValidator.InstructionsError, error);

        Assert.False(LocalModelAdvisorPromptTemplateValidator.TryValidate(
            AdvisoryIncidentClassifier.PromptTemplateId,
            "System {output_schema}",
            "Application",
            new string('n', LocalModelAdvisorPromptTemplateRevision.MaxNoteLength + 1),
            out error));
        Assert.Equal(LocalModelAdvisorPromptTemplateValidator.NoteError, error);
    }

    [Fact]
    public async Task Source_uses_active_revision_and_falls_back_to_code_template()
    {
        var emptySource = new LocalModelAdvisorPromptTemplateSource(new InMemoryLocalModelAdvisorPromptTemplateStore());
        await emptySource.RefreshAsync();
        Assert.Equal(LocalModelAdvisorPrompt.Template.Version, emptySource.Current.Version);

        var store = new InMemoryLocalModelAdvisorPromptTemplateStore();
        var revision = await store.CreateRevisionAsync(
            AdvisoryIncidentClassifier.PromptTemplateId,
            "System {output_schema}",
            "Application {base_category}",
            "active",
            "tester",
            DateTimeOffset.UtcNow);
        var source = new LocalModelAdvisorPromptTemplateSource(store);
        await source.RefreshAsync();

        Assert.Equal(revision.ToPromptTemplate(), source.Current);
        Assert.Equal(revision, source.ActiveRevision);
    }

    [Fact]
    public async Task In_memory_store_creates_revisions_lists_history_and_activates_prior_revision()
    {
        var store = new InMemoryLocalModelAdvisorPromptTemplateStore();
        var now = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

        var first = await store.CreateRevisionAsync(
            AdvisoryIncidentClassifier.PromptTemplateId,
            "System {output_schema}",
            "Application {base_category}",
            "first",
            "tester",
            now);
        var second = await store.CreateRevisionAsync(
            AdvisoryIncidentClassifier.PromptTemplateId,
            "System v2 {output_schema}",
            "Application {base_severity}",
            "second",
            "tester",
            now.AddMinutes(1));

        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Revision);
        Assert.False((await store.GetAsync(first.Id))!.IsActive);
        Assert.True((await store.GetAsync(second.Id))!.IsActive);
        var revisions = await store.ListAsync(AdvisoryIncidentClassifier.PromptTemplateId);
        Assert.Equal(new[] { 2, 1 }, revisions.Select(r => r.Revision).ToArray());

        var activated = await store.ActivateAsync(first.Id);

        Assert.True(activated.Succeeded);
        Assert.Equal(2, activated.Before!.Revision);
        Assert.Equal(1, activated.After!.Revision);
        Assert.True((await store.GetAsync(first.Id))!.IsActive);
        Assert.False((await store.GetAsync(second.Id))!.IsActive);
        Assert.Equal(1, (await store.GetActiveAsync(AdvisoryIncidentClassifier.PromptTemplateId))!.Revision);
    }
}
