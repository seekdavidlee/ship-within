using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShipWithin.Api;

/// <summary>A rejected workflow operation.</summary>
public sealed class WorkflowException(string message, int status = 400) : Exception(message)
{
    /// <summary>The HTTP status code.</summary>
    public int Status { get; } = status;
}

/// <summary>A defect or feature under consideration.</summary>
public sealed record Candidate(string Id, string Kind, string Source, string Title, string Body, string? Url,
    bool Triaged = true, int? MilestoneNumber = null);

/// <summary>The next available repository milestone.</summary>
public sealed record Milestone(int Number, string Title, DateTimeOffset? DueOn);

/// <summary>A saved objective and its candidates.</summary>
public sealed record Draft(string Repository, string Objective, string Criteria, DateTimeOffset Deadline,
    int PlanningCredits, string Model, List<Candidate> Candidates, int Revision)
{
    /// <summary>The credit allocation's reporting estimate at $0.01 USD per AI credit, not actual spend.</summary>
    public decimal EstimatedCostUsd => PlanningCredits * 0.01m;
}

/// <summary>A proposed priority and rationale.</summary>
public sealed record RankedItem(string Id, string Reason);

/// <summary>A clarification tied to an imported story.</summary>
public sealed record StoryQuestion(string Id, string Question);

/// <summary>An unapproved ranking for human review.</summary>
public sealed record Proposal(List<RankedItem> Ranked, List<string>? Questions = null,
    List<StoryQuestion>? StoryQuestions = null, List<string>? Assignments = null);

/// <summary>An external usage attestation.</summary>
public sealed record UsageReconciliation(DateTimeOffset ConfirmedAt, string Method);

/// <summary>One non-retryable planning attempt.</summary>
public sealed record PlanningRun(string Status, DateTimeOffset StartedAt, int Revision, int CreditLimit, string Usage,
    string? Message = null, UsageReconciliation? UsageReconciliation = null);

/// <summary>An archived kickoff.</summary>
public sealed record HistoryEntry(Draft Draft, PlanningRun? Run, Proposal? Proposal);

/// <summary>Persisted local workflow state.</summary>
public sealed record WorkspaceState(Draft? Draft, int? AuthorizedRevision, PlanningRun? Run, Proposal? Proposal,
    List<HistoryEntry> History, Dictionary<string, string>? AgentModels = null, string? Repository = null,
    Milestone? NextMilestone = null, List<string>? AppliedStories = null, Dictionary<string, string>? AssignmentErrors = null);

/// <summary>Coordinates local kickoff, authorization, and a single planning attempt.</summary>
public sealed class Workflow
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private readonly string file;
    private readonly IProductOwner agent;
    private readonly HttpClient http;
    private readonly IGitHubIssueWriter writer;
    private WorkspaceState state;
    private bool active;
    private bool applying;

    /// <summary>Loads local state without retrying interrupted work.</summary>
    public Workflow(string file, IProductOwner agent, HttpClient? issuesClient = null, IGitHubIssueWriter? issueWriter = null)
    {
        this.file = file;
        this.agent = agent;
        http = issuesClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        writer = issueWriter ?? new GitHubIssueWriter();
        if (File.Exists(file))
        {
            var saved = JsonNode.Parse(File.ReadAllText(file), new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject
                ?? throw new InvalidDataException("Invalid workspace state.");
            NormalizeLegacyCandidates(saved["draft"] as JsonObject);
            if (saved["history"] is JsonArray history)
                foreach (var entry in history) NormalizeLegacyCandidates(entry?["draft"] as JsonObject);
            state = saved.Deserialize<WorkspaceState>(JsonOptions) ?? throw new InvalidDataException("Invalid workspace state.");
        }
        else state = new(null, null, null, null, []);
        state = state with { History = state.History ?? [], AgentModels = state.AgentModels ?? new() { ["productOwner"] = state.Draft?.Model ?? "gpt-4.1" }, Repository = state.Repository ?? state.Draft?.Repository };
        if (state.Run?.Status is "running" or "stopping")
        {
            state = state with { Run = state.Run with { Status = "interrupted", Usage = "unknown", Message = "Worker stopped; charges may have occurred. No automatic retry." } };
            Persist();
        }
    }

    private static void NormalizeLegacyCandidates(JsonObject? draft)
    {
        if (draft?["candidates"] is not JsonArray candidates) return;
        foreach (var item in candidates)
            if (item is JsonObject candidate && candidate["triaged"] is null && (string?)candidate["source"] == "github")
                candidate["triaged"] = false;
    }

    /// <summary>Returns a stable view of the workspace.</summary>
    public WorkspaceState Snapshot() { lock (gate) return Clone(); }

    /// <summary>Binds the current kickoff to a repository before its objective is saved.</summary>
    public WorkspaceState SelectRepository(JsonElement input)
    {
        lock (gate)
        {
            var repository = Text(input, "repository", 120, "Repository");
            if (!Regex.IsMatch(repository, @"^[a-z\d_.-]+/[a-z\d_.-]+$", RegexOptions.IgnoreCase) || repository.Contains(".."))
                throw new WorkflowException("Repository must be owner/name.");
            if (state.Repository is not null)
            {
                if (!string.Equals(state.Repository, repository, StringComparison.OrdinalIgnoreCase))
                    throw new WorkflowException("Start a new kickoff to change repositories.", 409);
                return Clone();
            }
            state = state with { Repository = repository };
            Persist();
            return Clone();
        }
    }

    /// <summary>Sets the model used for future runs of a known agent.</summary>
    public WorkspaceState SetAgentModel(JsonElement input)
    {
        lock (gate)
        {
            if (Text(input, "agent", 80, "Agent") != "productOwner") throw new WorkflowException("Unknown agent.");
            var model = Text(input, "model", 80, "Model");
            if (!Regex.IsMatch(model, @"^[a-z\d][a-z\d._-]*$", RegexOptions.IgnoreCase)) throw new WorkflowException("Invalid model name.");
            if (state.AgentModels!["productOwner"] == model) return Clone();
            state = state with { AgentModels = new Dictionary<string, string>(state.AgentModels) { ["productOwner"] = model } };
            if (state.Draft is not null && state.Run is null && !active)
                state = state with { Draft = state.Draft with { Model = model, Revision = state.Draft.Revision + 1 }, AuthorizedRevision = null };
            Persist();
            return Clone();
        }
    }

    /// <summary>Saves validated review settings as a new revision.</summary>
    public WorkspaceState Save(JsonElement input)
    {
        lock (gate)
        {
            if (state.Run is not null || active) throw new WorkflowException("This objective already used its planning attempt.", 409);
            var repository = Text(input, "repository", 120, "Repository");
            if (!Regex.IsMatch(repository, @"^[a-z\d_.-]+/[a-z\d_.-]+$", RegexOptions.IgnoreCase) || repository.Contains(".."))
                throw new WorkflowException("Repository must be owner/name.");
            if (state.Repository is not null && !string.Equals(state.Repository, repository, StringComparison.OrdinalIgnoreCase))
                throw new WorkflowException("Start a new kickoff to change repositories.", 409);
            if (!DateTimeOffset.TryParse(Value(input, "deadline").ToString(), out var deadline) || deadline <= DateTimeOffset.UtcNow)
                throw new WorkflowException("Deadline must be a future date.");
            if (!int.TryParse(Value(input, "planningCredits").ToString(), out var credits) || credits is < 1 or > 100)
                throw new WorkflowException("Planning credits must be from 1 to 100.");
            state = state with
            {
                Draft = new Draft(repository, OptionalText(input, "objective", 2000), OptionalText(input, "criteria", 2000),
                    deadline, credits, state.AgentModels!["productOwner"], ValidateCandidates(Value(input, "candidates")), (state.Draft?.Revision ?? 0) + 1),
                AuthorizedRevision = null, Proposal = null, Repository = state.Repository ?? repository
            };
            Persist();
            return Clone();
        }
    }

    /// <summary>Imports public issues and open milestones for the selected repository.</summary>
    public Task<WorkspaceState> ImportAsync(CancellationToken cancellation) => ImportAsync(cancellation, false);

    private async Task<WorkspaceState> ImportAsync(CancellationToken cancellation, bool forPlanningRun)
    {
        Draft? original;
        string repository;
        bool ImportBlocked() => forPlanningRun ? !active || state.Run?.Status != "running" : state.Run is not null || active;
        lock (gate)
        {
            if (state.Repository is null || ImportBlocked()) throw new WorkflowException("Choose a repository before importing issues.", 409);
            original = state.Draft;
            repository = state.Repository;
        }
        var parts = repository.Split('/');
        var apiRoot = $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}";
        var issues = new List<Candidate>();
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{apiRoot}/issues?state=open&per_page=100&page={page}");
            request.Headers.Add("User-Agent", "ship-within-local");
            request.Headers.Add("Accept", "application/vnd.github+json");
            using var response = await http.SendAsync(request, cancellation);
            if (!response.IsSuccessStatusCode) throw new WorkflowException($"Public issue import failed (GitHub HTTP {(int)response.StatusCode}). Private repositories require manual input.", 502);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new WorkflowException("Unexpected GitHub issue response.", 502);
            issues.AddRange(document.RootElement.EnumerateArray().Where(item => !item.TryGetProperty("pull_request", out _)).Select(item =>
            {
                var labels = Value(item, "labels");
                var names = labels.ValueKind == JsonValueKind.Array ? labels.EnumerateArray().Select(label => Value(label, "name").ToString()).ToList() : [];
                var kind = names.Any(name => Regex.IsMatch(name, @"^(story|user.story)$", RegexOptions.IgnoreCase)) ? "story"
                    : names.Any(name => Regex.IsMatch(name, "bug|defect", RegexOptions.IgnoreCase)) ? "defect"
                    : names.Any(name => Regex.IsMatch(name, "feature|enhancement", RegexOptions.IgnoreCase)) ? "feature" : "unclassified";
                var milestone = Value(item, "milestone");
                return new Candidate("gh-" + Value(item, "number"), kind, "github", Value(item, "title").ToString(),
                    Value(item, "body").ToString(), Value(item, "html_url").ToString(),
                    names.Any(name => string.Equals(name, "triaged", StringComparison.OrdinalIgnoreCase)),
                    milestone.ValueKind == JsonValueKind.Object ? Value(milestone, "number").GetInt32() : null);
            }));
            lock (gate)
            {
                if (ImportBlocked() || !ReferenceEquals(state.Draft, original) || state.Repository != repository)
                    throw new WorkflowException("Repository or issue revision changed during import. Review the current revision.", 409);
            }
            if (document.RootElement.GetArrayLength() < 100) break;
        }
        using var milestonesRequest = new HttpRequestMessage(HttpMethod.Get, $"{apiRoot}/milestones?state=open&per_page=100");
        milestonesRequest.Headers.Add("User-Agent", "ship-within-local");
        milestonesRequest.Headers.Add("Accept", "application/vnd.github+json");
        using var milestonesResponse = await http.SendAsync(milestonesRequest, cancellation);
        if (!milestonesResponse.IsSuccessStatusCode) throw new WorkflowException($"Public milestone import failed (GitHub HTTP {(int)milestonesResponse.StatusCode}).", 502);
        using var milestonesDocument = JsonDocument.Parse(await milestonesResponse.Content.ReadAsStringAsync(cancellation));
        if (milestonesDocument.RootElement.ValueKind != JsonValueKind.Array) throw new WorkflowException("Unexpected GitHub milestone response.", 502);
        var nextMilestone = milestonesDocument.RootElement.EnumerateArray().Select(item => new Milestone(
            Value(item, "number").GetInt32(), Value(item, "title").ToString(),
            DateTimeOffset.TryParse(Value(item, "due_on").ToString(), out var dueOn) ? dueOn : null))
            .OrderBy(item => item.DueOn ?? DateTimeOffset.MaxValue).ThenBy(item => item.Number).FirstOrDefault();
        lock (gate)
        {
            if (ImportBlocked() || !ReferenceEquals(state.Draft, original) || state.Repository != repository)
                throw new WorkflowException("Repository or issue revision changed during import. Review the current revision.", 409);
            cancellation.ThrowIfCancellationRequested();
            var combined = (original?.Candidates.Where(item => item.Source != "github") ?? []).Concat(issues).ToList();
            var validated = ValidateCandidates(JsonSerializer.SerializeToElement(combined, JsonOptions));
            var draft = original is null ? new Draft(repository, "", "", DateTimeOffset.UtcNow.AddDays(1), 2,
                state.AgentModels!["productOwner"], validated, 1) : original with { Candidates = validated, Revision = original.Revision + (forPlanningRun ? 0 : 1) };
            state = state with { Draft = draft, AuthorizedRevision = null, Proposal = null, NextMilestone = nextMilestone };
            Persist();
            return Clone();
        }
    }

    /// <summary>Authorizes one planning attempt for the saved revision.</summary>
    public WorkspaceState Authorize(JsonElement input)
    {
        lock (gate)
        {
            if (state.History.Any(entry => Unreconciled(entry.Run))) throw new WorkflowException("Reconcile prior unknown usage before authorizing another attempt.", 409);
            if (state.Draft is null || state.Run is not null || active || Value(input, "revision").ToString() != state.Draft.Revision.ToString())
                throw new WorkflowException("Save and review the current revision first.", 409);
            if (state.Draft.Deadline <= DateTimeOffset.UtcNow) throw new WorkflowException("Deadline has expired.", 409);
            if (Value(input, "acknowledgeSoftCap").ValueKind != JsonValueKind.True)
                throw new WorkflowException("Acknowledge that the AI Credits soft cap can overshoot.");
            state = state with { AuthorizedRevision = state.Draft.Revision };
            Persist();
            return Clone();
        }
    }

    /// <summary>Consumes authorization before dispatch and discards late responses.</summary>
    public async Task<WorkspaceState> TriageAsync(JsonElement input)
    {
        Draft draft;
        lock (gate)
        {
            if (active || state.Run is not null || state.Draft is null || state.AuthorizedRevision != state.Draft.Revision ||
                Value(input, "revision").ToString() != state.Draft.Revision.ToString())
                throw new WorkflowException("This revision has no unused planning authorization.", 409);
            draft = state.Draft;
            if (draft.Deadline <= DateTimeOffset.UtcNow) throw new WorkflowException("Deadline has expired.", 409);
            active = true;
            state = state with { AuthorizedRevision = null, Run = new PlanningRun("running", DateTimeOffset.UtcNow, draft.Revision, draft.PlanningCredits, "not-started", "Importing the repository backlog.") };
            Persist();
        }
        var remaining = draft.Deadline - DateTimeOffset.UtcNow;
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        using var registration = deadline.Token.Register(() =>
        {
            lock (gate)
            {
                if (state.Run?.Status == "running")
                {
                    state = state with { Run = state.Run with { Status = "stopping", Message = state.Run.Usage == "not-started"
                        ? "Deadline reached during backlog import; cancellation requested."
                        : "Deadline reached; cancellation requested. Charges may already have occurred." } };
                    Persist();
                }
            }
        });
        try
        {
            var imported = await ImportAsync(deadline.Token, true);
            draft = imported.Draft!;
            if (draft.Candidates.Count == 0) throw new WorkflowException("No open issues are available for Product Owner review.");
            deadline.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                state = state with { Run = state.Run! with { Usage = "unknown", Message = "Product Owner planning is underway." } };
                Persist();
            }
            var raw = await agent.RunAsync(draft, imported.NextMilestone, deadline.Token).WaitAsync(deadline.Token);
            if (deadline.IsCancellationRequested || DateTimeOffset.UtcNow >= draft.Deadline)
                throw new WorkflowException("Late response was discarded.", 409);
            var proposal = ValidateProposal(raw, draft.Candidates, imported.NextMilestone);
            lock (gate)
            {
                state = state with { Proposal = proposal, Run = state.Run! with { Status = "completed", Message = "Draft proposal ready for human review. Usage has not been reconciled." } };
                Persist();
                return Clone();
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                state = state with { Run = state.Run! with { Status = deadline.IsCancellationRequested ? "expired" : "failed", Message = error is OperationCanceledException ? "Deadline reached; cancellation requested." : error.Message } };
                Persist();
            }
            throw error is OperationCanceledException ? new WorkflowException("Deadline reached; cancellation requested.", 409) : error;
        }
        finally { lock (gate) active = false; }
    }

    /// <summary>Archives after external reconciliation of unknown usage.</summary>
    public WorkspaceState New(JsonElement input)
    {
        lock (gate)
        {
            if (active || applying || state.Run?.Status is "running" or "stopping") throw new WorkflowException("Wait for the current attempt to settle before starting a new kickoff.", 409);
            var unknown = new[] { state.Run }.Concat(state.History.Select(entry => entry.Run)).Any(Unreconciled);
            if (unknown && Value(input, "reconcileUsage").ValueKind != JsonValueKind.True)
                throw new WorkflowException("Confirm external reconciliation of prior unknown usage before starting a new kickoff.", 409);
            var decision = new UsageReconciliation(DateTimeOffset.UtcNow, "external");
            PlanningRun? Reconcile(PlanningRun? run) => Unreconciled(run) ? run! with { UsageReconciliation = decision } : run;
            var history = state.History.Select(entry => entry with { Run = Reconcile(entry.Run) }).ToList();
            if (state.Draft is not null) history.Insert(0, new HistoryEntry(state.Draft, Reconcile(state.Run), state.Proposal));
            state = new WorkspaceState(null, null, null, null, history.Take(10).ToList(), state.AgentModels);
            Persist();
            return Clone();
        }
    }

    /// <summary>Changes only the unapproved proposal order.</summary>
    public WorkspaceState Order(JsonElement input)
    {
        lock (gate)
        {
            var ids = Value(input, "ids");
            if (applying || state.Proposal is null || ids.ValueKind != JsonValueKind.Array || ids.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new WorkflowException("Order must contain every proposed candidate once.");
            var order = ids.EnumerateArray().Select(item => item.GetString()!).ToList();
            if (order.Count != state.Proposal.Ranked.Count || order.Distinct().Count() != order.Count ||
                order.Any(id => state.Proposal.Ranked.All(item => item.Id != id))) throw new WorkflowException("Order must contain every proposed candidate once.");
            state = state with { Proposal = state.Proposal with { Ranked = state.Proposal.Ranked.OrderBy(item => order.IndexOf(item.Id)).ToList() } };
            Persist();
            return Clone();
        }
    }

    /// <summary>Applies explicitly acknowledged story assignments after a fresh remote check.</summary>
    public async Task<WorkspaceState> ApplyAsync(JsonElement input, CancellationToken cancellation)
    {
        string repository;
        int milestoneNumber;
        List<string> ids;
        lock (gate)
        {
            if (applying || state.Run?.Status != "completed" || state.Proposal?.Assignments is null || state.NextMilestone is null ||
                state.Draft is null || Value(input, "acknowledge").ValueKind != JsonValueKind.True ||
                Value(input, "revision").ToString() != state.Draft.Revision.ToString() ||
                Value(input, "milestoneNumber").ToString() != state.NextMilestone.Number.ToString())
                throw new WorkflowException("Review and acknowledge the current revision and milestone before assigning stories.", 409);
            var requested = Value(input, "ids");
            if (requested.ValueKind != JsonValueKind.Array || requested.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new WorkflowException("Provide the exact reviewed story IDs.");
            ids = requested.EnumerateArray().Select(item => item.GetString()!).ToList();
            var remaining = state.Proposal.Assignments.Except(state.AppliedStories ?? []).ToHashSet();
            if (ids.Count == 0 || ids.Count != ids.Distinct().Count() || !remaining.SetEquals(ids) ||
                ids.Any(id => state.Draft.Candidates.All(item => item.Id != id || item.Kind != "story" || !item.Triaged ||
                    item.Source != "github" || item.MilestoneNumber is not null) ||
                    state.Proposal.StoryQuestions?.Any(question => question.Id == id) == true))
                throw new WorkflowException("Acknowledged IDs must match all unapplied eligible stories.", 409);
            repository = state.Repository!;
            milestoneNumber = state.NextMilestone.Number;
            applying = true;
        }
        try
        {
            foreach (var id in ids)
            {
                cancellation.ThrowIfCancellationRequested();
                string? error = null;
                try
                {
                    var remote = await writer.ReadAsync(repository, int.Parse(id[3..], CultureInfo.InvariantCulture), cancellation);
                    if (!remote.Open || remote.MilestoneNumber is not null) error = "Remote issue is closed or already assigned. Refresh before retrying.";
                    else await writer.AssignAsync(repository, int.Parse(id[3..], CultureInfo.InvariantCulture), milestoneNumber, cancellation);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception) { error = "GitHub assignment failed. Check access and refresh the remote issue before retrying."; }
                lock (gate)
                {
                    var applied = state.AppliedStories?.ToList() ?? [];
                    var errors = new Dictionary<string, string>(state.AssignmentErrors ?? []);
                    if (error is null) { applied.Add(id); errors.Remove(id); }
                    else errors[id] = error;
                    state = state with { AppliedStories = applied, AssignmentErrors = errors };
                    Persist();
                }
            }
            return Snapshot();
        }
        finally { lock (gate) applying = false; }
    }

    /// <summary>Validates a model response against the authorized candidates.</summary>
    public static Proposal ValidateProposal(string raw, List<Candidate> candidates, Milestone? nextMilestone = null)
    {
        Proposal? proposal;
        try { proposal = JsonSerializer.Deserialize<Proposal>(raw.Trim().Trim('`').Replace("json\n", ""), JsonOptions); }
        catch (JsonException) { throw new WorkflowException("The Product Owner returned invalid JSON.", 502); }
        var triaged = candidates.Where(item => item.Triaged).Select(item => item.Id).ToHashSet();
        var stories = candidates.Where(item => item.Kind == "story").Select(item => item.Id).ToHashSet();
        var assignableStories = candidates.Where(item => item.Kind == "story" && item.Triaged && item.Source == "github").ToDictionary(item => item.Id);
        if (proposal?.Ranked is null || proposal.StoryQuestions is null || proposal.Assignments is null ||
            proposal.Ranked.Count != triaged.Count ||
            proposal.StoryQuestions.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) || !stories.Contains(item.Id) || string.IsNullOrWhiteSpace(item.Question) || item.Question.Length > 300))
            throw new WorkflowException("The proposal omitted triaged issues or story questions are invalid.", 502);
        if (proposal.Ranked.Any(item => item is null || !triaged.Contains(item.Id) || string.IsNullOrWhiteSpace(item.Reason) || item.Reason.Length > 600) ||
            proposal.Ranked.Select(item => item.Id).Distinct().Count() != triaged.Count)
            throw new WorkflowException("The proposal contains unknown, duplicate, or invalid priorities.", 502);
        if (proposal.StoryQuestions.Select(item => item.Id + "\0" + item.Question).Distinct().Count() != proposal.StoryQuestions.Count ||
            proposal.Assignments.Count != proposal.Assignments.Distinct().Count() ||
            proposal.Assignments.Any(id => id is null || !assignableStories.TryGetValue(id, out var story) || story.MilestoneNumber is not null ||
                proposal.StoryQuestions.Any(question => question.Id == id)) ||
            (nextMilestone is null && proposal.Assignments.Count > 0))
            throw new WorkflowException("The proposal contains an ineligible story assignment.", 502);
        return proposal with { Questions = null };
    }

    private static bool Unreconciled(PlanningRun? run) => run?.Usage == "unknown" && run.UsageReconciliation is null;
    private static JsonElement Value(JsonElement input, string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) ? value : default;

    private static string Text(JsonElement input, string name, int limit, string label)
    {
        var value = Value(input, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > limit)
            throw new WorkflowException($"{label} is required (max {limit} characters).");
        return value.GetString()!.Trim();
    }

    private static string OptionalText(JsonElement input, string name, int limit)
    {
        var value = Value(input, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return "";
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > limit)
            throw new WorkflowException($"{name} must be text (max {limit} characters).");
        return value.GetString()!.Trim();
    }

    private static List<Candidate> ValidateCandidates(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array) throw new WorkflowException("Provide candidates as an array.");
        var candidates = input.EnumerateArray().Select((item, index) =>
        {
            if (item.ValueKind != JsonValueKind.Object) throw new WorkflowException("Invalid candidate.");
            var kind = Value(item, "kind").ToString();
            if (kind is not ("story" or "defect" or "feature" or "unclassified")) throw new WorkflowException("Candidate type must be story, defect, feature or unclassified.");
            var source = Value(item, "source").ToString() == "github" ? "github" : "manual";
            var id = source == "github" ? Text(item, "id", 40, "Issue ID") : $"manual-{index + 1}";
            if (source == "github" && !Regex.IsMatch(id, @"^gh-\d+$")) throw new WorkflowException("Invalid GitHub issue ID.");
            var url = Value(item, "url").ToString();
            var body = Value(item, "body").ToString();
            var triaged = Value(item, "triaged");
            var milestone = Value(item, "milestoneNumber");
            if (triaged.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Undefined) ||
                milestone.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null or JsonValueKind.Undefined))
                throw new WorkflowException("Invalid issue triage or milestone status.");
            return new Candidate(id, kind, source, Text(item, "title", 180, "Candidate title"), body[..Math.Min(1200, body.Length)],
                source == "github" && url.StartsWith("https://github.com/", StringComparison.Ordinal) ? url : null,
                triaged.ValueKind == JsonValueKind.Undefined ? source == "manual" : triaged.ValueKind == JsonValueKind.True,
                milestone.ValueKind == JsonValueKind.Number ? milestone.GetInt32() : null);
        }).ToList();
        if (candidates.Select(item => item.Id).Distinct().Count() != candidates.Count) throw new WorkflowException("Candidate IDs must be unique.");
        return candidates;
    }

    private WorkspaceState Clone() => JsonSerializer.Deserialize<WorkspaceState>(JsonSerializer.Serialize(state, JsonOptions), JsonOptions)!;

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, file, true);
    }
}

/// <summary>Remote GitHub issue state used for assignment preflight.</summary>
public sealed record RemoteIssue(bool Open, int? MilestoneNumber);

/// <summary>Reads and assigns issues through a signed-in GitHub account.</summary>
public interface IGitHubIssueWriter
{
    /// <summary>Reads the current remote issue.</summary>
    Task<RemoteIssue> ReadAsync(string repository, int number, CancellationToken cancellation);

    /// <summary>Assigns one issue to a milestone.</summary>
    Task AssignAsync(string repository, int number, int milestoneNumber, CancellationToken cancellation);
}

/// <summary>Uses the signed-in local GitHub CLI for issue updates.</summary>
public sealed class GitHubIssueWriter : IGitHubIssueWriter
{
    /// <inheritdoc />
    public async Task<RemoteIssue> ReadAsync(string repository, int number, CancellationToken cancellation)
    {
        using var document = JsonDocument.Parse(await RunAsync(["api", $"repos/{repository}/issues/{number}"], cancellation));
        var root = document.RootElement;
        if (root.TryGetProperty("pull_request", out _)) throw new WorkflowException("The remote item is a pull request.", 409);
        var milestone = root.GetProperty("milestone");
        return new RemoteIssue(root.GetProperty("state").GetString() == "open",
            milestone.ValueKind == JsonValueKind.Object ? milestone.GetProperty("number").GetInt32() : null);
    }

    /// <inheritdoc />
    public async Task AssignAsync(string repository, int number, int milestoneNumber, CancellationToken cancellation)
    {
        await RunAsync(["api", "-X", "PATCH", $"repos/{repository}/issues/{number}", "-F", $"milestone={milestoneNumber}"], cancellation);
    }

    private static async Task<string> RunAsync(string[] arguments, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new WorkflowException("GitHub CLI could not start.", 502);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            if (process.ExitCode != 0) throw new WorkflowException("GitHub CLI request failed. Check sign-in and repository permissions.", 502);
            return await output;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}

/// <summary>Owner and repositories shown in the repository picker.</summary>
public sealed record RepositoryListing(string Owner, List<string> Repositories);

/// <summary>Lists repositories visible to the signed-in GitHub CLI account.</summary>
public sealed class GitHubRepositories(Func<string, CancellationToken, Task<string>>? list = null,
    Func<CancellationToken, Task<string>>? identity = null)
{
    /// <summary>Returns the ten most recently updated repositories owned by the requested account.</summary>
    public async Task<RepositoryListing> ListAsync(string? owner, CancellationToken cancellation)
    {
        string output;
        try
        {
            owner = string.IsNullOrWhiteSpace(owner) ? await (identity is null
                ? RunGhAsync(["api", "user", "--jq", ".login"], cancellation) : identity(cancellation)) : owner.Trim();
            owner = owner.Trim();
            if (!Regex.IsMatch(owner, @"^[a-z\d](?:[a-z\d-]{0,98}[a-z\d])?$", RegexOptions.IgnoreCase))
                throw new WorkflowException("Enter a valid GitHub owner.", 400);
            output = await (list is null ? QueryAsync(owner, cancellation) : list(owner, cancellation));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new WorkflowException("Repository discovery timed out. Enter owner/name manually or try again.", 502); }
        catch (Win32Exception)
        { throw new WorkflowException("GitHub CLI is unavailable. Enter owner/name manually or install and sign in to gh.", 502); }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement.GetProperty("data").GetProperty("repositoryOwner");
            if (root.ValueKind == JsonValueKind.Null) throw new WorkflowException("GitHub owner not found or inaccessible.", 404);
            var repositories = root.GetProperty("repositories").GetProperty("nodes");
            if (repositories.ValueKind != JsonValueKind.Array) throw new JsonException();
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var repository in repositories.EnumerateArray())
            {
                if (repository.ValueKind != JsonValueKind.Object || !repository.TryGetProperty("nameWithOwner", out var value) || value.ValueKind != JsonValueKind.String)
                    throw new JsonException();
                var name = value.GetString()!;
                if (name.Length <= 120 && Regex.IsMatch(name, @"^[a-z\d_.-]+/[a-z\d_.-]+$", RegexOptions.IgnoreCase) && !name.Contains("..") && seen.Add(name))
                    names.Add(name);
            }
            return new RepositoryListing(owner, names);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new WorkflowException("GitHub CLI returned an invalid repository list. Enter owner/name manually.", 502); }
    }

    private static Task<string> QueryAsync(string owner, CancellationToken cancellation)
    {
        const string query = "query($owner: String!) { repositoryOwner(login: $owner) { ... on User { repositories(first: 10, ownerAffiliations: OWNER, orderBy: {field: UPDATED_AT, direction: DESC}) { nodes { nameWithOwner } } } ... on Organization { repositories(first: 10, orderBy: {field: UPDATED_AT, direction: DESC}) { nodes { nameWithOwner } } } } }";
        return RunGhAsync(["api", "graphql", "-f", $"query={query}", "-f", $"owner={owner}"], cancellation);
    }

    private static async Task<string> RunGhAsync(string[] arguments, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new WorkflowException("GitHub CLI could not start.", 502);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            if (process.ExitCode != 0) throw new WorkflowException("Repository discovery failed. Sign in to gh or enter owner/name manually.", 502);
            return await output;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}