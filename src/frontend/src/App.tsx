import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ArrowDown, ArrowLeft, ArrowUp, ChevronRight, Play, Plus, RotateCcw, Search, Settings, Trash2 } from 'lucide-react';

type Candidate = { id: string; kind: 'defect' | 'feature' | 'unclassified'; source: 'github' | 'manual'; title: string; body: string; url: string | null };
type Draft = { repository: string; objective: string; criteria: string; budgetUsd: string; planningCredits: string; deadline: string; candidates: Candidate[]; revision?: number };
type SavedDraft = Omit<Draft, 'budgetUsd' | 'planningCredits'> & { budgetUsd: number; planningCredits: number; revision: number };
type Run = { status: string; message?: string; usage: string; usageReconciliation?: { confirmedAt: string; method: string } };
type Proposal = { ranked: { id: string; reason: string }[]; questions: string[] };
type Workspace = { repository: string | null; draft: SavedDraft | null; authorizedRevision: number | null; run: Run | null; proposal: Proposal | null; history: { draft: SavedDraft; run: Run | null; proposal: Proposal | null }[]; agentModels: Record<string, string> };
type RepositoryListing = { owner: string; repositories: string[] };
type AvailableModel = { id: string; name: string };

const empty: Workspace = { repository: null, draft: null, authorizedRevision: null, run: null, proposal: null, history: [], agentModels: { productOwner: 'gpt-4.1' } };
const blank = (repository = ''): Draft => ({ repository, objective: '', criteria: '', budgetUsd: '10', planningCredits: '2', deadline: '', candidates: [] });
const validRepository = (repository: string) => /^[a-z\d_.-]+\/[a-z\d_.-]+$/i.test(repository) && !repository.includes('..');
const repositoryFromUrl = () => {
  const repository = new URLSearchParams(window.location.search).get('repository') || '';
  return validRepository(repository) ? repository : '';
};

function localDeadline(value: string) {
  const date = new Date(value);
  return new Date(date.valueOf() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
}

async function api(path: string, method = 'GET', payload?: unknown): Promise<Workspace> {
  const response = await fetch(path, { method, headers: payload === undefined ? {} : { 'Content-Type': 'application/json' }, body: payload === undefined ? undefined : JSON.stringify(payload) });
  const result = await response.json();
  if (!response.ok) throw new Error(result.error || 'Request failed.');
  return result as Workspace;
}

export default function App() {
  const [state, setState] = useState<Workspace>(empty);
  const [page, setPage] = useState(window.location.pathname);
  const [agentModel, setAgentModel] = useState('gpt-4.1');
  const [settingsNotice, setSettingsNotice] = useState('');
  const [availableModels, setAvailableModels] = useState<AvailableModel[]>([]);
  const [modelLoading, setModelLoading] = useState(false);
  const [modelError, setModelError] = useState('');
  const [modelRequest, setModelRequest] = useState(0);
  const [draft, setDraft] = useState<Draft>(() => blank(repositoryFromUrl()));
  const [dirty, setDirty] = useState(false);
  const [ack, setAck] = useState(false);
  const [busy, setBusy] = useState(false);
  const [repositories, setRepositories] = useState<string[]>([]);
  const [owner, setOwner] = useState('');
  const repositoryRequest = useRef(0);
  const [repositoryLoading, setRepositoryLoading] = useState(true);
  const [repositoryError, setRepositoryError] = useState('');
  const [manualRepository, setManualRepository] = useState(false);
  const [notice, setNotice] = useState<{ text: string; tone: string }>({ text: 'Loading workspace...', tone: '' });
  const detailsRepository = state.repository;

  function applyState(next: Workspace) {
    setState(next);
    setAgentModel(next.agentModels.productOwner);
    setDraft(next.draft ? { ...next.draft, budgetUsd: String(next.draft.budgetUsd), planningCredits: String(next.draft.planningCredits), deadline: localDeadline(next.draft.deadline), candidates: structuredClone(next.draft.candidates) } : blank(next.repository || ''));
    setManualRepository(false);
    setDirty(false);
    setAck(false);
    showState(next);
  }

  function showState(next: Workspace) {
    const run = next.run;
    if (run) setNotice({ text: `${run.status.toUpperCase()} | ${run.message || 'Planning attempt underway.'} Usage: ${run.usage}. ${run.status === 'completed' ? 'Proposal requires human review.' : 'No automatic retry.'}`, tone: run.status === 'completed' ? 'success' : run.status === 'running' ? '' : 'error' });
    else if (next.draft && next.authorizedRevision === next.draft.revision) setNotice({ text: `Revision ${next.draft.revision} authorized for one planning attempt.`, tone: 'success' });
    else setNotice({ text: next.draft ? 'Saved. Review this revision before authorizing planning.' : 'Start with a kickoff objective and candidates.', tone: '' });
  }

  async function loadRepositories(requestedOwner: string) {
    const request = ++repositoryRequest.current;
    setRepositoryLoading(true);
    setRepositories([]);
    try {
      const response = await fetch(`/api/repositories${requestedOwner.trim() ? `?owner=${encodeURIComponent(requestedOwner.trim())}` : ''}`);
      const result = await response.json();
      if (request !== repositoryRequest.current) return;
      if (!response.ok) throw new Error(result.error || 'Repository discovery failed.');
      const listing = result as RepositoryListing;
      setOwner(listing.owner);
      setRepositories(listing.repositories);
      setRepositoryError('');
    } catch (error) {
      if (request === repositoryRequest.current) setRepositoryError(error instanceof Error ? error.message : 'Repository discovery failed.');
    } finally { if (request === repositoryRequest.current) setRepositoryLoading(false); }
  }

  useEffect(() => {
    api('/api/state').then(async next => {
      const requested = repositoryFromUrl();
      applyState(!next.repository && requested ? await api('/api/repository', 'POST', { repository: requested }) : next);
    }).catch(error => setNotice({ text: error.message, tone: 'error' }));
    void loadRepositories('');
    const onNavigate = () => {
      setPage(window.location.pathname);
    };
    window.addEventListener('popstate', onNavigate);
    return () => window.removeEventListener('popstate', onNavigate);
  }, []);

  useEffect(() => {
    if (page !== '/settings/agents') return;
    const controller = new AbortController();
    setModelLoading(true);
    setModelError('');
    setAvailableModels([]);
    fetch('/api/models', { signal: controller.signal }).then(async response => {
      const result = await response.json();
      if (!response.ok) throw new Error(result.error || 'Model discovery failed.');
      if (!Array.isArray(result)) throw new Error('Model discovery returned an invalid response.');
      if (!controller.signal.aborted) setAvailableModels(result as AvailableModel[]);
    }).catch(error => {
      if (!controller.signal.aborted) setModelError(error instanceof Error ? error.message : 'Model discovery failed.');
    }).finally(() => { if (!controller.signal.aborted) setModelLoading(false); });
    return () => controller.abort();
  }, [page, modelRequest]);

  function navigate(path: string) {
    if (dirty && !window.confirm('Discard unsaved kickoff changes?')) return;
    if (dirty) applyState(state);
    window.history.pushState(null, '', `${path}${window.location.search}`);
    setPage(path);
    setSettingsNotice('');
  }

  function selectRepository(repository: string) {
    if (!validRepository(repository)) {
      setNotice({ text: 'Repository must be owner/name.', tone: 'error' });
      return;
    }
    void execute(async () => {
      const next = await api('/api/repository', 'POST', { repository });
      const url = new URL(window.location.href);
      url.searchParams.set('repository', next.repository!);
      window.history.pushState(null, '', url);
      applyState(next);
    });
  }

  function change<K extends keyof Draft>(key: K, value: Draft[K]) {
    setDraft(current => ({ ...current, [key]: value }));
    setDirty(true);
    setAck(false);
  }

  function changeCandidate(id: string, key: 'title' | 'body' | 'kind', value: string) {
    change('candidates', draft.candidates.map(item => item.id === id ? { ...item, [key]: value } : item));
  }

  async function execute(work: () => Promise<void>) {
    if (busy) return;
    setBusy(true);
    try { await work(); }
    catch (error) { setNotice({ text: error instanceof Error ? error.message : 'Request failed.', tone: 'error' }); }
    finally { setBusy(false); }
  }

  function save(event: FormEvent) {
    event.preventDefault();
    const values = { ...draft, deadline: new Date(draft.deadline).toISOString(), budgetUsd: Number(draft.budgetUsd), planningCredits: Number(draft.planningCredits) };
    void execute(async () => applyState(await api('/api/draft', 'PUT', values)));
  }

  function archive() {
    const unknown = [state.run, ...state.history.map(item => item.run)].some(run => run?.usage === 'unknown' && !run.usageReconciliation);
    if (!window.confirm(unknown
      ? 'Before another planning attempt, check prior AI Credit usage outside this app. Have you reconciled the unknown usage and chosen to record that decision with the archived attempt? The USD budget is not enforced.'
      : 'Archive this kickoff and start a new one? Previous attempts remain in local history.')) return;
    void execute(async () => {
      const next = await api('/api/new', 'POST', unknown ? { reconcileUsage: true } : undefined);
      const url = new URL(window.location.href);
      url.searchParams.delete('repository');
      window.history.pushState(null, '', url);
      applyState(next);
    });
  }

  const saved = Boolean(state.draft) && !dirty;
  const used = Boolean(state.run);
  const authorized = saved && state.authorizedRevision === state.draft?.revision;
  const choosingManually = manualRepository || Boolean(repositoryError);
  const choices = draft.repository && !repositories.includes(draft.repository) ? [draft.repository, ...repositories] : repositories;
  const modelAvailable = availableModels.some(model => model.id === agentModel);

  if (page === '/settings' || page === '/settings/agents') return <div className="wrap">
    <header>
      <div><p className="eyebrow">Ship Within / Local workspace</p><h1>{page === '/settings' ? 'Settings' : 'Agent models'}</h1></div>
      <button className="btn" type="button" onClick={() => navigate('/')}><ArrowLeft size={16} /> Product Owner</button>
    </header>
    <main className="settings-page">
      {page === '/settings' ? <section aria-labelledby="settings-title">
        <div className="section-head"><h2 id="settings-title">Configuration</h2></div>
        <button className="settings-row" type="button" onClick={() => navigate('/settings/agents')}><span>Agent models</span><ChevronRight size={18} /></button>
      </section> : <section aria-labelledby="agents-title">
        <button className="back-link" type="button" onClick={() => navigate('/settings')}><ArrowLeft size={16} /> Settings</button>
        <div className="section-head"><h2 id="agents-title">Agent models</h2></div>
        <form onSubmit={event => {
          event.preventDefault();
          void execute(async () => {
            const next = await api('/api/settings/agents/model', 'PUT', { agent: 'productOwner', model: agentModel });
            applyState(next);
            setSettingsNotice(next.draft && !next.run ? 'Model saved. Review the updated revision before authorizing a run.' : 'Model saved for future kickoffs.');
          });
        }}>
          <div className="agent-setting"><div><h3>Product Owner</h3><p className="muted small">Used for future planning attempts.</p></div>
            <div><label htmlFor="product-owner-model">Model
              <select id="product-owner-model" required value={agentModel} disabled={modelLoading || Boolean(modelError) || availableModels.length === 0} onChange={event => setAgentModel(event.target.value)}>
                {agentModel && !modelAvailable && <option value={agentModel} disabled>{agentModel} (saved, unavailable)</option>}
                {availableModels.map(model => <option key={model.id} value={model.id}>{model.name} ({model.id})</option>)}
              </select>
            </label>
            {modelLoading && <p className="muted small" role="status">Loading available models...</p>}
            {modelError && <p className="field-hint" role="alert">{modelError} <button className="retry" type="button" onClick={() => setModelRequest(request => request + 1)}>Retry</button></p>}
            {!modelLoading && !modelError && availableModels.length === 0 && <p className="muted small" role="status">No models available. <button className="retry" type="button" onClick={() => setModelRequest(request => request + 1)}>Retry</button></p>}
            {!modelLoading && !modelError && availableModels.length > 0 && !modelAvailable && <p className="field-hint">Choose an available model to replace the saved one.</p>}</div>
          </div>
          <div className="controls"><button className="btn primary" type="submit" disabled={busy || modelLoading || Boolean(modelError) || !modelAvailable || agentModel === state.agentModels.productOwner}>Save model</button></div>
          {settingsNotice && <div className="notice success" role="status"><p>{settingsNotice}</p></div>}
          {notice.tone === 'error' && <div className="notice error" role="alert"><p>{notice.text}</p></div>}
        </form>
      </section>}
    </main>
  </div>;

  return <div className="wrap">
    <header>
      <div><p className="eyebrow">Ship Within / Local workspace</p><h1>Product Owner</h1></div>
      <div className="header-actions">
        {detailsRepository && <button className="btn" type="button" disabled={busy || state.run?.status === 'running' || state.run?.status === 'stopping'} onClick={archive}><RotateCcw size={16} /> New kickoff</button>}
        <button className="btn icon" type="button" title="Settings" aria-label="Settings" onClick={() => navigate('/settings')}><Settings size={18} /></button>
      </div>
    </header>
    <main>
      <div className={`workspace${detailsRepository ? '' : ' first-visit'}`}>
        <section aria-labelledby="kickoff-title">
          <div className="section-head"><h2 id="kickoff-title">{detailsRepository ? 'Repository details' : 'Kickoff'}</h2><span>{dirty ? 'Unsaved changes' : state.draft ? `Revision ${state.draft.revision}` : 'Not saved'}</span></div>
          <form onSubmit={save}>
            <fieldset disabled={used || busy} className="field-grid">
              {!detailsRepository ? <><div className="wide">
                <label htmlFor="repository-owner">Owner</label>
                <div className="owner-search">
                  <input id="repository-owner" value={owner} maxLength={100} autoComplete="off" placeholder="Signed-in GitHub account" onChange={event => {
                    ++repositoryRequest.current;
                    setOwner(event.target.value);
                    setRepositories([]);
                    setRepositoryLoading(false);
                    setRepositoryError('');
                  }} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); void loadRepositories(owner); } }} />
                  <button className="btn icon" type="button" title="Search repositories" aria-label="Search repositories" disabled={repositoryLoading} onClick={() => void loadRepositories(owner)}><Search size={18} /></button>
                </div>
              </div>
              <label>Repository
                <select aria-label="Repository" value={choosingManually ? '__manual__' : ''} onChange={event => {
                  const value = event.target.value;
                  setManualRepository(value === '__manual__');
                  if (value !== '__manual__') selectRepository(value);
                }}>
                  <option value="" disabled>{repositoryLoading ? 'Loading repositories...' : 'Choose a repository'}</option>
                  {choices.map(name => <option key={name} value={name}>{name}</option>)}
                  <option value="__manual__">Enter owner/name manually</option>
                </select>
                {choosingManually && <div className="owner-search"><input required maxLength={120} aria-label="Repository owner/name" value={draft.repository} onChange={event => setDraft(current => ({ ...current, repository: event.target.value }))} placeholder="owner/repository" autoComplete="off" /><button className="btn" type="button" disabled={busy || !validRepository(draft.repository)} onClick={() => selectRepository(draft.repository)}>Select</button></div>}
                {repositoryError && <span className="field-hint" role="status">{repositoryError} <button type="button" className="retry" disabled={repositoryLoading} onClick={() => void loadRepositories(owner)}>Retry</button></span>}
              </label>
              </> : <div className="wide"><span className="field-label">Repository</span><a className="repository-reference" href={`https://github.com/${detailsRepository}`} target="_blank" rel="noreferrer">{detailsRepository}</a></div>}
              {detailsRepository && <>
              <label className="wide">Objective <textarea required maxLength={2000} value={draft.objective} onChange={event => change('objective', event.target.value)} placeholder="What should be prioritized?" /></label>
              <label className="wide">Acceptance criteria <textarea required maxLength={2000} value={draft.criteria} onChange={event => change('criteria', event.target.value)} placeholder="What should a useful ranking account for?" /></label>
              <label>USD budget (record only) <input required type="number" step="0.01" min="0.01" value={draft.budgetUsd} onChange={event => change('budgetUsd', event.target.value)} /></label>
              <label>Planning AI Credits (soft cap) <input required type="number" step="1" min="1" max="100" value={draft.planningCredits} onChange={event => change('planningCredits', event.target.value)} /></label>
              <label className="wide">UTC deadline <input required type="datetime-local" value={draft.deadline} onChange={event => change('deadline', event.target.value)} /></label>
              </>}
            </fieldset>
            {detailsRepository && <div className="controls"><button className="btn" type="submit" disabled={used || busy}>Save objective</button></div>}
          </form>
          {!detailsRepository && notice.tone === 'error' && <div className="notice error" role="alert"><p>{notice.text}</p></div>}
          {detailsRepository && <>
          <div className="notice"><p>AI Credits are a soft limit and may overshoot. The USD budget is recorded but not enforced. A run can incur charges even if it expires or fails.</p></div>
          <label className="check"><input type="checkbox" checked={ack} disabled={used || busy} onChange={event => setAck(event.target.checked)} />I authorize one planning attempt for the saved revision under these limits.</label>
          <div className="controls">
            <button className="btn" type="button" disabled={!saved || used || busy || !draft.candidates.length || !ack} onClick={() => void execute(async () => { const next = await api('/api/authorize', 'POST', { revision: state.draft?.revision, acknowledgeSoftCap: ack }); setState(next); showState(next); })}>Authorize revision</button>
            <button className="btn primary" type="button" disabled={!authorized || used || busy} onClick={() => void execute(async () => {
              const revision = state.draft!.revision;
              setNotice({ text: 'Planning attempt running. Keep this page open for the result.', tone: '' });
              const refresh = window.setInterval(() => { void api('/api/state').then(next => { setState(next); showState(next); }).catch(() => {}); }, 1500);
              try { const next = await api('/api/triage', 'POST', { revision }); setState(next); showState(next); }
              finally { window.clearInterval(refresh); const next = await api('/api/state'); setState(next); showState(next); }
            })}><Play size={16} /> Run Product Owner</button>
          </div>
          <div className={`notice ${notice.tone}`} role="status" aria-live="polite"><p>{notice.text}</p></div>
          </>}
        </section>
        {detailsRepository &&
        <section aria-labelledby="candidates-title">
          <div className="section-head"><h2 id="candidates-title">Candidates</h2><span>{draft.candidates.length} item{draft.candidates.length === 1 ? '' : 's'}</span></div>
          <div className="controls">
            <button className="btn" type="button" disabled={used || busy || draft.candidates.length >= 30} onClick={() => change('candidates', [...draft.candidates, { id: crypto.randomUUID(), source: 'manual', title: '', body: '', kind: 'unclassified', url: null }])}><Plus size={16} /> Add item</button>
            <button className="btn" type="button" disabled={!saved || used || busy} onClick={() => void execute(async () => applyState(await api('/api/issues/import', 'POST')))}>Import public issues</button>
          </div>
          <div className="issue-list">{draft.candidates.length === 0 && <p className="empty">No candidates yet. Add one or import public issues.</p>}
            {draft.candidates.map(item => <div className="issue" key={item.id}>
              <div className="issue-top"><span>{item.source === 'github' ? item.id : 'MANUAL'}</span><button type="button" className="btn icon" title="Remove candidate" aria-label={`Remove ${item.title || 'candidate'}`} disabled={used || busy} onClick={() => change('candidates', draft.candidates.filter(candidate => candidate.id !== item.id))}><Trash2 size={16} /></button></div>
              <div className="issue-grid">
                <label>Title <input value={item.title} required maxLength={180} disabled={used || busy} onChange={event => changeCandidate(item.id, 'title', event.target.value)} /></label>
                <label>Type <select value={item.kind} disabled={used || busy} onChange={event => changeCandidate(item.id, 'kind', event.target.value)}><option value="defect">defect</option><option value="feature">feature</option><option value="unclassified">unclassified</option></select></label>
                <label>Context <textarea value={item.body} maxLength={1200} disabled={used || busy} onChange={event => changeCandidate(item.id, 'body', event.target.value)} /></label>
              </div>
            </div>)}
          </div>
        </section>
        }
      </div>
      {detailsRepository && <>
      <section className="review" aria-labelledby="review-title"><div className="section-head"><h2 id="review-title">Priority review</h2><span>{state.proposal ? 'UNAPPROVED DRAFT' : 'No proposal'}</span></div>
        {!state.proposal && <p className="empty">No proposal yet.</p>}
        {state.proposal?.ranked.map((item, index) => {
          const candidate = state.draft?.candidates.find(entry => entry.id === item.id);
          if (!candidate) return null;
          return <div className="rank" key={item.id}><span className="rank-number">{String(index + 1).padStart(2, '0')}</span><div><span className="tag">{candidate.kind}</span><h3>{candidate.title}</h3><p>{item.reason}</p>{candidate.url && <a href={candidate.url} target="_blank" rel="noopener noreferrer">View issue</a>}</div>
            <div className="rank-actions">{([-1, 1] as const).map(direction => <button key={direction} className="btn icon" type="button" title={direction < 0 ? 'Move up' : 'Move down'} aria-label={`${direction < 0 ? 'Move up' : 'Move down'}: ${candidate.title}`} disabled={busy || index + direction < 0 || index + direction >= state.proposal!.ranked.length} onClick={() => void execute(async () => {
              const ids = state.proposal!.ranked.map(entry => entry.id);
              [ids[index], ids[index + direction]] = [ids[index + direction], ids[index]];
              const next = await api('/api/proposal/order', 'PUT', { ids }); setState(next); setNotice({ text: 'Draft priority order updated. No GitHub issue was changed.', tone: 'success' });
            })}>{direction < 0 ? <ArrowUp size={16} /> : <ArrowDown size={16} />}</button>)}</div></div>;
        })}
        {!!state.proposal?.questions.length && <div><h3>Open questions</h3><ul>{state.proposal.questions.map(question => <li key={question}>{question}</li>)}</ul></div>}
      </section>
      <section className="history" aria-labelledby="history-title"><div className="section-head"><h2 id="history-title">Previous kickoffs</h2></div>
        {state.history.length === 0 && <p className="small muted">No previous kickoffs.</p>}
        {state.history.map((entry, index) => <div className="history-item small muted" key={index}>{entry.draft.repository} | {entry.run?.status || 'not run'} | {entry.proposal ? 'proposal saved' : 'no proposal'} | {entry.run?.usage === 'unknown' ? entry.run.usageReconciliation ? `External usage reconciled ${entry.run.usageReconciliation.confirmedAt}` : 'Usage not reconciled' : 'No usage decision needed'} | {entry.draft.deadline}</div>)}
      </section>
      </>}
    </main>
  </div>;
}