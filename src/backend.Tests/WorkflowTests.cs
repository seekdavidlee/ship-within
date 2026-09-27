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
        repository = "team/product", objective, criteria = "Customer blockers", budgetUsd = 10,
        planningCredits = 2, deadline = DateTimeOffset.UtcNow.AddMinutes(2).ToString("O"), model = "gpt-4.1",
        candidates = new[] { new { title = "Sign-in fails", kind = "defect", source = "manual", body = "Cannot sign in" } }
    };

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
    public void GivenInvalidDraft_WhenSaving_RejectsObjective()
    {
        Assert.Throws<WorkflowException>(() => sut.Save(Input(DraftInput(""))));
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

    private sealed class FakeProductOwner : IProductOwner
    {
        public int Calls { get; private set; }
        public Func<Draft, CancellationToken, Task<string>>? Response { get; set; }

        public Task<string> RunAsync(Draft draft, CancellationToken cancellation)
        {
            Calls++;
            if (Response is not null) return Response(draft, cancellation);
            return Task.FromResult(JsonSerializer.Serialize(new { ranked = draft.Candidates.Select(item => new { id = item.Id, reason = "High impact" }), questions = Array.Empty<string>() }));
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