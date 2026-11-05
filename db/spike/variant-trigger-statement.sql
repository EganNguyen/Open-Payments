-- Checks the rows of one insert statement, once, when the application inserts all of a
-- ledger transaction's entries in a single statement, as the design requires.
drop trigger if exists ledger_balance on ledger_entries;
drop trigger if exists ledger_balance_statement on ledger_entries;

create or replace function check_statement_balance() returns trigger language plpgsql as $$
begin
  if exists (
    select 1 from inserted e group by e.txn_id, e.currency
    having sum(case e.direction when 'D' then e.amount_minor else -e.amount_minor end) <> 0
  ) then
    raise exception 'unbalanced ledger statement';
  end if;
  return null;
end $$;

create trigger ledger_balance_statement after insert on ledger_entries
  referencing new table as inserted for each statement execute function check_statement_balance();
