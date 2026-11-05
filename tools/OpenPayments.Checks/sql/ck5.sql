select 'payment:' || p.id::text as sample_id, 0::bigint as impact_minor
from payments p
where p.state in ('PSP_PENDING', 'UNKNOWN')
  and p.updated_at < now() - interval '60 seconds'
union all
select 'attempt:' || a.attempt_id::text, 0::bigint
from payment_attempts a
where a.state in ('PENDING', 'UNKNOWN')
  and a.created_at < now() - interval '60 seconds'
union all
select 'refund:' || r.id::text, 0::bigint
from refunds r
where r.state in ('PENDING', 'UNKNOWN')
  and r.created_at < now() - interval '60 seconds'
union all
select 'event:' || e.psp_account_id::text || ':' || e.psp_event_id, 0::bigint
from psp_events e
where e.processed_at is null
  and e.received_at < now() - interval '300 seconds';
