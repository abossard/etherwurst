#!/usr/bin/env python3
"""
Ethereum ETL — Micro-batch Pipeline
Erigon → cryo (Rust) → Parquet → ADX + ClickHouse

Architecture:
  Processes blocks in small batches (default 500). Each batch:
    1. cryo extracts one dataset → Parquet files on disk
    2. ingest_from_file sends each file to ADX and/or ClickHouse
    3. Cleanup Parquet files
    4. Save progress

  This bounds memory usage regardless of total block range and provides
  incremental progress — a crash only loses the current batch.

Environment:
  ERIGON_RPC_URL          Erigon JSON-RPC endpoint
  ADX_CLUSTER_URI         ADX cluster URI (set to enable ADX)
  ADX_DATABASE            ADX database name
  AZURE_RESOURCE_GROUP    Azure resource group (for ADX start/stop)
  STOP_ADX_AFTER          Stop ADX cluster after run (default: false)
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

# ADX target (enabled when ADX_CLUSTER_URI is set)
ADX_CLUSTER_URI = os.environ.get("ADX_CLUSTER_URI", "")
ADX_ENABLED = bool(ADX_CLUSTER_URI)
ADX_DATABASE = os.environ.get("ADX_DATABASE", "ethereum")
AZURE_RESOURCE_GROUP = os.environ.get("AZURE_RESOURCE_GROUP", "")
STOP_ADX_AFTER = os.environ.get("STOP_ADX_AFTER", "false").lower() == "true"

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

# cryo dataset → ADX table / ClickHouse table mapping
DATASETS = {
    "blocks": "Blocks",
    "transactions": "Transactions",
    "logs": "Logs",
    "contracts": "Contracts",
}
# ClickHouse uses lowercase table names
CH_TABLES = {
    "blocks": "blocks",
    "transactions": "transactions",
    "logs": "logs",
    "contracts": "contracts",
}

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


# ── ADX client setup (conditional) ────────────────────────────────────────────

def get_kusto_client():
    if not ADX_ENABLED:
        return None
    from azure.identity import DefaultAzureCredential
    from azure.kusto.data import KustoClient, KustoConnectionStringBuilder
    credential = DefaultAzureCredential()
    kcsb = KustoConnectionStringBuilder.with_azure_token_credential(ADX_CLUSTER_URI, credential)
    return KustoClient(kcsb)


def get_ingest_client():
    if not ADX_ENABLED:
        return None
    from azure.identity import DefaultAzureCredential
    from azure.kusto.ingest import QueuedIngestClient
    from azure.kusto.data import KustoConnectionStringBuilder
    credential = DefaultAzureCredential()
    ingest_uri = ADX_CLUSTER_URI.replace("https://", "https://ingest-")
    kcsb = KustoConnectionStringBuilder.with_azure_token_credential(ingest_uri, credential)
    return QueuedIngestClient(kcsb)


def kusto_query(client, query):
    if not client:
        return []
    response = client.execute(ADX_DATABASE, query)
    return [row for row in response.primary_results[0]]


# ── ADX cluster management ───────────────────────────────────────────────────

def ensure_adx_running():
    if not ADX_ENABLED:
        return
    cluster_name = ADX_CLUSTER_URI.split("//")[1].split(".")[0]
    result = subprocess.run(
        ["az", "kusto", "cluster", "show", "--name", cluster_name,
         "--resource-group", AZURE_RESOURCE_GROUP, "--query", "state", "-o", "tsv"],
        capture_output=True, text=True, timeout=60
    )
    state = result.stdout.strip()
    print(f"  ADX cluster state: {state}")
    if state == "Stopped":
        print("  Starting ADX cluster...")
        run(["az", "kusto", "cluster", "start", "--name", cluster_name,
             "--resource-group", AZURE_RESOURCE_GROUP], timeout=600)
        run(["az", "kusto", "cluster", "wait", "--name", cluster_name,
             "--resource-group", AZURE_RESOURCE_GROUP,
             "--custom", "provisioningState=='Succeeded'"], timeout=600)
        print("  ADX cluster started.")


def stop_adx():
    if not ADX_ENABLED:
        return
    cluster_name = ADX_CLUSTER_URI.split("//")[1].split(".")[0]
    print("  Stopping ADX cluster...")
    subprocess.run(
        ["az", "kusto", "cluster", "stop", "--name", cluster_name,
         "--resource-group", AZURE_RESOURCE_GROUP, "--no-wait"],
        capture_output=True, text=True, timeout=60
    )


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

def get_last_ingested_block(client):
    # Try ADX first
    if client:
        try:
            rows = kusto_query(client, f"EtlProgress | where dataset == '{WORKER_ID}' | summarize max(last_block)")
            if rows and rows[0][0] is not None:
                return int(rows[0][0])
        except Exception as e:
            print(f"  Warning: Could not read ADX progress: {e}")
    # Fall back to ClickHouse
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


def update_progress(client, block_num):
    if not client:
        return
    ts = time.strftime("%Y-%m-%dT%H:%M:%SZ")
    try:
        kusto_query(client, f".ingest inline into table EtlProgress <| {WORKER_ID},{block_num},{ts}")
    except Exception as e:
        print(f"  Warning: Could not update progress: {e}")


# ── Micro-batch processing ───────────────────────────────────────────────────

def process_batch(batch_start, batch_end, ingest_client):
    """Extract, ingest, and cleanup one micro-batch of blocks."""
    cleanup()
    os.makedirs(WORK_DIR, exist_ok=True)
    files_ingested = 0

    for dataset, table in DATASETS.items():
        # Extract this dataset for the batch
        cmd = [
            "cryo", dataset,
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

        # Find ALL parquet files (cryo may use subdirectories)
        parquet_files = globmod.glob(f"{WORK_DIR}/**/*.parquet", recursive=True)
        if not parquet_files:
            # Fallback: check top level
            parquet_files = globmod.glob(f"{WORK_DIR}/*.parquet")

        if not parquet_files:
            print(f"    {table}: no files produced")
            continue

        # Ingest each file
        adx_props = None
        if ADX_ENABLED and ingest_client:
            from azure.kusto.ingest import IngestionProperties
            from azure.kusto.data import DataFormat
            adx_props = IngestionProperties(
                database=ADX_DATABASE,
                table=table,
                data_format=DataFormat.PARQUET,
            )

        ch_table = CH_TABLES.get(dataset)
        for f in parquet_files:
            if adx_props:
                ingest_client.ingest_from_file(f, ingestion_properties=adx_props)
            if ch_table:
                ingest_to_clickhouse(f, ch_table)
            files_ingested += 1

        # Remove ingested files to free disk space before next dataset
        for f in parquet_files:
            os.remove(f)

        tags = []
        if ADX_ENABLED:
            tags.append("ADX")
        if CH_ENABLED:
            tags.append("CH")
        print(f"    {table}: {len(parquet_files)} files ingested → {'+'.join(tags)}")

    cleanup()
    return files_ingested


# ── Main pipeline ─────────────────────────────────────────────────────────────

def main():
    print("═══ ETL (Micro-batch) starting ═══")
    print(f"  Erigon:  {ERIGON_RPC}")
    print(f"  ADX:     {ADX_CLUSTER_URI if ADX_ENABLED else 'disabled'}")
    print(f"  CH:      {CH_URL if CH_ENABLED else 'disabled'}")
    print(f"  Batch:   {BATCH_SIZE} blocks")
    print(f"  cryo:    concurrency={CRYO_CONCURRENCY}, chunk={CRYO_CHUNK_SIZE}, rps={CRYO_RPS}")

    if not ADX_ENABLED and not CH_URL:
        print("  ERROR: No ingest target configured (set ADX_ENABLED or CH_ENABLED)")
        sys.exit(1)

    wait_for_erigon()

    # Azure CLI login (needed for ADX; skip if ADX disabled)
    if ADX_ENABLED:
        client_id = os.environ.get("AZURE_CLIENT_ID", "")
        tenant_id = os.environ.get("AZURE_TENANT_ID", "")
        token_file = os.environ.get("AZURE_FEDERATED_TOKEN_FILE", "")
        if client_id and tenant_id and token_file:
            with open(token_file) as f:
                token = f.read().strip()
            run(["az", "login", "--service-principal",
                 "-u", client_id, "-t", tenant_id,
                 "--federated-token", token,
                 "--allow-no-subscriptions"], timeout=60)
        else:
            run(["az", "login", "--identity", "--allow-no-subscriptions"], timeout=60)

    chain_head = get_chain_head()
    print(f"  Chain head: {chain_head}")
    print(f"  Worker:  {WORKER_ID}")

    ensure_adx_running()

    kusto = get_kusto_client()
    ingest = get_ingest_client()

    # Determine block range — explicit overrides take priority
    if START_BLOCK is not None:
        start = int(START_BLOCK)
        print(f"  Explicit START_BLOCK: {start}")
    else:
        last_block = get_last_ingested_block(kusto)
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
            files = process_batch(batch_start, batch_end, ingest)
            total_files += files
            batches_done += 1

            # Save progress after each successful batch
            update_progress(kusto, batch_end - 1)
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

    if STOP_ADX_AFTER:
        stop_adx()


def run_once():
    """Single ETL run — used by both CronJob and sidecar loop."""
    main()


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
