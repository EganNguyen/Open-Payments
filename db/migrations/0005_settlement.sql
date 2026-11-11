-- Task 31 (D18, D63): settlement ingestion, per account and per file.
--
-- settlement_lines already exists with one row per line (0001). This migration adds the file's
-- own progress row and the discrepancy rows for an operation that never settles. A line's state
-- is RECEIVED until matching, then MATCHED, UNMATCHED, MISMATCH, DUPLICATE or IGNORED. Fee and
-- adjustment lines are IGNORED: they are stored, and their ledger treatment is still Open.

create table settlement_files (
  psp_account_id uuid not null references psp_accounts(id),
  file_id        text not null,
  business_date  date not null,
  state          text not null check (state in ('receiving','matching','done')),
  lines_total    int not null default 0,
  received_at    timestamptz not null default now(),
  completed_at   timestamptz,
  primary key (psp_account_id, file_id));

-- The worker's due list: files that have not finished.
create index settlement_files_due on settlement_files (psp_account_id, business_date)
  where state <> 'done';

-- One row per operation that has no matched line past the adapter's settlement lag. Either
-- attempt_id or refund_id is set, never both; the two partial indexes make each unique.
create table settlement_missing (
  psp_account_id uuid not null references psp_accounts(id),
  payment_id     uuid not null references payments(id),
  attempt_id     uuid,
  refund_id      uuid references refunds(id),
  expected_by    timestamptz not null,
  found_at       timestamptz,
  check ((attempt_id is null) <> (refund_id is null)),
  foreign key (payment_id, attempt_id) references payment_attempts (payment_id, attempt_id));
create unique index settlement_missing_attempt on settlement_missing (psp_account_id, attempt_id)
  where attempt_id is not null;
create unique index settlement_missing_refund on settlement_missing (psp_account_id, refund_id)
  where refund_id is not null;
create index settlement_missing_open on settlement_missing (expected_by)
  where found_at is null;

-- Matching walks the received lines of one file in line order.
create index settlement_lines_received on settlement_lines (psp_account_id, file_id, line_no)
  where state = 'RECEIVED';
create index settlement_lines_reference on settlement_lines (psp_account_id, psp_reference)
  where psp_reference is not null;

grant select, insert, update on settlement_files, settlement_missing to op_app;
