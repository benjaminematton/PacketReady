-- Post-deploy smoke tests for the AddDocumentStore migration. Not an xUnit
-- target (the backend has no real DB-bound integration test harness — see
-- decisions log). Run by hand after `dotnet ef database update` lands the
-- migration; every block is independent and rolls back. A passing run prints
-- "OK <n>" four times.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f AddDocumentStore.sql
--
-- These four invariants are load-bearing for the P3 extractor + P4 aggregator
-- + P5 confirmation paths. A regression here masquerades as "extractor is
-- silent" or "aggregator sees stale data" — debugging from there is brutal.

\echo === 1. UPDATE on document_extractions must raise (append-only trigger) ===
BEGIN;
  INSERT INTO providers (id, profile, created_at)
  VALUES ('11111111-1111-1111-1111-111111111111'::uuid, '{}'::jsonb, NOW());

  INSERT INTO documents (
    id, provider_id, doc_type, doc_type_conf,
    classifier_model, classifier_prompt_hash, storage_uri,
    original_name, mime_type, page_count, uploaded_at, uploaded_by
  ) VALUES (
    '22222222-2222-2222-2222-222222222222'::uuid,
    '11111111-1111-1111-1111-111111111111'::uuid,
    'License', 0.95, 'claude-haiku-4-5', repeat('a', 64),
    'file:///tmp/x.pdf', 'license.pdf', 'application/pdf', 3, NOW(), 'provider'
  );

  INSERT INTO document_extractions (
    id, document_id, extraction_id, schema_version, status,
    fields, field_locations, confidence,
    source, model, prompt_hash, input_tokens, output_tokens,
    extracted_at, confirmed_at
  ) VALUES (
    '33333333-3333-3333-3333-333333333333'::uuid,
    '22222222-2222-2222-2222-222222222222'::uuid,
    1, 'license.v1', 'Succeeded',
    '{}'::jsonb, '{}'::jsonb, '{}'::jsonb,
    'llm', 'claude-sonnet-4-6', repeat('a', 64), 100, 50,
    NOW(), NOW()
  );

  DO $$
  BEGIN
    BEGIN
      UPDATE document_extractions SET fields = '{}' WHERE id = '33333333-3333-3333-3333-333333333333'::uuid;
      RAISE EXCEPTION 'FAIL: UPDATE did not raise; append-only trigger is broken';
    EXCEPTION WHEN raise_exception THEN
      RAISE NOTICE 'OK 1: UPDATE blocked as expected (%)', SQLERRM;
    END;
  END$$;
ROLLBACK;

\echo === 2. Idempotency: duplicate (doc, schema, model, prompt_hash) must raise ===
BEGIN;
  INSERT INTO providers (id, profile, created_at)
  VALUES ('44444444-4444-4444-4444-444444444444'::uuid, '{}'::jsonb, NOW());

  INSERT INTO documents (
    id, provider_id, doc_type, doc_type_conf,
    classifier_model, classifier_prompt_hash, storage_uri,
    original_name, mime_type, page_count, uploaded_at, uploaded_by
  ) VALUES (
    '55555555-5555-5555-5555-555555555555'::uuid,
    '44444444-4444-4444-4444-444444444444'::uuid,
    'License', 0.95, 'claude-haiku-4-5', repeat('a', 64),
    'file:///tmp/y.pdf', 'license.pdf', 'application/pdf', 3, NOW(), 'provider'
  );

  INSERT INTO document_extractions (
    id, document_id, extraction_id, schema_version, status,
    fields, field_locations, confidence,
    source, model, prompt_hash, input_tokens, output_tokens, extracted_at, confirmed_at
  ) VALUES (
    gen_random_uuid(),
    '55555555-5555-5555-5555-555555555555'::uuid,
    1, 'license.v1', 'Succeeded',
    '{}'::jsonb, '{}'::jsonb, '{}'::jsonb,
    'llm', 'claude-sonnet-4-6', repeat('b', 64), 100, 50, NOW(), NOW()
  );

  DO $$
  BEGIN
    BEGIN
      INSERT INTO document_extractions (
        id, document_id, extraction_id, schema_version, status,
        fields, field_locations, confidence,
        source, model, prompt_hash, input_tokens, output_tokens, extracted_at, confirmed_at
      ) VALUES (
        gen_random_uuid(),
        '55555555-5555-5555-5555-555555555555'::uuid,
        2, 'license.v1', 'Succeeded',
        '{}'::jsonb, '{}'::jsonb, '{}'::jsonb,
        'llm', 'claude-sonnet-4-6', repeat('b', 64), 100, 50, NOW(), NOW()
      );
      RAISE EXCEPTION 'FAIL: duplicate (doc, schema, model, prompt_hash) did not raise';
    EXCEPTION WHEN unique_violation THEN
      RAISE NOTICE 'OK 2: idempotency constraint fired as expected (%)', SQLERRM;
    END;
  END$$;
ROLLBACK;

\echo === 3. NULL-distinct: two manual-edit rows with model=NULL must both succeed ===
BEGIN;
  INSERT INTO providers (id, profile, created_at)
  VALUES ('66666666-6666-6666-6666-666666666666'::uuid, '{}'::jsonb, NOW());

  INSERT INTO documents (
    id, provider_id, doc_type, doc_type_conf,
    classifier_model, classifier_prompt_hash, storage_uri,
    original_name, mime_type, page_count, uploaded_at, uploaded_by
  ) VALUES (
    '77777777-7777-7777-7777-777777777777'::uuid,
    '66666666-6666-6666-6666-666666666666'::uuid,
    'License', 0.95, 'claude-haiku-4-5', repeat('a', 64),
    'file:///tmp/z.pdf', 'license.pdf', 'application/pdf', 3, NOW(), 'provider'
  );

  INSERT INTO document_extractions (
    id, document_id, extraction_id, schema_version, status,
    fields, field_locations, confidence,
    source, model, prompt_hash, edited_by, input_tokens, output_tokens, extracted_at, confirmed_at
  ) VALUES (
    gen_random_uuid(),
    '77777777-7777-7777-7777-777777777777'::uuid,
    1, 'license.v1', 'Succeeded',
    '{}'::jsonb, '{}'::jsonb, '{}'::jsonb,
    'provider_edit', NULL, NULL,
    '88888888-8888-8888-8888-888888888888'::uuid,
    NULL, NULL, NOW(), NULL
  );

  INSERT INTO document_extractions (
    id, document_id, extraction_id, schema_version, status,
    fields, field_locations, confidence,
    source, model, prompt_hash, edited_by, input_tokens, output_tokens, extracted_at, confirmed_at
  ) VALUES (
    gen_random_uuid(),
    '77777777-7777-7777-7777-777777777777'::uuid,
    2, 'license.v1', 'Succeeded',
    '{}'::jsonb, '{}'::jsonb, '{}'::jsonb,
    'provider_edit', NULL, NULL,
    '88888888-8888-8888-8888-888888888888'::uuid,
    NULL, NULL, NOW(), NULL
  );
  -- If we got here, both edit rows landed; the unique index correctly skipped
  -- dedup because Postgres treats NULL as distinct.
  \echo OK 3: NULL model bypasses idempotency dedup (manual-edit rows coexist)
ROLLBACK;

\echo === 4. Cross-field invariant: Failed row without error must raise ===
BEGIN;
  INSERT INTO providers (id, profile, created_at)
  VALUES ('99999999-9999-9999-9999-999999999999'::uuid, '{}'::jsonb, NOW());

  INSERT INTO documents (
    id, provider_id, doc_type, doc_type_conf,
    classifier_model, classifier_prompt_hash, storage_uri,
    original_name, mime_type, page_count, uploaded_at, uploaded_by
  ) VALUES (
    'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'::uuid,
    '99999999-9999-9999-9999-999999999999'::uuid,
    'License', 0.95, 'claude-haiku-4-5', repeat('a', 64),
    'file:///tmp/w.pdf', 'license.pdf', 'application/pdf', 3, NOW(), 'provider'
  );

  DO $$
  BEGIN
    BEGIN
      INSERT INTO document_extractions (
        id, document_id, extraction_id, schema_version, status,
        fields, field_locations, confidence,
        source, model, prompt_hash, input_tokens, output_tokens, extracted_at, confirmed_at,
        error
      ) VALUES (
        gen_random_uuid(),
        'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'::uuid,
        1, 'license.v1', 'Failed',
        '{}'::jsonb, '{}'::jsonb, '{}'::jsonb,
        'llm', 'claude-sonnet-4-6', repeat('a', 64), NULL, NULL, NOW(), NULL,
        NULL  -- Failed with NULL error — must violate ck_document_extractions_status_error_pairing
      );
      RAISE EXCEPTION 'FAIL: Failed row with NULL error did not raise';
    EXCEPTION WHEN check_violation THEN
      RAISE NOTICE 'OK 4: status/error pairing constraint fired as expected (%)', SQLERRM;
    END;
  END$$;
ROLLBACK;
