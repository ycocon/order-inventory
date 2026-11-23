import { useState, type FormEvent } from 'react';
import { ApiError, errorMessage, request, type Product } from './api';
import { Pagination } from './Pagination';

export type Checkout = { requestKey: string; lines: { sku: string; quantity: number }[] };
export type Order = { orderId: string; status: 'awaiting_payment' | 'confirmed' | 'rejected' | 'dispatched'; rejectionReason: string | null;
  totalMinor: number; currency: string; createdAt: string;
  lines: { sku: string; productName: string; quantity: number; unitPriceMinor: number }[] };

const checkoutStorageKey = (email: string) => `retail.checkout:${email}`;
const orderDate = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });
export const formatMoney = (amount: number, currency = 'USD') =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(amount / 100);

export function orderLabel(order: Pick<Order, 'status' | 'rejectionReason'>) {
  if (order.status === 'confirmed') return 'Confirmed';
  if (order.status === 'dispatched') return 'Dispatched';
  if (order.status === 'awaiting_payment') return 'Awaiting payment';
  return order.rejectionReason === 'payment_declined'
    ? 'Rejected · payment declined' : 'Rejected · insufficient stock';
}

export function readPendingCheckout(email: string): Checkout | null {
  try {
    const saved = sessionStorage.getItem(checkoutStorageKey(email));
    return saved ? JSON.parse(saved) as Checkout : null;
  } catch {
    return null;
  }
}

export function Basket({ email, products, quantities, pending, csrfToken, onChange, onPending, onResult }: {
  email: string;
  products: Product[];
  quantities: Record<string, string>;
  pending: Checkout | null;
  csrfToken: string;
  onChange: (sku: string, quantity: string | null) => void;
  onPending: (checkout: Checkout | null) => void;
  onResult: (order: Order) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const lines = pending?.lines.map(line => [line.sku, String(line.quantity)] as const) ?? Object.entries(quantities);
  const estimates = lines.map(([sku, quantity]) => {
    const product = products.find(item => item.sku === sku);
    return product && Number.isInteger(Number(quantity)) && Number(quantity) > 0
      ? product.unitPriceMinor * Number(quantity) : null;
  });
  const subtotal = estimates.reduce<number>((sum, value) => sum + (value ?? 0), 0);
  const showSubtotal = estimates.every(value => value !== null) && Number.isSafeInteger(subtotal);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (busy || !lines.length) return;
    setError('');
    const checkout = pending ?? { requestKey: crypto.randomUUID(),
      lines: lines.map(([sku, quantity]) => ({ sku, quantity: Number(quantity) })) };
    try {
      sessionStorage.setItem(checkoutStorageKey(email), JSON.stringify(checkout));
    } catch {
      setError('Enable session storage so this checkout can be retried safely.');
      return;
    }
    onPending(checkout);
    setBusy(true);
    try {
      const order = await request<Order>('/api/orders', { method: 'POST', csrfToken, body: checkout });
      sessionStorage.removeItem(checkoutStorageKey(email));
      onPending(null);
      onResult(order);
    } catch (failure) {
      if (failure instanceof ApiError && (failure.code === 'invalid_checkout' || failure.status === 409)) {
        sessionStorage.removeItem(checkoutStorageKey(email));
        onPending(null);
      }
      setError(errorMessage(failure));
    } finally {
      setBusy(false);
    }
  }

  return <section className="catalog-card basket" aria-labelledby="basket-title">
    <div className="basket-header"><h2 id="basket-title" tabIndex={-1}>Basket</h2><span className="nav-count">{lines.length}</span></div>
    {lines.length === 0 ? <div className="basket-empty"><strong>Your basket is empty</strong><p>Add a product to get started.</p></div> : <form onSubmit={submit} className="form-stack">
      {pending && !busy && <p className="notice" role="status">Checkout confirmation is pending. Retry to recover the same order.</p>}
      <ul className="basket-lines">{lines.map(([sku, quantity], index) => {
        const name = products.find(product => product.sku === sku)?.name ?? sku;
        return <li key={sku}>
          <div><strong>{name}</strong><span className="sku">{sku}</span>{estimates[index] !== null && <span className="basket-line-price">{formatMoney(estimates[index])}</span>}</div>
          <label className="basket-quantity">Quantity
            <input aria-label={`Quantity for ${name}`} type="number" inputMode="numeric" min="1" max="2147483647" step="1"
              required disabled={pending !== null} value={quantity} onChange={event => onChange(sku, event.target.value)} />
          </label>
          {!pending && <button className="text-button" type="button" aria-label={`Remove ${name}`} onClick={() => onChange(sku, null)}>Remove</button>}
        </li>;
      })}</ul>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="basket-footer">
        {showSubtotal && <div className="basket-total"><span>Estimated subtotal</span><strong className="numeric">{formatMoney(subtotal)}</strong></div>}
        <button className="primary" disabled={busy}>{busy ? 'Submitting…' : pending ? 'Retry checkout' : 'Submit order'}</button>
        {!pending && <p className="muted basket-help">Stock is reserved when you submit your order.</p>}
      </div>
    </form>}
  </section>;
}

export function OrderHistory({ orders, onRefresh }: { orders: Order[]; onRefresh: () => void }) {
  const [currentPage, setPage] = useState(1);
  const pageSize = 5;
  const page = Math.min(currentPage, Math.max(1, Math.ceil(orders.length / pageSize)));
  return <section className="catalog-card basket history" aria-labelledby="orders-title">
    <div className="basket-header"><h2 id="orders-title" tabIndex={-1}>Recent orders</h2>
      <button className="secondary compact-button" type="button" onClick={onRefresh}>Refresh orders</button>
    </div>
    {orders.length === 0 ? <p className="muted">Submitted orders will appear here.</p> : <ul className="order-list">{orders.slice((page - 1) * pageSize, page * pageSize).map(order =>
      <li key={order.orderId}>
        <div className="order-list-summary">
          <strong className="order-items">{order.lines.map(line => `${line.quantity} × ${line.productName}`).join(', ')}</strong>
          <time dateTime={order.createdAt}>{orderDate.format(new Date(order.createdAt))}</time>
          <span className="sku order-reference">Order {order.orderId}</span>
        </div>
        <div className="order-list-actions">
          <div><span className={`status order-${order.status}`} role="status">{orderLabel(order)}</span>
            <strong className="numeric">{formatMoney(order.totalMinor, order.currency)}</strong></div>
          <CopyOrderReference orderId={order.orderId} />
        </div>
      </li>,
    )}</ul>}
    <Pagination label="Order pages" page={page} pageSize={pageSize} total={orders.length} onPage={setPage} />
  </section>;
}

function CopyOrderReference({ orderId }: { orderId: string }) {
  const [feedback, setFeedback] = useState('');

  async function copy() {
    try {
      await navigator.clipboard.writeText(orderId);
      setFeedback('Reference copied.');
    } catch {
      setFeedback('Select the order reference to copy it.');
    }
  }

  return <div className="copy-reference">
    <button className="text-button" type="button" onClick={() => void copy()}>Copy reference</button>
    {feedback && <span role="status" className="muted">{feedback}</span>}
  </div>;
}
