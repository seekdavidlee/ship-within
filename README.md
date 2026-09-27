---
title: Ship Within Product Owner Starter
description: Local kickoff and read-only issue prioritization with a guarded Copilot planning attempt.
---

## Get Started

Install .NET SDK 10, Node.js 18+, npm, and PowerShell 7.4+. For automatic
repository selection, install and sign in to GitHub CLI (`gh auth login`). For
live Product Owner triage, also install and sign in to GitHub Copilot CLI. From
the repository root:

```powershell
./Start-App.ps1
```

Open the frontend URL printed by the script (normally <http://127.0.0.1:5173>).
The script picks free loopback ports if the defaults (5173 for React and 4174 for the
API) are occupied. The .NET API and React dev server run in separate terminals. Press
Enter in the launcher to stop both services.

The ASP.NET Core 10 REST API lives in `src/backend`, and the React TypeScript
frontend lives in `src/frontend`. The launcher installs missing frontend packages,
builds the API, and records its managed terminal IDs under ignored `data/`.
The .NET Copilot SDK uses a separately installed CLI; the launcher sets
`COPILOT_CLI_PATH` when `copilot.exe` is available. Opening the app, editing
objectives, and importing public issues do not require a model call.

## Workflow

1. Open **Settings > Agent models** to choose the Product Owner model. The selection is stored locally per agent and applies to future planning attempts. The Owner field defaults to your signed-in `gh` account. Enter another user or organization and press Enter or select Search to list that owner's 10 most recently updated repositories visible to you. Choose a repository, or enter `owner/name` manually and select it if it is not listed or discovery is unavailable. Selection is saved immediately and shown as a repository link; start a new kickoff to choose a different repository. Enter an objective, acceptance criteria, recorded USD budget, planning AI Credit allocation, and future deadline. Add defects or features manually. Optionally save first, then import up to 30 open public GitHub issues. Private repositories require manual input for issues.
2. Save the objective and review its revision. Acknowledge that AI Credits are a soft cap and that the USD amount is **not** enforced, then authorize the saved revision.
3. Select **Run Product Owner** for one model attempt. The no-tools session ranks only the supplied candidates. Review and reorder the unapproved draft locally; no GitHub issues are changed. Select **New kickoff** to archive the attempt. When prior usage is unknown, confirm that you checked it outside the app; the decision is recorded with the archived attempt before another allocation and authorization.

An edited objective, candidate, allocation, or deadline needs a new save and authorization. Changing an agent model before a run creates a new revision and clears any previous authorization; after a run, it applies only to future kickoffs. An admitted attempt cannot be retried under the same allocation, even when the result fails, expires, or its usage is unknown. After a restart, an in-flight attempt is marked interrupted, never replayed. Unknown usage blocks a new kickoff or later authorization until you explicitly record external reconciliation, including for earlier archived attempts. This attestation does not verify billing: cancellation cannot undo charges, the SDK AI Credit ceiling can overshoot, and the USD amount is not enforced.

The loopback API is single-user, unauthenticated, and not suitable for shared hosting. Drafts, proposal history, and SDK session data remain in the ignored `data/` directory on this machine. Public GitHub reads and manual candidates require no Copilot session; a model call occurs only after explicit authorization and Run. The runtime has no repository write tools or GitHub mutation endpoints. Existing `data/workspace.json` state is reused after migration.

## Checks

```powershell
dotnet test src/backend.Tests/ShipWithin.Api.Tests.csproj
npm run build --prefix src/frontend
```

The .NET tests use a fake Product Owner and temporary state; they do not spend
AI Credits or require a Copilot login. A live model call is intentionally not
part of the automated checks.
