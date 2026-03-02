#!/usr/bin/env python3
"""
Ethereum ADX ETL — Optimal Pipeline
Erigon → cryo (Rust, parallel) → Parquet/Snappy → Azure Blob → ADX queued ingest

Designed for:
  - Maximum extraction speed via cryo (500-1500 blocks/sec)
  - Parquet ingestion into ADX (2-3x faster than JSON)
  - Zero startup overhead (all deps pre-installed in Docker image)
  - Cost optimization (runs on schedule, ADX auto-stops when idle)

Environment:
  ERIGON_RPC_URL          Erigon JSON-RPC endpoint
  ADX_CLUSTER_URI         ADX cluster URI
  ADX_DATABASE            ADX database name
  STORAGE_ACCOUNT_NAME    Azure Storage account
  STORAGE_CONTAINER       Blob container (default: ethereum-etl)
  AZURE_RESOURCE_GROUP    Azure resource group
  MAX_BLOCKS              Max blocks per run (default: 100000)
  CRYO_CONCURRENCY        cryo max concurrent requests (default: 32)
  CRYO_CHUNK_SIZE         cryo chunk size (default: 1000)
  CRYO_RPS                cryo requests per second (default: 100)
  STOP_ADX_AFTER          Stop ADX cluster after run (default: false)
"""
import glob as globmod
import json
import os
import subprocess
import sys
import time

import requests as req

# ── Configuration ─────────────────────────────────────────────────────────────

ERIGON_RPC = os.environ.get("ERIGON_RPC_URL", "http://erigon:8545")
ADX_CLUSTER_URI = os.environ["ADX_CLUSTER_URI"]
ADX_DATABASE = os.environ.get("ADX_DATABASE", "ethereum")
STORAGE_ACCOUNT = os.environ["STORAGE_ACCOUNT_NAME"]
STORAGE_CONTAINER = os.environ.get("STORAGE_CONTAINER", "ethereum-etl")
RESOURCE_GROUP = os.environ.get("AZURE_RESOURCE_GROUP", "")
MAX_BLOCKS = int(os.environ.get("MAX_BLOCKS", "100000"))
CRYO_CONCURRENCY = os.environ.get("CRYO_CONCURRENCY", "32")
CRYO_CHUNK_SIZE = os.environ.get("CRYO_CHUNK_SIZE", "1000")
CRYO_RPS = os.environ.get("CRYO_RPS", "100")
STOP_ADX_AFTER = os.environ.get("STOP_ADX_AFTER", "false").lower() == "true"
FIRST_RUN_LOOKBACK = int(os.environ.get("FIRST_RUN_LOOKBACK", "10000"))

WORK_DIR = "/tmp/cryo-extract"

# cryo dataset → ADX table mapping
DATASETS = {
    "blocks": "Blocks",
    "transactions": "Transactions",
    "logs": "Logs",
    "traces": "Traces",
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
        print(f"  STDOUT: {result.stdout[-500:]}" if result.stdout else "", file=sys.stderr)
        print(f"  STDERR: {result.stderr[-500:]}" if result.stderr else "", file=sys.stderr)
        raise RuntimeError(f"Command failed ({result.returncode}): {cmd[0]}")
    return result


def get_chain_head():
    resp = _session.post(ERIGON_RPC, json={
        "jsonrpc": "2.0", "id": 1, "method": "eth_blockNumber", "params": []
    }, timeout=30)
    result = resp.json().get("result", "0x0")
    return int(result, 16)


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


# ── Step 1: Extract with cryo ────────────────────────────────────────────────

def run_cryo(start_block, end_block):
    os.makedirs(WORK_DIR, exist_ok=True)
    cmd = [
        "cryo",
        *list(DATASETS.keys()),
        "-b", f"{start_block}:{end_block}",
        "--rpc", ERIGON_RPC,
        "-o", WORK_DIR,
        "--output-format", "parquet",
        "--compression", "snappy",
        "--chunk-size", CRYO_CHUNK_SIZE,
        "--max-concurrent-requests", CRYO_CONCURRENCY,
        "--requests-per-second", CRYO_RPS,
    ]
    t0 = time.time()
    run(cmd, timeout=7200)
    elapsed = time.time() - t0
    n_blocks = end_block - start_block + 1
    bps = n_blocks / elapsed if elapsed > 0 else 0
    print(f"  cryo: {n_blocks} blocks in {elapsed:.0f}s ({bps:.0f} blocks/sec)")

    # Count output files
    parquet_files = globmod.glob(f"{WORK_DIR}/**/*.parquet", recursive=True)
    total_bytes = sum(os.path.getsize(f) for f in parquet_files)
    print(f"  Output: {len(parquet_files)} Parquet files, {total_bytes / (1024*1024):.1f} MB total")


# ── Step 2: Upload Parquet to Blob ────────────────────────────────────────────

def upload_to_blob():
    parquet_files = globmod.glob(f"{WORK_DIR}/**/*.parquet", recursive=True)
    if not parquet_files:
        print("  No Parquet files to upload.")
        return
    t0 = time.time()
    run([
        "az", "storage", "blob", "upload-batch",
        "--destination", STORAGE_CONTAINER,
        "--source", WORK_DIR,
        "--account-name", STORAGE_ACCOUNT,
        "--auth-mode", "login",
        "--overwrite",
        "--pattern", "*.parquet",
    ], timeout=1800)
    elapsed = time.time() - t0
    print(f"  Uploaded {len(parquet_files)} files to blob in {elapsed:.0f}s")


# ── Step 3: Ingest Parquet from Blob into ADX ─────────────────────────────────

def ingest_from_blob(ingest_client):
    from azure.kusto.ingest import IngestionProperties
    from azure.kusto.data import DataFormat

    blob_base = f"https://{STORAGE_ACCOUNT}.blob.core.windows.net/{STORAGE_CONTAINER}"
    total_ingested = 0

    for dataset, table in DATASETS.items():
        props = IngestionProperties(
            database=ADX_DATABASE,
            table=table,
            data_format=DataFormat.PARQUET,
        )
        # Find parquet files for this dataset in work dir
        patterns = [
            f"{WORK_DIR}/{dataset}*.parquet",
            f"{WORK_DIR}/**/{dataset}*.parquet",
            f"{WORK_DIR}/*/{dataset}*.parquet",
        ]
        files = []
        for p in patterns:
            files.extend(globmod.glob(p, recursive=True))
        files = list(set(files))  # dedup

        if not files:
            print(f"  {table}: no Parquet files found")
            continue

        for f in files:
            rel_path = os.path.relpath(f, WORK_DIR)
            blob_url = f"{blob_base}/{rel_path}"
            ingest_client.ingest_from_blob(
                blob_url,
                ingestion_properties=props
            )
            total_ingested += 1

        print(f"  {table}: queued {len(files)} Parquet files for ingestion")

    print(f"  Total: {total_ingested} ingestion operations queued")


# ── Cleanup ───────────────────────────────────────────────────────────────────

def cleanup():
    subprocess.run(["rm", "-rf", WORK_DIR], capture_output=True)


# ── Main pipeline ─────────────────────────────────────────────────────────────

def main():
    print("═══ ADX ETL (Optimal) starting ═══")
    print(f"  Erigon:  {ERIGON_RPC}")
    print(f"  ADX:     {ADX_CLUSTER_URI}")
    print(f"  Blob:    {STORAGE_ACCOUNT}/{STORAGE_CONTAINER}")
    print(f"  cryo:    concurrency={CRYO_CONCURRENCY}, chunk={CRYO_CHUNK_SIZE}, rps={CRYO_RPS}")

    # Azure login (workload identity)
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
    print(f"  Target: blocks {start} → {end} ({end - start + 1:,} blocks)")

    t_total = time.time()
    try:
        print("\n── Step 1: Extract (cryo → Parquet) ──")
        run_cryo(start, end)

        print("\n── Step 2: Upload (Parquet → Blob) ──")
        upload_to_blob()

        print("\n── Step 3: Ingest (Blob → ADX) ──")
        ingest_from_blob(ingest)

        update_progress(kusto, end)
    finally:
        cleanup()

    elapsed = time.time() - t_total
    print(f"\n═══ ETL complete in {elapsed:.0f}s ═══")
    print(f"  Blocks {start} → {end} ({end - start + 1:,} blocks)")

    if STOP_ADX_AFTER:
        stop_adx()


if __name__ == "__main__":
    main()
