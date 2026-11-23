import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { formatMoney, orderLabel, type Order } from './Basket';
import { Dialog } from './Forms';
import { Pagination } from './Pagination';
import { errorMessage, request } from './api';

type Summary = Omit<Order, 'lines'> & { customer: string };
type OrderPage = { page: number; pageSize: number; total: number; items: Summary[] };
type Dispatch = { orderId: string; staffId: string; dispatchedAt: string };
type Payment = { operationId: string; amountMinor: number; currency: string; decision: string;
  workStatus: string; attemptCount: number; lastError: string | null };
type HistoryEvent = { kind: string; recordedAt: string; actor: string | null };
type Movement = HistoryEvent & { id: string; sku: string; quantity: number; reason: string };
type OrderDetail = { order: Order; customer: string; payment: Payment | null;
  dispatch: (Dispatch & { staff?: string | null }) | null; history: HistoryEvent[]; stock: Movement[];
  timelinePending: boolean; timelineUpdatedAt: string | null };

const formatDate = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'medium' });
const date = (value: string) => formatDate.format(new Date(value));

export function StaffOrders({ csrfToken }: { csrfToken: string }) {
  const [draft, setDraft] = useState('');
  const [filters, setFilters] = useState({ q: '', status: '', page: 1 });
  const [data, setData] = useState<OrderPage | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [openedId, openOrder] = useState<string | null>(null);
  const requestNumber = useRef(0);
  const load = useCallback(async () => {
    const current = ++requestNumber.current;
    setBusy(true);
    setError('');
    try {
      const params = new URLSearchParams({ ...filters, page: String(filters.page) });
      const next = await request<OrderPage>('/api/staff/orders?' + params);
      if (current === requestNumber.current) setData(next);
    } catch (failure) {
      if (current === requestNumber.current) setError(errorMessage(failure));
    } finally {
      if (current === requestNumber.current) setBusy(false);
    }
  }, [filters]);
  useEffect(() => { void load(); }, [load]);

  function search(event: FormEvent) {
    event.preventDefault();
    setFilters(previous => ({ ...previous, q: draft.trim(), page: 1 }));
  }

  return <main className="workspace">
    <div className="page-heading">
      <h1>Orders</h1>
      <button className="secondary" type="button" disabled={busy} onClick={() => void load()}>Refresh orders</button>
    </div>
    {error && <p className="error" role="alert">{error}</p>}
    <section className="catalog-card" aria-label="Order register" aria-busy={busy}>
      <div className="register-head">
        <div><h2>Order register</h2>{data && <p className="result-count">{data.total} {data.total === 1 ? 'order' : 'orders'}</p>}</div>
        <form className="filters" onSubmit={search}>
          <label className="search"><span className="magnifier" aria-hidden="true" /><span className="sr-only">Search by order reference or email</span>
            <input type="search" placeholder="Order reference or email" value={draft} maxLength={256} onChange={event => setDraft(event.target.value)} /></label>
          <button className="secondary compact-button" type="submit">Search</button>
          <label><span className="sr-only">Filter by order status</span><select value={filters.status}
            onChange={event => setFilters(previous => ({ ...previous, status: event.target.value, page: 1 }))}>
            <option value="">All statuses</option>
            <option value="awaiting_payment">Awaiting payment</option>
            <option value="overdue">Payment needs attention</option>
            <option value="confirmed">Confirmed</option>
            <option value="dispatched">Dispatched</option>
            <option value="rejected">Rejected</option>
          </select></label>
        </form>
      </div>
      {!data ? <p className="empty" role="status">{error ? 'Orders unavailable' : 'Loading orders…'}</p>
        : data.items.length === 0 ? <p className="empty">No orders match the current filters.</p>
        : <div className="table-scroll staff-order-table"><table>
          <thead><tr><th>Order</th><th>Status</th><th>Amount</th><th><span className="sr-only">Actions</span></th></tr></thead>
          <tbody>{data.items.map(order => <tr key={order.orderId}>
            <td><strong>{order.customer}</strong><span className="sku">{order.orderId}</span><span className="order-items">{date(order.createdAt)}</span></td>
            <td><span className={`status order-${order.status}`}>{orderLabel(order)}</span></td>
            <td className="numeric">{formatMoney(order.totalMinor, order.currency)}</td>
            <td className="row-action"><button className="text-button" type="button" aria-label={`Review order ${order.orderId}`}
              onClick={() => openOrder(order.orderId)}>Review</button></td>
          </tr>)}</tbody>
        </table></div>}
      {data && <Pagination label="Staff order pages" page={data.page} pageSize={data.pageSize} total={data.total}
        onPage={page => setFilters(previous => ({ ...previous, page }))} />}
    </section>
    {openedId && <OrderReview key={openedId} orderId={openedId} csrfToken={csrfToken} onClose={() => openOrder(null)} onDispatch={() => void load()} />}
  </main>;
}

function OrderReview({ orderId, csrfToken, onClose, onDispatch }: {
  orderId: string; csrfToken: string; onClose: () => void; onDispatch: () => void;
}) {
  const [detail, setDetail] = useState<OrderDetail | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [saved, setSaved] = useState(false);
  const path = '/api/staff/orders/' + orderId;
  const load = useCallback(async () => {
    setError('');
    try { setDetail(await request<OrderDetail>(path)); }
    catch (failure) { setError(errorMessage(failure)); }
  }, [path]);
  useEffect(() => { void load(); }, [load]);

  async function resumePayment() {
    setBusy(true);
    setError('');
    try {
      await request(path + '/resume-payment', { method: 'POST', csrfToken });
      await load();
      onDispatch();
    } catch (failure) {
      setError(errorMessage(failure));
    } finally {
      setBusy(false);
    }
  }

  async function dispatch() {
    if (!detail || busy) return;
    setBusy(true);
    setError('');
    try {
      const recorded = await request<Dispatch>(path + '/dispatch', { method: 'POST', csrfToken });
      setDetail({ ...detail, order: { ...detail.order, status: 'dispatched' }, dispatch: recorded });
      setConfirm(false);
      setSaved(true);
      onDispatch();
      await load();
    } catch (failure) {
      setError(errorMessage(failure));
    } finally {
      setBusy(false);
    }
  }

  return <Dialog title="Order details" busy={busy} onClose={onClose} wide>
    {error && <p className="error" role="alert">{error} <button type="button" className="text-button" onClick={() => void load()}>Reload details</button></p>}
    {!detail && !error && <p role="status">Loading order…</p>}
    {detail && <div className="order-review">
      <div className="order-review-summary">
        <div><strong>{detail.customer}</strong><span className="sku order-reference">Order {orderId}</span></div>
        <span className={`status order-${detail.order.status}`}>{orderLabel(detail.order)}</span>
      </div>
      {saved && <p className="notice" role="status">Dispatch recorded.</p>}
      <section aria-labelledby="order-lines-heading">
        <h3 id="order-lines-heading">Items</h3>
        <ul className="review-items">{detail.order.lines.map(line => <li key={line.sku}>
          <div>{line.quantity} × {line.productName}<span className="sku">{line.sku}</span></div>
          <span className="numeric">{formatMoney(line.quantity * line.unitPriceMinor, detail.order.currency)}</span>
        </li>)}</ul>
      </section>
      <section aria-labelledby="payment-heading">
        <h3 id="payment-heading">Payment</h3>
        {detail.payment ? <>
          <p>{detail.payment.decision === 'unresolved' ? 'Awaiting decision' : detail.payment.decision === 'approved' ? 'Approved' : 'Declined'} · {formatMoney(detail.payment.amountMinor, detail.payment.currency)}</p>
          <p className="sku">Payment {detail.payment.operationId}</p>
          {detail.payment.decision === 'unresolved' && <p className="muted">
            {detail.payment.workStatus === 'overdue' ? 'Payment needs attention.' : 'Payment check in progress.'} Pending for {Math.max(0, Math.floor((Date.now() - Date.parse(detail.order.createdAt)) / 60000))} min · Attempts: {detail.payment.attemptCount}.
          </p>}
          {detail.payment.workStatus === 'overdue' && <button type="button" className="secondary" disabled={busy} onClick={() => void resumePayment()}>Resume payment check</button>}
          {detail.payment.lastError && <p className="muted">{detail.payment.lastError}</p>}
        </> : <p className="muted">No payment was requested.</p>}
      </section>
      <section aria-labelledby="timeline-heading">
        <h3 id="timeline-heading">Order timeline</h3>
        {detail.timelinePending && <p className="muted" role="status">Timeline updates are pending.{' '}
          <button type="button" className="text-button" disabled={busy} onClick={() => void load()}>Refresh details</button>
        </p>}
        {detail.timelineUpdatedAt && <p className="muted">Last updated {date(detail.timelineUpdatedAt)}</p>}
        <ol className="review-timeline">{detail.history.map(event => <li key={event.kind}>
          <strong>{event.kind === 'OrderRejected' ? orderLabel(detail.order) : timelineLabels[event.kind] ?? event.kind}</strong>
          <span>{date(event.recordedAt)} · {event.actor ?? 'System'}</span>
        </li>)}</ol>
      </section>
      <section aria-labelledby="stock-heading">
        <h3 id="stock-heading">Stock movements</h3>
        {detail.stock.length ? <ul className="review-timeline">{detail.stock.map(movement => <li key={movement.id}>
          <strong>{movement.reason} · {movement.quantity} × {movement.sku}</strong>
          <span>{date(movement.recordedAt)} · {movement.actor ?? 'System'}</span>
        </li>)}</ul> : <p className="muted">No stock movements for this order.</p>}
      </section>
      {detail.dispatch && <p className="notice">Dispatched {date(detail.dispatch.dispatchedAt)}{detail.dispatch.staff ? ' by ' + detail.dispatch.staff : ''}.</p>}
      {detail.order.status === 'confirmed' && (confirm
        ? <section className="dispatch-confirm" aria-label="Confirm dispatch">
          <p>Dispatch every item in this order? This records the stock leaving inventory.</p>
          <div className="heading-actions">
            <button type="button" className="secondary" disabled={busy} onClick={() => setConfirm(false)}>Cancel</button>
            <button type="button" className="primary" disabled={busy} onClick={() => void dispatch()}>{busy ? 'Dispatching…' : 'Confirm dispatch'}</button>
          </div>
        </section>
        : <div><button type="button" className="primary" onClick={() => setConfirm(true)}>Dispatch order</button></div>)}
    </div>}
  </Dialog>;
}

const timelineLabels: Record<string, string> = {
  OrderAwaitingPayment: 'Order submitted',
  OrderConfirmed: 'Payment approved',
  OrderDispatched: 'Order dispatched'
};
