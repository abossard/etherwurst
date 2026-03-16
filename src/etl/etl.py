#!/usr/bin/env python3
"""
Ethereum ETL — Micro-batch Pipeline
Erigon → cryo (Rust) → Parquet → ClickHouse

Architecture:
  Processes blocks in small batches (default 500). Each batch:
    1. cryo extracts one dataset → Parquet files on disk
    2. HTTP POST sends each file to ClickHouse
    3. Cleanup Parquet files
    4. Save progress

  This bounds memory usage regardless of total block range and provides
  incremental progress — a crash only loses the current batch.

Environment:
  ERIGON_RPC_URL          Erigon JSON-RPC endpoint
  CH_URL                  ClickHouse HTTP endpoint (set to enable CH)
  CH_USER                 ClickHouse username
  CH_PASSWORD             ClickHouse password
  MAX_BLOCKS              Max blocks per run (default: 100000)
  BATCH_SIZE              Blocks per micro-batch (default: 500)
  CRYO_CONCURRENCY        cryo max concurrent requests (default: 4)
  CRYO_CHUNK_SIZE         cryo chunk size (default: 500)
  CRYO_RPS                cryo requests per second (default: 30)
  FIRST_RUN_LOOKBACK      Blocks to look back on first run (default: 1000)
"""
import glob as globmod
import os
import subprocess
import sys
import time

import requests as req

# ── Configuration ─────────────────────────────────────────────────────────────

ERIGON_RPC = os.environ.get("ERIGON_RPC_URL", "http://erigon:8545")

# ClickHouse target (enabled when CH_URL is set)
CH_URL = os.environ.get("CH_URL", "")
CH_ENABLED = bool(CH_URL)
CH_USER = os.environ.get("CH_USER", "default")
CH_PASSWORD = os.environ.get("CH_PASSWORD", "")

# Extraction tuning
MAX_BLOCKS = int(os.environ.get("MAX_BLOCKS", "100000"))
BATCH_SIZE = int(os.environ.get("BATCH_SIZE", "500"))
CRYO_CONCURRENCY = os.environ.get("CRYO_CONCURRENCY", "4")
CRYO_CHUNK_SIZE = os.environ.get("CRYO_CHUNK_SIZE", "500")
CRYO_RPS = os.environ.get("CRYO_RPS", "30")
FIRST_RUN_LOOKBACK = int(os.environ.get("FIRST_RUN_LOOKBACK", "1000"))

# Backfill overrides — set these to run a specific block range
START_BLOCK = os.environ.get("START_BLOCK")  # explicit start (overrides progress)
END_BLOCK = os.environ.get("END_BLOCK")      # explicit end (overrides chain head)
WORKER_ID = os.environ.get("WORKER_ID", "cryo")  # progress key for parallel workers

# Sidecar mode: loop continuously, sleeping LOOP_SLEEP_SECS between runs
SIDECAR_MODE = os.environ.get("SIDECAR_MODE", "false").lower() == "true"
LOOP_SLEEP_SECS = int(os.environ.get("LOOP_SLEEP_SECS", "1800"))

WORK_DIR = "/tmp/cryo-extract"

# cryo dataset → ClickHouse table mapping
CH_TABLES = {
    "blocks": "blocks",
    "transactions": "transactions",
    "logs": "logs",
    "contracts": "contracts",
}

# Optional filter: EXTRACT_DATASETS=contracts,blocks → only those datasets
_ds_filter = os.environ.get("EXTRACT_DATASETS", "")
if _ds_filter:
    _wanted = {d.strip() for d in _ds_filter.split(",")}
    CH_TABLES = {k: v for k, v in CH_TABLES.items() if k in _wanted}

# ── Helpers ───────────────────────────────────────────────────────────────────

_session = req.Session()
_adapter = req.adapters.HTTPAdapter(pool_connections=4, pool_maxsize=4)
_session.mount("http://", _adapter)
_session.mount("https://", _adapter)


def run(cmd, timeout=3600):
    print(f"  $ {' '.join(cmd[:6])}{'...' if len(cmd) > 6 else ''}")
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    if result.returncode != 0:
        if result.stdout:
            print(f"  STDOUT: {result.stdout[-500:]}", file=sys.stderr)
        if result.stderr:
            print(f"  STDERR: {result.stderr[-500:]}", file=sys.stderr)
        raise RuntimeError(f"Command failed ({result.returncode}): {cmd[0]}")
    return result


def get_chain_head():
    resp = _session.post(ERIGON_RPC, json={
        "jsonrpc": "2.0", "id": 1, "method": "eth_blockNumber", "params": []
    }, timeout=30)
    result = resp.json().get("result", "0x0")
    return int(result, 16)


def wait_for_erigon(max_wait=300):
    """Wait for Erigon RPC to become available (sidecar starts before Erigon)."""
    import urllib3
    urllib3.disable_warnings()
    for i in range(max_wait // 5):
        try:
            get_chain_head()
            return
        except Exception:
            if i == 0:
                print("  Waiting for Erigon RPC...")
            time.sleep(5)
    raise RuntimeError(f"Erigon RPC not available after {max_wait}s")


def cleanup():
    subprocess.run(["rm", "-rf", WORK_DIR], capture_output=True)


# ── ClickHouse ingestion ─────────────────────────────────────────────────────

def ingest_to_clickhouse(parquet_file, ch_table):
    """Ingest a Parquet file into ClickHouse via HTTP interface."""
    if not CH_ENABLED:
        return
    with open(parquet_file, "rb") as f:
        resp = _session.post(
            CH_URL,
            params={
                "query": f"INSERT INTO {ch_table} FORMAT Parquet",
                "user": CH_USER,
                "password": CH_PASSWORD,
            },
            data=f,
            headers={"Content-Type": "application/octet-stream"},
            timeout=300,
        )
    if resp.status_code != 200:
        print(f"    CH ingest error ({ch_table}): {resp.text[:200]}", file=sys.stderr)


def update_ch_progress(block_num):
    """Update progress in ClickHouse."""
    if not CH_ENABLED:
        return
    ts = time.strftime("%Y-%m-%d %H:%M:%S")
    try:
        _session.post(
            CH_URL,
            params={
                "query": f"INSERT INTO etl_progress VALUES('{WORKER_ID}',{block_num},'{ts}')",
                "user": CH_USER,
                "password": CH_PASSWORD,
            },
            timeout=30,
        )
    except Exception:
        pass


# ── ETL progress tracking ────────────────────────────────────────────────────

def get_last_ingested_block():
    if CH_ENABLED:
        try:
            resp = _session.get(CH_URL, params={
                "query": f"SELECT max(last_block) FROM etl_progress WHERE dataset = '{WORKER_ID}'",
                "user": CH_USER, "password": CH_PASSWORD,
            }, timeout=30)
            val = resp.text.strip()
            if val and val != "0":
                return int(val)
        except Exception as e:
            print(f"  Warning: Could not read CH progress: {e}")
    return 0


# ── Micro-batch processing ───────────────────────────────────────────────────

def process_batch(batch_start, batch_end):
    """Extract, ingest, and cleanup one micro-batch of blocks."""
    cleanup()
    os.makedirs(WORK_DIR, exist_ok=True)
    files_ingested = 0

    # Extract ALL datasets in a single cryo pass (one RPC sweep)
    dataset_names = list(CH_TABLES.keys())
    cmd = [
        "cryo", *dataset_names,
        "-b", f"{batch_start}:{batch_end}",
        "--rpc", ERIGON_RPC,
        "-o", WORK_DIR,
        "--chunk-size", CRYO_CHUNK_SIZE,
        "--max-concurrent-requests", CRYO_CONCURRENCY,
        "--requests-per-second", CRYO_RPS,
        "--no-report",
        "--hex",
    ]
    run(cmd, timeout=3600)

    # Ingest each dataset's parquet files into the corresponding table
    for dataset in dataset_names:
        ch_table = CH_TABLES[dataset]

        # cryo names files like ethereum__<dataset>__*.parquet
        parquet_files = globmod.glob(f"{WORK_DIR}/**/*{dataset}*.parquet", recursive=True)
        if not parquet_files:
            parquet_files = globmod.glob(f"{WORK_DIR}/*{dataset}*.parquet")

        if not parquet_files:
            print(f"    {ch_table}: no files produced")
            continue

        for f in parquet_files:
            ingest_to_clickhouse(f, ch_table)
            files_ingested += 1

        print(f"    {ch_table}: {len(parquet_files)} files ingested → CH")

    cleanup()
    return files_ingested


# ── Main pipeline ─────────────────────────────────────────────────────────────

def main():
    print("═══ ETL (Micro-batch) starting ═══")
    print(f"  Erigon:  {ERIGON_RPC}")
    print(f"  CH:      {CH_URL if CH_ENABLED else 'disabled'}")
    print(f"  Batch:   {BATCH_SIZE} blocks")
    print(f"  cryo:    concurrency={CRYO_CONCURRENCY}, chunk={CRYO_CHUNK_SIZE}, rps={CRYO_RPS}")

    if not CH_ENABLED:
        print("  ERROR: No ingest target configured (set CH_URL)")
        sys.exit(1)

    wait_for_erigon()

    chain_head = get_chain_head()
    print(f"  Chain head: {chain_head}")
    print(f"  Worker:  {WORKER_ID}")

    # Determine block range — explicit overrides take priority
    if START_BLOCK is not None:
        start = int(START_BLOCK)
        print(f"  Explicit START_BLOCK: {start}")
    else:
        last_block = get_last_ingested_block()
        print(f"  Last ingested ({WORKER_ID}): {last_block}")
        if last_block == 0:
            start = max(chain_head - FIRST_RUN_LOOKBACK, 0)
            print(f"  First run — starting from block {start}")
        else:
            start = last_block + 1

    if END_BLOCK is not None:
        end = min(int(END_BLOCK), chain_head)
        print(f"  Explicit END_BLOCK: {end}")
    else:
        end = min(start + MAX_BLOCKS - 1, chain_head)

    if start > end:
        print("  Already up to date!")
        return

    total_blocks = end - start + 1
    n_batches = (total_blocks + BATCH_SIZE - 1) // BATCH_SIZE
    print(f"  Target: blocks {start} → {end} ({total_blocks:,} blocks, {n_batches} batches)")

    t_total = time.time()
    total_files = 0
    batches_done = 0

    for batch_idx in range(n_batches):
        batch_start = start + batch_idx * BATCH_SIZE
        batch_end = min(batch_start + BATCH_SIZE, end + 1)

        t_batch = time.time()
        print(f"\n── Batch {batch_idx + 1}/{n_batches}: blocks {batch_start}→{batch_end - 1} ──")

        try:
            files = process_batch(batch_start, batch_end)
            total_files += files
            batches_done += 1

            # Save progress after each successful batch
            update_ch_progress(batch_end - 1)

            elapsed = time.time() - t_batch
            bps = (batch_end - batch_start) / elapsed if elapsed > 0 else 0
            print(f"    Done in {elapsed:.0f}s ({bps:.0f} blocks/sec, {files} files)")

        except Exception as e:
            print(f"    FAILED: {e}", file=sys.stderr)
            cleanup()
            # Continue with next batch — progress was saved for previous batches
            continue

    elapsed = time.time() - t_total
    print(f"\n═══ ETL complete in {elapsed:.0f}s ═══")
    print(f"  {batches_done}/{n_batches} batches, {total_files} files ingested")


if __name__ == "__main__":
    if SIDECAR_MODE:
        print("═══ Sidecar mode: looping continuously ═══")
        while True:
            try:
                main()
            except Exception as e:
                print(f"Run failed: {e}", file=sys.stderr)
            print(f"  Sleeping {LOOP_SLEEP_SECS}s before next run...")
            time.sleep(LOOP_SLEEP_SECS)
    else:
        main()
