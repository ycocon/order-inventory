import { useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { ApiError, errorMessage, request, type Product, type Receipt } from './api';

type FormProps = { csrfToken: string; onClose: () => void; onSaved: (message: string) => void };

export function Dialog({ title, busy, onClose, children, wide }: { title: string; busy: boolean; onClose: () => void; children: ReactNode; wide?: boolean }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  useEffect(() => {
    const element = dialog.current;
    element?.showModal();
    return () => element?.close();
  }, []);
  return <dialog ref={dialog} className={wide ? 'wide-dialog' : undefined} aria-labelledby={titleId} onCancel={event => { event.preventDefault(); if (!busy) onClose(); }}>
    <div className="dialog-head"><h2 id={titleId}>{title}</h2><button type="button" className="close" aria-label="Close dialog" disabled={busy} onClick={onClose}>×</button></div>
    {children}
  </dialog>;
}

export function ProductForm({ product, csrfToken, onClose, onSaved }: FormProps & { product?: Product }) {
  const [sku, setSku] = useState(product?.sku ?? '');
  const [name, setName] = useState(product?.name ?? '');
  const [price, setPrice] = useState(product ? (product.unitPriceMinor / 100).toFixed(2) : '');
  const [active, setActive] = useState(product?.active ?? true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (busy) return;
    setError('');
    const [whole, cents = ''] = price.split('.');
    const unitPriceMinor = Number(whole) * 100 + Number(cents.padEnd(2, '0'));
    if (!/^\d+(\.\d{1,2})?$/.test(price) || !Number.isSafeInteger(unitPriceMinor)) {
      setError('Enter a valid USD price with up to two decimal places.');
      return;
    }
    setBusy(true);
    try {
      const path = product ? '/api/products/' + encodeURIComponent(product.sku) : '/api/products';
      await request(path, { method: product ? 'PUT' : 'POST', csrfToken, body: { sku: sku.trim(), name: name.trim(), unitPriceMinor, active } });
      onSaved(name.trim() + (product ? ' updated.' : ' added to the catalog.'));
    } catch (failure) { setError(errorMessage(failure)); }
    finally { setBusy(false); }
  }
  return <Dialog title={product ? 'Edit product' : 'Add product'} busy={busy} onClose={onClose}>
    <form className="form-stack" onSubmit={submit}>
      <label>Product name<input autoFocus required maxLength={160} value={name} onChange={e => setName(e.target.value)} /></label>
      <div className="form-pair">
        <label>SKU<input required maxLength={48} readOnly={Boolean(product)} value={sku} onChange={e => setSku(e.target.value)} /></label>
        <label>Unit price (USD)<input inputMode="decimal" placeholder="0.00" required value={price} onChange={e => setPrice(e.target.value)} /></label>
      </div>
      <label className="check"><input type="checkbox" checked={active} onChange={e => setActive(e.target.checked)} />Active in the customer catalog</label>
      {error && <p role="alert" className="error">{error}</p>}
      <div className="actions"><button type="button" className="secondary" disabled={busy} onClick={onClose}>Cancel</button><button className="primary" disabled={busy}>{busy ? 'Saving…' : 'Save product'}</button></div>
    </form>
  </Dialog>;
}

const receiptKey = (email: string) => 'stockroom:receipt:v1:' + email;
export function readPendingReceipt(email: string): Receipt | null {
  try { return JSON.parse(sessionStorage.getItem(receiptKey(email)) ?? 'null') as Receipt | null; }
  catch { return null; }
}

export function ReceiptForm({ products, email, csrfToken, onClose, onSaved }: FormProps & { products: Product[]; email: string }) {
  const productLabelId = useId();
  const [pending, setPending] = useState(() => readPendingReceipt(email));
  const [sku, setSku] = useState(pending?.sku ?? '');
  const [quantity, setQuantity] = useState(pending ? String(pending.quantity) : '');
  const [reason, setReason] = useState(pending?.reason ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const locked = busy || Boolean(pending);
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (busy) return;
    setError('');
    if (!pending && (!Number.isInteger(Number(quantity)) || Number(quantity) < 1 || Number(quantity) > 2147483647 || !reason.trim())) {
      setError('Enter a positive whole quantity and a reason for this receipt.');
      return;
    }
    const receipt: Receipt = pending ?? { sku, quantity: Number(quantity), reason: reason.trim(), requestKey: crypto.randomUUID() };
    try {
      // Keep the exact command across reloads before asking the API to credit stock.
      sessionStorage.setItem(receiptKey(email), JSON.stringify(receipt));
    } catch {
      setError('Enable session storage in this browser so a receipt can be retried safely.');
      return;
    }
    setPending(receipt);
    setBusy(true);
    try {
      await request('/api/receipts', { method: 'POST', csrfToken, body: receipt });
      sessionStorage.removeItem(receiptKey(email));
      onSaved('Received ' + receipt.quantity + (receipt.quantity === 1 ? ' unit of ' : ' units of ') + receipt.sku + '.');
    } catch (failure) {
      // An explicit command rejection means these terms did not credit stock.
      // Expired authentication or CSRF cannot resolve an earlier lost reply.
      if (failure instanceof ApiError && (
        failure.code === 'invalid_receipt' || [404, 409, 422].includes(failure.status) ||
        (failure.status === 400 && !pending)
      )) {
        sessionStorage.removeItem(receiptKey(email));
        setPending(null);
      }
      setError(errorMessage(failure));
    } finally { setBusy(false); }
  }
  return <Dialog title="Receive stock" busy={busy} onClose={onClose}>
    <form className="form-stack" onSubmit={submit}>
      {pending && !busy && <p className="notice">Confirmation is pending. Retrying uses the saved receipt reference and adds the units only once.</p>}
      <label><span id={productLabelId}>Product</span><select aria-labelledby={productLabelId} required autoFocus disabled={locked} value={sku} onChange={e => setSku(e.target.value)}>
        <option value="" disabled>Select a product</option>
        {pending && !products.some(p => p.sku === pending.sku) && <option value={pending.sku}>{pending.sku}</option>}
        {products.map(p => <option key={p.sku} value={p.sku}>{p.name} ({p.sku})</option>)}
      </select></label>
      <label>Quantity received<input type="number" inputMode="numeric" min="1" max="2147483647" step="1" required disabled={locked} value={quantity} onChange={e => setQuantity(e.target.value)} /></label>
      <label>Reason<input required maxLength={300} disabled={locked} value={reason} onChange={e => setReason(e.target.value)} placeholder="e.g. Supplier delivery" /></label>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="actions"><button className="secondary" type="button" disabled={busy} onClick={onClose}>{pending ? 'Close' : 'Cancel'}</button><button className="primary" disabled={busy}>{busy ? 'Recording…' : pending ? 'Retry receipt' : 'Record receipt'}</button></div>
    </form>
  </Dialog>;
}
