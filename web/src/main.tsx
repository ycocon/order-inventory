import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { createRoot } from 'react-dom/client';
import { Catalog } from './Catalog';
import { StaffOrders } from './StaffOrders';
import { errorMessage, request, type Session } from './api';
import './styles.css';

function Brand() {
  return <span className="brand"><span className="brand-mark" aria-hidden="true"><i /><i /><i /></span>Stockroom</span>;
}

function Login({ session, onLogin }: { session: Session; onLogin: () => Promise<void> }) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError('');
    try {
      await request('/api/login', { method: 'POST', csrfToken: session.csrfToken, body: { email: email.trim(), password } });
      await onLogin();
    } catch (failure) {
      setError(errorMessage(failure));
    } finally { setBusy(false); }
  }
  return <main className="login-layout">
    <div className="login-preview" aria-hidden="true">
      <div className="preview-orbit orbit-outer" /><div className="preview-orbit orbit-inner" />
      <div className="preview-box box-back" /><div className="preview-box box-middle" /><div className="preview-box box-front" />
    </div>
    <section className="login-card" aria-labelledby="login-title">
      <h1 id="login-title">Sign in</h1>
      <p className="intro">Access the catalog, your orders, or the inventory workspace.</p>
      <form className="form-stack" onSubmit={submit}>
        <label>Email address<input type="email" autoComplete="username" required value={email} onChange={e => setEmail(e.target.value)} /></label>
        <label>Password<input type="password" autoComplete="current-password" required value={password} onChange={e => setPassword(e.target.value)} /></label>
        {error && <p className="error" role="alert">{error}</p>}
        <button className="primary" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
      </form>
      <details className="demo"><summary>Demo accounts</summary><div className="demo-options">
        <button className="secondary" type="button" onClick={() => setEmail('staff@demo.local')}>Use staff email</button>
        <button className="secondary" type="button" onClick={() => setEmail('customer@demo.local')}>Use customer email</button>
      </div><p>Use the account password from the local demo configuration.</p></details>
    </section>
  </main>;
}

function App() {
  const orderWorkspace = window.location.pathname.replace(/\/$/, '') === '/staff/orders';
  const [theme, setTheme] = useState<'light' | 'dark'>(() => {
    let saved: string | null = null;
    try { saved = localStorage.getItem('stockroom-theme'); } catch { /* Storage may be disabled. */ }
    if (saved === 'light' || saved === 'dark') return saved;
    return matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  });
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', theme === 'dark' ? '#162234' : '#ffffff');
    try { localStorage.setItem('stockroom-theme', theme); } catch { /* The theme still applies for this visit. */ }
  }, [theme]);
  const [session, setSession] = useState<Session | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const loadSession = useCallback(async () => {
    const result = await request<Session>('/api/session');
    setSession(result);
    setError('');
  }, []);
  useEffect(() => {
    const reload = () => {
      setSession(null);
      void loadSession().catch(failure => setError(errorMessage(failure)));
    };
    reload();
    window.addEventListener('session-expired', reload);
    return () => window.removeEventListener('session-expired', reload);
  }, [loadSession]);
  async function logout() {
    if (!session || busy) return;
    setBusy(true);
    try {
      await request('/api/logout', { method: 'POST', csrfToken: session.csrfToken });
      setSession(null);
      await loadSession();
    } catch (failure) {
      setError(errorMessage(failure));
    } finally { setBusy(false); }
  }
  return <>
    <header className="masthead"><div className="masthead-inner">
      <Brand />
      {session?.user?.role === 'staff' && <nav className="staff-tabs" aria-label="Staff workspace">
        <a href="/" aria-current={orderWorkspace ? undefined : 'page'}>Inventory</a>
        <a href="/staff/orders" aria-current={orderWorkspace ? 'page' : undefined}>Orders</a>
      </nav>}
      <div className="header-actions">
      <button className="quiet-light theme-toggle" type="button" onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}>
        <span aria-hidden="true">{theme === 'dark' ? '☀' : '☾'}</span> {theme === 'dark' ? 'Light mode' : 'Dark mode'}
      </button>
      {session?.user && <div className="account">
        <div className="account-avatar" aria-hidden="true">{session.user.email[0].toUpperCase()}</div>
        <div className="account-identity"><strong>{session.user.role === 'staff' ? 'Staff' : 'Customer'}</strong><span className="account-email">{session.user.email}</span></div>
        <button className="quiet-light" onClick={logout} disabled={busy}>Sign out</button>
      </div>}
      </div>
    </div></header>
    {error && <div className="global-error" role="alert">{error} <button className="text-button" onClick={() => void loadSession().catch(failure => setError(errorMessage(failure)))}>Retry</button></div>}
    {!session ? <main className="loading" aria-busy={!error}>{error ? 'Session unavailable' : 'Opening Stockroom…'}</main>
      : session.user?.role === 'staff' && orderWorkspace ? <StaffOrders csrfToken={session.csrfToken} />
      : session.user ? <Catalog key={session.user.email} user={session.user} csrfToken={session.csrfToken} />
      : <Login session={session} onLogin={loadSession} />}
  </>;
}

createRoot(document.getElementById('root')!).render(<App />);
