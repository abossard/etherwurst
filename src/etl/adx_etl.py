#!/usr/bin/env python3
"""
Ethereum ADX ETL — Micro-batch Pipeline
Erigon → cryo (Rust) → Parquet → ADX (direct ingest)

Architecture:
  Processes blocks in small batches (default 500). Each batch:
    1. cryo extracts one dataset → Parquet files on disk
    2. ingest_from_file sends each file to ADX (SDK handles blob staging)
    3. Cleanup Parquet files
    4. Save progress to ADX

  This bounds memory usage regardless of total block range and provides
  incremental progress — a crash only loses the current batch.

Environment:
  ERIGON_RPC_URL          Erigon JSON-RPC endpoint
  ADX_CLUSTER_URI         ADX cluster URI
  ADX_DATABASE            ADX database name
  AZURE_RESOURCE_GROUP    Azure resource group
  MAX_BLOCKS              Max blocks per run (default: 100000)
  BATCH_SIZE              Blocks per micro-batch (default: 500)
  CRYO_CONCURRENCY        cryo max concurrent requests (default: 4)
  CRYO_CHUNK_SIZE         cryo chunk size (default: 500)
  CRYO_RPS                cryo requests per second (default: 30)
  STOP_ADX_AFTER          Stop ADX cluster after run (default: false)
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
ADX_CLUSTER_URI = os.environ["ADX_CLUSTER_URI"]
ADX_DATABASE = os.environ.get("ADX_DATABASE", "ethereum")
RESOURCE_GROUP = os.environ.get("AZURE_RESOURCE_GROUP", "")
MAX_BLOCKS = int(os.environ.get("MAX_BLOCKS", "100000"))
BATCH_SIZE = int(os.environ.get("BATCH_SIZE", "500"))
CRYO_CONCURRENCY = os.environ.get("CRYO_CONCURRENCY", "4")
CRYO_CHUNK_SIZE = os.environ.get("CRYO_CHUNK_SIZE", "500")
CRYO_RPS = os.environ.get("CRYO_RPS", "30")
STOP_ADX_AFTER = os.environ.get("STOP_ADX_AFTER", "false").lower() == "true"
FIRST_RUN_LOOKBACK = int(os.environ.get("FIRST_RUN_LOOKBACK", "1000"))

WORK_DIR = "/tmp/cryo-extract"

# cryo dataset → ADX table mapping
DATASETS = {
    "blocks": "Blocks",
    "transactions": "Transactions",
    "logs": "Logs",
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


def cleanup():
    subprocess.run(["rm", "-rf", WORK_DIR], capture_output=True)


# ── ADX client setup ─────────────────────────────────────────────────────────

def get_kusto_client():
    from azure.identity import DefaultAzureCredential
    from azure.kusto.data import KustoClient, KustoConnectionStringBuilder
    credential = DefaultAzureCredential()
    kcsb = KustoConnectionStringBuilder.with_azure_token_credential(ADX_CLUSTER_URI, credential)
    return KustoClient(kcsb)


def get_ingest_client():
    from azure.identity import DefaultAzureCredential
    from azure.kusto.ingest import QueuedIngestClient
    from azure.kusto.data import KustoConnectionStringBuilder
    credential = DefaultAzureCredential()
    ingest_uri = ADX_CLUSTER_URI.replace("https://", "https://ingest-")
    kcsb = KustoConnectionStringBuilder.with_azure_token_credential(ingest_uri, credential)
    return QueuedIngestClient(kcsb)


def kusto_query(client, query):
    response = client.execute(ADX_DATABASE, query)
    return [row for row in response.primary_results[0]]


# ── ADX cluster management ───────────────────────────────────────────────────

def ensure_adx_running():
    cluster_name = ADX_CLUSTER_URI.split("//")[1].split(".")[0]
    result = subprocess.run(
        ["az", "kusto", "cluster", "show", "--name", cluster_name,
         "--resource-group", RESOURCE_GROUP, "--query", "state", "-o", "tsv"],
        capture_output=True, text=True, timeout=60
    )
    state = result.stdout.strip()
    print(f"  ADX cluster state: {state}")
    if state == "Stopped":
        print("  Starting ADX cluster...")
        run(["az", "kusto", "cluster", "start", "--name", cluster_name,
             "--resource-group", RESOURCE_GROUP], timeout=600)
        run(["az", "kusto", "cluster", "wait", "--name", cluster_name,
             "--resource-group", RESOURCE_GROUP,
             "--custom", "provisioningState=='Succeeded'"], timeout=600)
        print("  ADX cluster started.")


def stop_adx():
    cluster_name = ADX_CLUSTER_URI.split("//")[1].split(".")[0]
    print("  Stopping ADX cluster...")
    subprocess.run(
        ["az", "kusto", "cluster", "stop", "--name", cluster_name,
         "--resource-group", RESOURCE_GROUP, "--no-wait"],
        capture_output=True, text=True, timeout=60
    )


# ── ETL progress tracking ────────────────────────────────────────────────────

def get_last_ingested_block(client):
    try:
        rows = kusto_query(client, "EtlProgress | where dataset == 'cryo' | summarize max(last_block)")
        if rows and rows[0][0] is not None:
            return int(rows[0][0])
    except Exception as e:
        print(f"  Warning: Could not read progress: {e}")
    return 0


def update_progress(client, block_num):
    ts = time.strftime("%Y-%m-%dT%H:%M:%SZ")
    try:
        kusto_query(client, f".ingest inline into table EtlProgress <| cryo,{block_num},{ts}")
    except Exception as e:
        print(f"  Warning: Could not update progress: {e}")


# ── Micro-batch processing ───────────────────────────────────────────────────

def process_batch(batch_start, batch_end, ingest_client):
    """Extract, ingest, and cleanup one micro-batch of blocks."""
    from azure.kusto.ingest import IngestionProperties
    from azure.kusto.data import DataFormat

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

        # Ingest each file directly (SDK handles temporary blob staging)
        props = IngestionProperties(
            database=ADX_DATABASE,
            table=table,
            data_format=DataFormat.PARQUET,
        )
        for f in parquet_files:
            ingest_client.ingest_from_file(f, ingestion_properties=props)
            files_ingested += 1

        # Remove ingested files to free disk space before next dataset
        for f in parquet_files:
            os.remove(f)

        print(f"    {table}: {len(parquet_files)} files ingested")

    cleanup()
    return files_ingested


# ── Main pipeline ─────────────────────────────────────────────────────────────

def main():
    print("═══ ADX ETL (Micro-batch) starting ═══")
    print(f"  Erigon:  {ERIGON_RPC}")
    print(f"  ADX:     {ADX_CLUSTER_URI}")
    print(f"  Batch:   {BATCH_SIZE} blocks")
    print(f"  cryo:    concurrency={CRYO_CONCURRENCY}, chunk={CRYO_CHUNK_SIZE}, rps={CRYO_RPS}")

    # Azure CLI login (workload identity uses federated token, not managed identity)
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

    ensure_adx_running()

    kusto = get_kusto_client()
    ingest = get_ingest_client()

    last_block = get_last_ingested_block(kusto)
    print(f"  Last ingested: {last_block}")

    if last_block == 0:
        start = max(chain_head - FIRST_RUN_LOOKBACK, 0)
        print(f"  First run — starting from block {start}")
    else:
        start = last_block + 1

    if start > chain_head:
        print("  Already up to date!")
        return

    end = min(start + MAX_BLOCKS - 1, chain_head)
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


if __name__ == "__main__":
    main()
