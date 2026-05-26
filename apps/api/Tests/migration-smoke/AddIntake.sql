-- Post-deploy smoke tests for the AddIntake migration. Same conventions as
-- AddDocumentStore.sql: per-block BEGIN/ROLLBACK, prints "OK <n>" on success.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f AddIntake.sql
--
-- These four invariants are load-bearing for P5: single-row-per-provider
-- (the FOR UPDATE row-lock pattern relies on it), the budget cap (the
-- escalation trigger), outbox dedup (retried Hangfire jobs must not
-- double-send), and magic-link sanity (expiry must follow issuance). A
-- regression masquerades as "two concurrent agent turns ran for the same
-- provider" or "the provider got the same followup twice" — both
-- demo-killers.

\echo === 1. UNIQUE (provider_id) on intake_sessions — second insert must raise ===
BEGIN;
  INSERT INTO providers (id, profile, created_at)
  VALUES ('11111111-1111-1111-1111-111111111111'::uuid, '{}'::jsonb, NOW());

  INSERT INTO intake_sessions (
    id, provider_id, state, state_payload,
    turns_consumed, turn_budget, created_at, last_transition_at
  ) VALUES (
    gen_random_uuid(),
    '11111111-1111-1111-1111-111111111111'::uuid,
    'Pending',
    jsonb_build_object('kind', 'Pending', 'createdAt', NOW()),
    0, 8, NOW(), NOW()
  );

  DO $$
  BEGIN
    BEGIN
      INSERT INTO intake_sessions (
        id, provider_id, state, state_payload,
        turns_consumed, turn_budget, created_at, last_transition_at
      ) VALUES (
        gen_random_uuid(),
        '11111111-1111-1111-1111-111111111111'::uuid,
        'Pending',
        jsonb_build_object('kind', 'Pending', 'createdAt', NOW()),
        0, 8, NOW(), NOW()
      );
      RAISE EXCEPTION 'FAIL: duplicate intake_sessions for one provider did not raise';
    EXCEPTION WHEN unique_violation THEN
      RAISE NOTICE 'OK 1: ux_intake_sessions_provider blocks duplicates (%)', SQLERRM;
    END;
  END$$;
ROLLBACK;

\echo === 2. CHECK (turns_consumed <= turn_budget) — overrun must raise ===
BEGIN;
  INSERT INTO providers (id, profile, created_at)
  VALUES ('22222222-2222-2222-2222-222222222222'::uuid, '{}'::jsonb, NOW());

  DO $$
  BEGIN
    BEGIN
      INSERT INTO intake_sessions (
        id, provider_id, state, state_payload,
        turns_consumed, turn_budget, created_at, last_transition_at
      ) VALUES (
        gen_random_uuid(),
        '22222222-2222-2222-2222-222222222222'::uuid,
        'AgentProcessing',
        jsonb_build_object('kind', 'AgentProcessing',
          'turnId', gen_random_uuid(), 'startedAt', NOW()),
        9, 8, NOW(), NOW()  -- 9 > 8: must violate the cap
      );
      RAISE EXCEPTION 'FAIL: turns_consumed > turn_budget did not raise';
    EXCEPTION WHEN check_violation THEN
      RAISE NOTICE 'OK 2: ck_intake_sessions_turns_within_budget fired (%)', SQLERRM;
    END;
  END$$;
ROLLBACK;

\echo === 3. UNIQUE (provider_id, turn_id, kind) on outbound_messages — retry must dedup ===
BEGIN;
  INSERT INTO providers (id, profile, created_at)
  VALUES ('33333333-3333-3333-3333-333333333333'::uuid, '{}'::jsonb, NOW());

  INSERT INTO outbound_messages (
    id, provider_id, turn_id, kind, subject, body,
    status, composed_at
  ) VALUES (
    gen_random_uuid(),
    '33333333-3333-3333-3333-333333333333'::uuid,
    '44444444-4444-4444-4444-444444444444'::uuid,
    'Followup', 'subject one', 'body one',
    'Queued', NOW()
  );

  DO $$
  BEGIN
    BEGIN
      INSERT INTO outbound_messages (
        id, provider_id, turn_id, kind, subject, body,
        status, composed_at
      ) VALUES (
        gen_random_uuid(),
        '33333333-3333-3333-3333-333333333333'::uuid,
        '44444444-4444-4444-4444-444444444444'::uuid,
        'Followup', 'subject two', 'body two',
        'Queued', NOW()
      );
      RAISE EXCEPTION 'FAIL: duplicate (provider, turn, kind) did not raise';
    EXCEPTION WHEN unique_violation THEN
      RAISE NOTICE 'OK 3: ux_outbound_messages_dedup fired (%)', SQLERRM;
    END;
  END$$;
ROLLBACK;

\echo === 4. CHECK (expires_at > issued_at) on magic_links — flipped order must raise ===
BEGIN;
  INSERT INTO providers (id, profile, created_at)
  VALUES ('55555555-5555-5555-5555-555555555555'::uuid, '{}'::jsonb, NOW());

  DO $$
  BEGIN
    BEGIN
      INSERT INTO magic_links (id, provider_id, issued_at, expires_at)
      VALUES (
        gen_random_uuid(),
        '55555555-5555-5555-5555-555555555555'::uuid,
        NOW(),
        NOW() - INTERVAL '1 hour'  -- expires_at < issued_at: must violate
      );
      RAISE EXCEPTION 'FAIL: expires_at <= issued_at did not raise';
    EXCEPTION WHEN check_violation THEN
      RAISE NOTICE 'OK 4: ck_magic_links_expires_after_issued fired (%)', SQLERRM;
    END;
  END$$;
ROLLBACK;
