select p.id::text as sample_id,
       abs(p.refunded_minor + p.refund_pending_minor - p.captured_minor)::bigint as impact_minor
from payments p
where p.refunded_minor + p.refund_pending_minor > p.captured_minor
union all
select p.id::text,
       abs(p.refund_pending_minor - coalesce(r.unresolved, 0))::bigint
from payments p
left join (
  select payment_id, sum(amount_minor) as unresolved
  from refunds
  where state in ('PENDING', 'UNKNOWN')
  group by payment_id
) r on r.payment_id = p.id
where p.refund_pending_minor <> coalesce(r.unresolved, 0);
