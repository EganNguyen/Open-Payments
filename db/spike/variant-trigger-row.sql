drop trigger if exists ledger_balance on ledger_entries;
drop trigger if exists ledger_balance_statement on ledger_entries;
create constraint trigger ledger_balance after insert on ledger_entries
  deferrable initially deferred for each row execute function check_txn_balance();
