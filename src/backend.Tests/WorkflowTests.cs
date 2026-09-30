using System.Text.Json;
using System.Net;
using System.ComponentModel;
using ShipWithin.Api;
using Xunit;

namespace ShipWithin.Api.Tests;

/// <summary>Exercises one-shot planning without a live Copilot request.</summary>
public sealed class WorkflowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ship-within-" + Guid.NewGuid());
    private readonly FakeProductOwner agent = new();
    private readonly Workflow sut;

    /// <summary>Creates an isolated local state file.</summary>
    public WorkflowTests()
    {
        sut = new Workflow(Path.Combine(directory, "workspace.json"), agent);
    }

    /// <summary>Deletes test-only local state.</summary>
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);

    private static object DraftInput(string objective = "Order defects and features") => new
    {
        repository = "team/product", objective, criteria = "Customer blockers",
        planningCredits = 2, deadline = DateTimeOffset.UtcNow.AddMinutes(2).ToString("O"), model = "gpt-4.1",
        candidates = new[] { new { title = "Sign-in fails", kind = "defect", source = "manual", body = "Cannot sign in" } }
    };

    [Theory]
    [InlineData(1, 0.01)]
    [InlineData(2, 0.02)]
    [InlineData(100, 1.00)]
    public void GivenCreditAllocation_WhenSaving_ReportsDerivedUsd(int credits, double expectedUsd)
    {
        var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Input(DraftInput()).GetRawText())!;
        input["planningCredits"] = Input(credits);
        var saved = sut.Save(Input(input));
        var reported = JsonSerializer.SerializeToElement(saved.Draft, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal((decimal)expectedUsd, saved.Draft!.EstimatedCostUsd);
        Assert.Equal((decimal)expectedUsd, reported.GetProperty("estimatedCostUsd").GetDecimal());
        Assert.False(reported.TryGetProperty("budgetUsd", out _));
        var reloaded = new Workflow(Path.Combine(directory, "workspace.json"), agent);
        Assert.Equal((decimal)expectedUsd, reloaded.Snapshot().Draft!.EstimatedCostUsd);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("1.5")]
    [InlineData("")]
    public void GivenInvalidCredits_WhenSaving_RejectsAllocation(string credits)
    {
        var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Input(DraftInput()).GetRawText())!;
        input["planningCredits"] = Input(credits);

        Assert.Equal("Planning credits must be from 1 to 100.", Assert.Throws<WorkflowException>(() => sut.Save(Input(input))).Message);
        Assert.Null(sut.Snapshot().Draft);
    }

    [Fact]
    public void GivenLegacyUsdBudgets_WhenReloading_ReportsFromCredits()
    {
        sut.Save(Input(DraftInput()));
        sut.New(default);
        var saved = sut.Save(Input(DraftInput()));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacy = JsonSerializer.SerializeToNode(saved, options)!;
        legacy["draft"]!["budgetUsd"] = 999;
        legacy["draft"]!.AsObject().Remove("estimatedCostUsd");
        legacy["history"]![0]!["draft"]!["budgetUsd"] = 10;
        legacy["history"]![0]!["draft"]!.AsObject().Remove("estimatedCostUsd");
        var file = Path.Combine(directory, "workspace.json");
        File.WriteAllText(file, legacy.ToJsonString());

        var reloaded = new Workflow(file, agent).Snapshot();

        Assert.Equal(0.02m, reloaded.Draft!.EstimatedCostUsd);
        Assert.Equal(0.02m, Assert.Single(reloaded.History).Draft.EstimatedCostUsd);
        var reported = JsonSerializer.SerializeToElement(reloaded, options);
        Assert.False(reported.GetProperty("draft").TryGetProperty("budgetUsd", out _));
        Assert.False(reported.GetProperty("history")[0].GetProperty("draft").TryGetProperty("budgetUsd", out _));
    }

    [Fact]
    public void GivenSelectedRepository_WhenReloadingAndSaving_KeepsBindingUntilNewKickoff()
    {
        var selected = sut.SelectRepository(Input(new { repository = "team/product" }));
        Assert.Equal("team/product", selected.Repository);
        Assert.Null(selected.Draft);

        var reloaded = new Workflow(Path.Combine(directory, "workspace.json"), agent);
        Assert.Equal("team/product", reloaded.Snapshot().Repository);
        Assert.Equal(409, Assert.Throws<WorkflowException>(() => reloaded.SelectRepository(Input(new { repository = "other/product" }))).Status);
        var changed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Input(DraftInput()).GetRawText())!;
        changed["repository"] = Input("other/product");
        Assert.Equal(409, Assert.Throws<WorkflowException>(() => reloaded.Save(Input(changed))).Status);
        Assert.Equal("team/product", reloaded.Save(Input(DraftInput())).Repository);
        Assert.Null(reloaded.New(default).Repository);
        Assert.Equal("other/product", reloaded.SelectRepository(Input(new { repository = "other/product" })).Repository);
    }

    [Fact]
    public void GivenNoObjective_WhenSaving_PreservesRepositoryAndLimits()
    {
        var saved = sut.Save(Input(DraftInput("")));

        Assert.Equal("team/product", saved.Draft!.Repository);
        Assert.Equal("", saved.Draft.Objective);
        Assert.Equal(2, saved.Draft.PlanningCredits);
    }

    [Fact]
    public void GivenAgentModelChanged_WhenSaving_UsesSettingAndInvalidatesAuthorization()
    {
        var saved = sut.Save(Input(DraftInput()));
        sut.Authorize(Input(new { revision = saved.Draft!.Revision, acknowledgeSoftCap = true }));

        var configured = sut.SetAgentModel(Input(new { agent = "productOwner", model = "gpt-5" }));

        Assert.Equal("gpt-5", configured.AgentModels!["productOwner"]);
        Assert.Equal("gpt-5", configured.Draft!.Model);
        Assert.Equal(2, configured.Draft.Revision);
        Assert.Null(configured.AuthorizedRevision);
        Assert.Throws<WorkflowException>(() => sut.TriageAsync(Input(new { revision = 1 })).GetAwaiter().GetResult());
        Assert.Equal("gpt-5", sut.Save(Input(DraftInput())).Draft!.Model);
    }

    [Fact]
    public async Task GivenCompletedRun_WhenModelChanges_PreservesRunSnapshotAndFutureSetting()
    {
        sut.Save(Input(DraftInput()));
        sut.Authorize(Input(new { revision = 1, acknowledgeSoftCap = true }));
        await sut.TriageAsync(Input(new { revision = 1 }));

        var configured = sut.SetAgentModel(Input(new { agent = "productOwner", model = "gpt-5" }));

        Assert.Equal("gpt-4.1", configured.Draft!.Model);
        Assert.Equal(1, configured.Draft.Revision);
        Assert.Equal("gpt-5", configured.AgentModels!["productOwner"]);
        Assert.Throws<WorkflowException>(() => sut.SetAgentModel(Input(new { agent = "unknown", model = "gpt-5" })));
        var next = sut.New(Input(new { reconcileUsage = true }));
        Assert.Equal("gpt-5", next.AgentModels!["productOwner"]);
        Assert.Equal("gpt-5", sut.Save(Input(DraftInput())).Draft!.Model);
    }

    [Fact]
    public void GivenLegacyDraft_WhenReloading_UsesItsModelAsAgentDefault()
    {
        var saved = sut.Save(Input(DraftInput()));
        var file = Path.Combine(directory, "workspace.json");
        var legacy = new { saved.Draft, saved.AuthorizedRevision, saved.Run, saved.Proposal, saved.History };
        File.WriteAllText(file, JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var reloaded = new Workflow(file, agent);

        Assert.Equal("gpt-4.1", reloaded.Snapshot().AgentModels!["productOwner"]);
        Assert.Equal("team/product", reloaded.Snapshot().Repository);
        Assert.Equal(409, Assert.Throws<WorkflowException>(() => reloaded.SelectRepository(Input(new { repository = "other/product" }))).Status);
    }

    [Fact]
    public void GivenLegacyCandidates_WhenReloading_PreservesOnlyConfirmedGitHubTriage()
    {
        var saved = sut.Save(Input(DraftInput()));
        var file = Path.Combine(directory, "workspace.json");
        var legacy = new
        {
            draft = saved.Draft! with
            {
                Candidates =
                [
                    new("gh-1", "story", "github", "Unknown", "", null),
                    new("gh-2", "story", "github", "Confirmed", "", null, true),
                    new("manual-3", "story", "manual", "Local", "", null)
                ]
            },
            history = new[] { new HistoryEntry(saved.Draft with { Candidates = [new("gh-3", "story", "github", "Archived", "", null)] }, null, null) }
        };
        var state = JsonSerializer.SerializeToNode(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        state["draft"]!["candidates"]![0]!.AsObject().Remove("triaged");
        state["draft"]!["candidates"]![1]!.AsObject().Remove("triaged");
        state["draft"]!["candidates"]![1]!["TRIAGED"] = true;
        state["draft"]!["candidates"]![2]!.AsObject().Remove("triaged");
        state["history"]![0]!["draft"]!["candidates"]![0]!.AsObject().Remove("triaged");
        File.WriteAllText(file, state.ToJsonString());

        var reloaded = new Workflow(file, agent).Snapshot();

        Assert.Equal([false, true, true], reloaded.Draft!.Candidates.Select(item => item.Triaged));
        Assert.False(reloaded.History[0].Draft.Candidates[0].Triaged);
    }

    [Fact]
    public async Task GivenSavedRevision_WhenPlanning_RunsOnceAndRequiresReconciliation()
    {
        var saved = sut.Save(Input(DraftInput()));
        Assert.Equal("manual-1", saved.Draft!.Candidates[0].Id);
        Assert.Throws<WorkflowException>(() => sut.Authorize(Input(new { revision = 1, acknowledgeSoftCap = false })));
        sut.Authorize(Input(new { revision = 1, acknowledgeSoftCap = true }));
        var edited = sut.Save(Input(DraftInput("New objective")));
        Assert.Null(edited.AuthorizedRevision);
        Assert.Throws<WorkflowException>(() => sut.TriageAsync(Input(new { revision = 1 })).GetAwaiter().GetResult());
        sut.Authorize(Input(new { revision = 2, acknowledgeSoftCap = true }));
        var result = await sut.TriageAsync(Input(new { revision = 2 }));
        Assert.Equal("completed", result.Run!.Status);
        Assert.Equal(1, agent.Calls);
        Assert.Throws<WorkflowException>(() => sut.New(default));
        Assert.Throws<WorkflowException>(() => sut.TriageAsync(Input(new { revision = 2 })).GetAwaiter().GetResult());
        var archived = sut.New(Input(new { reconcileUsage = true }));
        Assert.Equal("external", archived.History[0].Run!.UsageReconciliation!.Method);
        Assert.Null(archived.Run);
    }

    [Fact]
    public void GivenInterruptedRun_WhenReloading_RequiresExternalReconciliation()
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "workspace.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            draft = (object?)null, authorizedRevision = (int?)null,
            run = new { status = "running", revision = 1, usage = "unknown" }, proposal = (object?)null,
            history = new[] { new { draft = DraftInput(), run = new { status = "failed", revision = 1, usage = "unknown" }, proposal = (object?)null } }
        }));
        var restarted = new Workflow(file, agent);
        Assert.Equal("interrupted", restarted.Snapshot().Run!.Status);
        Assert.Throws<WorkflowException>(() => restarted.New(Input(new { reconcileUsage = false })));
        var archived = restarted.New(Input(new { reconcileUsage = true }));
        Assert.Equal("external", archived.History[0].Run!.UsageReconciliation!.Method);
    }

    [Fact]
    public void GivenUnknownArchivedUsage_WhenAuthorizing_BlocksUntilReconciled()
    {
        var saved = sut.Save(Input(DraftInput()));
        var file = Path.Combine(directory, "workspace.json");
        var state = new WorkspaceState(saved.Draft, null, null, null,
            [new HistoryEntry(saved.Draft!, new PlanningRun("failed", DateTimeOffset.UtcNow, 1, 2, "unknown"), null)]);
        File.WriteAllText(file, JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var reloaded = new Workflow(file, agent);
        Assert.Throws<WorkflowException>(() => reloaded.Authorize(Input(new { revision = 1, acknowledgeSoftCap = true })));
        reloaded.New(Input(new { reconcileUsage = true }));
        var next = reloaded.Save(Input(DraftInput()));
        Assert.Equal(1, next.Draft!.Revision);
        Assert.NotNull(reloaded.Authorize(Input(new { revision = 1, acknowledgeSoftCap = true })).AuthorizedRevision);
    }

    [Fact]
    public void GivenInventedOrDuplicateRanking_WhenValidating_RejectsProposal()
    {
        var candidates = sut.Save(Input(DraftInput())).Draft!.Candidates;
        Assert.Equal(502, Assert.Throws<WorkflowException>(() => Workflow.ValidateProposal(
            "{\"ranked\":[{\"id\":\"other\",\"reason\":\"priority\"}],\"questions\":[]}", candidates)).Status);
    }

    [Fact]
    public void GivenTriagedStories_WhenValidating_KeepsLinkedQuestionsAndEligibleAssignments()
    {
        var candidates = ReviewCandidates();
        var proposal = Workflow.ValidateProposal("""{"ranked":[{"id":"gh-1","reason":"Urgent"},{"id":"gh-2","reason":"Ready"}],"storyQuestions":[{"id":"gh-1","question":"Who signs off?"}],"assignments":["gh-2"]}""",
            candidates, new Milestone(5, "Next", null));

        Assert.Equal("gh-1", proposal.StoryQuestions![0].Id);
        Assert.Equal(["gh-2"], proposal.Assignments);
    }

    [Fact]
    public void GivenUntriagedStories_WhenValidating_AcceptsQuestionsWithoutRankingOrAssigningThem()
    {
        var candidates = ReviewCandidates();
        candidates.Add(new("manual-4", "story", "manual", "Local review", "", null, false));
        var proposal = Workflow.ValidateProposal("""{"ranked":[{"id":"gh-1","reason":"Urgent"},{"id":"gh-2","reason":"Ready"}],"storyQuestions":[{"id":"gh-3","question":"What is the outcome?"},{"id":"manual-4","question":"Who owns this?"}],"assignments":["gh-2"]}""",
            candidates, new Milestone(5, "Next", null));

        Assert.Equal(["gh-3", "manual-4"], proposal.StoryQuestions!.Select(item => item.Id));
        Assert.Equal(["gh-1", "gh-2"], proposal.Ranked.Select(item => item.Id));
        Assert.Equal(["gh-2"], proposal.Assignments);
    }

    [Theory]
    [InlineData("""{"ranked":[{"id":"gh-1","reason":"a"},{"id":"gh-2","reason":"b"},{"id":"gh-3","reason":"c"}],"storyQuestions":[],"assignments":[]}""")]
    [InlineData("""{"ranked":[{"id":"gh-1","reason":"a"},{"id":"gh-2","reason":"b"}],"storyQuestions":[{"id":"unknown","question":"Why?"}],"assignments":[]}""")]
    [InlineData("""{"ranked":[{"id":"gh-1","reason":"a"},{"id":"gh-2","reason":"b"}],"storyQuestions":[{"id":"gh-4","question":"Why?"}],"assignments":[]}""")]
    [InlineData("""{"ranked":[{"id":"gh-1","reason":"a"},{"id":"gh-2","reason":"b"}],"storyQuestions":[{"id":"gh-1","question":"Why?"}],"assignments":["gh-1"]}""")]
    [InlineData("""{"ranked":[{"id":"gh-1","reason":"a"},{"id":"gh-2","reason":"b"}],"storyQuestions":[],"assignments":["gh-3"]}""")]
    [InlineData("""{"ranked":[{"id":"gh-1","reason":"a"},{"id":"gh-2","reason":"b"}],"storyQuestions":[],"assignments":["unknown"]}""")]
    public void GivenIneligibleModelOutput_WhenValidating_RejectsProposal(string raw)
    {
        var candidates = ReviewCandidates();
        candidates.Add(new("gh-4", "defect", "github", "Not a story", "", null, false));
        Assert.Equal(502, Assert.Throws<WorkflowException>(() => Workflow.ValidateProposal(raw, candidates,
            new Milestone(5, "Next", null))).Status);
    }

    [Fact]
    public void GivenNoMilestone_WhenValidating_RejectsAssignments()
    {
        var raw = """{"ranked":[{"id":"gh-1","reason":"a"},{"id":"gh-2","reason":"b"}],"storyQuestions":[],"assignments":["gh-2"]}""";

        Assert.Equal(502, Assert.Throws<WorkflowException>(() => Workflow.ValidateProposal(raw, ReviewCandidates())).Status);
    }

    [Fact]
    public void GivenAlreadyMilestonedStory_WhenValidating_RejectsAssignment()
    {
        var candidates = ReviewCandidates();
        candidates[1] = candidates[1] with { MilestoneNumber = 4 };
        var raw = """{"ranked":[{"id":"gh-1","reason":"a"},{"id":"gh-2","reason":"b"}],"storyQuestions":[],"assignments":["gh-2"]}""";

        Assert.Equal(502, Assert.Throws<WorkflowException>(() => Workflow.ValidateProposal(raw, candidates,
            new Milestone(5, "Next", null))).Status);
    }

    private static List<Candidate> ReviewCandidates() =>
    [
        new("gh-1", "story", "github", "Needs review", "", null),
        new("gh-2", "story", "github", "Ready", "", null),
        new("gh-3", "story", "github", "Not triaged", "", null, false)
    ];

    [Fact]
    public async Task GivenDeadlineDuringPlanning_WhenAgentIsCancelled_ExpiresWithoutProposal()
    {
        agent.Response = async (draft, cancellation) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return "{}";
        };
        var input = JsonSerializer.SerializeToElement(DraftInput());
        var attributes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input.GetRawText())!;
        attributes["deadline"] = Input(DateTimeOffset.UtcNow.AddMilliseconds(300).ToString("O"));
        sut.Save(Input(attributes));
        sut.Authorize(Input(new { revision = 1, acknowledgeSoftCap = true }));

        await Assert.ThrowsAsync<WorkflowException>(() => sut.TriageAsync(Input(new { revision = 1 })));

        Assert.Equal("expired", sut.Snapshot().Run!.Status);
        Assert.Null(sut.Snapshot().Proposal);
        Assert.Equal("unknown", sut.Snapshot().Run!.Usage);
    }

    [Fact]
    public async Task GivenSlowImport_WhenKickoffChanges_RejectsStaleIssues()
    {
        var handler = new PendingIssueHandler();
        var workflow = new Workflow(Path.Combine(directory, "import.json"), agent, new HttpClient(handler));
        workflow.Save(Input(DraftInput()));
        var import = workflow.ImportAsync(CancellationToken.None);
        await handler.Entered.Task;
        workflow.New(default);
        workflow.Save(Input(DraftInput("Changed objective")));
        handler.Release.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"number\":1,\"title\":\"Old issue\",\"body\":\"\",\"labels\":[]}]", System.Text.Encoding.UTF8, "application/json")
        });

        var error = await Assert.ThrowsAsync<WorkflowException>(() => import);
        Assert.Equal(409, error.Status);
        Assert.DoesNotContain(workflow.Snapshot().Draft!.Candidates, item => item.Source == "github");
    }

    [Fact]
    public async Task GivenRepositoryOnly_WhenImporting_PreservesTriageAndFindsNextMilestone()
    {
        var handler = new ResponsesHandler(
            """[{"number":7,"title":"Checkout story","body":"Review payment","labels":[{"name":"story"},{"name":"triaged"}],"milestone":null,"html_url":"https://github.com/team/product/issues/7"},{"number":8,"title":"Unclassified","body":"","labels":[],"milestone":null}]""",
            """[{"number":3,"title":"Later","due_on":"2027-12-01T00:00:00Z"},{"number":2,"title":"Next","due_on":"2027-01-01T00:00:00Z"}]""");
        var workflow = new Workflow(Path.Combine(directory, "import.json"), agent, new HttpClient(handler));
        workflow.SelectRepository(Input(new { repository = "team/product" }));

        var imported = await workflow.ImportAsync(CancellationToken.None);

        Assert.Equal("", imported.Draft!.Objective);
        Assert.Equal("", imported.Draft.Criteria);
        Assert.Equal(imported.Draft.Revision, workflow.Authorize(Input(new { revision = imported.Draft.Revision, acknowledgeSoftCap = true })).AuthorizedRevision);
        Assert.Equal(2, imported.NextMilestone!.Number);
        Assert.Equal("story", imported.Draft!.Candidates[0].Kind);
        Assert.True(imported.Draft.Candidates[0].Triaged);
        Assert.False(imported.Draft.Candidates[1].Triaged);
        Assert.Equal(2, new Workflow(Path.Combine(directory, "import.json"), agent).Snapshot().NextMilestone!.Number);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GivenNoOpenMilestones_WhenImporting_ExposesAbsenceAndAllowsLocalTriageCorrection()
    {
        var handler = new ResponsesHandler(
            """[{"number":9,"title":"Review me","body":"","labels":[],"milestone":null}]""", "[]");
        var workflow = new Workflow(Path.Combine(directory, "import.json"), agent, new HttpClient(handler));
        workflow.SelectRepository(Input(new { repository = "team/product" }));
        var imported = await workflow.ImportAsync(CancellationToken.None);
        var corrected = imported.Draft!.Candidates[0] with { Kind = "story", Triaged = true };

        var saved = workflow.Save(Input(new { repository = "team/product", budgetUsd = 10, planningCredits = 2,
            deadline = DateTimeOffset.UtcNow.AddDays(1), candidates = new[] { new { id = corrected.Id, kind = corrected.Kind,
                source = corrected.Source, title = corrected.Title, body = corrected.Body, url = corrected.Url,
                triaged = corrected.Triaged, milestoneNumber = corrected.MilestoneNumber } } }));

        Assert.Null(saved.NextMilestone);
        Assert.Equal("", saved.Draft!.Objective);
        Assert.True(saved.Draft.Candidates[0].Triaged);
        Assert.Equal("story", saved.Draft.Candidates[0].Kind);
    }

    [Fact]
    public async Task GivenRemoteConflict_WhenApplying_PersistsSuccessAndRetriesOnlyRemainingStory()
    {
        var writer = new FakeIssueWriter();
        writer.Issues[2] = new RemoteIssue(true, 7);
        var workflow = await PreparedAssignmentAsync(writer);
        var acknowledgment = Input(new { revision = 1, milestoneNumber = 5, ids = new[] { "gh-1", "gh-2" }, acknowledge = true });

        var partial = await workflow.ApplyAsync(acknowledgment, CancellationToken.None);

        Assert.Equal(["gh-1"], partial.AppliedStories);
        Assert.Contains("gh-2", partial.AssignmentErrors!);
        Assert.Equal([1], writer.Assignments);
        var reloaded = new Workflow(Path.Combine(directory, "apply.json"), agent, issueWriter: writer);
        Assert.Equal(409, (await Assert.ThrowsAsync<WorkflowException>(() => reloaded.ApplyAsync(acknowledgment, CancellationToken.None))).Status);
        writer.Issues[2] = new RemoteIssue(true, null);
        writer.FailAssignments.Add(2);
        var retry = Input(new { revision = 1, milestoneNumber = 5, ids = new[] { "gh-2" }, acknowledge = true });
        var failed = await reloaded.ApplyAsync(retry, CancellationToken.None);
        Assert.Equal(["gh-1"], failed.AppliedStories);
        Assert.Contains("gh-2", failed.AssignmentErrors!);
        writer.FailAssignments.Clear();
        var completed = await reloaded.ApplyAsync(retry, CancellationToken.None);

        Assert.Equal(["gh-1", "gh-2"], completed.AppliedStories);
        Assert.Empty(completed.AssignmentErrors!);
        Assert.Equal([1, 2], writer.Assignments);
    }

    [Fact]
    public async Task GivenMissingOrStaleAcknowledgement_WhenApplying_DoesNotReadOrAssign()
    {
        var writer = new FakeIssueWriter();
        var workflow = await PreparedAssignmentAsync(writer);

        await Assert.ThrowsAsync<WorkflowException>(() => workflow.ApplyAsync(Input(new { revision = 0, milestoneNumber = 5,
            ids = new[] { "gh-1", "gh-2" }, acknowledge = true }), CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowException>(() => workflow.ApplyAsync(Input(new { revision = 1, milestoneNumber = 5,
            ids = new[] { "gh-1", "gh-2" }, acknowledge = false }), CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowException>(() => workflow.ApplyAsync(Input(new { revision = 1, milestoneNumber = 5,
            ids = new[] { "gh-1" }, acknowledge = true }), CancellationToken.None));

        Assert.Empty(writer.Reads);
        Assert.Empty(writer.Assignments);
    }

    private async Task<Workflow> PreparedAssignmentAsync(FakeIssueWriter writer)
    {
        var handler = new ResponsesHandler(
            """[{"number":1,"title":"Story A","body":"","labels":[{"name":"story"},{"name":"triaged"}],"milestone":null},{"number":2,"title":"Story B","body":"","labels":[{"name":"story"},{"name":"triaged"}],"milestone":null}]""",
            """[{"number":5,"title":"Next","due_on":null}]""");
        var workflow = new Workflow(Path.Combine(directory, "apply.json"), agent, new HttpClient(handler), writer);
        workflow.SelectRepository(Input(new { repository = "team/product" }));
        await workflow.ImportAsync(CancellationToken.None);
        agent.Response = (draft, cancellation) => Task.FromResult(
            """{"ranked":[{"id":"gh-1","reason":"a"},{"id":"gh-2","reason":"b"}],"storyQuestions":[],"assignments":["gh-1","gh-2"]}""");
        workflow.Authorize(Input(new { revision = 1, acknowledgeSoftCap = true }));
        await workflow.TriageAsync(Input(new { revision = 1 }));
        return workflow;
    }

    private sealed class FakeProductOwner : IProductOwner
    {
        public int Calls { get; private set; }
        public Func<Draft, CancellationToken, Task<string>>? Response { get; set; }

        public Task<string> RunAsync(Draft draft, Milestone? nextMilestone, CancellationToken cancellation)
        {
            Calls++;
            if (Response is not null) return Response(draft, cancellation);
            return Task.FromResult(JsonSerializer.Serialize(new { ranked = draft.Candidates.Where(item => item.Triaged).Select(item => new { id = item.Id, reason = "High impact" }), storyQuestions = Array.Empty<object>(), assignments = Array.Empty<string>() }));
        }
    }

    private sealed class PendingIssueHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ResponsesHandler(params string[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[Calls++], System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeIssueWriter : IGitHubIssueWriter
    {
        public Dictionary<int, RemoteIssue> Issues { get; } = new();
        public List<int> Reads { get; } = [];
        public List<int> Assignments { get; } = [];
        public HashSet<int> FailAssignments { get; } = [];

        public Task<RemoteIssue> ReadAsync(string repository, int number, CancellationToken cancellation)
        {
            Reads.Add(number);
            return Task.FromResult(Issues.GetValueOrDefault(number, new RemoteIssue(true, null)));
        }

        public Task AssignAsync(string repository, int number, int milestoneNumber, CancellationToken cancellation)
        {
            if (FailAssignments.Contains(number)) throw new WorkflowException("Simulated GitHub failure", 502);
            Assignments.Add(number);
            return Task.CompletedTask;
        }
    }
}

/// <summary>Checks the authenticated repository listing without running gh.</summary>
public sealed class GitHubRepositoriesTests
{
    [Fact]
    public async Task GivenUnavailableCli_WhenListing_ProvidesManualFallbackMessage()
    {
        var catalog = new GitHubRepositories((_, _) => throw new Win32Exception());

        var error = await Assert.ThrowsAsync<WorkflowException>(() => catalog.ListAsync("team", CancellationToken.None));

        Assert.Contains("Enter owner/name manually", error.Message);
    }

    [Fact]
    public async Task GivenRecentlyUpdatedRepositories_WhenListing_PreservesOrderAndDistinctNames()
    {
        var catalog = new GitHubRepositories((_, _) => Task.FromResult("{\"data\":{\"repositoryOwner\":{\"repositories\":{\"nodes\":[" +
            "{\"nameWithOwner\":\"Team/Product\"},{\"nameWithOwner\":\"team/product\"},{\"nameWithOwner\":\"Team/other\"}]}}}}"));

        var listing = await catalog.ListAsync("Team", CancellationToken.None);

        Assert.Equal("Team", listing.Owner);
        Assert.Equal(["Team/Product", "Team/other"], listing.Repositories);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":{\"repositoryOwner\":{\"repositories\":{\"nodes\":[null]}}}}")]
    [InlineData("{\"data\":{\"repositoryOwner\":{\"repositories\":{\"nodes\":[{\"name\":\"not-full-name\"}]}}}}")]
    public async Task GivenInvalidCliOutput_WhenListing_RejectsResponse(string output)
    {
        var catalog = new GitHubRepositories((_, _) => Task.FromResult(output));

        var error = await Assert.ThrowsAsync<WorkflowException>(() => catalog.ListAsync("team", CancellationToken.None));

        Assert.Equal(502, error.Status);
    }

    [Fact]
    public async Task GivenNoOwner_WhenListing_DefaultsToSignedInAccount()
    {
        string? queriedOwner = null;
        var catalog = new GitHubRepositories((owner, _) =>
        {
            queriedOwner = owner;
            return Task.FromResult("{\"data\":{\"repositoryOwner\":{\"repositories\":{\"nodes\":[]}}}}");
        }, _ => Task.FromResult("signed-in-owner"));

        var listing = await catalog.ListAsync(null, CancellationToken.None);

        Assert.Equal("signed-in-owner", listing.Owner);
        Assert.Equal("signed-in-owner", queriedOwner);
    }

    [Theory]
    [InlineData("bad/owner")]
    [InlineData("-bad")]
    [InlineData("owner name")]
    public async Task GivenInvalidOwner_WhenListing_RejectsInput(string owner)
    {
        var catalog = new GitHubRepositories((_, _) => throw new Exception("Query must not run"));

        var error = await Assert.ThrowsAsync<WorkflowException>(() => catalog.ListAsync(owner, CancellationToken.None));

        Assert.Equal(400, error.Status);
    }
}