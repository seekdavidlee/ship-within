---
title: Ship Within Product Owner
description: Local story review, triaged issue prioritization, and confirmed GitHub milestone assignment.
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
review settings, and importing public issues do not require a model call.

## Workflow

1. Choose a repository in the picker or enter `owner/name`. The Owner field defaults to your signed-in `gh` account when available. **Import public issues** works immediately after selecting a repository; no objective or acceptance criteria are needed. Up to 30 open issues are imported; pull requests are excluded. A `story` label marks stories and only the `triaged` label marks an issue as triaged. Correct type or triage locally, and save any edits before importing again. The next open milestone is selected by earliest due date, then milestone number; if none is open, assignment is unavailable. Private repositories require manual issue input.
2. Review the saved revision, AI Credits allocation, and future deadline. AI Credits are the only budget input. For reporting, assume **1 GitHub AI credit = $0.01 USD**; the displayed USD allocation estimate is not actual spend or a separate budget. Acknowledge that AI Credits are a soft cap; then authorize a single planning attempt. Select **Run Product Owner**. The no-tools session asks story-linked questions and ranks only triaged issues. Review and reorder priorities locally. Stories with outstanding questions or an existing milestone cannot be proposed for assignment.
3. When reviewed stories are eligible, check the assignment approval and confirm the exact story IDs and milestone in the dialog. Only then does the backend read each remote issue and apply its milestone through your signed-in `gh` account. Closed or already-milestoned remote issues are rejected, and successful and failed assignments are persisted separately for retry. A remote issue can still change between the preflight read and update. No questions are posted as GitHub comments. Select **New kickoff** to archive the attempt; reconcile unknown AI Credit usage outside the app before another attempt.

An edited candidate, allocation, or deadline needs a new save and authorization. Changing an agent model before a run creates a new revision and clears any previous authorization; after a run, it applies only to future kickoffs. An admitted attempt cannot be retried under the same allocation, even when the result fails, expires, or its usage is unknown. After a restart, an in-flight attempt is marked interrupted, never replayed. Unknown usage blocks a new kickoff or later authorization until you explicitly record external reconciliation, including for earlier archived attempts. This attestation does not verify billing: cancellation cannot undo charges, the SDK AI Credit ceiling can overshoot, and USD estimates use the reporting assumption rather than verified charges.

The loopback API is single-user, unauthenticated, and not suitable for shared hosting. Drafts, proposal history, and SDK session data remain in the ignored `data/` directory on this machine. Public GitHub reads and manual candidates require no Copilot session; a model call occurs only after explicit authorization and Run. The model has no repository write tools. The separate apply endpoint can change GitHub issues after explicit confirmation; it requires `gh` authentication with write access. Existing `data/workspace.json` state is reused after migration.

## Checks

```powershell
dotnet test src/backend.Tests/ShipWithin.Api.Tests.csproj
npm run build --prefix src/frontend
```

The .NET tests use a fake Product Owner and temporary state; they do not spend
AI Credits or require a Copilot login. A live model call is intentionally not
part of the automated checks.
