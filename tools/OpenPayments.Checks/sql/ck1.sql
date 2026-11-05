select t.txn_id::text as sample_id,
       sum(abs(t.diff))::bigint as impact_minor
from (
  select txn_id,
         currency,
         sum(case direction when 'D' then amount_minor else -amount_minor end)::bigint as diff
  from ledger_entries
  group by txn_id, currency
) t
where t.diff <> 0
group by t.txn_id
order by 1;
