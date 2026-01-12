# Batch Weigh + Auto Print Spec (v1)

## 1) FSM: Batch Weigh & Auto Print (To'liq)

Parameters (static):
- T_SETTLE = 0.50s
- T_CLEAR = 0.70s
- N_MIN = 10
- EMPTY_THRESH = noise-based (see formulas)
- PLACEMENT_MIN = config placement_min_weight (see formulas)

Definitions:
- placement_id increments on rising edge (weight >= PLACEMENT_MIN after EMPTY).
- print_sent is false on entering LOADING/SETTLING/LOCKED.
- product switch applies only in WAIT_EMPTY; in other states it is queued as pending_product.
- pending_product applies only on entering WAIT_EMPTY or on weight < EMPTY_THRESH for T_CLEAR events.
- 1 placement = 1 label: event_id is created only on SETTLING -> LOCKED transition; no new event is created until POST_GUARD returns to WAIT_EMPTY.

| CurrentState | Guard/Condition | Action | NextState |
| --- | --- | --- | --- |
| WAIT_EMPTY | batch_start(product_id, batch_id) | set active_batch, active_product, clear pending_product | WAIT_EMPTY |
| WAIT_EMPTY | product_switch(product_id) | set pending_product | WAIT_EMPTY |
| WAIT_EMPTY | on_enter | apply pending_product if set | WAIT_EMPTY |
| WAIT_EMPTY | batch_stop | set pause_reason = BATCH_STOP | PAUSED |
| WAIT_EMPTY | weight >= PLACEMENT_MIN | placement_id++; print_sent = false; reset filters; start settle timer | LOADING |
| LOADING | batch_stop | set pause_reason = BATCH_STOP | PAUSED |
| LOADING | product_switch(product_id) | set pending_product | LOADING |
| LOADING | weight < EMPTY_THRESH for T_CLEAR | reset filters; apply pending_product | WAIT_EMPTY |
| LOADING | time_in_state >= T_SETTLE AND window.sample_count >= N_MIN | enable stability window | SETTLING |
| SETTLING | batch_stop | set pause_reason = BATCH_STOP | PAUSED |
| SETTLING | product_switch(product_id) | set pending_product | SETTLING |
| SETTLING | weight < EMPTY_THRESH for T_CLEAR | reset filters; apply pending_product | WAIT_EMPTY |
| SETTLING | stable == true | lock_weight = mean(window); create event_id; print_sent = false | LOCKED |
| LOCKED | batch_stop | set pause_reason = BATCH_STOP | PAUSED |
| LOCKED | product_switch(product_id) | set pending_product | LOCKED |
| LOCKED | abs(weight - lock_weight) > CHANGE_LIMIT AND print_sent == false | reset filters | SETTLING |
| LOCKED | abs(weight - lock_weight) > CHANGE_LIMIT AND print_sent == true | set pause_reason = REWEIGH_REQUIRED | PAUSED |
| LOCKED | print_sent == false AND printer_ready == true | enqueue_print(event_id); print_sent = true | PRINTING |
| LOCKED | print_sent == false AND printer_ready == false | set pause_reason = PRINTER_OFFLINE | PAUSED |
| PRINTING | batch_stop | set pause_reason = BATCH_STOP | PAUSED |
| PRINTING | abs(weight - lock_weight) > CHANGE_LIMIT | set pause_reason = REWEIGH_REQUIRED | PAUSED |
| PRINTING | ack == RECEIVED | mark_job(RECEIVED) | PRINTING |
| PRINTING | ack == COMPLETED | mark_job(COMPLETED); start post_guard timer | POST_GUARD |
| PRINTING | send_timeout (no RECEIVED) | mark_job(RETRY) | PRINTING |
| PRINTING | completed_timeout (RECEIVED but no COMPLETED) | set pause_reason = PRINT_TIMEOUT | PAUSED |
| POST_GUARD | batch_stop | set pause_reason = BATCH_STOP | PAUSED |
| POST_GUARD | weight < EMPTY_THRESH for T_CLEAR | mark_job(DONE); unlock; apply pending_product | WAIT_EMPTY |
| POST_GUARD | weight >= EMPTY_THRESH | no-op | POST_GUARD |
| PAUSED | operator_resume AND weight < EMPTY_THRESH | clear pause_reason; unlock; apply pending_product | WAIT_EMPTY |
| PAUSED | operator_resume AND weight >= EMPTY_THRESH | keep paused (reweigh requires removal) | PAUSED |

## 2) Stable detection (o'lchovli)

Calibration (30s empty log):
- median = median(x_i)
- sigma = 1.4826 * median(|x_i - median|)
- res = smallest non-zero diff between consecutive samples
- EPS = max(3 * sigma, 2 * res)
- EPS_ALIGN = max(2 * EPS, 2 * sigma, 3 * res)
- WINDOW = max(0.80s, 30 * median_dt)
- EMPTY_THRESH = max(3 * sigma, 2 * res)
- PLACEMENT_MIN = max(config.placement_min_weight, 5 * sigma, 2 * res)
- CHANGE_LIMIT = max(4 * sigma, 0.005 * lock_weight, 2 * res)
- SLOPE_LIMIT = 2 * sigma / WINDOW

Median prefilter + fast/slow EMA:
- M = 5 samples
- m_t = median(last M raw samples)
- alpha_f = 1 - exp(-dt / 0.20s)
- alpha_s = 1 - exp(-dt / 1.00s)
- fast_t = alpha_f * m_t + (1 - alpha_f) * fast_{t-1}
- slow_t = alpha_s * m_t + (1 - alpha_s) * slow_{t-1}

Jitter/latency spike handling:
- if dt > 3 * median_dt then mark sample invalid and do not update EMA or window.
- window uses only valid samples within last WINDOW seconds.

Stable boolean formula:
- Let W be valid m_t samples within last WINDOW seconds.
- stable = (mean(W) >= PLACEMENT_MIN)
          AND (max(W) - min(W) <= EPS)
          AND (abs(fast_t - slow_t) <= EPS_ALIGN)
          AND (abs(slope) <= SLOPE_LIMIT)
- slope = (slow_t - slow_{t-WINDOW}) / WINDOW

Pseudocode:
```
if dt > 3*median_dt: invalid_sample
else:
  m = median(last5)
  fast = ema(m, fast, alpha_f)
  slow = ema(m, slow, alpha_s)
  window.add(m, t)
  if window.time_span >= WINDOW:
    stable = mean(window) >= PLACEMENT_MIN
          && range(window) <= EPS
          && abs(fast - slow) <= EPS_ALIGN
          && abs(slope) <= SLOPE_LIMIT
```

## 3) Atomic batch_seq + event creation

SQLite schema:
```sql
CREATE TABLE batch_state (
  device_id TEXT PRIMARY KEY,
  batch_id TEXT NOT NULL,
  product_id TEXT NOT NULL,
  next_seq INTEGER NOT NULL,
  status TEXT NOT NULL,
  updated_at INTEGER NOT NULL
);

CREATE TABLE batch_runs (
  run_id TEXT PRIMARY KEY,
  device_id TEXT NOT NULL,
  batch_id TEXT NOT NULL,
  product_id TEXT NOT NULL,
  started_at INTEGER NOT NULL,
  stopped_at INTEGER,
  stop_reason TEXT,
  created_at INTEGER NOT NULL
);

CREATE TABLE print_jobs (
  job_id TEXT PRIMARY KEY,
  event_id TEXT NOT NULL UNIQUE,
  device_id TEXT NOT NULL,
  batch_id TEXT NOT NULL,
  seq INTEGER NOT NULL,
  status TEXT NOT NULL,
  completion_mode TEXT NOT NULL DEFAULT 'STATUS_QUERY',
  payload_json TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  lease_expires_at INTEGER,
  next_retry_at INTEGER,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  last_error TEXT,
  UNIQUE(batch_id, seq)
);
```

Atomic creation (C# pseudocode):
```
BEGIN IMMEDIATE;
row = SELECT next_seq FROM batch_state WHERE device_id = :dev;
seq = row.next_seq;
UPDATE batch_state SET next_seq = next_seq + 1, updated_at = now WHERE device_id = :dev;
INSERT INTO print_jobs(job_id, event_id, device_id, batch_id, seq, status, payload_json, payload_hash, created_at, updated_at)
VALUES(uuid(), uuid(), :dev, :batch, seq, 'NEW', :payload, :hash, now, now);
COMMIT;
```

Restart proof:
- If crash before COMMIT, transaction rolls back and next_seq is unchanged.
- If crash after COMMIT, next_seq and job are persisted together.
- Therefore sequence monotonic and gap-free per device_id.

SQL diff:
```sql
ALTER TABLE print_jobs ADD COLUMN completion_mode TEXT NOT NULL DEFAULT 'STATUS_QUERY';
CREATE TABLE batch_runs (
  run_id TEXT PRIMARY KEY,
  device_id TEXT NOT NULL,
  batch_id TEXT NOT NULL,
  product_id TEXT NOT NULL,
  started_at INTEGER NOT NULL,
  stopped_at INTEGER,
  stop_reason TEXT,
  created_at INTEGER NOT NULL
);
```

## 4) Outbox / Print Queue (Zebra)

Status flow:
- NEW -> SENT -> RECEIVED -> COMPLETED -> DONE
- NEW/SENT/RECEIVED can move to RETRY; RETRY can move to SENT.
- Any state can move to FAIL when max attempts reached.

Retry policy:
- backoff_ms = min(60000, 1000 * 2^(attempts-1))
- max_attempts = 8

Exactly-once:
- event_id UNIQUE + (batch_id, seq) UNIQUE.
- If INSERT violates uniqueness, treat as duplicate and do not enqueue.

Worker loop (pseudocode):
```
BEGIN IMMEDIATE;
job = SELECT * FROM print_jobs
      WHERE status IN ('NEW','RETRY') AND next_retry_at <= now
      ORDER BY created_at LIMIT 1;
UPDATE print_jobs
  SET status='SENT', attempts=attempts+1, lease_expires_at=now+1500, updated_at=now
  WHERE job_id = job.job_id;
COMMIT;

send_to_printer(job);
if ack_received: UPDATE status='RECEIVED';
if completed: UPDATE status='COMPLETED' then status='DONE';
if timeout: UPDATE status='RETRY', next_retry_at=now+backoff;
if attempts >= max_attempts: UPDATE status='FAIL';
```

## 5) Zebra ACK semantics

Protocol:
- ZPL over RAW transport (USB bulk or TCP 9100).
- COMPLETED probe is pluggable: STATUS_QUERY or SCAN_RECON.

ACK meaning:
- RECEIVED: payload accepted AND status query returns READY with no error flags.
- COMPLETED: STATUS_QUERY returns READY and job buffer empty AND RFID status OK.
- SCAN_RECON: COMPLETED is emitted only after scan reconciliation confirms the tag.

RFID encode success/fail:
- After send, issue RFID status query; parse response field RFID_OK=1.
- If RFID_OK=0 or no response, mark FAIL and pause.

Timeout + fallback:
- RECEIVED timeout: 1500ms -> RETRY
- COMPLETED timeout: 5000ms -> PAUSED with reason PRINT_TIMEOUT
- If transceive not available, set completion_mode=SCAN_RECON and require scan reconciliation.

## 6) ERPNext Edge API Contract

Endpoints:
- POST /api/method/rfidenter.edge_batch_start
- POST /api/method/rfidenter.edge_batch_stop
- GET  /api/method/rfidenter.device_status?device_id=...
- POST /api/method/rfidenter.edge_event_report

Common headers:
- Authorization: token <key>:<secret>
- Idempotency-Key: <uuid>
- X-Device-Id: <device_id>

Payloads:
- batch_start: { device_id, batch_id, product_id, operator_id, started_at, config:{ placement_min_weight } }
- batch_stop:  { device_id, batch_id, reason, stopped_at }
- device_status response: { device_id, batch_id, product_id, printer_status, last_event_seq }
- event_report: { event_id, batch_id, seq, product_id, device_id, weight, unit, stable, locked_at, printed_at, printer_status }

Idempotency:
- event_id is the idempotency key for event_report.
- server returns 200 with { ok:true, duplicate:true } on duplicates.

Conflict policy:
- 409 if batch_id does not match active batch for device_id.
- 409 if product_id does not match active product for batch_id.
- 401 for auth, 422 for invalid payload, 503 for printer or ERP outage.
