# Local PSP secrets

Everything in this folder except this file is git-ignored. Put PSP **sandbox** keys here. Never put live keys here: the API refuses a live key prefix at startup ([Stage 2 constraints](../../../docs/superpowers/plans/2026-11-10-stage-2-psp-and-recovery.md#global-constraints)).

The owner creates these files. Agents never create PSP accounts or type keys.

## Stripe, two sandboxes (merchants A and B)

One file per sandbox, named after its `psp_accounts.credential_ref`:

`stripe-a.env`

```
STRIPE_SECRET_KEY=sk_test_...
STRIPE_WEBHOOK_SECRET=whsec_...
```

`stripe-b.env` holds the same two keys for the second sandbox.

- `STRIPE_SECRET_KEY` comes from that sandbox's Developers → API keys page. Use the secret test key, or a restricted test key (`rk_test_...`) with write access to PaymentIntents and Refunds and read access to Events, Balance and Reports.
- `STRIPE_WEBHOOK_SECRET` is printed by `stripe listen` for local work (see Task 32 of the Stage 2 plan), or shown on a registered webhook endpoint.

Check that git ignores them:

```bash
git check-ignore -v deploy/compose/secrets/stripe-a.env deploy/compose/secrets/stripe-b.env
```
