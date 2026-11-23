import { useCallback, useEffect, useRef, useState } from 'react';
import { errorMessage, request, type Product, type User } from './api';
import { ProductForm, ReceiptForm, readPendingReceipt } from './Forms';
import { Basket, OrderHistory, readPendingCheckout, type Checkout, type Order } from './Basket';
import { Pagination } from './Pagination';

const usd = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });
const integer = new Intl.NumberFormat('en-US');
const productPageSize = 6;
type Mode = { kind: 'product'; product?: Product } | { kind: 'receipt' } | null;

export function Catalog({ user, csrfToken }: { user: User; csrfToken: string }) {
  const isStaff = user.role === 'staff';
  const [products, setProducts] = useState<Product[] | null>(null);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [query, setQuery] = useState('');
  const [status, setStatus] = useState('all');
  const [inStockOnly, setInStockOnly] = useState(false);
  const [productPage, setProductPage] = useState(1);
  const [mode, setMode] = useState<Mode>(null);
  const [hasPendingReceipt, setHasPendingReceipt] = useState(() => Boolean(readPendingReceipt(user.email)));
  const [pending, setPending] = useState<Checkout | null>(() => isStaff ? null : readPendingCheckout(user.email));
  const [quantities, setQuantities] = useState<Record<string, string>>(() =>
    Object.fromEntries((pending?.lines ?? []).map(line => [line.sku, String(line.quantity)])));
  const [orders, setOrders] = useState<Order[]>([]);
  const [ordersError, setOrdersError] = useState('');
  const orderRequest = useRef(0);
  const refresh = useCallback(async () => {
    try {
      setProducts(await request<Product[]>('/api/products'));
      setError('');
    } catch (failure) { setError(errorMessage(failure)); }
  }, []);
  useEffect(() => { void refresh(); }, [refresh]);
  const loadOrders = useCallback(async () => {
    const requestNumber = ++orderRequest.current;
    try {
      const updated = await request<Order[]>('/api/orders');
      if (requestNumber !== orderRequest.current) return;
      setOrders(updated);
      setOrdersError('');
    } catch (failure) {
      if (requestNumber === orderRequest.current) setOrdersError(errorMessage(failure));
    }
  }, []);
  useEffect(() => { if (!isStaff) void loadOrders(); }, [isStaff, loadOrders]);
  const awaitingPayment = orders.some(order => order.status === 'awaiting_payment');
  useEffect(() => {
    if (isStaff || !awaitingPayment) return;
    let active = true;
    let timer = window.setTimeout(update, 1500);
    async function update() {
      await loadOrders();
      // Read availability after the payment outcome so a decline also refreshes stock.
      await refresh();
      if (active) timer = window.setTimeout(update, 1500);
    }
    return () => { active = false; window.clearTimeout(timer); };
  }, [isStaff, awaitingPayment, loadOrders, refresh]);
  function changeQuantity(sku: string, quantity: string | null) {
    setQuantities(current => {
      const next = { ...current };
      if (quantity === null) delete next[sku];
      else next[sku] = quantity;
      return next;
    });
  }
  function orderPlaced(order: Order) {
    setQuantities({});
    setOrders(existing => [order, ...existing.filter(item => item.orderId !== order.orderId)].slice(0, 25));
    setMessage(order.status !== 'rejected' ? 'Order submitted. Follow its payment status in Recent orders.'
      : order.rejectionReason === 'payment_declined' ? 'Order rejected. Payment was declined and the stock was released.'
      : 'Order rejected. There was insufficient stock for the whole basket.');
    void loadOrders();
    void refresh();
    requestAnimationFrame(() => document.getElementById('orders-title')?.focus());
  }
  function close() {
    setMode(null);
    setHasPendingReceipt(Boolean(readPendingReceipt(user.email)));
  }
  function saved(text: string) {
    close();
    setMessage(text);
    void refresh();
  }
  const catalog = (products ?? []).filter(p => isStaff || p.active);
  const term = query.trim().toLowerCase();
  const shown = catalog.filter(p =>
    (p.name.toLowerCase().includes(term) || p.sku.toLowerCase().includes(term)) &&
    (status === 'all' || (status === 'active') === p.active) &&
    (!inStockOnly || p.available > 0),
  );
  const page = Math.min(productPage, Math.max(1, Math.ceil(shown.length / productPageSize)));
  const pageProducts = shown.slice((page - 1) * productPageSize, page * productPageSize);
  const stats = [
    { label: 'Catalog products', value: catalog.length },
    { label: 'Active listings', value: catalog.filter(p => p.active).length },
    { label: 'Available units', value: catalog.reduce((sum, p) => sum + p.available, 0) },
  ];
  const basketSize = Object.keys(quantities).length;
  return <main className="workspace">
    <div className="page-heading" id="catalog">
      <h1>{isStaff ? 'Inventory' : 'Product catalog'}</h1>
      {isStaff && <div className="heading-actions">
        <button className="secondary" onClick={() => setMode({ kind: 'receipt' })} disabled={!catalog.length && !hasPendingReceipt}>Receive stock</button>
        <button className="primary" onClick={() => setMode({ kind: 'product' })}>Add product</button>
      </div>}
      {!isStaff && <nav className="heading-actions customer-nav" aria-label="Customer shortcuts">
        <a className="secondary" href="#basket-title">Basket <span className="nav-count">{basketSize}</span></a>
        <a className="secondary" href="#orders-title">Orders</a>
      </nav>}
    </div>
    {message && <p className={message.startsWith('Order rejected') ? 'notice notice-warning' : 'notice'} role="status">{message}</p>}
    {error && <p className="error" role="alert">{error} <button className="text-button" onClick={() => void refresh()}>Reload catalog</button></p>}
    {isStaff && hasPendingReceipt && !mode && <div className="notice pending"><span>A stock receipt is awaiting confirmation.</span><button className="text-button" onClick={() => setMode({ kind: 'receipt' })}>Review receipt</button></div>}
    {isStaff && products && <dl className="stats" aria-label="Inventory summary">{stats.map(stat =>
      <div key={stat.label}><dt>{stat.label}</dt><dd>{integer.format(stat.value)}</dd></div>,
    )}</dl>}
    <div className={isStaff ? 'staff-content' : 'customer-content'}>
      <section className="listing" aria-label="Product register">
        <div className="listing-toolbar">
          <div className="filters">
            <label className="search"><span className="magnifier" aria-hidden="true" /><span className="sr-only">Search by name or SKU</span><input type="search" placeholder="Name or SKU" value={query} onChange={e => { setQuery(e.target.value); setProductPage(1); }} /></label>
            {isStaff && <label className="status-filter"><span className="sr-only">Filter by sale status</span><select value={status} onChange={e => { setStatus(e.target.value); setProductPage(1); }}><option value="all">All statuses</option><option value="active">Active</option><option value="inactive">Inactive</option></select></label>}
            {!isStaff && <label className="stock-filter"><input type="checkbox" checked={inStockOnly}
              onChange={event => { setInStockOnly(event.target.checked); setProductPage(1); }} /> In stock only</label>}
          </div>
          {products && <p className="result-count" role="status">{shown.length} {shown.length === 1 ? 'product' : 'products'}</p>}
        </div>
        <div className="catalog-card listing-results">
          {!products ? <p className="empty" role="status">{error ? 'Catalog unavailable' : 'Loading products…'}</p>
            : !shown.length ? <div className="empty"><h3>{catalog.length ? 'No matching products' : 'No products yet'}</h3><p>{catalog.length ? 'Try a different name, SKU, or filter.' : isStaff ? 'Add a product to begin receiving stock.' : 'Active products will appear here.'}</p>
              {catalog.length > 0 && <button className="secondary compact-button" type="button" onClick={() => {
                setQuery(''); setStatus('all'); setInStockOnly(false); setProductPage(1);
              }}>Clear filters</button>}
            </div>
            : <div className="table-scroll"><table className={isStaff ? 'product-table staff-product-table' : 'product-table'} role="table" aria-label="Products">
              <thead role="rowgroup"><tr role="row"><th scope="col" role="columnheader">Product</th><th scope="col" role="columnheader">SKU</th><th scope="col" role="columnheader">Unit price</th><th scope="col" role="columnheader">Available</th>{isStaff && <th scope="col" role="columnheader">Sale status</th>}<th scope="col" role="columnheader"><span className="sr-only">Actions</span></th></tr></thead>
              <tbody role="rowgroup">{pageProducts.map(product => <tr key={product.sku} role="row">
                <td className="record-name" role="cell"><strong>{product.name}</strong></td>
                <td className="record-sku" role="cell"><span className="sku">{product.sku}</span></td>
                <td className="numeric record-price" role="cell"><span className="mobile-label" aria-hidden="true">Unit price</span>{usd.format(product.unitPriceMinor / 100)}</td>
                <td className="numeric record-stock" role="cell"><span className="mobile-label" aria-hidden="true">Available</span><span className={product.available === 0 ? 'stock-count stock-zero' : 'stock-count'}>{integer.format(product.available)}<span className="sr-only"> units available</span></span></td>
                {isStaff && <><td className="record-status" role="cell"><span className="mobile-label" aria-hidden="true">Sale status</span><span className={'status ' + (product.active ? 'active' : '')}>{product.active ? 'Active' : 'Inactive'}</span></td>
                  <td className="row-action" role="cell"><button className="secondary compact-button" aria-label={'Edit ' + product.name} onClick={() => setMode({ kind: 'product', product })}>Edit</button></td></>}
                {!isStaff && <td className="row-action" role="cell"><button className={product.available > 0 && quantities[product.sku] === undefined ? 'primary add-action' : 'secondary add-action'} aria-label={quantities[product.sku] !== undefined
                  ? product.name + ' is in your basket' : product.available === 0 ? product.name + ' is sold out' : 'Add ' + product.name + ' to basket'}
                  disabled={pending !== null || product.available === 0 || quantities[product.sku] !== undefined || basketSize >= 50}
                  onClick={() => changeQuantity(product.sku, '1')}>{quantities[product.sku] !== undefined ? 'Added' : product.available === 0 ? 'Sold out' : 'Add'}</button></td>}
              </tr>)}</tbody>
            </table></div>}
        </div>
        <Pagination label="Product pages" page={page} pageSize={productPageSize} total={shown.length} onPage={setProductPage} />
      </section>
      {!isStaff && <Basket email={user.email} products={catalog} quantities={quantities} pending={pending} csrfToken={csrfToken}
        onChange={changeQuantity} onPending={setPending} onResult={orderPlaced} />}
    </div>
    {!isStaff && ordersError && <p className="error" role="alert">{ordersError} <button className="text-button" onClick={() => void loadOrders()}>Reload orders</button></p>}
    {!isStaff && <OrderHistory key={orders[0]?.orderId} orders={orders} onRefresh={() => void loadOrders()} />}
    {!isStaff && basketSize > 0 && <div className="mobile-basket">
      <a href="#basket-title" className="primary">View basket <span className="nav-count">{basketSize}</span></a>
    </div>}
    {mode?.kind === 'product' && isStaff && <ProductForm product={mode.product} csrfToken={csrfToken} onClose={close} onSaved={saved} />}
    {mode?.kind === 'receipt' && isStaff && <ReceiptForm email={user.email} products={catalog} csrfToken={csrfToken} onClose={close} onSaved={saved} />}
  </main>;
}
