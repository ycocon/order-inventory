export function Pagination({ label, page, pageSize, total, onPage }: {
  label: string;
  page: number;
  pageSize: number;
  total: number;
  onPage: (page: number) => void;
}) {
  const last = Math.max(1, Math.ceil(total / pageSize));
  if (last === 1) return null;

  return <nav className="pagination" aria-label={label}>
    <span aria-live="polite">{(page - 1) * pageSize + 1}–{Math.min(page * pageSize, total)} of {total}</span>
    <div>
      <button type="button" className="secondary" disabled={page === 1} onClick={() => onPage(page - 1)}>Previous</button>
      <button type="button" className="primary" disabled={page === last} onClick={() => onPage(page + 1)}>Next</button>
    </div>
  </nav>;
}
